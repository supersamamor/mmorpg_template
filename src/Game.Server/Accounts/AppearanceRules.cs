using System.Collections.Concurrent;
using Game.Server.World;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Accounts;

/// <summary>
/// What <c>appearance.yaml</c> allows a character to look like (T-705, ARCHITECTURE §7.5.1): checks a creation request, and turns what is stored into a look that the
/// current content has. Thread-safe; knows no database and no sockets.
/// </summary>
/// <remarks>
/// <para><b>The default gender</b> is the first term of <see cref="Genders"/> (<c>male</c>), not an id from the content: the vocabulary is in code and its order is fixed, a
/// fork's <c>appearance.yaml</c> must offer every term (the validator checks it), and so a request without a gender always has a gender whose own defaults exist.</para>
/// <para><b>Stored parts that content no longer has</b> are replaced at load by that gender's default (or the file's colour default), logged once per character and process,
/// and never written back: put the option back and the character looks as it chose.</para>
/// </remarks>
public sealed class AppearanceRules
{
    private const int MaxLogged = 10_000;

    private readonly AppearanceDef _def;
    private readonly IServerLog? _log;
    private readonly ConcurrentDictionary<Guid, bool> _logged = new();

    /// <summary>Rules over <paramref name="def"/>; <paramref name="log"/> (optional) hears about stored ids that content no longer has.</summary>
    public AppearanceRules(AppearanceDef def, IServerLog? log = null)
    {
        _def = def ?? throw new ArgumentNullException(nameof(def));
        _log = log;
        DefaultGender = Genders.All.Ordered[0];
        Defaults = Resolve(null);
    }

    /// <summary>The gender of a request or a character that names none: the first term of <see cref="Genders"/>.</summary>
    public string DefaultGender { get; }

    /// <summary>The looks of a character that has none stored, and of a dev login: the default gender, its default face and hairstyle, the file's colours.</summary>
    public AppearanceRecord Defaults { get; }

    /// <summary>
    /// The looks a creation request asks for, with its omissions filled by the defaults; null when it asks for something the file does not offer: an unknown id, an id in the
    /// wrong palette, or a face or hairstyle that is not one of the chosen gender's. A null <paramref name="choice"/> is every default.
    /// </summary>
    public AppearanceRecord? Choose(AppearanceChoice? choice)
    {
        var gender = choice?.Gender ?? DefaultGender;
        if (!_def.Genders.TryGetValue(gender, out var options)) return null;
        var skin = choice?.Skin ?? _def.DefaultSkinToneId;
        var face = choice?.Face ?? options.Default.FaceId;
        var hair = choice?.Hair ?? options.Default.HairstyleId;
        var hairColor = choice?.HairColor ?? _def.DefaultHairColorId;
        if (!_def.SkinTones.Any(o => o.Id == skin)) return null;
        if (!options.Faces.Any(o => o.Id == face)) return null;
        if (!options.Hairstyles.Any(o => o.Id == hair)) return null;
        if (!_def.HairColors.Any(o => o.Id == hairColor)) return null;
        return new AppearanceRecord(gender, skin, face, hair, hairColor);
    }

    /// <summary>
    /// What is stored of a character as the current content shows it: a part never stored (null) or no longer offered is replaced by the default. A gender that is gone
    /// takes the default gender, and then a face or hairstyle of the old gender that the new one lacks falls back too. Logged once for <paramref name="character"/> when
    /// something that <i>was</i> stored had to be replaced.
    /// </summary>
    public AppearanceRecord Resolve(Guid character, AppearanceChoice? stored)
    {
        var replaced = new List<string>();
        var result = Resolve(stored, replaced);
        if (replaced.Count > 0 && _log is not null)
        {
            if (_logged.Count >= MaxLogged) _logged.Clear();
            if (_logged.TryAdd(character, true))
                _log.Warn($"[accounts] character {character}: appearance {string.Join(", ", replaced)} is not in appearance.yaml any more; shown with the default, still stored as it was.");
        }
        return result;
    }

    private AppearanceRecord Resolve(AppearanceChoice? stored, List<string>? replaced = null)
    {
        var gender = stored?.Gender;
        if (gender is not null && !_def.Genders.ContainsKey(gender))
        {
            replaced?.Add($"gender '{gender}'");
            gender = null;
        }
        gender ??= DefaultGender;
        var options = _def.Genders[gender];
        var skin = Pick(stored?.Skin, _def.SkinTones.Any(o => o.Id == stored?.Skin), _def.DefaultSkinToneId, replaced);
        var face = Pick(stored?.Face, options.Faces.Any(o => o.Id == stored?.Face), options.Default.FaceId, replaced);
        var hair = Pick(stored?.Hair, options.Hairstyles.Any(o => o.Id == stored?.Hair), options.Default.HairstyleId, replaced);
        var hairColor = Pick(stored?.HairColor, _def.HairColors.Any(o => o.Id == stored?.HairColor), _def.DefaultHairColorId, replaced);
        return new AppearanceRecord(gender, skin, face, hair, hairColor);
    }

    private static string Pick(string? stored, bool offered, string fallback, List<string>? replaced)
    {
        if (stored is null) return fallback;
        if (offered) return stored;
        replaced?.Add($"'{stored}'");
        return fallback;
    }
}
