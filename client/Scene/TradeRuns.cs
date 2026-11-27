using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted trade runs (T-903): two new accounts' characters at the start of the meadow, against a real server and database, through the HUD's buttons only.
/// <list type="bullet">
/// <item><c>--e2e-trade</c> (the trader): enters, asks the script for the partner, sells a draught at the merchant for some currency, taps the partner and asks it to trade from the party
/// panel, offers a draught (the picker) and the currency, locks and confirms when both offers show, and checks its bag after the swap. When the server stops (it stops itself) and the next
/// one is up, it checks its bag again, asks again, offers a draught and walks away: the trade ends <c>too_far</c> and nothing moved.</item>
/// <item><c>--e2e-trade-join &lt;trader&gt;</c> (the partner): enters, answers the ask with Trade, offers its Realign Tonic, locks and confirms when both offers show, checks its bag after
/// the swap and again on the next server, then accepts the second ask and sees it end <c>too_far</c> with nothing moved.</item>
/// </list>
/// The clients decide nothing: every check is against what the server sent.
/// </summary>
public sealed class TradeRuns(Main main, string draught, string tonic, string merchantNpc)
{
    // The content ids come from the script (--e2e-trade-ids <stack> <single> <merchant npc>): client code names none.
    private readonly string Draught = draught;
    private readonly string Tonic = tonic;

    private ClientSession Session => main.Session!;

    private WorldView World => Session.World;

    private TradePanel Panel => main.Hud!.Trade!;

    /// <summary>The trader.</summary>
    public async Task TraderAsync()
    {
        var name = await EnterAsync("Ta");
        Print($"trade: trader {name} in the world as {World.OwnId}; asking for the partner");
        Print("@ trade-second:" + name);

        // Currency first: a draught sold at the merchant.
        var merchant = await FindAsync(e => e.Kind == EntityKinds.Npc && e.DisplayKey == merchantNpc, "the merchant");
        Session.TapNpc(merchant.Id);
        await UntilAsync(() => Session.Dialogue.Open && Session.Dialogue.Choices.Any(c => c.Text.Contains("wares", StringComparison.Ordinal)), 20, "the merchant's greeting");
        Session.ChooseDialogue(Session.Dialogue.Choices.First(c => c.Text.Contains("wares", StringComparison.Ordinal)).Index);
        await SecondsAsync(1);
        Session.ShopSell(Entry(Draught).Id, 1);
        await UntilAsync(() => Session.Inventory.Currency > 0, 10, "the sale");
        var coins = Session.Inventory.Currency;
        Session.CloseDialogue();
        await SecondsAsync(1);
        var before = Describe();
        Print($"trade: trader before the trade: {before}");

        // Ask the partner: tap it, then the party panel's Trade.
        await OpenTradeAsync();
        Check(Panel.Open, "the trade window opens with the trade");
        Press(Panel.AddButton);
        await FramesAsync(2);
        Press(PickButton(Draught));
        await FramesAsync(2);
        if (Panel.Picking) Press(Panel.OfferButton);   // a stack: the quantity picker, one unit
        Panel.CurrencyField.Text = coins.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Press(Panel.SetCurrencyButton);
        await UntilAsync(() => Session.Trade.Open is { } t && t.Mine.Items.Count == 1 && t.Mine.Items[0].Quantity == 1 && t.Mine.Currency == coins, 15, "the own offer, from the server");
        await UntilAsync(() => Session.Trade.Open is { } t && t.Theirs.Items.Any(i => i.Item == Tonic), 60, "the partner's offer");
        await FramesAsync(3);
        Print($"trade: offers drawn: mine [{Panel.MineText}] theirs [{Panel.TheirsText}]");
        Check(Panel.TheirsText.Contains(main.Bundle!.NameOf(Tonic)), "the window shows what the partner gives");
        await LockAndConfirmAsync();
        var after = Describe();
        Print($"trade: trader after the swap: {after}");
        Check(Session.Inventory.Currency == 0 && Count(Tonic) == 2 && Count(Draught) == CountIn(before, Draught) - 1, "the trader gave a draught and its currency and got the tonic");

        // The server stops by itself; the next one has what the swap gave.
        await NextServerAsync();
        Check(Describe() == after, $"the next server has the trade: {Describe()} (was {after})");
        Print("trade: trader on the next server: " + Describe());

        // A second trade, ended by walking away.
        await OpenTradeAsync();
        Press(Panel.AddButton);
        await FramesAsync(2);
        Press(PickButton(Draught));
        await FramesAsync(2);
        if (Panel.Picking) Press(Panel.OfferButton);
        await UntilAsync(() => Session.Trade.Open is { } t && t.Mine.Items.Count == 1, 15, "the second offer");
        var own = World.Own!.Cell;
        Session.TapGround(Away(own), Clocks.View());
        await UntilAsync(() => Session.Trade.Open is null && Session.Trade.Ended == UiText.TradeClosed(TradeEnds.TooFar, null, false), 30, "the trade ending as the trader walks away");
        await SecondsAsync(1);
        Check(Describe() == after, "nothing moved: " + Describe());
        Print("trade: the second trade ended too_far; nothing moved");
        await LeaveAsync();
    }

    /// <summary>The partner.</summary>
    public async Task PartnerAsync(string trader)
    {
        var name = await EnterAsync("Tb");
        Print($"trade: partner {name} in the world as {World.OwnId}; waiting for {trader}'s ask");
        var before = Describe();
        await UntilAsync(() => Panel.PromptShown && Session.Trade.Ask?.Name == trader, 120, "the ask prompt");
        Press(Panel.AcceptButton);
        await UntilAsync(() => Session.Trade.Open is not null && Panel.Open, 15, "the trade window");
        Press(Panel.AddButton);
        await FramesAsync(2);
        Press(PickButton(Tonic));
        await UntilAsync(() => Session.Trade.Open is { } t && t.Mine.Items.Any(i => i.Item == Tonic), 15, "the own offer");
        await UntilAsync(() => Session.Trade.Open is { } t && t.Theirs.Items.Count == 1 && t.Theirs.Currency > 0, 60, "the trader's offer");
        var coins = Session.Trade.Open!.Theirs.Currency;
        await LockAndConfirmAsync();
        var after = Describe();
        Print($"trade: partner after the swap: {after} (before {before})");
        Check(Session.Inventory.Currency == coins && Count(Tonic) == 0 && Count(Draught) == CountIn(before, Draught) + 1, "the partner got the draught and the currency and gave the tonic");

        await NextServerAsync();
        Check(Describe() == after, $"the next server has the trade: {Describe()} (was {after})");
        Print("trade: partner on the next server: " + Describe());

        await UntilAsync(() => Panel.PromptShown && Session.Trade.Ask?.Name == trader, 120, "the second ask");
        Press(Panel.AcceptButton);
        await UntilAsync(() => Session.Trade.Open is not null, 15, "the second trade");
        await UntilAsync(() => Session.Trade.Open is null && Session.Trade.Ended == UiText.TradeClosed(TradeEnds.TooFar, trader, false), 60, "the second trade ending too_far");
        await SecondsAsync(1);
        Check(Describe() == after, "nothing moved: " + Describe());
        Print("trade: the second trade ended too_far for the partner too; nothing moved");
        await LeaveAsync();
    }

    /// <summary>Taps the partner and presses the party panel's Trade until the ask goes out and the partner accepts (it may not be in the world yet).</summary>
    private async Task OpenTradeAsync()
    {
        var clock = Stopwatch.StartNew();
        while (Session.Trade.Open is null)
        {
            if (clock.Elapsed.TotalSeconds > 150) throw new TimeoutException("no trade opened: " + Session.Ui.Flash);
            var partner = World.Entities.FirstOrDefault(e => e.Kind == EntityKinds.Player && !e.Mine);
            if (partner is null)
            {
                await SecondsAsync(1);
                continue;
            }
            Session.TapPlayer(partner.Id);
            await FramesAsync(3);
            var party = main.Hud!.Party!;
            Check(party.Open, "tapping a player opens the party panel");
            Press(party.TradeTappedButton);
            await FramesAsync(2);
            var asked = Stopwatch.StartNew();
            while (Session.Trade.Open is null && asked.Elapsed.TotalSeconds < 22) await FramesAsync(1);   // the ask waits 20 s for an answer
        }
        Print("trade: open with " + Session.Trade.Open.Name);
    }

    private async Task LockAndConfirmAsync()
    {
        Press(Panel.LockButton);
        await UntilAsync(() => Session.Trade.CanConfirm, 60, "both offers locked");
        await FramesAsync(2);
        Check(Panel.StateText.Length > 0, "the state line says what to do");
        Press(Panel.ConfirmButton);
        await UntilAsync(() => Session.Trade.Open is null && Session.Trade.Ended == UiText.TradeClosed(TradeEnds.Done, null, false), 30, "the swap");
        await UntilAsync(() => !Session.Inventory.Stale, 5, "the bag after the swap");
        await FramesAsync(3);
        Check(!Panel.Open, "the window closes after the swap");
    }

    private async Task NextServerAsync()
    {
        await UntilAsync(() => Session.Connection.Notice == ConnectionNotice.ServerShutdown, 240, "bye shutdown");
        Print("trade: bye shutdown; the client signs in again by itself when the next server is up");
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && Session.Inventory.Known, 120, "the next server");
        await SecondsAsync(1);
    }

    private async Task<string> EnterAsync(string prefix)
    {
        var accounts = Session.Accounts;
        var screen = main.Hud!.Accounts!;
        await FramesAsync(3);
        var tag = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));
        var username = "e2e-trade-" + tag;
        screen.Username.Text = username;
        screen.Password.Text = Guid.NewGuid().ToString("N");   // thrown away with the run
        Press(screen.RegisterButton);
        await UntilAsync(() => accounts.Screen == AccountScreen.Characters && !accounts.Busy, 15, "the character list after registering");
        var name = prefix + tag;
        await LookRuns.CreateAsync(main, name);
        await UntilAsync(() => screen.Rows.Count > 0 && screen.Rows[0].Play.IsVisibleInTree(), 10, "the character in the list");
        Press(screen.Rows[0].Play);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && main.LoadedMap == World.MapId && Session.Inventory.Known, 20, "entering the world");
        await FramesAsync(3);
        return name;
    }

    private async Task LeaveAsync()
    {
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        await FramesAsync(3);
        Check(Session.Trade.Open is null && Session.Trade.Ask is null, "no trade is left once signed out");
    }

    private Button PickButton(string itemId)
    {
        var name = main.Bundle!.NameOf(itemId);
        return Panel.PickButtons.FirstOrDefault(b => b.Text.StartsWith(name, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"no {name} to offer: " + string.Join(", ", Panel.PickButtons.Select(b => b.Text)));
    }

    private Game.Shared.Protocol.InventoryEntryRecord Entry(string itemId) =>
        Session.Inventory.Entries.First(e => e.Item == itemId && e.Slot is null);

    private int Count(string itemId) => Session.Inventory.Entries.Where(e => e.Item == itemId).Sum(e => e.Quantity);

    /// <summary>The bag in one line: currency, then each item's units in id order.</summary>
    private string Describe() =>
        "currency " + Session.Inventory.Currency + "; " + string.Join(", ", Session.Inventory.Entries.GroupBy(e => e.Item).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + " " + g.Sum(e => e.Quantity)));

    private static int CountIn(string described, string itemId)
    {
        var at = described.IndexOf(itemId + " ", StringComparison.Ordinal);
        if (at < 0) return 0;
        var rest = described[(at + itemId.Length + 1)..];
        var end = rest.IndexOf(',');
        return int.Parse(end < 0 ? rest : rest[..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A walkable cell well past the trade's range from <paramref name="from"/>.</summary>
    private GridCell Away(GridCell from)
    {
        var map = World.MapId is { } id && main.Bundle!.TryGetMap(id, out var data) && data is not null ? data : throw new InvalidOperationException("no map");
        foreach (var (dx, dy) in new[] { (0, 9), (9, 0), (0, -9), (-9, 0), (7, 7), (-7, 7), (7, -7), (-7, -7) })
            if (map.IsWalkable(from.X + dx, from.Y + dy)) return new GridCell(from.X + dx, from.Y + dy);
        throw new InvalidOperationException("no walkable cell away from " + from);
    }

    private async Task<EntityView> FindAsync(Func<EntityView, bool> match, string what)
    {
        EntityView? found = null;
        await UntilAsync(() => (found = World.Entities.FirstOrDefault(match)) is not null, 20, what);
        return found!;
    }

    private static void Press(Button button)
    {
        Check(button.IsVisibleInTree() && !button.Disabled, $"the button \"{button.Text}\" can be pressed");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task SecondsAsync(double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) await FramesAsync(1);
    }

    private async Task UntilAsync(Func<bool> condition, double seconds, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}; trade {(Session.Trade.Open is { } t ? t.Name + " v" + t.Version : "none")}; flash " + Session.Ui.Flash);
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
