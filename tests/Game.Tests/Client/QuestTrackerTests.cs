using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Quests;
using Game.Content.ClientExport;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>
/// The quest tracker's size cap (T-406, T-408): the HUD corner holds the quest's name and at most one more line, whatever the quest asks; the quest log still lists every
/// objective. The ellipsis that shortens a too-wide line is the Label's own (<c>TrimEllipsis</c>, set in the Hud): it needs the engine, so the <c>-Run ui</c> layout matrix covers
/// it with the longest tracker text a quest can make.
/// </summary>
public sealed class QuestTrackerTests
{
    private const string Quest = "quest.first_hunt";

    private static DisplayBundle Bundle()
    {
        DisplayBundle.TryRead(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        return bundle!;
    }

    private static QuestBook BookWith(QuestEntryRecord entry)
    {
        var book = new QuestBook();
        book.Apply(new QuestLogMessage([entry], [], [], []));
        return book;
    }

    private static QuestObjectiveRecord[] Objectives(int count) =>
        Enumerable.Range(1, count).Select(i => new QuestObjectiveRecord(QuestObjectiveTypes.Kill, "monster.slime", i, 10)).ToArray();

    [Fact]
    public void The_tracker_shows_the_name_and_at_most_one_objective_line()
    {
        var bundle = Bundle();
        QuestBook.MaxTrackerLines.ShouldBe(2);
        foreach (var count in new[] { 1, 2, 3, 9 })
        {
            var lines = BookWith(new QuestEntryRecord(Quest, 0, Objectives(count))).TrackerText(bundle).Split('\n');
            lines.Length.ShouldBe(Math.Min(count + 1, QuestBook.MaxTrackerLines));
            lines[0].ShouldBe(bundle.Quest(Quest)?.Name ?? Quest);
            if (count > 0) lines[1].ShouldBe(QuestBook.ObjectiveLine(bundle, Objectives(count)[0]));   // the first objective is the one shown
        }
    }

    [Fact]
    public void A_held_reason_takes_the_objective_line_and_the_cap_still_holds()
    {
        var bundle = Bundle();
        var text = BookWith(new QuestEntryRecord(Quest, 0, Objectives(3), QuestReasons.InventoryFull)).TrackerText(bundle);
        var lines = text.Split('\n');
        lines.Length.ShouldBe(QuestBook.MaxTrackerLines);
        lines[1].ShouldBe(UiText.QuestHeld(QuestReasons.InventoryFull));
    }

    [Fact]
    public void Without_an_active_quest_the_tracker_is_the_quests_button()
    {
        new QuestBook().TrackerText(Bundle()).ShouldBe(UiText.QuestsButton);
    }

    [Fact]
    public void The_log_still_has_every_objective_the_tracker_leaves_out()
    {
        var bundle = Bundle();
        var book = BookWith(new QuestEntryRecord(Quest, 0, Objectives(5), QuestReasons.InventoryFull));
        book.TrackerText(bundle).Split('\n').Length.ShouldBe(QuestBook.MaxTrackerLines);
        // The quest log panel lists Active[i].Objectives one line each (QuestBook.ObjectiveLine): nothing is dropped from the model, only from the tracker's text.
        var entry = book.Find(Quest)!;
        entry.Objectives.Count.ShouldBe(5);
        entry.Held.ShouldBe(QuestReasons.InventoryFull);
        entry.Objectives.Select(o => QuestBook.ObjectiveLine(bundle, o)).Distinct().Count().ShouldBe(5);
    }
}
