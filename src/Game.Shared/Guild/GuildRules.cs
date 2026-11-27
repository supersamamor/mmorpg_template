using System;
using System.Collections.Generic;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Shared.Guild
{
    /// <summary>
    /// The guild rules a member meets (T-904, ARCHITECTURE §6.11). Pure: the server's store runs them inside the transaction that has the guild row locked, on the ranks and members it just
    /// read, so two officers on two servers can never both pass on a state only one of them saw. Rank places count down from the leader: 0 is the leader, 1 the highest rank below it.
    /// Every check answers a "Guild reasons" term, or null when the act is allowed.
    /// </summary>
    public static class GuildRules
    {
        /// <summary>The leader rank's place.</summary>
        public const int LeaderRank = 0;

        /// <summary>True when rank <paramref name="rank"/> of <paramref name="ranks"/> holds <paramref name="permission"/> (the leader holds all).</summary>
        public static bool Has(IReadOnlyList<GuildRankRecord> ranks, int rank, string permission)
        {
            if (rank == LeaderRank) return true;
            if (rank < 0 || rank >= ranks.Count) return false;
            foreach (var p in ranks[rank].Permissions)
                if (string.Equals(p, permission, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>May a member of rank <paramref name="actor"/> invite?</summary>
        public static string? MayInvite(IReadOnlyList<GuildRankRecord> ranks, int actor) => Has(ranks, actor, GuildPermissions.Invite) ? null : GuildReasons.NotAllowed;

        /// <summary>May a member of rank <paramref name="actor"/> write the notice?</summary>
        public static string? MayEditNotice(IReadOnlyList<GuildRankRecord> ranks, int actor) => Has(ranks, actor, GuildPermissions.EditNotice) ? null : GuildReasons.NotAllowed;

        /// <summary>May a member of rank <paramref name="actor"/> kick one of rank <paramref name="target"/>? Only one of a lower rank (a higher place).</summary>
        public static string? MayKick(IReadOnlyList<GuildRankRecord> ranks, int actor, int target)
        {
            if (!Has(ranks, actor, GuildPermissions.Kick)) return GuildReasons.NotAllowed;
            return target <= actor ? GuildReasons.RankTooHigh : null;
        }

        /// <summary>
        /// May a member of rank <paramref name="actor"/> give one of rank <paramref name="target"/> the rank <paramref name="rank"/>? The member must be below the actor, and so must the new
        /// rank; the leader rank moves only by a hand-over.
        /// </summary>
        public static string? MaySetRank(IReadOnlyList<GuildRankRecord> ranks, int actor, int target, int rank)
        {
            if (!Has(ranks, actor, GuildPermissions.SetRank)) return GuildReasons.NotAllowed;
            if (rank <= LeaderRank || rank >= ranks.Count) return GuildReasons.InvalidRanks;
            return target <= actor || rank <= actor ? GuildReasons.RankTooHigh : null;
        }

        /// <summary>Only the leader names and orders the ranks.</summary>
        public static string? MayEditRanks(int actor) => actor == LeaderRank ? null : GuildReasons.NotAllowed;

        /// <summary>Only the leader hands the leader rank over.</summary>
        public static string? MayLead(int actor) => actor == LeaderRank ? null : GuildReasons.NotAllowed;

        /// <summary>
        /// The ranks below the leader as <paramref name="asked"/>, or a refusal: between 1 and <c>guild.max_ranks</c> - 1 of them, each name cleaned by <paramref name="cleanName"/> (null refuses),
        /// each permission a term, none twice. The result has the leader's rank (kept as it is) first.
        /// </summary>
        public static string? CheckRanks(GuildBalanceDef def, GuildRankRecord leader, IReadOnlyList<GuildRankRecord>? asked, Func<string, string?> cleanName, out List<GuildRankRecord> ranks)
        {
            ranks = new List<GuildRankRecord> { leader };
            if (asked is null || asked.Count < 1 || asked.Count > def.MaxRanks - 1) return GuildReasons.InvalidRanks;
            foreach (var r in asked)
            {
                if (r?.Name is null || cleanName(r.Name) is not { } name) return GuildReasons.InvalidRanks;
                var permissions = new List<string>();
                foreach (var p in r.Permissions ?? Array.Empty<string>())
                {
                    if (!GuildPermissions.All.Contains(p)) return GuildReasons.InvalidRanks;
                    if (!permissions.Contains(p)) permissions.Add(p);
                }
                ranks.Add(new GuildRankRecord(name, permissions));
            }
            return null;
        }

        /// <summary>
        /// Who takes over when the leader goes (leaves, is erased or deleted): the member of the highest rank (lowest place), the earliest to join among them; none when it was alone, and then
        /// the guild ends. <paramref name="members"/> are the others, as (rank place, joined order).
        /// </summary>
        public static int Successor(IReadOnlyList<(int Rank, long Joined)> members)
        {
            var best = -1;
            for (var i = 0; i < members.Count; i++)
                if (best < 0 || members[i].Rank < members[best].Rank || (members[i].Rank == members[best].Rank && members[i].Joined < members[best].Joined)) best = i;
            return best;
        }

        /// <summary>The naming rules of guilds: those of characters (letters, separators) with the guild's own lengths and extra separators (<c>guild.name_separators</c>, a space).</summary>
        public static CharacterNameRules NameRules(CharactersBalanceDef characters, GuildBalanceDef guild)
        {
            var separators = new List<string>(characters.NameSeparators);
            foreach (var s in guild.NameSeparators)
                if (!separators.Contains(s)) separators.Add(s);
            return new CharacterNameRules(new CharactersBalanceDef
            {
                MaxSlots = characters.MaxSlots, NameMinLength = guild.NameMinLength, NameMaxLength = guild.NameMaxLength, NameLetters = characters.NameLetters, NameSeparators = separators,
            });
        }
    }
}
