using System;
using System.Collections.Generic;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Random;

namespace Game.Client.Core.Accounts;

/// <summary>
/// What is chosen on the creation screen (T-706, ARCHITECTURE §7.5.1), from the bundle's options only. It asks nobody: the server decides when the character is created,
/// and whatever it answers the choices stay, so a refusal or a step back loses nothing. A new character (<see cref="Reset"/>) starts from the defaults.
/// </summary>
public sealed class CharacterCreator
{
    private static readonly IReadOnlyList<ClientAppearanceOption> s_noOptions = Array.Empty<ClientAppearanceOption>();
    private static readonly IReadOnlyList<ClientColorOption> s_noColors = Array.Empty<ClientColorOption>();

    private readonly ClientAppearance? _options;
    private readonly IRandom _random;
    private readonly Dictionary<string, (string Face, string Hair)> _byGender = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
    private ClientGender? _gender;

    /// <summary>Starts with the defaults. Without options only the name can be chosen.</summary>
    public CharacterCreator(ClientAppearance? options, IRandom random)
    {
        _options = options;
        _random = random;
        Reset();
    }

    /// <summary>Counts changes, so a UI redraws only on one.</summary>
    public int Version { get; private set; }

    /// <summary>The name as typed.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The genders offered.</summary>
    public IReadOnlyList<ClientGender> Genders => _options?.Genders ?? Array.Empty<ClientGender>();

    /// <summary>The chosen gender's faces.</summary>
    public IReadOnlyList<ClientAppearanceOption> Faces => _gender?.Faces ?? s_noOptions;

    /// <summary>The chosen gender's hairstyles.</summary>
    public IReadOnlyList<ClientAppearanceOption> Hairstyles => _gender?.Hairstyles ?? s_noOptions;

    /// <summary>The skin tones.</summary>
    public IReadOnlyList<ClientColorOption> SkinTones => _options?.SkinTones ?? s_noColors;

    /// <summary>The hair colours.</summary>
    public IReadOnlyList<ClientColorOption> HairColors => _options?.HairColors ?? s_noColors;

    /// <summary>The job a new character starts in (the preview wears its look), or null.</summary>
    public string? StartJob => _options?.StartJobId;

    /// <summary>The chosen gender.</summary>
    public string Gender => _gender?.Gender ?? string.Empty;

    /// <summary>The chosen face.</summary>
    public string Face { get; private set; } = string.Empty;

    /// <summary>The chosen hairstyle.</summary>
    public string Hair { get; private set; } = string.Empty;

    /// <summary>The chosen skin tone.</summary>
    public string Skin { get; private set; } = string.Empty;

    /// <summary>The chosen hair colour.</summary>
    public string HairColor { get; private set; } = string.Empty;

    /// <summary>The look chosen, for the preview; null when there are no options.</summary>
    public AppearanceRecord? Look => _gender is null ? null : new AppearanceRecord(Gender, Skin, Face, Hair, HairColor);

    /// <summary>The look chosen, for the creation request; null (the server's defaults) when there are no options.</summary>
    public AppearanceChoice? Choice => _gender is null ? null : new AppearanceChoice(Gender, Skin, Face, Hair, HairColor);

    /// <summary>Takes the typed name.</summary>
    public void SetName(string name)
    {
        if (name == Name) return;
        Name = name;
        Version++;
    }

    /// <summary>
    /// Picks a gender. What fits stays: the colours always, and a face or hairstyle the new gender has as well. Otherwise the new gender's own face and hairstyle
    /// return, as last chosen for it, else its defaults; switching back finds the first gender's again.
    /// </summary>
    public bool SelectGender(string gender)
    {
        if (gender == Gender || Find(Genders, g => g.Gender, gender) is not { } next) return false;
        if (_gender is not null) _byGender[_gender.Gender] = (Face, Hair);
        _byGender.TryGetValue(next.Gender, out var remembered);
        _gender = next;
        Face = FirstOf(next.Faces, Face, remembered.Face, next.DefaultFaceId);
        Hair = FirstOf(next.Hairstyles, Hair, remembered.Hair, next.DefaultHairstyleId);
        Version++;
        return true;
    }

    /// <summary>Picks a face; false for one the gender does not have.</summary>
    public bool SelectFace(string id) => Take(Faces, o => o.Id, id, Face, v => Face = v);

    /// <summary>Picks a hairstyle; false for one the gender does not have.</summary>
    public bool SelectHair(string id) => Take(Hairstyles, o => o.Id, id, Hair, v => Hair = v);

    /// <summary>Picks a skin tone; false for one that is not offered.</summary>
    public bool SelectSkin(string id) => Take(SkinTones, o => o.Id, id, Skin, v => Skin = v);

    /// <summary>Picks a hair colour; false for one that is not offered.</summary>
    public bool SelectHairColor(string id) => Take(HairColors, o => o.Id, id, HairColor, v => HairColor = v);

    /// <summary>A random look: gender, face, hairstyle and colours. The name stays.</summary>
    public void Randomize()
    {
        if (Genders.Count == 0) return;
        SelectGender(Genders[_random.Next(Genders.Count)].Gender);
        if (Faces.Count > 0) Face = Faces[_random.Next(Faces.Count)].Id;
        if (Hairstyles.Count > 0) Hair = Hairstyles[_random.Next(Hairstyles.Count)].Id;
        if (SkinTones.Count > 0) Skin = SkinTones[_random.Next(SkinTones.Count)].Id;
        if (HairColors.Count > 0) HairColor = HairColors[_random.Next(HairColors.Count)].Id;
        Version++;
    }

    /// <summary>The defaults and no name: for the next new character.</summary>
    public void Reset()
    {
        Name = string.Empty;
        _byGender.Clear();
        _gender = Genders.Count > 0 ? Genders[0] : null;
        Face = _gender is null ? string.Empty : FirstOf(_gender.Faces, _gender.DefaultFaceId);
        Hair = _gender is null ? string.Empty : FirstOf(_gender.Hairstyles, _gender.DefaultHairstyleId);
        Skin = First(SkinTones, _options?.DefaultSkinToneId);
        HairColor = First(HairColors, _options?.DefaultHairColorId);
        Version++;
    }

    private bool Take<T>(IReadOnlyList<T> options, Func<T, string> idOf, string id, string current, Action<string> set) where T : class
    {
        if (Find(options, idOf, id) is null) return false;
        if (id == current) return true;
        set(id);
        Version++;
        return true;
    }

    /// <summary>The first of <paramref name="wanted"/> that is an option, else the first option, else nothing.</summary>
    private static string FirstOf(IReadOnlyList<ClientAppearanceOption> options, params string?[] wanted)
    {
        foreach (var id in wanted)
            if (id is not null && Find(options, o => o.Id, id) is not null) return id;
        return options.Count > 0 ? options[0].Id : string.Empty;
    }

    private static string First(IReadOnlyList<ClientColorOption> options, string? wanted)
    {
        if (wanted is not null && Find(options, o => o.Id, wanted) is not null) return wanted;
        return options.Count > 0 ? options[0].Id : string.Empty;
    }

    private static T? Find<T>(IReadOnlyList<T> options, Func<T, string> idOf, string id) where T : class
    {
        for (var i = 0; i < options.Count; i++)
            if (idOf(options[i]) == id) return options[i];
        return null;
    }
}
