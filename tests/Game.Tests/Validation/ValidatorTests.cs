using System.Text.Json;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Shared.Formulas;

namespace Game.Tests.Validation;

/// <summary>
/// One test per rule. Each copies the sample content to a temp folder, breaks exactly one thing with a text edit,
/// and expects the matching issue (code, file, YAML path). The base content is the T-004 sample set.
/// </summary>
public class ValidatorTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "validate_" + Guid.NewGuid().ToString("N"));

    public ValidatorTests() => CopyDir(Path.Combine(RepoRoot(), "content"), _temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static IReadOnlySet<string> RealAssetKeys() =>
        AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));

    /// <summary>Replaces <paramref name="from"/> with <paramref name="to"/> once in a content file (line endings normalised); fails if absent.</summary>
    private void Edit(string relativeFile, string from, string to)
    {
        var path = Path.Combine(_temp, relativeFile);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from, customMessage: $"test setup: '{from}' not found in {relativeFile}");
        var index = text.IndexOf(from, StringComparison.Ordinal);
        File.WriteAllText(path, text[..index] + to + text[(index + from.Length)..]);
    }

    private ValidationReport Validate(IReadOnlySet<string>? assetKeys = null, IEnumerable<IValidationRule>? rules = null)
    {
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldBeEmpty("test setup: the edit must still load: " + string.Join("\n", load.Errors));
        return ContentValidator.Validate(load.Database!, _temp, assetKeys ?? RealAssetKeys(), rules ?? ContentValidator.DefaultRules);
    }

    private static ValidationIssue Single(ValidationReport report, string code)
    {
        var hits = report.Issues.Where(i => i.Code == code).ToList();
        hits.Count.ShouldBe(1, string.Join("\n", report.Issues));
        return hits[0];
    }

    private static void ShouldBe(ValidationIssue issue, string file, string path, ValidationSeverity severity = ValidationSeverity.Error)
    {
        issue.File.ShouldBe(file);
        issue.Path.ShouldBe(path);
        issue.Severity.ShouldBe(severity);
        issue.Line.ShouldBeGreaterThan(0);
        issue.Column.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Sample_content_has_no_errors()
    {
        var report = Validate();

        report.IsValid.ShouldBeTrue(string.Join("\n", report.Errors));
    }

    // ---- references --------------------------------------------------------------------------------------------

    [Fact]
    public void Renamed_item_in_quest_reward_is_an_error_naming_file_line_and_path()
    {
        Edit("quests/first_hunt.yaml", "item_id: item.healing_draught", "item_id: item.healing_draugth");

        var issue = Single(Validate(), ValidationCodes.RefMissing);

        ShouldBe(issue, "quests/first_hunt.yaml", "rewards[1].item_id");
        issue.Line.ShouldBe(19);
        issue.Message.ShouldContain("did you mean 'item.healing_draught'");
        issue.ToString().ShouldStartWith("quests/first_hunt.yaml:19:");
    }

    [Theory]
    [InlineData("jobs/wanderer.yaml", "skill_id: skill.second_wind", "skill_id: skill.nope", "skills[0].skill_id")]
    [InlineData("jobs/warden.yaml", "quest_ids: [quest.warden_trial]", "quest_ids: [quest.nope]", "requirements.quest_ids[0]")]
    [InlineData("jobs/wanderer.yaml", "job_exp_table_id: exp.job_wanderer", "job_exp_table_id: exp.nope", "job_exp_table_id")]
    [InlineData("jobs/spellbinder.yaml", "parent_job_id: job.wanderer", "parent_job_id: job.nope", "parent_job_id")]
    [InlineData("drops/gel_slime.yaml", "item.gel_glob", "item.nope", "entries[0].item_id")]
    [InlineData("npcs/guide.yaml", "dialogue_id: dialogue.guide_intro", "dialogue_id: dialogue.nope", "dialogue_id")]
    [InlineData("skills/call_seedling.yaml", "monster_id: monster.mossling", "monster_id: monster.nope", "effects[0].monster_id")]
    [InlineData("quests/warden_trial.yaml", "job_ids: [job.wanderer]", "job_ids: [job.nope]", "prerequisites.job_ids[0]")]
    [InlineData("quests/first_hunt.yaml", "monster_id: monster.gel_slime", "monster_id: monster.nope", "steps[0].objectives[0].monster_id")]
    [InlineData("quests/first_hunt.yaml", "giver_npc_id: npc.guide", "giver_npc_id: npc.nope", "giver_npc_id")]
    [InlineData("quests/gear_up.yaml", "map_id: map.starter_meadow", "map_id: map.nope", "steps[2].objectives[0].map_id")]
    [InlineData("quests/gear_up.yaml", "status_id: status.swift", "status_id: status.nope", "rewards[3].status_id")]
    [InlineData("items/iron_blade.yaml", "job_ids: [job.warden]", "job_ids: [job.nope]", "equip.job_ids[0]")]
    [InlineData("monsters/gel_slime.yaml", "drop.tier1_supplies", "drop.nope", "drop_table_ids[1]")]
    [InlineData("dialogue/guide_intro.yaml", "quest_id: quest.first_hunt", "quest_id: quest.nope", "nodes.greet.choices[0].conditions[0].quest_id")]
    public void Dangling_reference_is_reported(string file, string from, string to, string path)
    {
        Edit(file, from, to);

        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.RefMissing);

        ShouldBe(issue, file, path);
    }

    [Fact]
    public void Monster_ai_skill_must_exist()
    {
        Edit("monsters/gel_slime.yaml", "chase_range: 8", "chase_range: 8\n  skills:\n    - { skill_id: skill.nope, level: 1, chance: 0.1 }");

        ShouldBe(Single(Validate(), ValidationCodes.RefMissing), "monsters/gel_slime.yaml", "ai.skills[0].skill_id");
    }

    [Fact]
    public void Reference_to_an_id_of_another_type_says_so()
    {
        Edit("drops/gel_slime.yaml", "item.gel_glob", "monster.gel_slime");

        Single(Validate(), ValidationCodes.RefMissing).Message.ShouldContain("exists, but is not a item");
    }

    // ---- vocabulary --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("monsters/gel_slime.yaml", "element: water", "element: aqua", "element")]
    [InlineData("monsters/gel_slime.yaml", "race: formless", "race: blobby", "race")]
    [InlineData("monsters/gel_slime.yaml", "size: small", "size: tiny", "size")]
    [InlineData("monsters/gel_slime.yaml", "luk: 1,", "lucky: 1,", "stats.lucky")]
    [InlineData("items/iron_blade.yaml", "weapon_type: sword_1h", "weapon_type: swordish", "equip.weapon_type")]
    [InlineData("items/iron_blade.yaml", "slots: [weapon]", "slots: [hand]", "equip.slots[0]")]
    [InlineData("items/iron_blade.yaml", "stat: str", "stat: strength", "equip.modifiers[0].stat")]
    [InlineData("skills/call_seedling.yaml", "animation_id: cast", "animation_id: zap", "animation_id")]
    [InlineData("jobs/wanderer.yaml", "weapon_types: [unarmed, dagger]", "weapon_types: [unarmed, lance]", "weapon_types[1]")]
    [InlineData("jobs/spellbinder.yaml", "stat: max_sp", "stat: mana", "stat_bonuses[0].stat")]
    public void Value_outside_the_vocabulary_is_reported(string file, string from, string to, string path)
    {
        Edit(file, from, to);

        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.VocabUnknown);

        ShouldBe(issue, file, path);
    }

    [Fact]
    public void Unknown_element_in_the_element_table_is_reported_in_that_file()
    {
        Edit("elements.yaml", "\n  fire:\n    neutral: 1", "\n  flame:\n    neutral: 1");

        var issue = Single(Validate(), ValidationCodes.VocabUnknown);

        issue.File.ShouldBe("elements.yaml");
        issue.Path.ShouldBe("multipliers.flame");
        issue.Line.ShouldBeGreaterThan(0);
    }

    // ---- item kind ---------------------------------------------------------------------------------------------

    [Fact]
    public void Item_with_block_for_another_kind_is_reported()
    {
        Edit("items/iron_blade.yaml", "kind: equip", "kind: material");

        ShouldBe(Single(Validate(), ValidationCodes.ItemKindBlock), "items/iron_blade.yaml", "equip");
    }

    [Fact]
    public void Item_missing_the_block_for_its_kind_is_reported()
    {
        Edit("items/healing_draught.yaml", "kind: consumable", "kind: card");

        var issues = Validate().Issues.Where(i => i.Code == ValidationCodes.ItemKindBlock).ToList();

        issues.Select(i => i.Path).OrderBy(p => p).ShouldBe(new[] { "consumable", "kind" });
    }

    // ---- assets ------------------------------------------------------------------------------------------------

    [Fact]
    public void Asset_key_missing_from_the_manifest_is_reported()
    {
        Edit("items/iron_blade.yaml", "icon_asset_key: icon.item.iron_blade", "icon_asset_key: icon.item.nope");

        var issue = Single(Validate(), ValidationCodes.AssetMissing);

        ShouldBe(issue, "items/iron_blade.yaml", "icon_asset_key");
        issue.Message.ShouldContain("icon.item.nope");
    }

    [Fact]
    public void Asset_keys_are_checked_against_the_given_manifest()
    {
        var keys = RealAssetKeys().Where(k => k != "scene.map.starter_meadow").ToHashSet();

        ShouldBe(Single(Validate(keys), ValidationCodes.AssetMissing), "maps/starter_meadow.yaml", "scene_asset_key");
    }

    [Fact]
    public void Empty_required_asset_key_is_reported()
    {
        Edit("maps/starter_meadow.yaml", "scene_asset_key: scene.map.starter_meadow", "scene_asset_key: \"\"");

        ShouldBe(Single(Validate(), ValidationCodes.AssetEmpty), "maps/starter_meadow.yaml", "scene_asset_key");
    }

    [Fact]
    public void Manifest_loading_fails_readably()
    {
        Should.Throw<InvalidDataException>(() => AssetManifest.LoadKeys(Path.Combine(_temp, "nope.json")))
            .Message.ShouldContain("not found");
        var bad = Path.Combine(_temp, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Should.Throw<InvalidDataException>(() => AssetManifest.LoadKeys(bad)).Message.ShouldContain("invalid JSON");
    }

    // ---- skill tree / job chain --------------------------------------------------------------------------------

    [Fact]
    public void Skill_prerequisite_cycle_is_reported()
    {
        Edit("jobs/spellbinder.yaml", "  - skill_id: skill.ember_bolt\n    max_level: 10",
            "  - skill_id: skill.ember_bolt\n    max_level: 10\n    requires:\n      - { skill_id: skill.mana_font, level: 1 }");

        var issue = Single(Validate(), ValidationCodes.SkillCycle);

        ShouldBe(issue, "jobs/spellbinder.yaml", "skills[0]");
        issue.Message.ShouldContain("skill.ember_bolt -> skill.mana_font -> skill.ember_bolt");
    }

    [Fact]
    public void Skill_requiring_itself_is_a_cycle()
    {
        Edit("jobs/warden.yaml", "  - skill_id: skill.iron_guard\n    max_level: 3",
            "  - skill_id: skill.iron_guard\n    max_level: 3\n    requires:\n      - { skill_id: skill.iron_guard, level: 1 }");

        Single(Validate(), ValidationCodes.SkillCycle).Message.ShouldContain("skill.iron_guard -> skill.iron_guard");
    }

    [Fact]
    public void Skill_prerequisite_from_another_jobs_tree_is_reported()
    {
        Edit("jobs/spellbinder.yaml", "skill_id: skill.ember_bolt\n        level: 2", "skill_id: skill.twin_strike\n        level: 2");

        ShouldBe(Single(Validate(), ValidationCodes.SkillPrereqNotInTree), "jobs/spellbinder.yaml", "skills[1].requires[0].skill_id");
    }

    [Fact]
    public void Skill_prerequisite_above_max_level_is_reported()
    {
        Edit("jobs/warden.yaml", "skill_id: skill.twin_strike\n        level: 3", "skill_id: skill.twin_strike\n        level: 9");

        ShouldBe(Single(Validate(), ValidationCodes.SkillPrereqLevel), "jobs/warden.yaml", "skills[1].requires[0].level");
    }

    [Fact]
    public void Job_parent_loop_is_reported()
    {
        Edit("jobs/wanderer.yaml", "parent_job_id: null", "parent_job_id: job.warden");

        var issues = Validate().Issues.Where(i => i.Code == ValidationCodes.JobCycle).ToList();

        issues.Select(i => i.File).OrderBy(f => f).ShouldBe(new[] { "jobs/wanderer.yaml", "jobs/warden.yaml" });
    }

    // ---- dialogue ----------------------------------------------------------------------------------------------

    [Fact]
    public void Dangling_dialogue_choice_target_is_reported()
    {
        Edit("dialogue/guide_intro.yaml", "next: accepted", "next: nowhere");

        ShouldBe(Single(Validate(), ValidationCodes.DialogueDangling), "dialogue/guide_intro.yaml", "nodes.offer_hunt.choices[0].next");
    }

    // ---- shops (T-603) ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("  - item_id: item.healing_draught\n", "  - item_id: item.healing_draught\n    price: 0\n", "stock[0].price")]
    [InlineData("  - item_id: item.healing_draught\n", "  - item_id: item.healing_draught\n    price: -5\n", "stock[0].price")]
    [InlineData("  - item_id: item.healing_draught\n", "  - item_id: item.healing_draught\n    price: 1000000001\n", "stock[0].price")]
    [InlineData("  - item_id: item.antidote\n", "  - item_id: item.healing_draught\n", "stock[1].item_id")]   // a second Healing Draught row
    [InlineData("  - item_id: item.antidote\n", "  - item_id: item.gel_glob\n", "stock[1].price")]   // a material has no buy_price and the row gives none
    public void A_shop_with_a_bad_price_a_repeated_item_or_an_unpriced_item_is_an_error(string from, string to, string path)
    {
        Edit("shops/meadow_goods.yaml", from, to);

        var issue = Validate().Issues.First(i => i.Code == ValidationCodes.ShopInvalid);

        ShouldBe(issue, "shops/meadow_goods.yaml", path);
    }

    [Fact]
    public void A_shop_that_sells_nothing_or_an_unknown_item_is_an_error()
    {
        File.WriteAllText(Path.Combine(_temp, "shops", "empty.yaml"), "id: shop.empty\nname: Empty\nstock: []\n");
        ShouldBe(Single(Validate(), ValidationCodes.ShopInvalid), "shops/empty.yaml", "stock");

        File.Delete(Path.Combine(_temp, "shops", "empty.yaml"));
        Edit("shops/meadow_goods.yaml", "item.antidote", "item.antidot");
        ShouldBe(Single(Validate(), ValidationCodes.RefMissing), "shops/meadow_goods.yaml", "stock[1].item_id");
    }

    [Fact]
    public void The_shop_service_needs_a_real_shop_and_no_other_service_takes_one()
    {
        Edit("dialogue/merchant_intro.yaml", "shop_id: shop.meadow_goods", "shop_id: shop.nowhere");
        var missing = Single(Validate(), ValidationCodes.RefMissing);
        ShouldBe(missing, "dialogue/merchant_intro.yaml", "nodes.greet.choices[0].actions[0].shop_id");

        Edit("dialogue/merchant_intro.yaml", "            shop_id: shop.nowhere\n", "");
        ShouldBe(Single(Validate(), ValidationCodes.DialogueInvalid), "dialogue/merchant_intro.yaml", "nodes.greet.choices[0].actions[0].shop_id");
    }

    [Fact]
    public void A_refine_service_with_a_shop_id_is_an_error()
    {
        Edit("dialogue/smith_intro.yaml", "service: refine", "service: refine\n            shop_id: shop.meadow_goods");

        ShouldBe(Single(Validate(), ValidationCodes.DialogueInvalid), "dialogue/smith_intro.yaml", "nodes.greet.choices[0].actions[0].shop_id");
    }

    [Fact]
    public void A_buy_limit_below_one_is_an_error()
    {
        Edit("balance.yaml", "max_buy_quantity: 99", "max_buy_quantity: 0");

        Validate().Issues.ShouldContain(i => i.Path == "shop.max_buy_quantity" && i.Severity == ValidationSeverity.Error);
    }

    // ---- quests (T-602) ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_quest_whose_giver_has_no_start_quest_for_it_is_unreachable()
    {
        // Odalys' only start_quest for First Hunt now starts a quest she does not give: First Hunt can no longer be accepted from her.
        Edit("dialogue/guide_intro.yaml", "- type: start_quest\n            quest_id: quest.first_hunt", "- type: start_quest\n            quest_id: quest.warden_trial");

        var issues = Validate().Issues.Where(i => i.Code == ValidationCodes.QuestUnreachable).ToList();

        issues.ShouldContain(i => i.Path == "giver_npc_id" && i.Message.Contains("npc.guide has no dialogue choice"));   // First Hunt
        issues.ShouldContain(i => i.Path == "giver_npc_id" && i.Message.Contains("npc.guide's dialogue starts quest.warden_trial"));   // the Trial, offered by the wrong NPC
    }

    [Fact]
    public void A_turn_in_npc_that_is_not_placed_is_unreachable()
    {
        Edit("quests/warden_trial.yaml", "{ type: talk, npc_id: npc.trainer }", "{ type: talk, npc_id: npc.nobody_placed }");
        File.WriteAllText(Path.Combine(_temp, "npcs", "nobody_placed.yaml"),
            "id: npc.nobody_placed\nname: Nobody\ndialogue_id: dialogue.smith_intro\nmodel_asset_key: model.npc.guide\n");

        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.QuestUnreachable);

        ShouldBe(issue, "quests/warden_trial.yaml", "steps[2].objectives[0].npc_id");
    }

    [Theory]
    [InlineData("{ type: kill, monster_id: monster.sting_wasp, count: 3 }", "{ type: kill, monster_id: monster.sting_wasp, count: 0 }", "steps[1].objectives[0].count")]
    [InlineData("{ type: level, base_level: 10 }", "{ type: level, base_level: 0 }", "steps[0].objectives[0].base_level")]
    [InlineData("{ type: exp, base: 300, job: 150 }", "{ type: exp, base: -1, job: 150 }", "rewards[0]")]
    [InlineData("prerequisites:\n  min_base_level: 5", "prerequisites:\n  quest_ids: [quest.warden_oath]\n  min_base_level: 5", "prerequisites.quest_ids")]
    public void Quest_values_out_of_range_are_errors(string from, string to, string path)
    {
        Edit("quests/warden_trial.yaml", from, to);

        ShouldBe(Validate().Issues.First(i => i.Code == ValidationCodes.QuestInvalid && i.File == "quests/warden_trial.yaml"), "quests/warden_trial.yaml", path);
    }

    [Fact]
    public void Quest_conditions_are_checked_at_load()
    {
        Edit("dialogue/trainer_intro.yaml", "state: available\n        actions:\n          - type: start_quest\n            quest_id: quest.warden_trial",
            "state: underway\n        actions:\n          - type: start_quest\n            quest_id: quest.warden_trial");
        Edit("dialogue/trainer_intro.yaml", "      - text: Maybe later.", "      - text: Step five.\n        next: null\n        conditions:\n          - { type: quest_step, quest_id: quest.warden_trial, step: 5 }\n      - text: Maybe later.");

        var report = Validate();

        ShouldBe(Single(report, ValidationCodes.VocabUnknown), "dialogue/trainer_intro.yaml", "nodes.greet.choices[0].conditions[0].state");
        ShouldBe(Single(report, ValidationCodes.DialogueInvalid), "dialogue/trainer_intro.yaml", "nodes.greet.choices[4].conditions[0].step");
    }

    [Fact]
    public void Max_active_quests_must_be_at_least_one()
    {
        Edit("balance.yaml", "max_active: 10", "max_active: 0");

        Single(Validate(), ValidationCodes.BalanceRange).Message.ShouldContain("quests.max_active");
    }

    [Fact]
    public void Dangling_dialogue_start_is_reported()
    {
        Edit("dialogue/guide_intro.yaml", "start: greet", "start: hello");

        ShouldBe(Single(Validate(), ValidationCodes.DialogueDangling), "dialogue/guide_intro.yaml", "start");
    }

    [Fact]
    public void Unreachable_dialogue_node_is_a_warning()
    {
        File.AppendAllText(Path.Combine(_temp, "dialogue", "guide_intro.yaml"), "  orphan:\n    text: Nobody gets here.\n    next: null\n");

        var report = Validate();

        ShouldBe(Single(report, ValidationCodes.WarnDialogueUnreachable), "dialogue/guide_intro.yaml", "nodes.orphan", ValidationSeverity.Warning);
        report.IsValid.ShouldBeTrue();
    }

    // ---- dialogue conditions and actions (T-601) -------------------------------------------------------------------------------------

    private void AddChoice(string choiceYaml) =>
        File.AppendAllText(Path.Combine(_temp, "dialogue", "guide_intro.yaml"), "  extra:\n    text: More.\n    choices:\n      - text: Pick\n        next: null\n" + choiceYaml);

    [Theory]
    [InlineData("        conditions:\n          - type: has_item\n            item_id: item.nope\n", "conditions[0].item_id")]
    [InlineData("        conditions:\n          - type: job_is\n            job_id: job.nope\n", "conditions[0].job_id")]
    [InlineData("        conditions:\n          - type: job_is_not\n            job_id: job.nope\n", "conditions[0].job_id")]
    [InlineData("        conditions:\n          - type: has_status\n            status_id: status.nope\n", "conditions[0].status_id")]
    [InlineData("        actions:\n          - type: take_item\n            item_id: item.nope\n", "actions[0].item_id")]
    [InlineData("        actions:\n          - type: give_item\n            item_id: item.nope\n", "actions[0].item_id")]
    [InlineData("        actions:\n          - type: start_quest\n            quest_id: quest.nope\n", "actions[0].quest_id")]
    public void A_dialogue_condition_or_action_naming_something_missing_is_reported(string yaml, string path)
    {
        AddChoice(yaml);

        ShouldBe(Single(Validate(), ValidationCodes.RefMissing), "dialogue/guide_intro.yaml", "nodes.extra.choices[0]." + path);
    }

    [Theory]
    [InlineData("        conditions:\n          - type: base_level\n", "conditions[0]")]
    [InlineData("        conditions:\n          - type: job_level\n            min: 5\n            max: 2\n", "conditions[0]")]
    [InlineData("        conditions:\n          - type: has_item\n            item_id: item.gel_glob\n            quantity: 0\n", "conditions[0].quantity")]
    [InlineData("        actions:\n          - type: give_item\n            item_id: item.gel_glob\n            quantity: 0\n", "actions[0].quantity")]
    [InlineData("        actions:\n          - type: take_item\n            item_id: item.gel_glob\n            quantity: -2\n", "actions[0].quantity")]
    [InlineData("        actions:\n          - type: set_save_point\n            x: 999\n            y: 0\n", "actions[0]")]
    public void Unusable_dialogue_values_are_reported(string yaml, string path)
    {
        AddChoice(yaml);

        ShouldBe(Single(Validate(), ValidationCodes.DialogueInvalid), "dialogue/guide_intro.yaml", "nodes.extra.choices[0]." + path);
    }

    [Fact]
    public void An_unknown_service_is_reported()
    {
        AddChoice("        actions:\n          - type: open_service\n            service: bank\n");

        ShouldBe(Single(Validate(), ValidationCodes.VocabUnknown), "dialogue/guide_intro.yaml", "nodes.extra.choices[0].actions[0].service");
    }

    [Fact]
    public void A_save_point_needs_an_npc_with_the_dialogue_on_a_map()
    {
        AddChoice("        actions:\n          - type: set_save_point\n            x: 12\n            y: 12\n");
        File.WriteAllText(Path.Combine(_temp, "dialogue", "lonely.yaml"),
            "id: dialogue.lonely\nname: Lonely\nstart: a\nnodes:\n  a:\n    text: Hi.\n    choices:\n      - text: Home\n        next: null\n        actions:\n          - type: set_save_point\n            x: 12\n            y: 12\n");

        var issue = Single(Validate(), ValidationCodes.DialogueInvalid);

        issue.File.ShouldBe("dialogue/lonely.yaml");
        issue.Message.ShouldContain("NPC");
    }

    [Fact]
    public void A_dialogue_effect_the_runner_cannot_run_there_is_reported()
    {
        AddChoice("        actions:\n          - type: run_effects\n            effects:\n              - type: knockback\n");

        ShouldBe(Single(Validate(), ValidationCodes.EffectUnsupported), "dialogue/guide_intro.yaml", "nodes.extra.choices[0].actions[0].effects[0]");
    }

    [Fact]
    public void Resets_are_allowed_in_a_service_but_still_not_in_a_skill()
    {
        Validate().Issues.ShouldBeEmpty("the guide's reset choices are services: " + string.Join("\n", Validate().Issues));
        Edit("skills/second_wind.yaml", "effects:\n", "effects:\n  - type: reset_stats\n");

        Validate().Issues.Count(i => i.Code == ValidationCodes.EffectUnsupported).ShouldBe(1);
    }

    [Fact]
    public void The_talk_range_must_be_at_least_one_in_the_balance()
    {
        Edit("balance.yaml", "talk_range: 2", "talk_range: 0");

        ShouldBe(Single(Validate(), ValidationCodes.BalanceRange), "balance.yaml", "dialogue.talk_range");
    }

    // ---- formulas ----------------------------------------------------------------------------------------------

    [Fact]
    public void Unknown_variable_in_item_modifier_is_an_error()
    {
        Edit("items/iron_blade.yaml", "value: \"1\"", "value: \"foo + 1\"");

        var issue = Single(Validate(), FormulaErrorCodes.UnknownVariable);

        ShouldBe(issue, "items/iron_blade.yaml", "equip.modifiers[0].value");
        issue.Message.ShouldContain("foo");
    }

    [Fact]
    public void Syntax_error_in_formulas_yaml_is_reported_on_that_file()
    {
        Edit("formulas.yaml", "physical_damage: max(1,", "physical_damage: max(1,,");

        var issue = Validate().Errors.First(i => i.Path == "physical_damage");

        issue.File.ShouldBe("formulas.yaml");
        issue.Code.ShouldStartWith("formula.");
    }

    // ---- warnings ----------------------------------------------------------------------------------------------

    [Fact]
    public void File_name_that_differs_from_the_id_is_a_warning()
    {
        File.Move(Path.Combine(_temp, "items", "antidote.yaml"), Path.Combine(_temp, "items", "remedy.yaml"));

        var report = Validate();

        ShouldBe(Single(report, ValidationCodes.WarnFileName), "items/remedy.yaml", "id", ValidationSeverity.Warning);
        report.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Unused_item_is_a_warning()
    {
        Edit("drops/sting_wasp.yaml", "  - { item_id: item.wasp_stinger, chance: 0.45 }\n", "");
        var issue = Single(Validate(), ValidationCodes.WarnUnusedItem);

        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("item.wasp_stinger");
    }

    [Fact]
    public void Unused_status_is_a_warning()
    {
        Edit("quests/gear_up.yaml", "status_id: status.swift", "status_id: status.poison");
        Edit("items/swift_elixir.yaml", "status.swift", "status.poison");

        Validate().Issues.ShouldContain(i => i.Code == ValidationCodes.WarnUnusedStatus && i.Message.Contains("status.swift"));
    }

    [Fact]
    public void Skill_used_only_by_a_monster_does_not_warn()
    {
        Validate().Issues.ShouldNotContain(i => i.Code == ValidationCodes.WarnSkillNoJob);
    }

    [Fact]
    public void Skill_in_no_job_tree_and_no_monster_is_a_warning()
    {
        Edit("monsters/sting_wasp.yaml", "  skills:\n    - skill_id: skill.venom_sting\n      level: 1\n      chance: 0.25\n", "  skills: []\n");

        var issue = Single(Validate(), ValidationCodes.WarnSkillNoJob);

        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("skill.venom_sting");
    }

    [Fact]
    public void Monster_no_grid_spawns_is_a_warning()
    {
        foreach (var grid in Directory.GetFiles(Path.Combine(_temp, "maps"), "*.grid.json"))
            File.WriteAllText(grid, File.ReadAllText(grid).Replace("\"monster_id\": \"monster.gel_slime\"", "\"monster_id\": \"monster.mossling\""));

        var issue = Single(Validate(), ValidationCodes.WarnMonsterNotSpawned);

        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("monster.gel_slime");
    }

    [Fact]
    public void Npc_no_grid_places_is_a_warning()
    {
        foreach (var grid in Directory.GetFiles(Path.Combine(_temp, "maps"), "*.grid.json"))
            File.WriteAllText(grid, File.ReadAllText(grid).Replace("\"npc_id\": \"npc.trainer\"", "\"npc_id\": \"npc.guide\""));

        var issue = Single(Validate(), ValidationCodes.WarnNpcNotPlaced);

        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("npc.trainer");
    }

    [Theory]
    [InlineData("-100", true)]
    [InlineData("-250", true)]
    [InlineData("-99", false)]
    [InlineData("level * -100", false)]
    public void Percent_modifier_of_minus_100_or_less_is_a_warning(string value, bool warns)
    {
        Edit("items/bone_dirk.yaml", "op: percent, value: \"5\"", $"op: percent, value: \"{value}\"");

        var issues = Validate().Issues.Where(i => i.Code == ValidationCodes.WarnModifierPercentFloor).ToList();

        issues.Count.ShouldBe(warns ? 1 : 0, string.Join("\n", issues));
        if (warns) ShouldBe(issues[0], "items/bone_dirk.yaml", "equip.modifiers[0].value", ValidationSeverity.Warning);
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------

    private sealed class CustomRule : IValidationRule
    {
        public string Name => "custom";

        public void Validate(ValidationContext context) =>
            context.Error("item.antidote", "name", "custom.code", "plugged in");
    }

    private sealed class CrashingRule : IValidationRule
    {
        public string Name => "crashing";

        public void Validate(ValidationContext context) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Extra_rules_plug_in_and_report_with_location()
    {
        var report = Validate(rules: ContentValidator.DefaultRules.Append(new CustomRule()));

        var issue = Single(report, "custom.code");
        ShouldBe(issue, "items/antidote.yaml", "name");
        issue.Line.ShouldBe(3);
    }

    [Fact]
    public void Crashing_rule_becomes_an_error_and_does_not_stop_the_others()
    {
        var report = Validate(rules: new IValidationRule[] { new CrashingRule(), new CustomRule() });

        Single(report, ValidationCodes.RuleCrashed).Message.ShouldContain("crashing");
        Single(report, "custom.code");
    }

    [Fact]
    public void Text_output_groups_by_file_and_json_is_parseable()
    {
        Edit("quests/first_hunt.yaml", "item_id: item.healing_draught", "item_id: item.nope");
        var report = Validate();

        var text = ValidationReportFormatter.ToText(report.Issues);
        text.ShouldContain("quests/first_hunt.yaml:19:");
        text.ShouldContain("FAILED: 1 error(s)");
        text.Split('\n').Count(l => l.TrimEnd() == "quests/first_hunt.yaml").ShouldBe(1);

        using var json = JsonDocument.Parse(ValidationReportFormatter.ToJson(report.Issues));
        json.RootElement.GetProperty("valid").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("errors").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("issues").EnumerateArray()
            .ShouldContain(i => i.GetProperty("code").GetString() == "ref.missing" && i.GetProperty("line").GetInt32() == 19);
    }
}
