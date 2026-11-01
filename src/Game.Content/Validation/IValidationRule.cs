namespace Game.Content.Validation;

/// <summary>
/// One cross-reference / consistency check over a fully loaded <see cref="Registry.ContentDatabase"/>.
/// <para>
/// To add a rule: create a class in <c>Validation/Rules</c> implementing this interface and add an instance to
/// <see cref="ContentValidator.DefaultRules"/>. The validator calls <see cref="Validate"/> once per run, on every
/// registered rule, in order, and merges all issues; a rule never stops the others. A rule reads
/// <see cref="ValidationContext.Database"/> (never mutates it) and reports through
/// <see cref="ValidationContext.Error"/> / <see cref="ValidationContext.Warn"/>, naming the owning definition and the
/// YAML-key path so the report carries file, line and column. Rules must be deterministic and do no I/O beyond
/// the context (the theme manifest is provided as <see cref="ValidationContext.AssetKeys"/>).
/// The formula rule of T-103 plugs in exactly this way.
/// </para>
/// </summary>
public interface IValidationRule
{
    /// <summary>Short stable name, used when a rule crashes and in docs.</summary>
    string Name { get; }

    /// <summary>Runs the check and reports findings on <paramref name="context"/>.</summary>
    void Validate(ValidationContext context);
}
