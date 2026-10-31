namespace Game.Shared.Definitions
{
    /// <summary>
    /// How a modifier combines with a stat (ARCHITECTURE §6.1). Serialized in snake_case:
    /// <c>flat</c>, <c>percent</c>, <c>override</c> (see <see cref="Vocabulary.ModifierOps"/>).
    /// </summary>
    public enum ModifierOp
    {
        /// <summary>Added to the base value: <c>Base + ΣFlat</c>.</summary>
        Flat,

        /// <summary>Summed and applied as <c>× (1 + ΣPercent)</c>. <c>10</c> means +10%.</summary>
        Percent,

        /// <summary>Replaces the final value.</summary>
        Override,
    }

    /// <summary>
    /// A change to one stat, from equipment, a card, a job bonus, a passive skill or a status.
    /// The engine attaches the source when it applies the modifier, so the definition has none.
    /// </summary>
    public sealed record ModifierDef
    {
        /// <summary>Creates an empty modifier (used by deserializers).</summary>
        public ModifierDef()
        {
        }

        /// <summary>Creates a modifier.</summary>
        /// <param name="stat">Stat id; see <see cref="Stat"/>.</param>
        /// <param name="op">Operation; see <see cref="Op"/>.</param>
        /// <param name="value">Formula; see <see cref="Value"/>.</param>
        public ModifierDef(string stat, ModifierOp op, string value)
        {
            Stat = stat;
            Op = op;
            Value = value;
        }

        /// <summary>Primary or derived stat id (<see cref="Vocabulary.Stats"/>), e.g. <c>str</c>, <c>atk</c>.</summary>
        public string Stat { get; init; } = string.Empty;

        /// <summary>How the value combines with the stat.</summary>
        public ModifierOp Op { get; init; }

        /// <summary>
        /// Amount as a formula string, so it can be a constant (<c>"5"</c>) or scaled
        /// (<c>"level * 2"</c>, <c>"refine * 3"</c>). Only whitelisted formula variables are allowed.
        /// </summary>
        public string Value { get; init; } = "0";
    }
}
