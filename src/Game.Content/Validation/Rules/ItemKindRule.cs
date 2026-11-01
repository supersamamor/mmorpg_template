using Game.Shared.Definitions;

namespace Game.Content.Validation.Rules;

/// <summary>An item carries exactly the kind-specific block that matches its <c>kind</c>, and no other.</summary>
public sealed class ItemKindRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "item-kind";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        foreach (var item in context.Database.Items.All)
        {
            var kind = item.Kind.ToString().ToLowerInvariant();
            Expect(context, item, "consumable", item.Consumable is not null, item.Kind == ItemKind.Consumable, kind);
            Expect(context, item, "equip", item.Equip is not null, item.Kind == ItemKind.Equip, kind);
            Expect(context, item, "card", item.Card is not null, item.Kind == ItemKind.Card, kind);
        }
    }

    private static void Expect(ValidationContext c, ItemDef item, string block, bool present, bool wanted, string kind)
    {
        if (wanted && !present)
            c.Error(item.Id, "kind", ValidationCodes.ItemKindBlock, $"{item.Id} has kind '{kind}' but no '{block}' block");
        else if (!wanted && present)
            c.Error(item.Id, block, ValidationCodes.ItemKindBlock, $"{item.Id} has kind '{kind}' but carries a '{block}' block");
    }
}
