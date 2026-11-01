using Game.Content.Registry;
using Game.Content.Validation;

namespace Game.Content.Loading;

/// <summary>
/// The one way to load and check content, used by the server at boot and by <c>Game.Tools validate</c>, so the two cannot disagree:
/// load the files, read the theme's asset manifest (asset keys and audio data), run every validation rule.
/// </summary>
public static class ContentBoot
{
    /// <summary>
    /// Loads <paramref name="contentDir"/> and validates it against the theme's asset manifest (<c>themes/</c> next to <c>content/</c>).
    /// Returns the database when there are no errors, else null; <paramref name="issues"/> holds every finding, warnings included.
    /// </summary>
    public static ContentDatabase? Load(string contentDir, string theme, out IReadOnlyList<ValidationIssue> issues)
    {
        var result = Check(contentDir, theme);
        issues = result.Issues;
        return result.Issues.Any(i => i.Severity == ValidationSeverity.Error) ? null : result.Database;
    }

    /// <summary>Like <see cref="Load"/> but always returns what was loaded (null only when loading itself failed) with the findings.</summary>
    public static ContentBootResult Check(string contentDir, string theme)
    {
        var load = ContentLoader.Load(contentDir);
        if (load.Database is null)
            return new ContentBootResult(null, ValidationReportFormatter.FromLoadErrors(load.Errors));

        var manifest = AssetManifest.PathFor(Path.Combine(Path.GetFullPath(contentDir), "..", "themes"), theme);
        IReadOnlySet<string> assetKeys;
        AudioManifest audio;
        try
        {
            assetKeys = AssetManifest.LoadKeys(manifest);
            audio = AudioManifest.Load(manifest, $"themes/{theme}/asset-manifest.json");
        }
        catch (InvalidDataException e)
        {
            return new ContentBootResult(null,
                [new ValidationIssue(ValidationSeverity.Error, "", 0, 0, "", ValidationCodes.ThemeManifest, e.Message)]);
        }

        return new ContentBootResult(load.Database, ContentValidator.Validate(load.Database, contentDir, assetKeys, audio).Issues);
    }
}

/// <summary>What <see cref="ContentBoot.Check"/> found.</summary>
/// <param name="Database">The loaded content, or null when loading failed or the theme manifest is unusable. Not usable when <paramref name="Issues"/> holds errors.</param>
/// <param name="Issues">All findings, in the order the rules produced them.</param>
public sealed record ContentBootResult(ContentDatabase? Database, IReadOnlyList<ValidationIssue> Issues);
