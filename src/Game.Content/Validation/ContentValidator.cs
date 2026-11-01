using Game.Content.Registry;
using Game.Content.Validation.Rules;

namespace Game.Content.Validation;

/// <summary>Runs every registered <see cref="IValidationRule"/> over a loaded content set.</summary>
public static class ContentValidator
{
    /// <summary>The rules run by default. Add new rules here (see <see cref="IValidationRule"/>).</summary>
    public static IReadOnlyList<IValidationRule> DefaultRules { get; } = new IValidationRule[]
    {
        new ReferenceRule(),
        new VocabularyRule(),
        new ItemKindRule(),
        new AssetKeyRule(),
        new SkillTreeRule(),
        new JobChainRule(),
        new DialogueRule(),
        new FormulaRule(),
        new BalanceRule(),
        new AppearanceRule(),
        new FileNameRule(),
        new MapGridRule(),
        new AudioRule(),
        new ModifierRule(),
        new UnusedRule(),
    };

    /// <summary>Validates with <see cref="DefaultRules"/>.</summary>
    public static ValidationReport Validate(ContentDatabase database, string contentRoot, IReadOnlySet<string> assetKeys, AudioManifest? audio = null) =>
        Validate(database, contentRoot, assetKeys, DefaultRules, audio);

    /// <summary>Validates with an explicit rule list (tests, or tools that add rules).</summary>
    public static ValidationReport Validate(
        ContentDatabase database, string contentRoot, IReadOnlySet<string> assetKeys, IEnumerable<IValidationRule> rules,
        AudioManifest? audio = null)
    {
        var context = new ValidationContext(database, contentRoot, assetKeys, audio);
        foreach (var rule in rules)
        {
            try { rule.Validate(context); }
            catch (Exception e)
            {
                context.ErrorGlobal(ValidationCodes.RuleCrashed, $"Rule '{rule.Name}' crashed: {e.GetType().Name}: {e.Message}");
            }
        }
        return new ValidationReport(context.Issues);
    }
}
