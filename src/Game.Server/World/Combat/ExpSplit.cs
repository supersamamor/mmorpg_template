namespace Game.Server.World.Combat;

/// <summary>
/// Splits a monster's EXP between the players who damaged it, in proportion to the damage each dealt, with integer math only and nothing
/// lost or created: the shares always add up to exactly the total (largest remainder method).
/// </summary>
/// <remarks>
/// <para>For a total <c>T</c> and damage <c>d_i</c> with sum <c>D</c>: every attacker first gets <c>floor(T × d_i / D)</c>. The
/// <c>R = T − Σ floor</c> EXP left over (fewer than the number of attackers) go one each to the <c>R</c> attackers with the largest
/// remainder <c>(T × d_i) mod D</c>; equal remainders go to the attacker listed first, which is the one who hit first.</para>
/// <para>Example: 10 EXP, damage 5 / 5 / 5 → 3 / 3 / 3 and 1 left; all remainders are equal, so the first hitter gets it: 4 / 3 / 3.
/// Products are computed in 128 bits, so no total or damage overflows.</para>
/// </remarks>
public static class ExpSplit
{
    /// <summary>
    /// Writes each attacker's share of <paramref name="total"/> into <paramref name="shares"/> (same length as <paramref name="damage"/>).
    /// Damage of 0 or less earns nothing. With no positive damage, or a total of 0 or less, every share is 0. Allocates nothing.
    /// </summary>
    public static void Split(long total, ReadOnlySpan<long> damage, Span<long> shares)
    {
        if (shares.Length != damage.Length) throw new ArgumentException("shares must be as long as damage.", nameof(shares));
        shares.Clear();
        Int128 sum = 0;
        foreach (var d in damage)
            if (d > 0) sum += d;
        if (total <= 0 || sum == 0) return;

        long given = 0;
        for (var i = 0; i < damage.Length; i++)
        {
            if (damage[i] <= 0) continue;
            shares[i] = (long)(total * (Int128)damage[i] / sum);
            given += shares[i];
        }

        // Hand out the leftover one at a time to the largest remainder not yet topped up; the strict > keeps the earliest attacker on ties.
        for (var left = total - given; left > 0; left--)
        {
            var best = -1;
            Int128 bestRemainder = -1;
            for (var i = 0; i < damage.Length; i++)
            {
                if (damage[i] <= 0) continue;
                var product = total * (Int128)damage[i];
                if (shares[i] * sum > product) continue;   // already topped up (share is above its floor)
                var remainder = product % sum;
                if (remainder > bestRemainder)
                {
                    best = i;
                    bestRemainder = remainder;
                }
            }
            shares[best]++;
        }
    }
}
