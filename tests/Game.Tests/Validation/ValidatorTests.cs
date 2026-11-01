using System.Text.Json;
using Game.Content.Loading;
using Game.Content.Validation;

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
    [InlineData("jobs/novice.yaml", "skill_id: skill.second_wind", "skill_id: skill.nope", "skills[0].skill_id")]
    [InlineData("jobs/warden.yaml", "quest_ids: [quest.warden_trial]", "quest_ids: [quest.nope]", "requirements.quest_ids[0]")]
    [InlineData("jobs/novice.yaml", "job_exp_table_id: exp.job_novice", "job_exp_table_id: exp.nope", "job_exp_table_id")]
    [InlineData("jobs/spellbinder.yaml", "parent_job_id: job.novice", "parent_job_id: job.nope", "parent_job_id")]
    [InlineData("drops/gel_slime.yaml", "item.gel_glob", "item.nope", "entries[0].item_id")]
    [InlineData("npcs/guide.yaml", "dialogue_id: dialogue.guide_intro", "dialogue_id: dialogue.nope", "dialogue_id")]
    [InlineData("skills/call_seedling.yaml", "monster_id: monster.mossling", "monster_id: monster.nope", "effects[0].monster_id")]
    [InlineData("quests/warden_trial.yaml", "job_ids: [job.novice]", "job_ids: [job.nope]", "prerequisites.job_ids[0]")]
    [InlineData("quests/first_hunt.yaml", "monster_id: monster.gel_slime", "monster_id: monster.nope", "steps[0].objectives[0].monster_id")]
    [InlineData("quests/first_hunt.yaml", "giver_npc_id: npc.guide", "giver_npc_id: npc.nope", "giver_npc_id")]
    [InlineData("quests/gear_up.yaml", "map_id: map.starter_meadow", "map_id: map.nope", "steps[2].objectives[0].map_id")]
    [InlineData("quests/gear_up.yaml", "status_id: status.swift", "status_id: status.nope", "rewards[3].status_id")]
    [InlineData("items/iron_blade.yaml", "job_ids: [job.warden]", "job_ids: [job.nope]", "equip.job_ids[0]")]
    [InlineData("monsters/gel_slime.yaml", "drop.tier1_supplies", "drop.nope", "drop_table_ids[1]")]
    [InlineData("dialogue/guide_intro.yaml", "start_quest_id: quest.first_hunt", "start_quest_id: quest.nope", "nodes.greet.choices[0].start_quest_id")]
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
    [InlineData("jobs/novice.yaml", "weapon_types: [unarmed, dagger]", "weapon_types: [unarmed, lance]", "weapon_types[1]")]
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
        Edit("jobs/novice.yaml", "parent_job_id: null", "parent_job_id: job.warden");

        var issues = Validate().Issues.Where(i => i.Code == ValidationCodes.JobCycle).ToList();

        issues.Select(i => i.File).OrderBy(f => f).ShouldBe(new[] { "jobs/novice.yaml", "jobs/warden.yaml" });
    }

    // ---- dialogue ----------------------------------------------------------------------------------------------

    [Fact]
    public void Dangling_dialogue_choice_target_is_reported()
    {
        Edit("dialogue/guide_intro.yaml", "next: accepted", "next: nowhere");

        ShouldBe(Single(Validate(), ValidationCodes.DialogueDangling), "dialogue/guide_intro.yaml", "nodes.greet.choices[0].next");
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
        var issue = Single(Validate(), ValidationCodes.WarnUnusedItem);

        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("item.bone_dirk");
    }

    [Fact]
    public void Unused_status_is_a_warning()
    {
        Edit("quests/gear_up.yaml", "status_id: status.swift", "status_id: status.poison");
        Edit("items/swift_elixir.yaml", "status.swift", "status.poison");

        Validate().Issues.ShouldContain(i => i.Code == ValidationCodes.WarnUnusedStatus && i.Message.Contains("status.swift"));
    }

    [Fact]
    public void Skill_in_no_job_tree_is_a_warning()
    {
        var issue = Single(Validate(), ValidationCodes.WarnSkillNoJob);

        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("skill.venom_sting");
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
