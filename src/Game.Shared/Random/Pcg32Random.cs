using System;

namespace Game.Shared.Random
{
    /// <summary>
    /// PCG32 (PCG-XSH-RR, 64-bit state, 32-bit output) by M. E. O'Neill, <see href="https://www.pcg-random.org"/>.
    /// Hand-written so the sequence is fixed by this file, not by the runtime: <c>System.Random</c>'s algorithm differs
    /// between .NET versions and is not specified. Uses only 64-bit integer arithmetic (wrapping), so the output is
    /// identical on net10.0 and netstandard2.1, on every CPU. Not thread-safe; give each simulation its own instance.
    /// </summary>
    /// <remarks>
    /// Seeding follows the reference <c>pcg32_srandom_r(initstate, initseq)</c>, so <c>new Pcg32Random(42, 54)</c>
    /// reproduces the reference demo output (<c>0xa15c02b7, 0x7b47f409, ...</c>). <see cref="NextDouble"/> combines two
    /// outputs into 53 bits (27 + 26); <see cref="Next"/> rejects the biased low range of outputs.
    /// </remarks>
    public sealed class Pcg32Random : IRandom
    {
        private const ulong Multiplier = 6364136223846793005UL;
        private const ulong DefaultStream = 0xda3e39cb94b95bdbUL; // the reference library's default increment stream

        private ulong _state;
        private readonly ulong _increment;

        /// <summary>Creates a generator on the default stream.</summary>
        /// <param name="seed">Initial state.</param>
        public Pcg32Random(ulong seed)
            : this(seed, DefaultStream >> 1)
        {
        }

        /// <summary>Creates a generator on a chosen stream. Different streams with the same seed are independent sequences.</summary>
        /// <param name="seed">Initial state.</param>
        /// <param name="stream">Stream selector (only the low 63 bits matter).</param>
        public Pcg32Random(ulong seed, ulong stream)
        {
            _state = 0;
            _increment = (stream << 1) | 1;
            NextUInt();
            unchecked { _state += seed; }
            NextUInt();
        }

        /// <inheritdoc />
        public uint NextUInt()
        {
            unchecked
            {
                var old = _state;
                _state = old * Multiplier + _increment;
                var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
                var rot = (int)(old >> 59);
                return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
            }
        }

        /// <inheritdoc />
        public double NextDouble()
        {
            ulong high = NextUInt() >> 5; // 27 bits
            ulong low = NextUInt() >> 6;  // 26 bits
            return (high * 67108864UL + low) * (1.0 / 9007199254740992.0); // / 2^53
        }

        /// <inheritdoc />
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be positive.");
            var bound = (uint)maxExclusive;
            var threshold = unchecked(0u - bound) % bound; // 2^32 mod bound: outputs below it would bias the result
            while (true)
            {
                var r = NextUInt();
                if (r >= threshold) return (int)(r % bound);
            }
        }
    }
}
