using System.Net;
using System.Text.RegularExpressions;
using Game.Server.Net;
using Game.Tests.Network;
using YamlDotNet.RepresentationModel;

namespace Game.Tests.Server;

/// <summary>
/// T-807, local play (docs/LOCAL_PLAY.md): the server in the container of <c>docker-compose.yml</c>'s <c>play</c> profile gets exactly these settings, and they
/// keep ARCHITECTURE §10's transport rules: it is not in development, so it listens inside the container on every interface only with TLS and refuses to start
/// without a certificate; the development rule (loopback only) is untouched; and the port reaches the PC's loopback only. The files themselves are read, so a
/// change to them that breaks one of these fails here. The running container is checked by <c>client/check-local-play.ps1</c> (it needs Docker).
/// </summary>
public class LocalPlayTests
{
    private static readonly string[] PlayServices = ["play-db", "play-server"];

    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory), name);
    }

    private static string[] Lines(string name) => File.ReadAllLines(RepoFile(name)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();

    private static YamlMappingNode Compose()
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(RepoFile("docker-compose.yml"));
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static bool Has(YamlMappingNode node, string key) => node.Children.ContainsKey(new YamlScalarNode(key));

    private static string[] List(YamlMappingNode node, string key) =>
        Has(node, key) ? ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Select(n => ((YamlScalarNode)n).Value!).ToArray() : [];

    private static YamlMappingNode Service(string name) => Map(Map(Compose(), "services"), name);

    /// <summary>What compose makes of a value when nothing is set: <c>${NAME:-default}</c> is the default, which may hold another one.</summary>
    private static string Defaults(string value)
    {
        var pattern = new Regex(@"\$\{[A-Z0-9_]+:-([^${}]*)\}");
        while (pattern.IsMatch(value)) value = pattern.Replace(value, "$1");
        value.ShouldNotContain("${", customMessage: "every variable needs a default: a plain 'docker compose up -d' reads the whole file");
        return value;
    }

    private static Dictionary<string, string> ContainerEnvironment() =>
        Map(Service("play-server"), "environment").Children.ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => Defaults(((YamlScalarNode)p.Value).Value!));

    /// <summary>The gateway's options as <c>Program</c> makes them inside the container; <paramref name="change"/> alters the environment first.</summary>
    private static GatewayOptions ContainerOptions(Action<Dictionary<string, string>>? change = null)
    {
        var environment = ContainerEnvironment();
        change?.Invoke(environment);
        var development = string.Equals(environment["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);
        var options = GatewayOptions.Parse([], name => environment.GetValueOrDefault(name), development, out var error);
        error.ShouldBeNull();
        return options.ShouldNotBeNull();
    }

    [Fact]
    public void The_container_is_not_in_development_and_listens_on_every_interface_with_tls_only()
    {
        var certificate = Path.Combine(Path.GetTempPath(), $"game-tests-{Guid.NewGuid():N}.pfx");
        GatewayRig.WriteSelfSignedCertificate(certificate);
        try
        {
            var options = ContainerOptions();
            options.Development.ShouldBeFalse();
            options.CertificatePath.ShouldBe("/certs/server.pfx");
            options.ListenAddress.ShouldBe(IPAddress.Any);   // Docker forwards the published port to the container's own interface
            options.Port.ShouldBe(7780);

            // With the certificate where the options say (here: a throw-away one), nothing is wrong with them.
            (options with { CertificatePath = certificate }).Problems().ShouldBeEmpty();
        }
        finally { File.Delete(certificate); }
    }

    [Fact]
    public void Without_its_certificate_the_container_s_server_refuses_to_start()
    {
        ContainerOptions().Problems().ShouldContain(p => p.Contains("does not exist"));   // nothing mounted at /certs
        ContainerOptions(e => e.Remove("GAME_NET_CERTIFICATE_PATH")).Problems().ShouldContain(p => p.Contains("TLS certificate"));
    }

    [Fact]
    public void Turned_into_development_the_container_s_settings_still_mean_loopback_only()
    {
        // The way to plain ws:// from a container would be development mode on another address than loopback, and that is refused.
        var options = ContainerOptions(e =>
        {
            e["DOTNET_ENVIRONMENT"] = "Development";
            e.Remove("GAME_NET_CERTIFICATE_PATH");
        });
        options.Development.ShouldBeTrue();
        IPAddress.IsLoopback(options.ListenAddress).ShouldBeTrue();
        foreach (var address in new[] { "0.0.0.0", "::", "172.18.0.3" })
            (options with { BindAddress = address }).Problems().ShouldContain(p => p.Contains("loopback only"), address);
    }

    [Fact]
    public void The_container_gets_no_listen_address_or_password_beyond_what_it_needs()
    {
        var environment = ContainerEnvironment();
        environment.Keys.ShouldBe(["DOTNET_ENVIRONMENT", "GAME_DB_CONNECTION", "GAME_NET_PORT", "GAME_NET_CERTIFICATE_PATH", "GAME_NET_CERTIFICATE_PASSWORD"], ignoreOrder: true);
        environment["GAME_NET_CERTIFICATE_PASSWORD"].ShouldBeEmpty("the certificate's password is made on each PC, never written in the file");
        environment["GAME_DB_CONNECTION"].ShouldContain("Host=play-db;");   // the play stack's own database, not the development one
        Map(Service("play-server"), "environment").Children.Values.Select(v => ((YamlScalarNode)v).Value!)
            .ShouldContain(v => v.StartsWith("${MMO_PLAY_DB_CONNECTION:-", StringComparison.Ordinal), "the connection string comes from the environment");
    }

    [Fact]
    public void Plain_compose_starts_what_it_did_before_and_the_play_services_are_behind_the_profile()
    {
        var services = Map(Compose(), "services").Children.ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => (YamlMappingNode)p.Value);
        services.Where(s => !Has(s.Value, "profiles")).Select(s => s.Key).ShouldBe(["postgres", "pgadmin"], ignoreOrder: true);
        foreach (var name in PlayServices)
        {
            List(services[name], "profiles").ShouldBe(["play"], name);
            // A fixed name would clash between projects (the development database has one: a play service must not share it).
            Has(services[name], "container_name").ShouldBeFalse(name);
        }
        List(services["postgres"], "volumes").ShouldBe(["mmo_pgdata:/var/lib/postgresql/data"]);
        List(services["play-db"], "volumes").ShouldBe(["mmo_play_pgdata:/var/lib/postgresql/data"]);
    }

    [Fact]
    public void Only_the_game_port_is_published_and_only_on_loopback()
    {
        Has(Service("play-db"), "ports").ShouldBeFalse();
        var ports = List(Service("play-server"), "ports");
        ports.Length.ShouldBe(1);
        Defaults(ports[0]).ShouldBe("127.0.0.1:7780:7780");
        ports[0].ShouldStartWith("127.0.0.1:");   // not a variable: no setting publishes it to the network
        Has(Service("play-server"), "network_mode").ShouldBeFalse();
        List(Service("play-server"), "volumes").ShouldAllBe(v => v.EndsWith(":/certs:ro", StringComparison.Ordinal));
    }

    [Fact]
    public void The_server_waits_for_a_healthy_database_and_has_time_to_save()
    {
        var server = Service("play-server");
        ((YamlScalarNode)Map(Map(server, "depends_on"), "play-db").Children[new YamlScalarNode("condition")]).Value.ShouldBe("service_healthy");
        List(Map(Service("play-db"), "healthcheck"), "test")[1].ShouldContain("-h 127.0.0.1");   // not ready until it answers over TCP
        Has(server, "healthcheck").ShouldBeTrue();
        var grace = int.Parse(((YamlScalarNode)server.Children[new YamlScalarNode("stop_grace_period")]).Value!.TrimEnd('s'));
        grace.ShouldBeGreaterThan(new Game.Server.Hosting.ServerOptions("", "default", null, null).SaveShutdownWaitSeconds);
    }

    [Fact]
    public void The_image_runs_the_server_unprivileged_after_migrating_and_in_production_unless_told_otherwise()
    {
        var lines = Lines("Dockerfile");
        var runtime = lines.Skip(Array.FindLastIndex(lines, l => l.StartsWith("FROM ", StringComparison.Ordinal))).ToArray();
        runtime.Last(l => l.StartsWith("USER ", StringComparison.Ordinal)).ShouldBe("USER $APP_UID");
        var entry = runtime.Single(l => l.StartsWith("ENTRYPOINT ", StringComparison.Ordinal));
        entry.ShouldContain("--migrate && exec dotnet Game.Server.dll");   // exec: SIGTERM reaches the server, which saves
        // The image itself never asks for development: that would open plain ws:// and the dev login.
        string.Join('\n', lines).ShouldNotContain("Development");
        runtime.Where(l => l.StartsWith("COPY ", StringComparison.Ordinal)).ShouldBe(["COPY --from=build /out/ ./", "COPY content/ ./content/", "COPY themes/ ./themes/"]);
    }

    [Fact]
    public void The_build_context_holds_only_what_the_server_needs()
    {
        var rules = Lines(".dockerignore");
        rules[0].ShouldBe("**");   // everything out first
        rules.Where(r => r.StartsWith('!')).ShouldBe([
            "!global.json", "!Directory.Build.props", "!Directory.Packages.props", "!.editorconfig", "!src/Game.Shared/**", "!src/Game.Content/**", "!src/Game.Server/**",
            "!content/**", "!themes/*/asset-manifest.json"
        ]);
        rules.ShouldContain("**/*.pfx");
    }

    [Fact]
    public void The_local_certificate_is_never_committed()
    {
        Lines(".gitignore").ShouldContain("/.play/");
        Lines(".gitignore").ShouldContain("*.pfx");
        ((YamlScalarNode)((YamlSequenceNode)Service("play-server").Children[new YamlScalarNode("volumes")]).First()).Value!.ShouldContain("./.play/");
    }
}
