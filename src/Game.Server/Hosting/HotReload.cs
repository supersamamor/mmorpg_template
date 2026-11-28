using System.Net;
using Game.Content.ClientExport;
using Game.Content.Loading;
using Game.Content.Registry;
using Game.Content.Validation;
using Game.Server.World;
using Game.Server.World.Reload;
using Game.Shared.Definitions;
using Game.Shared.Stats;

namespace Game.Server.Hosting;

/// <summary>Whether this server may watch its content (T-801): <c>--hot-reload</c> is for a development server on loopback, in a debug build.</summary>
public static class HotReloadGate
{
    /// <summary>True in a debug build of the server (the configuration <c>dotnet run</c> and the tests use); false in a release build.</summary>
#if DEBUG
    public const bool DebugBuild = true;
#else
    public const bool DebugBuild = false;
#endif

    /// <summary>
    /// Why <c>--hot-reload</c> is refused here, or nothing. A release build, an environment other than Development and a gateway that listens on anything but a
    /// loopback address each refuse it: the server then does not start (exit code 1), so a production server can never run with it.
    /// </summary>
    public static IReadOnlyList<string> Problems(bool debugBuild, bool development, IPAddress listenAddress)
    {
        var problems = new List<string>();
        if (!debugBuild) problems.Add("this is a release build; hot reload is only in debug builds (dotnet run)");
        if (!development) problems.Add($"hot reload is for development servers only (--environment {ServerOptions.DevelopmentName})");
        if (!IPAddress.IsLoopback(listenAddress)) problems.Add($"the gateway listens on {listenAddress}, not on a loopback address; hot reload needs --net-bind-address 127.0.0.1 or none");
        return problems;
    }
}

/// <summary>
/// What a running server cannot take from a reload in v1 (T-801, ARCHITECTURE §5.5). Each reason refuses the whole reload: what runs is always one validated set,
/// never a mix. The rest swaps live.
/// </summary>
public static class HotReloadRules
{
    /// <summary>
    /// <c>balance.yaml</c> sections (YAML keys) read once at start by something outside the tick: the AOI grid of each instance (<c>aoi</c>), the accounts and
    /// the guild service (<c>characters</c>, <c>guild</c>), the party service (<c>party</c>), and the gateway's own-player messages and new characters
    /// (<c>new_character</c>, <c>progression</c>, <c>quests</c>, <c>inventory</c>; Net/** builds them at start).
    /// </summary>
    public static readonly IReadOnlySet<string> RestartSections = new SortedSet<string>(StringComparer.Ordinal)
    {
        "aoi", "characters", "guild", "inventory", "new_character", "party", "progression", "quests",
    };

    /// <summary>
    /// Definition types whose ids a character's saved state names (its bag and cards, skills, job and unlocked jobs, quests, outfits): removing one is refused, since
    /// only a load sets such a thing aside (ARCHITECTURE §3 "Loading") and a playing character may hold it. Adding and changing them reloads.
    /// </summary>
    public static readonly IReadOnlyList<Type> HeldKinds = [typeof(ItemDef), typeof(SkillDef), typeof(JobDef), typeof(QuestDef), typeof(OutfitDef)];

    /// <summary>
    /// Definition types and root files the gateway's own-player messages and new characters read (Net/**, built at start): the EXP tables, jobs, skills, quests
    /// and outfits behind the player's <c>exp</c>, <c>skills</c>, <c>stats</c>, <c>quest_log</c> and <c>wardrobe</c>, and <c>appearance.yaml</c>. A change to them
    /// needs a restart in v1.
    /// </summary>
    public static readonly IReadOnlyList<Type> GatewayKinds = [typeof(ExpTableDef), typeof(JobDef), typeof(SkillDef), typeof(QuestDef), typeof(OutfitDef)];

    /// <summary>Every reason <paramref name="diff"/> cannot be swapped into a running server; empty when it can.</summary>
    public static IReadOnlyList<string> RestartReasons(ContentDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var reasons = new List<string>();
        if (diff.Grids.Count > 0)
            reasons.Add($"map grids cannot hot-reload in v1 (each instance is built on its grid at start): {string.Join(", ", diff.Grids)}");
        var maps = diff.Of<MapDef>();
        if (!maps.IsEmpty)
            reasons.Add($"maps cannot hot-reload in v1 (instances are made at start): {string.Join(", ", maps.Added.Concat(maps.Removed).Concat(maps.Changed).Order(StringComparer.Ordinal))}");
        foreach (var kind in diff.Kinds)
        {
            if (HeldKinds.Contains(kind.DefinitionType) && kind.Removed.Count > 0)
                reasons.Add($"removing {string.Join(", ", kind.Removed)} needs a restart: a playing character may hold it, and only a load sets it aside");
            if (GatewayKinds.Contains(kind.DefinitionType))
                reasons.Add($"{kind.Kind} cannot hot-reload in v1 (the gateway's own-player messages read them from the content it started with): {string.Join(", ", kind.Added.Concat(kind.Removed).Concat(kind.Changed).Order(StringComparer.Ordinal))}");
        }
        if (diff.RootFiles.Contains(ContentLayout.AppearanceFile))
            reasons.Add($"{ContentLayout.AppearanceFile} cannot hot-reload in v1 (accounts and new characters read it at start)");
        var sections = diff.BalanceSections.Where(RestartSections.Contains).ToList();
        if (sections.Count > 0)
            reasons.Add($"{ContentLayout.BalanceFile} {string.Join(", ", sections)} cannot hot-reload in v1 (read once at start outside the tick)");
        return reasons;
    }
}

/// <summary>How one reload attempt ended.</summary>
public enum ReloadOutcome
{
    /// <summary>The new set was handed to every instance (it swaps in at each one's next tick).</summary>
    Swapped,

    /// <summary>The files changed but the content they make is the same: nothing to do.</summary>
    Unchanged,

    /// <summary>The set has validation errors; the running content stays.</summary>
    Invalid,

    /// <summary>The set is valid but changes something that cannot hot-reload in v1; the running content stays.</summary>
    NeedsRestart,
}

/// <summary>
/// Hot reload of the content (T-801): watches <c>content/</c> and the theme's asset manifest, and after a change has settled loads and validates the whole set off the
/// tick threads with <see cref="ContentBoot"/> (every rule and cross-reference, as at boot). A set with an error is refused and its errors logged; a valid one that
/// changes something that cannot reload live (<see cref="HotReloadRules"/>) is refused with the reason; anything else is built (<see cref="ServerContent.Build"/>) and
/// handed to the world, whose instances swap it in between ticks. Afterwards the listeners get it (the character loader, the gateway's content hash and message).
/// Nothing in the database is written by a reload. One attempt at a time.
/// </summary>
public sealed class ContentReloader : IDisposable
{
    /// <summary>How long the files must be quiet before a reload starts (an editor's save is often several writes).</summary>
    public const int SettleMs = 300;

    private readonly string _contentDir;
    private readonly string _theme;
    private readonly string _manifestPath;
    private readonly GameWorld _world;
    private readonly Func<ContentDatabase, Func<Entity, StatSheet?>> _sheets;
    private readonly IServerLog _log;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _timer;
    private int _running;
    private bool _again;

    /// <summary>
    /// A reloader of <paramref name="contentDir"/> (theme <paramref name="theme"/>) for <paramref name="world"/>, which runs <paramref name="current"/> with display hash
    /// <paramref name="currentHash"/>. <paramref name="sheets"/> makes, from new content, the builder of a returning player's stat sheet.
    /// </summary>
    public ContentReloader(string contentDir, string theme, GameWorld world, ServerContent current, string currentHash, Func<ContentDatabase, Func<Entity, StatSheet?>> sheets,
        IServerLog log)
    {
        _contentDir = Path.GetFullPath(contentDir);
        _theme = theme;
        _manifestPath = AssetManifest.PathFor(Path.Combine(_contentDir, "..", "themes"), theme);
        _world = world ?? throw new ArgumentNullException(nameof(world));
        Current = current ?? throw new ArgumentNullException(nameof(current));
        CurrentHash = currentHash;
        _sheets = sheets ?? throw new ArgumentNullException(nameof(sheets));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The content the world runs, or is about to (the last set handed to it).</summary>
    public ServerContent Current { get; private set; }

    /// <summary>The display bundle hash of <see cref="Current"/>.</summary>
    public string CurrentHash { get; private set; }

    /// <summary>The last swap handed to the world, or null.</summary>
    public ContentSwap? LastSwap { get; private set; }

    /// <summary>Called after a set was handed to the world (off the tick threads): the content and its display hash.</summary>
    public event Action<ServerContent, string>? Reloaded;

    /// <summary>Starts watching. Each change starts (or restarts) the <see cref="SettleMs"/> wait.</summary>
    public void Start()
    {
        var content = new FileSystemWatcher(_contentDir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size };
        Watch(content);
        var manifestDir = Path.GetDirectoryName(_manifestPath);
        if (manifestDir is not null && Directory.Exists(manifestDir))
            Watch(new FileSystemWatcher(manifestDir, Path.GetFileName(_manifestPath)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size });
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
        _log.Info($"Hot reload on (development): watching {_contentDir} and {_manifestPath}. A valid change swaps in between ticks; see docs/CONTENT_EDITING.md.");
    }

    private void Watch(FileSystemWatcher watcher)
    {
        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += OnChange;
        watcher.Error += (_, e) => _log.Warn($"[reload] the file watcher lost changes ({e.GetException().Message}); reloading to catch up.");
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? "";
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith('~')) return;   // an editor's temporary file; its rename follows
        _timer?.Change(SettleMs, Timeout.Infinite);
    }

    private void Run()
    {
        lock (_gate)
        {
            if (_running == 1)
            {
                _again = true;   // a change during a reload: one more afterwards
                return;
            }
            _running = 1;
        }
        try
        {
            TryReload();
        }
        catch (Exception ex)
        {
            _log.Error("[reload] the reload failed; the running content stays.", ex);
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                _running = 0;
                again = _again;
                _again = false;
            }
            if (again) _timer?.Change(SettleMs, Timeout.Infinite);
        }
    }

    /// <summary>One reload attempt, now, on the calling thread (the watcher's timer, or a test).</summary>
    public ReloadOutcome TryReload()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var database = ContentBoot.Load(_contentDir, _theme, out var issues);
        if (database is null)
        {
            _log.Error($"[reload] REFUSED: the content has errors; the running content stays.{Environment.NewLine}{ValidationReportFormatter.ToText(issues)}");
            return ReloadOutcome.Invalid;
        }
        var diff = ContentDiff.Compare(Current.Database, database);
        if (diff.IsEmpty)
        {
            _log.Info("[reload] the files changed, the content did not: nothing to reload.");
            return ReloadOutcome.Unchanged;
        }
        var restart = HotReloadRules.RestartReasons(diff);
        if (restart.Count > 0)
        {
            foreach (var reason in restart) _log.Warn($"[reload] NEEDS A RESTART: {reason}.");
            _log.Warn("[reload] nothing was reloaded; the running content stays. Restart the server to use the change.");
            return ReloadOutcome.NeedsRestart;
        }
        ServerContent next;
        try
        {
            next = ServerContent.Build(database, Current.Generation + 1);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _log.Error($"[reload] REFUSED: the content cannot be used: {ex.Message}; the running content stays.");
            return ReloadOutcome.Invalid;
        }
        var hash = ClientBundleExporter.Hash(database);
        var generation = next.Generation;
        var swap = new ContentSwap(next, _sheets(database), (instance, players, removed, took) =>
            _log.Info($"[reload] [{instance.MapId}] content {generation} swapped in at tick {instance.TickNumber}: {players} player(s) re-entered, {removed} monster(s) and NPC(s) respawn; {took.TotalMilliseconds:0.0} ms."));
        _world.Reload(swap);
        LastSwap = swap;
        Current = next;
        var bundleChanged = !string.Equals(hash, CurrentHash, StringComparison.Ordinal);
        CurrentHash = hash;
        foreach (var line in diff.Describe()) _log.Info($"[reload] {line}");
        _log.Info($"[reload] content {generation} validated and handed to {_world.Instances.Count} instance(s) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms; " +
                  $"content hash {hash}{(bundleChanged ? " (the display bundle changed: clients are told)" : " (display bundle unchanged)")}.");
        Reloaded?.Invoke(next, hash);
        return ReloadOutcome.Swapped;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _timer?.Dispose();
        _timer = null;
    }
}
