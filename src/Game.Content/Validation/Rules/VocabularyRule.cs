using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Values that come from a fixed vocabulary (<c>Game.Shared/Vocabulary</c>) must be members: stats, elements,
/// equip slots, weapon types, monster race/size, animation ids. Effect, objective and reward <c>type</c>s are
/// checked by the loader's discriminator tables, so they cannot reach this rule unknown.
/// </summary>
public sealed class VocabularyRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "vocabulary";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;

        foreach (var m in ContentWalker.Modifiers(db))
            Check(context, m.Owner, m.Path + ".stat", m.Modifier.Stat, Stats.All, "stat");

        foreach (var e in ContentWalker.Effects(db))
            if (e.Effect is DamageEffect { Element: { } el })
                Check(context, e.Owner, e.Path + ".element", el, Elements.All, "element");

        foreach (var job in db.Jobs.All)
            CheckList(context, job.Id, "weapon_types", job.WeaponTypes, WeaponTypes.All, "weapon type");

        foreach (var skill in db.Skills.All)
        {
            CheckList(context, skill.Id, "weapon_types", skill.WeaponTypes, WeaponTypes.All, "weapon type");
            if (skill.AnimationId is { } anim) Check(context, skill.Id, "animation_id", anim, AnimationIds.All, "animation id");
        }

        foreach (var item in db.Items.All)
        {
            if (item.Equip is { } eq)
            {
                CheckList(context, item.Id, "equip.slots", eq.Slots, EquipSlots.All, "equip slot");
                if (eq.WeaponType is { } wt) Check(context, item.Id, "equip.weapon_type", wt, WeaponTypes.All, "weapon type");
                if (eq.Element is { } el) Check(context, item.Id, "equip.element", el, Elements.All, "element");
            }
            if (item.Card is { } card) CheckList(context, item.Id, "card.slots", card.Slots, EquipSlots.All, "equip slot");
        }

        foreach (var m in db.Monsters.All)
        {
            Check(context, m.Id, "race", m.Race, MonsterRaces.All, "monster race");
            Check(context, m.Id, "size", m.Size, MonsterSizes.All, "monster size");
            Check(context, m.Id, "element", m.Element, Elements.All, "element");
            foreach (var stat in m.Stats.Keys) Check(context, m.Id, "stats." + stat, stat, Stats.All, "stat");
        }

        foreach (var (attacker, row) in db.Elements.Multipliers)
        {
            CheckInFile(context, attacker, "multipliers." + attacker);
            foreach (var defender in row.Keys) CheckInFile(context, defender, $"multipliers.{attacker}.{defender}");
        }
    }

    private static void CheckInFile(ValidationContext c, string value, string path)
    {
        if (!Elements.All.Contains(value))
            c.ErrorInFile("elements.yaml", path, ValidationCodes.VocabUnknown, $"Unknown element '{value}'. Known: {string.Join(", ", Elements.All)}");
    }

    private static void CheckList(ValidationContext c, string owner, string path, IReadOnlyList<string> values, VocabularySet set, string what)
    {
        for (var i = 0; i < values.Count; i++) Check(c, owner, $"{path}[{i}]", values[i], set, what);
    }

    private static void Check(ValidationContext c, string owner, string path, string value, VocabularySet set, string what)
    {
        if (!set.Contains(value))
            c.Error(owner, path, ValidationCodes.VocabUnknown, $"Unknown {what} '{value}'. Known: {string.Join(", ", set)}");
    }
}
