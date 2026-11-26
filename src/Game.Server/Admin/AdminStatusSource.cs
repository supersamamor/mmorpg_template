using System.Globalization;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Saving;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>
/// The server's state for <c>GET /admin/v1/status</c> (ARCHITECTURE §12.4 "Server"): players online and sessions (the gateway), every instance's tick figures and counters
/// (<see cref="MapInstance.GetStats"/>, T-213), saving (T-702), errors logged (<see cref="CountingLog"/>), and the loaded content's hash (<c>ClientBundleExporter.Hash</c>, rows 50 and 74).
/// Every part is safe to read from the request's thread.
/// </summary>
public sealed class AdminStatusSource(GameWorld world, Gateway gateway, CharacterSaves? saves, CountingLog log, string contentHash, DateTime startedAt)
{
    /// <summary>The state now.</summary>
    public AdminStatusAnswer Read()
    {
        var instances = world.Instances.Select(i =>
        {
            var stats = i.GetStats();
            var c = stats.Counters;
            return new AdminInstanceStatus(stats.MapId, stats.TickNumber, stats.Timings.P50Micros, stats.Timings.P99Micros, stats.Timings.MaxMicros,
                stats.Timings.LifetimeMaxMicros, c.SystemErrors, c.HandlerErrors, c.IntentsDroppedUnknownEntity + c.IntentsDroppedDeadEntity + c.IntentsDroppedUnrouted);
        }).ToList();
        var saved = saves?.Stats is { } s ? new AdminSaveStatus(s.Saves, s.Groups, s.Failures, s.Lost, s.Held, s.Waiting, s.AtRisk) : null;
        var serverErrors = gateway.Stats.Byes.TryGetValue(ByeReasons.ServerError, out var n) ? n : 0;
        return new AdminStatusAnswer(contentHash, startedAt.ToString("O", CultureInfo.InvariantCulture), gateway.CharacterCount, gateway.SessionCount, instances, saved,
            new AdminErrorCounts(log.Errors, log.Warnings, serverErrors));
    }
}
