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
/// The scripted guild runs (T-904), against a server with a database, two clients on one map, each with an account of its own:
/// <list type="bullet">
/// <item><c>--e2e-guild</c> (the founder): enters, founds a guild through the guild panel (a name, the last emblem of the picker), asks the script for the second client, learns its name from
/// its first map line, invites it by name through the panel, talks with it on the guild channel, writes the notice, promotes it with the row's Up, and sees it leave.</item>
/// <item><c>--e2e-guild-join &lt;founder&gt;</c> (the member): enters, says a map line, joins through the invite prompt, answers on the guild channel, sees the notice and its new rank,
/// then leaves through the panel.</item>
/// </list>
/// T-905 adds the storage: the member (its rank may deposit, not take out) sells a draught at the merchant (<c>--e2e-guild-ids &lt;merchant npc&gt;</c>), puts the first stack of its bag and the
/// coins into the storage through the window, is refused when it tries to take one out, and tells the founder on the guild channel; the founder takes both out through the window and
/// reads both moves in the log.
/// T-810: before inviting, the founder uses the rank editor (an empty name is refused in words; the last rank loses the deposit permission, then is renamed and given it back), so the
/// member joins at the renamed rank and deposits by it. Later the member keeps the storage open with its renewals stopped, so only the server's push can show it the coin the founder then
/// puts in.
/// What is said carries the word <c>e2esaid</c>, which the script looks for in the server's log: it must not be there.
/// </summary>
public sealed class GuildRuns
{
    private const string Said = ChatRuns.Said;
    private const string Notice = "e2esaid meet at the well";
    private const string Stored = Said + " stored";
    private const string Seen = Said + " seen";
    private const string Renamed = "Recruit";
    private static readonly string OneCoin = 1.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private readonly Main _main;
    private readonly Func<Task> _shot;
    private readonly string? _merchantNpc;

    /// <summary>Prepares the runs; <paramref name="merchantNpc"/> (the member's) is the script's content id of the merchant.</summary>
    public GuildRuns(Main main, Func<Task> shot, string? merchantNpc = null)
    {
        _main = main;
        _shot = shot;
        _merchantNpc = merchantNpc;
    }

    private ClientSession Session => _main.Session!;

    private GuildPanel Panel => _main.Hud!.Guild!;

    private GuildStoragePanel Storage => _main.Hud!.GuildStorage!;

    private async Task OpenGuildPanelAsync()
    {
        Press(_main.Hud!.Chat!.OpenButton);
        await FramesAsync(2);
        Press(_main.Hud!.Chat!.GuildButton);
        await FramesAsync(2);
    }

    /// <summary>T-810: the leader's rank editor: a refusal in words, then the last rank without <c>storage_deposit</c>, then renamed and given it.</summary>
    private async Task EditRanksAsync()
    {
        Check(Panel.RanksButton.IsVisibleInTree(), "the leader has the Ranks button");
        Press(Panel.RanksButton);
        await FramesAsync(2);
        var editor = _main.Hud!.GuildRanks!;
        Check(editor.Open && !Panel.Open && editor.Count == Session.Guild.State.Ranks.Count - 1, "the editor shows the ranks below the leader's");
        var last = editor.Count - 1;
        var name = editor.NameFieldOf(last).Text;
        editor.TypeName(last, " ");
        Press(editor.SaveButton);
        var refused = UiText.GuildRefused(GuildReasons.InvalidRanks, null, null);
        await UntilAsync(() => editor.Status == refused, 15, "the empty name refused in words");
        Check(Session.Guild.State.Ranks[^1].Name == name, "nothing changed");

        editor.TypeName(last, name);
        editor.BoxOf(last, GuildPermissions.StorageDeposit).ButtonPressed = false;
        Press(editor.SaveButton);
        await UntilAsync(() => editor.Status == UiText.GuildRanksSaved && !Session.Guild.State.Ranks[^1].Permissions.Contains(GuildPermissions.StorageDeposit), 15, "the permission taken");

        editor.TypeName(last, Renamed);
        editor.BoxOf(last, GuildPermissions.StorageDeposit).ButtonPressed = true;
        Press(editor.SaveButton);
        await UntilAsync(() => editor.Status == UiText.GuildRanksSaved && Session.Guild.State.Ranks[^1].Name == Renamed
            && Session.Guild.State.Ranks[^1].Permissions.Contains(GuildPermissions.StorageDeposit), 15, "the rank renamed with the deposit permission");
        Print($"guild: the rank editor was refused an empty name, then renamed \"{name}\" to \"{Renamed}\" and gave it storage_deposit");
        Press(editor.CloseButton);
        await FramesAsync(2);
        await OpenGuildPanelAsync();
    }

    private async Task OpenStorageAsync()
    {
        if (!Panel.Open) await OpenGuildPanelAsync();
        Press(Panel.StorageButton);
        await UntilAsync(() => Storage.Open && Session.Guild.Storage is not null, 15, "the storage window with the storage");
        await FramesAsync(2);
    }

    /// <summary>The founder.</summary>
    public async Task FounderAsync()
    {
        var name = await EnterAsync("Fnd");
        var title = "Oak " + char.ToUpperInvariant(Tag()[0]) + Tag()[..6];
        Press(_main.Hud!.Chat!.OpenButton);
        await FramesAsync(2);
        Press(_main.Hud!.Chat!.GuildButton);
        await FramesAsync(2);
        Check(Panel.Open && Panel.FoundButton.IsVisibleInTree(), "the Guild button opens the founding form");
        Check(Panel.EmblemButtons.Count >= 2, "the theme's emblems are in the picker");
        Press(Panel.EmblemButtons[^1]);
        var emblem = Panel.ChosenEmblem!;
        Panel.NameField.Text = title;
        Press(Panel.FoundButton);
        await UntilAsync(() => Session.Guild.InGuild, 30, "the guild founded");
        Check(Session.Guild.State.Name == title && Session.Guild.State.Emblem == emblem && Session.Guild.Leading, "founded with its name and emblem, led by the founder: " + Session.Guild.State);
        await FramesAsync(2);
        Check(Panel.Text.Contains(title), "the panel shows the guild: " + Panel.Text);
        await EditRanksAsync();
        Print($"guild: {name} founded \"{title}\" with {emblem}; asking for the member");
        Print("@ guild-second:" + name);

        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Map && !l.Mine && l.Text.Contains(Said)), 90, "the member's first map line");
        var member = Session.Chat.Lines.First(l => l.Channel == ChatChannels.Map && !l.Mine && l.Text.Contains(Said)).Name;
        Panel.NameField.Text = member;
        Press(Panel.InviteButton);
        await UntilAsync(() => Session.Guild.State.Members.Count == 2, 60, "the member joined");
        Print($"guild: {member} joined");

        Session.Chat.SetChannel(ChatChannels.Guild);
        Check(Session.Say(Said + " guild hello"), "the guild line went out");
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Guild && l.Name == member && l.Text == Said + " guild back"), 30, "the member's guild line");
        Print("guild: talked on the guild channel");

        // T-905: the member has put a stack and coins in; the leader takes both out and reads the log.
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Guild && l.Name == member && l.Text == Stored), 90, "the member's deposits");
        await OpenStorageAsync();
        await UntilAsync(() => Session.Guild.Storage is { Entries.Count: 1, Currency: > 0 }, 15, "the stack and the coins in the storage");
        var stored = Session.Guild.Storage!;
        var entry = stored.Entries[0];
        var coins = stored.Currency;
        var carried = Session.Inventory.Currency;
        Check(Storage.LogTab.IsVisibleInTree() && !Storage.EntryButtons[0].Disabled, "the leader may take out and read the log");
        Press(Storage.EntryButtons[0]);
        await FramesAsync(2);
        if (Storage.MoveButton.IsVisibleInTree()) Press(Storage.MoveButton);
        await UntilAsync(() => Session.Guild.Storage is { Entries.Count: 0 } && Session.Inventory.Find(entry.Id) is { } got && got.Quantity == entry.Quantity, 20, "the stack taken out, its id kept");
        Storage.AmountField.Text = coins.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Press(Storage.TakeCoinsButton);
        await UntilAsync(() => Session.Guild.Storage is { Currency: 0 } && Session.Inventory.Currency == carried + coins, 20, "the coins taken out");
        Press(Storage.LogTab);
        await FramesAsync(2);
        Check(Storage.Text.Contains(member + " put") && Storage.Text.Contains(name + " took"), "the log shows the member's deposits and the leader's withdrawals: " + Storage.Text);
        Check(Session.Guild.Storage!.Log!.Count == 4, "four moves in the log: " + Session.Guild.Storage!.Log!.Count);
        Print($"guild: took out {entry.Item} x{entry.Quantity} and {coins} coins; the log has the four moves");

        // T-810: one coin in while the member has the storage open; once it has seen it arrive, the coin comes back out (the guild's end needs an empty storage).
        Press(Storage.StorageTab);
        await FramesAsync(2);
        Storage.AmountField.Text = OneCoin;
        Press(Storage.PutCoinsButton);
        await UntilAsync(() => Session.Guild.Storage is { Currency: 1 }, 20, "the leader's coin in the storage");
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Guild && l.Name == member && l.Text == Seen), 30, "the member saw the coin arrive");
        Storage.AmountField.Text = OneCoin;
        Press(Storage.TakeCoinsButton);
        await UntilAsync(() => Session.Guild.Storage is { Currency: 0 }, 20, "the coin taken back");
        Press(Storage.CloseButton);
        await FramesAsync(2);
        await OpenGuildPanelAsync();

        Panel.NoticeField.Text = Notice;
        Press(Panel.NoticeSaveButton);
        await UntilAsync(() => Session.Guild.State.Notice == Notice, 15, "the notice written");
        await FramesAsync(2);
        Press(Panel.PromoteButtonOf(member) ?? throw new InvalidOperationException("no Up on the member's row: " + Panel.Text));
        await UntilAsync(() => Session.Guild.State.Members.Any(m => m.Name == member && m.Rank == 1), 15, "the member promoted");
        Print("guild: wrote the notice and promoted the member to " + Session.Guild.State.Ranks[1].Name);
        await _shot();

        await UntilAsync(() => Session.Guild.State.Members.Count == 1, 60, "the member left");
        Check(Session.Guild.Leading, "the founder still leads");
        Print("guild: the member left; the guild has its founder");
        Session.LeaveGuild();   // the last member: the guild ends and its name is free
        await UntilAsync(() => !Session.Guild.InGuild, 15, "the guild disbanded");
        await LeaveAsync();
    }

    /// <summary>The member: <paramref name="founder"/> is the founder's character name.</summary>
    public async Task MemberAsync(string founder)
    {
        var name = await EnterAsync("Mbr");
        Print($"guild: member {name} in the world; waiting for {founder}'s invite");
        var attempts = 0;
        while (Session.Guild.Invite is null)
        {
            if (++attempts > 20) throw new InvalidOperationException("no guild invite came");
            Session.Chat.SetChannel(ChatChannels.Map);
            Session.Say(Said + " hello from " + name);
            await SecondsAsync(3);
        }
        await FramesAsync(2);
        Check(Panel.PromptShown, "the invite prompt shows");
        Check(Session.Guild.Invite!.Name == founder, "the invite names the founder");
        Press(Panel.AcceptButton);
        await UntilAsync(() => Session.Guild.InGuild && Session.Guild.State.Members.Count == 2, 30, "in the guild");
        Check(Session.Guild.OwnRank == Session.Guild.State.Ranks.Count - 1, "new members join at the last rank");
        Check(Session.Guild.State.Ranks[^1].Name == Renamed && !Panel.RanksButton.Visible, "at the rank the founder renamed, without the rank editor");
        Check(Session.Guild.State.Members.Single(m => m.Name == founder).Online, "the founder shows online");

        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Guild && l.Name == founder && l.Text == Said + " guild hello"), 30, "the founder's guild line");
        Session.Chat.SetChannel(ChatChannels.Guild);
        Check(Session.Say(Said + " guild back"), "the guild line back went out");

        // T-905: coins from the merchant, then a stack and the coins into the storage; taking out is refused at this rank.
        await SellAsync();
        var coins = Session.Inventory.Currency;
        await OpenStorageAsync();
        Check(!Storage.LogTab.IsVisibleInTree(), "a member's rank cannot read the log");
        Press(Storage.BagTab);
        await FramesAsync(2);
        Check(Storage.EntryButtons.Count > 0, "the bag lists what may go in");
        Press(Storage.EntryButtons[0]);
        await FramesAsync(2);
        if (Storage.MoveButton.IsVisibleInTree()) Press(Storage.MoveButton);
        await UntilAsync(() => Session.Guild.Storage is { Entries.Count: 1 }, 20, "the stack in the storage");
        await FramesAsync(2);
        Storage.AmountField.Text = coins.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Press(Storage.PutCoinsButton);
        await UntilAsync(() => Session.Guild.Storage is { Currency: > 0 } && Session.Inventory.Currency == 0, 20, "the coins in the storage");
        Press(Storage.StorageTab);
        await FramesAsync(2);
        Check(Storage.EntryButtons.Count == 1 && Storage.EntryButtons[0].Disabled && !Storage.TakeCoinsButton.IsVisibleInTree(), "a member's rank sees no Take");
        Session.WithdrawFromGuild(Session.Guild.Storage!.Entries[0].Id, 1);   // as a modified client would
        await UntilAsync(() => Session.Guild.Notice == UiText.GuildRefused(GuildReasons.NotAllowed, null, null), 15, "the withdrawal refused");
        Print($"guild: the member put {Session.Guild.Storage!.Entries[0].Item} and {coins} coins in; taking out was refused: not_allowed");
        // T-810: the window stays open. Opened afresh, then no more renewals: within the lease, only the server's push can change what this client holds.
        Press(Storage.CloseButton);
        await FramesAsync(2);
        await OpenStorageAsync();
        await UntilAsync(() => Session.Guild.Storage is { Entries.Count: 1 }, 15, "the storage opened again");
        await SecondsAsync(1);
        Session.CloseGuildStorage();
        Session.Chat.SetChannel(ChatChannels.Guild);
        Check(Session.Say(Stored), "the stored line went out");
        await UntilAsync(() => Session.Guild.Storage is { Entries.Count: 0, Currency: 1 }, 14, "the founder's coin arriving in the open window");
        await FramesAsync(2);
        Check(Storage.Open && Storage.EntryButtons.Count == 0 && Storage.Text.StartsWith(UiText.GuildStorageTitle(0, Session.Guild.Storage!.Size, _main.Bundle!.CurrencyName, 1), StringComparison.Ordinal),
            "the open window shows it: " + Storage.Text);
        Check(Session.Guild.Storage!.Log is null, "without the log");
        Print("guild: the member, its storage open, saw the leader's moves and the coin arrive");
        Check(Session.Say(Seen), "the seen line went out");
        Press(Storage.CloseButton);

        await UntilAsync(() => Session.Guild.State.Notice == Notice, 90, "the notice");
        await UntilAsync(() => Session.Guild.OwnRank == 1, 30, "the promotion");
        Print("guild: the member sees the notice and its new rank " + Session.Guild.State.Ranks[1].Name);
        Press(_main.Hud!.Chat!.OpenButton);
        await FramesAsync(2);
        Press(_main.Hud!.Chat!.GuildButton);
        await FramesAsync(2);
        Check(Panel.Text.Contains("meet at the well"), "the panel shows the notice: " + Panel.Text);
        Press(Panel.LeaveButton);
        await UntilAsync(() => !Session.Guild.InGuild, 15, "out of the guild");
        await LeaveAsync();
    }

    /// <summary>A start stack's unit sold at the merchant (in view at the map's start): coins to deposit.</summary>
    private async Task SellAsync()
    {
        if (_merchantNpc is null) throw new InvalidOperationException("the member needs --e2e-guild-ids <merchant npc>");
        EntityView? merchant = null;
        await UntilAsync(() => (merchant = Session.World.Entities.FirstOrDefault(e => e.Kind == EntityKinds.Npc && e.DisplayKey == _merchantNpc)) is not null, 20, "the merchant");
        Session.TapNpc(merchant!.Id);
        await UntilAsync(() => Session.Dialogue.Open && Session.Dialogue.Choices.Any(c => c.Text.Contains("wares", StringComparison.Ordinal)), 20, "the merchant's greeting");
        Session.ChooseDialogue(Session.Dialogue.Choices.First(c => c.Text.Contains("wares", StringComparison.Ordinal)).Index);
        await SecondsAsync(1);
        var stack = Session.Inventory.Entries.First(e => e.Slot is null && e.Quantity > 1);
        Session.ShopSell(stack.Id, 1);
        await UntilAsync(() => Session.Inventory.Currency > 0, 10, "the sale");
        Session.CloseDialogue();
        await SecondsAsync(1);
    }

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private async Task<string> EnterAsync(string prefix)
    {
        var accounts = Session.Accounts;
        var screen = _main.Hud!.Accounts!;
        await FramesAsync(3);
        var tag = Tag();
        var username = "e2e-guild-" + tag;
        screen.Username.Text = username;
        screen.Password.Text = Guid.NewGuid().ToString("N");   // thrown away with the run
        Press(screen.RegisterButton);
        await UntilAsync(() => accounts.Screen == AccountScreen.Characters && !accounts.Busy, 15, "the character list after registering");
        var name = prefix + tag;
        await LookRuns.CreateAsync(_main, name);
        await UntilAsync(() => screen.Rows.Count > 0 && screen.Rows[0].Play.IsVisibleInTree(), 10, "the character in the list");
        Press(screen.Rows[0].Play);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && Session.World.Own is not null && _main.LoadedMap == Session.World.MapId && Session.Chat.MaxLength > 0, 20,
            "entering the world");
        await FramesAsync(3);
        return name;
    }

    private async Task LeaveAsync()
    {
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        await FramesAsync(3);
        Check(!Session.Guild.InGuild, "no guild is left once signed out");
    }

    private static void Press(Button button)
    {
        Check(button.IsVisibleInTree() && !button.Disabled, $"the button \"{button.Text}\" can be pressed");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);
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
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}; guild: {Session.Guild.Notice} {Session.Guild.State}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
