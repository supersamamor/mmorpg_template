using System;

namespace Game.Shared.Stats
{
    /// <summary>
    /// The unmodified inputs of a stat sheet: base primary stats (allocated points) and the level context
    /// derived formulas read (<c>base_level</c>, <c>job_level</c>). Not thread-safe.
    /// </summary>
    public sealed class StatBlock
    {
        private readonly int[] _primary = new int[StatIndex.PrimaryCount];
        private int _baseLevel;
        private int _jobLevel;

        /// <summary>Incremented on every change that alters a value.</summary>
        public long Version { get; private set; }

        /// <summary>Base level (<c>base_level</c> in formulas).</summary>
        public int BaseLevel
        {
            get => _baseLevel;
            set
            {
                if (_baseLevel == value) return;
                _baseLevel = value;
                Version++;
            }
        }

        /// <summary>Job level (<c>job_level</c> in formulas).</summary>
        public int JobLevel
        {
            get => _jobLevel;
            set
            {
                if (_jobLevel == value) return;
                _jobLevel = value;
                Version++;
            }
        }

        /// <summary>Base value of a primary stat by index (<c>0 .. StatIndex.PrimaryCount-1</c>).</summary>
        /// <param name="primaryIndex">Primary stat index.</param>
        public int this[int primaryIndex]
        {
            get => _primary[CheckPrimary(primaryIndex)];
            set
            {
                if (_primary[CheckPrimary(primaryIndex)] == value) return;
                _primary[primaryIndex] = value;
                Version++;
            }
        }

        /// <summary>Base value of a primary stat by id; derived stat ids throw <see cref="ArgumentException"/>.</summary>
        /// <param name="primaryStat">Primary stat id, e.g. <c>str</c>.</param>
        public int this[string primaryStat]
        {
            get => this[PrimaryIndexOf(primaryStat)];
            set => this[PrimaryIndexOf(primaryStat)] = value;
        }

        private static int PrimaryIndexOf(string stat)
        {
            var index = StatIndex.IndexOf(stat);
            if (!StatIndex.IsPrimary(index)) throw new ArgumentException("'" + stat + "' is not a primary stat.", nameof(stat));
            return index;
        }

        private static int CheckPrimary(int index)
        {
            if (!StatIndex.IsPrimary(index)) throw new ArgumentOutOfRangeException(nameof(index));
            return index;
        }
    }
}
