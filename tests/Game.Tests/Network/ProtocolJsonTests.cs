using System.Text;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;

namespace Game.Tests.Network;

/// <summary>Golden JSON of every message type (ARCHITECTURE §10) and round trips through <see cref="ProtocolJson"/>.</summary>
public class ProtocolJsonTests
{
    private static readonly EntityRecord Walker = new(7, EntityKinds.Monster, "monster.gel_slime", new GridCell(3, 4), Directions.E, new GridCell(4, 4), 3.514);
    private static readonly EntityRecord Standing = new(42, EntityKinds.Player, null, new GridCell(12, 12), Directions.N, null, 0);
    private static readonly EntityRecord Hurt = new(7, EntityKinds.Monster, "monster.gel_slime", new GridCell(3, 4), Directions.E, null, 0, 0.37);
    private static readonly EntityRecord Corpse = new(8, EntityKinds.Monster, "monster.gel_slime", new GridCell(4, 4), Directions.W, null, 0, 0, Dead: true);
    private static readonly EntityRecord Loot = new(9, EntityKinds.Item, "item.gel_glob", new GridCell(4, 5), Directions.S, null, 0, Quantity: 2, Mine: true);

    public static TheoryData<string, ClientMessage, string> ClientGolden => new()
    {
        { "hello", new HelloMessage(1, "Ada"), """{"type":"hello","version":1,"name":"Ada"}""" },
        { "hello with content hash", new HelloMessage(1, "Ada", "ab12"), """{"type":"hello","version":1,"name":"Ada","content":"ab12"}""" },
        { "hello with a resume token", new HelloMessage(1, null, "ab12", "Qk3x-_9"), """{"type":"hello","version":1,"resume":"Qk3x-_9","content":"ab12"}""" },
        { "hello with a session", new HelloMessage(1, null, "ab12", null, "Zk3x-_9", "0198"), """{"type":"hello","version":1,"session":"Zk3x-_9","character":"0198","content":"ab12"}""" },
        { "move", new MoveMessage(new GridCell(12, 9)), """{"type":"move","cell":[12,9]}""" },
        { "stop", new StopMessage(), """{"type":"stop"}""" },
        { "attack", new AttackMessage(7), """{"type":"attack","target":7}""" },
        { "stop_attack", new StopAttackMessage(), """{"type":"stop_attack"}""" },
        { "respawn", new RespawnMessage(), """{"type":"respawn"}""" },
        { "ping", new PingMessage(7), """{"type":"ping","seq":7}""" },
        { "logout", new LogoutMessage(), """{"type":"logout"}""" },
        { "pick_up", new PickUpMessage(9), """{"type":"pick_up","target":9}""" },
        { "use_item", new UseItemMessage(12), """{"type":"use_item","entry":12}""" },
        { "equip", new EquipMessage(12), """{"type":"equip","entry":12}""" },
        { "equip into a slot", new EquipMessage(12, "accessory_2"), """{"type":"equip","entry":12,"slot":"accessory_2"}""" },
        { "unequip", new UnequipMessage("weapon"), """{"type":"unequip","slot":"weapon"}""" },
        { "drop_item", new DropItemMessage(12, 2), """{"type":"drop_item","entry":12,"quantity":2}""" },
        { "insert_card", new InsertCardMessage(7, 12), """{"type":"insert_card","entry":7,"card":12}""" },
        { "refine", new RefineMessage(7), """{"type":"refine","entry":7}""" },
        { "use_skill on an entity", new UseSkillMessage("skill.twin_strike", null, 7), """{"type":"use_skill","skill":"skill.twin_strike","target":7}""" },
        { "use_skill on the ground at a level", new UseSkillMessage("skill.mana_font", 2, null, new GridCell(5, 6)), """{"type":"use_skill","skill":"skill.mana_font","level":2,"cell":[5,6]}""" },
        { "learn_skill", new LearnSkillMessage("skill.twin_strike"), """{"type":"learn_skill","skill":"skill.twin_strike"}""" },
        { "change_job", new ChangeJobMessage("job.spellbinder"), """{"type":"change_job","job":"job.spellbinder"}""" },
        { "raise_stat", new RaiseStatMessage("str", 2), """{"type":"raise_stat","stat":"str","amount":2}""" },
        { "talk", new TalkMessage(12), """{"type":"talk","target":12}""" },
        { "dialogue_choice", new DialogueChoiceMessage(2), """{"type":"dialogue_choice","index":2}""" },
        { "dialogue_close", new DialogueCloseMessage(), """{"type":"dialogue_close"}""" },
        { "abandon_quest", new AbandonQuestMessage("quest.first_hunt"), """{"type":"abandon_quest","quest":"quest.first_hunt"}""" },
        { "shop_buy", new ShopBuyMessage("item.healing_draught", 5), """{"type":"shop_buy","item":"item.healing_draught","quantity":5}""" },
        { "shop_sell", new ShopSellMessage(12, 3), """{"type":"shop_sell","entry":12,"quantity":3}""" },
        {
            "start_autopilot", new StartAutopilotMessage(["quest.first_hunt", "quest.gear_up"], [new SkillLevelRecord("skill.twin_strike", 2), new SkillLevelRecord("skill.second_wind", 1)]),
            """{"type":"start_autopilot","quests":["quest.first_hunt","quest.gear_up"],"skills":[["skill.twin_strike",2],["skill.second_wind",1]]}"""
        },
        { "start_autopilot without skills", new StartAutopilotMessage(["quest.first_hunt"], []), """{"type":"start_autopilot","quests":["quest.first_hunt"],"skills":[]}""" },
        {
            "start_autopilot, auto-battle (T-811)", new StartAutopilotMessage([], [new SkillLevelRecord("skill.twin_strike", 0)], "nearby"),
            """{"type":"start_autopilot","quests":[],"skills":[["skill.twin_strike",0]],"battle":"nearby"}"""
        },
        { "stop_autopilot", new StopAutopilotMessage(), """{"type":"stop_autopilot"}""" },
        { "set_outfit", new SetOutfitMessage("head", "outfit.rain_cloak"), """{"type":"set_outfit","slot":"head","outfit":"outfit.rain_cloak"}""" },
        { "set_outfit to nothing", new SetOutfitMessage("head"), """{"type":"set_outfit","slot":"head"}""" },
        { "set_outfit hides them all", new SetOutfitMessage(null, null, true), """{"type":"set_outfit","hidden":true}""" },
        { "chat map", new ChatMessage("map", null, "hello"), """{"type":"chat","channel":"map","text":"hello"}""" },
        { "chat whisper", new ChatMessage("whisper", "Ada", "café <b>"), """{"type":"chat","channel":"whisper","to":"Ada","text":"caf\u00E9 \u003Cb\u003E"}""" },
        { "block", new BlockMessage("Ada"), """{"type":"block","name":"Ada"}""" },
        { "unblock", new UnblockMessage("Ada"), """{"type":"unblock","name":"Ada"}""" },
        { "report", new ReportMessage(812), """{"type":"report","line":812}""" },
        { "party_invite by name", new PartyInviteMessage("Bo", null), """{"type":"party_invite","name":"Bo"}""" },
        { "party_invite by tap", new PartyInviteMessage(null, 43), """{"type":"party_invite","target":43}""" },
        { "party_answer", new PartyAnswerMessage(true), """{"type":"party_answer","accept":true}""" },
        { "party_answer no", new PartyAnswerMessage(false), """{"type":"party_answer","accept":false}""" },
        { "party_leave", new PartyLeaveMessage(), """{"type":"party_leave"}""" },
        { "party_kick", new PartyKickMessage(43), """{"type":"party_kick","target":43}""" },
        { "party_lead", new PartyLeadMessage(43), """{"type":"party_lead","target":43}""" },
        { "trade_ask", new TradeAskMessage(43), """{"type":"trade_ask","target":43}""" },
        { "trade_answer", new TradeAnswerMessage(true), """{"type":"trade_answer","accept":true}""" },
        { "trade_offer", new TradeOfferMessage(1207, 3), """{"type":"trade_offer","entry":1207,"quantity":3}""" },
        { "trade_offer out", new TradeOfferMessage(1207, 0), """{"type":"trade_offer","entry":1207,"quantity":0}""" },
        { "trade_currency", new TradeCurrencyMessage(5_000_000_000), """{"type":"trade_currency","currency":5000000000}""" },
        { "trade_lock", new TradeLockMessage(), """{"type":"trade_lock"}""" },
        { "trade_unlock", new TradeUnlockMessage(), """{"type":"trade_unlock"}""" },
        { "trade_confirm", new TradeConfirmMessage(7), """{"type":"trade_confirm","version":7}""" },
        { "trade_cancel", new TradeCancelMessage(), """{"type":"trade_cancel"}""" },
        { "guild_found", new GuildFoundMessage("Iron Oaks", "icon.emblem.oak"), """{"type":"guild_found","name":"Iron Oaks","emblem":"icon.emblem.oak"}""" },
        { "guild_invite by name", new GuildInviteMessage("Bo", null), """{"type":"guild_invite","name":"Bo"}""" },
        { "guild_invite by tap", new GuildInviteMessage(null, 43), """{"type":"guild_invite","target":43}""" },
        { "guild_answer", new GuildAnswerMessage(true), """{"type":"guild_answer","accept":true}""" },
        { "guild_leave", new GuildLeaveMessage(), """{"type":"guild_leave"}""" },
        { "guild_kick", new GuildKickMessage(12), """{"type":"guild_kick","member":12}""" },
        { "guild_rank", new GuildRankMessage(12, 1), """{"type":"guild_rank","member":12,"rank":1}""" },
        { "guild_ranks", new GuildRanksMessage([new GuildRankRecord("Officer", ["invite", "kick"]), new GuildRankRecord("Member", [])]),
            """{"type":"guild_ranks","ranks":[{"name":"Officer","permissions":["invite","kick"]},{"name":"Member","permissions":[]}]}""" },
        { "guild_lead", new GuildLeadMessage(12), """{"type":"guild_lead","member":12}""" },
        { "guild_notice", new GuildNoticeMessage("Meet at the well"), """{"type":"guild_notice","text":"Meet at the well"}""" },
        { "guild_storage_open", new GuildStorageOpenMessage(), """{"type":"guild_storage_open"}""" },
        { "guild_deposit", new GuildDepositMessage(981, 5, 0), """{"type":"guild_deposit","entry":981,"quantity":5}""" },
        { "guild_withdraw", new GuildWithdrawMessage(null, 0, 300), """{"type":"guild_withdraw","currency":300}""" },
    };

    public static TheoryData<string, ServerMessage, string> ServerGolden => new()
    {
        { "welcome", new WelcomeMessage(1, 42, "map.starter_meadow", 10), """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10}""" },
        { "welcome with content hash", new WelcomeMessage(1, 42, "map.starter_meadow", 10, "ab12"), """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10,"content":"ab12"}""" },
        {
            "welcome with a resume token", new WelcomeMessage(1, 42, "map.starter_meadow", 10, "ab12", "Qk3x-_9"),
            """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10,"content":"ab12","token":"Qk3x-_9"}"""
        },
        {
            "snapshot", new SnapshotMessage(120, "map.starter_meadow", 42, [Walker, Standing]),
            """{"type":"snapshot","tick":120,"map":"map.starter_meadow","id":42,"entities":[""" +
            """{"id":7,"kind":"monster","display_key":"monster.gel_slime","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51},""" +
            """{"id":42,"kind":"player","cell":[12,12],"facing":"n"}]}"""
        },
        {
            "delta", new DeltaMessage(121, [Standing],
                [
                    new ChangeRecord(7, ChangedFields.Cell | ChangedFields.Next, new GridCell(4, 4), null, new GridCell(5, 4), 0, null),
                    new ChangeRecord(8, ChangedFields.Cell | ChangedFields.Next | ChangedFields.Speed, new GridCell(5, 4), null, null, 0, null),
                    new ChangeRecord(9, ChangedFields.Facing | ChangedFields.DisplayKey, default, Directions.Sw, null, 0, null),
                ], [3, 5]),
            """{"type":"delta","tick":121,"appeared":[{"id":42,"kind":"player","cell":[12,12],"facing":"n"}],"changed":[{"id":7,"cell":[4,4],"next":[5,4]},{"id":8,"cell":[5,4],"next":null,"speed":0},{"id":9,"facing":"sw","display_key":null}],"disappeared":[3,5]}"""
        },
        { "delta, only gone", new DeltaMessage(5, [], [], [9]), """{"type":"delta","tick":5,"disappeared":[9]}""" },
        {
            "snapshot with combat: HP bar, a corpse, a ground item, own vitals",
            new SnapshotMessage(130, "map.starter_meadow", 42, [Hurt, Corpse, Loot, Standing with { Hp = 0.8 }], new VitalsRecord(40, 50, 9, 11)),
            """{"type":"snapshot","tick":130,"map":"map.starter_meadow","id":42,"entities":[""" +
            """{"id":7,"kind":"monster","display_key":"monster.gel_slime","cell":[3,4],"facing":"e","hp":0.37},""" +
            """{"id":8,"kind":"monster","display_key":"monster.gel_slime","cell":[4,4],"facing":"w","hp":0,"dead":true},""" +
            """{"id":9,"kind":"item","display_key":"item.gel_glob","cell":[4,5],"facing":"s","quantity":2,"mine":true},""" +
            """{"id":42,"kind":"player","cell":[12,12],"facing":"n","hp":0.8}],"vitals":{"hp":40,"max_hp":50,"sp":9,"max_sp":11}}"""
        },
        {
            "delta with combat: hits, HP, death, loot priority over, own vitals",
            new DeltaMessage(131, [],
                [
                    new ChangeRecord(7, ChangedFields.Hp, default, null, null, 0, null, Hp: 0.12),
                    new ChangeRecord(8, ChangedFields.Hp | ChangedFields.Dead, default, null, null, 0, null, Hp: 0, Dead: true),
                    new ChangeRecord(9, ChangedFields.Mine, default, null, null, 0, null, Mine: true),
                ], [],
                [new HitRecord(42, 7, 13, "crit"), new HitRecord(7, 42, 0, "miss")], new VitalsRecord(38, 50, 9, 11)),
            """{"type":"delta","tick":131,"changed":[{"id":7,"hp":0.12},{"id":8,"hp":0,"dead":true},{"id":9,"mine":true}],"hits":""" +
            """[[42,7,13,"crit"],[7,42,0,"miss"]],"vitals":{"hp":38,"max_hp":50,"sp":9,"max_sp":11}}"""
        },
        { "delta, only hits", new DeltaMessage(6, [], [], [], [new HitRecord(3, 4, 5, "hit")]), """{"type":"delta","tick":6,"hits":[[3,4,5,"hit"]]}""" },
        {
            "autopilot running", new AutopilotMessage("running", "quest.first_hunt", "hunting", null),
            """{"type":"autopilot","mode":"running","quest":"quest.first_hunt","doing":"hunting"}"""
        },
        { "autopilot paused", new AutopilotMessage("paused", "quest.gear_up", null, "needs_equip"), """{"type":"autopilot","mode":"paused","quest":"quest.gear_up","reason":"needs_equip"}""" },
        { "autopilot off", new AutopilotMessage("off", null, null, "finished"), """{"type":"autopilot","mode":"off","reason":"finished"}""" },
        { "autopilot, auto-battle (T-811)", new AutopilotMessage("running", null, "fighting", null), """{"type":"autopilot","mode":"running","doing":"fighting"}""" },
        {
            "wardrobe", new WardrobeMessage(["outfit.harvest_garland", "outfit.rain_cloak"], [new ShownOutfit("head", "outfit.rain_cloak"), new ShownOutfit("body", "outfit.harvest_garland")], true),
            """{"type":"wardrobe","owned":["outfit.harvest_garland","outfit.rain_cloak"],"chosen":{"head":"outfit.rain_cloak","body":"outfit.harvest_garland"},"hidden":true}"""
        },
        { "wardrobe, empty", new WardrobeMessage([], [], false), """{"type":"wardrobe","owned":[],"chosen":{}}""" },
        {
            "rejected set_outfit", new RejectedMessage(MessageTypes.SetOutfit, OutfitReasons.NotOwned, null, Slot: "head", Item: "outfit.rain_cloak"),
            """{"type":"rejected","intent":"set_outfit","reason":"not_owned","slot":"head","item":"outfit.rain_cloak"}"""
        },
        {
            "snapshot and delta with outfits", new DeltaMessage(9, [Standing with { Outfit = [new ShownOutfit("head", "outfit.rain_cloak")] }],
                [new ChangeRecord(43, ChangedFields.Outfit, default, null, null, 0, null, Outfit: [new ShownOutfit("back", "outfit.rain_cloak")]),
                    new ChangeRecord(44, ChangedFields.Outfit, default, null, null, 0, null, Outfit: [])], []),
            """{"type":"delta","tick":9,"appeared":[{"id":42,"kind":"player","cell":[12,12],"facing":"n","outfit":{"head":"outfit.rain_cloak"}}],"changed":""" +
            """[{"id":43,"outfit":{"back":"outfit.rain_cloak"}},{"id":44,"outfit":{}}]}"""
        },
        { "rejected", new RejectedMessage("move", "no_path", new GridCell(30, 2)), """{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}""" },
        {
            "rejected attack", new RejectedMessage("attack", "target_gone", null, 99),
            """{"type":"rejected","intent":"attack","reason":"target_gone","target":99}"""
        },
        {
            "delta with casts: a cast bar opens and one closes",
            new DeltaMessage(7, [], [], [], null, null, [new CastRecord(7, "skill.ember_bolt", 993)], [new CastEndRecord(9, "skill.venom_sting", "done")]),
            """{"type":"delta","tick":7,"casts":[[7,"skill.ember_bolt",993]],"cast_ends":[[9,"skill.venom_sting","done"]]}"""
        },
        {
            "skills", new SkillsMessage("job.wanderer", 3, [new SkillLevelRecord("skill.second_wind", 2)], [new SkillCooldownRecord("skill.second_wind", 4000)], ["job.spellbinder"]),
            """{"type":"skills","job":"job.wanderer","points":3,"levels":[["skill.second_wind",2]],"cooldowns":[["skill.second_wind",4000]],"jobs":["job.spellbinder"]}"""
        },
        { "skills, nothing yet", new SkillsMessage("job.wanderer", 0, [], [], []), """{"type":"skills","job":"job.wanderer","points":0,"levels":[],"cooldowns":[],"jobs":[]}""" },
        { "cooldown", new CooldownMessage("skill.twin_strike", 1500), """{"type":"cooldown","skill":"skill.twin_strike","ms":1500}""" },
        {
            "dialogue", new DialogueMessage(12, "npc.guide", null, "Hello.", [new DialogueChoiceRecord(0, "Hi"), new DialogueChoiceRecord(2, "Bye")], false),
            """{"type":"dialogue","npc":12,"speaker":"npc.guide","text":"Hello.","choices":[{"index":0,"text":"Hi"},{"index":2,"text":"Bye"}]}"""
        },
        {
            "dialogue continue", new DialogueMessage(12, "npc.guide", "portrait.x", "Go.", [], true),
            """{"type":"dialogue","npc":12,"speaker":"npc.guide","portrait":"portrait.x","text":"Go.","choices":[],"more":true}"""
        },
        { "dialogue_ended", new DialogueEndedMessage(DialogueEndReasons.MovedAway), """{"type":"dialogue_ended","reason":"moved_away"}""" },
        { "service_opened", new ServiceOpenedMessage(ServiceIds.Refine, 12), """{"type":"service_opened","service":"refine","npc":12}""" },
        {
            "shop", new ShopMessage(12, "shop.meadow_goods", 99, [new ShopStockRecord("item.healing_draught", 40), new ShopStockRecord("item.bone_dirk", 100)], [new ShopStockRecord("item.gel_glob", 4)]),
            """{"type":"shop","npc":12,"shop":"shop.meadow_goods","max_quantity":99,"stock":[{"item":"item.healing_draught","price":40},{"item":"item.bone_dirk","price":100}],"sell":[{"item":"item.gel_glob","price":4}]}"""
        },
        { "shop, empty", new ShopMessage(12, "shop.x", 1, [], []), """{"type":"shop","npc":12,"shop":"shop.x","max_quantity":1,"stock":[],"sell":[]}""" },
        {
            "rejected shop_buy", new RejectedMessage(MessageTypes.ShopBuy, ItemReasons.CannotAfford, null, Item: "item.healing_draught"),
            """{"type":"rejected","intent":"shop_buy","reason":"cannot_afford","item":"item.healing_draught"}"""
        },
        {
            "rejected shop_sell", new RejectedMessage(MessageTypes.ShopSell, ItemReasons.NotSellable, null, Entry: 12),
            """{"type":"rejected","intent":"shop_sell","reason":"not_sellable","entry":12}"""
        },
        {
            "stats", new StatsMessage(4, [new StatRecord("str", 3, 2), new StatRecord("luk", 99, 0)], [new DerivedRecord("atk", 12), new DerivedRecord("max_hp", 55)]),
            """{"type":"stats","points":4,"stats":[["str",3,2],["luk",99,0]],"derived":[["atk",12],["max_hp",55]]}"""
        },
        { "stats, nothing yet", new StatsMessage(0, [], []), """{"type":"stats","points":0,"stats":[],"derived":[]}""" },
        {
            "rejected talk", new RejectedMessage(MessageTypes.Talk, DialogueReasons.Unreachable, null, 12),
            """{"type":"rejected","intent":"talk","reason":"unreachable","target":12}"""
        },
        {
            "rejected raise_stat", new RejectedMessage(MessageTypes.RaiseStat, StatReasons.NotEnoughPoints, null, Stat: "str"),
            """{"type":"rejected","intent":"raise_stat","reason":"not_enough_points","stat":"str"}"""
        },
        {
            "rejected skill intent", new RejectedMessage("use_skill", "on_cooldown", null, 7, Skill: "skill.twin_strike"),
            """{"type":"rejected","intent":"use_skill","reason":"on_cooldown","target":7,"skill":"skill.twin_strike"}"""
        },
        { "attack_stopped", new AttackStoppedMessage(7, "target_dead"), """{"type":"attack_stopped","target":7,"reason":"target_dead"}""" },
        {
            "exp", new ExpMessage(2, 5, 24, 1, 4, 10),
            """{"type":"exp","base_level":2,"base_exp":5,"base_next":24,"job_level":1,"job_exp":4,"job_next":10}"""
        },
        { "level_up", new LevelUpMessage(3, null), """{"type":"level_up","base_level":3}""" },
        { "level_up, job", new LevelUpMessage(null, 2), """{"type":"level_up","job_level":2}""" },
        { "you_died", new YouDiedMessage(7), """{"type":"you_died","killer":7}""" },
        { "you_died, no killer", new YouDiedMessage(null), """{"type":"you_died"}""" },
        { "respawned", new RespawnedMessage("map.starter_meadow", new GridCell(12, 12), 26), """{"type":"respawned","map":"map.starter_meadow","cell":[12,12],"hp":26}""" },
        {
            "rejected item intent", new RejectedMessage("equip", "level_too_low", null, null, 12, "weapon"),
            """{"type":"rejected","intent":"equip","reason":"level_too_low","entry":12,"slot":"weapon"}"""
        },
        {
            "players with a weapon and worn items",
            new SnapshotMessage(140, "map.starter_meadow", 42, [Standing with { Weapon = "dagger", Worn = ["item.bone_dirk", "item.leather_cap"] }]),
            """{"type":"snapshot","tick":140,"map":"map.starter_meadow","id":42,"entities":[""" +
            """{"id":42,"kind":"player","cell":[12,12],"facing":"n","weapon":"dagger","worn":["item.bone_dirk","item.leather_cap"]}]}"""
        },
        {
            "delta: a player took its weapon off",
            new DeltaMessage(141, [], [new ChangeRecord(42, ChangedFields.Look, default, null, null, 0, null, Weapon: "unarmed", Worn: [])], []),
            """{"type":"delta","tick":141,"changed":[{"id":42,"weapon":"unarmed","worn":[]}]}"""
        },
        {
            "inventory", new InventoryMessage(4, 100, 45, 2040, "normal", [new InventoryEntryRecord(1, "item.bone_dirk", 1, "weapon"), new InventoryEntryRecord(2, "item.healing_draught", 3)]),
            """{"type":"inventory","rev":4,"slots":100,"weight":45,"weight_limit":2040,"load":"normal","currency":0,"entries":[""" +
            """{"id":1,"item":"item.bone_dirk","quantity":1,"slot":"weapon"},{"id":2,"item":"item.healing_draught","quantity":3}]}"""
        },
        {
            "inventory_changed", new InventoryChangedMessage(5, "used", "item.healing_draught", 40, 2040, "normal", [new InventoryEntryRecord(2, "item.healing_draught", 2)], [7]),
            """{"type":"inventory_changed","rev":5,"cause":"used","item":"item.healing_draught","weight":40,"weight_limit":2040,"load":"normal","currency":0,""" +
            "\"entries\":[{\"id\":2,\"item\":\"item.healing_draught\",\"quantity\":2}],\"removed\":[7]}"
        },
        {
            "inventory_changed, weight only", new InventoryChangedMessage(6, "weight", null, 40, 2070, "normal", [], []),
            """{"type":"inventory_changed","rev":6,"cause":"weight","weight":40,"weight_limit":2070,"load":"normal","currency":0}"""
        },
        {
            "inventory with a socketed, refined weapon (owner only)",
            new InventoryMessage(8, 100, 45, 2040, "normal", [new InventoryEntryRecord(1, "item.iron_blade", 1, "weapon", ["item.gelcap_card"], 3)]),
            """{"type":"inventory","rev":8,"slots":100,"weight":45,"weight_limit":2040,"load":"normal","currency":0,"entries":[""" +
            """{"id":1,"item":"item.iron_blade","quantity":1,"slot":"weapon","cards":["item.gelcap_card"],"refine":3}]}"""
        },
        {
            "inventory_changed after a refine", new InventoryChangedMessage(9, "refined", "item.padded_vest", 43, 2040, "normal",
                [new InventoryEntryRecord(3, "item.padded_vest", 1, null, null, 2)], [5]),
            """{"type":"inventory_changed","rev":9,"cause":"refined","item":"item.padded_vest","weight":43,"weight_limit":2040,"load":"normal","currency":0,""" +
            "\"entries\":[{\"id\":3,\"item\":\"item.padded_vest\",\"quantity\":1,\"refine\":2}],\"removed\":[5]}"
        },
        {
            "refine_result", new RefineResultMessage(3, "item.padded_vest", "success", 2),
            """{"type":"refine_result","entry":3,"item":"item.padded_vest","result":"success","refine":2}"""
        },
        {
            "rejected insert_card", new RejectedMessage("insert_card", "card_does_not_fit", null, null, 3),
            """{"type":"rejected","intent":"insert_card","reason":"card_does_not_fit","entry":3}"""
        },
        {
            "inventory_changed after a quest turn-in, with currency (T-602)", new InventoryChangedMessage(10, "quest", null, 52, 2040, "normal",
                [new InventoryEntryRecord(4, "item.healing_draught", 5)], [6], 150),
            """{"type":"inventory_changed","rev":10,"cause":"quest","weight":52,"weight_limit":2040,"load":"normal","currency":150,""" +
            "\"entries\":[{\"id\":4,\"item\":\"item.healing_draught\",\"quantity\":5}],\"removed\":[6]}"
        },
        {
            "quest_log (T-602)",
            new QuestLogMessage([new QuestEntryRecord("quest.first_hunt", 0,
                [new QuestObjectiveRecord("kill", "monster.gel_slime", 3, 5), new QuestObjectiveRecord("collect", "item.gel_glob", 3, 3), new QuestObjectiveRecord("talk", "npc.guide", 0, 1)]),
                new QuestEntryRecord("quest.warden_trial", 0, [new QuestObjectiveRecord("level", null, 6, 10)])],
                ["quest.gear_up"], ["npc.trainer"], ["npc.guide"]),
            """{"type":"quest_log","active":[{"quest":"quest.first_hunt","step":0,"objectives":[{"type":"kill","target":"monster.gel_slime","have":3,"need":5},""" +
            """{"type":"collect","target":"item.gel_glob","have":3,"need":3},{"type":"talk","target":"npc.guide","have":0,"need":1}]},""" +
            """{"quest":"quest.warden_trial","step":0,"objectives":[{"type":"level","have":6,"need":10}]}]""" +
            ""","completed":["quest.gear_up"],"offer":["npc.trainer"],"turn_in":["npc.guide"]}"""
        },
        { "quest_log, empty", new QuestLogMessage([], [], [], []), """{"type":"quest_log","active":[],"completed":[],"offer":[],"turn_in":[]}""" },
        {
            "quest progress, held for a full bag",
            new QuestMessage("progress", "quest.first_hunt", new QuestEntryRecord("quest.first_hunt", 0, [new QuestObjectiveRecord("talk", "npc.guide", 1, 1)], "inventory_full")),
            """{"type":"quest","change":"progress","quest":"quest.first_hunt","state":{"quest":"quest.first_hunt","step":0""" +
            ""","objectives":[{"type":"talk","target":"npc.guide","have":1,"need":1}],"held":"inventory_full"}}"""
        },
        { "quest completed", new QuestMessage("completed", "quest.first_hunt", null), """{"type":"quest","change":"completed","quest":"quest.first_hunt"}""" },
        {
            "rejected quest start", new RejectedMessage("dialogue_choice", "base_level_too_low", null, Quest: "quest.gear_up"),
            """{"type":"rejected","intent":"dialogue_choice","reason":"base_level_too_low","quest":"quest.gear_up"}"""
        },
        { "pong", new PongMessage(7), """{"type":"pong","seq":7}""" },
        { "bye", new ByeMessage("timeout"), """{"type":"bye","reason":"timeout"}""" },
        { "bye banned", new ByeMessage("banned", 3600), """{"type":"bye","reason":"banned","retry_after":3600}""" },
        { "announcement", new AnnouncementMessage("staff", "Noon.", null), """{"type":"announcement","kind":"staff","text":"Noon."}""" },
        { "announcement maintenance", new AnnouncementMessage("maintenance", null, 300), """{"type":"announcement","kind":"maintenance","in":300}""" },
        { "content_changed", new ContentChangedMessage("f5579e66c2db05f8"), """{"type":"content_changed","content":"f5579e66c2db05f8"}""" },   // T-801
        { "chat_line", new ChatLineMessage(812, "map", 42, "Ada", "hello"), """{"type":"chat_line","line":812,"channel":"map","from":42,"name":"Ada","text":"hello"}""" },
        { "chat_line whisper", new ChatLineMessage(813, "whisper", 42, "Ada", "psst", "Bo"), """{"type":"chat_line","line":813,"channel":"whisper","from":42,"name":"Ada","to":"Bo","text":"psst"}""" },
        { "chat_refused muted", new ChatRefusedMessage("chat", "muted", 240, "map"), """{"type":"chat_refused","intent":"chat","reason":"muted","retry_after":240,"channel":"map"}""" },
        { "chat_refused whisper", new ChatRefusedMessage("chat", "not_online", null, "whisper", "Bo"), """{"type":"chat_refused","intent":"chat","reason":"not_online","channel":"whisper","to":"Bo"}""" },
        { "chat_refused report", new ChatRefusedMessage("report", "unknown_line", Line: 9), """{"type":"chat_refused","intent":"report","reason":"unknown_line","line":9}""" },
        { "chat_state", new ChatStateMessage(120, ["Bo", "Cy"]), """{"type":"chat_state","max_length":120,"blocked":["Bo","Cy"]}""" },
        { "chat_reported", new ChatReportedMessage(812), """{"type":"chat_reported","line":812}""" },
        { "party", new PartyMessage(3, 42, [new PartyMemberRecord(42, "Ada"), new PartyMemberRecord(43, "Bo")]),
            """{"type":"party","party":3,"leader":42,"members":[{"id":42,"name":"Ada"},{"id":43,"name":"Bo"}]}""" },
        { "party none", new PartyMessage(null, 0, []), """{"type":"party","members":[]}""" },
        { "party_members", new PartyMembersMessage([new PartyMemberStatusRecord(43, 80, 100, "map.meadow", new GridCell(3, 4), false, false),
                new PartyMemberStatusRecord(44, 0, 0, "map.cave", new GridCell(1, 2), true, false), new PartyMemberStatusRecord(45, 0, 0, null, null, false, true)]),
            """{"type":"party_members","status":[{"id":43,"hp":80,"sp":100,"map":"map.meadow","cell":[3,4]},{"id":44,"hp":0,"sp":0,"map":"map.cave","cell":[1,2],"dead":true},{"id":45,"away":true}]}""" },
        { "party_invited", new PartyInvitedMessage(42, "Ada", 30), """{"type":"party_invited","from":42,"name":"Ada","expires_in":30}""" },
        { "party_notice", new PartyNoticeMessage("declined", "Bo"), """{"type":"party_notice","notice":"declined","name":"Bo"}""" },
        { "party_notice disbanded", new PartyNoticeMessage("disbanded"), """{"type":"party_notice","notice":"disbanded"}""" },
        { "party_refused", new PartyRefusedMessage("party_invite", "rate_limited", 10, "Bo"), """{"type":"party_refused","intent":"party_invite","reason":"rate_limited","retry_after":10,"name":"Bo"}""" },
        { "trade_asked", new TradeAskedMessage(42, "Ada", 20), """{"type":"trade_asked","from":42,"name":"Ada","expires_in":20}""" },
        { "trade", new TradeMessage(43, "Bo", 7,
                new TradeSideRecord([new TradeItemRecord(1207, "item.iron_blade", 1, ["item.gelcap_card"], 4), new TradeItemRecord(1300, "item.healing_draught", 3)], 0, true, false),
                new TradeSideRecord([], 250, true, true)),
            """{"type":"trade","partner":43,"name":"Bo","version":7,"mine":{"items":[{"entry":1207,"item":"item.iron_blade","quantity":1,"cards":["item.gelcap_card"],"refine":4},{"entry":1300,"item":"item.healing_draught","quantity":3}],"currency":0,"locked":true},"theirs":{"items":[],"currency":250,"locked":true,"confirmed":true}}""" },
        { "trade_notice", new TradeNoticeMessage("declined", "Bo"), """{"type":"trade_notice","notice":"declined","name":"Bo"}""" },
        { "trade_closed", new TradeClosedMessage("too_far", "Bo", true), """{"type":"trade_closed","reason":"too_far","name":"Bo","own":true}""" },
        { "trade_closed done", new TradeClosedMessage("done", "Bo"), """{"type":"trade_closed","reason":"done","name":"Bo"}""" },
        { "trade_refused", new TradeRefusedMessage("trade_ask", "rate_limited", 10), """{"type":"trade_refused","intent":"trade_ask","reason":"rate_limited","retry_after":10}""" },
        { "guild", new GuildMessage(3, "Iron Oaks", "icon.emblem.oak", "Meet at the well", 12, [new GuildRankRecord("Leader", ["invite"]), new GuildRankRecord("Member", [])],
                [new GuildMemberRecord(12, "Ada", 0, 7, true), new GuildMemberRecord(13, "Bo", 1, 3, false)]),
            """{"type":"guild","guild":{"id":3,"name":"Iron Oaks","emblem":"icon.emblem.oak","notice":"Meet at the well","own":12,"ranks":[{"name":"Leader","permissions":["invite"]},{"name":"Member","permissions":[]}],"members":[{"member":12,"name":"Ada","rank":0,"level":7,"online":true},{"member":13,"name":"Bo","rank":1,"level":3}]}}""" },
        { "guild none", GuildMessage.None, """{"type":"guild"}""" },
        { "guild_invited", new GuildInvitedMessage("Ada", "Iron Oaks", "icon.emblem.oak", 30), """{"type":"guild_invited","name":"Ada","title":"Iron Oaks","emblem":"icon.emblem.oak","expires_in":30}""" },
        { "guild_news", new GuildNewsMessage("joined", "Bo"), """{"type":"guild_news","news":"joined","name":"Bo"}""" },
        { "guild_news notice", new GuildNewsMessage("notice"), """{"type":"guild_news","news":"notice"}""" },
        { "guild_refused", new GuildRefusedMessage("guild_invite", "not_online", null, "Bo"), """{"type":"guild_refused","intent":"guild_invite","reason":"not_online","name":"Bo"}""" },
        { "guild_tags", new GuildTagsMessage([new GuildTagRecord(42, "icon.emblem.oak")]), """{"type":"guild_tags","tags":[{"entity":42,"emblem":"icon.emblem.oak"}]}""" },
        {
            "guild_storage", new GuildStorageMessage(50, 1200, [new InventoryEntryRecord(981, "item.healing_draught", 5)],
                [new GuildStorageLogRecord(1764320000, "Ada", "deposit", "item.healing_draught", 981, 5, 0), new GuildStorageLogRecord(1764320060, "", "withdraw", null, null, 0, 300)]),
            """{"type":"guild_storage","size":50,"currency":1200,"entries":[{"id":981,"item":"item.healing_draught","quantity":5}],"log":[""" +
            """{"at":1764320000,"name":"Ada","move":"deposit","item":"item.healing_draught","entry":981,"quantity":5},{"at":1764320060,"name":"","move":"withdraw","currency":300}]}"""
        },
    };

    [Theory]
    [MemberData(nameof(ClientGolden))]
    public void Client_messages_match_golden_json_and_round_trip(string what, ClientMessage message, string json)
    {
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(message)).ShouldBe(json, what);
        var read = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(json));
        read.Error.ShouldBeNull(what);
        read.Message!.GetType().ShouldBe(message.GetType(), what);
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(read.Message!)).ShouldBe(json, what); // records may hold lists, so compare re-encoded
    }

    [Theory]
    [MemberData(nameof(ServerGolden))]
    public void Server_messages_match_golden_json_and_round_trip(string what, ServerMessage message, string json)
    {
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(message)).ShouldBe(json, what);
        var read = ProtocolJson.ReadServer(Encoding.UTF8.GetBytes(json));
        read.Error.ShouldBeNull(what);
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(read.Message!)).ShouldBe(json, what); // records hold lists, so compare re-encoded
    }

    [Fact]
    public void Every_message_type_has_a_golden_case()
    {
        ClientGolden.Select(r => ((ClientMessage)r[1]).Type).Concat(ServerGolden.Select(r => ((ServerMessage)r[1]).Type)).Distinct()
            .ShouldBe(MessageTypes.All.Ordered, ignoreOrder: true);
    }

    [Theory]
    [InlineData("", ByeReasons.Malformed)]
    [InlineData("null", ByeReasons.Malformed)]
    [InlineData("{}", ByeReasons.Malformed)]
    [InlineData("""{"type":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[1]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[1,2,3]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[1.5,2]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[99999999999,2]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"ping","seq":1} trailing""", ByeReasons.Malformed)]
    [InlineData("""{"type":"ping","seq":1}{"type":"ping","seq":2}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"ping","seq":1,"x":[[[[[[[[[[1]]]]]]]]]]}""", ByeReasons.Malformed)] // nested too deep
    [InlineData("""{"type":"teleport","cell":[1,1]}""", ByeReasons.UnknownType)]
    [InlineData("""{"type":"bye","reason":"x"}""", ByeReasons.UnknownType)]
    [InlineData("""{"type":"hello","version":1}""", ByeReasons.Malformed)]                                  // neither name nor resume
    [InlineData("""{"type":"hello","version":1,"name":"Ada","resume":"Qk3x"}""", ByeReasons.Malformed)]   // both
    [InlineData("""{"type":"hello","version":1,"resume":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":"7"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":7.5}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":null}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":[7]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":99999999999999999999}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":7""", ByeReasons.Malformed)]
    [InlineData("""{"type":"you_died"}""", ByeReasons.UnknownType)]   // server-to-client types are not accepted from clients
    [InlineData("""{"type":"insert_card","entry":7}""", ByeReasons.Malformed)]            // no card
    [InlineData("""{"type":"insert_card","card":12}""", ByeReasons.Malformed)]            // no equipment
    [InlineData("""{"type":"insert_card","entry":7,"card":"12"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"refine"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"abandon_quest"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"abandon_quest","quest":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"shop_buy","item":"item.healing_draught"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"shop_buy","quantity":2}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"shop_buy","item":7,"quantity":2}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"shop_sell","entry":12}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"shop_sell","quantity":1}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"refine","entry":1.5}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","quests":"quest.first_hunt"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","quests":[7]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","battle":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","quests":["quest.a"],"skills":[["skill.a"]]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","quests":["quest.a"],"skills":[["skill.a",1.5]]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","quests":["quest.a"],"skills":[["skill.a",99999999999]]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"start_autopilot","quests":["quest.a"],"skills":["skill.a"]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"autopilot","mode":"off"}""", ByeReasons.UnknownType)]
    [InlineData("""{"type":"set_outfit"}""", ByeReasons.Malformed)]                                          // neither slot nor hidden
    [InlineData("""{"type":"chat","channel":"map"}""", ByeReasons.Malformed)]                                // no text (T-901)
    [InlineData("""{"type":"chat","text":"hi"}""", ByeReasons.Malformed)]                                   // no channel
    [InlineData("""{"type":"chat","channel":"map","text":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"block"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"report","line":"7"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"set_outfit","slot":"head","hidden":true}""", ByeReasons.Malformed)]             // both
    [InlineData("""{"type":"set_outfit","outfit":"outfit.x","hidden":false}""", ByeReasons.Malformed)]      // an outfit without a slot
    [InlineData("""{"type":"set_outfit","slot":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"set_outfit","slot":"head","outfit":["outfit.x"]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"set_outfit","hidden":"yes"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"wardrobe","owned":[],"chosen":{}}""", ByeReasons.UnknownType)]
    [InlineData("""{"type":"refine_result","entry":1,"item":"item.x","result":"success","refine":1}""", ByeReasons.UnknownType)]
    public void Bad_client_messages_name_their_reason(string json, string reason)
    {
        var read = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(json));
        read.Message.ShouldBeNull();
        read.Error.ShouldBe(reason);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_field_order_does_not_matter()
    {
        ProtocolJson.ReadClient("""{"name":"Ada","extra":{"a":[1,2]},"version":1,"type":"hello"}"""u8).Message
            .ShouldBe(new HelloMessage(1, "Ada"));
    }

    public static TheoryData<string> ClientTypes => [.. MessageTypes.ClientToServer];

    [Theory]
    [MemberData(nameof(ClientTypes))]
    public void The_autopilots_lists_are_unknown_fields_in_every_other_message(string type)
    {
        if (type == MessageTypes.StartAutopilot) return;
        foreach (var (_, message, json) in ClientGolden.Select(r => ((string)r[0], (ClientMessage)r[1], (string)r[2])).Where(g => g.Item2.Type == type))
            foreach (var extra in new[] { "\"quests\":7", "\"quests\":[7]", "\"skills\":\"x\"", "\"skills\":[[\"a\",1.5]]", "\"skills\":[[\"a\",99999999999]]" })
            {
                var read = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(json[..^1] + "," + extra + "}"));
                read.Error.ShouldBeNull(type + " with " + extra);
                Encoding.UTF8.GetString(ProtocolJson.ToBytes(read.Message!)).ShouldBe(json);
            }
    }

    [Fact]
    public void Start_autopilot_is_read_in_any_field_order()
    {
        var read = ProtocolJson.ReadClient("""{"skills":[["skill.a",1]],"extra":[],"quests":["quest.a"],"type":"start_autopilot"}"""u8).Message.ShouldBeOfType<StartAutopilotMessage>();
        read.Quests.ShouldBe(["quest.a"]);
        read.Skills.ShouldBe([new SkillLevelRecord("skill.a", 1)]);
    }

    [Fact]
    public void An_auto_battle_start_needs_no_quests_and_keeps_a_filter_the_server_does_not_know()
    {
        // T-811: the reader takes the shape only; the autopilot refuses an unknown filter (unknown_filter) and quests beside a battle (quests_given).
        var read = ProtocolJson.ReadClient("""{"battle":"attackers","type":"start_autopilot"}"""u8).Message.ShouldBeOfType<StartAutopilotMessage>();
        read.Quests.ShouldBeEmpty();
        read.Skills.ShouldBeEmpty();
        read.Battle.ShouldBe("attackers");
        ProtocolJson.ReadClient("""{"type":"start_autopilot","quests":["quest.a"],"battle":"anything"}"""u8).Message.ShouldBeOfType<StartAutopilotMessage>().Battle.ShouldBe("anything");
        ProtocolJson.ReadClient("""{"type":"start_autopilot","quests":["quest.a"]}"""u8).Message.ShouldBeOfType<StartAutopilotMessage>().Battle.ShouldBeNull();
    }

    [Fact]
    public void Wire_field_bits_match_the_aoi_view_fields()
    {
        foreach (var field in Enum.GetValues<ViewFields>())
            ViewMessageWriter.FieldsOf(field).ToString().ShouldBe(field.ToString());
        Enum.GetValues<EntityKind>().Select(ViewMessageWriter.KindTerm).ShouldBe(EntityKinds.All.Ordered);
    }

    [Fact]
    public void An_item_is_mine_without_an_owner_or_as_its_owner_and_nothing_else_has_the_flag()
    {
        var me = new EntityId(42);
        var item = new EntityView(new EntityId(9), EntityKind.Item, "item.x", new GridCell(1, 1), Directions.S, null, 0, Quantity: 1);
        ViewMessageWriter.Mine(item, me).ShouldBe(true);
        ViewMessageWriter.Mine(item with { LootOwner = me }, me).ShouldBe(true);
        ViewMessageWriter.Mine(item with { LootOwner = new EntityId(43) }, me).ShouldBe(false);
        ViewMessageWriter.Mine(item with { Kind = EntityKind.Monster }, me).ShouldBeNull();
    }

    /// <summary>The tick-thread fast path writes the same bytes as <see cref="ProtocolJson"/> for the same view, over a real walk.</summary>
    [Fact]
    public void View_writer_matches_the_record_writer_for_real_snapshots_and_deltas()
    {
        var rig = new AoiRig();
        var sink = new DualSink();
        var observer = rig.Add(new GridCell(2, 2), kind: EntityKind.Player, sink: sink);
        var walker = rig.Add(new GridCell(4, 2), key: "monster.gel_slime");
        rig.Ticks();
        var leaver = rig.Add(new GridCell(3, 5), kind: EntityKind.Npc, key: "npc.guide");
        rig.Ticks();
        rig.Send(new MoveTo(walker, new GridCell(6, 3))); // stays in view (buckets of 4, radius 1)
        rig.Ticks(6);
        rig.World.Despawn(leaver);
        rig.Ticks(8);

        // Combat on the wire: HP bars, own vitals, hits, a corpse, a ground item whose loot priority runs out.
        var me = rig.Get(observer);
        me.Vitals = new Game.Server.World.Combat.Vitals(me.Stats!);
        var slime = rig.Get(walker);
        var vitals = slime.Vitals = new Game.Server.World.Combat.Vitals(slime.Stats!);
        rig.Ticks();
        vitals.Hp = vitals.MaxHp / 3;
        rig.A.Events.Publish(new DamageDealt(observer, walker, 7, HitResults.Crit, vitals.Hp));
        rig.Ticks();
        vitals.Hp = 0;
        slime.Dead = true;
        rig.A.Events.Publish(new DamageDealt(observer, walker, 3, HitResults.Hit, 0));
        rig.World.Spawn(EntityKind.Item, "map.a", new GridCell(3, 3), Directions.S, e =>
        {
            e.DefinitionId = "item.gel_glob";
            e.GroundItem = new Game.Server.World.Combat.GroundItem("item.gel_glob", 2, walker, new EntityId(77), 0, rig.A.TickNumber * 100 + 300, 60_000);
        });
        rig.Ticks(6);

        sink.Pairs.Count.ShouldBeGreaterThan(9);
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"changed\"") && p.Fast.Contains("\"next\":null"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"disappeared\""));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"hits\":[[1,2,7,\"crit\"]]") && p.Fast.Contains("\"vitals\"") == false);
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"dead\":true"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"kind\":\"item\"") && p.Fast.Contains("\"mine\":false"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"mine\":true"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"vitals\":{"));
        foreach (var (fast, records) in sink.Pairs) fast.ShouldBe(records);
    }

    private sealed class DualSink : IViewSink
    {
        private readonly ViewMessageWriter _writer = new();
        public readonly List<(string Fast, string Records)> Pairs = new();

        public void OnSnapshot(ViewSnapshot snapshot)
        {
            var message = new SnapshotMessage(snapshot.Tick, snapshot.MapId, snapshot.Observer.Value, snapshot.Entities.Select(v => Record(v, snapshot.Observer)).ToList(),
                Vitals(snapshot.Vitals));
            Pairs.Add((Encoding.UTF8.GetString(_writer.Write(snapshot)), Encoding.UTF8.GetString(ProtocolJson.ToBytes(message))));
        }

        public void OnDelta(ViewDelta delta)
        {
            var message = new DeltaMessage(delta.Tick, delta.Appeared.Select(v => Record(v, delta.Observer)).ToList(),
                delta.Changed.Select(c => new ChangeRecord(c.View.Id.Value, ViewMessageWriter.FieldsOf(c.Fields), c.View.Cell, c.View.Facing, c.View.Next,
                    c.View.Speed, c.View.DisplayKey, ViewMessageWriter.HpFraction(c.View) ?? 0, c.View.Dead, ViewMessageWriter.Mine(c.View, delta.Observer) ?? false)).ToList(),
                delta.Disappeared.Select(id => id.Value).ToList(),
                delta.Hits.Select(h => new HitRecord(h.Attacker.Value, h.Target.Value, h.Amount, h.Result)).ToList(), Vitals(delta.Vitals));
            Pairs.Add((Encoding.UTF8.GetString(_writer.Write(delta)), Encoding.UTF8.GetString(ProtocolJson.ToBytes(message))));
        }

        private static EntityRecord Record(EntityView v, EntityId observer) =>
            new(v.Id.Value, ViewMessageWriter.KindTerm(v.Kind), v.DisplayKey, v.Cell, v.Facing, v.Next, v.Speed, ViewMessageWriter.HpFraction(v), v.Dead,
                v.Quantity, ViewMessageWriter.Mine(v, observer));

        private static VitalsRecord? Vitals(OwnVitals? v) => v is { } own ? new VitalsRecord(own.Hp, own.MaxHp, own.Sp, own.MaxSp) : null;
    }
}
