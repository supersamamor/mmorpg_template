using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>
/// <c>close_report</c> (T-901, <c>support</c>): staff handled a player's report of a chat line. In one transaction the report is closed and its audit row written (rule 10); before and after
/// hold the report's number, its status and the ids in it, never the line itself (the report keeps that, and goes when either account is erased, or when it has been closed for
/// <c>--chat-closed-report-days</c>). What staff did about it (a mute, nothing) is its own command and its own row.
/// </summary>
public sealed class CloseReportCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.CloseReport;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Number(call.Body, AdminCommandFields.ReportId, 1, long.MaxValue, out long report)) return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.ChatReports is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var target = report.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var change = await store.CloseReportAsync(report, road.Now, r => road.Draft(call, Action, AdminTargetKinds.ChatReport, target,
            new Dictionary<string, object?>
            {
                ["status"] = r.Status, ["sender_account_id"] = r.SenderAccountId.ToString(), ["sender_character_id"] = r.SenderCharacterId.ToString(),
                ["reporter_account_id"] = r.ReporterAccountId.ToString(),
            },
            new Dictionary<string, object?> { ["status"] = ChatReportStatuses.Closed }), CancellationToken.None).ConfigureAwait(false);   // once committed, the answer must go out
        if (change.Error is { } error) return AdminResult.Refused(error);
        return Moderation.Result(AdminOutcomes.Done, change.AuditId, AdminTargetKinds.ChatReport, target, new() { ["closed_at"] = AdminFields.Iso(change.Report!.ClosedAt) });
    }
}
