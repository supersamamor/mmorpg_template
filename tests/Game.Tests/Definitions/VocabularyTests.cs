using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Tests.Definitions;

public class VocabularyTests
{
    // Expected values are copied from docs/VOCABULARY.md, in document order.
    public static TheoryData<string, VocabularySet, string[]> Categories => new()
    {
        { "Id namespaces", IdPrefixes.All, ["job.", "skill.", "item.", "monster.", "status.", "quest.", "npc.", "dialogue.", "shop.", "outfit.", "map.", "drop.", "exp."] },
        { "Asset key prefixes", AssetKeyPrefixes.All, ["model.", "anim.", "scene.", "tex.", "mat.", "vfx.", "sprite.", "icon.", "ui.", "portrait.", "sfx.", "music.", "font.", "ambience."] },
        { "Audio buses", AudioBuses.All, ["music", "sfx", "ui", "ambient"] },
        { "Audio default categories", AudioCategories.Fixed, ["monster.aggro", "monster.hit", "monster.die", "item.use", "item.equip", "ui.tap", "ui.open", "ui.close", "ui.error"] },
        { "Directions", Directions.All, ["n", "ne", "e", "se", "s", "sw", "w", "nw"] },
        { "Cell flags", CellFlags.All, ["walkable", "blocks_los"] },
        { "Map object types", MapObjectTypes.All, ["spawn", "warp", "npc", "zone"] },
        { "Zone kinds", ZoneKinds.All, ["safe", "pvp"] },
        { "Appearance option namespaces", AppearanceIdPrefixes.All, ["face.", "hair.", "skin.", "haircolor."] },
        { "Genders", Genders.All, ["male", "female"] },
        { "Appearance colour roles", AppearanceColorRoles.All, ["skin", "hair", "cloth"] },
        { "Attach points", AttachPoints.All, ["head", "face", "mouth", "back", "hand_r", "hand_l"] },
        { "Animation ids", AnimationIds.All, ["idle", "walk", "attack", "cast", "hit", "die", "sit", "pick_up"] },
        { "Primary stats", PrimaryStats.All, ["str", "agi", "vit", "int", "dex", "luk"] },
        { "Derived stats", DerivedStats.All, ["max_hp", "max_sp", "atk", "matk", "def", "mdef", "hit", "flee", "crit", "aspd", "move_speed", "cast_time_mod", "hp_regen", "sp_regen", "weight_limit"] },
        { "Modifier ops", ModifierOps.All, ["flat", "percent", "override"] },
        { "Elements", Elements.All, ["neutral", "fire", "water", "earth", "wind", "holy", "shadow", "poison", "ghost", "undead"] },
        { "Races", MonsterRaces.All, ["formless", "beast", "plant", "insect", "fish", "demon", "undead", "humanoid", "dragon", "angel"] },
        { "Sizes", MonsterSizes.All, ["small", "medium", "large"] },
        { "Effect types", EffectTypes.All, ["damage", "heal", "restore_sp", "apply_status", "remove_status", "modify_stat", "knockback", "teleport", "summon", "area", "reset_stats", "reset_skills"] },
        { "Outfit slots", OutfitSlots.All, ["head", "face", "body", "back", "feet"] },
        { "Outfit reasons", OutfitReasons.All, ["unknown_slot", "unknown_outfit", "not_owned", "slot_not_covered", "wrong_gender"] },
        { "Equipment slots", EquipSlots.All, ["head_top", "head_mid", "head_low", "body", "weapon", "shield", "garment", "footgear", "accessory_1", "accessory_2"] },
        { "Weapon types", WeaponTypes.All, ["unarmed", "dagger", "sword_1h", "sword_2h", "spear", "axe", "mace", "staff", "bow", "knuckle"] },
        { "Item kinds", ItemKinds.All, ["consumable", "equip", "card", "material", "quest", "ammo"] },
        { "Quest objective types", QuestObjectiveTypes.All, ["kill", "collect", "talk", "reach", "level", "job", "use_item", "equip"] },
        { "Reward types", RewardTypes.All, ["exp", "item", "currency", "skill_point", "stat_point", "unlock_job", "status", "outfit"] },
        { "Dialogue condition types", DialogueConditionTypes.All, ["base_level", "job_level", "job_is", "job_is_not", "has_item", "has_status", "quest_state", "quest_step", "out_of_combat"] },
        { "Quest states", QuestStates.All, ["not_started", "available", "active", "completed"] },
        { "Quest reasons", QuestReasons.All, ["unknown_quest", "already_active", "already_completed", "base_level_too_low", "wrong_job", "quest_not_completed", "too_many_active", "not_active", "inventory_full", "too_heavy", "missing_item"] },
        { "Quest changes", QuestChanges.All, ["accepted", "progress", "step_done", "completed", "abandoned"] },
        { "Quest markers", QuestMarkers.All, ["offer", "turn_in"] },
        { "Autopilot modes", AutopilotModes.All, ["running", "paused", "off"] },
        { "Autopilot activities", AutopilotActivities.All, ["travelling", "hunting", "gathering", "talking", "waiting", "fighting", "returning"] },
        { "Autopilot reasons", AutopilotReasons.All, ["disabled", "dead", "time_limit", "empty_queue", "too_many_quests", "unknown_quest", "duplicate_quest", "not_active", "too_many_skills", "unknown_skill", "not_learned", "passive_skill", "level_too_high"] },
        { "Autopilot pauses", AutopilotPauses.All, ["needs_level", "needs_job", "needs_equip", "needs_item_use", "no_source", "unreachable", "no_progress", "no_safe_choice"] },
        { "Autopilot stops", AutopilotStops.All, ["finished", "stopped", "manual_input", "died", "logout", "disconnected", "bag_full", "time_limit", "quest_abandoned", "low_hp", "left_area", "nothing_to_fight"] },
        { "Auto-battle filters", AutoBattleFilters.All, ["nearby", "attackers", "with_bosses"] },
        { "Auto-battle reasons", AutoBattleReasons.All, ["battle_disabled", "unknown_filter", "quests_given"] },
        { "Dialogue action types", DialogueActionTypes.All, ["start_quest", "set_save_point", "run_effects", "take_item", "give_item", "open_service"] },
        { "Service ids", ServiceIds.All, ["refine", "shop"] },
        { "Dialogue reasons", DialogueReasons.All, ["target_gone", "invalid_target", "dead", "no_dialogue", "unreachable", "cancelled", "invalid_choice", "busy", "missing_item", "inventory_full", "too_heavy"] },
        { "Dialogue end reasons", DialogueEndReasons.All, ["finished", "closed", "moved_away", "warped", "died", "replaced", "npc_gone", "disconnected"] },
        { "Monster AI states", AiStates.All, ["idle", "wander", "chase", "attack", "return"] },
        { "Aggro reasons", AggroReasons.All, ["sighted", "attacked", "target_gone", "leash", "unreachable"] },
        { "Events", EventNames.All, ["EntitySpawned", "EntityDied", "MonsterKilled", "DamageDealt", "ItemAcquired", "ItemUsed", "ItemEquipped", "NpcTalked", "ZoneEntered", "LevelUp", "JobLevelUp", "JobChanged", "SkillLearned", "QuestStarted", "QuestStepCompleted", "QuestCompleted", "StatusApplied", "StatusExpired", "EntityEnteredMap", "EntityLeftMap", "WarpRejected", "MovementStarted", "CellReached", "MovementStopped", "MoveRejected", "AggroGained", "AggroLost", "MonsterReturned", "AttackRequested", "AttackRefused", "AttackStarted", "AttackStopped", "ExpGained", "ExpLost", "PlayerRespawned", "ItemDropped", "ItemExpired", "ItemUnequipped", "ItemDiscarded", "ItemRefused", "InventoryChanged", "CardInserted", "ItemRefined", "StatusRemoved", "HpRestored", "SpRestored", "KnockedBack", "Teleported", "SummonRequested", "MonsterSummoned", "AreaApplied", "SkillCastStarted", "SkillRequested", "SkillUsed", "SkillRefused", "SkillCastEnded", "SkillCooldownStarted", "SkillLearnRefused", "JobChangeRefused", "StatRaised", "StatRaiseRefused", "StatsReset", "SkillsReset", "DialogueShown", "DialogueEnded", "DialogueRefused", "ServiceOpened", "ShopOpened", "QuestStartRequested", "SavePointSet", "DialogueEffectsRequested", "DialogueItemsRequested", "DialogueItemsResolved", "QuestProgressed", "QuestAbandoned", "QuestRefused", "QuestItemsRequested", "QuestItemsResolved", "QuestRewardsRequested", "QuestRewarded", "QuestMarkersChanged", "AutopilotChanged", "AutopilotRefused", "OutfitGranted", "WardrobeChanged", "OutfitRefused", "AdminActionApplied", "PartyKillCredit", "TradeAsked", "TradeNoticed", "TradeChanged", "TradeRefused", "TradeClosed", "TradeSwapRequested", "TradeSwapResolved", "ItemsTraded"] },
        { "Attack reasons", AttackReasons.All, ["attacker_dead", "target_dead", "target_gone", "out_of_range", "no_line_of_sight", "safe_zone", "invalid_target", "unreachable", "cancelled", "overweight", "cannot_attack"] },
        { "Skill reasons", SkillReasons.All, ["caster_dead", "target_dead", "target_gone", "out_of_range", "no_line_of_sight", "safe_zone", "invalid_target", "cannot_use_skills", "not_learned", "level_too_high", "passive_skill", "not_enough_sp", "on_cooldown", "wrong_weapon", "unknown_skill", "not_in_tree", "at_max_level", "prerequisite_not_met", "not_enough_points", "unknown_job", "same_job", "wrong_parent_job", "base_level_too_low", "job_level_too_low", "quest_not_completed", "not_unlocked"] },
        { "Stat reasons", StatReasons.All, ["unknown_stat", "invalid_amount", "at_max_value", "not_enough_points", "dead"] },
        { "Status end reasons", StatusEndReasons.All, ["removed", "died", "warped"] },
        { "Item reasons", ItemReasons.All, ["item_gone", "not_yours", "unreachable", "cancelled", "too_heavy", "inventory_full", "not_usable", "cooldown", "not_equipment", "wrong_slot", "level_too_low", "wrong_job", "not_equipped", "item_equipped", "invalid_quantity", "not_a_card", "card_does_not_fit", "no_free_socket", "not_refinable", "at_max_refine", "missing_material", "reset_in_combat", "reset_dead", "nothing_to_reset", "service_required", "out_of_range", "not_in_stock", "quantity_limit", "cannot_afford", "not_sellable", "currency_full", "dead"] },
        { "Weight states", WeightStates.All, ["normal", "burdened", "overweight"] },
        { "Inventory changes", InventoryChanges.All, ["picked_up", "used", "equipped", "unequipped", "dropped", "weight", "card_inserted", "refined", "exchanged", "quest", "shop_buy", "shop_sell", "staff", "traded", "guild_founded", "guild_storage"] },
        { "Refine failures", RefineFailures.All, ["keep", "down", "break"] },
        { "Refine results", RefineResults.All, ["success", "keep", "down", "break"] },
        { "Hit results", HitResults.All, ["hit", "crit", "miss", "status"] },
        { "Currency", Currencies.All, ["currency"] },
        { "Account errors", AccountErrors.All, ["invalid_request", "invalid_username", "invalid_password", "same_password", "username_taken", "invalid_credentials", "account_deleting", "invalid_session", "rate_limited", "invalid_name", "invalid_appearance", "name_taken", "no_free_slot", "unknown_character", "character_in_use", "unavailable", "account_banned", "guild_storage_not_empty"] },
        { "Staff roles", StaffRoles.All, ["support", "gm", "admin"] },
        { "Admin actions", AdminActions.All, ["kick", "ban", "unban", "mute", "unmute", "give_item", "remove_item", "change_currency", "unstick", "announce", "start_maintenance", "end_maintenance", "open_request", "complete_request", "cancel_request", "close_report"] },
        { "Admin target kinds", AdminTargetKinds.All, ["account", "character", "request", "server", "chat_report"] },
        { "Admin errors", AdminErrors.All, ["invalid_request", "unauthorized", "invalid_staff", "unknown_role", "forbidden", "reason_required", "not_found", "unknown_target", "rate_limited", "timeout", "unavailable", "already_banned", "not_banned", "already_muted", "not_muted", "character_busy", "never_saved", "unknown_item", "item_gone", "not_enough", "inventory_full", "currency_limit", "maintenance_on", "maintenance_off", "request_closed", "account_deleting", "report_closed"] },
        { "Admin outcomes", AdminOutcomes.All, ["done", "accepted", "refused"] },
        { "Sanction kinds", SanctionKinds.All, ["ban", "mute"] },
        { "Support request kinds", SupportRequestKinds.All, ["deletion", "export"] },
        { "Support request statuses", SupportRequestStatuses.All, ["open", "done", "cancelled"] },
        { "Announcement kinds", Game.Shared.Protocol.AnnouncementKinds.All, ["staff", "maintenance"] },
        { "Chat channels", ChatChannels.All, ["map", "whisper", "party", "guild", "system"] },
        { "Chat reasons", ChatReasons.All, ["muted", "rate_limited", "not_online", "invalid_target", "empty", "too_long", "invalid_channel", "unavailable", "unknown_character", "already_blocked", "not_blocked", "block_limit", "unknown_line", "already_reported"] },
        { "Chat report statuses", ChatReportStatuses.All, ["open", "closed"] },
        { "Party reasons", PartyReasons.All, ["not_online", "invalid_target", "full", "already_in_party", "invite_pending", "not_leader", "not_in_party", "no_invite", "rate_limited"] },
        { "Party notices", PartyNotices.All, ["invite_sent", "declined", "expired", "joined", "left", "kicked", "leader", "disbanded"] },
        { "Trade reasons", TradeReasons.All, ["not_online", "invalid_target", "busy", "too_far", "dead", "ask_pending", "no_ask", "rate_limited", "not_trading", "not_tradeable", "item_gone", "item_equipped", "invalid_quantity", "offer_full", "not_locked", "stale", "swapping", "cannot_trade"] },
        { "Trade notices", TradeNotices.All, ["ask_sent", "declined", "expired"] },
        { "Trade ends", TradeEnds.All, ["done", "cancelled", "too_far", "dead", "left_map", "disconnected", "left", "too_heavy", "inventory_full", "currency_full", "offer_changed"] },
        { "Guild permissions", GuildPermissions.All, ["invite", "kick", "edit_notice", "set_rank", "storage_deposit", "storage_items", "storage_currency", "storage_log"] },
        { "Guild storage moves", GuildStorageMoves.All, ["deposit", "withdraw"] },
        { "Guild reasons", GuildReasons.All, ["not_online", "invalid_target", "invalid_name", "name_taken", "invalid_emblem", "already_in_guild", "not_in_guild", "invite_pending", "no_invite", "full", "not_allowed", "rank_too_high", "invalid_ranks", "too_long", "cannot_afford", "busy", "rate_limited", "unavailable", "item_gone", "item_equipped", "not_tradeable", "storage_full", "inventory_full", "too_heavy", "currency_full", "storage_not_empty"] },
        { "Guild news", GuildNews.All, ["invite_sent", "declined", "expired", "founded", "joined", "left", "kicked", "rank", "leader", "ranks", "notice", "disbanded"] },
        { "Party EXP share variables", FormulaVariables.PartyShare, ["exp", "share_count", "level_spread"] },
        { "Formula functions", FormulaFunctions.All, ["min", "max", "floor", "ceil", "round", "clamp", "pow", "sqrt"] },
    };

    [Theory]
    [MemberData(nameof(Categories))]
    public void Category_matches_vocabulary_doc_in_order(string category, VocabularySet set, string[] expected)
    {
        set.Ordered.ShouldBe(expected, category);
        set.Count.ShouldBe(expected.Length);
        foreach (var value in expected) set.Contains(value).ShouldBeTrue(value);
    }

    [Fact]
    public void Sets_are_ordinal_and_reject_unknown_values()
    {
        Elements.All.Contains("Fire").ShouldBeFalse();
        Elements.All.Contains("lightning").ShouldBeFalse();
        Elements.All.Contains(null!).ShouldBeFalse();
    }

    [Fact]
    public void Sets_are_IReadOnlySet_on_modern_targets()
    {
        IReadOnlySet<string> set = Directions.All;
        set.SetEquals(["n", "ne", "e", "se", "s", "sw", "w", "nw"]).ShouldBeTrue();
    }

    [Fact]
    public void Duplicate_terms_are_rejected()
    {
        Should.Throw<ArgumentException>(() => new VocabularySet("a", "b", "a"));
        Should.Throw<ArgumentException>(() => VocabularySet.Union(PrimaryStats.All, PrimaryStats.All));
    }

    [Fact]
    public void Stats_is_primary_then_derived()
    {
        Stats.All.Ordered.ShouldBe(PrimaryStats.All.Concat(DerivedStats.All));
    }

    [Fact]
    public void Formula_variables_cover_caster_target_and_skill()
    {
        foreach (var name in new[] { "base_level", "job_level", "weapon_atk", "refine", "str", "max_hp", "move_speed", "level", "t_def", "t_vit", "t_level" })
            FormulaVariables.All.Contains(name).ShouldBeTrue(name);

        FormulaVariables.All.Contains("foo").ShouldBeFalse();
        FormulaVariables.All.Contains("t_foo").ShouldBeFalse();
        foreach (var stat in Stats.All) FormulaVariables.Target.Contains("t_" + stat).ShouldBeTrue(stat);
    }

    [Fact]
    public void Enums_mirror_their_vocabulary_lists_in_snake_case()
    {
        Enum.GetNames<ItemKind>().Select(ToSnakeCase).ShouldBe(ItemKinds.All.Ordered);
        Enum.GetNames<ModifierOp>().Select(ToSnakeCase).ShouldBe(ModifierOps.All.Ordered);
    }

    private static string ToSnakeCase(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
