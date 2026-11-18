namespace Game.Shared.Definitions
{
    /// <summary>NPC talking and services (T-601): the <c>dialogue</c> section of <c>balance.yaml</c>.</summary>
    public sealed record DialogueBalanceDef
    {
        /// <summary>
        /// How close (Chebyshev, in cells) a player must be to start or keep a dialogue with an NPC. A <c>talk</c> from farther walks the player in first;
        /// walking farther than this from the NPC ends the dialogue and any service it opened. At least 1.
        /// </summary>
        public int TalkRange { get; init; } = 2;

        /// <summary>When true the <c>refine</c> intent works only while an NPC's <c>refine</c> service is open (an <c>open_service</c> action) and in reach.</summary>
        public bool RefineNeedsService { get; init; }
    }
}
