using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Client.Core.Bundle;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene.Audio;

/// <summary>
/// Turns what the view model shows into sound requests (ARCHITECTURE §7.8) and owns the engine side. It reads: the swings and impacts of an <see cref="IImpactSource"/>
/// (the timing source shared with the combat visuals), monsters that died, the map's music and ambience, level ups and interface taps. It decides nothing about the game:
/// every sound follows something the server already said. Allocates nothing per frame after warm-up when the trace is off.
/// </summary>
public sealed class AudioDriver
{
    private sealed class Seen
    {
        public bool Dead;
        public bool Aggro;
        public int Stamp;
    }

    private readonly ClientConfig _config;
    private readonly DisplayBundle _bundle;
    private readonly WorldView _world;
    private readonly Dictionary<long, Seen> _seen = new();
    private readonly List<long> _stale = new();
    private readonly GodotAudioSink _sink;
    private readonly PlatformAudio _platform;
    private IImpactSource _impacts;
    private int _stamp;

    /// <summary>Builds the audio system under <paramref name="parent"/>.</summary>
    public AudioDriver(Node parent, ClientConfig config, SoundCatalog catalog, DisplayBundle bundle, WorldView world, IRandom random, UserPreferences preferences)
    {
        _config = config;
        _bundle = bundle;
        _world = world;
        Preferences = preferences;
        _sink = new GodotAudioSink(parent, config, message => GD.PushWarning(message));
        Director = new AudioDirector(catalog, random, _sink, config, config.AudioTrace ? Print : null);
        Preferences.ApplyTo(Director.Mixer);
        Scheduler = new ImpactScheduler();
        _impacts = Scheduler;
        _platform = new PlatformAudio(Director.App);
        WeaponTypeOf = DefaultWeaponType;
        foreach (var key in bundle.SoundKeys.Concat(catalog.Defaults.Values).Concat(catalog.Events.Values))
            if (catalog.TryResolve(key, out var sound)) _sink.Preload(sound);
        GD.Print($"[audio] ready: {_sink.PlayerCount} players, {_sink.LoadedEffects} effect files loaded ({_sink.Budget.Used / 1024.0 / 1024.0:0.00} MB decoded of {_sink.Budget.Limit / 1024 / 1024} MB), trace {(config.AudioTrace ? "on" : "off")}, {_platform.Describe()}");
    }

    /// <summary>The pure director (volumes, stats, the pool).</summary>
    public AudioDirector Director { get; }

    /// <summary>The engine side (memory figures).</summary>
    public GodotAudioSink Sink => _sink;

    /// <summary>The player's stored choices; the HUD saves them.</summary>
    public UserPreferences Preferences { get; }

    /// <summary>The interim impact timing, fed with <see cref="OnHits"/> until the combat visuals' source takes over (<see cref="UseImpactSource"/>).</summary>
    public ImpactScheduler Scheduler { get; }

    /// <summary>Weapon type of an entity's attacks, or null for none. T-401 (equipment) replaces the default, which says unarmed for every character: the wire does not carry weapons yet.</summary>
    public Func<EntityView, string?> WeaponTypeOf { get; set; }

    /// <summary>Plugs in another timing source (the combat visuals').</summary>
    public void UseImpactSource(IImpactSource source) => _impacts = source;

    /// <summary>A delta's hits arrived (the interim timing source only).</summary>
    public void OnHits(IReadOnlyList<HitRecord> hits, double viewNow)
    {
        if (!ReferenceEquals(_impacts, Scheduler)) return;
        for (var i = 0; i < hits.Count; i++)
            Scheduler.Queue(new ImpactEvent(hits[i].Attacker, hits[i].Target, hits[i].Amount, hits[i].Result), viewNow, _world.RenderDelay);
    }

    /// <summary><c>level_up</c> arrived.</summary>
    public void OnLevelUp(LevelUpMessage level) => Director.PlayEvent(level.BaseLevel is not null ? EventNames.LevelUp : EventNames.JobLevelUp);

    /// <summary>A map was loaded (login, warp): its music and ambience crossfade in.</summary>
    public void OnMapLoaded(MapData map)
    {
        Scheduler.Clear();
        _seen.Clear();
        Director.SetMap(map.MusicKey, map.AmbienceKey);
    }

    /// <summary>An interface control was pressed.</summary>
    public void PlayUi(string category) => Director.PlayUi(category);

    /// <summary>The player used an item (T-401 calls this; no wire message says so yet).</summary>
    public void OnItemUsed(string? itemId, int priority = AudioPriority.High) => Director.PlayCategory(_bundle.ItemSound(itemId, 0), AudioCategories.ItemUse, priority);

    /// <summary>The player equipped an item (T-401).</summary>
    public void OnItemEquipped(string? itemId, int priority = AudioPriority.High) => Director.PlayCategory(_bundle.ItemSound(itemId, 1), AudioCategories.ItemEquip, priority);

    /// <summary>The player picked something up (T-401: the wire has no message for it yet).</summary>
    public void OnItemAcquired() => Director.PlayEvent(EventNames.ItemAcquired);

    /// <summary>The app went to the background.</summary>
    public void OnBackgrounded() => Director.App.SetBackgrounded(true, Clocks.View());

    /// <summary>The app came back.</summary>
    public void OnForegrounded() => Director.App.SetBackgrounded(false, Clocks.View());

    /// <summary>Saves the mixer into the preferences (the HUD calls this after a change).</summary>
    public void SavePreferences()
    {
        Preferences.TakeFrom(Director.Mixer);
        UserPrefsStore.Save(Preferences);
    }

    /// <summary>Once a frame, after the session has applied what arrived.</summary>
    public void Update(double viewNow)
    {
        var realNow = Clocks.View();
        _platform.Poll(realNow);
        Director.Tick(realNow);
        if (ReferenceEquals(_impacts, Scheduler)) Scheduler.Advance(viewNow);

        var own = _world.Own;
        var swings = _impacts.Swings;
        for (var i = 0; i < swings.Count; i++) Swing(swings[i], own);
        var impacts = _impacts.Impacts;
        for (var i = 0; i < impacts.Count; i++) Impact(impacts[i], own);
        WatchEntities(own);
    }

    private void Swing(in ImpactEvent hit, EntityView? own)
    {
        if (_world.Find(hit.Attacker) is not { } attacker || attacker.Kind != EntityKinds.Player) return;
        var weapon = WeaponTypeOf(attacker);
        if (weapon is null) return;
        Director.PlayWeaponSwing(weapon, attacker.Id == _world.OwnId ? AudioPriority.High : AudioPriority.Low, Distance(own, attacker));
    }

    private void Impact(in ImpactEvent hit, EntityView? own)
    {
        var attacker = _world.Find(hit.Attacker);
        var target = _world.Find(hit.Target);
        if (attacker is null && target is null) return;
        var mine = hit.Attacker == _world.OwnId || hit.Target == _world.OwnId;
        var priority = mine ? AudioPriority.High : AudioPriority.Low;
        var at = target ?? attacker!;
        var distance = Distance(own, at);

        if (attacker is { Kind: EntityKinds.Monster } monster && _seen.TryGetValue(monster.Id, out var state) && !state.Aggro)
        {
            state.Aggro = true;
            Director.PlayCategory(_bundle.MonsterSound(monster.DisplayKey, 0), AudioCategories.MonsterAggro, priority, Distance(own, monster));
        }
        if (hit.Result == HitResults.Miss) return; // the swing was the sound; nothing landed
        var weapon = (attacker is null ? null : WeaponTypeOf(attacker)) ?? WeaponTypes.Unarmed;
        Director.PlayWeaponImpact(weapon, priority, distance);
        if (target is { Kind: EntityKinds.Monster })
            Director.PlayCategory(_bundle.MonsterSound(target.DisplayKey, 1), AudioCategories.MonsterHit, priority, distance);
    }

    /// <summary>A monster that died since the last frame. Entities seen for the first time are only noted, so a snapshot of corpses is silent.</summary>
    private void WatchEntities(EntityView? own)
    {
        _stamp++;
        var entities = _world.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity.Kind != EntityKinds.Monster) continue;
            if (!_seen.TryGetValue(entity.Id, out var state))
            {
                _seen[entity.Id] = new Seen { Dead = entity.Dead, Stamp = _stamp };
                continue;
            }
            state.Stamp = _stamp;
            if (entity.Dead && !state.Dead)
                Director.PlayCategory(_bundle.MonsterSound(entity.DisplayKey, 2), AudioCategories.MonsterDie, AudioPriority.Normal, Distance(own, entity));
            state.Dead = entity.Dead;
        }
        if (_stamp % 300 != 0) return;
        _stale.Clear();
        foreach (var pair in _seen)
            if (pair.Value.Stamp != _stamp) _stale.Add(pair.Key);
        for (var i = 0; i < _stale.Count; i++) _seen.Remove(_stale[i]);
    }

    private static double Distance(EntityView? from, EntityView to)
    {
        if (from is null) return AudioDirector.Global;
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        return dx * dx + dz * dz;
    }

    private static string? DefaultWeaponType(EntityView entity) => entity.Kind == EntityKinds.Player ? WeaponTypes.Unarmed : null;

    private static void Print(string line) => GD.Print(line);
}
