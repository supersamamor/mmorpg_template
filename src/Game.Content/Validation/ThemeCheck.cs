using System.Text;
using System.Text.Json;
using Game.Content.Registry;
using Game.Content.Themes;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation;

/// <summary>
/// <c>validate</c>'s check of a theme against the theme contract (docs/THEMES.md; T-405, T-224). It reads text and glTF files only, so it needs no Godot.
/// <para>Equipment and clips (T-405): a <c>model.equip.*</c> entry without exactly one of <c>attach</c> or <c>skinned</c>; an <c>attach</c> bone that a character
/// rig lacks; a <c>skinned</c> rig no model declares; a clip suffix that is no weapon type; a theme file that is a Git LFS pointer; (warning) an item's worn visual
/// with no file in the chain.</para>
/// <para>Contract (T-224): variants (<c>variant_of</c>, <c>tint</c>, <c>scale</c>, <c>outline</c>); every model's <c>Body_LOD0</c>, §9 budgets (triangles, one
/// material, texture size); a rigged model's skin, <c>Armature</c>, humanoid bones and facing; a living model's height; an animation library's rig, required clips,
/// clip names and tracks; (warnings) see-through materials, missing outline directions, an origin off the ground.</para>
/// <para>Outfits (T-708): their pieces are worn pieces; (warning) an outfit that takes a colour none of its pieces is painted with.</para>
/// <para>Appearance (T-706): <c>recolor</c> and <c>hides_hair</c>; a job's pieces are worn pieces; a body with its largest face and hairstyle stays inside the §9
/// character budgets; (warnings) a body with no skin paint, or no cloth paint while a job has a cloth colour, and a face or hairstyle with a texture of its own.</para>
/// Every finding names the manifest, the key, the reason and, where there is one, the fix.
/// </summary>
public static class ThemeCheck
{
    private const string DefaultTheme = "default";

    /// <summary>The findings for <paramref name="theme"/> and, when it is another theme, <c>default</c> behind it.</summary>
    public static IReadOnlyList<ValidationIssue> Check(ContentDatabase database, string themesDir, string theme)
    {
        var issues = new List<ValidationIssue>();
        var chain = new List<Manifest>();
        foreach (var name in theme == DefaultTheme ? new[] { DefaultTheme } : new[] { theme, DefaultTheme })
        {
            var manifest = Manifest.Load(themesDir, name);
            if (manifest is not null) chain.Add(manifest);
        }
        if (chain.Count == 0) return issues; // a missing manifest is reported by the content boot

        foreach (var manifest in chain)
            foreach (var (key, _) in manifest.Entries)
                if (manifest.FileOf(key) is { } file && file.EndsWith(".glb", StringComparison.Ordinal) && File.Exists(file) && IsPointer(file))
                    issues.Add(Issue(ValidationSeverity.Error, manifest, key, key, ValidationCodes.EquipManifest, $"{key}: {LfsPointer.Message(manifest.RelativeFile(key))}"));

        var rigs = RigBones(chain, issues);
        var bodyRigs = BodyRigs(database, chain);

        foreach (var manifest in chain)
        {
            foreach (var (key, entry) in manifest.Entries)
            {
                if (key.StartsWith("model.equip.", StringComparison.Ordinal) && Text(entry, "variant_of") is null) CheckEquip(manifest, key, entry, rigs, bodyRigs, issues);
                if (entry.TryGetProperty("clips", out var clips) && clips.ValueKind == JsonValueKind.Object) CheckClips(manifest, key, clips, issues);
                CheckRestyle(manifest, key, entry, issues);
                CheckLookFields(manifest, key, entry, issues);
                if (Text(entry, "variant_of") is not null) CheckVariant(chain, manifest, key, entry, issues);
            }
        }

        var contract = new ContractPass(chain, rigs, issues);
        contract.Run();
        CheckAppearance(database, chain, rigs, bodyRigs, contract, issues);
        CheckCharacterImages(database, chain, contract, issues);

        var missing = chain.Select(m => m.Fallback("missing")).FirstOrDefault(k => k is not null);
        foreach (var item in database.Items.All)
        {
            var key = item.Equip?.VisualAssetKey;
            if (string.IsNullOrEmpty(key)) continue;
            var first = chain.FirstOrDefault(m => m.Entries.ContainsKey(key));
            if (first is null) continue; // an unknown key is the asset-keys rule's error
            if (chain.Any(m => m.HasFile(key))) continue;
            issues.Add(Issue(ValidationSeverity.Warning, first, key, key, ValidationCodes.WarnVisualFile,
                $"{item.Id}: worn visual '{key}' has no model file in the theme chain; the client draws {(missing is null ? "the built-in missing model" : $"the missing model '{missing}'")} instead"));
        }
        return issues;
    }

    /// <summary>Every asset key content names (for reports such as <c>tools/themeimport</c>'s list of keys a theme leaves to <c>default</c>).</summary>
    public static IReadOnlySet<string> KeysContentUses(ContentDatabase database) =>
        Rules.ContentWalker.Assets(database).Where(a => !string.IsNullOrEmpty(a.Key)).Select(a => a.Key!).ToHashSet(StringComparer.Ordinal);

    // ---------------- T-405: equipment and clips ----------------

    private static void CheckEquip(Manifest manifest, string key, JsonElement entry, Dictionary<string, IReadOnlySet<string>> rigs, IReadOnlyList<string> bodyRigs, List<ValidationIssue> issues)
    {
        var attach = Text(entry, "attach");
        var skinned = Text(entry, "skinned");
        if ((attach is null) == (skinned is null))
        {
            issues.Add(Issue(ValidationSeverity.Error, manifest, key, key, ValidationCodes.EquipManifest,
                $"{key}: must have exactly one of 'attach' or 'skinned', not {(attach is null ? "neither" : "both")}"));
            return;
        }
        if (attach is not null)
        {
            foreach (var rig in bodyRigs)
            {
                if (!rigs.TryGetValue(rig, out var bones)) continue; // unreadable: reported with the rig
                if (!bones.Contains(attach))
                    issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + ".attach", ValidationCodes.EquipManifest,
                        $"{key}: attach bone '{attach}' does not exist in rig '{rig}' (bones: {string.Join(", ", bones.OrderBy(b => b, StringComparer.Ordinal))})"));
            }
        }
        else if (!rigs.ContainsKey(skinned!))
        {
            issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + ".skinned", ValidationCodes.EquipManifest,
                $"{key}: skinned to rig '{skinned}', but no model in the theme chain declares a readable rig of that name"));
        }
    }

    private static void CheckClips(Manifest manifest, string key, JsonElement clips, List<ValidationIssue> issues)
    {
        foreach (var clip in clips.EnumerateObject())
        {
            var dot = clip.Name.IndexOf('.');
            var id = dot < 0 ? clip.Name : clip.Name[..dot];
            if (!AnimationIds.All.Contains(id))
                issues.Add(Issue(ValidationSeverity.Error, manifest, clip.Name, key + ".clips." + clip.Name, ValidationCodes.ThemeAnim,
                    $"{key}: clip '{clip.Name}' is not an animation id ({string.Join(", ", AnimationIds.All.Ordered)}); map the file's clip to one of them"));
            if (dot < 0) continue;
            var suffix = clip.Name[(dot + 1)..];
            if (!WeaponTypes.All.Contains(suffix))
                issues.Add(Issue(ValidationSeverity.Error, manifest, clip.Name, key + ".clips." + clip.Name, ValidationCodes.EquipManifest,
                    $"{key}: clip '{clip.Name}' ends in '{suffix}', which is not a weapon type ({string.Join(", ", WeaponTypes.All.Ordered)})"));
        }
    }

    /// <summary>The rigs of the body models of every gender (the characters that wear gear).</summary>
    private static IReadOnlyList<string> BodyRigs(ContentDatabase database, List<Manifest> chain)
    {
        var rigs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var gender in database.Appearance.Genders.Values)
            foreach (var manifest in chain)
                if (manifest.Entries.TryGetValue(gender.BodyModelAssetKey, out var entry) && Text(entry, "rig") is { } rig) { rigs.Add(rig); break; }
        return rigs.ToList();
    }

    /// <summary>Bone names by rig name, read from the first model of each rig whose file is a glTF with a skin.</summary>
    private static Dictionary<string, IReadOnlySet<string>> RigBones(List<Manifest> chain, List<ValidationIssue> issues)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in chain)
        {
            foreach (var (key, entry) in manifest.Entries)
            {
                if (Text(entry, "rig") is not { } rig || result.ContainsKey(rig) || !key.StartsWith("model.", StringComparison.Ordinal)) continue;
                var file = chain.Select(m => m.FileOf(key)).FirstOrDefault(f => f is not null && File.Exists(f));
                if (file is null || !tried.Add(file)) continue;
                var read = GlbBones(file, out var problem);
                if (read is not null) result[rig] = read;
                else if (problem is not null && !problem.Contains("LFS pointer", StringComparison.Ordinal)) issues.Add(Issue(ValidationSeverity.Error, manifest, key, key, ValidationCodes.EquipManifest, $"{key}: {problem}"));
            }
        }
        return result;
    }

    private static bool IsPointer(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[64];
        return LfsPointer.Is(head.AsSpan(0, stream.Read(head, 0, head.Length)));
    }

    /// <summary>The joint names of the first skin of a .glb, or null (with <paramref name="problem"/> when the file is a Git LFS pointer or not a glTF).</summary>
    internal static IReadOnlySet<string>? GlbBones(string path, out string? problem)
    {
        problem = null;
        var bytes = File.ReadAllBytes(path);
        if (LfsPointer.Is(bytes)) { problem = LfsPointer.Message(path); return null; }
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67u) { problem = $"{path} is not a binary glTF"; return null; }
        var length = (int)BitConverter.ToUInt32(bytes, 12);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, length));
        if (!doc.RootElement.TryGetProperty("skins", out var skins) || skins.GetArrayLength() == 0 || !doc.RootElement.TryGetProperty("nodes", out var nodes)) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var joint in skins[0].GetProperty("joints").EnumerateArray())
            if (nodes[joint.GetInt32()].TryGetProperty("name", out var name) && name.GetString() is { } text) names.Add(text);
        return names;
    }

    // ---------------- T-224: variants ----------------

    private static void CheckRestyle(Manifest manifest, string key, JsonElement entry, List<ValidationIssue> issues)
    {
        void Error(string field, string message) => issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + "." + field, ValidationCodes.ThemeVariant, $"{key}: {message}"));
        var restyled = false;
        if (entry.TryGetProperty("tint", out var tint))
        {
            restyled = true;
            if (tint.ValueKind != JsonValueKind.String || !IsColor(tint.GetString()!)) Error("tint", "'tint' must be a colour \"#rrggbb\"");
        }
        if (entry.TryGetProperty("scale", out var scale))
        {
            restyled = true;
            if (scale.ValueKind != JsonValueKind.Number || scale.GetDouble() < ThemeContract.MinVariantScale || scale.GetDouble() > ThemeContract.MaxVariantScale)
                Error("scale", $"'scale' must be a number from {ThemeContract.MinVariantScale} to {ThemeContract.MaxVariantScale}");
        }
        if (entry.TryGetProperty("outline", out var outline))
        {
            restyled = true;
            if (!(outline.ValueKind == JsonValueKind.False || (outline.ValueKind == JsonValueKind.String && IsColor(outline.GetString()!))))
                Error("outline", "'outline' must be false (no outline) or a colour \"#rrggbb\"");
        }
        if (restyled && !ThemeContract.VariantPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            Error("tint", $"only whole models ({string.Join(", ", ThemeContract.VariantPrefixes.Select(p => p + "*"))}) can take a tint, scale or outline; a worn piece merges into its character's mesh");
    }

    private static void CheckVariant(List<Manifest> chain, Manifest manifest, string key, JsonElement entry, List<ValidationIssue> issues)
    {
        void Error(string field, string message) => issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + "." + field, ValidationCodes.ThemeVariant, $"{key}: {message}"));
        if (!ThemeContract.VariantPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            Error("variant_of", $"only whole models ({string.Join(", ", ThemeContract.VariantPrefixes.Select(p => p + "*"))}) can be variants; a worn piece merges into its character's mesh");
        foreach (var field in new[] { "file", "files", "rig", "anims", "attach", "skinned", "clips" })
            if (entry.TryGetProperty(field, out _))
                Error(field, $"a variant takes '{field}' from its base; remove it here (or remove 'variant_of' to make this an ordinary entry)");

        var seen = new List<string> { key };
        var at = Text(entry, "variant_of")!;
        for (var depth = 0; ; depth++)
        {
            if (seen.Contains(at)) { Error("variant_of", $"variants form a loop: {string.Join(" -> ", seen)} -> {at}"); return; }
            if (depth >= ThemeContract.MaxVariantDepth) { Error("variant_of", $"variants stand more than {ThemeContract.MaxVariantDepth} deep ({string.Join(" -> ", seen)} -> {at})"); return; }
            var listing = chain.Select(m => m.Entries.TryGetValue(at, out var e) ? e : (JsonElement?)null).FirstOrDefault(e => e is not null);
            if (listing is not { } baseEntry) { Error("variant_of", $"base '{at}' is in no theme of the chain"); return; }
            seen.Add(at);
            if (Text(baseEntry, "variant_of") is { } next) { at = next; continue; }
            if (ThemeContract.IsPiece(at, Text(baseEntry, "attach"), Text(baseEntry, "skinned"), Text(baseEntry, "rig"), Text(baseEntry, "anims")))
                Error("variant_of", $"base '{at}' is a worn piece or appearance part; a variant must draw a whole model");
            else if (!chain.Any(m => m.HasFile(at)))
                Error("variant_of", $"base '{at}' has no model file in the theme chain");
            return;
        }
    }

    // ---------------- T-706: appearance ----------------

    /// <summary>The manifest fields of a character's looks: <c>recolor</c> (colour role to one painted colour or a list) on models, <c>hides_hair</c> on worn pieces.</summary>
    private static void CheckLookFields(Manifest manifest, string key, JsonElement entry, List<ValidationIssue> issues)
    {
        void Error(string field, string message) => issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + "." + field, ValidationCodes.ThemeAppearance, $"{key}: {message}"));
        if (entry.TryGetProperty("hides_hair", out var hides))
        {
            if (hides.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Error("hides_hair", "'hides_hair' must be true or false");
            else if (Text(entry, "attach") is null && Text(entry, "skinned") is null) Error("hides_hair", "only a worn piece ('attach' or 'skinned') can hide the hair");
        }
        if (!entry.TryGetProperty("recolor", out var recolor)) return;
        if (!key.StartsWith("model.", StringComparison.Ordinal)) Error("recolor", "only a model can have 'recolor'");
        if (recolor.ValueKind != JsonValueKind.Object)
        {
            Error("recolor", "'recolor' must be an object of colour role to painted colour(s), e.g. { \"skin\": [\"#f1c7a1\", \"#e9a08a\"] }");
            return;
        }
        foreach (var role in recolor.EnumerateObject())
        {
            if (!AppearanceColorRoles.All.Contains(role.Name))
                Error("recolor." + role.Name, $"'{role.Name}' is not an appearance colour role ({string.Join(", ", AppearanceColorRoles.All.Ordered)})");
            var good = role.Value.ValueKind switch
            {
                JsonValueKind.String => IsColor(role.Value.GetString()!),
                JsonValueKind.Array => role.Value.EnumerateArray().All(c => c.ValueKind == JsonValueKind.String && IsColor(c.GetString()!)),
                _ => false,
            };
            if (!good) Error("recolor." + role.Name, "must be a painted colour \"#rrggbb\" or a list of them (an empty list: nothing takes this colour)");
        }
    }

    /// <summary>The painted colours of a role on a key's entry: its <c>recolor</c>, else the contract's.</summary>
    private static IReadOnlyList<string> Paint(JsonElement entry, string role, IReadOnlyList<string> contract)
    {
        if (!entry.TryGetProperty("recolor", out var recolor) || recolor.ValueKind != JsonValueKind.Object || !recolor.TryGetProperty(role, out var value)) return contract;
        if (value.ValueKind == JsonValueKind.String) return IsColor(value.GetString()!) ? new[] { value.GetString()! } : Array.Empty<string>();
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).Where(IsColor).ToList()
            : Array.Empty<string>();
    }

    private static void CheckAppearance(ContentDatabase database, List<Manifest> chain, Dictionary<string, IReadOnlySet<string>> rigs, IReadOnlyList<string> bodyRigs,
        ContractPass contract, List<ValidationIssue> issues)
    {
        Manifest? Holder(string key) => chain.FirstOrDefault(m => m.HasFile(key));

        var worn = database.Jobs.All.SelectMany(j => j.Look.Pieces).Select(p => p.ModelAssetKey).Concat(database.Outfits.All.SelectMany(o => o.Pieces).Select(p => p.ModelAssetKey));
        foreach (var key in worn.Distinct(StringComparer.Ordinal))
        {
            if (key.StartsWith("model.equip.", StringComparison.Ordinal)) continue; // checked with the equipment
            if (chain.FirstOrDefault(m => m.Entries.ContainsKey(key)) is { } first) CheckEquip(first, key, first.Entries[key], rigs, bodyRigs, issues);
        }

        foreach (var outfit in database.Outfits.All)
        {
            // An outfit that takes a colour needs a piece painted with it (T-708).
            foreach (var role in outfit.TakesColors)
            {
                if (ContractPaint(role) is not { } paint) continue; // an unknown role is the vocabulary rule's error
                Manifest? at = null;
                var painted = false;
                foreach (var key in outfit.Pieces.Select(p => p.ModelAssetKey).Distinct(StringComparer.Ordinal))
                {
                    if (Holder(key) is not { } holder || contract.ChainFacts(key) is not { Body0: { } piece }) continue; // reported elsewhere
                    at ??= holder;
                    var colors = Paint(holder.Entries[key], role, paint);
                    painted |= colors.Count > 0 && piece.IsPainted(colors);
                }
                if (at is null || painted) continue;
                var first = outfit.Pieces[0].ModelAssetKey;
                issues.Add(Issue(ValidationSeverity.Warning, at, first, first, ValidationCodes.WarnThemeLook,
                    $"{outfit.Id}: takes the character's {role} colour, but no piece has a vertex painted with it; paint it with the contract's colours, or name yours in 'recolor' (docs/THEMES.md 1.8)"));
            }
        }

        var clothed = database.Jobs.All.FirstOrDefault(j => j.Look.ClothColor is not null);
        foreach (var gender in database.Appearance.Genders.Values)
        {
            var bodyKey = gender.BodyModelAssetKey;
            if (Holder(bodyKey) is not { } manifest || contract.ChainFacts(bodyKey) is not { Body0: { } body } facts) continue; // reported elsewhere
            void Warn(Manifest at, string key, string message) =>
                issues.Add(Issue(ValidationSeverity.Warning, at, key, key, ValidationCodes.WarnThemeLook, $"{key}: {message}"));

            var entry = manifest.Entries[bodyKey];
            var skin = Paint(entry, AppearanceColorRoles.Skin, AppearancePaint.Skin);
            if (skin.Count > 0 && !body.IsPainted(skin))
                Warn(manifest, bodyKey, $"no vertex is painted with its skin colour ({string.Join(", ", skin)}), so skin tones do not show; paint the skin's vertices with it, or name the colour in 'recolor' (docs/THEMES.md 1.7)");
            var cloth = Paint(entry, AppearanceColorRoles.Cloth, AppearancePaint.Cloth);
            if (clothed is not null && cloth.Count > 0 && !body.IsPainted(cloth))
                Warn(manifest, bodyKey, $"no vertex is painted with its cloth colour ({string.Join(", ", cloth)}), so a job's colour ({clothed.Id}) does not show; paint it, or name the colour in 'recolor' (docs/THEMES.md 1.7)");

            // What every character of this gender draws: the body with its largest face and hairstyle, merged (§9).
            int lod0 = body.Triangles, lod1 = (facts.Body1 ?? body).Triangles;
            foreach (var options in new[] { gender.Faces.Select(f => f.ModelAssetKey), gender.Hairstyles.Select(h => h.ModelAssetKey) })
            {
                int most0 = 0, most1 = 0;
                foreach (var key in options.Distinct(StringComparer.Ordinal))
                {
                    if (contract.ChainFacts(key) is not { Body0: { } part } partFacts) continue;
                    most0 = Math.Max(most0, part.Triangles);
                    most1 = Math.Max(most1, (partFacts.Body1 ?? part).Triangles);
                }
                lod0 += most0;
                lod1 += most1;
            }
            if (body.Triangles <= ThemeContract.CharacterLod0Triangles && lod0 > ThemeContract.CharacterLod0Triangles)
                issues.Add(Issue(ValidationSeverity.Error, manifest, bodyKey, bodyKey, ValidationCodes.ThemeBudget,
                    $"{bodyKey}: with its largest face and hairstyle it has {lod0} triangles at {ModelFacts.Lod0}, over the {ThemeContract.CharacterLod0Triangles} budget of a character (§9); they are one mesh in the game"));
            if ((facts.Body1 ?? body).Triangles <= ThemeContract.CharacterLod1Triangles && lod1 > ThemeContract.CharacterLod1Triangles)
                issues.Add(Issue(ValidationSeverity.Error, manifest, bodyKey, bodyKey, ValidationCodes.ThemeBudget,
                    $"{bodyKey}: with its largest face and hairstyle it has {lod1} triangles at {ModelFacts.Lod1}, over the {ThemeContract.CharacterLod1Triangles} budget of a far character (§9); give the face and hairstyle a '{ModelFacts.Lod1}' mesh"));
        }
    }

    // ---------------- T-711: one draw call a character ----------------

    /// <summary>
    /// Every part a character of a gender merges into its body (its faces and hairstyles, the jobs' pieces, the outfits' pieces for that gender, worn equipment)
    /// must draw the same image as the body, with the same shading, or it stays a surface of its own: one more draw call for every character that has it (§9). One
    /// warning per part, naming the bodies it splits from. A body with more than one material is the budget's error, so it is left out here.
    /// </summary>
    private static void CheckCharacterImages(ContentDatabase database, List<Manifest> chain, ContractPass contract, List<ValidationIssue> issues)
    {
        Manifest? Holder(string key) => chain.FirstOrDefault(m => m.HasFile(key));
        var splits = new SortedDictionary<string, (string Part, SortedSet<string> Bodies, string Body)>(StringComparer.Ordinal);
        var equipment = database.Items.All.Select(i => i.Equip?.VisualAssetKey).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).ToList();
        var jobPieces = database.Jobs.All.SelectMany(j => j.Look.Pieces).Select(p => p.ModelAssetKey).ToList();
        foreach (var (genderId, gender) in database.Appearance.Genders)
        {
            var bodyKey = gender.BodyModelAssetKey;
            if (contract.ChainFacts(bodyKey) is not { Body0: { } body } bodyFacts) continue; // reported elsewhere
            var bodyGroups = Groups(bodyFacts, body);
            if (bodyGroups.Count != 1) continue;
            var reference = bodyGroups.First();
            var parts = gender.Faces.Select(f => f.ModelAssetKey)
                .Concat(gender.Hairstyles.Select(h => h.ModelAssetKey))
                .Concat(jobPieces)
                .Concat(database.Outfits.All.SelectMany(o => o.Pieces).Where(p => p.Gender is null || p.Gender == genderId).Select(p => p.ModelAssetKey))
                .Concat(equipment);
            foreach (var key in parts.Distinct(StringComparer.Ordinal))
            {
                if (contract.ChainFacts(key) is not { Body0: { } part } facts) continue;
                var other = Groups(facts, part).Where(g => g != reference).ToList();
                if (other.Count == 0) continue;
                var shading = ModelFacts.ImageOf(other[0]) == ModelFacts.ImageOf(reference); // the same image, other material settings: say which
                if (!splits.TryGetValue(key, out var split)) splits[key] = split = (Describe(other[0], shading), new SortedSet<string>(StringComparer.Ordinal), Describe(reference, shading));
                split.Bodies.Add(bodyKey);
            }
        }
        foreach (var (key, split) in splits)
        {
            if (Holder(key) is not { } manifest) continue;
            issues.Add(Issue(ValidationSeverity.Warning, manifest, key, key, ValidationCodes.WarnThemeLook,
                $"{key}: draws {split.Part} where {string.Join(" and ", split.Bodies)} draw{(split.Bodies.Count == 1 ? "s" : "")} {split.Body}, so it stays a surface of its own and every character "
                + "that has it costs one more draw call (§9); paint it on the body's atlas image with the same material settings (docs/THEMES.md 1.9)"));
        }
    }

    private static HashSet<string> Groups(ModelFacts facts, MeshFacts mesh) =>
        mesh.Materials.Select(m => facts.MaterialGroups.TryGetValue(m, out var g) ? g : "none|r1|m1|one-sided").ToHashSet(StringComparer.Ordinal);

    /// <summary>A material group in words.</summary>
    private static string Describe(string group, bool withShading)
    {
        var image = ModelFacts.ImageOf(group);
        var shading = withShading && group.Length > image.Length ? " (" + group[(image.Length + 1)..].Replace('|', ' ') + ")" : "";
        if (image.StartsWith("own:", StringComparison.Ordinal)) return "a material of its own (see-through, or a texture other than the base colour)";
        return (image == "none" ? "no texture" : "image " + image["image:".Length..]) + shading;
    }

    private static IReadOnlyList<string>? ContractPaint(string role) => role switch
    {
        AppearanceColorRoles.Skin => AppearancePaint.Skin,
        AppearanceColorRoles.Hair => AppearancePaint.Hair,
        AppearanceColorRoles.Cloth => AppearancePaint.Cloth,
        _ => null,
    };

    private static bool IsColor(string text) => text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);

    // ---------------- T-224: the contract on model and animation files ----------------

    private sealed class ContractPass
    {
        private readonly List<Manifest> _chain;
        private readonly Dictionary<string, IReadOnlySet<string>> _rigs;
        private readonly List<ValidationIssue> _issues;
        private readonly Dictionary<string, ModelFacts?> _facts = new(StringComparer.Ordinal);

        public ContractPass(List<Manifest> chain, Dictionary<string, IReadOnlySet<string>> rigs, List<ValidationIssue> issues)
        {
            _chain = chain;
            _rigs = rigs;
            _issues = issues;
        }

        public void Run()
        {
            foreach (var manifest in _chain)
            {
                foreach (var (key, entry) in manifest.Entries)
                {
                    if (Text(entry, "variant_of") is not null) continue;
                    if (key.StartsWith("anim.", StringComparison.Ordinal)) Library(manifest, key, entry);
                    else if (key.StartsWith("model.", StringComparison.Ordinal)) Model(manifest, key, entry);
                }
            }
        }

        private void Error(Manifest manifest, string key, string code, string message, string? path = null) =>
            _issues.Add(Issue(ValidationSeverity.Error, manifest, key, path ?? key, code, $"{key}: {message}"));

        private void Warn(Manifest manifest, string key, string message) =>
            _issues.Add(Issue(ValidationSeverity.Warning, manifest, key, key, ValidationCodes.WarnThemeLook, $"{key}: {message}"));

        /// <summary>The facts of a key's own file in <paramref name="manifest"/>; null (after one error) when the file cannot be read, or when it is absent or a pointer.</summary>
        private ModelFacts? Facts(Manifest manifest, string key)
        {
            if (manifest.FileOf(key) is not { } file || !file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || !File.Exists(file) || IsPointer(file)) return null;
            if (_facts.TryGetValue(file, out var known)) return known;
            try
            {
                return _facts[file] = ModelFacts.Of(GltfFile.Load(file));
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or InvalidOperationException or FormatException)
            {
                Error(manifest, key, ValidationCodes.ThemeModel, $"{manifest.RelativeFile(key)} cannot be read as glTF: {e.Message}");
                return _facts[file] = null;
            }
        }

        /// <summary>The facts of a key through the chain (the first theme whose file exists).</summary>
        public ModelFacts? ChainFacts(string key)
        {
            foreach (var manifest in _chain)
                if (manifest.HasFile(key)) return Facts(manifest, key);
            return null;
        }

        private JsonElement? ChainEntry(string key) => _chain.Select(m => m.Entries.TryGetValue(key, out var e) ? e : (JsonElement?)null).FirstOrDefault(e => e is not null);

        private void Model(Manifest manifest, string key, JsonElement entry)
        {
            if (Facts(manifest, key) is not { } facts) return;
            var rig = Text(entry, "rig");
            var anims = Text(entry, "anims");
            var attach = Text(entry, "attach");
            var skinned = Text(entry, "skinned");
            var piece = ThemeContract.IsPiece(key, attach, skinned, rig, anims);
            var living = anims is not null;

            if (facts.Body0 is not { } body)
            {
                var names = facts.Meshes.Select(m => m.Name).ToList();
                Error(manifest, key, ValidationCodes.ThemeModel, $"has no mesh node named '{ModelFacts.Lod0}' (mesh nodes: {(names.Count == 0 ? "none" : string.Join(", ", names))}); "
                    + (names.Count > 1 ? $"join the meshes into one in Blender, or name the main one with 'meshes.lod0' in the import mapping" : $"rename it, or set 'meshes.lod0' in the import mapping"));
                return;
            }
            var lod1 = facts.Body1;

            // Budgets (§9).
            if (living)
            {
                if (body.Triangles > ThemeContract.CharacterLod0Triangles)
                    Error(manifest, key, ValidationCodes.ThemeBudget, $"{ModelFacts.Lod0} has {body.Triangles} triangles, over the {ThemeContract.CharacterLod0Triangles} budget (§9); decimate it in Blender");
                if (lod1 is not null && lod1.Triangles > ThemeContract.CharacterLod1Triangles)
                    Error(manifest, key, ValidationCodes.ThemeBudget, $"{ModelFacts.Lod1} has {lod1.Triangles} triangles, over the {ThemeContract.CharacterLod1Triangles} budget (§9); decimate it further");
                if (lod1 is null && body.Triangles > ThemeContract.CharacterLod1Triangles)
                    Error(manifest, key, ValidationCodes.ThemeBudget, $"has no {ModelFacts.Lod1}, so far characters draw {ModelFacts.Lod0}'s {body.Triangles} triangles, over the {ThemeContract.CharacterLod1Triangles} LOD1 budget (§9); add a decimated '{ModelFacts.Lod1}' (or 'meshes.lod1' in the import mapping)");
            }
            else
            {
                var budget = piece ? ThemeContract.PieceTriangles : ThemeContract.PropTriangles;
                if (body.Triangles > budget)
                    Error(manifest, key, ValidationCodes.ThemeBudget, $"{ModelFacts.Lod0} has {body.Triangles} triangles, over the {budget} budget for {(piece ? "a worn piece or appearance part" : "a prop")}; decimate it in Blender");
            }
            var materials = body.Materials.Union(lod1?.Materials ?? Enumerable.Empty<int>()).ToList();
            if (materials.Count > ThemeContract.MaxMaterials)
                Error(manifest, key, ValidationCodes.ThemeBudget, $"uses {materials.Count} materials; a model has {ThemeContract.MaxMaterials} (§9: one draw call per character): bake them into one atlas in Blender");
            foreach (var (image, width, height) in facts.Textures)
                if (width > ThemeContract.MaxTextureSize || height > ThemeContract.MaxTextureSize)
                    Error(manifest, key, ValidationCodes.ThemeBudget, $"texture {image} is {width}x{height}, over {ThemeContract.MaxTextureSize}x{ThemeContract.MaxTextureSize} (§9); scale it down");

            // The toon look (T-222).
            if (materials.Any(facts.SeeThroughMaterials.Contains))
                Warn(manifest, key, "has a see-through material (alphaMode BLEND or MASK); the toon look and the outline leave it alone, so it draws plain. Make it opaque unless it must show through");
            if (living && !body.HasOutlineDirections)
                Warn(manifest, key, $"{ModelFacts.Lod0} has no outline directions (glTF TANGENT); the outline falls back to normals and opens at hard edges. tools/themeimport writes them");

            // Size and placement.
            if (living)
            {
                if (body.Height < ThemeContract.MinLivingHeight || body.Height > ThemeContract.MaxLivingHeight)
                    Error(manifest, key, ValidationCodes.ThemeModel, $"is {body.Height:0.###} units tall; 1 cell = 1 unit, so the units are wrong: set 'scale' in the import mapping (e.g. {SuggestScale(body.Height)})");
                else if (Math.Abs(body.Min.Y) > ThemeContract.GroundTolerance * body.Height)
                    Warn(manifest, key, $"its lowest point is {body.Min.Y:0.##} units from its origin ({body.Height:0.##} tall); the origin should be on the ground under it (§7.7). Move the origin in Blender");
            }
            else if (Math.Max(body.Height, Math.Max(body.Max.X - body.Min.X, body.Max.Z - body.Min.Z)) > ThemeContract.MaxPieceSize)
            {
                Error(manifest, key, ValidationCodes.ThemeModel, $"is larger than {ThemeContract.MaxPieceSize} units; 1 cell = 1 unit, so the units are wrong: set 'scale' in the import mapping");
            }

            // Skeleton.
            if (rig is not null || skinned is not null)
            {
                if (!facts.HasSkin)
                {
                    Error(manifest, key, ValidationCodes.ThemeModel, $"declares rig '{rig ?? skinned}' but has no skin; export it with its armature");
                    return;
                }
                if (facts.SkeletonHolder != ModelFacts.ArmatureName)
                    Error(manifest, key, ValidationCodes.ThemeModel, facts.SkeletonHolder is null
                        ? $"its skeleton's root bone is a scene root; it must sit under a node named '{ModelFacts.ArmatureName}' (§7.7). tools/themeimport adds one"
                        : $"its skeleton sits under '{facts.SkeletonHolder}'; that node must be named '{ModelFacts.ArmatureName}' (§7.7). tools/themeimport renames it");
            }
            if (rig == ThemeContract.HumanoidRig)
            {
                var lacking = ThemeContract.HumanoidBones.Where(b => !facts.Bones.Contains(b)).ToList();
                if (lacking.Count > 0)
                    Error(manifest, key, ValidationCodes.ThemeModel, $"the shared humanoid rig lacks bone(s) {string.Join(", ", lacking)} (bones: {string.Join(", ", facts.Bones)}); map the pack's bones with 'rigs.{ThemeContract.HumanoidRig}.bones' in the import mapping");
                else if (living) Facing(manifest, key, facts);
            }
            if (skinned is not null && _rigs.TryGetValue(skinned, out var rigBones))
            {
                var foreign = facts.Bones.Where(b => !rigBones.Contains(b)).ToList();
                if (foreign.Count > 0)
                    Error(manifest, key, ValidationCodes.ThemeModel, $"is skinned to bone(s) {string.Join(", ", foreign)} that rig '{skinned}' lacks; skin it to the body's own armature");
            }
            if (rig is not null && anims is not null)
            {
                if (ChainEntry(anims) is not { } library)
                {
                    Error(manifest, key, ValidationCodes.ThemeAnim, $"its animation library '{anims}' is in no theme of the chain", key + ".anims");
                }
                else
                {
                    if (Text(library, "rig") is { } libraryRig && libraryRig != rig)
                        Error(manifest, key, ValidationCodes.ThemeAnim, $"is on rig '{rig}' but its animation library '{anims}' is on rig '{libraryRig}'", key + ".anims");
                    if (ChainFacts(anims) is { } clips)
                    {
                        var moved = clips.AnimatedNodes.Where(n => clips.Bones.Contains(n) && !facts.Bones.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                        if (moved.Count > 0)
                            Error(manifest, key, ValidationCodes.ThemeAnim, $"lacks bone(s) {string.Join(", ", moved)} that its animation library '{anims}' moves; both must use the same bone names (the import mapping's 'rigs.<rig>.bones' applies to both)");
                    }
                }
            }
        }

        /// <summary>A humanoid faces -Z when its right hand is on +X (ARCHITECTURE §7.7: north at yaw 0).</summary>
        private void Facing(Manifest manifest, string key, ModelFacts facts)
        {
            var right = facts.BoneAt["hand_r"];
            var left = facts.BoneAt["hand_l"];
            var dx = right.X - left.X;
            var dz = right.Z - left.Z;
            if (dx > 0 && Math.Abs(dx) >= Math.Abs(dz)) return;
            var facing = Math.Abs(dx) >= Math.Abs(dz) ? "+z" : dz > 0 ? "+x" : "-x";
            Error(manifest, key, ValidationCodes.ThemeModel, $"faces {facing.ToUpperInvariant()} (its right hand is not on +X); models face -Z. Set 'facing: {facing}' in the import mapping");
        }

        private static string SuggestScale(float height)
        {
            var target = 1.6f;
            var raw = target / Math.Max(height, 1e-4f);
            var power = Math.Pow(10, Math.Round(Math.Log10(raw)));
            return $"scale: {power.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private void Library(Manifest manifest, string key, JsonElement entry)
        {
            var rig = Text(entry, "rig");
            if (rig is null) Error(manifest, key, ValidationCodes.ThemeAnim, "an animation library must name its 'rig'");
            var clips = entry.TryGetProperty("clips", out var c) && c.ValueKind == JsonValueKind.Object
                ? c.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var facts = Facts(manifest, key);
            var available = facts is null ? "" : $" (the file has: {(facts.Animations.Count == 0 ? "no animations" : string.Join(", ", facts.Animations))})";
            foreach (var id in ThemeContract.RequiredClips)
                if (!clips.ContainsKey(id))
                    Error(manifest, key, ValidationCodes.ThemeAnim, $"maps no clip to '{id}', which the client plays{available}; add it to 'clips' in the import mapping", key + ".clips");
            if (facts is null) return;
            foreach (var (id, name) in clips)
                if (!facts.Animations.Contains(name))
                    Error(manifest, key, ValidationCodes.ThemeAnim, $"clip '{id}' is '{name}', which the file does not have{available}", key + ".clips." + id);
            if (rig is not null && _rigs.TryGetValue(rig, out var bones) && facts.HasSkin)
            {
                var unknown = facts.AnimatedNodes.Where(n => facts.Bones.Contains(n) && !bones.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (unknown.Count > 0)
                    Error(manifest, key, ValidationCodes.ThemeAnim, $"moves bone(s) {string.Join(", ", unknown.Take(8))}{(unknown.Count > 8 ? ", ..." : "")} that rig '{rig}' has no bone named; map them with 'rigs.{rig}.bones' in the import mapping");
            }
        }
    }

    private static ValidationIssue Issue(ValidationSeverity severity, Manifest manifest, string token, string path, string code, string message)
    {
        var line = manifest.LineOf(token);
        return new ValidationIssue(severity, manifest.DisplayPath, line, line > 0 ? 1 : 0, path, code, message);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class Manifest
    {
        private readonly string _themeDir;
        private readonly string _text;
        private readonly JsonDocument _doc;

        private Manifest(string themeDir, string displayPath, string text, JsonDocument doc)
        {
            _themeDir = themeDir;
            DisplayPath = displayPath;
            _text = text;
            _doc = doc;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name is not ("audio" or "fallbacks") && p.Value.ValueKind == JsonValueKind.Object) Entries[p.Name] = p.Value;
        }

        public string DisplayPath { get; }

        public Dictionary<string, JsonElement> Entries { get; } = new(StringComparer.Ordinal);

        public static Manifest? Load(string themesDir, string theme)
        {
            var path = AssetManifest.PathFor(themesDir, theme);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            try
            {
                var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? new Manifest(Path.Combine(themesDir, theme), $"themes/{theme}/asset-manifest.json", text, doc) : null;
            }
            catch (JsonException) { return null; } // the content boot reports a manifest that is not JSON
        }

        public string? Fallback(string name) =>
            _doc.RootElement.TryGetProperty("fallbacks", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public string? FileOf(string key) =>
            Entries.TryGetValue(key, out var e) && Text(e, "file") is { } file ? Path.Combine(_themeDir, file.Replace('/', Path.DirectorySeparatorChar)) : null;

        public string RelativeFile(string key) => Entries.TryGetValue(key, out var e) && Text(e, "file") is { } file ? DisplayPath[..DisplayPath.LastIndexOf('/')] + "/" + file : key;

        public bool HasFile(string key) => FileOf(key) is { } f && File.Exists(f);

        public int LineOf(string token)
        {
            var at = _text.IndexOf("\"" + token + "\"", StringComparison.Ordinal);
            return at < 0 ? 0 : _text.AsSpan(0, at).Count('\n') + 1;
        }
    }
}

/// <summary>Git LFS pointer files: what a checkout holds in place of a binary until <c>git lfs pull</c> (ADR-002 row 208).</summary>
public static class LfsPointer
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("version https://git-lfs.github.com/spec/");

    /// <summary>True when <paramref name="bytes"/> start like a pointer file.</summary>
    public static bool Is(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Prefix);

    /// <summary>The message for a file that is a pointer.</summary>
    public static string Message(string path) => $"{path} is a Git LFS pointer, not the file itself; run `git lfs pull`";
}
