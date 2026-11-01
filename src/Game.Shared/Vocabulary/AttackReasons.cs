namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Why combat dropped an attack (<c>AttackRefused</c>) or ended a player's auto-attack (<c>AttackStopped</c>).
    /// VOCABULARY.md: "Attack reasons".
    /// </summary>
    public static class AttackReasons
    {
        /// <summary><c>attacker_dead</c>: the attacker is dead (or has no stats to fight with).</summary>
        public const string AttackerDead = "attacker_dead";

        /// <summary><c>target_dead</c>: the target is dead.</summary>
        public const string TargetDead = "target_dead";

        /// <summary><c>target_gone</c>: the target is not on the attacker's map (left, despawned, never existed).</summary>
        public const string TargetGone = "target_gone";

        /// <summary><c>out_of_range</c>: the target is farther than the attack range (Chebyshev) when the attack is resolved.</summary>
        public const string OutOfRange = "out_of_range";

        /// <summary><c>safe_zone</c>: the attacking player, or the player being attacked, stands in a <c>safe</c> zone.</summary>
        public const string SafeZone = "safe_zone";

        /// <summary><c>invalid_target</c>: the target may not be attacked by this attacker (players target only monsters in v1).</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>unreachable</c>: no path leads into range of the target.</summary>
        public const string Unreachable = "unreachable";

        /// <summary><c>cancelled</c>: the player stopped (<c>StopAttack</c>) or gave a new move order (<c>MoveTo</c> / <c>StopMove</c>).</summary>
        public const string Cancelled = "cancelled";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            AttackerDead,
            TargetDead,
            TargetGone,
            OutOfRange,
            SafeZone,
            InvalidTarget,
            Unreachable,
            Cancelled);
    }
}
