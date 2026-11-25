using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Tools.Commands;

namespace Game.Tests.Server;

/// <summary>
/// T-213: <c>Game.Server</c> as a real process (the copy next to the test assembly, run with the dotnet host): <c>--duration</c> and
/// Ctrl+C both stop it cleanly with exit code 0, and content with an error makes it refuse to start with the text <c>validate</c> prints.
/// Real time passes and the Ctrl+C test borrows the console, so these run in the non-parallel collection.
/// </summary>
[Collection(TimingCollection.Name)]
public class ServerProcessTests(Accounts.DatabaseFixture databases) : IDisposable, IClassFixture<Accounts.DatabaseFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly string[] Quiet = ["--environment", "Development", "--net-port", "0", "--seed", "1"];

    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var dir in _temp)
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private sealed class Running : IDisposable
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public Running(Process process)
        {
            Process = process;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public Process Process { get; }

        public string Output => string.Join("\n", _lines);

        public bool WaitForLine(string text)
        {
            var until = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < until && !Process.HasExited)
            {
                if (_lines.Any(l => l.Contains(text, StringComparison.Ordinal))) return true;
                Thread.Sleep(20);
            }
            return _lines.Any(l => l.Contains(text, StringComparison.Ordinal));
        }

        /// <summary>Waits for the exit and for the redirected output to be read to its end.</summary>
        public bool WaitForExit()
        {
            if (!Process.WaitForExit(Patience)) return false;
            Process.WaitForExit();
            return true;
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.Dispose();
        }
    }

    private static Running Start(params string[] args) => StartWith(null, args);

    private static Running StartWith(string? connection, params string[] args)
    {
        // A child inherits "Ctrl+C is ignored" from a parent that has it switched off (some test runners and shells do); undo that first.
        if (OperatingSystem.IsWindows()) SetConsoleCtrlHandler(IntPtr.Zero, false);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,   // on Windows the child gets its own hidden console, which the Ctrl+C test attaches to
            WorkingDirectory = RepoRoot(),
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Game.Server.dll"));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (connection is not null) info.Environment[Game.Server.Persistence.GameDatabase.ConnectionVariable] = connection;
        else info.Environment.Remove(Game.Server.Persistence.GameDatabase.ConnectionVariable);
        return new Running(Process.Start(info)!);
    }

    [Fact]
    public void The_server_runs_for_the_given_duration_and_exits_cleanly()
    {
        using var server = Start([.. Quiet, "--duration", "1.5"]);

        server.WaitForExit().ShouldBeTrue(server.Output);

        server.Process.ExitCode.ShouldBe(0, server.Output);
        server.Output.ShouldContain("Running for 1.5 s.");
        server.Output.ShouldContain("Stopping:");
        server.Output.ShouldContain("stopped after tick");
        server.Output.ShouldContain("Server stopped.");
    }

    [Fact]
    public void Ctrl_C_stops_the_server_cleanly()
    {
        using var server = Start(Quiet);
        server.WaitForLine("Running. Press Ctrl+C to stop.").ShouldBeTrue(server.Output);

        SendCtrlC(server.Process);

        server.WaitForExit().ShouldBeTrue(server.Output);
        server.Process.ExitCode.ShouldBe(0, server.Output);
        server.Output.ShouldContain("Stopping:");
        server.Output.ShouldContain("Server stopped.");
    }

    /// <summary>T-710: with accounts and saving on, Ctrl+C also says goodbye to whoever plays and saves them.</summary>
    [Accounts.DatabaseFact]
    public async Task Ctrl_C_with_a_database_saves_every_character_and_stops_cleanly()
    {
        using var server = StartWith(databases.ConnectionString, Quiet);
        server.WaitForLine("Running. Press Ctrl+C to stop.").ShouldBeTrue(server.Output);
        var endpoint = new Uri(System.Text.RegularExpressions.Regex.Match(server.Output, @"Gateway listening on (\S+)").Groups[1].Value);
        var hash = System.Text.RegularExpressions.Regex.Match(server.Output, @"Content hash: (\S+)").Groups[1].Value;
        using var http = new Accounts.AccountHttp(endpoint);
        var token = await http.RegisterAsync("ctrlc" + Guid.NewGuid().ToString("N")[..10]);
        var character = await http.CreateAsync(token, new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 12)));
        using var client = await Network.TestClient.ConnectAsync(endpoint);
        await client.SendAsync(new Game.Shared.Protocol.HelloMessage(Game.Shared.Protocol.ProtocolVersion.Current, null, hash, null, token, character));
        await client.WaitForAsync<Game.Shared.Protocol.SnapshotMessage>();
        var start = client.Seen(client.Id)!.Cell;
        await client.SendAsync(new Game.Shared.Protocol.MoveMessage(new Game.Shared.World.GridCell(start.X + 1, start.Y)));
        var there = await Network.Poll.UntilAsync(() => client.Seen(client.Id), r => r is { Next: null } && !r.Cell.Equals(start), "the first step");

        SendCtrlC(server.Process);

        server.WaitForExit().ShouldBeTrue(server.Output);
        server.Process.ExitCode.ShouldBe(0, server.Output);
        (await client.WaitForByeAsync()).ShouldBe(Game.Shared.Protocol.ByeReasons.Shutdown);
        server.Output.ShouldContain("Every character is saved");
        server.Output.ShouldContain("Server stopped.");
        await using var db = databases.Database.Open();
        var id = Guid.Parse(character);
        var row = db.CharacterStates.Single(r => r.CharacterId == id);
        (row.CellX, row.CellY).ShouldBe((there!.Cell.X, there.Cell.Y));
        row.HeldBy.ShouldBeNull("it is free for the next server at once");
    }

    [Fact]
    public void A_broken_audio_entry_makes_validate_and_the_server_refuse_with_the_same_message()
    {
        var root = Path.Combine(Path.GetTempPath(), "bootaudio_" + Guid.NewGuid().ToString("N"));
        _temp.Add(root);
        CopyDirectory(Path.Combine(RepoRoot(), "content"), Path.Combine(root, "content"));
        Directory.CreateDirectory(Path.Combine(root, "themes", "default"));
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "themes", "default", "asset-manifest.json"));
        text.ShouldContain("\"ui.tap\": \"sfx.ui.tap\"");
        File.WriteAllText(Path.Combine(root, "themes", "default", "asset-manifest.json"), text.Replace("\"ui.tap\": \"sfx.ui.tap\"", "\"ui.tapp\": \"sfx.ui.tap\""));
        var content = Path.Combine(root, "content");

        // The shared boot refuses ...
        ContentBoot.Load(content, "default", out var issues).ShouldBeNull();
        var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(i => i.File == "themes/default/asset-manifest.json");

        // ... validate prints those lines and fails ...
        var (exit, validateOutput) = RunValidate(content);
        exit.ShouldBe(1);
        foreach (var error in errors) validateOutput.ShouldContain(error.ToString());

        // ... and so does the server process, with the same text.
        using var server = Start("--environment", "Development", "--net-port", "0", content);
        server.WaitForExit().ShouldBeTrue(server.Output);
        server.Process.ExitCode.ShouldBe(1, server.Output);
        server.Output.ShouldContain("refusing to start");
        foreach (var error in errors) server.Output.ShouldContain(error.ToString());
        server.Output.ShouldNotContain("Starting ");
    }

    private static (int Exit, string Output) RunValidate(string content)
    {
        var root = new RootCommand();
        root.Subcommands.Add(ValidateCommand.Create());
        var oldOut = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var exit = root.Parse(["validate", "--content", content, "--schemas", Path.Combine(RepoRoot(), "schemas")]).Invoke();
            return (exit, stdout.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Ctrl+C for a child process: SIGINT elsewhere; on Windows attach to the child's console and raise CTRL_C_EVENT there.</summary>
    private static void SendCtrlC(Process child)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var kill = Process.Start("kill", ["-INT", child.Id.ToString(CultureInfo.InvariantCulture)])!;
            kill.WaitForExit();
            return;
        }

        FreeConsole();                                    // this process may have its own console (or none)
        try
        {
            AttachConsole(child.Id).ShouldBeTrue("could not attach to the server's console");
            SetConsoleCtrlHandler(IntPtr.Zero, true);     // ignore the event in this process
            GenerateConsoleCtrlEvent(0, 0).ShouldBeTrue();
            child.WaitForExit(Patience);
        }
        finally
        {
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
