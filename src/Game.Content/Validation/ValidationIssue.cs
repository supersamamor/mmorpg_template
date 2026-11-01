namespace Game.Content.Validation;

/// <summary>How serious a <see cref="ValidationIssue"/> is. Only errors make <c>validate</c> fail.</summary>
public enum ValidationSeverity
{
    /// <summary>Does not fail validation; something probably unintended.</summary>
    Warning,

    /// <summary>Fails validation (exit code 1).</summary>
    Error,
}

/// <summary>
/// One finding of a validation rule.
/// </summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="File">Path relative to the content root, <c>/</c>-separated; empty if the issue is not tied to a file.</param>
/// <param name="Line">1-based line, or 0 if unknown.</param>
/// <param name="Column">1-based column, or 0 if unknown.</param>
/// <param name="Path">Location inside the document in YAML key naming, e.g. <c>rewards[1].item_id</c>.</param>
/// <param name="Code">Stable machine-readable code, see <see cref="ValidationCodes"/>. Warning codes start with <c>warn.</c>.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record ValidationIssue(
    ValidationSeverity Severity, string File, int Line, int Column, string Path, string Code, string Message)
{
    /// <summary><c>file:line:col: code: message [path]</c></summary>
    public override string ToString()
    {
        var pos = Line > 0 ? $":{Line}:{Column}" : "";
        var path = Path.Length > 0 ? $" [{Path}]" : "";
        return $"{File}{pos}: {Code}: {Message}{path}";
    }
}

/// <summary>Codes used by <see cref="ValidationIssue.Code"/>.</summary>
public static class ValidationCodes
{
    /// <summary>An id points at a definition that does not exist (or exists under another type).</summary>
    public const string RefMissing = "ref.missing";
    /// <summary>A value is not in the fixed vocabulary (stat, element, slot, ...).</summary>
    public const string VocabUnknown = "vocab.unknown";
    /// <summary>An item's <c>kind</c> does not match the block it carries.</summary>
    public const string ItemKindBlock = "item.kind_block";
    /// <summary>An asset key is not in the theme manifest.</summary>
    public const string AssetMissing = "asset.missing";
    /// <summary>A required asset key is empty.</summary>
    public const string AssetEmpty = "asset.empty";
    /// <summary>The theme manifest could not be found or read.</summary>
    public const string ThemeManifest = "theme.manifest";
    /// <summary>Skill prerequisites form a cycle.</summary>
    public const string SkillCycle = "skill.prereq_cycle";
    /// <summary>A skill prerequisite is not part of the job's (or an ancestor job's) skill tree.</summary>
    public const string SkillPrereqNotInTree = "skill.prereq_not_in_tree";
    /// <summary>A prerequisite level exceeds the prerequisite skill's max level.</summary>
    public const string SkillPrereqLevel = "skill.prereq_level";
    /// <summary>The parent job chain loops.</summary>
    public const string JobCycle = "job.parent_cycle";
    /// <summary>A dialogue's start or next points at a node that does not exist.</summary>
    public const string DialogueDangling = "dialogue.dangling";
    /// <summary>A number in <c>balance.yaml</c> is out of range or inconsistent with another.</summary>
    public const string BalanceRange = "balance.range";
    /// <summary>A warp, warp target or NPC is on a cell that is out of bounds or not walkable.</summary>
    public const string GridCellBlocked = "grid.cell_blocked";
    /// <summary>A spawn or zone rectangle is out of bounds, empty, or (spawns) has no walkable cell.</summary>
    public const string GridRect = "grid.rect_invalid";
    /// <summary>A spawn count or respawn time is out of range.</summary>
    public const string GridValueRange = "grid.value_range";
    /// <summary>Cells carry flag bits that are not in <c>CellFlags</c>.</summary>
    public const string GridUnknownFlags = "grid.flags_unknown";
    /// <summary>Two warps share a cell.</summary>
    public const string GridWarpDuplicate = "grid.warp_duplicate";
    /// <summary>A rule threw; this is a validator bug, reported so it cannot pass silently.</summary>
    public const string RuleCrashed = "validator.rule_crashed";

    /// <summary>File name differs from the id (warning).</summary>
    public const string WarnFileName = "warn.file_name";
    /// <summary>Item referenced by nothing (warning).</summary>
    public const string WarnUnusedItem = "warn.unused_item";
    /// <summary>Status referenced by nothing (warning).</summary>
    public const string WarnUnusedStatus = "warn.unused_status";
    /// <summary>Skill not in any job tree (warning).</summary>
    public const string WarnSkillNoJob = "warn.skill_no_job";
    /// <summary>Dialogue node not reachable from start (warning).</summary>
    public const string WarnDialogueUnreachable = "warn.dialogue_unreachable";
}
