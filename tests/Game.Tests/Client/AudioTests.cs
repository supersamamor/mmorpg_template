using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Client.Core.Bundle;
using Game.Client.Core.Ui;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>The client audio rules (T-305, ARCHITECTURE §7.8, §9): fallback, variation, voices, buses, crossfade, pause. All pure; no engine and no sound.</summary>
public class AudioTests
{
    private sealed class FakeSink : IAudioSink
    {
        public readonly List<string> Log = new();
        public readonly Dictionary<string, double> BusDb = new();
        public readonly Dictionary<(int, int), double> Gain = new();
        public readonly List<(int Voice, string Key, int File, double Db, double Pitch)> Voices = new();
        public double Length = 1;
        public bool Paused;

        public double DurationOf(ResolvedSound sound, int fileIndex) => Length;

        public void StartVoice(int voice, ResolvedSound sound, int fileIndex, double volumeDb, double pitch) => Voices.Add((voice, sound.Key, fileIndex, volumeDb, pitch));

        public void StopVoice(int voice) => Log.Add("stop-voice " + voice);

        public void StartStream(int channel, int slot, ResolvedSound sound, int fileIndex) => Log.Add($"start {channel}/{slot} {sound.Key}");

        public void StopStream(int channel, int slot) => Log.Add($"stop {channel}/{slot}");

        public void SetStreamGain(int channel, int slot, double gain) => Gain[(channel, slot)] = gain;

        public void SetBusDb(string bus, double db) => BusDb[bus] = db;

        public void SetPaused(bool paused) => Paused = paused;
    }

    private sealed class Rig
    {
        public readonly SoundTable Table = new("test");
        public readonly FakeSink Sink = new();
        public readonly List<string> Warnings = new();
        public readonly List<string> Trace = new();
        public readonly ClientConfig Config = new();
        public AudioDirector Director = null!;
        public SoundCatalog Catalog = null!;
        public double Now;

        public Rig Build(ulong seed = 1, Func<string, bool>? exists = null)
        {
            Catalog = new SoundCatalog(new[] { Table }, exists ?? (_ => true), Warnings.Add);
            Director = new AudioDirector(Catalog, new Pcg32Random(seed), Sink, Config, Trace.Add);
            return this;
        }

        public void At(double seconds)
        {
            Now = seconds;
            Director.Tick(seconds);
        }
    }

    private static Rig Standard()
    {
        var rig = new Rig();
        rig.Table.Add("sfx.hit.slime", new[] { "audio/slime.ogg" }, volumeDb: -3);
        rig.Table.Add("sfx.monster.hit.generic", new[] { "audio/g1.ogg", "audio/g2.ogg", "audio/g3.ogg" }, pitchMin: 0.9, pitchMax: 1.1);
        rig.Table.Add("sfx.weapon.impact.slash", new[] { "audio/slash.ogg" });
        rig.Table.Add("sfx.ui.tap", new[] { "audio/tap.ogg" });
        rig.Table.Add("sfx.event.level_up", new[] { "audio/lvl.ogg" });
        rig.Table.Add("music.map.a", new[] { "audio/a.ogg" }, loop: true);
        rig.Table.Add("music.map.b", new[] { "audio/b.ogg" }, loop: true);
        rig.Table.Add("ambience.map.a", new[] { "audio/amb.ogg" }, loop: true);
        rig.Table.SetDefault(AudioCategories.MonsterHit, "sfx.monster.hit.generic");
        rig.Table.SetDefault(AudioCategories.MonsterDie, "sfx.missing.everywhere");
        rig.Table.SetDefault(AudioCategories.WeaponImpact(WeaponTypes.Sword1h), "sfx.weapon.impact.slash");
        rig.Table.SetDefault(AudioCategories.UiTap, "sfx.ui.tap");
        rig.Table.SetEvent(EventNames.LevelUp, "sfx.event.level_up");
        rig.Table.SetEvent(EventNames.QuestCompleted, "sfx.event.nobody_has_this");
        return rig;
    }

    // ---- the manifest ----

    [Fact]
    public void A_manifest_gives_entries_with_defaults_and_the_audio_tables()
    {
        const string json = """
            { "model.x": { "file": "m.glb" },
              "sfx.a": { "files": ["audio/a1.ogg", "audio/a2.ogg"], "volume_db": -4.5, "pitch": [0.9, 1.1], "bus": "ui", "loop": false },
              "sfx.ui.b": { "file": "audio/b.ogg" },
              "music.m": { "file": "audio/m.ogg", "loop": true },
              "ambience.w": { "file": "audio/w.ogg", "loop": true },
              "sfx.broken": { "file": 3 },
              "audio": { "defaults": { "ui.tap": "sfx.ui.b" }, "events": { "LevelUp": "sfx.a" } } }
            """;
        SoundTable.TryParse("t", json, out var table, out _).ShouldBeTrue();
        table!.Count.ShouldBe(4);
        table.TryGet("sfx.a", out var a).ShouldBeTrue();
        a.Files.ShouldBe(new[] { "audio/a1.ogg", "audio/a2.ogg" });
        a.VolumeDb.ShouldBe(-4.5);
        (a.PitchMin, a.PitchMax).ShouldBe((0.9, 1.1));
        a.Bus.ShouldBe("ui");
        table.TryGet("sfx.ui.b", out var b).ShouldBeTrue();
        b.Bus.ShouldBe(AudioBuses.Sfx); // a key names the ui bus itself; the prefix never does
        table.TryGet("music.m", out var m).ShouldBeTrue();
        (m.Bus, m.Loop).ShouldBe((AudioBuses.Music, true));
        table.TryGet("ambience.w", out var w).ShouldBeTrue();
        w.Bus.ShouldBe(AudioBuses.Ambient);
        table.Defaults["ui.tap"].ShouldBe("sfx.ui.b");
        table.Events["LevelUp"].ShouldBe("sfx.a");
    }

    [Fact]
    public void The_default_theme_sounds_all_resolve_through_the_catalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
        var root = directory!.FullName;
        SoundTable.TryParse("default", File.ReadAllText(Path.Combine(root, "themes", "default", "asset-manifest.json")), out var table, out _).ShouldBeTrue();
        var warnings = new List<string>();
        var catalog = new SoundCatalog(new[] { table! }, path => File.Exists(Path.Combine(root, path)), warnings.Add);
        table!.Defaults.Count.ShouldBeGreaterThan(20);
        foreach (var key in table.Defaults.Values.Concat(table.Events.Values)) catalog.TryResolve(key, out _).ShouldBeTrue(key);
        warnings.ShouldBeEmpty();
    }

    // ---- the fallback chain ----

    [Fact]
    public void A_content_key_wins_over_the_theme_default()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.PlayCategory("sfx.hit.slime", AudioCategories.MonsterHit, AudioPriority.Normal);
        rig.Sink.Voices.Single().Key.ShouldBe("sfx.hit.slime");
        rig.Sink.Voices.Single().Db.ShouldBe(-3);
    }

    [Fact]
    public void No_content_key_falls_to_the_theme_default()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Normal);
        rig.Sink.Voices.Single().Key.ShouldBe("sfx.monster.hit.generic");
    }

    [Fact]
    public void A_content_key_the_theme_lacks_falls_to_the_default_with_one_warning()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.PlayCategory("sfx.not.in.theme", AudioCategories.MonsterHit, AudioPriority.Normal);
        rig.At(1);
        rig.Director.PlayCategory("sfx.not.in.theme", AudioCategories.MonsterHit, AudioPriority.Normal);
        rig.Sink.Voices.Select(v => v.Key).ShouldBe(new[] { "sfx.monster.hit.generic", "sfx.monster.hit.generic" });
        rig.Warnings.Count(w => w.Contains("sfx.not.in.theme")).ShouldBe(1);
    }

    [Fact]
    public void Nothing_found_is_silence_with_one_warning_and_no_crash()
    {
        var rig = Standard().Build();
        rig.At(0);
        for (var i = 0; i < 3; i++)
        {
            rig.At(i);
            rig.Director.PlayCategory(null, AudioCategories.MonsterDie, AudioPriority.Normal); // default names a key nobody has
            rig.Director.PlayCategory(null, AudioCategories.ItemUse, AudioPriority.Normal); // no default at all
            rig.Director.PlayEvent(EventNames.QuestCompleted); // event names a key nobody has
            rig.Director.PlayEvent(EventNames.JobChanged); // no event entry
        }
        rig.Sink.Voices.ShouldBeEmpty();
        rig.Director.Stats.Silent.ShouldBe(12);
        rig.Warnings.Count(w => w.Contains("sfx.missing.everywhere")).ShouldBe(1);
        rig.Warnings.Count(w => w.Contains("sfx.event.nobody_has_this")).ShouldBe(1);
    }

    [Fact]
    public void A_key_whose_files_are_missing_on_disk_is_silence_too()
    {
        var rig = Standard().Build(exists: path => !path.EndsWith("slime.ogg"));
        rig.At(0);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal);
        rig.Sink.Voices.ShouldBeEmpty();
        rig.Warnings.Single().ShouldContain("missing on disk");
    }

    [Fact]
    public void A_theme_chain_finds_a_key_in_the_second_theme()
    {
        var active = new SoundTable("mine");
        active.Add("sfx.only.mine", new[] { "a.ogg" });
        var fallback = new SoundTable("default");
        fallback.Add("sfx.only.default", new[] { "b.ogg" });
        fallback.Add("sfx.only.mine", new[] { "other.ogg" });
        fallback.SetEvent(EventNames.LevelUp, "sfx.only.default");
        var catalog = new SoundCatalog(new[] { active, fallback }, _ => true, _ => { });
        catalog.TryResolve("sfx.only.mine", out var mine).ShouldBeTrue();
        mine.Paths.ShouldBe(new[] { "themes/mine/a.ogg" });
        catalog.TryResolve("sfx.only.default", out var other).ShouldBeTrue();
        other.Paths.ShouldBe(new[] { "themes/default/b.ogg" });
        catalog.Events[EventNames.LevelUp].ShouldBe("sfx.only.default");
    }

    [Fact]
    public void Weapon_events_and_the_interface_use_the_theme_tables()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.PlayWeaponImpact(WeaponTypes.Sword1h, AudioPriority.Normal, 4);
        rig.Director.PlayEvent(EventNames.LevelUp);
        rig.Director.PlayUi(AudioCategories.UiTap);
        rig.Sink.Voices.Select(v => v.Key).ShouldBe(new[] { "sfx.weapon.impact.slash", "sfx.event.level_up", "sfx.ui.tap" });
    }

    // ---- variation, pitch ----

    [Fact]
    public void Variation_and_pitch_come_from_the_injected_random_source()
    {
        List<(int File, double Pitch)> Run(ulong seed)
        {
            var rig = Standard().Build(seed);
            rig.Config.AudioCooldownSeconds = 0;
            var played = new List<(int, double)>();
            for (var i = 0; i < 40; i++)
            {
                rig.At(i * 10);
                rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Normal);
                var v = rig.Sink.Voices[^1];
                played.Add((v.File, v.Pitch));
            }
            return played;
        }

        var first = Run(7);
        Run(7).ShouldBe(first);
        Run(8).ShouldNotBe(first);
        first.Select(p => p.File).Distinct().Order().ShouldBe(new[] { 0, 1, 2 });
        first.ShouldAllBe(p => p.Pitch >= 0.9 && p.Pitch <= 1.1);
        first.Select(p => p.Pitch).Distinct().Count().ShouldBeGreaterThan(10);
    }

    [Fact]
    public void A_sound_without_a_pitch_range_plays_at_normal_pitch()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal);
        rig.Sink.Voices.Single().Pitch.ShouldBe(1);
    }

    // ---- the voice pool ----

    [Fact]
    public void Thirty_hits_in_one_tick_play_once()
    {
        var rig = Standard().Build();
        rig.At(0);
        for (var i = 0; i < 30; i++) rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Low);
        rig.Sink.Voices.Count.ShouldBe(1);
        rig.Director.Stats.Cooldown.ShouldBe(29);
        rig.Trace.Count(l => l.Contains("reason=cooldown")).ShouldBe(29);
        rig.At(rig.Config.AudioCooldownSeconds + 0.01);
        rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Low);
        rig.Sink.Voices.Count.ShouldBe(2);
    }

    [Fact]
    public void The_pool_never_has_more_voices_than_its_cap()
    {
        var pool = new VoicePool(24, 0);
        for (var i = 0; i < 100; i++) pool.Request("k" + i, AudioPriority.Normal, 100, i * 0.001, out var voice, out _);
        pool.Active(1).ShouldBe(24);
        pool.Capacity.ShouldBe(24);
        new ClientConfig().AudioMaxVoices.ShouldBe(24);
    }

    [Fact]
    public void A_full_pool_steals_the_lowest_priority_then_the_oldest()
    {
        var pool = new VoicePool(3, 0);
        pool.Request("old_normal", AudioPriority.Normal, 100, 0, out var a, out _).ShouldBe(VoiceOutcome.Played);
        pool.Request("low", AudioPriority.Low, 100, 1, out var b, out _).ShouldBe(VoiceOutcome.Played);
        pool.Request("new_normal", AudioPriority.Normal, 100, 2, out var c, out _).ShouldBe(VoiceOutcome.Played);
        new[] { a, b, c }.ShouldBe(new[] { 0, 1, 2 });

        pool.Request("high", AudioPriority.High, 100, 3, out var voice, out var victim).ShouldBe(VoiceOutcome.Stole);
        (voice, victim).ShouldBe((1, "low")); // the lowest priority goes first, although it is not the oldest

        pool.Request("another_high", AudioPriority.High, 100, 4, out voice, out victim).ShouldBe(VoiceOutcome.Stole);
        (voice, victim).ShouldBe((0, "old_normal")); // then the oldest of the lowest left
    }

    [Fact]
    public void A_sound_is_never_cut_for_a_less_important_one()
    {
        var pool = new VoicePool(2, 0);
        pool.Request("a", AudioPriority.High, 100, 0, out _, out _);
        pool.Request("b", AudioPriority.High, 100, 1, out _, out _);
        pool.Request("c", AudioPriority.Low, 100, 2, out var voice, out _).ShouldBe(VoiceOutcome.SkippedFull);
        voice.ShouldBe(-1);
        pool.Request("d", AudioPriority.High, 100, 3, out voice, out var victim).ShouldBe(VoiceOutcome.Stole); // equal importance: the older one gives way
        (voice, victim).ShouldBe((0, "a"));
    }

    [Fact]
    public void A_voice_whose_sound_ended_is_free_again()
    {
        var pool = new VoicePool(1, 0);
        pool.Request("a", AudioPriority.High, 0.5, 0, out _, out _);
        pool.Request("b", AudioPriority.Low, 1, 0.4, out _, out _).ShouldBe(VoiceOutcome.SkippedFull);
        pool.Request("b", AudioPriority.Low, 1, 0.6, out var voice, out _).ShouldBe(VoiceOutcome.Played);
        voice.ShouldBe(0);
    }

    [Fact]
    public void The_director_reports_steals_in_the_trace()
    {
        var rig = Standard();
        rig.Config.AudioMaxVoices = 2;
        rig.Config.AudioCooldownSeconds = 0;
        rig.Sink.Length = 10;
        rig.Build();
        rig.At(0);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Low);
        rig.At(1);
        rig.Director.PlayKey("sfx.weapon.impact.slash", AudioPriority.Low);
        rig.At(2);
        rig.Director.PlayKey("sfx.ui.tap", AudioPriority.Critical);
        rig.Director.Stats.Stolen.ShouldBe(1);
        rig.Trace.ShouldContain(l => l.StartsWith("[audio] steal voice=0 victim=sfx.hit.slime for=sfx.ui.tap"));
    }

    // ---- distance ----

    [Fact]
    public void Sounds_far_outside_the_camera_are_skipped_and_global_ones_never_are()
    {
        var rig = Standard().Build();
        rig.At(0);
        var limit = rig.Config.AudioCullDistance;
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal, (limit + 1) * (limit + 1));
        rig.Sink.Voices.ShouldBeEmpty();
        rig.Director.Stats.Distance.ShouldBe(1);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal, (limit - 1) * (limit - 1));
        rig.Director.PlayKey("sfx.ui.tap", AudioPriority.Critical, AudioDirector.Global);
        rig.Sink.Voices.Count.ShouldBe(2);
    }

    // ---- buses, volumes, mute ----

    [Fact]
    public void Bus_volumes_become_engine_decibels_and_mute_silences_everything()
    {
        var rig = Standard().Build();
        rig.Director.Mixer.SetVolume(AudioBuses.Sfx, 0.5);
        rig.Director.Mixer.SetVolume(AudioBuses.Music, 0);
        rig.At(0);
        rig.Sink.BusDb[AudioBuses.Sfx].ShouldBe(-6.02, 0.01);
        rig.Sink.BusDb[AudioBuses.Music].ShouldBe(BusMixer.SilenceDb);
        rig.Sink.BusDb[AudioBuses.Ui].ShouldBe(0);

        rig.Director.Mixer.Muted = true;
        rig.At(1);
        BusMixer.Buses.ShouldAllBe(bus => rig.Sink.BusDb[bus] == BusMixer.SilenceDb);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal);
        rig.Sink.Voices.ShouldBeEmpty();
        rig.Director.Stats.Muted.ShouldBe(1);

        rig.Director.Mixer.Muted = false;
        rig.At(2);
        rig.Sink.BusDb[AudioBuses.Ui].ShouldBe(0);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal);
        rig.Sink.Voices.Count.ShouldBe(1);
    }

    [Fact]
    public void Volumes_are_clamped_and_ignore_nonsense()
    {
        var mixer = new BusMixer();
        mixer.SetVolume("sfx", 7);
        mixer.Volume("sfx").ShouldBe(1);
        mixer.SetVolume("sfx", -1);
        mixer.Volume("sfx").ShouldBe(0);
        mixer.SetVolume("sfx", 0.3);
        mixer.SetVolume("sfx", double.NaN);
        mixer.Volume("sfx").ShouldBe(0.3);
        mixer.SetVolume("not_a_bus", 0.1);
        BusMixer.IndexOf("not_a_bus").ShouldBe(-1);
    }

    [Fact]
    public void Player_volumes_are_stored_with_the_preferences_and_survive_damage()
    {
        var prefs = new UserPreferences { UiScale = 1.25, Muted = true };
        prefs.Volumes[BusMixer.IndexOf(AudioBuses.Sfx)] = 0.25;
        var back = UserPreferences.Parse(prefs.ToJson());
        back.Muted.ShouldBeTrue();
        back.UiScale.ShouldBe(1.25);
        back.Volumes[BusMixer.IndexOf(AudioBuses.Sfx)].ShouldBe(0.25);
        var mixer = new BusMixer();
        back.ApplyTo(mixer);
        (mixer.Muted, mixer.Volume(AudioBuses.Sfx)).ShouldBe((true, 0.25));

        UserPreferences.Parse("{\"volume_sfx\": 9, \"volume_music\": \"loud\", \"sound_muted\": 3}"u8).Volumes[BusMixer.IndexOf(AudioBuses.Sfx)].ShouldBe(1);
        UserPreferences.Parse("not json"u8).Muted.ShouldBeFalse();
        UserPreferences.Parse("{}"u8).Volumes[BusMixer.IndexOf(AudioBuses.Music)].ShouldBe(new BusMixer().Volume(AudioBuses.Music));
    }

    // ---- crossfade ----

    [Fact]
    public void Music_crossfades_over_the_configured_time_with_equal_power()
    {
        var rig = Standard();
        rig.Config.AudioCrossfadeSeconds = 2;
        rig.Build();
        rig.At(0);
        rig.Director.SetMap("music.map.a", "ambience.map.a");
        rig.Director.Playing(AudioDirector.MusicChannel).ShouldBe("music.map.a");
        rig.Sink.Log.ShouldContain("start 0/1 music.map.a");
        rig.Sink.Gain[(0, 1)].ShouldBe(0);

        rig.At(2);
        rig.Sink.Gain[(0, 1)].ShouldBe(1, 0.001);

        rig.At(10);
        rig.Director.SetMap("music.map.b", "ambience.map.a");
        rig.Director.Playing(AudioDirector.MusicChannel).ShouldBe("music.map.b");
        rig.Director.FadingOut(AudioDirector.MusicChannel).ShouldBe("music.map.a");
        rig.Director.Playing(AudioDirector.AmbientChannel).ShouldBe("ambience.map.a"); // unchanged: no fade
        rig.At(11);
        rig.Sink.Gain[(0, 0)].ShouldBe(Math.Sin(Math.PI / 4), 0.001);
        rig.Sink.Gain[(0, 1)].ShouldBe(Math.Cos(Math.PI / 4), 0.001);
        (Math.Pow(rig.Sink.Gain[(0, 0)], 2) + Math.Pow(rig.Sink.Gain[(0, 1)], 2)).ShouldBe(1, 0.001);

        rig.At(12);
        rig.Sink.Gain[(0, 0)].ShouldBe(1, 0.001);
        rig.Director.FadingOut(AudioDirector.MusicChannel).ShouldBeNull();
        rig.Sink.Log.Last(l => l.StartsWith("stop 0/")).ShouldBe("stop 0/1");
        rig.Trace.ShouldContain(l => l.Contains("crossfade channel=music from=music.map.a to=music.map.b seconds=2.0"));
        rig.Trace.ShouldContain("[audio] crossfade done channel=music");
    }

    [Fact]
    public void A_warp_to_a_map_without_music_fades_the_old_music_out()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.SetMap("music.map.a", null);
        rig.At(5);
        rig.Director.SetMap(null, null);
        rig.Director.Playing(AudioDirector.MusicChannel).ShouldBeNull();
        rig.At(6);
        rig.Sink.Gain[(0, 1)].ShouldBe(Math.Cos(Math.PI / 4), 0.001);
        rig.At(7);
        rig.Director.FadingOut(AudioDirector.MusicChannel).ShouldBeNull();
    }

    [Fact]
    public void A_third_change_in_the_middle_of_a_fade_cuts_the_oldest_at_once()
    {
        var channel = new CrossfadeChannel(2);
        channel.Set("a", 0);
        channel.Set("b", 5);
        channel.Cut.ShouldBeFalse();
        channel.Set("c", 6);
        channel.Cut.ShouldBeTrue();
        channel.Outgoing.ShouldBe("b");
        channel.Set("c", 6.5).ShouldBeFalse();
    }

    [Fact]
    public void A_music_key_the_theme_lacks_is_silence_not_a_crash()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.SetMap("music.nobody.has.this", null);
        rig.Director.Playing(AudioDirector.MusicChannel).ShouldBeNull();
        rig.Warnings.Count(w => w.Contains("music.nobody.has.this")).ShouldBe(1);
    }

    // ---- the phone ----

    [Fact]
    public void Audio_pauses_in_the_background_and_resumes_with_the_clock_standing_still()
    {
        var state = new AppAudioState();
        state.Clock(10).ShouldBe(10);
        state.SetBackgrounded(true, 10).ShouldBeTrue();
        state.Paused.ShouldBeTrue();
        state.Clock(25).ShouldBe(10); // frozen
        state.SetBackgrounded(true, 26).ShouldBeFalse();
        state.SetBackgrounded(false, 40).ShouldBeTrue();
        state.Clock(40).ShouldBe(10);
        state.Clock(41).ShouldBe(11);
    }

    [Fact]
    public void The_director_pauses_the_engine_and_plays_nothing_while_in_the_background()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal);
        rig.Director.App.SetBackgrounded(true, 0.5);
        rig.At(0.5);
        rig.Sink.Paused.ShouldBeTrue();
        rig.Director.PlayKey("sfx.ui.tap", AudioPriority.Critical);
        rig.Sink.Voices.Count.ShouldBe(1);
        rig.Director.Stats.Paused.ShouldBe(1);

        rig.Director.App.SetBackgrounded(false, 60);
        rig.At(60);
        rig.Sink.Paused.ShouldBeFalse();
        rig.Trace.ShouldContain("[audio] pause (background or focus lost)");
        rig.Trace.ShouldContain("[audio] resume");
        rig.Director.PlayKey("sfx.ui.tap", AudioPriority.Critical);
        rig.Sink.Voices.Count.ShouldBe(2);
        rig.Director.Pool.Active(rig.Director.App.Clock(60)).ShouldBe(2); // the clock stood still, so the first sound is still counted
    }

    [Fact]
    public void A_fade_waits_while_paused()
    {
        var rig = Standard().Build();
        rig.At(0);
        rig.Director.SetMap("music.map.a", null);
        rig.At(0.5);
        rig.Director.App.SetBackgrounded(true, 0.5);
        rig.At(100);
        rig.Sink.Gain[(0, 1)].ShouldBe(Math.Sin(Math.PI / 8), 0.001); // 0.5 s of 2 s, however long the pause
        rig.Director.App.SetBackgrounded(false, 100);
        rig.At(101);
        rig.Sink.Gain[(0, 1)].ShouldBe(Math.Sin(Math.PI / 2 * 1.5 / 2), 0.001);
    }

    [Fact]
    public void Another_app_taking_audio_focus_pauses_like_the_background()
    {
        var state = new AppAudioState();
        state.SetFocusLost(true, 5).ShouldBeTrue();
        state.Paused.ShouldBeTrue();
        state.SetBackgrounded(true, 6).ShouldBeFalse(); // already paused
        state.SetFocusLost(false, 7).ShouldBeFalse(); // still in the background
        state.SetBackgrounded(false, 9).ShouldBeTrue();
        state.Clock(9).ShouldBe(5);
    }

    [Fact]
    public void The_silent_switch_silences_effects_and_ambience_but_not_music()
    {
        var rig = Standard().Build();
        rig.Director.App.SetSilentSwitch(true);
        rig.At(0);
        rig.Sink.BusDb[AudioBuses.Music].ShouldBeGreaterThan(BusMixer.SilenceDb);
        new[] { AudioBuses.Sfx, AudioBuses.Ui, AudioBuses.Ambient }.ShouldAllBe(bus => rig.Sink.BusDb[bus] == BusMixer.SilenceDb);
        rig.Director.PlayKey("sfx.hit.slime", AudioPriority.Normal);
        rig.Sink.Voices.ShouldBeEmpty();
        rig.Trace.ShouldContain(l => l.Contains("reason=silent_switch"));
        rig.Director.SetMap("music.map.a", null);
        rig.Sink.Log.ShouldContain("start 0/1 music.map.a");
    }

    // ---- memory ----

    [Fact]
    public void The_effects_budget_drops_the_least_recently_used_over_the_limit()
    {
        var budget = new SoundBudget(100);
        var evicted = new List<string>();
        budget.Add("a", 40, evicted);
        budget.Add("b", 40, evicted);
        evicted.ShouldBeEmpty();
        budget.Touch("a");
        budget.Add("c", 40, evicted);
        evicted.ShouldBe(new[] { "b" });
        budget.Used.ShouldBe(80);
        budget.Contains("b").ShouldBeFalse();
        budget.Peak.ShouldBe(120);
        budget.Evictions.ShouldBe(1);
        budget.Add("huge", 500, evicted);
        evicted.ShouldBe(new[] { "a", "c" });
        budget.Count.ShouldBe(1); // a file bigger than the limit is kept alone
    }

    [Fact]
    public void Decoded_size_counts_16_bit_samples_for_every_channel()
    {
        SoundBudget.DecodedBytes(1, 44100, 2).ShouldBe(176400);
        (new ClientConfig().AudioEffectsBudgetMb).ShouldBe(48);
    }

    // ---- hit timing ----

    [Fact]
    public void A_hit_is_due_at_the_render_delay_and_never_replays()
    {
        var scheduler = new ImpactScheduler();
        scheduler.Queue(new ImpactEvent(1, 2, 5, HitResults.Hit), now: 10, renderDelay: 0.2);
        scheduler.Advance(10.1);
        scheduler.Impacts.ShouldBeEmpty();
        scheduler.Pending.ShouldBe(1);
        scheduler.Advance(10.2);
        scheduler.Swings.Count.ShouldBe(1);
        scheduler.Impacts.Single().Target.ShouldBe(2);
        scheduler.Advance(10.3);
        scheduler.Impacts.ShouldBeEmpty();
        scheduler.Pending.ShouldBe(0);
    }

    [Fact]
    public void A_late_batch_delivers_each_hit_once_and_a_clear_forgets_the_waiting_ones()
    {
        var scheduler = new ImpactScheduler(4);
        for (var i = 0; i < 6; i++) scheduler.Queue(new ImpactEvent(1, i, 1, HitResults.Hit), now: i, renderDelay: 0.1);
        scheduler.Pending.ShouldBe(4); // the oldest two were dropped
        scheduler.Advance(100);
        scheduler.Impacts.Select(h => h.Target).ShouldBe(new long[] { 2, 3, 4, 5 });
        scheduler.Advance(101);
        scheduler.Impacts.ShouldBeEmpty();
        scheduler.Queue(new ImpactEvent(1, 9, 1, HitResults.Miss), 200, 0.1);
        scheduler.Clear();
        scheduler.Advance(300);
        scheduler.Impacts.ShouldBeEmpty();
    }

    // ---- the bundle ----

    [Fact]
    public void The_bundle_gives_maps_their_music_and_monsters_their_sounds()
    {
        var json = "{\"format_version\":" + Game.Shared.ClientContent.ClientBundle.CurrentFormatVersion + ",\"content_hash\":\"h\"," +
            "\"items\":[{\"id\":\"item.a\",\"name\":\"A\",\"use_sfx\":\"sfx.item.use.a\",\"equip_sfx\":\"sfx.item.equip.a\"}]," +
            "\"skills\":[{\"id\":\"skill.a\",\"name\":\"S\",\"sfx\":\"sfx.skill.a\"}],\"jobs\":[],\"statuses\":[]," +
            "\"monsters\":[{\"id\":\"monster.a\",\"name\":\"M\",\"sounds\":{\"hit\":\"sfx.m.hit\",\"die\":\"sfx.m.die\"}},{\"id\":\"monster.b\",\"name\":\"N\"}]," +
            "\"npcs\":[],\"quests\":[],\"maps\":[{\"id\":\"map.a\",\"name\":\"A\",\"scene\":\"scene.map.a\",\"music\":\"music.map.a\",\"ambience\":\"ambience.map.a\",\"width\":1,\"height\":1,\"cells\":\"AQ==\",\"warps\":[],\"zones\":[]}," +
            "{\"id\":\"map.b\",\"name\":\"B\",\"scene\":\"scene.map.b\",\"width\":1,\"height\":1,\"cells\":\"AQ==\",\"warps\":[],\"zones\":[]}]}";
        DisplayBundle.TryRead(System.Text.Encoding.UTF8.GetBytes(json), out var bundle, out _).ShouldBe(BundleStatus.Ok);
        bundle!.TryGetMap("map.a", out var a);
        (a!.MusicKey, a.AmbienceKey).ShouldBe(("music.map.a", "ambience.map.a"));
        bundle.TryGetMap("map.b", out var b);
        (b!.MusicKey, b.AmbienceKey).ShouldBe((null, null));
        bundle.MonsterSound("monster.a", 0).ShouldBeNull();
        bundle.MonsterSound("monster.a", 1).ShouldBe("sfx.m.hit");
        bundle.MonsterSound("monster.a", 2).ShouldBe("sfx.m.die");
        bundle.MonsterSound("monster.b", 2).ShouldBeNull();
        bundle.ItemSound("item.a", 0).ShouldBe("sfx.item.use.a");
        bundle.ItemSound("item.a", 1).ShouldBe("sfx.item.equip.a");
        bundle.SkillSound("skill.a").ShouldBe("sfx.skill.a");
        bundle.SoundKeys.ShouldBe(new[] { "ambience.map.a", "music.map.a", "sfx.item.equip.a", "sfx.item.use.a", "sfx.m.die", "sfx.m.hit", "sfx.skill.a" });
        bundle.MonsterSound("monster.none", 0).ShouldBeNull();
        bundle.ItemSound(null, 0).ShouldBeNull();
    }

    [Fact]
    public void The_shipped_settings_carry_the_audio_numbers()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
        var config = new ClientConfig();
        config.ApplyJson(File.ReadAllBytes(Path.Combine(directory!.FullName, "client", "client_settings.json")));
        (config.AudioMaxVoices, config.AudioEffectsBudgetMb, config.AudioTrace).ShouldBe((24, 48.0, false));
        config.ApplyArgs(new[] { "--set", "audio_trace=1", "--set", "audio_max_voices=999" });
        config.AudioTrace.ShouldBeTrue();
        config.AudioMaxVoices.ShouldBe(64);
    }

    [Fact]
    public void Nothing_allocates_when_a_sound_is_skipped_with_the_trace_off()
    {
        var rig = Standard();
        rig.Build();
        rig.Director.Trace = null;
        rig.At(0);
        rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Low);
        rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Low); // warm-up: the cooldown table has the key
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) rig.Director.PlayCategory(null, AudioCategories.MonsterHit, AudioPriority.Low);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }
}
