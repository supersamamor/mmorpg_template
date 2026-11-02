using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Game.LoadTest;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.World;

// T-214 load test: starts a real Game.Server (Release build, development environment) as a child process, logs in N bots that walk and
// fight over the T-307 messages, optionally parks G more characters in their grace period first, and reports per-client KB/s, the server's
// tick p50/p99 after warm-up, its CPU per tick and its memory. Output stays a short report (the server's log is parsed, not echoed).

var options = LoadTestOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(LoadTestOptions.Usage);
    return 2;
}

var root = RepoRoot();
var serverDll = options.Server ?? Path.Combine(root, "src", "Game.Server", "bin", "Release", "net10.0", "Game.Server.dll");
if (!File.Exists(serverDll))
{
    Console.Error.WriteLine($"No server build at {serverDll}. Run: dotnet build -c Release src/Game.Server");
    return 2;
}

var port = FreePort();
var runSeconds = options.WarmupSeconds + options.MeasureSeconds + 60;
var graceMs = (runSeconds + 60) * 1000;   // characters parked in grace must outlast the run
var maxSessions = options.Bots + options.Grace;
var serverArgs = new List<string>
{
    serverDll, "--duration", runSeconds.ToString(CultureInfo.InvariantCulture), "--seed", "214",
    "--net-port", port.ToString(CultureInfo.InvariantCulture), "--net-max-sessions", maxSessions.ToString(CultureInfo.InvariantCulture),
    "--net-grace-period-ms", graceMs.ToString(CultureInfo.InvariantCulture), "--net-compression", options.Compression ? "true" : "false",
};
var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
foreach (var arg in serverArgs) start.ArgumentList.Add(arg);
start.Environment["DOTNET_ENVIRONMENT"] = "Development";

using var server = Process.Start(start) ?? throw new InvalidOperationException("Could not start the server.");
var log = new ServerLog(server);
try
{
    var hash = await log.WaitForAsync(new Regex("Content hash: (\\S+)"), 60_000);
    await log.WaitForAsync(new Regex("Gateway listening on"), 60_000);
    var endpoint = new Uri($"ws://127.0.0.1:{port}/ws");
    Console.WriteLine($"server pid {server.Id}, {options.Bots} bots ({options.FighterShare:P0} fighting), {options.Grace} in grace, " +
                      $"permessage-deflate {(options.Compression ? "on" : "off")}, warm-up {options.WarmupSeconds} s, measure {options.MeasureSeconds} s");

    // 1. Characters in their grace period: log in, then vanish without a close frame (a phone losing its signal).
    var parked = new List<Bot>();
    for (var i = 0; i < options.Grace; i += 20)
        parked.AddRange(await Task.WhenAll(Enumerable.Range(i, Math.Min(20, options.Grace - i)).Select(n => Bot.LoginAsync(endpoint, hash, "parked" + n, false, options.Compression))));
    var parkedTokens = parked.Select(b => b.Token).ToList();
    foreach (var bot in parked) bot.Vanish();
    if (options.Grace > 0) await log.WaitForAsync(new Regex($" {options.Grace} in their grace period"), 30_000);

    // 2. The active bots, in waves so the gateway's per-tick join cost stays realistic.
    var random = new Random(214);
    var bots = new List<Bot>();
    for (var i = 0; i < options.Bots; i += 20)
        bots.AddRange(await Task.WhenAll(Enumerable.Range(i, Math.Min(20, options.Bots - i))
            .Select(n => Bot.LoginAsync(endpoint, hash, "bot" + n, n < options.Bots * options.FighterShare, options.Compression))));
    Console.WriteLine($"{bots.Count} bots in (memory {server.WorkingSetMb():0} MB)");

    using var stop = new CancellationTokenSource();
    var acting = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            foreach (var bot in bots) await bot.ActAsync(random);
            await Task.Delay(250);
        }
    });

    // 3. Warm-up, then the measured window.
    await Task.Delay(options.WarmupSeconds * 1000);
    var bytes0 = bots.Select(b => b.BytesReceived).ToArray();
    server.Refresh();
    var cpu0 = server.TotalProcessorTime;
    var wall = Stopwatch.StartNew();
    var measureStart = log.Elapsed;
    await Task.Delay(options.MeasureSeconds * 1000);
    var seconds = wall.Elapsed.TotalSeconds;
    server.Refresh();
    var cpu = server.TotalProcessorTime - cpu0;
    var perClient = bots.Select((b, i) => (b.BytesReceived - bytes0[i]) / seconds / 1024).OrderBy(v => v).ToList();
    var workingSet = server.WorkingSet64 / 1048576.0;
    var peak = server.PeakWorkingSet64 / 1048576.0;
    var measureEnd = log.Elapsed;

    // The tick line after the window covers the last minute, all of it after warm-up while the bots still play.
    var ticks = await log.WaitForAsync(new Regex(@"\[net\] ticks .*"), 15_000, after: measureEnd);
    var reports = log.Lines(new Regex(@"\[net\] \d+ player\(s\).*"), measureStart + TimeSpan.FromSeconds(10), measureEnd + TimeSpan.FromSeconds(10));
    double Average(string pattern) => reports.Select(r => Regex.Match(r, pattern)).Where(m => m.Success)
        .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(double.NaN).Average();

    Console.WriteLine($"per-client payload KB/s: mean {perClient.Average():0.00}, min {perClient[0]:0.00}, p50 {perClient[perClient.Count / 2]:0.00}, max {perClient[^1]:0.00}");
    Console.WriteLine($"server per-player KB/s: payload {Average(@"([\d.]+) KB/s per player"):0.00}, wire {Average(@"wire ([\d.]+) KB/s"):0.00}");
    Console.WriteLine($"server CPU: {cpu.TotalMilliseconds / (seconds * 10):0.00} ms per 100 ms tick ({cpu.TotalMilliseconds / seconds / 10:0.0}% of one core)");
    Console.WriteLine($"view sinks: {Average(@"view sinks ([\d.]+) us"):0} us per tick (all instances)");
    Console.WriteLine($"server memory: working set {workingSet:0} MB (peak {peak:0} MB)");
    Console.WriteLine("server " + ticks[(ticks.IndexOf("ticks", StringComparison.Ordinal))..]);
    Console.WriteLine($"server fell behind {log.Count("fell behind")} time(s); warnings {log.Count(" WARN ")}, errors {log.Count(" ERROR")}");
    Console.WriteLine($"bots: {bots.Sum(b => b.Attacks)} attacks, {bots.Sum(b => b.Kills)} hits seen as kills, {bots.Sum(b => b.Deaths)} deaths, " +
                      $"{bots.Count(b => b.Bye is not null)} got bye ({string.Join(",", bots.Select(b => b.Bye).OfType<string>().Distinct())})");

    // 4. With characters in grace the server is full: a new login is refused, a resume still gets in.
    if (options.Grace > 0)
    {
        var late = await Bot.LoginAsync(endpoint, hash, "late", false, options.Compression, expectWelcome: false);
        var back = await Bot.ResumeAsync(endpoint, hash, parkedTokens[0], options.Compression);
        Console.WriteLine($"full server: new login -> {late.Bye ?? "welcome"} (expected server_full); resume of a parked character -> " +
                          $"{(back.Welcomed ? "welcome" : back.Bye)} (expected welcome)");
    }

    await stop.CancelAsync();
    await acting;
    foreach (var bot in bots) bot.Vanish();
    return 0;
}
finally
{
    if (!server.HasExited) server.Kill(entireProcessTree: true);
}

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the repository (no Game.sln found above the current directory).");
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

namespace Game.LoadTest
{
    /// <summary>Command line: <c>--bots N --grace G --compression --warmup S --measure S --fighters 0.5 --server path</c>.</summary>
    internal sealed record LoadTestOptions(int Bots, int Grace, bool Compression, int WarmupSeconds, int MeasureSeconds, double FighterShare, string? Server)
    {
        public const string Usage =
            "usage: loadtest [--bots 50] [--grace 0] [--compression] [--warmup 30] [--measure 60] [--fighters 0.5] [--server path/Game.Server.dll]";

        public static LoadTestOptions? Parse(string[] args)
        {
            var o = new LoadTestOptions(50, 0, false, 30, 60, 0.5, null);
            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException(args[i] + " needs a value");
                    o = args[i] switch
                    {
                        "--bots" => o with { Bots = int.Parse(Next(), CultureInfo.InvariantCulture) },
                        "--grace" => o with { Grace = int.Parse(Next(), CultureInfo.InvariantCulture) },
                        "--compression" => o with { Compression = true },
                        "--warmup" => o with { WarmupSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
                        "--measure" => o with { MeasureSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
                        "--fighters" => o with { FighterShare = double.Parse(Next(), CultureInfo.InvariantCulture) },
                        "--server" => o with { Server = Next() },
                        _ => throw new FormatException("unknown option " + args[i]),
                    };
                }
            }
            catch (FormatException e)
            {
                Console.Error.WriteLine(e.Message);
                return null;
            }
            return o.Bots > 0 && o.Grace >= 0 && o.MeasureSeconds >= 60 && o.WarmupSeconds >= 0 ? o : null;
        }
    }

    /// <summary>The server's stdout, kept in memory with arrival times and searched by the report.</summary>
    internal sealed class ServerLog
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<(TimeSpan At, string Line)> _lines = new();

        public ServerLog(Process process)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue((_clock.Elapsed, e.Data)); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue((_clock.Elapsed, "stderr: " + e.Data)); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public TimeSpan Elapsed => _clock.Elapsed;

        public int Count(string text) => _lines.Count(l => l.Line.Contains(text, StringComparison.Ordinal));

        public List<string> Lines(Regex pattern, TimeSpan from, TimeSpan to) =>
            _lines.Where(l => l.At > from && l.At <= to && pattern.IsMatch(l.Line)).Select(l => l.Line).ToList();

        /// <summary>The first match (group 1 if the pattern has one) of a line that arrived after <paramref name="after"/>.</summary>
        public async Task<string> WaitForAsync(Regex pattern, int timeoutMs, TimeSpan after = default)
        {
            var deadline = _clock.Elapsed + TimeSpan.FromMilliseconds(timeoutMs);
            while (_clock.Elapsed < deadline)
            {
                foreach (var (at, line) in _lines)
                {
                    if (at <= after) continue;
                    var match = pattern.Match(line);
                    if (match.Success) return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException($"The server never logged /{pattern}/. Last lines:{Environment.NewLine}" +
                                       string.Join(Environment.NewLine, _lines.TakeLast(10).Select(l => l.Line)));
        }
    }

    /// <summary>A visible entity, as much as a bot needs of it.</summary>
    internal sealed record Seen(string Kind, GridCell Cell, bool Dead);

    /// <summary>
    /// One scripted client: a single receive loop (one pending receive per socket), a view model of kinds, cells and deaths, and a behaviour.
    /// Fighters attack the nearest living monster and walk towards it when refused; walkers wander near where they joined.
    /// </summary>
    internal sealed class Bot
    {
        private readonly ClientWebSocket _socket;
        private readonly bool _fighter;
        private readonly Dictionary<long, Seen> _view = new();
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _bytes;
        private long _id;
        private long? _target;
        private bool _attackEnded = true;
        private bool _refusedHere;
        private bool _dead;
        private GridCell _anchor;
        private bool _anchored;
        private long _nextActMs;
        private long _ping;

        private Bot(ClientWebSocket socket, bool fighter)
        {
            _socket = socket;
            _fighter = fighter;
            _ = Task.Run(ReceiveLoopAsync);
        }

        public long BytesReceived => Interlocked.Read(ref _bytes);

        public string Token { get; private set; } = string.Empty;

        public bool Welcomed { get; private set; }

        public string? Bye { get; private set; }

        public int Attacks { get; private set; }

        public int Kills { get; private set; }

        public int Deaths { get; private set; }

        public static async Task<Bot> LoginAsync(Uri endpoint, string hash, string name, bool fighter, bool compression, bool expectWelcome = true)
        {
            var bot = await ConnectAsync(endpoint, fighter, compression);
            await bot.SendAsync(new HelloMessage(ProtocolVersion.Current, name, hash));
            await bot.WaitFirstAsync();
            if (expectWelcome && !bot.Welcomed) throw new InvalidOperationException($"{name}: no welcome (bye {bot.Bye}).");
            return bot;
        }

        public static async Task<Bot> ResumeAsync(Uri endpoint, string hash, string token, bool compression)
        {
            var bot = await ConnectAsync(endpoint, false, compression);
            await bot.SendAsync(new HelloMessage(ProtocolVersion.Current, null, hash, token));
            await bot.WaitFirstAsync();
            return bot;
        }

        private static async Task<Bot> ConnectAsync(Uri endpoint, bool fighter, bool compression)
        {
            var socket = new ClientWebSocket();
            if (compression) socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
            await socket.ConnectAsync(endpoint, CancellationToken.None);
            return new Bot(socket, fighter);
        }

        /// <summary>Drops the connection without a close frame.</summary>
        public void Vanish() => _socket.Abort();

        public async Task ActAsync(Random random)
        {
            var now = Environment.TickCount64;
            if (Bye is not null || !Welcomed || now < _nextActMs) return;
            _nextActMs = now + 800 + random.Next(400);
            ClientMessage? message;
            lock (_view) message = Decide(random);
            if (message is null && ++_ping % 10 == 0) message = new PingMessage(_ping);
            if (message is not null) await SendAsync(message);
        }

        private ClientMessage? Decide(Random random)
        {
            if (_dead) return new RespawnMessage();
            if (!_view.TryGetValue(_id, out var me)) return null;
            if (!_fighter)
                return new MoveMessage(new GridCell(_anchor.X + random.Next(-6, 7), _anchor.Y + random.Next(-6, 7)));
            if (_target is { } t && !_attackEnded && _view.TryGetValue(t, out var current) && !current.Dead) return null; // still swinging
            var monster = _view.Where(e => e.Value is { Kind: EntityKinds.Monster, Dead: false })
                .OrderBy(e => Math.Max(Math.Abs(e.Value.Cell.X - me.Cell.X), Math.Abs(e.Value.Cell.Y - me.Cell.Y))).Select(e => (long?)e.Key).FirstOrDefault();
            if (monster is not { } target) return new MoveMessage(new GridCell(me.Cell.X + random.Next(-8, 9), me.Cell.Y + random.Next(-8, 9)));
            if (_refusedHere)
            {
                // Refused where we stand (safe zone, no path...): walk toward it and try again from there.
                _refusedHere = false;
                var cell = _view[target].Cell;
                return new MoveMessage(new GridCell(cell.X + random.Next(-1, 2), cell.Y + random.Next(-1, 2)));
            }
            _target = target;
            _attackEnded = false;
            Attacks++;
            return new AttackMessage(target);
        }

        private async Task SendAsync(ClientMessage message)
        {
            try
            {
                await _socket.SendAsync(ProtocolJson.ToBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                Bye ??= "send failed";
            }
        }

        private async Task WaitFirstAsync()
        {
            if (await Task.WhenAny(_first.Task, Task.Delay(30_000)) != _first.Task) throw new TimeoutException("No welcome or bye in 30 s.");
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var count = 0;
            try
            {
                while (true)
                {
                    if (count == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                    var result = await _socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    count += result.Count;
                    if (!result.EndOfMessage) continue;
                    Interlocked.Add(ref _bytes, count);
                    var read = ProtocolJson.ReadServer(buffer.AsSpan(0, count));
                    count = 0;
                    if (read.Message is { } message) Apply(message);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // vanished or dropped
            }
            _first.TrySetResult();
        }

        private void Apply(ServerMessage message)
        {
            lock (_view)
            {
                switch (message)
                {
                    case WelcomeMessage welcome:
                        _id = welcome.Id;
                        Token = welcome.Token ?? string.Empty;
                        Welcomed = true;
                        break;
                    case SnapshotMessage snapshot:
                        _view.Clear();
                        foreach (var e in snapshot.Entities) _view[e.Id] = new Seen(e.Kind, e.Cell, e.Dead);
                        if (_view.TryGetValue(_id, out var me))
                        {
                            if (!_anchored) { _anchor = me.Cell; _anchored = true; }
                            _dead = me.Dead;
                        }
                        _first.TrySetResult();
                        break;
                    case DeltaMessage delta:
                        foreach (var e in delta.Appeared) _view[e.Id] = new Seen(e.Kind, e.Cell, e.Dead);
                        foreach (var c in delta.Changed)
                        {
                            if (!_view.TryGetValue(c.Id, out var seen)) continue;
                            if ((c.Fields & ChangedFields.Cell) != 0) seen = seen with { Cell = c.Cell };
                            if ((c.Fields & ChangedFields.Dead) != 0)
                            {
                                if (c.Dead && !seen.Dead && c.Id == _target) Kills++;
                                seen = seen with { Dead = c.Dead };
                            }
                            _view[c.Id] = seen;
                        }
                        foreach (var id in delta.Disappeared) _view.Remove(id);
                        break;
                    case AttackStoppedMessage:
                        _attackEnded = true;
                        break;
                    case RejectedMessage { Intent: MessageTypes.Attack }:
                        _attackEnded = true;
                        _refusedHere = true;
                        break;
                    case YouDiedMessage:
                        _dead = true;
                        _attackEnded = true;
                        Deaths++;
                        break;
                    case RespawnedMessage:
                        _dead = false;
                        break;
                    case ByeMessage bye:
                        Bye = bye.Reason;
                        _first.TrySetResult();
                        break;
                }
            }
        }
    }

    internal static class ProcessExtensions
    {
        public static double WorkingSetMb(this Process process)
        {
            process.Refresh();
            return process.WorkingSet64 / 1048576.0;
        }
    }
}
