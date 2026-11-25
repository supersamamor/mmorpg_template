using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Client.Core.Accounts;
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
            case ConnectionNotice.LoadFailed: return "Your character could not be loaded. Nothing was lost. Please try again later.";
            case ConnectionNotice.SavePending: return "Your character is still being saved. Please try again in a moment.";
            case ConnectionNotice.SaveLost: return "The server lost touch with your character's save. Please log in again.";
            case ConnectionNotice.SessionEnded: return "You were signed out from another device, or your password was changed.";
            case ConnectionNotice.ConnectionFailed: return "Could not reach the server.";
            case ConnectionNotice.UpdateRequired: return "A new version is required to play. Please update the app.";
            case ConnectionNotice.Replaced: return "You are playing on another connection.";
            case ConnectionNotice.LoggedOut: return "You have logged out.";
            case ConnectionNotice.ServerShutdown: return "The server is restarting. Trying again soon...";
            default: return string.Empty;
        }
    }

    // ---- sign-in and character select (T-701) ----

    /// <summary>The sign-in screen's title.</summary>
    public const string SignInTitle = "Sign in";

    /// <summary>The username field's hint.</summary>
    public const string Username = "Username";

    /// <summary>The password field's hint.</summary>
    public const string Password = "Password (at least 10 characters)";

    /// <summary>The sign-in button.</summary>
    public const string SignIn = "Sign in";

    /// <summary>The register button.</summary>
    public const string Register = "New account";

    /// <summary>The development login's button (debug builds only).</summary>
    public const string DevLogin = "Dev login";

    /// <summary>The character screen's title.</summary>
    public const string CharactersTitle = "Your characters";

    /// <summary>The button that creates a character.</summary>
    public const string CreateCharacter = "Create character";

    /// <summary>The button that opens the creation screen (T-706).</summary>
    public const string NewCharacter = "New character";

    /// <summary>The creation screen's random button.</summary>
    public const string RandomLook = "Random";

    /// <summary>The creation screen's way back to the character list.</summary>
    public const string Back = "Back";

    /// <summary>The title over the genders.</summary>
    public const string CreationGender = "Body";

    /// <summary>The title over the faces.</summary>
    public const string CreationFace = "Face";

    /// <summary>The title over the hairstyles.</summary>
    public const string CreationHairstyle = "Hairstyle";

    /// <summary>The title over the skin tones.</summary>
    public const string CreationSkin = "Skin tone";

    /// <summary>The title over the hair colours.</summary>
    public const string CreationHairColor = "Hair colour";

    /// <summary>The chosen option's button: marked in words as well as by its frame.</summary>
    public static string Chosen(string name) => "[ " + name + " ]";

    /// <summary>The button that deletes a character.</summary>
    public const string DeleteCharacter = "Delete";

    /// <summary>The same button, once pressed.</summary>
    public const string ConfirmDelete = "Really?";

    /// <summary>The button that leaves the account.</summary>
    public const string SignOut = "Sign out";

    /// <summary>The button that opens the password screen (T-710).</summary>
    public const string ChangePassword = "Change password";

    /// <summary>The password screen's title.</summary>
    public const string PasswordTitle = "Change password";

    /// <summary>The hint of the current password's field.</summary>
    public const string CurrentPassword = "Current password";

    /// <summary>The hint of the new password's field.</summary>
    public const string NewPassword = "New password (at least 10 characters)";

    /// <summary>The hint of the repeat's field.</summary>
    public const string RepeatPassword = "New password again";

    /// <summary>What a password change does besides.</summary>
    public const string PasswordHelp = "This signs out every other device and ends a game running there.";

    /// <summary>The refusal of a password change whose current password is wrong.</summary>
    public const string WrongCurrentPassword = "The current password is wrong.";

    /// <summary>The line while the server is asked.</summary>
    public const string AccountBusy = "One moment...";

    /// <summary>A character's line: its name and its job.</summary>
    public static string CharacterRow(string name, string job) => name + "\n" + job;

    /// <summary>The hint of the new character's name field.</summary>
    public static string NewCharacterName(int min, int max) =>
        "Name (" + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + " letters)";

    /// <summary>The line for an account notice: for a refusal, the sentence of its "Account errors" term.</summary>
    public static string Account(AccountNotice notice, string error, long retryAfterSeconds)
    {
        switch (notice)
        {
            case AccountNotice.Unreachable: return "Could not reach the server.";
            case AccountNotice.SessionEnded: return "Your session has ended. Please sign in again.";
            case AccountNotice.PasswordsDiffer: return "The two new passwords are not the same.";
            case AccountNotice.PasswordChanged: return "Your password is changed.";
            case AccountNotice.Refused: break;
            default: return string.Empty;
        }
        switch (error)
        {
            case AccountErrors.InvalidUsername: return "A username has 3 to 24 letters, digits, dots, dashes or underscores, and starts with a letter or digit.";
            case AccountErrors.InvalidPassword: return "A password has 10 to 128 characters.";
            case AccountErrors.SamePassword: return "The new password is the same as the old one.";
            case AccountErrors.UsernameTaken: return "That username is taken.";
            case AccountErrors.InvalidSession: return Account(AccountNotice.SessionEnded, error, 0);
            case AccountErrors.InvalidCredentials: return "Wrong username or password.";
            case AccountErrors.RateLimited:
                return retryAfterSeconds > 0
                    ? "Too many tries. Please wait " + retryAfterSeconds.ToString(CultureInfo.InvariantCulture) + " seconds."
                    : "Too many tries. Please wait a moment.";
            case AccountErrors.InvalidName: return "That name cannot be used. Use letters, with single dashes or apostrophes between them at most.";
            case AccountErrors.InvalidAppearance: return "That look is not available. Pick another.";
            case AccountErrors.NameTaken: return "That name is taken.";
            case AccountErrors.NoFreeSlot: return "You have no free character slot.";
            case AccountErrors.UnknownCharacter: return "That character no longer exists.";
            case AccountErrors.CharacterInUse: return "That character is still in the world. Please try again in a minute.";
            case AccountErrors.Unavailable: return "The server is busy. Please try again.";
            default: return "The server could not use that. Please try again.";
        }
    }

    /// <summary>The longest line <see cref="Account"/> can return, for the layout check.</summary>
    public static string LongestAccount => Account(AccountNotice.Refused, AccountErrors.InvalidUsername, 0);

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
        ItemReasons.ServiceRequired => "Find a smith to refine your gear.",
        ItemReasons.OutOfRange => "Step closer to the shop.",
        ItemReasons.NotInStock => "The shop does not sell that.",
        ItemReasons.QuantityLimit => "That is more than the shop will sell at once.",
        ItemReasons.CannotAfford => "You cannot afford that.",
        ItemReasons.NotSellable => "The shop will not buy that.",
        ItemReasons.CurrencyFull => "You cannot carry any more money.",
        ItemReasons.Dead => "You cannot trade now.",
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

    /// <summary>The button of a dialogue node without choices that goes on (T-601).</summary>
    public const string DialogueContinue = "Continue";

    /// <summary>The button of a dialogue's last node, which ends it.</summary>
    public const string DialogueDone = "Goodbye";

    /// <summary>What an NPC without a name in the bundle is called (a speaker the display bundle does not know).</summary>
    public const string DialogueUnknownSpeaker = "Someone";

    /// <summary>The flash for a refused talk or dialogue choice (a "Dialogue reasons" term); empty for one that needs no word.</summary>
    public static string DialogueRefused(string reason) => reason switch
    {
        DialogueReasons.TargetGone => "They are not here.",
        DialogueReasons.InvalidTarget => "There is nobody to talk to.",
        DialogueReasons.NoDialogue => "They have nothing to say.",
        DialogueReasons.Unreachable => "You cannot get there.",
        DialogueReasons.InvalidChoice => "That is no longer possible.",
        DialogueReasons.Busy => "One moment.",
        DialogueReasons.MissingItem => "You do not have what that needs.",
        DialogueReasons.InventoryFull => "Your bag is full.",
        DialogueReasons.TooHeavy => "Too heavy to carry.",
        _ => string.Empty,   // dead, cancelled: nothing to say
    };

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

    // ---- quests (T-602) ----------------------------------------------------------------------------------------------------------------------

    /// <summary>The tracker when no quest is active (it opens the quest log like the tracker does).</summary>
    public const string QuestsButton = "Quests";

    /// <summary>The quest log's title.</summary>
    public const string QuestLogTitle = "Quests";

    /// <summary>The heading over the active quests.</summary>
    public const string ActiveQuests = "Active";

    /// <summary>The heading over the completed quests.</summary>
    public const string CompletedQuests = "Completed";

    /// <summary>The log line when no quest is active: where quests come from.</summary>
    public const string NoActiveQuests = "No quests yet. Talk to people with a ! over their heads.";

    /// <summary>The log line when nothing is completed yet.</summary>
    public const string NoCompletedQuests = "None yet.";

    /// <summary>The log's button that puts a quest in the tracker.</summary>
    public const string Track = "Track";

    /// <summary>The log's button that drops a quest (the server forgets its progress).</summary>
    public const string Abandon = "Abandon";

    /// <summary>The confirmation of Abandon: progress is lost and nothing comes back.</summary>
    public const string ConfirmAbandon = "Abandon it (progress is lost)";

    /// <summary>A step line of the log: <c>Step 2/3: text</c>.</summary>
    public static string QuestStep(int index, int count, string text) =>
        "Step " + (index + 1).ToString(CultureInfo.InvariantCulture) + "/" + Math.Max(1, count).ToString(CultureInfo.InvariantCulture) + ": " + text;

    /// <summary>One objective with its counter, e.g. <c>Gel Slime 3/5</c>; <paramref name="target"/> is the display name of what it is about.</summary>
    public static string QuestObjective(string type, string target, int have, int need)
    {
        var count = have.ToString(CultureInfo.InvariantCulture) + "/" + need.ToString(CultureInfo.InvariantCulture);
        var done = have >= need ? " (done)" : string.Empty;
        return type switch
        {
            QuestObjectiveTypes.Kill => "Defeat " + target + " " + count,
            QuestObjectiveTypes.Collect => target + " " + count,
            QuestObjectiveTypes.Talk => "Talk to " + target + done,
            QuestObjectiveTypes.Reach => "Reach the spot (" + target + ")" + done,
            QuestObjectiveTypes.Level => "Base level " + count,
            QuestObjectiveTypes.Job => "Become " + target + done,
            QuestObjectiveTypes.UseItem => "Use " + target + " " + count,
            QuestObjectiveTypes.Equip => "Wear " + target + done,
            _ => target + " " + count,
        };
    }

    /// <summary>Why a met step waits (a "Quest reasons" term): the turn-in's items need room.</summary>
    public static string QuestHeld(string reason) => reason switch
    {
        QuestReasons.InventoryFull => "Make room in your bag to turn this in.",
        QuestReasons.TooHeavy => "Carry less to turn this in.",
        QuestReasons.MissingItem => "Some items it needs are gone.",
        _ => "Waiting.",
    };

    /// <summary>The flash for a quest change (a "Quest changes" term) of the quest named <paramref name="quest"/>; empty for <c>progress</c>.</summary>
    public static string QuestChange(string change, string quest) => change switch
    {
        QuestChanges.Accepted => "New quest: " + quest,
        QuestChanges.StepDone => "Quest updated: " + quest,
        QuestChanges.Completed => "Quest complete: " + quest,
        QuestChanges.Abandoned => "Quest abandoned: " + quest,
        _ => string.Empty,
    };

    // ---- the autopilot (T-605) ----

    /// <summary>The log's tick on a quest that is not in the run.</summary>
    public const string AutopilotAdd = "Add to autopilot";

    /// <summary>The log's tick on a quest in the run, with its place (1 for the first).</summary>
    public static string AutopilotTicked(int order) => "Autopilot: #" + order.ToString(CultureInfo.InvariantCulture);

    /// <summary>Moves a ticked quest earlier in the run.</summary>
    public const string AutopilotEarlier = "Earlier";

    /// <summary>Moves a ticked quest later in the run.</summary>
    public const string AutopilotLater = "Later";

    /// <summary>The log's button that starts the run.</summary>
    public const string AutopilotStart = "Start autopilot";

    /// <summary>The log's button that stops a run.</summary>
    public const string AutopilotStop = "Stop autopilot";

    /// <summary>The log's line over Start and Stop: the run's order, as quest names.</summary>
    public static string AutopilotQueue(string names) => "Run order: " + names;

    /// <summary>The log's line while nothing is ticked.</summary>
    public const string AutopilotNothingTicked = "Nothing ticked yet.";

    /// <summary>What the autopilot does (an "Autopilot activities" term), for the indicator.</summary>
    public static string AutopilotDoing(string? doing) => doing switch
    {
        AutopilotActivities.Travelling => "travelling",
        AutopilotActivities.Hunting => "hunting",
        AutopilotActivities.Gathering => "gathering",
        AutopilotActivities.Talking => "talking",
        AutopilotActivities.Waiting => "waiting",
        _ => "playing",
    };

    /// <summary>The indicator's first line while a run plays: the tap stops it.</summary>
    public static string AutopilotRunning(string? doing) => "Stop autopilot: " + AutopilotDoing(doing);

    /// <summary>The indicator's first line while a run waits: the tap stops it.</summary>
    public const string AutopilotPausedTitle = "Stop autopilot: paused";

    /// <summary>
    /// What the player can do about a pause (an "Autopilot pauses" term); <paramref name="detail"/> is the level, job or item's name it waits for, when known.
    /// </summary>
    public static string AutopilotPause(string? reason, string? detail) => reason switch
    {
        AutopilotPauses.NeedsLevel => detail is null ? "Raise your level to go on." : "Reach level " + detail + " to go on.",
        AutopilotPauses.NeedsJob => detail is null ? "Change your job to go on." : "Become " + detail + " to go on.",
        AutopilotPauses.NeedsEquip => detail is null ? "Put on the gear it needs to go on." : "Wear " + detail + " to go on.",
        AutopilotPauses.NeedsItemUse => detail is null ? "Use the item it needs to go on." : "Use " + detail + " to go on.",
        AutopilotPauses.NoSource => "Nothing gives what it needs. Take over.",
        AutopilotPauses.Unreachable => "No way there. Take over.",
        AutopilotPauses.NoProgress => "Nothing is moving. Take over.",
        AutopilotPauses.NoSafeChoice => "It needs your choice. Take over.",
        _ => "Waiting for you.",
    };

    /// <summary>The flash when a run ends (an "Autopilot stops" term): always says why.</summary>
    public static string AutopilotEnded(string? reason) => reason switch
    {
        AutopilotStops.Finished => "Autopilot finished: every quest is done.",
        AutopilotStops.Stopped => "Autopilot stopped.",
        AutopilotStops.ManualInput => "Autopilot stopped: you took over.",
        AutopilotStops.Died => "Autopilot stopped: you died.",
        AutopilotStops.Logout or AutopilotStops.Disconnected => "Autopilot stopped: the connection ended.",
        AutopilotStops.BagFull => "Autopilot stopped: your bag is full.",
        AutopilotStops.TimeLimit => "Autopilot stopped: no time left today.",
        AutopilotStops.QuestAbandoned => "Autopilot stopped: you dropped its quest.",
        _ => "Autopilot stopped.",
    };

    /// <summary>The flash for a refused start (an "Autopilot reasons" term); <paramref name="about"/> is the display name of the quest or skill it is about, if any.</summary>
    public static string AutopilotRefused(string reason, string? about = null) => reason switch
    {
        AutopilotReasons.Disabled => "Autopilot is switched off here.",
        AutopilotReasons.Dead => "You cannot use autopilot while dead.",
        AutopilotReasons.TimeLimit => "No autopilot time left today.",
        AutopilotReasons.EmptyQueue => "Tick a quest first.",
        AutopilotReasons.TooManyQuests => "Too many quests ticked.",
        AutopilotReasons.UnknownQuest or AutopilotReasons.NotActive => about is null ? "A ticked quest is not active." : about + " is not active.",
        AutopilotReasons.DuplicateQuest => about is null ? "A quest is ticked twice." : about + " is ticked twice.",
        AutopilotReasons.TooManySkills => "Too many skills on the skill bar.",
        AutopilotReasons.UnknownSkill or AutopilotReasons.NotLearned => about is null ? "A skill on the bar is not learned." : about + " is not learned.",
        AutopilotReasons.PassiveSkill => about is null ? "A passive skill cannot be used." : about + " is passive.",
        AutopilotReasons.LevelTooHigh => about is null ? "A skill is above its learned level." : about + " is above its learned level.",
        _ => "Autopilot could not start.",
    };

    /// <summary>The flash for a refused quest start or abandon (a "Quest reasons" term).</summary>
    public static string QuestRefused(string reason) => reason switch
    {
        QuestReasons.AlreadyActive => "You are already on that quest.",
        QuestReasons.AlreadyCompleted => "You have done that quest.",
        QuestReasons.BaseLevelTooLow => "You need a higher level for that quest.",
        QuestReasons.WrongJob => "That quest is not for your job.",
        QuestReasons.QuestNotCompleted => "Finish the quests before it first.",
        QuestReasons.TooManyActive => "You have too many quests. Finish or abandon one.",
        QuestReasons.NotActive => "That quest is not active.",
        _ => "You cannot take that quest.",
    };

    // ---- the shop (T-603) ----

    /// <summary>The shop panel's heading.</summary>
    public const string ShopTitle = "Shop";

    /// <summary>The tab that lists what the shop sells.</summary>
    public const string ShopBuyTab = "Buy";

    /// <summary>The tab that lists the player's bag with the shop's prices.</summary>
    public const string ShopSellTab = "Sell";

    /// <summary>The button that buys the picked item.</summary>
    public const string ShopBuyButton = "Buy";

    /// <summary>The button that sells the picked item.</summary>
    public const string ShopSellButton = "Sell";

    /// <summary>The shop's hint while nothing is picked on the buy tab.</summary>
    public const string ShopPickToBuy = "Tap something to buy.";

    /// <summary>The shop's hint while nothing is picked on the sell tab.</summary>
    public const string ShopPickToSell = "Tap something in your bag to sell.";

    /// <summary>The sell tab when the bag holds nothing the shop will take.</summary>
    public const string ShopNothingToSell = "You have nothing the shop will buy.";

    /// <summary>The minus button of the quantity picker.</summary>
    public const string QuantityLess = "-";

    /// <summary>The plus button of the quantity picker.</summary>
    public const string QuantityMore = "+";

    /// <summary>The quantity picker's jump to the most allowed.</summary>
    public const string QuantityMax = "Max";

    /// <summary>The flash when a purchase went through.</summary>
    public static string Bought(string item) => "Bought: " + item;

    /// <summary>The flash when a sale went through.</summary>
    public static string Sold(string item) => "Sold: " + item;

    /// <summary>A shop row: the item with its price per unit, e.g. <c>Healing Draught - Crowns: 40</c> (an item the shop will not buy has no price).</summary>
    public static string ShopRow(string name, string currencyName, long? price) =>
        price is { } unit ? name + "  -  " + Currency(currencyName, unit) : name + "  -  " + ShopNoPrice;

    /// <summary>What a bag row shows as its price when the shop will not take the item.</summary>
    public const string ShopNoPrice = "not bought";

    /// <summary>The picker's count between its minus and plus buttons, e.g. <c>x5</c>.</summary>
    public static string QuantityLabel(int quantity) => "x" + quantity.ToString(CultureInfo.InvariantCulture);

    /// <summary>The picker's line: how many, and the total, e.g. <c>x5   Crowns: 200</c>.</summary>
    public static string ShopQuantity(int quantity, string currencyName, long? total) =>
        QuantityLabel(quantity) + "   " + (total is { } sum ? Currency(currencyName, sum) : "-");

    /// <summary>The line under the list while something is picked: the item, how many and the total, e.g. <c>Antidote x2   Crowns: 60</c>.</summary>
    public static string ShopPick(string name, int quantity, string currencyName, long? total) => name + " " + ShopQuantity(quantity, currencyName, total);

    /// <summary>The shop panel's heading line: its title and the balance, e.g. <c>Shop   Crowns: 150</c>.</summary>
    public static string ShopHeader(string currencyName, long currency) => ShopTitle + "   " + Currency(currencyName, currency);

    /// <summary>The bag's first line: the entry count and the currency (T-602), e.g. <c>12 / 100 items   Crowns: 150</c>.</summary>
    public static string BagHeader(int count, int slots, string currencyName, long currency) => EntryCount(count, slots) + "   " + Currency(currencyName, currency);

    /// <summary>The bag's currency line, e.g. <c>Crowns: 150</c>; <paramref name="name"/> is the bundle's currency name.</summary>
    public static string Currency(string name, long amount) =>
        (name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : "Money") + ": " + amount.ToString(CultureInfo.InvariantCulture);
}
