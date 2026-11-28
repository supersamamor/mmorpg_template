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
/// Turns what the view model shows into sound requests (ARCHITECTURE §7.8) and owns the engine side. It reads: the impacts of an <see cref="IImpactSource"/>
/// (<c>World.Combat.Impact</c>, shared with the combat visuals), attack clips starting, monsters that died, the map's music and ambience, level ups and interface taps. It decides nothing about the game:
/// every sound follows something the server already said. Allocates nothing per frame after warm-up when the trace is off.
/// </summary>
public sealed class AudioDriver
{
    private sealed class Seen
    {
        public bool Dead;
        public bool Aggro;
        public int Attacks;
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
        _impacts = new CombatImpactSource(world.Combat);
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

    /// <summary>Weapon type of an entity's attacks, or null for none. The default is what the server says a player holds (its view record's <c>weapon</c>, T-401).</summary>
    public Func<EntityView, string?> WeaponTypeOf { get; set; }

    /// <summary>Plugs in another timing source (the default is <see cref="CombatImpactSource"/> over <c>World.Combat</c>).</summary>
    public void UseImpactSource(IImpactSource source) => _impacts = source;

    /// <summary><c>level_up</c> arrived.</summary>
    public void OnLevelUp(LevelUpMessage level) => Director.PlayEvent(level.BaseLevel is not null ? EventNames.LevelUp : EventNames.JobLevelUp);

    /// <summary>A map was loaded (login, warp): its music and ambience crossfade in.</summary>
    public void OnMapLoaded(MapData map)
    {
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
        // The HUD keeps its own copy (the UI scale, the minimap switch, T-812): write the sound choices over what is stored, not this copy's older others.
        var stored = UserPrefsStore.Load();
        stored.TakeFrom(Director.Mixer);
        UserPrefsStore.Save(stored);
    }

    /// <summary>Once a frame, after the session has applied what arrived.</summary>
    public void Update(double viewNow)
    {
        var realNow = Clocks.View();
        _platform.Poll(realNow);
        Director.Tick(realNow);

        var own = _world.Own;
        var impacts = _impacts.Impacts;
        for (var i = 0; i < impacts.Count; i++) Impact(impacts[i], own);
        _impacts.Clear();
        WatchEntities(own);
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

    /// <summary>
    /// Per entity, once a frame: a character whose attack clip started (its <c>AttackCount</c> went up, which the combat timeline does at swing time) plays its weapon's swing sound,
    /// and a monster that died plays its die sound. Entities seen for the first time are only noted, so a snapshot of corpses or of attackers mid-swing is silent.
    /// </summary>
    private void WatchEntities(EntityView? own)
    {
        _stamp++;
        var entities = _world.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            var monster = entity.Kind == EntityKinds.Monster;
            if (!monster && entity.Kind != EntityKinds.Player) continue;
            if (!_seen.TryGetValue(entity.Id, out var state))
            {
                _seen[entity.Id] = new Seen { Dead = entity.Dead, Attacks = entity.AttackCount, Stamp = _stamp };
                continue;
            }
            state.Stamp = _stamp;
            if (entity.AttackCount > state.Attacks && !monster && WeaponTypeOf(entity) is { } weapon)
                Director.PlayWeaponSwing(weapon, entity.Id == _world.OwnId ? AudioPriority.High : AudioPriority.Low, Distance(own, entity));
            state.Attacks = entity.AttackCount;
            if (monster && entity.Dead && !state.Dead)
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

    private static string? DefaultWeaponType(EntityView entity) => entity.Kind == EntityKinds.Player ? entity.Weapon ?? WeaponTypes.Unarmed : null;

    /// <summary>
    /// An own inventory change (T-401): a pick-up plays the item acquired event, a use the item's use sound, a put-on its equip sound. Taking off and
    /// dropping are silent.
    /// </summary>
    public void OnInventoryChanged(InventoryChangedMessage change)
    {
        switch (change.Cause)
        {
            case InventoryChanges.PickedUp: OnItemAcquired(); break;
            case InventoryChanges.Used: OnItemUsed(change.Item); break;
            case InventoryChanges.Equipped: OnItemEquipped(change.Item); break;
        }
    }

    private static void Print(string line) => GD.Print(line);
}
