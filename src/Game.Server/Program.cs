using System.Runtime.InteropServices;
using Game.Content.ClientExport;
using Game.Content.Validation;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;

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
var pipeline = ServerPipeline.Create(content); // more systems plug in in ServerPipeline: spawns (T-301), combat, ...
var world = new GameWorld(content.Grids.Values, pipeline, seed, clock, log);
var runners = world.Instances.Select(i => new InstanceRunner(i, clock, log)).ToList();
var contentHash = ClientBundleExporter.Hash(content); // the display bundle's hash: a client packed with another one must update (ARCHITECTURE §5.4)
log.Info($"Content hash: {contentHash}");
var gateway = new Gateway(world, new NewCharacters(content), netOptions, clock, log, contentHash); // subscribes to instance events: before ticking

GatewayHost host;
try
{
    host = await GatewayHost.StartAsync(gateway);
}
catch (Exception e) when (e is InvalidOperationException or IOException)
{
    log.Error("Gateway could not start: " + e.Message);
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
log.Info($"Gateway listening on {host.Endpoint}");
log.Info(options.DurationSeconds is { } d ? $"Running for {d} s." : "Running. Press Ctrl+C to stop.");

if (options.DurationSeconds is { } seconds) stop.Wait(TimeSpan.FromSeconds(seconds));
else stop.Wait();

log.Info($"Stopping: saying bye to {gateway.SessionCount} session(s), releasing {gateway.CharactersInGrace} character(s) in their grace period, " +
         "then finishing the current tick of each instance.");
await gateway.ShutdownAsync();
await host.StopAsync();
await host.DisposeAsync();
foreach (var runner in runners) runner.Stop();
foreach (var runner in runners)
    log.Info($"[{runner.Instance.MapId}] stopped after tick {runner.Instance.TickNumber}; lifetime: {runner.Lifetime}");
log.Info($"Gateway: {gateway.Stats.Accepted} connection(s), {gateway.Stats.MessagesIn} message(s) in, {gateway.Stats.MessagesOut} out " +
         $"({gateway.Stats.BytesOut} bytes), sinks {gateway.Stats.SinkCalls} call(s) in {gateway.Stats.SinkTime.TotalMilliseconds:0.0} ms");
log.Info("Server stopped.");
return 0;
