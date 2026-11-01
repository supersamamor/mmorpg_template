using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// A quest reward. Selected by the <c>type</c> key; the table is <see cref="Discriminators.Rewards"/>.
    /// </summary>
    public abstract record RewardDef
    {
        /// <summary>Reward type discriminator; one of <see cref="RewardTypes"/>.</summary>
        public abstract string Type { get; }
    }

    /// <summary><c>exp</c>: base and/or job EXP.</summary>
    public sealed record ExpReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.Exp;

        /// <summary>Base EXP.</summary>
        public long Base { get; init; }

        /// <summary>Job EXP.</summary>
        public long Job { get; init; }
    }

    /// <summary><c>item</c>: give items.</summary>
    public sealed record ItemReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.Item;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many. Default 1.</summary>
        public int Count { get; init; } = 1;
    }

    /// <summary><c>currency</c>: give currency (engine id <see cref="Currencies.Currency"/>).</summary>
    public sealed record CurrencyReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.Currency;

        /// <summary>Amount of currency.</summary>
        public long Amount { get; init; }
    }

    /// <summary><c>skill_point</c>: give unspent skill points.</summary>
    public sealed record SkillPointReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.SkillPoint;

        /// <summary>Points given.</summary>
        public int Amount { get; init; } = 1;
    }

    /// <summary><c>stat_point</c>: give unspent stat points.</summary>
    public sealed record StatPointReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.StatPoint;

        /// <summary>Points given.</summary>
        public int Amount { get; init; } = 1;
    }

    /// <summary><c>unlock_job</c>: allow a job change to <see cref="JobId"/> (see <see cref="JobDef.Requirements"/>).</summary>
    public sealed record UnlockJobReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.UnlockJob;

        /// <summary>Job id (<c>job.*</c>).</summary>
        public string JobId { get; init; } = string.Empty;
    }

    /// <summary><c>status</c>: apply a status to the character.</summary>
    public sealed record StatusReward : RewardDef
    {
        /// <inheritdoc />
        public override string Type => RewardTypes.Status;

        /// <summary>Status id (<c>status.*</c>).</summary>
        public string StatusId { get; init; } = string.Empty;

        /// <summary>Duration in milliseconds.</summary>
        public int DurationMs { get; init; }
    }
}
