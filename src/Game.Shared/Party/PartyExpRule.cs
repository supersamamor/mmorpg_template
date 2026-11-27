using System;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Shared.Party
{
    /// <summary>
    /// How a party shares a kill's EXP (T-902, ARCHITECTURE §6.9): pure, so the server and the golden tests run the same numbers. The server first splits the monster's EXP by damage
    /// between <b>parties and players without one</b>, exactly as before parties (a party counts as one damage dealer with its members' damage added up), then gives each party's part
    /// to the members who share it: <see cref="Pool"/> (<c>formulas.yaml</c> <c>party_exp</c>) split evenly (<see cref="Even"/>). Who shares (alive, on the instance, within
    /// <see cref="PartyBalanceDef.ExpRange"/>) is the server's; whether they may (<see cref="Shares"/>) is the level-gap rule.
    /// Immutable and thread-safe; each caller brings its own <see cref="FormulaContext"/> (<see cref="NewContext"/>).
    /// </summary>
    public sealed class PartyExpRule
    {
        private readonly Formula _pool;

        /// <summary>Compiles <paramref name="formula"/> (<c>party_exp</c>, with <see cref="FormulaSymbols.PartyShare"/>) for <paramref name="balance"/>'s numbers.</summary>
        public PartyExpRule(string formula, PartyBalanceDef balance)
        {
            Balance = balance ?? throw new ArgumentNullException(nameof(balance));
            _pool = Formula.Compile(formula, FormulaSymbols.PartyShare);
        }

        /// <summary>The rule of a loaded formula set and balance.</summary>
        public static PartyExpRule Of(FormulaSetDef formulas, BalanceDef balance) => new PartyExpRule(formulas.PartyExp, balance.Party);

        /// <summary>No bonus (<c>exp</c>) with the default numbers: what combat uses when it is given no rule.</summary>
        public static PartyExpRule Neutral { get; } = new PartyExpRule(FormulaVariables.Exp, new PartyBalanceDef());

        /// <summary>The <c>party</c> numbers.</summary>
        public PartyBalanceDef Balance { get; }

        /// <summary>A context for <see cref="Pool"/>; not thread-safe, so one per caller.</summary>
        public FormulaContext NewContext() => new FormulaContext(FormulaSymbols.PartyShare);

        /// <summary>The level-gap rule: true when members whose base levels differ by <paramref name="levelSpread"/> share a kill.</summary>
        public bool Shares(int levelSpread) => levelSpread <= Balance.ExpLevelGap;

        /// <summary>The EXP <paramref name="shareCount"/> members split from the party's part <paramref name="exp"/>: <c>party_exp</c>, floored, never below 0.</summary>
        public long Pool(long exp, int shareCount, int levelSpread, FormulaContext context)
        {
            if (exp <= 0 || shareCount < 1) return 0;
            context[FormulaVariables.Exp] = exp;
            context[FormulaVariables.ShareCount] = shareCount;
            context[FormulaVariables.LevelSpread] = levelSpread;
            var value = Math.Floor(_pool.Evaluate(context) + 1e-9);   // as the stat sheet: 4.9999999999 is 5
            if (double.IsNaN(value) || value <= 0) return 0;
            return value >= long.MaxValue ? long.MaxValue : (long)value;
        }

        /// <summary>
        /// Splits <paramref name="total"/> evenly over <paramref name="shares"/>: each gets <c>total / n</c>, and the <c>total mod n</c> left over go one each to the first ones (the
        /// server lists the party's members in party order, the leader first). The shares add up to exactly the total; 0 or less gives nothing.
        /// </summary>
        public static void Even(long total, Span<long> shares)
        {
            shares.Clear();
            if (total <= 0 || shares.Length == 0) return;
            var each = total / shares.Length;
            var left = total % shares.Length;
            for (var i = 0; i < shares.Length; i++) shares[i] = each + (i < left ? 1 : 0);
        }
    }
}
