using Game.Shared.Formulas;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Every formula string in content parses and uses only whitelisted variables and functions (T-103 evaluator).
/// The sources come from <see cref="FormulaSources"/>, so a new formula field is checked once it is listed there.
/// </summary>
public sealed class FormulaRule : IValidationRule
{
    /// <summary>File that holds the <see cref="Game.Shared.Definitions.FormulaSetDef"/>.</summary>
    private const string FormulasFile = "formulas.yaml";

    /// <summary>File that holds the <see cref="Game.Shared.Definitions.BalanceDef"/>.</summary>
    private const string BalanceFile = "balance.yaml";

    /// <inheritdoc />
    public string Name => "formula";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;

        foreach (var source in FormulaSources.Of(db.Formulas))
            Check(source, (path, code, message) => context.ErrorInFile(FormulasFile, path, code, message));

        foreach (var source in FormulaSources.Of(db.Balance))
            Check(source, (path, code, message) => context.ErrorInFile(BalanceFile, path, code, message));

        foreach (var skill in db.Skills.All) CheckAll(context, skill.Id, FormulaSources.Of(skill));
        foreach (var item in db.Items.All) CheckAll(context, item.Id, FormulaSources.Of(item));
        foreach (var job in db.Jobs.All) CheckAll(context, job.Id, FormulaSources.Of(job));
        foreach (var status in db.Statuses.All) CheckAll(context, status.Id, FormulaSources.Of(status));
    }

    private static void CheckAll(ValidationContext context, string ownerId, IReadOnlyList<FormulaSource> sources)
    {
        foreach (var source in sources)
            Check(source, (path, code, message) => context.Error(ownerId, path, code, message));
    }

    private static void Check(FormulaSource source, Action<string, string, string> report)
    {
        var result = Formula.Parse(source.Text, source.Symbols);
        foreach (var error in result.Errors)
            report(source.Path, error.Code, $"'{source.Text}' {error}");
    }
}
