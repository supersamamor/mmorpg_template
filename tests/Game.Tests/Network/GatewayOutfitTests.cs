using System.Text;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Outfits;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Network;

/// <summary>
/// T-708 on the real stack: the own <c>wardrobe</c> after the snapshot and after every change, <c>set_outfit</c> refused for every reason with the outfit named, hostile and malformed
/// messages, and others in view told of a change once, in a change record, and of the whole look in the record that brings a player into view. And what it costs in bytes.
/// </summary>
public class GatewayOutfitTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Garland = "outfit.harvest_garland";
    private const string Cloak = "outfit.rain_cloak";

    private static async Task GrantAsync(GatewayRig rig, TestClient client, params string[] outfits)
    {
        var before = client.Messages.OfType<WardrobeMessage>().Count();
        foreach (var outfit in outfits) rig.World.TryEnqueue(new GrantOutfit(new EntityId(client.Id), outfit)).ShouldBe(EnqueueResult.Accepted);
        await Poll.UntilAsync(() => client.Messages.OfType<WardrobeMessage>().LastOrDefault(), w => w is not null && outfits.All(w.Owned.Contains) && client.Messages.OfType<WardrobeMessage>().Count() > before,
            "the wardrobe with the new outfits");
    }

    [Fact]
    public async Task The_owner_is_told_its_wardrobe_and_every_refusal_and_another_player_sees_the_change_once()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("dresser");
        var first = await me.WaitForAsync<WardrobeMessage>();
        (first.Owned.Count, first.Chosen.Count, first.Hidden).ShouldBe((0, 0, false), "a new character has nothing, and is told so after its snapshot");
        await GrantAsync(rig, me, Garland, Cloak);
        var other = await rig.JoinAsync("onlooker");
        await Poll.UntilAsync(() => other.Seen(me.Id), r => r is not null, "the dresser in view");
        other.Seen(me.Id)!.Outfit.ShouldBeNull("nothing shown yet, so nothing sent");

        // Refusals: each names the reason, the slot and the outfit; nothing changes.
        foreach (var (slot, outfit, reason) in new[]
                 {
                     ("wings", Cloak, OutfitReasons.UnknownSlot), (OutfitSlots.Head, "outfit.nothing", OutfitReasons.UnknownOutfit), (OutfitSlots.Feet, Cloak, OutfitReasons.SlotNotCovered),
                 })
        {
            await me.SendAsync(new SetOutfitMessage(slot, outfit));
            (await me.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.SetOutfit && r.Reason == reason))
                .ShouldBe(new RejectedMessage(MessageTypes.SetOutfit, reason, null, Slot: slot, Item: outfit));
        }

        // A choice: the wardrobe comes back, and the onlooker gets one change with the outfit and nothing else of it.
        var changesBefore = Changes(other, me.Id).Count;
        await me.SendAsync(new SetOutfitMessage(OutfitSlots.Body, Cloak));
        var wardrobe = await me.WaitForAsync<WardrobeMessage>(w => w.Chosen.Count == 1);
        (wardrobe.Chosen.Single(), wardrobe.Hidden).ShouldBe((new ShownOutfit(OutfitSlots.Body, Cloak), false));
        wardrobe.Owned.ShouldBe([Garland, Cloak]);
        var change = await Poll.UntilAsync(() => Changes(other, me.Id).Skip(changesBefore).FirstOrDefault(c => (c.Fields & ChangedFields.Outfit) != 0), c => c is not null, "the change");
        change!.Outfit.ShouldBe([new ShownOutfit(OutfitSlots.Body, Cloak)]);
        await me.SendAsync(new SetOutfitMessage(OutfitSlots.Body, Cloak));   // the same again
        await me.SendAsync(new PingMessage(77));
        await me.WaitForAsync<PongMessage>(p => p.Seq == 77);
        await Task.Delay(400);
        Changes(other, me.Id).Count(c => (c.Fields & ChangedFields.Outfit) != 0).ShouldBe(1, "a change reaches the others once");

        // Hiding: everyone sees the gear; a new onlooker sees the whole look when the player comes into view.
        await me.SendAsync(new SetOutfitMessage(OutfitSlots.Head, Garland));
        await me.WaitForAsync<WardrobeMessage>(w => w.Chosen.Count == 2);
        var third = await rig.JoinAsync("latecomer");
        await Poll.UntilAsync(() => third.Seen(me.Id), r => r?.Outfit is { Count: 2 }, "the whole look in the snapshot");
        third.Seen(me.Id)!.Outfit.ShouldBe([new ShownOutfit(OutfitSlots.Head, Garland), new ShownOutfit(OutfitSlots.Body, Cloak)]);
        await me.SendAsync(new SetOutfitMessage(null, null, true));
        (await me.WaitForAsync<WardrobeMessage>(w => w.Hidden)).Chosen.Count.ShouldBe(2, "the choices stay");
        await Poll.UntilAsync(() => Changes(other, me.Id).LastOrDefault(c => (c.Fields & ChangedFields.Outfit) != 0), c => c!.Outfit is { Count: 0 }, "the outfits hidden for others");
        rig.Progressions.TryGet(new EntityId(me.Id), out var state).ShouldBeTrue();
        state.Wardrobe.Hidden.ShouldBeTrue();
    }

    [Fact]
    public async Task Hostile_and_malformed_choices_change_nothing()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("hostile");
        await me.WaitForAsync<WardrobeMessage>();
        await me.SendAsync(new SetOutfitMessage(OutfitSlots.Head, Cloak));
        (await me.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.SetOutfit)).Reason.ShouldBe(OutfitReasons.NotOwned, "the bundle lists every outfit: owning is the server's word");
        await me.SendRawAsync("""{"type":"set_outfit","slot":"head","outfit":"outfit.rain_cloak","hidden":false}""");
        (await me.WaitForByeAsync()).ShouldBe(ByeReasons.Malformed);
        rig.Progressions.TryGet(new EntityId(me.Id), out var state).ShouldBeTrue();
        (state.Wardrobe.Owned.Count, state.Wardrobe.Chosen.Count, state.Wardrobe.Hidden).ShouldBe((0, 0, false));
    }

    [Fact]
    public async Task A_female_character_cannot_wear_the_male_cut()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("cut");
        await me.WaitForAsync<WardrobeMessage>();
        var appearance = new AppearanceRecord("female", "skin.fair", "face.female_calm", "hair.female_bob", "haircolor.raven");
        rig.World.GetInstance(rig.World.LocationOf(new EntityId(me.Id))!).TryGetEntity(new EntityId(me.Id), out var entity).ShouldBeTrue();
        entity.Appearance = appearance;   // the dev login has the defaults; looks are fixed while in the world, so this stands in for a female account character
        await GrantAsync(rig, me, Cloak);
        await me.SendAsync(new SetOutfitMessage(OutfitSlots.Body, Cloak));
        (await me.WaitForAsync<WardrobeMessage>(w => w.Chosen.Count == 1)).Chosen.ShouldBe([new ShownOutfit(OutfitSlots.Body, Cloak)], "the female cut is chosen: the server checks the gender, the client draws its piece");
    }

    /// <summary>The cost, as the AOI writes it (§9 "Network"): the bytes a player's record grows by in a snapshot or an appeared entry, and a change.</summary>
    [Fact]
    public void An_outfit_costs_a_few_dozen_bytes_once_per_view()
    {
        string Bytes(IReadOnlyList<ShownOutfit>? outfit) => Encoding.UTF8.GetString(ProtocolJsonRecord(outfit));
        byte[] ProtocolJsonRecord(IReadOnlyList<ShownOutfit>? outfit) =>
            ProtocolJson.ToBytes(new SnapshotMessage(1, "map.a", 1, [new EntityRecord(42, EntityKinds.Player, "job.wanderer", new GridCell(12, 12), Directions.N, null, 0, 1, Worn: ["item.bone_dirk"],
                Appearance: new AppearanceRecord("female", "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember"), Outfit: outfit)]));
        var none = Bytes(null).Length;
        var one = Bytes([new ShownOutfit(OutfitSlots.Body, Cloak)]).Length - none;
        var all = Bytes([new ShownOutfit(OutfitSlots.Head, Garland), new ShownOutfit(OutfitSlots.Body, Cloak), new ShownOutfit(OutfitSlots.Back, Cloak)]).Length - none;
        var change = ProtocolJson.ToBytes(new DeltaMessage(2, [], [new ChangeRecord(42, ChangedFields.Outfit, default, null, null, 0, null, Outfit: [new ShownOutfit(OutfitSlots.Body, Cloak)])], [])).Length;
        output.WriteLine($"a player's record without outfits: {none} bytes; one outfit +{one}, three slots +{all}; a delta that changes one: {change} bytes");
        one.ShouldBeLessThanOrEqualTo(40);
        all.ShouldBeLessThanOrEqualTo(100);
        Bytes([]).Length.ShouldBe(none, "nothing shown, nothing sent");
    }

    private static List<ChangeRecord> Changes(TestClient client, long id) => client.Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).Where(c => c.Id == id).ToList();
}
