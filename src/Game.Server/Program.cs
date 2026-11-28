using System.Runtime.InteropServices;
using Game.Content.ClientExport;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Chat;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.Party;
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
    Console.Error.WriteLine(AdminOptions.Usage);
    Console.Error.WriteLine(ChatOptions.Usage);
    Console.Error.WriteLine(PartyOptions.Usage);
    Console.Error.WriteLine(Game.Server.Trade.TradeOptions.Usage);
    Console.Error.WriteLine(Game.Server.Guild.GuildOptions.Usage);
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

var adminOptions = AdminOptions.Parse(options.AdminArgs, Environment.GetEnvironmentVariable, development, out var adminError);
if (adminOptions is null)
{
    Console.Error.WriteLine(adminError);
    Console.Error.WriteLine(AdminOptions.Usage);
    return 2;
}

var chatOptions = ChatOptions.Parse(options.ChatArgs, Environment.GetEnvironmentVariable, out var chatError);   // T-901
if (chatOptions is null)
{
    Console.Error.WriteLine(chatError);
    Console.Error.WriteLine(ChatOptions.Usage);
    return 2;
}

var partyOptions = PartyOptions.Parse(options.PartyArgs, Environment.GetEnvironmentVariable, out var partyError);   // T-902
if (partyOptions is null)
{
    Console.Error.WriteLine(partyError);
    Console.Error.WriteLine(PartyOptions.Usage);
    return 2;
}

var tradeOptions = Game.Server.Trade.TradeOptions.Parse(options.TradeArgs, Environment.GetEnvironmentVariable, out var tradeError);   // T-903
if (tradeOptions is null)
{
    Console.Error.WriteLine(tradeError);
    Console.Error.WriteLine(Game.Server.Trade.TradeOptions.Usage);
    return 2;
}

var guildOptions = Game.Server.Guild.GuildOptions.Parse(options.GuildArgs, Environment.GetEnvironmentVariable, out var guildError);   // T-904
if (guildOptions is null)
{
    Console.Error.WriteLine(guildError);
    Console.Error.WriteLine(Game.Server.Guild.GuildOptions.Usage);
    return 2;
}

var log = new CountingLog(new ConsoleLog()); // the admin API's status reports its counts
var startedAt = DateTime.UtcNow;
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

var netProblems = netOptions.Problems().Concat(chatOptions.Problems(netOptions.MaxMessageBytes).Select(p => "chat: " + p))
    .Concat(partyOptions.Problems().Select(p => "party: " + p)).Concat(tradeOptions.Problems().Select(p => "trade: " + p))
    .Concat(guildOptions.Problems(netOptions.MaxMessageBytes).Select(p => "guild: " + p)).ToList();
if (options.HotReload) netProblems.AddRange(HotReloadGate.Problems(HotReloadGate.DebugBuild, development, netOptions.ListenAddress).Select(p => "--hot-reload: " + p));   // T-801
if (netProblems.Count > 0)
{
    foreach (var problem in netProblems) log.Error("Gateway: " + problem);
    log.Error("Refusing to start.");
    return 1;
}
// T-804: the admin API, when it is configured. Its audit log is in the database, so it needs one.
var adminProblems = adminOptions.Problems(netOptions.Port, Environment.GetEnvironmentVariable).ToList();
if (adminOptions.Enabled && database is null) adminProblems.Add($"the admin API writes its audit log to the database: set {GameDatabase.ConnectionVariable}");
if (adminProblems.Count > 0)
{
    foreach (var problem in adminProblems) log.Error("Admin API: " + problem);
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

var built = Game.Server.World.Reload.ServerContent.Build(content);   // what the tick's systems read; a hot reload (T-801) swaps in a new one

// The seed only feeds the simulation's PCG32 streams; it is logged so a run can be replayed.
var seed = options.Seed ?? (ulong)Random.Shared.NextInt64();
var clock = new SystemClock();
var progressions = new InMemoryProgressionStore(); // combat writes it; the gateway reads it for each player's own exp messages
// Saved characters (T-702): with a database, accounts' characters are loaded before they enter and saved as they play and leave, and item ids are unique for ever.
CharacterSaves? saves = null;
CharacterLoader? loader = null;
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
    saves.Open(loader = new CharacterLoader(content, saves.NextItemId));
    log.Info($"Saving characters every {options.SaveIntervalSeconds} s; server run {saves.Run}.");
}
var inventories = new InMemoryInventoryStore(saves is null ? null : saves.NextItemId); // the inventory writes it; the gateway reads it for each player's own inventory messages
var savePoints = new InMemorySavePointStore();
var keeper = saves is null ? null : new CharacterKeeper(saves, progressions, inventories, savePoints, clock, TimeProvider.System);
var parties = new Game.Server.World.Party.PartyRoster();   // T-902: the party service writes who is in which party; combat and every instance's party system read it
var pipeline = ServerPipeline.Create(built, progressions, inventories, clock, savePoints, keeper, parties, partyOptions.StatusIntervalTicks); // more systems plug in in ServerPipeline: spawns (T-301), combat, ...
var world = new GameWorld(content.Grids.Values, pipeline, seed, clock, log) { Keeper = keeper };
var runners = world.Instances.Select(i => new InstanceRunner(i, clock, log, !options.LegacyTimer)).ToList();
var contentHash = ClientBundleExporter.Hash(content); // the display bundle's hash: a client packed with another one must update (ARCHITECTURE §5.4)
log.Info($"Content hash: {contentHash}");
var tokens = new ResumeTokens();
var devLogin = new DevLogin(development, netOptions.MaxNameLength, tokens);
var accounts = database is null ? null : new AccountService(new PostgresAccountStore(database), new Argon2idHasher(), HashQueue.Default(), new AccountLimits { EraseWait = TimeSpan.FromDays(options.AccountEraseDays) }, content.Balance, content.Appearance, clock,
    TimeProvider.System, log);
var gateway = new Gateway(world, new NewCharacters(content), netOptions, clock, log, contentHash, accounts is null ? devLogin : new AccountLogin(accounts, devLogin), tokens,
    progress: new OwnProgress(content, progressions), inventory: new OwnInventory(content.Balance.Inventory, inventories), keeper: keeper); // subscribes to instance events: before ticking

// T-806: bans and mutes are the database's, so every server with one asks it (sign-in, hello, the ban watch), admin API or not.
var moderation = database is null ? null : new PostgresModerationStore(database);
var checks = moderation is null ? null : new ModerationChecks(moderation, TimeProvider.System);
if (accounts is not null && checks is not null) accounts.BanOf = checks.BanOfAsync;
// T-901: chat. Its block lists and reports are in the database; without one nobody has an account, so nobody can be muted, block or report.
var chatStore = database is null ? null : new PostgresChatStore(database);
await using var chat = new ChatService(chatOptions, gateway, chatStore, checks is null ? null : checks.SpeakAsync, TimeProvider.System, log, chatStore);
gateway.Chat = chat;
// T-902: parties, in this process's memory only; the party channel goes through chat's seam.
await using var party = new PartyService(content.Balance.Party, partyOptions, parties, new GatewayPartyDirectory(gateway, chat), TimeProvider.System, log);
chat.Register(Game.Shared.Vocabulary.ChatChannels.Party, party);
gateway.Party = party;
party.Start();
// T-903: trade asks are rate-limited per account and blocked like chat before the tick decides the rest; the trades themselves live on the tick.
// T-904: guilds are the database's; without one (development's dev login) there are none, and guild messages are ignored.
await using var guilds = database is null ? null : new Game.Server.Guild.GuildService(content.Balance.Guild, content.Balance.Characters, guildOptions,
    new PostgresGuildStore(database), new Game.Server.Guild.GatewayGuildDirectory(gateway, chat), i => gateway.Enqueue(i) == EnqueueResult.Accepted, chatOptions.MarksPerLetter,
    TimeProvider.System, log);
if (guilds is not null)
{
    // T-905: the guild storage's moves are held in the database, moved on the tick and landed with the character's save.
    guilds.Storage = new Game.Server.Guild.GuildStorage(content.Balance.Guild.Storage, new PostgresGuildStorageStore(database!), saves!.NextItemId,
        i => gateway.Enqueue(i) == EnqueueResult.Accepted, guildOptions.PaymentWaitMs, TimeProvider.System, log);
    chat.Register(Game.Shared.Vocabulary.ChatChannels.Guild, guilds);
    gateway.Guild = guilds;
    guilds.Start();
}
gateway.Trade = new Game.Server.Trade.TradeGate(tradeOptions, new GatewayPartyDirectory(gateway, chat), i => gateway.Enqueue(i), TimeProvider.System);
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
AdminHost? adminHost = null;
if (adminOptions.Enabled)
{
    try
    {
        var adminStore = new PostgresAdminStore(database!);
        var adminEvents = new AdminEvents(log);
        var road = new AdminRoad(world, gateway, adminStore, adminOptions, TimeProvider.System)
        {
            Moderation = moderation, Checks = checks, ChatReports = chatStore, NextItemId = saves is null ? null : saves.NextItemId, Content = content, EraseWait = TimeSpan.FromDays(options.AccountEraseDays),
        };
        var commands = new AdminCommands(AdminCommandTable.AllCommands(), road, adminEvents, log);
        var api = new AdminApi(adminOptions, new ServiceCredential(Environment.GetEnvironmentVariable(adminOptions.CredentialEnv)!), new AdminRateLimit(adminOptions, TimeProvider.System),
            commands, adminStore, new AdminStatusSource(world, gateway, saves, log, contentHash, startedAt), gateway, log, moderation, road.Maintenance, chatStore);
        adminHost = await AdminHost.StartAsync(adminOptions, host.Endpoint.Port, api);
    }
    catch (Exception e) when (e is InvalidOperationException or IOException)
    {
        log.Error("Admin API could not start: " + e.Message);
        await host.StopAsync();
        await host.DisposeAsync();
        if (saves is not null) await saves.DrainAsync(TimeSpan.Zero);
        return 1;
    }
}

// T-801: hot reload (development only; refused above otherwise). A valid change swaps in between ticks; clients are told when the display bundle changed.
using var reloader = options.HotReload ? new ContentReloader(options.ContentDir, options.Theme, world, built, contentHash, next =>
{
    var start = new NewCharacters(next);
    return player => progressions.TryGet(player.Id, out var state) ? start.CreateStats(state) : null;
}, log) : null;
if (reloader is not null)
{
    reloader.Reloaded += (next, hash) =>
    {
        loader?.Swap(next.Database);
        var told = gateway.ContentChanged(hash);
        if (told > 0) log.Info($"[reload] told {told} session(s) that the content changed (hash {hash}).");
    };
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
reloader?.Start();
gateway.Start();
chat.Start();
await using var eraser = database is null ? null : AccountEraser.Start(new PostgresAccountStore(database), TimeProvider.System, log); // T-703
await using var banWatch = moderation is null ? null : BanWatch.Start(moderation, gateway, TimeProvider.System, log); // T-806: bans made through another process end games here too
log.Info($"Gateway listening on {host.Endpoint}{(accounts is null ? "" : " (account endpoints on the same port)")}");
if (adminHost is not null) log.Info($"Admin API listening on {adminHost.Endpoint} (T-804; docs/ADMIN_API.md)");
log.Info(options.DurationSeconds is { } d ? $"Running for {d} s." : "Running. Press Ctrl+C to stop.");

if (options.DurationSeconds is { } seconds) stop.Wait(TimeSpan.FromSeconds(seconds));
else stop.Wait();

var unsaved = 0;
if (eraser is not null) await eraser.DisposeAsync(); // an erasure in progress is one transaction: it is finished or undone
if (banWatch is not null) await banWatch.DisposeAsync();
log.Info($"Stopping: saying bye to {gateway.SessionCount} session(s), releasing {gateway.CharactersInGrace} character(s) in their grace period, " +
         "then finishing the current tick of each instance.");
if (adminHost is not null)
{
    await adminHost.StopAsync(); // no admin command starts while the sessions are said bye to
    await adminHost.DisposeAsync();
}
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
