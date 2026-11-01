namespace Game.Shared.Combat
{
    /// <summary>
    /// A hook that scales damage after the element multiplier, e.g. "+20% against large" or "+15% against plant"
    /// from cards. The numbers come from content (cards feed this list in Phase 4); the core ships none.
    /// Implementations must be pure and deterministic: no randomness, no state changes.
    /// </summary>
    public interface IDamageModifier
    {
        /// <summary>Step name recorded in the <see cref="DamageBreakdown"/>, e.g. the source card id.</summary>
        string Name { get; }

        /// <summary>
        /// Multiplier for this attack (1 = no change). Read <see cref="AttackContext.DefenderSize"/>,
        /// <see cref="AttackContext.DefenderRace"/>, <see cref="AttackContext.Element"/> and the like to decide.
        /// </summary>
        /// <param name="context">The attack being resolved; do not modify it.</param>
        double GetMultiplier(AttackContext context);
    }
}
