using Game.Content.Loading;
using Game.Content.Validation;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Outfits;
using Game.Server.World.Quests;
using Game.Server.World.Saving;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Quests;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.Wardrobe;
using Game.Shared.World;
using Game.Tests.Saving;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// T-708, outfits: the rules of the wardrobe, outfits as content that can only be looks, the quest reward that grants them, the outfit system on the real pipeline, what is
/// kept across a save and a content change, and the guarantee that an outfit changes no number of the game.
/// </summary>
public class OutfitTests
{
    private const string Garland = "outfit.harvest_garland";
    private const string Cloak = "outfit.rain_cloak";
    private static readonly AppearanceRecord Male = new("male", "skin.fair", "face.male_calm", "hair.male_crop", "haircolor.raven");
    private static readonly AppearanceRecord Female = new("female", "skin.fair", "face.female_calm", "hair.female_bob", "haircolor.raven");

    private static WardrobeRules Rules => new(TestGrids.SampleContent.Outfits.All);

    // ---- the rules -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Granting_adds_each_outfit_once_and_keeps_one_the_content_does_not_have()
    {
        var state = new WardrobeState();
        WardrobeRules.Grant(state, Cloak).ShouldBeTrue();
        WardrobeRules.Grant(state, Cloak).ShouldBeFalse("owning it already changes nothing");
        WardrobeRules.Grant(state, "outfit.from_the_future").ShouldBeTrue("a promised one is kept");
        WardrobeRules.Grant(state, "").ShouldBeFalse();
        state.Owned.ShouldBe([Cloak, "outfit.from_the_future"], ignoreOrder: true);
        Rules.Owned(state).ShouldBe([Cloak], "only what the content has is told");
    }

    [Fact]
    public void A_choice_is_checked_in_order_and_a_refusal_changes_nothing()
    {
        var rules = Rules;
        var state = new WardrobeState();
        WardrobeRules.Grant(state, Garland);
        rules.Choose(state, "hat", Garland, Genders.Male).ShouldBe(OutfitReasons.UnknownSlot);
        rules.Choose(state, "hat", null, Genders.Male).ShouldBe(OutfitReasons.UnknownSlot);
        rules.Choose(state, OutfitSlots.Head, "outfit.nothing", Genders.Male).ShouldBe(OutfitReasons.UnknownOutfit);
        rules.Choose(state, OutfitSlots.Head, Cloak, Genders.Male).ShouldBe(OutfitReasons.NotOwned);
        rules.Choose(state, OutfitSlots.Feet, Garland, Genders.Male).ShouldBe(OutfitReasons.SlotNotCovered);
        state.Chosen.ShouldBeEmpty();

        WardrobeRules.Grant(state, Cloak);
        rules.Choose(state, OutfitSlots.Body, Cloak, Genders.Male).ShouldBeNull("the coat is cut for each gender");
        rules.Choose(state, OutfitSlots.Body, Cloak, null).ShouldBe(OutfitReasons.WrongGender, "a character without a gender has no coat");
        rules.Choose(state, OutfitSlots.Head, Garland, Genders.Female).ShouldBeNull();
        rules.Choose(state, OutfitSlots.Back, Cloak, Genders.Female).ShouldBeNull();
        rules.Chosen(state, Genders.Male).ShouldBe([new ShownOutfit(OutfitSlots.Head, Garland), new ShownOutfit(OutfitSlots.Body, Cloak), new ShownOutfit(OutfitSlots.Back, Cloak)]);
        rules.Choose(state, OutfitSlots.Head, null, Genders.Male).ShouldBeNull("nothing is always allowed");
        rules.Chosen(state, Genders.Male).Select(s => s.Slot).ShouldBe([OutfitSlots.Body, OutfitSlots.Back]);
    }

    [Fact]
    public void Hiding_shows_nothing_and_keeps_the_choices_and_a_lost_or_unowned_choice_is_ignored()
    {
        var rules = Rules;
        var state = new WardrobeState();
        WardrobeRules.Grant(state, Cloak);
        rules.Choose(state, OutfitSlots.Head, Cloak, Genders.Female).ShouldBeNull();
        state.Hidden = true;
        rules.Shown(state, Genders.Female).ShouldBeEmpty();
        rules.Chosen(state, Genders.Female).Count.ShouldBe(1);
        state.Hidden = false;
        state.Chosen[OutfitSlots.Face] = "outfit.long_gone";   // as a load leaves it
        state.Chosen["wings"] = Cloak;                           // a slot the vocabulary has lost
        state.Chosen[OutfitSlots.Feet] = Garland;                 // not owned (taken away, say)
        rules.Shown(state, Genders.Female).ShouldBe([new ShownOutfit(OutfitSlots.Head, Cloak)]);
    }

    // ---- content ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_template_ships_two_plain_outfits_that_the_first_quest_gives()
    {
        var content = TestGrids.SampleContent;
        content.Outfits.All.Select(o => o.Id).ShouldBe([Garland, Cloak]);
        content.Outfits.Get(Cloak).TakesColors.ShouldBe([AppearanceColorRoles.Cloth]);
        content.Outfits.Get(Cloak).Pieces.Where(p => p.Slot == OutfitSlots.Body).Select(p => p.Gender).ShouldBe([Genders.Male, Genders.Female]);
        content.Quests.Get("quest.first_hunt").Rewards.OfType<OutfitReward>().Select(r => r.OutfitId).ShouldBe([Garland, Cloak]);
    }

    public static TheoryData<string, string> Forbidden => new()
    {
        { "a modifier", "modifiers:\n  - { stat: def, op: flat, value: \"1\" }\n" },
        { "an effect", "effects:\n  - { type: heal, amount: \"5\" }\n" },
        { "a requirement", "min_base_level: 10\n" },
        { "a price", "buy_price: 100\n" },
        { "a drop", "drop_table_ids: [drop.gel_slime]\n" },
        { "a stat on a piece", "" },
    };

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void An_outfit_has_no_place_for_stats_effects_requirements_prices_or_drops(string what, string extra)
    {
        using var temp = new TempContent();
        var file = Path.Combine(temp.Root, "outfits", "rain_cloak.yaml");
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        text = extra.Length > 0 ? text + extra : text.Replace("model_asset_key: model.outfit.rain_cape }", "model_asset_key: model.outfit.rain_cape, atk: 5 }");
        File.WriteAllText(file, text);
        var load = ContentLoader.Load(temp.Root);
        load.Database.ShouldBeNull(what);
        load.Errors.ShouldContain(e => e.File == "outfits/rain_cloak.yaml", what);
    }

    [Fact]
    public void Nothing_that_has_numbers_can_name_an_outfit()
    {
        using var temp = new TempContent();
        temp.Edit("drops/gel_slime.yaml", "item.gel_glob", Cloak);
        temp.Edit("shops/meadow_goods.yaml", "item_id: item.antidote", "item_id: " + Garland);
        var report = temp.Validate();
        report.Issues.Count(i => i.Code == ValidationCodes.RefMissing && i.Message.Contains("is not a item", StringComparison.Ordinal)).ShouldBe(2, string.Join("\n", report.Issues));
    }

    [Fact]
    public void An_outfit_is_checked_where_it_points_and_what_it_covers()
    {
        using var temp = new TempContent();
        temp.Edit("outfits/rain_cloak.yaml", "model_asset_key: model.outfit.rain_coat_female, gender: female", "model_asset_key: model.outfit.rain_coat_female");
        temp.Edit("outfits/rain_cloak.yaml", "model.outfit.rain_cape }", "model.outfit.no_such_cape }");
        temp.Edit("outfits/rain_cloak.yaml", "takes_colors: [cloth]", "takes_colors: [cloth, glow]");
        temp.Edit("outfits/harvest_garland.yaml", "slot: body,", "slot: wings,");
        temp.Edit("quests/first_hunt.yaml", "outfit_id: outfit.rain_cloak", "outfit_id: outfit.rain_cloke");
        var issues = temp.Validate().Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        issues.ShouldContain(i => i.Code == ValidationCodes.OutfitInvalid && i.Path == "pieces[2].slot", "two body pieces for a male character");
        issues.ShouldContain(i => i.Code == ValidationCodes.AssetMissing && i.Path == "pieces[3].model_asset_key");
        issues.ShouldContain(i => i.Code == ValidationCodes.VocabUnknown && i.Path == "takes_colors[1]");
        issues.ShouldContain(i => i.Code == ValidationCodes.VocabUnknown && i.Path == "pieces[1].slot");
        issues.ShouldContain(i => i.Code == ValidationCodes.RefMissing && i.Message.Contains("did you mean 'outfit.rain_cloak'", StringComparison.Ordinal));
    }

    // ---- the outfit system on the real pipeline ----------------------------------------------------------------------------------------------

    private static (QuestRig Rig, EntityId Player) Dressed(AppearanceRecord? looks = null)
    {
        var rig = new QuestRig();
        var player = rig.Join();
        rig.Get(player).Appearance = looks ?? Male;
        return (rig, player);
    }

    [Fact]
    public void The_first_hunt_grants_both_outfits_with_its_other_rewards_and_once()
    {
        var (rig, player) = Dressed();
        rig.Progress(player).OwedRewards.Add(new OwedQuestRewards("quest.first_hunt", 0, 0, 0, 0, [], [], [Garland, Cloak]));
        rig.Publish(new QuestRewardsRequested(player, "quest.first_hunt"));
        rig.Ticks(2);
        rig.Progress(player).Wardrobe.Owned.ShouldBe([Garland, Cloak], ignoreOrder: true);
        rig.Of<OutfitGranted>().Select(e => e.Outfit).ShouldBe([Garland, Cloak]);
        rig.Progress(player).OwedRewards.Add(new OwedQuestRewards("quest.again", 0, 0, 0, 0, [], [], [Cloak]));
        rig.Publish(new QuestRewardsRequested(player, "quest.again"));
        rig.Ticks(2);
        rig.Of<OutfitGranted>().Count.ShouldBe(2, "owning it already grants nothing");
        rig.Get(player).Outfit.ShouldBeNull("a granted outfit is not put on");
    }

    [Fact]
    public void The_quest_owes_its_outfits_like_its_other_rewards()
    {
        var quests = new QuestRules(TestGrids.SampleContent.Quests.All, 5);
        quests.Rewards("quest.first_hunt").Outfits.ShouldBe([Garland, Cloak]);
    }

    [Fact]
    public void A_choice_shows_once_a_refusal_says_why_and_dead_or_hidden_works_too()
    {
        var (rig, player) = Dressed();
        rig.Send(new GrantOutfit(player, Cloak));
        rig.Ticks();
        rig.Of<OutfitGranted>().Single().Outfit.ShouldBe(Cloak);

        rig.Send(new SetOutfit(player, OutfitSlots.Head, Garland));
        rig.Send(new SetOutfit(player, "wings", Cloak));
        rig.Ticks();
        rig.Of<OutfitRefused>().Select(r => r.Reason).ShouldBe([OutfitReasons.NotOwned, OutfitReasons.UnknownSlot]);
        rig.Get(player).Outfit.ShouldBeNull();

        rig.Send(new SetOutfit(player, OutfitSlots.Body, Cloak));
        rig.Ticks();
        var look = rig.Get(player).Outfit!;
        look.Shown.ShouldBe([new ShownOutfit(OutfitSlots.Body, Cloak)]);
        rig.Send(new SetOutfit(player, OutfitSlots.Body, Cloak));
        rig.Ticks();
        rig.Get(player).Outfit.ShouldBeSameAs(look, "the same choice again is no change");
        rig.Of<WardrobeChanged>().Count.ShouldBe(1);

        rig.Get(player).Dead = true;   // looks only: a dead character may dress too
        rig.Send(new HideOutfits(player, true));
        rig.Ticks();
        rig.Get(player).Outfit!.Shown.ShouldBeEmpty();
        rig.Progress(player).Wardrobe.Chosen.ShouldContainKey(OutfitSlots.Body);
        rig.Send(new HideOutfits(player, false));
        rig.Ticks();
        rig.Get(player).Outfit!.Shown.ShouldBe(look.Shown);
    }

    [Fact]
    public void An_outfit_changes_no_stat_no_hit_and_no_step()
    {
        // Two worlds alike in everything but the wardrobe: the same seed, the same clock, the same orders.
        var (bare, a) = Dressed();
        var (dressed, b) = Dressed();
        foreach (var outfit in TestGrids.SampleContent.Outfits.All) dressed.Send(new GrantOutfit(b, outfit.Id));
        dressed.Ticks();
        foreach (var slot in OutfitSlots.All)
            foreach (var outfit in TestGrids.SampleContent.Outfits.All)
                dressed.Send(new SetOutfit(b, slot, outfit.Id));
        dressed.Ticks();
        bare.Ticks(2);
        dressed.Get(b).Outfit!.Shown.Select(s => s.Slot).ShouldBe([OutfitSlots.Head, OutfitSlots.Body, OutfitSlots.Back], "every slot the outfits have is shown");

        Sheet(dressed.Get(b)).ShouldBe(Sheet(bare.Get(a)));
        foreach (var rig in new[] { bare, dressed })
        {
            var player = rig == bare ? a : b;
            rig.Send(new MoveTo(player, QuestRig.Field));
            rig.TickUntil(() => rig.Get(player).Cell.Equals(QuestRig.Field), 100, "the walk");
            var monster = rig.SpawnMonster("monster.gel_slime", new GridCell(QuestRig.Field.X + 1, QuestRig.Field.Y));
            rig.Send(new AttackTarget(player, monster));
            rig.Ticks(60);
        }
        Hits(dressed, b).ShouldBe(Hits(bare, a));
        Hits(bare, a).ShouldNotBeEmpty();
        dressed.Of<CellReached>().Where(e => e.Entity == b).Select(e => (e.Cell, dressed.Events.First(x => x.Event == (Game.Server.Events.IGameEvent)e).Tick))
            .ShouldBe(bare.Of<CellReached>().Where(e => e.Entity == a).Select(e => (e.Cell, bare.Events.First(x => x.Event == (Game.Server.Events.IGameEvent)e).Tick)), "the same steps at the same ticks");
        Sheet(dressed.Get(b)).ShouldBe(Sheet(bare.Get(a)));

        static List<string> Sheet(Entity e) => Stats.All.Ordered.Select(s => s + "=" + e.Stats!.Get(s)).Append("hp=" + e.Vitals?.Hp).ToList();
        static List<string> Hits(QuestRig rig, EntityId player) => rig.Of<DamageDealt>().Where(d => d.Source == player || d.Target == player).Select(d => $"{d.Amount} {d.Result}").ToList();
    }

    // ---- saving ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_is_owned_and_chosen_survives_leaving_and_entering()
    {
        var store = new MemoryCharacterStore();
        var ada = Guid.NewGuid();
        EntityId id;
        await using (var first = SaveRig.Start(store))
        {
            id = await first.EnterAsync(ada);
            first.Get(id).Appearance = Female;
            first.Send(new GrantOutfit(id, Cloak));
            first.Ticks();
            first.Send(new SetOutfit(id, OutfitSlots.Body, Cloak));
            first.Send(new HideOutfits(id, true));
            first.Ticks();
            await first.LeaveAsync(id);
        }
        var saved = store.StateOf(ada)!;
        saved.Outfits.ShouldBe([Cloak]);
        (saved.OutfitSlots[OutfitSlots.Body], saved.OutfitsHidden).ShouldBe((Cloak, true));
        await using var second = SaveRig.Start(store);
        var again = await second.EnterAsync(ada);
        var wardrobe = second.Progress(again).Wardrobe;
        (wardrobe.Owned.Single(), wardrobe.Chosen[OutfitSlots.Body], wardrobe.Hidden).ShouldBe((Cloak, Cloak, true));
        second.Get(again).Outfit.ShouldBeNull("hidden, so nothing shows");
    }

    [Fact]
    public void An_outfit_the_content_has_lost_is_kept_and_ignored_and_comes_back_with_the_content()
    {
        long n = 100;
        var saved = SavedContent.Full(() => ++n, DateTime.UtcNow) with { Outfits = [Cloak, "outfit.long_gone"], OutfitSlots = new Dictionary<string, string> { ["back"] = "outfit.long_gone" } };
        var loaded = new CharacterLoader(TestGrids.SampleContent, () => ++n).Load(saved, DateTime.UtcNow);
        loaded.Progression.Wardrobe.Owned.ShouldBe([Cloak, "outfit.long_gone"], ignoreOrder: true);
        loaded.Progression.Wardrobe.Chosen["back"].ShouldBe("outfit.long_gone");
        loaded.Notes.ShouldContain(note => note.Contains("outfit.long_gone", StringComparison.Ordinal));
        Rules.Shown(loaded.Progression.Wardrobe, Genders.Male).ShouldBeEmpty("it shows nothing while the content lacks it");
        var rules = new WardrobeRules(TestGrids.SampleContent.Outfits.All.Select(o => o.Id == Cloak ? o with { Id = "outfit.long_gone" } : o));
        loaded.Progression.Wardrobe.Hidden = false;
        rules.Shown(loaded.Progression.Wardrobe, Genders.Male).ShouldBe([new ShownOutfit("back", "outfit.long_gone")], "and shows again when it is back");
    }

    /// <summary>A copy of the real content in a temp folder, with the theme beside it.</summary>
    private sealed class TempContent : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "t708_" + Guid.NewGuid().ToString("N"), "content");

        public TempContent()
        {
            var from = Path.Combine(TestGrids.RepoRoot(), "content");
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(Root, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        public void Edit(string relative, string from, string to)
        {
            var path = Path.Combine(Root, relative);
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            text.ShouldContain(from);
            var at = text.IndexOf(from, StringComparison.Ordinal);
            File.WriteAllText(path, text[..at] + to + text[(at + from.Length)..]);
        }

        public ValidationReport Validate()
        {
            var load = ContentLoader.Load(Root);
            load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
            var keys = AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(TestGrids.RepoRoot(), "themes"), "default"));
            return ContentValidator.Validate(load.Database!, Root, keys);
        }

        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
    }
}
