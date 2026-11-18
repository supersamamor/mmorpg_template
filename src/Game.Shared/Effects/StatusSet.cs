using System;
using System.Collections.Generic;

namespace Game.Shared.Effects
{
    /// <summary>What an <c>apply_status</c> did to the bearer's statuses (<see cref="StatusSet.Apply"/>).</summary>
    public enum StatusChange
    {
        /// <summary>The status was not there: it starts with one stack.</summary>
        Applied,

        /// <summary>It was there below <c>max_stacks</c>: one more stack, and its end time refreshed.</summary>
        Stacked,

        /// <summary>It was there at <c>max_stacks</c>: only its end time refreshed.</summary>
        Refreshed,
    }

    /// <summary>One status on one bearer. Mutable; owned by its <see cref="StatusSet"/> and changed only through it and the runner.</summary>
    public sealed class ActiveStatus
    {
        internal ActiveStatus(CompiledStatus status) => Status = status;

        /// <summary>The status.</summary>
        public CompiledStatus Status { get; }

        /// <summary>Its id.</summary>
        public string Id => Status.Id;

        /// <summary>Stacks, 1..<c>max_stacks</c>.</summary>
        public int Stacks { get; internal set; }

        /// <summary>Simulated time it started.</summary>
        public long StartedAtMs { get; internal set; }

        /// <summary>Simulated time it ends (it expires on the first tick at or after it).</summary>
        public long EndsAtMs { get; internal set; }

        /// <summary>Simulated time its next <c>on_tick</c> is due; <see cref="long.MaxValue"/> for a status that does not tick.</summary>
        public long NextTickMs { get; internal set; }

        /// <summary>
        /// Who applied it (the latest applier when re-applied): its numbers feed the <c>on_tick</c> formulas, and its id gets the kill credit and
        /// the EXP share for tick damage.
        /// </summary>
        public EffectActor Source { get; internal set; }

        /// <summary>The formula variable <c>level</c> of the skill (or item: 1) that applied it, for its <c>on_apply</c> and <c>on_tick</c> formulas.</summary>
        public int Level { get; internal set; }
    }

    /// <summary>
    /// The statuses on one entity (ARCHITECTURE §6.3) and the stacking rule, with no I/O: every time is simulated milliseconds from the caller (the
    /// instance's tick clock). Kept in application order. <see cref="Revision"/> changes whenever something a client sees changes (a status
    /// added or removed, a stack, a new end time), so views know when to resend. Not thread-safe: one bearer, one instance thread.
    /// </summary>
    /// <remarks>
    /// <para><b>Stacking rule.</b> Applying a status the bearer does not have starts it with one stack, ending <c>duration</c> from now, first
    /// tick one <c>tick_interval_ms</c> from now. Applying it again adds a stack while below <c>max_stacks</c> (its <c>on_apply</c> runs again for
    /// that stack, so a stacked <c>modify_stat</c> counts once per stack), else only refreshes. Either way the end time becomes the later of the
    /// current end and now + the new duration (a shorter re-application never shortens it), the tick schedule stays as it was, and the newest
    /// applier becomes the <see cref="ActiveStatus.Source"/>. All stacks share one timer and end together.</para>
    /// </remarks>
    public sealed class StatusSet
    {
        private readonly List<ActiveStatus> _active = new List<ActiveStatus>();

        /// <summary>The statuses, in application order.</summary>
        public IReadOnlyList<ActiveStatus> Active => _active;

        /// <summary>Changes whenever a status starts, stacks, is refreshed or ends.</summary>
        public int Revision { get; private set; }

        /// <summary>
        /// The map whose instance clock times these statuses (set by the host when the first one starts). Statuses end when the bearer moves to
        /// another map, so a reader seeing a different map on the entity treats the set as empty until the host clears it.
        /// </summary>
        public string? MapId { get; set; }

        /// <summary>Some status has <c>prevents_move</c>.</summary>
        public bool PreventsMove { get; private set; }

        /// <summary>Some status has <c>prevents_attack</c>.</summary>
        public bool PreventsAttack { get; private set; }

        /// <summary>Some status has <c>prevents_skills</c>.</summary>
        public bool PreventsSkills { get; private set; }

        /// <summary>No status is active.</summary>
        public bool IsEmpty => _active.Count == 0;

        /// <summary>The active status <paramref name="id"/>, or null.</summary>
        public ActiveStatus? Find(string id)
        {
            for (var i = 0; i < _active.Count; i++)
                if (string.Equals(_active[i].Id, id, StringComparison.Ordinal)) return _active[i];
            return null;
        }

        /// <summary>
        /// Applies <paramref name="status"/> for <paramref name="durationMs"/> (above 0) at <paramref name="nowMs"/> by the stacking rule (class
        /// remarks). Returns the active status and what changed; the caller runs <c>on_apply</c> for <see cref="StatusChange.Applied"/> and
        /// <see cref="StatusChange.Stacked"/>.
        /// </summary>
        public ActiveStatus Apply(CompiledStatus status, EffectActor source, int level, long nowMs, long durationMs, out StatusChange change)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs), durationMs, "A status needs a duration above 0.");
            var endsAt = nowMs + durationMs;
            var active = Find(status.Id);
            if (active == null)
            {
                var interval = status.Def.TickIntervalMs;
                active = new ActiveStatus(status)
                {
                    Stacks = 1,
                    StartedAtMs = nowMs,
                    EndsAtMs = endsAt,
                    NextTickMs = interval > 0 && status.OnTick.Count > 0 ? nowMs + interval : long.MaxValue,
                };
                _active.Add(active);
                change = StatusChange.Applied;
            }
            else
            {
                var max = Math.Max(1, status.Def.MaxStacks);
                change = active.Stacks < max ? StatusChange.Stacked : StatusChange.Refreshed;
                if (change == StatusChange.Stacked) active.Stacks++;
                active.EndsAtMs = Math.Max(active.EndsAtMs, endsAt);
            }
            active.Source = source;
            active.Level = level;
            Changed();
            return active;
        }

        /// <summary>Removes <paramref name="active"/> (the caller removes its modifiers and raises the event). False when it was not here.</summary>
        public bool Remove(ActiveStatus active)
        {
            if (!_active.Remove(active)) return false;
            Changed();
            return true;
        }

        /// <summary>True when <paramref name="active"/> is still in this set.</summary>
        public bool Contains(ActiveStatus active) => _active.Contains(active);

        /// <summary>Copies the active statuses into <paramref name="into"/> (cleared first), so a caller can change the set while walking the copy.</summary>
        public void CopyTo(List<ActiveStatus> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            into.AddRange(_active);
        }

        private void Changed()
        {
            Revision++;
            bool move = false, attack = false, skills = false;
            for (var i = 0; i < _active.Count; i++)
            {
                var def = _active[i].Status.Def;
                move |= def.PreventsMove;
                attack |= def.PreventsAttack;
                skills |= def.PreventsSkills;
            }
            PreventsMove = move;
            PreventsAttack = attack;
            PreventsSkills = skills;
        }
    }
}
