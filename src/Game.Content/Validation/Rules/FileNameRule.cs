namespace Game.Content.Validation.Rules;

/// <summary>
/// A file holding exactly one definition should be named after its id without the namespace
/// (<c>item.red_potion</c> → <c>red_potion.yaml</c>). Warning, not error: the id inside the file is what counts,
/// so a mismatch never breaks the game, but it makes files hard to find and renames silently drift.
/// Files with several definitions (e.g. <c>exp_tables.yaml</c>) are exempt.
/// </summary>
public sealed class FileNameRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "file-names";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        var defs = db.Registries.SelectMany(r => r.Definitions)
            .Select(d => (d.Id, Source: db.SourceOf(d.Id)))
            .Where(x => x.Source is not null)
            .ToList();

        foreach (var group in defs.GroupBy(x => x.Source!.Value.File))
        {
            if (group.Count() != 1) continue;
            var id = group.First().Id;
            var expected = id[(id.IndexOf('.') + 1)..];
            var actual = Path.GetFileNameWithoutExtension(group.Key);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                context.Warn(id, "id", ValidationCodes.WarnFileName, $"File name '{actual}' does not match id '{id}' (expected '{expected}.yaml')");
        }
    }
}
