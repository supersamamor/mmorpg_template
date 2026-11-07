using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Content.ClientExport;
using Game.Shared.Random;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>"No plaintext outside debug" (T-216, ARCHITECTURE §10): the pure rule and the places that must obey it.</summary>
public class TransportPolicyTests
{
    [Theory]
    [InlineData("wss://game.example.com/ws")]
    [InlineData("WSS://game.example.com:8443/ws")]
    [InlineData("  wss://203.0.113.5/ws  ")]
    public void Secure_urls_are_allowed_in_every_build(string url)
    {
        TransportPolicy.Check(url, allowCleartext: false).ShouldBe(TransportVerdict.Allowed);
        TransportPolicy.Check(url, allowCleartext: true).ShouldBe(TransportVerdict.Allowed);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:7780/ws")]
    [InlineData("WS://game.example.com/ws")]
    [InlineData("ws://192.168.1.20:7780/ws")]
    public void Plain_urls_are_refused_in_release_and_allowed_in_debug(string url)
    {
        TransportPolicy.Check(url, allowCleartext: false).ShouldBe(TransportVerdict.CleartextRefused);
        TransportPolicy.Check(url, allowCleartext: true).ShouldBe(TransportVerdict.Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://game.example.com/ws")]
    [InlineData("https://game.example.com/ws")]
    [InlineData("game.example.com/ws")]
    [InlineData("wss://")]
    [InlineData("ws://")]
    [InlineData("wss:///ws")]
    [InlineData("ws://:7780/ws")]
    [InlineData("wss:// game.example.com")]
    [InlineData("ftp://x/ws")]
    public void Anything_else_is_invalid_in_every_build(string? url)
    {
        TransportPolicy.Check(url, allowCleartext: false).ShouldBe(TransportVerdict.Invalid);
        TransportPolicy.Check(url, allowCleartext: true).ShouldBe(TransportVerdict.Invalid);
    }

    [Fact]
    public void Cleartext_is_off_by_default()
    {
        new ClientConfig().AllowCleartext.ShouldBeFalse();
    }

    [Fact]
    public void A_settings_file_or_an_argument_cannot_turn_cleartext_on()
    {
        var config = new ClientConfig();
        config.ApplyJson("""{"allow_cleartext":true,"allowCleartext":true,"AllowCleartext":true,"cleartext":"true"}"""u8);
        config.AllowCleartext.ShouldBeFalse();

        config.ApplyArgs(["--set", "allow_cleartext=true", "--set", "AllowCleartext=1", "--allow-cleartext", "true"]);
        config.AllowCleartext.ShouldBeFalse();
    }

    [Fact]
    public void The_shipped_settings_do_not_mention_cleartext()
    {
        var path = Path.Combine(FindRoot(), "client", "client_settings.json");
        File.ReadAllText(path).ShouldNotContain("cleartext", Case.Insensitive);
    }

    [Fact]
    public void Only_the_engine_start_up_sets_the_flag_from_the_debug_build()
    {
        var root = FindRoot();
        var setters = Directory.GetFiles(Path.Combine(root, "client"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".godot" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("AllowCleartext =", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToArray();
        setters.ShouldBe(["client/Scene/Main.cs"]);
        File.ReadAllText(Path.Combine(root, "client", "Scene", "Main.cs")).ShouldContain("AllowCleartext = OS.IsDebugBuild();");
    }

    [Fact]
    public void The_refusal_screens_say_what_is_wrong()
    {
        UiText.TransportRefused(TransportVerdict.CleartextRefused).ShouldContain("wss://");
        UiText.TransportRefused(TransportVerdict.Invalid).ShouldNotBe(UiText.CleartextRefused);
    }

    [Fact]
    public void A_session_never_opens_a_plain_socket_in_release_and_does_in_debug()
    {
        var release = Open("ws://127.0.0.1:7780/ws", allowCleartext: false);
        release.Opened.ShouldBe(0);

        var debug = Open("ws://127.0.0.1:7780/ws", allowCleartext: true);
        debug.Opened.ShouldBe(1);

        var secure = Open("wss://game.example.com/ws", allowCleartext: false);
        secure.Opened.ShouldBe(1);

        var invalid = Open("http://game.example.com/ws", allowCleartext: true);
        invalid.Opened.ShouldBe(0);
    }

    private static CountingSocket Open(string url, bool allowCleartext)
    {
        var socket = new CountingSocket();
        DisplayBundle.TryRead(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        var session = new ClientSession(new ClientConfig { ServerUrl = url, AllowCleartext = allowCleartext }, bundle!, socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        return socket;
    }

    private sealed class CountingSocket : IClientSocket
    {
        public int Opened;

        public void Open(string url) => Opened++;

        public void Send(byte[] utf8)
        {
        }

        public void Close()
        {
        }

        public bool TryDequeue(out SocketEvent socketEvent)
        {
            socketEvent = default;
            return false;
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
