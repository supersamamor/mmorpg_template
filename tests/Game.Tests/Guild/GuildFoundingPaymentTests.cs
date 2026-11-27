using Game.Server.World.Guild;
using Game.Server.World.Saving;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;

namespace Game.Tests.Guild;

/// <summary>T-904: the inventory system takes a guild's founding cost on the tick, all or nothing, and puts the founding note on the founder's save.</summary>
public sealed class GuildFoundingPaymentTests
{
    private const string Draught = "item.healing_draught";
    private static readonly GuildCostDef Cost = new() { Currency = 100, ItemId = Draught, Quantity = 2 };

    private static (InventoryRig Rig, Game.Server.World.EntityId Player, CharacterTicket Ticket) Saved(long currency, int draughts)
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(new GridCell(2, 2));
        rig.Ticks();
        var ticket = new CharacterTicket(Guid.NewGuid());
        rig.Get(player).Save = ticket;
        rig.State(player).Currency = currency;
        if (draughts > 0) rig.Give(player, Draught, draughts);
        return (rig, player, ticket);
    }

    private static int Carried(InventoryRig rig, Game.Server.World.EntityId player) => rig.State(player).Entries.Where(e => e.ItemId == Draught).Sum(e => e.Quantity);

    [Fact]
    public async Task The_cost_is_taken_with_the_note_on_the_save()
    {
        var (rig, player, ticket) = Saved(150, 3);
        var pay = new GuildFoundingPayment(player, 42, Cost);
        rig.Send(pay);
        rig.Ticks();
        (await pay.Done).ShouldBeNull();
        (rig.State(player).Currency, Carried(rig, player)).ShouldBe((50L, 1));
        ticket.PendingNotes().ShouldHaveSingleItem().FoundsGuild.ShouldBe(42);
        pay.Note.ShouldBeSameAs(ticket.PendingNotes()[0]);
        rig.Of<Game.Server.World.Inventory.InventoryChanged>().Last().Cause.ShouldBe(InventoryChanges.GuildFounded);
    }

    [Theory]
    [InlineData(99, 3)]
    [InlineData(150, 1)]
    public async Task Short_of_either_part_takes_nothing(long currency, int draughts)
    {
        var (rig, player, ticket) = Saved(currency, draughts);
        var pay = new GuildFoundingPayment(player, 42, Cost);
        rig.Send(pay);
        rig.Ticks();
        (await pay.Done).ShouldBe(GuildReasons.CannotAfford);
        (rig.State(player).Currency, Carried(rig, player)).ShouldBe((currency, draughts));
        ticket.PendingNotes().ShouldBeEmpty();
    }

    [Fact]
    public async Task No_cost_still_founds_with_the_next_save_and_a_character_that_is_not_saved_cannot_found()
    {
        var (rig, player, ticket) = Saved(0, 0);
        var free = new GuildFoundingPayment(player, 7, null);
        rig.Send(free);
        rig.Ticks();
        (await free.Done).ShouldBeNull();
        ticket.PendingNotes().ShouldHaveSingleItem().FoundsGuild.ShouldBe(7);

        rig.Get(player).Save = null;   // a dev login
        var unsaved = new GuildFoundingPayment(player, 8, null);
        rig.Send(unsaved);
        rig.Ticks();
        (await unsaved.Done).ShouldBe(GuildReasons.Unavailable);
    }

    [Fact]
    public void A_payment_given_up_before_its_tick_changes_nothing()
    {
        var (rig, player, ticket) = Saved(150, 3);
        var pay = new GuildFoundingPayment(player, 42, Cost);
        rig.Send(pay);
        pay.TryAbandon().ShouldBeTrue();
        rig.Ticks();
        pay.Done.IsCompleted.ShouldBeFalse();
        (rig.State(player).Currency, Carried(rig, player)).ShouldBe((150L, 3));
        ticket.PendingNotes().ShouldBeEmpty();
        pay.TryAbandon().ShouldBeFalse();
    }
}
