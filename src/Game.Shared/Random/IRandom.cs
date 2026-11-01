namespace Game.Shared.Random
{
    /// <summary>
    /// Injected random source (CLAUDE.md rule 8). Gameplay code never creates <c>System.Random</c>: it takes an
    /// <see cref="IRandom"/> so tests and replays can fix the seed. Implementations must give the same sequence for the
    /// same seed on every runtime and platform; <see cref="Pcg32Random"/> is the standard one.
    /// </summary>
    public interface IRandom
    {
        /// <summary>Next uniformly distributed 32-bit value.</summary>
        uint NextUInt();

        /// <summary>Next uniformly distributed value in [0, 1), with 53 random bits.</summary>
        double NextDouble();

        /// <summary>Next uniformly distributed integer in [0, <paramref name="maxExclusive"/>), without modulo bias.</summary>
        /// <param name="maxExclusive">Upper bound; must be positive.</param>
        int Next(int maxExclusive);
    }
}
