namespace Game.Shared.World
{
    /// <summary>Bounds on one path search. In the game they come from <c>balance.yaml</c> <c>movement</c> (<c>BalanceDef.Movement</c>).</summary>
    public readonly struct PathLimits
    {
        /// <summary>Creates limits.</summary>
        public PathLimits(int maxPathLength, int maxNodesExpanded)
        {
            MaxPathLength = maxPathLength;
            MaxNodesExpanded = maxNodesExpanded;
        }

        /// <summary>Longest path, in steps, a search may return.</summary>
        public int MaxPathLength { get; init; }

        /// <summary>Most nodes a search may expand.</summary>
        public int MaxNodesExpanded { get; init; }
    }

    /// <summary>Outcome of <see cref="Pathfinder.FindPath"/>.</summary>
    public enum PathStatus
    {
        /// <summary>A path was found (it is empty when the start already is, or touches, the goal).</summary>
        Found = 0,

        /// <summary>The start or the goal lies outside the grid.</summary>
        OutOfBounds,

        /// <summary>The start cell is not walkable.</summary>
        StartNotWalkable,

        /// <summary>The goal cell is not walkable (and <see cref="PathOptions.PathToNeighbourIfGoalBlocked"/> is off).</summary>
        GoalNotWalkable,

        /// <summary>The goal cannot be reached from the start.</summary>
        NoPath,

        /// <summary>The search hit <see cref="PathLimits.MaxNodesExpanded"/> before reaching the goal.</summary>
        NodeLimit,

        /// <summary>A path exists but has more steps than <see cref="PathLimits.MaxPathLength"/>.</summary>
        TooLong,
    }

    /// <summary>Optional behaviour of a search.</summary>
    public enum PathOptions
    {
        /// <summary>Path exactly to the goal cell.</summary>
        None = 0,

        /// <summary>
        /// If the goal is not walkable (a target standing on a blocked tile), path to the cheapest walkable cell touching it
        /// (Chebyshev distance 1) instead. The goal itself is never entered.
        /// </summary>
        PathToNeighbourIfGoalBlocked = 1,
    }
}
