using Game.Content.Registry;

namespace Game.Content.Validation.Rules;

/// <summary>Every id used inside a definition must exist in the registry of the type it is used as (rule 5 of CLAUDE.md).</summary>
public sealed class ReferenceRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "references";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        foreach (var r in ContentWalker.Refs(db))
        {
            var registry = RegistryFor(db, r.Kind);
            if (registry.Contains(r.Id)) continue;

            var kind = r.Kind.ToString().ToLowerInvariant();
            var elsewhere = db.ContainsId(r.Id) ? $" ('{r.Id}' exists, but is not a {kind})" : "";
            var hint = Closest(registry, r.Id);
            context.Error(r.Owner, r.Path, ValidationCodes.RefMissing,
                $"{r.Owner} refers to unknown {kind} '{r.Id}'{elsewhere}{(hint is null ? "" : $"; did you mean '{hint}'?")}");
        }
    }

    private static IRegistry RegistryFor(ContentDatabase db, RefKind kind) => kind switch
    {
        RefKind.Job => db.Jobs,
        RefKind.Skill => db.Skills,
        RefKind.Item => db.Items,
        RefKind.Monster => db.Monsters,
        RefKind.Status => db.Statuses,
        RefKind.Quest => db.Quests,
        RefKind.Npc => db.Npcs,
        RefKind.Dialogue => db.Dialogue,
        RefKind.Drop => db.Drops,
        RefKind.Map => db.Maps,
        RefKind.ExpTable => db.ExpTables,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Nearest existing id by edit distance, if reasonably close (typo help).</summary>
    private static string? Closest(IRegistry registry, string id)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in registry.Definitions.Select(d => d.Id))
        {
            var d = Distance(id, candidate);
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }
        return best is not null && bestDistance <= Math.Max(3, id.Length / 4) ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
