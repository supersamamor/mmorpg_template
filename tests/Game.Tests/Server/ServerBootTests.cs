using Game.Content.Loading;
using Game.Content.Validation;
using Game.Server.Hosting;
using Game.Server.World;
using Game.Shared.Vocabulary;

namespace Game.Tests.Server;

/// <summary>Boot path against the real sample content: validate, one instance per map, a content warp end to end.</summary>
public class ServerBootTests
{
    private static string ContentDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Game.sln not found"), "content");
    }

    [Fact]
    public void Sample_content_boots_into_one_instance_per_map_and_its_warps_work()
    {
        var content = ContentBoot.Load(ContentDir(), "default", out var issues);
        issues.Where(i => i.Severity == ValidationSeverity.Error).ShouldBeEmpty();
        content.ShouldNotBeNull();

        var world = new GameWorld(content.Grids.Values, new SystemPipeline(), 1, new ManualClock(), new ListLog());
        world.Instances.Select(i => i.MapId).ShouldBe(content.Maps.All.Select(m => m.Id).Order(StringComparer.Ordinal));

        var source = world.Instances.First(i => i.Grid.Warps.Count > 0);
        var warp = source.Grid.Warps[0];
        var id = world.Spawn(EntityKind.Player, source.MapId, warp.Cell, Directions.S);
        source.Tick();
        world.Warp(id, warp.TargetMapId, warp.TargetCell);
        source.Tick();
        var target = world.GetInstance(warp.TargetMapId);
        target.Tick();

        target.TryGetEntity(id, out var entity).ShouldBeTrue();
        entity.Cell.ShouldBe(warp.TargetCell);
    }

    [Fact]
    public void Options_parse_positional_content_and_flags()
    {
        var options = ServerOptions.Parse(["some/dir", "--seed", "42", "--duration", "1.5", "--theme", "night"], out var error);
        error.ShouldBeNull();
        options.ShouldBe(new ServerOptions("some/dir", "night", 42UL, 1.5));

        ServerOptions.Parse(["--seed"], out error).ShouldBeNull();
        error.ShouldNotBeNull();
        ServerOptions.Parse(["a", "b"], out _).ShouldBeNull();
        ServerOptions.Parse(["--nope"], out _).ShouldBeNull();
    }

    [Fact]
    public void Bad_content_refuses_to_boot()
    {
        var empty = Path.Combine(Path.GetTempPath(), "boot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            ContentBoot.Load(empty, "default", out var issues).ShouldBeNull();
            issues.ShouldContain(i => i.Severity == ValidationSeverity.Error);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
