namespace Game.Shared.Combat
{
    /// <summary>Outcome of one <see cref="CombatResolver.Resolve"/> call. A value type, so resolving allocates nothing.</summary>
    public readonly struct AttackResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="hit">Whether the attack landed.</param>
        /// <param name="crit">Whether it was critical.</param>
        /// <param name="element">Attack element id.</param>
        /// <param name="multiplier">Element table multiplier.</param>
        /// <param name="damage">Final damage.</param>
        /// <param name="breakdown">The breakdown, if one was requested.</param>
        public AttackResult(bool hit, bool crit, string element, double multiplier, int damage, DamageBreakdown? breakdown)
        {
            Hit = hit;
            Crit = crit;
            Element = element;
            Multiplier = multiplier;
            Damage = damage;
            Breakdown = breakdown;
        }

        /// <summary>True if the attack landed. A miss has <see cref="Damage"/> 0.</summary>
        public bool Hit { get; }

        /// <summary>True if the hit was critical.</summary>
        public bool Crit { get; }

        /// <summary>Attack element id (<see cref="AttackContext.Element"/>).</summary>
        public string Element { get; }

        /// <summary>Element table multiplier of <see cref="Element"/> against the defending element (1 normal, 0 immune, negative heals).</summary>
        public double Multiplier { get; }

        /// <summary>Final damage. 0 on a miss or an immune element; negative when the element heals the target.</summary>
        public int Damage { get; }

        /// <summary>The <see cref="AttackContext.Breakdown"/> that was filled, or null when recording was off.</summary>
        public DamageBreakdown? Breakdown { get; }
    }
}
