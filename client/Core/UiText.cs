using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core;

/// <summary>
/// The client's own words: the only place UI text is written (T-211, ADR-002 row 120: see ARCHITECTURE §7.4), so a later translation has one table to replace. Names of game things never
/// come from here; they come from the bundle by <c>display_key</c>.
/// </summary>
public static class UiText
{
    /// <summary>The login button.</summary>
    public const string Play = "Play";

    /// <summary>The logout button.</summary>
    public const string Logout = "Log out";

    /// <summary>The death screen's title.</summary>
    public const string YouDied = "You died";

    /// <summary>The respawn button.</summary>
    public const string Respawn = "Respawn";

    /// <summary>The button that leaves a stop for the login screen.</summary>
    public const string BackToLogin = "Back to login";

    /// <summary>The update button.</summary>
    public const string GoToStore = "Get the update";

    /// <summary>The screen when the packed bundle has another format.</summary>
    public static string BundleRefused(int version) =>
        "This build cannot read its game data (format " + version.ToString(CultureInfo.InvariantCulture) + "). Please update the app.";

    /// <summary>The screen when the packed bundle cannot be read at all.</summary>
    public const string BundleCorrupt = "The game data in this build is damaged. Please reinstall the app.";

    /// <summary>The screen when the server names a map the bundle does not have.</summary>
    public const string MapMissing = "This build does not know the map you are on. Please update the app.";

    /// <summary>The screen when a release build is pointed at a plain <c>ws://</c> server (ARCHITECTURE §10).</summary>
    public const string CleartextRefused = "This build only connects over a secure connection (wss://). The server address in this build is not secure. Please update the app.";

    /// <summary>The screen when the server address is not a <c>ws://</c> or <c>wss://</c> URL.</summary>
    public const string ServerAddressInvalid = "The server address in this build is not valid. Please update the app.";

    /// <summary>The refusal screen's text for a verdict that is not <see cref="TransportVerdict.Allowed"/>.</summary>
    public static string TransportRefused(TransportVerdict verdict) => verdict == TransportVerdict.CleartextRefused ? CleartextRefused : ServerAddressInvalid;

    /// <summary>The line for a connection notice.</summary>
    public static string Notice(ConnectionNotice notice)
    {
        switch (notice)
        {
            case ConnectionNotice.Reconnecting: return "Connection lost. Reconnecting...";
            case ConnectionNotice.SessionExpired: return "You were away too long. Please log in again.";
            case ConnectionNotice.ResumeFailed: return "Your session has ended. Please log in again.";
            case ConnectionNotice.ResumeRateLimited: return "Too many reconnect attempts. Please log in again.";
            case ConnectionNotice.LoginRefused: return "The server refused this login.";
            case ConnectionNotice.ServerFull: return "The server is full. Please try again soon.";
            case ConnectionNotice.ConnectionFailed: return "Could not reach the server.";
            case ConnectionNotice.UpdateRequired: return "A new version is required to play. Please update the app.";
            case ConnectionNotice.Replaced: return "You are playing on another connection.";
            case ConnectionNotice.LoggedOut: return "You have logged out.";
            case ConnectionNotice.ServerShutdown: return "The server is restarting. Trying again soon...";
            default: return string.Empty;
        }
    }

    /// <summary>The line while a connection is being made, with no notice.</summary>
    public const string Connecting = "Connecting...";

    /// <summary>The word a floating number shows for a missed attack.</summary>
    public const string MissWord = "Miss";

    /// <summary>The flash when this client hits something.</summary>
    public static string YouHit(string target, int amount, string result)
    {
        if (result == HitResults.Miss) return "You miss " + target;
        var verb = result == HitResults.Crit ? "You crit " : "You hit ";
        return verb + target + " for " + amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The flash when something hits this client.</summary>
    public static string HitYou(string attacker, int amount, string result)
    {
        if (result == HitResults.Miss) return attacker + " misses you";
        var verb = result == HitResults.Crit ? " crits you for " : " hits you for ";
        return attacker + verb + amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The flash when a drop this client may take appears.</summary>
    public static string Dropped(string item, int quantity) =>
        quantity > 1 ? "Dropped: " + item + " x" + quantity.ToString(CultureInfo.InvariantCulture) : "Dropped: " + item;

    /// <summary>
    /// The flash for a move or attack a status refused (T-501: a "Movement reasons" <c>cannot_move</c> or "Attack reasons" <c>cannot_attack</c> term);
    /// empty for every other reason, which needs no word.
    /// </summary>
    public static string Held(string reason) => reason switch
    {
        "cannot_move" => "You cannot move right now.",
        AttackReasons.CannotAttack => "You cannot attack right now.",
        _ => string.Empty,
    };

    /// <summary>
    /// Under a status icon in the HUD (T-501): its stacks when above 1 and the whole seconds left, e.g. <c>"x2 7s"</c>; a status with no time
    /// shown yet reads just its stacks.
    /// </summary>
    public static string StatusLeft(int stacks, int seconds) =>
        (stacks > 1 ? "x" + stacks.ToString(CultureInfo.InvariantCulture) + " " : string.Empty) + seconds.ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>The letter drawn for a status whose icon the theme does not have (§7.7 fallback): the first letter of its name.</summary>
    public static string StatusLetter(string name) => name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";

    /// <summary>The flash when something went into the bag.</summary>
    public static string PickedUp(string item) => "Picked up: " + item;

    /// <summary>The flash for a refused item intent (an "Item reasons" term); empty for one that needs no word.</summary>
    public static string ItemRefused(string reason) => reason switch
    {
        ItemReasons.ItemGone => "That item is gone.",
        ItemReasons.NotYours => "Someone else may take that first.",
        ItemReasons.Unreachable => "You cannot get there.",
        ItemReasons.TooHeavy => "Too heavy to carry.",
        ItemReasons.InventoryFull => "Your bag is full.",
        ItemReasons.NotUsable => "That cannot be used.",
        ItemReasons.Cooldown => "Not yet.",
        ItemReasons.NotEquipment => "That cannot be worn.",
        ItemReasons.WrongSlot => "That does not go there.",
        ItemReasons.LevelTooLow => "Your level is too low for that.",
        ItemReasons.WrongJob => "Your job cannot use that.",
        ItemReasons.NotEquipped => "Nothing is worn there.",
        ItemReasons.ItemEquipped => "Take it off first.",
        ItemReasons.InvalidQuantity => "You do not have that many.",
        ItemReasons.NotACard => "That is not a card.",
        ItemReasons.CardDoesNotFit => "That card does not fit there.",
        ItemReasons.NoFreeSocket => "No free card slot.",
        ItemReasons.NotRefinable => "That cannot be refined.",
        ItemReasons.AtMaxRefine => "It cannot be refined any further.",
        ItemReasons.MissingMaterial => "You do not have enough material.",
        ItemReasons.ResetInCombat => "Not while you are fighting.",
        ItemReasons.ResetDead => "You cannot do that now.",
        ItemReasons.NothingToReset => "There is nothing to take back.",
        _ => string.Empty,   // cancelled: the player moved on, nothing to say
    };

    /// <summary>The flash for a card socketed into <paramref name="item"/>.</summary>
    public static string CardInserted(string item) => "Card set in " + item + ".";

    /// <summary>The flash for a refine's outcome (a "Refine results" term), with the name the item has now (+level).</summary>
    public static string RefineResult(string item, string result, int level) => result switch
    {
        RefineResults.Success => "Refined: " + Refined(item, level) + "!",
        RefineResults.Keep => "The refine failed. " + Refined(item, level) + " is unchanged.",
        RefineResults.Down => "The refine failed. " + Refined(item, level) + " lost a level.",
        RefineResults.Break => "The refine failed and " + Refined(item, level) + " broke.",
        _ => string.Empty,
    };

    /// <summary>An item's name with its refine level, e.g. <c>+3 Bone Dirk</c> (just the name at 0).</summary>
    public static string Refined(string item, int level) => level > 0 ? "+" + level.ToString(CultureInfo.InvariantCulture) + " " + item : item;

    /// <summary>An equipment entry's line: name with refine level, then its cards (filled and free sockets), e.g. <c>+2 Padded Vest [Gelcap Card]</c>.</summary>
    public static string EquipmentLine(string item, int level, IReadOnlyList<string> cards, int cardSlots)
    {
        var line = Refined(item, level);
        if (cardSlots <= 0 && cards.Count == 0) return line;
        var sockets = new List<string>(cards);
        for (var i = cards.Count; i < cardSlots; i++) sockets.Add(EmptySocket);
        return line + " [" + string.Join(", ", sockets) + "]";
    }

    /// <summary>An empty card socket.</summary>
    public const string EmptySocket = "free slot";

    /// <summary>Socket a card: the action button that lists the equipment it fits.</summary>
    public const string InsertCard = "Set card";

    /// <summary>Refine: the action button that shows the next step.</summary>
    public const string Refine = "Refine";

    /// <summary>The refine confirmation button.</summary>
    public const string ConfirmRefine = "Refine now";

    /// <summary>The skills button (T-502).</summary>
    public const string Skills = "Skills";

    /// <summary>The button in the skill panel that opens the stats panel (T-503).</summary>
    public const string Stats = "Stats";

    /// <summary>The stats panel's title.</summary>
    public const string StatsTitle = "Stats";

    /// <summary>The button in the stats panel that goes back to the skill panel.</summary>
    public const string BackToSkills = "Skills";

    /// <summary>The label over the derived stats.</summary>
    public const string DerivedTitle = "Derived";

    /// <summary>Stat points left to spend, e.g. <c>Stat points: 4</c>.</summary>
    public static string StatPoints(int points) => "Stat points: " + points.ToString(CultureInfo.InvariantCulture);

    /// <summary>A primary stat's row, e.g. <c>STR 12</c>.</summary>
    public static string StatLine(string stat, int value) => StatName(stat) + " " + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A stat's + button with the server's cost, e.g. <c>+ (2)</c>; just <c>max</c> when it cannot be raised.</summary>
    public static string RaiseButton(int cost) => cost > 0 ? "+ (" + cost.ToString(CultureInfo.InvariantCulture) + ")" : "max";

    /// <summary>One derived stat, e.g. <c>ATK 24</c>.</summary>
    public static string DerivedLine(string stat, int value) => StatName(stat) + " " + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The short name of a primary or derived stat id.</summary>
    public static string StatName(string stat) => stat switch
    {
        "str" => "STR", "agi" => "AGI", "vit" => "VIT", "int" => "INT", "dex" => "DEX", "luk" => "LUK",
        "atk" => "ATK", "matk" => "MATK", "def" => "DEF", "mdef" => "MDEF", "hit" => "HIT", "flee" => "FLEE", "crit" => "CRIT",
        "aspd" => "ASPD", "max_hp" => "Max HP", "max_sp" => "Max SP", _ => stat.ToUpperInvariant(),
    };

    /// <summary>The flash for a refused <c>raise_stat</c> (a "Stat reasons" term).</summary>
    public static string StatRefused(string reason) => reason switch
    {
        StatReasons.NotEnoughPoints => "Not enough stat points.",
        StatReasons.AtMaxValue => "That stat is at its highest.",
        StatReasons.Dead => "You cannot do that now.",
        _ => "That is not possible.",
    };

    /// <summary>The confirm button for using a reset item (T-503).</summary>
    public const string ConfirmUse = "Use it";

    /// <summary>What the bag says before a reset item is used, e.g. <c>Use Realign Tonic? This cannot be undone.</c></summary>
    public static string UseOffer(string item, string? description) =>
        "Use " + item + "?" + (description is { Length: > 0 } ? " " + description : string.Empty) + " Tap Use it to confirm.";

    /// <summary>The skill panel's title.</summary>
    public const string SkillsTitle = "Skills";

    /// <summary>The skill panel's hint when nothing is selected.</summary>
    public const string PickASkill = "Tap a skill to read it or learn it.";

    /// <summary>The learn button.</summary>
    public const string Learn = "Learn";

    /// <summary>Points left to spend, e.g. <c>Skill points: 3</c>.</summary>
    public static string SkillPoints(int points) => "Skill points: " + points.ToString(CultureInfo.InvariantCulture);

    /// <summary>The player's job line in the skill panel.</summary>
    public static string JobLine(string job) => "Job: " + job;

    /// <summary>A skill's level in the tree, e.g. <c>Lv 2/5</c>.</summary>
    public static string SkillLevel(int level, int max) => "Lv " + level.ToString(CultureInfo.InvariantCulture) + "/" + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>A requirement that is not met, e.g. <c>Needs Ember Bolt Lv 2</c>.</summary>
    public static string Needs(string skill, int level) => "Needs " + skill + " Lv " + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>A passive skill's tag.</summary>
    public const string Passive = "Passive";

    /// <summary>The job-change button for a job the player can change to.</summary>
    public static string BecomeJob(string job) => "Become " + job;

    /// <summary>The skill bar's cooldown text, whole seconds left rounded up.</summary>
    public static string CooldownLeft(double seconds) => Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>The flash when a skill needs a target (T-502): tap an enemy.</summary>
    public static string TapTarget(string skill) => skill + ": tap a target";

    /// <summary>The flash when a skill needs a cell: tap the ground.</summary>
    public static string TapGround(string skill) => skill + ": tap the ground";

    /// <summary>The flash for a refused skill intent (a "Skill reasons" term); empty for a reason that needs no word.</summary>
    public static string SkillRefused(string reason) => reason switch
    {
        SkillReasons.NotLearned => "You have not learned that.",
        SkillReasons.LevelTooHigh => "You have not learned that level.",
        SkillReasons.PassiveSkill => "That works by itself.",
        SkillReasons.NotEnoughSp => "Not enough SP.",
        SkillReasons.OnCooldown => "Not ready yet.",
        SkillReasons.WrongWeapon => "Your weapon cannot do that.",
        SkillReasons.OutOfRange or "no_line_of_sight" => "You cannot reach that.",
        SkillReasons.SafeZone => "Not here.",
        SkillReasons.InvalidTarget => "That is not a target for it.",
        SkillReasons.TargetGone or SkillReasons.TargetDead => "That target is gone.",
        SkillReasons.CannotUseSkills => "You cannot use skills right now.",
        SkillReasons.NotEnoughPoints => "No skill points left.",
        SkillReasons.NotInTree => "Your job cannot learn that.",
        SkillReasons.AtMaxLevel => "Already at its highest level.",
        SkillReasons.PrerequisiteNotMet => "Learn the skill before it first.",
        SkillReasons.BaseLevelTooLow => "Your level is too low for that job.",
        SkillReasons.JobLevelTooLow => "Your job level is too low for that job.",
        SkillReasons.QuestNotCompleted => "A quest for that job is not done yet.",
        SkillReasons.NotUnlocked => "That job is not open to you yet.",
        SkillReasons.WrongParentJob or SkillReasons.SameJob or SkillReasons.UnknownJob or SkillReasons.UnknownSkill => "That is not possible.",
        _ => string.Empty,
    };

    /// <summary>Cancels a pending choice (card target or refine).</summary>
    public const string Cancel = "Cancel";

    /// <summary>The hint above the equipment a selected card fits.</summary>
    public const string PickEquipmentForCard = "Set the card in (it stays there for good):";

    /// <summary>A selected card that fits nothing the player has.</summary>
    public const string NothingFitsCard = "Nothing you have can hold this card.";

    /// <summary>A card target button: the equipment's line and, while worn, its slot.</summary>
    public static string CardTarget(string equipmentLine, string? slot) => slot is null ? equipmentLine : equipmentLine + " (" + SlotName(slot) + ")";

    /// <summary>
    /// What refining offers, from the bundle's refine step for the next level: the chance, the material (with how many are carried) and what a failure does.
    /// Every number comes from the bundle; nothing here is a client constant.
    /// </summary>
    public static string RefineOffer(string item, int nextLevel, int successPercent, string material, int quantity, int carried, string failure)
    {
        var outcome = failure switch
        {
            RefineFailures.Down => "it loses a level",
            RefineFailures.Break => "it breaks and is gone, with its cards",
            _ => "it stays as it is",
        };
        return "Refine " + item + " to +" + nextLevel.ToString(CultureInfo.InvariantCulture) + ": " + successPercent.ToString(CultureInfo.InvariantCulture)
            + "% chance. Uses " + material + " x" + quantity.ToString(CultureInfo.InvariantCulture) + " (you have " + carried.ToString(CultureInfo.InvariantCulture)
            + "), success or not. If it fails, " + outcome + ".";
    }

    /// <summary>Refining is over for this item.</summary>
    public static string RefineMaxed(string item) => item + " cannot be refined any further.";

    /// <summary>The button that opens the inventory panel.</summary>
    public const string Bag = "Bag";

    /// <summary>The inventory panel's title.</summary>
    public const string BagTitle = "Bag and equipment";

    /// <summary>Uses a consumable.</summary>
    public const string Use = "Use";

    /// <summary>Puts an item on.</summary>
    public const string Equip = "Equip";

    /// <summary>Takes an item off.</summary>
    public const string Unequip = "Take off";

    /// <summary>Puts an item on the ground.</summary>
    public const string Drop = "Drop";

    /// <summary>The panel's hint while nothing is selected.</summary>
    public const string PickAnItem = "Tap an item to use, wear, refine or drop it.";

    /// <summary>An empty equipment slot.</summary>
    public const string EmptySlot = "(empty)";

    /// <summary>An inventory button: the item's name and, for a stack, the count.</summary>
    public static string ItemCount(string name, int quantity) =>
        quantity > 1 ? name + " x" + quantity.ToString(CultureInfo.InvariantCulture) : name;

    /// <summary>The weight line, with the weight state when it limits anything.</summary>
    public static string Weight(int weight, int limit, string load)
    {
        var line = "Weight " + weight.ToString(CultureInfo.InvariantCulture) + " / " + limit.ToString(CultureInfo.InvariantCulture);
        return load switch
        {
            WeightStates.Burdened => line + " (heavy: no regeneration)",
            WeightStates.Overweight => line + " (too heavy to fight)",
            _ => line,
        };
    }

    /// <summary>The entry count, e.g. <c>12 / 100 items</c>.</summary>
    public static string EntryCount(int count, int slots) => count.ToString(CultureInfo.InvariantCulture) + " / " + slots.ToString(CultureInfo.InvariantCulture) + " items";

    /// <summary>An equipment slot's label (an "Equipment slots" term).</summary>
    public static string SlotName(string slot) => slot switch
    {
        EquipSlots.HeadTop => "Head",
        EquipSlots.HeadMid => "Face",
        EquipSlots.HeadLow => "Mouth",
        EquipSlots.Body => "Body",
        EquipSlots.Weapon => "Weapon",
        EquipSlots.Shield => "Shield",
        EquipSlots.Garment => "Cloak",
        EquipSlots.Footgear => "Feet",
        EquipSlots.Accessory1 => "Ring 1",
        EquipSlots.Accessory2 => "Ring 2",
        _ => slot,
    };

    /// <summary>An equipment slot button: its label and what is worn there.</summary>
    public static string SlotButton(string slot, string? item) => SlotName(slot) + ": " + (item ?? EmptySlot);

    /// <summary>The flash for a level reached.</summary>
    public static string LevelUp(int level) => "Level " + level.ToString(CultureInfo.InvariantCulture) + "!";

    /// <summary>The flash for a job level reached.</summary>
    public static string JobLevelUp(int level) => "Job level " + level.ToString(CultureInfo.InvariantCulture) + "!";

    /// <summary>The info panel's second line for a kind of entity (an <c>EntityKinds</c> term). A dead one says so.</summary>
    public static string EntityKind(string kind, bool dead)
    {
        var label = kind switch
        {
            EntityKinds.Player => "Adventurer",
            EntityKinds.Monster => "Monster",
            EntityKinds.Npc => "Villager",
            EntityKinds.Item => "Item on the ground",
            _ => string.Empty,
        };
        return dead && label.Length > 0 ? label + " (defeated)" : label;
    }

    /// <summary>The button that cycles the UI size, showing the current one (a percentage).</summary>
    /// <summary>The sound settings button.</summary>
    public const string Sound = "Sound";

    /// <summary>Closes the sound settings.</summary>
    public const string Close = "Close";

    /// <summary>A bus volume button, e.g. <c>Music 70%</c>.</summary>
    public static string BusVolume(string name, double volume) => name + " " + Math.Round(volume * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>The mute switch.</summary>
    public static string Mute(bool muted) => muted ? "Sound off" : "Sound on";

    public static string UiSize(double scale) => "UI " + Math.Round(scale * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>The small connection indicator: a phase in one or two words.</summary>
    public static string Connection(ConnectionPhase phase) => phase switch
    {
        ConnectionPhase.Online => "Online",
        ConnectionPhase.Connecting or ConnectionPhase.AwaitingWelcome => "Connecting",
        ConnectionPhase.Backoff => "Offline",
        ConnectionPhase.LoggingOut => "Leaving",
        _ => string.Empty,
    };

    /// <summary>The HUD's HP line.</summary>
    public static string Hp(int hp, int max) => "HP " + hp.ToString(CultureInfo.InvariantCulture) + " / " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The HUD's SP line.</summary>
    public static string Sp(int sp, int max) => "SP " + sp.ToString(CultureInfo.InvariantCulture) + " / " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The HUD's EXP line (<paramref name="next"/> 0 means the cap).</summary>
    public static string Exp(int level, long exp, long next) =>
        "Lv " + level.ToString(CultureInfo.InvariantCulture) + "  EXP " + exp.ToString(CultureInfo.InvariantCulture) +
        (next > 0 ? " / " + next.ToString(CultureInfo.InvariantCulture) : string.Empty);
}
