using System.CommandLine;
using Game.Tools.Editor;

namespace Game.Tools.Commands;

/// <summary>
/// <c>editor [--content &lt;dir&gt;] [--host 127.0.0.1] [--port 7880] [--theme name]</c> (T-801): serves the local content editor (docs/CONTENT_EDITING.md) until Ctrl+C. It listens on a
/// loopback address only and refuses any other (exit code 2): it has no sign-in.
/// </summary>
internal static class EditorCommand
{
    public static Command Create()
    {
        var contentOption = new Option<DirectoryInfo?>("--content") { Description = "Content directory (default: <repo>/content); schemas/ and themes/ are next to it" };
        var hostOption = new Option<string>("--host") { Description = "Loopback address to listen on (127.0.0.1 or ::1; anything else is refused)", DefaultValueFactory = _ => "127.0.0.1" };
        var portOption = new Option<int>("--port") { Description = "Port (0: any free one)", DefaultValueFactory = _ => 7880 };
        var themeOption = new Option<string>("--theme") { Description = "Theme whose asset keys references may name", DefaultValueFactory = _ => "default" };
        var command = new Command("editor", "Serve the local content editor (loopback only)") { contentOption, hostOption, portOption, themeOption };
        command.SetAction(parse => Run(parse.GetValue(contentOption), parse.GetValue(hostOption)!, parse.GetValue(portOption), parse.GetValue(themeOption)!));
        return command;
    }

    private static int Run(DirectoryInfo? contentDir, string host, int port, string theme)
    {
        if (EditorServer.Problem(host) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 2;
        }
        var content = contentDir?.FullName ?? Path.Combine(FindRepoRoot(), "content");
        var editor = new ContentEditor(content, Path.Combine(content, "..", "schemas"), Path.Combine(content, "..", "themes"), theme);
        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        var server = EditorServer.StartAsync(editor, host, port).GetAwaiter().GetResult();
        Console.WriteLine($"Content editor for {content}: {server.Address} (kinds: {string.Join(", ", editor.Kinds.Select(k => k.Kind))}). Ctrl+C stops it.");
        stop.Wait();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new InvalidOperationException("Game.sln not found above the current directory; run from the repo or pass --content.");
    }
}
