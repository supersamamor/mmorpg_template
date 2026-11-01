using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Sounds (ARCHITECTURE §7.8). Content: every sound key has the right prefix (existence is the asset-key rule's job).
/// Theme manifest, when the validator was given one: each sound entry is well formed (files are <c>.ogg</c>, volume and pitch
/// in range, bus known), and <c>audio.defaults</c> / <c>audio.events</c> name known categories and events and existing
/// <c>sfx.</c> keys. A missing optional content key is not an error.
/// </summary>
public sealed class AudioRule : IValidationRule
{
    /// <summary>Lowest accepted <c>volume_db</c>.</summary>
    public const double MinVolumeDb = -60;

    /// <summary>Highest accepted <c>volume_db</c>.</summary>
    public const double MaxVolumeDb = 12;

    /// <inheritdoc />
    public string Name => "audio";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        CheckContentKeys(context);
        if (context.Audio is { } audio) CheckManifest(context, audio);
    }

    private static void CheckContentKeys(ValidationContext c)
    {
        var db = c.Database;
        foreach (var s in db.Skills.All) Prefix(c, s.Id, "sfx_asset_key", s.SfxAssetKey, AssetKeyPrefixes.Sfx);
        foreach (var m in db.Monsters.All)
        {
            Prefix(c, m.Id, "sounds.aggro", m.Sounds?.Aggro, AssetKeyPrefixes.Sfx);
            Prefix(c, m.Id, "sounds.hit", m.Sounds?.Hit, AssetKeyPrefixes.Sfx);
            Prefix(c, m.Id, "sounds.die", m.Sounds?.Die, AssetKeyPrefixes.Sfx);
        }
        foreach (var i in db.Items.All)
        {
            Prefix(c, i.Id, "use_sfx", i.UseSfx, AssetKeyPrefixes.Sfx);
            Prefix(c, i.Id, "equip_sfx", i.EquipSfx, AssetKeyPrefixes.Sfx);
        }
        foreach (var m in db.Maps.All)
        {
            Prefix(c, m.Id, "music_asset_key", m.MusicAssetKey, AssetKeyPrefixes.Music);
            Prefix(c, m.Id, "ambience_asset_key", m.AmbienceAssetKey, AssetKeyPrefixes.Ambience);
        }
    }

    private static void Prefix(ValidationContext c, string owner, string path, string? key, string prefix)
    {
        if (string.IsNullOrEmpty(key) || key.StartsWith(prefix, StringComparison.Ordinal)) return;
        c.Error(owner, path, ValidationCodes.AudioPrefix, $"Sound key '{key}' must start with '{prefix}'");
    }

    private static void CheckManifest(ValidationContext c, AudioManifest audio)
    {
        foreach (var p in audio.Problems)
            c.ErrorInManifest(audio, p.Token, p.Path, ValidationCodes.AudioShape, p.Message);

        foreach (var entry in audio.Sounds) CheckEntry(c, audio, entry);

        foreach (var (category, key) in audio.Defaults)
        {
            var path = "audio.defaults." + category;
            if (!AudioCategories.All.Contains(category))
                c.ErrorInManifest(audio, category, path, ValidationCodes.VocabUnknown,
                    $"Unknown audio category '{category}'. Known: {string.Join(", ", AudioCategories.All)}");
            CheckTarget(c, audio, category, path, key);
        }

        foreach (var (name, key) in audio.Events)
        {
            var path = "audio.events." + name;
            if (!EventNames.All.Contains(name))
                c.ErrorInManifest(audio, name, path, ValidationCodes.VocabUnknown,
                    $"Unknown event '{name}'. Known: {string.Join(", ", EventNames.All)}");
            CheckTarget(c, audio, name, path, key);
        }
    }

    private static void CheckTarget(ValidationContext c, AudioManifest audio, string token, string path, string key)
    {
        if (!c.AssetKeys.Contains(key))
            c.ErrorInManifest(audio, token, path, ValidationCodes.AssetMissing, $"Sound key '{key}' is not in the theme's asset-manifest.json");
        else if (!key.StartsWith(AssetKeyPrefixes.Sfx, StringComparison.Ordinal))
            c.ErrorInManifest(audio, token, path, ValidationCodes.AudioPrefix, $"Sound key '{key}' must start with '{AssetKeyPrefixes.Sfx}'");
    }

    private static void CheckEntry(ValidationContext c, AudioManifest audio, SoundEntry e)
    {
        void Error(string field, string code, string message) =>
            c.ErrorInManifest(audio, e.Key, e.Key + "." + field, code, message);

        if (e.Files.Count == 0)
            Error(e.FilesListed ? "files" : "file", ValidationCodes.AudioFile,
                e.FilesListed ? "'files' must list at least one file" : "a sound needs 'file' or 'files'");
        for (var i = 0; i < e.Files.Count; i++)
            if (!e.Files[i].EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                Error(e.FilesListed ? $"files[{i}]" : "file", ValidationCodes.AudioFile, $"'{e.Files[i]}' is not an .ogg file (audio is OGG Vorbis)");

        if (e.VolumeDb is { } db && (db < MinVolumeDb || db > MaxVolumeDb))
            Error("volume_db", ValidationCodes.AudioRange, $"volume_db {db} is outside {MinVolumeDb} to {MaxVolumeDb}");

        if (e.PitchMin is { } lo && e.PitchMax is { } hi)
        {
            if (lo <= 0 || hi <= 0) Error("pitch", ValidationCodes.AudioRange, $"pitch [{lo}, {hi}] must be above 0");
            else if (lo > hi) Error("pitch", ValidationCodes.AudioRange, $"pitch min {lo} is above max {hi}");
        }

        if (e.Bus is { } bus && !AudioBuses.All.Contains(bus))
            Error("bus", ValidationCodes.VocabUnknown, $"Unknown audio bus '{bus}'. Known: {string.Join(", ", AudioBuses.All)}");
    }
}
