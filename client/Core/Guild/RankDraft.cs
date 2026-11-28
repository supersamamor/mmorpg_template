using System.Collections.Generic;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Guild
{
    /// <summary>
    /// The leader's rank editor as plain data (T-810): the ranks below the leader's, highest first, as typed and ticked, until Save sends them as one <c>guild_ranks</c>. The client checks
    /// nothing that matters: how many ranks there may be, what a name may be and who may send them are the server's rules (a refusal comes back in words).
    /// <para>
    /// The server keeps members at their <b>place</b>, not with their rank: moving or removing a rank changes what the members at that place are called and may do, and those at a place the
    /// list no longer has go to its last. <see cref="MembersAt"/> says who a row would hold after Save.
    /// </para>
    /// </summary>
    public sealed class RankDraft
    {
        private readonly List<Rank> _ranks = new List<Rank>();

        /// <summary>One rank below the leader's, as the editor holds it.</summary>
        public sealed class Rank
        {
            private readonly HashSet<string> _permissions = new HashSet<string>(System.StringComparer.Ordinal);

            internal Rank(string name, IEnumerable<string> permissions)
            {
                Name = name;
                _permissions.UnionWith(permissions);
            }

            /// <summary>The name as typed.</summary>
            public string Name { get; set; }

            /// <summary>Whether <paramref name="permission"/> is ticked.</summary>
            public bool Has(string permission) => _permissions.Contains(permission);

            /// <summary>Ticks or unticks <paramref name="permission"/>.</summary>
            public void Set(string permission, bool on)
            {
                if (on) _permissions.Add(permission);
                else _permissions.Remove(permission);
            }

            /// <summary>The ticked "Guild permissions" terms, in the vocabulary's order.</summary>
            public IReadOnlyList<string> Permissions
            {
                get
                {
                    var list = new List<string>();
                    foreach (var p in GuildPermissions.All)
                        if (_permissions.Contains(p)) list.Add(p);
                    return list;
                }
            }
        }

        /// <summary>The ranks of <paramref name="guild"/> below the leader's (place 1 first).</summary>
        public static RankDraft From(GuildMessage guild)
        {
            var draft = new RankDraft();
            for (var i = 1; i < guild.Ranks.Count; i++) draft._ranks.Add(new Rank(guild.Ranks[i].Name, guild.Ranks[i].Permissions));
            return draft;
        }

        /// <summary>The ranks, place 1 first.</summary>
        public IReadOnlyList<Rank> Ranks => _ranks;

        /// <summary>A rank may go while another stays: a guild needs one below the leader's for new members.</summary>
        public bool CanRemove => _ranks.Count > 1;

        /// <summary>A new rank with no permissions at the end (it moves nobody; new members join at the last).</summary>
        public void Add(string name) => _ranks.Add(new Rank(name, System.Array.Empty<string>()));

        /// <summary>Takes the rank at <paramref name="index"/> out, unless it is the only one.</summary>
        public void Remove(int index)
        {
            if (CanRemove && index >= 0 && index < _ranks.Count) _ranks.RemoveAt(index);
        }

        /// <summary>Moves the rank at <paramref name="index"/> one up (<paramref name="by"/> -1) or down (1).</summary>
        public void Move(int index, int by)
        {
            var to = index + by;
            if (index < 0 || index >= _ranks.Count || to < 0 || to >= _ranks.Count) return;
            (_ranks[index], _ranks[to]) = (_ranks[to], _ranks[index]);
        }

        /// <summary>How many of <paramref name="guild"/>'s members the row at <paramref name="index"/> would hold after Save: those at its place, and for the last row those past it too.</summary>
        public int MembersAt(GuildMessage guild, int index)
        {
            var place = index + 1;
            var count = 0;
            foreach (var m in guild.Members)
                if (m.Rank == place || (index == _ranks.Count - 1 && m.Rank > place)) count++;
            return count;
        }

        /// <summary>The <c>guild_ranks</c> list.</summary>
        public IReadOnlyList<GuildRankRecord> ToRecords()
        {
            var list = new List<GuildRankRecord>(_ranks.Count);
            foreach (var r in _ranks) list.Add(new GuildRankRecord(r.Name, r.Permissions));
            return list;
        }
    }
}
