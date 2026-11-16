using System;
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
        ItemReasons.UnsupportedEffect => "That cannot be used yet.",
        ItemReasons.NotEquipment => "That cannot be worn.",
        ItemReasons.WrongSlot => "That does not go there.",
        ItemReasons.LevelTooLow => "Your level is too low for that.",
        ItemReasons.WrongJob => "Your job cannot use that.",
        ItemReasons.NotEquipped => "Nothing is worn there.",
        ItemReasons.ItemEquipped => "Take it off first.",
        ItemReasons.InvalidQuantity => "You do not have that many.",
        _ => string.Empty,   // cancelled: the player moved on, nothing to say
    };

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
    public const string PickAnItem = "Tap an item to use, wear or drop it.";

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
