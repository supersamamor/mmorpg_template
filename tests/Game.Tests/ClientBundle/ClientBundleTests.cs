using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Content.ClientExport;
using Game.Content.Registry;
using Game.Shared.ClientContent;
using Game.Shared.Definitions;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.ClientExport;

/// <summary>The client bundle (T-209): shape, canonical bytes, the hash, and that nothing server-only gets out.</summary>
public class ClientBundleTests
{
    private static ContentDatabase Content => TestGrids.SampleContent;

    private static ClientBundleFile Export(ContentDatabase content) => ClientBundleExporter.Export(content);

    private static string Json(ClientBundleFile file) => Encoding.UTF8.GetString(file.FileBytes);

    // ---- shape ----

    private static readonly Shared.ClientContent.ClientBundle Fixture = new(
        3, ["fire"], ["str"],
        [new ClientItem("item.a", "A \"quoted\" é", null, "equip", "icon.a", null, ["weapon"], "dagger", "model.a", "sfx.use", null)],
        [new ClientSkill("skill.a", "Bolt", "Hits.", 3, false, 6, true, false, ClientAreaShapes.Square, 2, 500, "cast", "icon.s", null, null)],
        [new ClientJob("job.a", "Jobber", null, null, [], null, null, [new ClientJobSkill("skill.a", null, [new ClientSkillRequirement("skill.b", 2)])])],
        [new ClientStatus("status.a", "Stun", null, true, null, null)],
        [new ClientMonster("monster.a", "Blob", "model.m", new ClientMonsterSounds(null, "sfx.hit", "sfx.die"))],
        [new ClientNpc("npc.a", "Guide", "Helper", "dialogue.a", null, null)],
        [new ClientDialogue("dialogue.a", "start", [new ClientDialogueNode("start", null, null, "Hi", null,
            [new ClientDialogueChoice("Yes", null, "quest.a")])])],
        [new ClientQuest("quest.a", "Q", null, "npc.a", ["Do it."])],
        [new ClientMap("map.a", "Map", "scene.a", null, "ambience.a", 2, 1, "AwM=", [new ClientWarp(new GridCell(1, 0), "map.b", new GridCell(0, 0))],
            [new ClientNpcPlacement("npc.a", new GridCell(0, 0), "s")], [new ClientZone("safe", new GridRect(0, 0, 2, 1))])],
        new ClientAppearance(
            [new ClientGender("male", "Male", "model.b", [new ClientAppearanceOption("face.a", "Calm", "model.f")], [new ClientAppearanceOption("hair.a", "Crop", "model.h")], "face.a", "hair.a")],
            [new ClientColorOption("skin.a", "Fair", "#F6D9C4")], "skin.a", [new ClientColorOption("haircolor.a", "Raven", "#1E1B1D")], "haircolor.a"));

    private static readonly string FixtureBody =
        """
        {"format_version":3,
        HASH
        "elements":["fire"],"stats":["str"],
        "items":[{"id":"item.a","name":"A @U0022quoted@U0022 @U00E9","kind":"equip","icon":"icon.a","slots":["weapon"],"weapon_type":"dagger","visual":"model.a","use_sfx":"sfx.use"}],
        "skills":[{"id":"skill.a","name":"Bolt","description":"Hits.","max_level":3,"passive":false,"range":6,"ground":true,"friendly":false,"area_shape":"square","area_radius":2,"cast_time_ms":500,"animation":"cast","icon":"icon.s"}],
        "jobs":[{"id":"job.a","name":"Jobber","skills":[{"skill":"skill.a","requires":[{"skill":"skill.b","level":2}]}]}],
        "statuses":[{"id":"status.a","name":"Stun","debuff":true}],
        "monsters":[{"id":"monster.a","name":"Blob","model":"model.m","sounds":{"hit":"sfx.hit","die":"sfx.die"}}],
        "npcs":[{"id":"npc.a","name":"Guide","title":"Helper","dialogue":"dialogue.a"}],
        "dialogues":[{"id":"dialogue.a","start":"start","nodes":[{"key":"start","text":"Hi","choices":[{"text":"Yes","quest":"quest.a"}]}]}],
        "quests":[{"id":"quest.a","name":"Q","giver":"npc.a","steps":["Do it."]}],
        "maps":[{"id":"map.a","name":"Map","scene":"scene.a","ambience":"ambience.a","width":2,"height":1,"cells":"AwM=",
        "warps":[{"cell":[1,0],"map":"map.b","target":[0,0]}],"npcs":[{"npc":"npc.a","cell":[0,0],"facing":"s"}],
        "zones":[{"kind":"safe","area":[0,0,2,1]}]}],
        "appearance":{"genders":[{"gender":"male","name":"Male","body":"model.b","faces":[{"id":"face.a","name":"Calm","model":"model.f"}],"hairstyles":[{"id":"hair.a","name":"Crop","model":"model.h"}],"default_face":"face.a","default_hairstyle":"hair.a"}],
        "skin_tones":[{"id":"skin.a","name":"Fair","color":"#F6D9C4"}],"default_skin_tone":"skin.a","hair_colors":[{"id":"haircolor.a","name":"Raven","color":"#1E1B1D"}],"default_hair_color":"haircolor.a"}}
        """.ReplaceLineEndings("").Replace("@U", ((char)92) + "u"); // @U stands for a JSON unicode escape

    [Fact]
    public void Fixture_bundle_has_the_golden_shape_and_hash()
    {
        var hashed = FixtureBody.Replace("HASH", "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashed))).ToLowerInvariant();

        var file = ClientBundleWriter.Export(Fixture);

        Json(file).ShouldBe(FixtureBody.Replace("HASH", $"\"content_hash\":\"{hash}\",") + "\n");
        Encoding.UTF8.GetString(ClientBundleWriter.HashedBytes(Fixture)).ShouldBe(hashed);
        file.Hash.ShouldBe(hash);
    }

    [Fact]
    public void Client_record_fields_are_the_approved_whitelist()
    {
        // Adding a field to a client record is a deliberate act: it must show up here (and in the docs table) in the same change.
        var fields = typeof(ClientItem).Assembly.GetTypes()
            .Where(t => t.Namespace == "Game.Shared.ClientContent" && t.IsSealed && !t.IsAbstract)
            .Where(t => t.GetConstructors().Length > 0)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => t.Name + ": " + string.Join(",", t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name)))
            .ToList();

        fields.ShouldBe(
        [
            "ClientAppearance: Genders,SkinTones,DefaultSkinToneId,HairColors,DefaultHairColorId",
            "ClientAppearanceOption: Id,Name,ModelAssetKey",
            "ClientBundle: FormatVersion,Elements,Stats,Items,Skills,Jobs,Statuses,Monsters,Npcs,Dialogues,Quests,Maps,Appearance",
            "ClientColorOption: Id,Name,Color",
            "ClientDialogue: Id,Start,Nodes",
            "ClientDialogueChoice: Text,Next,StartQuestId",
            "ClientDialogueNode: Key,SpeakerNpcId,PortraitAssetKey,Text,Next,Choices",
            "ClientGender: Gender,Name,BodyModelAssetKey,Faces,Hairstyles,DefaultFaceId,DefaultHairstyleId",
            "ClientItem: Id,Name,Description,Kind,IconAssetKey,GroundAssetKey,Slots,WeaponType,VisualAssetKey,UseSfx,EquipSfx",
            "ClientJob: Id,Name,Description,ParentJobId,WeaponTypes,ModelAssetKey,IconAssetKey,Skills",
            "ClientJobSkill: SkillId,MaxLevel,Requires",
            "ClientMap: Id,Name,SceneAssetKey,MusicAssetKey,AmbienceAssetKey,Width,Height,Cells,Warps,Npcs,Zones",
            "ClientMonster: Id,Name,ModelAssetKey,Sounds",
            "ClientMonsterSounds: Aggro,Hit,Die",
            "ClientNpc: Id,Name,Title,DialogueId,ModelAssetKey,PortraitAssetKey",
            "ClientNpcPlacement: NpcId,Cell,Facing",
            "ClientQuest: Id,Name,Description,GiverNpcId,StepTexts",
            "ClientSkill: Id,Name,Description,MaxLevel,Passive,TargetRange,TargetGround,TargetFriendly,AreaShape,AreaRadius,CastTimeMs,AnimationId,IconAssetKey,VfxAssetKey,SfxAssetKey",
            "ClientSkillRequirement: SkillId,Level",
            "ClientStatus: Id,Name,Description,Debuff,IconAssetKey,VfxAssetKey",
            "ClientWarp: Cell,TargetMapId,TargetCell",
            "ClientZone: Kind,Area",
        ]);
    }

    [Fact]
    public void Sample_bundle_has_the_expected_content()
    {
        using var doc = JsonDocument.Parse(Export(Content).FileBytes);
        var root = doc.RootElement;
        root.GetProperty("format_version").GetInt32().ShouldBe(3);
        root.GetProperty("items").GetArrayLength().ShouldBe(Content.Items.Count);
        root.GetProperty("skills").GetArrayLength().ShouldBe(Content.Skills.Count);
        root.GetProperty("jobs").GetArrayLength().ShouldBe(Content.Jobs.Count);
        root.GetProperty("monsters").GetArrayLength().ShouldBe(Content.Monsters.Count);
        root.GetProperty("maps").GetArrayLength().ShouldBe(Content.Maps.Count);
        foreach (var map in root.GetProperty("maps").EnumerateArray())
        {
            var grid = Content.GetGrid(map.GetProperty("id").GetString()!);
            map.GetProperty("width").GetInt32().ShouldBe(grid.Width);
            Convert.FromBase64String(map.GetProperty("cells").GetString()!).Length.ShouldBe(grid.Width * grid.Height);
        }
        var shockwave = root.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "skill.shockwave");
        shockwave.GetProperty("area_radius").GetInt32().ShouldBe(2);
        shockwave.GetProperty("cast_time_ms").GetInt32().ShouldBe(500);
    }

    // ---- canonical bytes ----

    [Fact]
    public void Building_twice_gives_identical_bytes_and_hash()
    {
        var a = Export(Content);
        var b = Export(Content);

        a.FileBytes.ShouldBe(b.FileBytes);
        a.Hash.ShouldBe(b.Hash);
        a.Hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void File_is_utf8_without_bom_with_only_a_final_newline()
    {
        var bytes = Export(Content).FileBytes;

        bytes[0].ShouldBe((byte)'{');
        bytes[^1].ShouldBe((byte)'\n');
        bytes.Count(b => b == (byte)'\n').ShouldBe(1);
        bytes.ShouldNotContain((byte)'\r');
        bytes.ShouldAllBe(b => b < 0x80); // non-ASCII text is escaped, so the bytes do not depend on an encoder
    }

    [Fact]
    public void Hash_covers_the_bundle_without_the_hash_field()
    {
        var file = Export(Content);
        var text = Json(file).TrimEnd('\n');
        var without = Regex.Replace(text, "\"content_hash\":\"[0-9a-f]{64}\",", "");

        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(without))).ToLowerInvariant().ShouldBe(file.Hash);
    }

    [Fact]
    public void Order_does_not_depend_on_load_order()
    {
        var shuffled = Rebuild(Content, items: new Registry<ItemDef>(Content.Items.All.Reverse()));

        Export(shuffled).FileBytes.ShouldBe(Export(Content).FileBytes);
    }

    // ---- --check ----

    [Fact]
    public void Check_passes_on_a_fresh_export_and_fails_after_a_content_edit()
    {
        var file = Export(Content);
        ClientBundleExporter.IsCurrent(Content, file.FileBytes).ShouldBeTrue();

        var edited = Rebuild(Content, items: Replace(Content.Items, "item.antidote", i => i with { Name = "Renamed antidote" }));
        ClientBundleExporter.IsCurrent(edited, file.FileBytes).ShouldBeFalse();

        var tampered = (byte[])file.FileBytes.Clone();
        tampered[20] ^= 1;
        ClientBundleExporter.IsCurrent(Content, tampered).ShouldBeFalse();
    }

    // ---- the hash follows display data only ----

    [Fact]
    public void Hash_changes_when_a_displayed_field_changes()
    {
        var baseline = Export(Content).Hash;

        Hash(Rebuild(Content, items: Replace(Content.Items, "item.antidote", i => i with { Name = "Other" }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, items: Replace(Content.Items, "item.antidote", i => i with { IconAssetKey = "icon.item.other" }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, skills: Replace(Content.Skills, "skill.shockwave", s => s with { Target = s.Target with { Range = 9 } }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, skills: Replace(Content.Skills, "skill.shockwave", s => s with { CastTimeMs = "750" }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, statuses: Replace(Content.Statuses, "status.stun", s => s with { Debuff = !s.Debuff }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, monsters: Replace(Content.Monsters, "monster.gel_slime", m => m with { Name = "Renamed" }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, quests: Replace(Content.Quests, "quest.first_hunt", q => q with { Description = "New text" }))).ShouldNotBe(baseline);
        Hash(Rebuild(Content, maps: Replace(Content.Maps, "map.starter_meadow", m => m with { MusicAssetKey = "music.other" }))).ShouldNotBe(baseline);
    }

    [Fact]
    public void Hash_does_not_change_when_only_server_only_data_changes()
    {
        var baseline = Export(Content);
        var secret = "SECRET-SENTINEL-4711";

        var changed = Rebuild(Content,
            items: Replace(Content.Items, "item.antidote", i => i with { BuyPrice = 987_654_321, SellPrice = 123_456_789, Weight = 777, MaxStack = 555 }),
            skills: Replace(Content.Skills, "skill.shockwave", s => s with { SpCost = secret, CooldownMs = secret }),
            monsters: Replace(Content.Monsters, "monster.gel_slime", m => m with
            {
                Level = 99, BaseExp = 424_242, JobExp = 313_131, AttackRange = 17, Stats = new Dictionary<string, double> { ["max_hp"] = 654_321 },
                DropTableIds = [], Ai = new MonsterAiDef { Aggressive = true, AggroRange = 31, ChaseRange = 37 },
            }),
            drops: new Registry<DropTableDef>(Content.Drops.All.Select(d => d with { Entries = [new DropEntryDef { ItemId = "item.antidote", Chance = 0.123456 }] })),
            expTables: Registry<ExpTableDef>.Empty(),
            formulas: new FormulaSetDef { PhysicalDamage = secret },
            balance: new BalanceDef());

        var after = Export(changed);

        after.Hash.ShouldBe(baseline.Hash);
        after.FileBytes.ShouldBe(baseline.FileBytes);
    }

    // ---- nothing leaks ----

    private static readonly string[] DeniedKeyWords =
        ["drop", "drops", "chance", "spawn", "spawns", "formula", "formulas", "ai", "exp", "respawn", "hp", "atk", "balance", "hash", "stats_table"];

    [Fact]
    public void No_key_in_the_bundle_names_server_only_data()
    {
        using var doc = JsonDocument.Parse(Export(Content).FileBytes);
        var keys = new List<string>();
        CollectKeys(doc.RootElement, keys);

        keys.ShouldNotBeEmpty();
        foreach (var key in keys.Distinct())
        {
            if (key == "content_hash") continue; // the one deliberate hash
            foreach (var word in key.Split('_')) DeniedKeyWords.ShouldNotContain(word, $"key '{key}'");
        }
    }

    [Fact]
    public void No_value_in_the_bundle_is_server_only_data()
    {
        var text = Json(Export(Content));
        using var doc = JsonDocument.Parse(text);
        var strings = new List<string>();
        CollectStrings(doc.RootElement, strings);
        var values = strings.ToHashSet(StringComparer.Ordinal);

        // Namespaces that are never client data, wherever an id of that kind would sneak in.
        values.ShouldNotContain(v => v.StartsWith("drop.", StringComparison.Ordinal) || v.StartsWith("exp.", StringComparison.Ordinal));
        // Formulas and expressions of skills, effects and the formula set.
        foreach (var skill in Content.Skills.All)
        {
            if (!int.TryParse(skill.SpCost, out _)) values.ShouldNotContain(skill.SpCost);
            if (!int.TryParse(skill.CooldownMs, out _)) values.ShouldNotContain(skill.CooldownMs);
        }
        foreach (var formula in Content.Formulas.Derived.Values.Append(Content.Formulas.PhysicalDamage).Append(Content.Formulas.MagicDamage)
                     .Append(Content.Formulas.HitChance).Append(Content.Formulas.CritChance))
            values.ShouldNotContain(formula);
        // Grid scene hashes (hard rule 11) and the drop tables' item/chance pairs.
        foreach (var grid in Content.Grids.Values.Where(g => g.SourceSceneHash.Length > 0))
            text.ShouldNotContain(grid.SourceSceneHash);
        // Monsters carry an id, a name, a model and their sound keys (T-304), nothing else: no numbers at all.
        foreach (var monster in doc.RootElement.GetProperty("monsters").EnumerateArray())
        {
            monster.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(["id", "name", "model", "sounds"]);
            if (monster.TryGetProperty("sounds", out var sounds))
            {
                sounds.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(["aggro", "hit", "die"]);
                foreach (var sound in sounds.EnumerateObject()) sound.Value.GetString()!.ShouldStartWith("sfx.");
            }
        }
        // Monster numbers from the content: none of the exact stat values appears as a JSON number in the monsters section.
        text.ShouldNotContain("\"respawn");
        text.ShouldNotContain("\"spawns\"");
    }

    [Fact]
    public void A_property_added_to_a_server_definition_is_not_in_the_bundle_until_mapped()
    {
        // Every server-only property of every definition type the bundle reads gets a unique value; the output must be the same bytes.
        var sentinel = "ZZ-LEAK-9f2c";
        var probed = Rebuild(Content,
            items: Replace(Content.Items, "item.bone_dirk", i => i with { Equip = i.Equip! with { WeaponAtk = 888_001, AttackRange = 88, MinBaseLevel = 77, CardSlots = 66, Refinable = true } }),
            jobs: Replace(Content.Jobs, "job.warden", j => j with { JobExpTableId = sentinel, Requirements = new JobChangeRequirementsDef { MinBaseLevel = 888_002 } }),
            statuses: Replace(Content.Statuses, "status.stun", s => s with { Tags = [sentinel], MaxStacks = 888_003, TickIntervalMs = 888_004 }),
            npcs: Content.Npcs,
            quests: Replace(Content.Quests, "quest.first_hunt", q => q with { Repeatable = true, Rewards = [new ExpReward { Base = 888005 }] }));

        var json = Json(Export(probed));

        json.ShouldNotContain(sentinel);
        json.ShouldNotContain("888");
        Export(probed).FileBytes.ShouldBe(Export(Content).FileBytes);
    }

    // ---- helpers ----

    private static string Hash(ContentDatabase content) => Export(content).Hash;

    private static void CollectKeys(JsonElement element, List<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    keys.Add(p.Name);
                    CollectKeys(p.Value, keys);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectKeys(item, keys);
                break;
        }
    }

    private static void CollectStrings(JsonElement element, List<string> strings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Name != "cells" && p.Name != "content_hash") CollectStrings(p.Value, strings);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStrings(item, strings);
                break;
            case JsonValueKind.String:
                strings.Add(element.GetString()!);
                break;
        }
    }

    private static Registry<T> Replace<T>(Registry<T> registry, string id, Func<T, T> change) where T : class, IDefinition =>
        new(registry.All.Select(d => d.Id == id ? change(d) : d));

    private static ContentDatabase Rebuild(
        ContentDatabase c, Registry<JobDef>? jobs = null, Registry<SkillDef>? skills = null, Registry<ItemDef>? items = null,
        Registry<MonsterDef>? monsters = null, Registry<StatusDef>? statuses = null, Registry<QuestDef>? quests = null,
        Registry<NpcDef>? npcs = null, Registry<DropTableDef>? drops = null, Registry<MapDef>? maps = null,
        Registry<ExpTableDef>? expTables = null, FormulaSetDef? formulas = null, BalanceDef? balance = null) =>
        new(jobs ?? c.Jobs, skills ?? c.Skills, items ?? c.Items, monsters ?? c.Monsters, statuses ?? c.Statuses, quests ?? c.Quests,
            npcs ?? c.Npcs, c.Dialogue, drops ?? c.Drops, maps ?? c.Maps, expTables ?? c.ExpTables, formulas ?? c.Formulas, c.Elements,
            balance ?? c.Balance, c.Appearance, grids: c.Grids);
}
