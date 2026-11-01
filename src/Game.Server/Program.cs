using System.Runtime.InteropServices;
using Game.Content.Validation;
using Game.Server.Hosting;
using Game.Server.World;

var options = ServerOptions.Parse(args, out var argError);
if (options is null)
{
    Console.Error.WriteLine(argError);
    Console.Error.WriteLine(ServerOptions.Usage);
    return 2;
}

var log = new ConsoleLog();
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
var pipeline = ServerPipeline.Create(content); // more systems plug in in ServerPipeline: AOI (T-204), spawns (T-301)
var world = new GameWorld(content.Grids.Values, pipeline, seed, clock, log);
var runners = world.Instances.Select(i => new InstanceRunner(i, clock, log)).ToList();

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
log.Info(options.DurationSeconds is { } d ? $"Running for {d} s." : "Running. Press Ctrl+C to stop.");

if (options.DurationSeconds is { } seconds) stop.Wait(TimeSpan.FromSeconds(seconds));
else stop.Wait();

log.Info("Stopping: finishing the current tick of each instance.");
foreach (var runner in runners) runner.Stop();
foreach (var runner in runners)
    log.Info($"[{runner.Instance.MapId}] stopped after tick {runner.Instance.TickNumber}; lifetime: {runner.Lifetime}");
log.Info("Server stopped.");
return 0;
