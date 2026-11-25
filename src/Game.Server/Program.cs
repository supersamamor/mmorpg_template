using System.Runtime.InteropServices;
using Game.Content.ClientExport;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Server.Accounts;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.Persistence;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Saving;

var options = ServerOptions.Parse(args, out var argError);
if (options is null)
{
    Console.Error.WriteLine(argError);
    Console.Error.WriteLine(ServerOptions.Usage);
    Console.Error.WriteLine(GatewayOptions.Usage);
    return 2;
}

var environmentName = options.EnvironmentName(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
var development = string.Equals(environmentName, ServerOptions.DevelopmentName, StringComparison.OrdinalIgnoreCase);
var netOptions = GatewayOptions.Parse(options.NetArgs, Environment.GetEnvironmentVariable, development, out var netError);
if (netOptions is null)
{
    Console.Error.WriteLine(netError);
    Console.Error.WriteLine(GatewayOptions.Usage);
    return 2;
}

var log = new ConsoleLog();
log.Info($"Environment: {environmentName}{(development ? " (development: ws:// on loopback, dev login enabled)" : "")}");

// The database (T-701): accounts and characters. Optional in development (then only the dev login works), required everywhere else.
GameDatabase? database = null;
try
{
    var connection = Environment.GetEnvironmentVariable(GameDatabase.ConnectionVariable);
    if (!string.IsNullOrWhiteSpace(connection)) database = new GameDatabase(connection);
    else if (options.Migrate || !development)
        throw new DatabaseException($"{(options.Migrate ? "--migrate needs" : "outside development the server needs")} a database: set {GameDatabase.ConnectionVariable} to its connection string.");
    else log.Info($"No database ({GameDatabase.ConnectionVariable} is not set): accounts are off, only the dev login works.");
    if (database is not null) await database.PrepareAsync(development, options.Migrate, log);
}
catch (DatabaseException e)
{
    log.Error(e.Message);
    log.Error(options.Migrate ? "Not migrated." : "Refusing to start.");
    return 1;
}
if (options.Migrate) return 0;

var netProblems = netOptions.Problems();
if (netProblems.Count > 0)
{
    foreach (var problem in netProblems) log.Error("Gateway: " + problem);
    log.Error("Refusing to start.");
    return 1;
}

log.Info($"Loading content from {options.ContentDir} (theme '{options.Theme}')");
var content = ContentBoot.Load(options.ContentDir, options.Theme, out var issues);
if (issues.Count > 0) Console.WriteLine(ValidationReportFormatter.ToText(issues));
if (content is null)
{
    log.Error("Content has errors; refusing to start.");
    return 1;
}

// The seed only feeds the simulation's PCG32 streams; it is logged so a run can be replayed.
var seed = options.Seed ?? (ulong)Random.Shared.NextInt64();
var clock = new SystemClock();
var progressions = new InMemoryProgressionStore(); // combat writes it; the gateway reads it for each player's own exp messages
// Saved characters (T-702): with a database, accounts' characters are loaded before they enter and saved as they play and leave, and item ids are unique for ever.
CharacterSaves? saves = null;
if (database is not null)
{
    saves = new CharacterSaves(new PostgresCharacterStore(database),
        new CharacterSaveOptions { IntervalMs = options.SaveIntervalSeconds * 1_000, ShutdownWaitMs = options.SaveShutdownWaitSeconds * 1_000 }, clock, TimeProvider.System, log);
    try
    {
        await saves.StartAsync();
    }
    catch (StoreUnavailableException e)
    {
        log.Error($"The server run could not be registered: {e.Message}. Refusing to start.");
        return 1;
    }
    saves.Open(new CharacterLoader(content, saves.NextItemId));
    log.Info($"Saving characters every {options.SaveIntervalSeconds} s; server run {saves.Run}.");
}
var inventories = new InMemoryInventoryStore(saves is null ? null : saves.NextItemId); // the inventory writes it; the gateway reads it for each player's own inventory messages
var savePoints = new InMemorySavePointStore();
var keeper = saves is null ? null : new CharacterKeeper(saves, progressions, inventories, savePoints, clock, TimeProvider.System);
var pipeline = ServerPipeline.Create(content, progressions, inventories, clock, savePoints, keeper); // more systems plug in in ServerPipeline: spawns (T-301), combat, ...
var world = new GameWorld(content.Grids.Values, pipeline, seed, clock, log) { Keeper = keeper };
var runners = world.Instances.Select(i => new InstanceRunner(i, clock, log, !options.LegacyTimer)).ToList();
var contentHash = ClientBundleExporter.Hash(content); // the display bundle's hash: a client packed with another one must update (ARCHITECTURE §5.4)
log.Info($"Content hash: {contentHash}");
var tokens = new ResumeTokens();
var devLogin = new DevLogin(development, netOptions.MaxNameLength, tokens);
var accounts = database is null ? null : new AccountService(new PostgresAccountStore(database), new Argon2idHasher(), HashQueue.Default(), new AccountLimits(), content.Balance, content.Appearance, clock,
    TimeProvider.System, log);
var gateway = new Gateway(world, new NewCharacters(content), netOptions, clock, log, contentHash, accounts is null ? devLogin : new AccountLogin(accounts, devLogin), tokens,
    progress: new OwnProgress(content, progressions), inventory: new OwnInventory(content.Balance.Inventory, inventories), keeper: keeper); // subscribes to instance events: before ticking

GatewayHost host;
try
{
    if (accounts is not null) accounts.InWorld = id => gateway.HoldsCharacter(id) || saves!.Holds(id); // a character in the world, on its way in, or not saved yet is not deleted
    if (accounts is not null) accounts.SessionsEnded = gateway.EndSessions; // T-710: what played with them ends
    host = await GatewayHost.StartAsync(gateway, accounts: accounts is null ? null : new AccountEndpoints(accounts, log));
}
catch (Exception e) when (e is InvalidOperationException or IOException)
{
    log.Error("Gateway could not start: " + e.Message);
    if (saves is not null) await saves.DrainAsync(TimeSpan.Zero);
    return 1;
}

using var stop = new ManualResetEventSlim();
void RequestStop(PosixSignalContext context)
{
    context.Cancel = true; // keep the process alive; we shut down ourselves
    stop.Set();
}
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);

log.Info($"Starting {runners.Count} map instance(s) at {TickRate.Hz} Hz, seed {seed}, {pipeline.Count} system(s): " +
         string.Join(", ", world.Instances.Select(i => $"{i.MapId} {i.Grid.Width}x{i.Grid.Height}")));
foreach (var runner in runners) runner.Start();
gateway.Start();
log.Info($"Gateway listening on {host.Endpoint}{(accounts is null ? "" : " (account endpoints on the same port)")}");
log.Info(options.DurationSeconds is { } d ? $"Running for {d} s." : "Running. Press Ctrl+C to stop.");

if (options.DurationSeconds is { } seconds) stop.Wait(TimeSpan.FromSeconds(seconds));
else stop.Wait();

var unsaved = 0;
log.Info($"Stopping: saying bye to {gateway.SessionCount} session(s), releasing {gateway.CharactersInGrace} character(s) in their grace period, " +
         "then finishing the current tick of each instance.");
await gateway.ShutdownAsync();
await host.StopAsync();
await host.DisposeAsync();
if (saves is not null)
{
    // The instances still tick: every character leaves on its own, is copied there, and the copies land here.
    var report = await saves.DrainAsync(TimeSpan.FromMilliseconds(saves.Options.ShutdownWaitMs));
    var stats = saves.Stats;
    if (report.Failed.Count == 0) log.Info($"Every character is saved ({report.Took.TotalMilliseconds:0} ms; {stats.Saves} save(s) in {stats.Groups} transaction(s) since the start, {stats.Failures} failed).");
    else log.Error($"{report.Failed.Count} character(s) NOT SAVED after {report.Took.TotalMilliseconds:0} ms: {string.Join(", ", report.Failed)}.");
    unsaved = report.Failed.Count;
}
if (database is not null) await database.DisposeAsync();
foreach (var runner in runners) runner.Stop();
foreach (var runner in runners)
    log.Info($"[{runner.Instance.MapId}] stopped after tick {runner.Instance.TickNumber}; lifetime: {runner.Lifetime}");
log.Info($"Gateway: {gateway.Stats.Accepted} connection(s), {gateway.Stats.MessagesIn} message(s) in, {gateway.Stats.MessagesOut} out " +
         $"({gateway.Stats.BytesOut} bytes), sinks {gateway.Stats.SinkCalls} call(s) in {gateway.Stats.SinkTime.TotalMilliseconds:0.0} ms");
log.Info("Server stopped.");
return unsaved == 0 ? 0 : 3;
