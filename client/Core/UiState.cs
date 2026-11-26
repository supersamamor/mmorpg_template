using Game.Client.Core.Bundle;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Client.Core;

/// <summary>
/// What the player's own actions and owner-only messages put on screen: the tap marker, the target highlight, the death screen and the
/// one-line flash. None of it decides anything: a marker is a request shown back, and the server's answer removes it.
/// </summary>
public sealed class UiState
{
    private readonly ClientConfig _config;
    private readonly DisplayBundle _bundle;
    private double _markerEnds;
    private double _flashEnds;

    /// <summary>Creates an empty state.</summary>
    public UiState(ClientConfig config, DisplayBundle bundle)
    {
        _config = config;
        _bundle = bundle;
    }

    /// <summary>The cell of the tap marker, or null.</summary>
    public GridCell? Marker { get; private set; }

    /// <summary>The entity with the target highlight, or null.</summary>
    public long? Target { get; private set; }

    /// <summary>True while the death screen shows.</summary>
    public bool Dead { get; private set; }

    /// <summary>The flash line; empty when nothing shows.</summary>
    public string Flash { get; private set; } = string.Empty;

    /// <summary>The skill waiting for a tap on a target or the ground (T-502), or null.</summary>
    public string? PendingSkill { get; private set; }

    /// <summary>Counts level-ups (base or job), so the presenter plays its effect and the HUD flashes once per level.</summary>
    public int LevelUpSerial { get; private set; }

    /// <summary>Counts changes to anything here, so a HUD redraws only on a change.</summary>
    public int Version { get; private set; }

    /// <summary>A ground tap went out as <c>move</c>: show the marker at once (§7.3). A new walk ends any highlight, as it ends the attack on the server.</summary>
    public void OnMoveSent(GridCell cell, double now)
    {
        Marker = cell;
        Target = null;
        _markerEnds = now + _config.TapMarkerSeconds;
        Version++;
    }

    /// <summary>A monster tap went out as <c>attack</c>.</summary>
    public void OnAttackSent(long target)
    {
        Target = target;
        Marker = null;
        Version++;
    }

    /// <summary>A ground item tap went out as <c>pick_up</c>: a new walk ends the highlight, as it ends the attack on the server.</summary>
    /// <summary>A skill that needs a tap: the next tap on an enemy or the ground casts it. The flash says so.</summary>
    public void SetPending(string skillId, string message, double now)
    {
        PendingSkill = skillId;
        SetFlash(message, now);
    }

    /// <summary>Drops the pending skill (cast, cancelled or the character died).</summary>
    public void ClearPending()
    {
        if (PendingSkill is null) return;
        PendingSkill = null;
        Version++;
    }

    /// <summary>A <c>use_skill</c> went out: the target (if any) is the one shown, and nothing is pending any more.</summary>
    public void OnSkillSent(long? target)
    {
        PendingSkill = null;
        if (target is { } id)
        {
            Target = id;
            Marker = null;
        }
        Version++;
    }

    public void OnPickUpSent()
    {
        Marker = null;
        Target = null;
        Version++;
    }

    /// <summary>An own inventory change that the player should hear of: a pick-up becomes the flash line.</summary>
    public void OnInventoryChanged(InventoryChangedMessage change, double now)
    {
        if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.PickedUp && change.Item is { } item) SetFlash(UiText.PickedUp(_bundle.NameOf(item)), now);
        else if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.ShopBuy && change.Item is { } bought) SetFlash(UiText.Bought(_bundle.NameOf(bought)), now);
        else if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.ShopSell && change.Item is { } sold) SetFlash(UiText.Sold(_bundle.NameOf(sold)), now);
        else if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.CardInserted && change.Item is { } socketed) SetFlash(UiText.CardInserted(_bundle.NameOf(socketed)), now);
    }

    /// <summary>A <c>quest</c> change (T-602): accepted, a step done, completed or abandoned goes on the flash line with the quest's name.</summary>
    public void OnQuest(QuestMessage message, double now)
    {
        if (UiText.QuestChange(message.Change, _bundle.Quest(message.Quest)?.Name ?? message.Quest) is { Length: > 0 } text) SetFlash(text, now);
    }

    /// <summary>An <c>autopilot</c> message ended a run (T-605): the flash line says why, so a stop is never a surprise.</summary>
    public void OnAutopilotEnded(string? reason, double now) => SetFlash(UiText.AutopilotEnded(reason), now);

    /// <summary>A new outfit is in the wardrobe (T-708).</summary>
    public void OnOutfitGained(string outfitId, double now) => SetFlash(UiText.OutfitGained(_bundle.NameOf(outfitId)), now);

    /// <summary><c>refine_result</c>: the outcome goes on the flash line (success, kept, down a level, broke), with the item's name and level from the server.</summary>
    public void OnRefineResult(RefineResultMessage result, double now) =>
        SetFlash(UiText.RefineResult(_bundle.NameOf(result.Item), result.Result, result.Refine), now);

    /// <summary><c>rejected</c>: quietly take back what the tap showed; a refused item intent says why on the flash line.</summary>
    public void OnRejected(RejectedMessage rejected, double now = 0)
    {
        if (rejected.Intent == MessageTypes.StartAutopilot)
        {
            SetFlash(UiText.AutopilotRefused(rejected.Reason, rejected.Quest is { } q ? _bundle.Quest(q)?.Name ?? q : rejected.Skill is { } sk ? _bundle.Skill(sk)?.Name ?? sk : null), now);   // nothing changed: a run in progress goes on
            return;
        }
        if (rejected.Intent == MessageTypes.SetOutfit)
        {
            SetFlash(UiText.OutfitRefused(rejected.Reason), now);
            return;
        }
        if (IsItemIntent(rejected.Intent))
        {
            if (UiText.ItemRefused(rejected.Reason) is { Length: > 0 } why) SetFlash(why, now);
            return;
        }
        if (rejected.Quest is not null)
        {
            SetFlash(UiText.QuestRefused(rejected.Reason), now);   // a start (the choice that asked) or an abandon (T-602)
            return;
        }
        if (rejected.Intent is MessageTypes.Talk or MessageTypes.DialogueChoice)
        {
            if (UiText.DialogueRefused(rejected.Reason) is { Length: > 0 } talkWhy) SetFlash(talkWhy, now);
            return;
        }
        if (rejected.Intent == MessageTypes.RaiseStat)
        {
            if (UiText.StatRefused(rejected.Reason) is { Length: > 0 } statWhy) SetFlash(statWhy, now);
            return;
        }
        if (IsSkillIntent(rejected.Intent))
        {
            if (rejected.Intent == MessageTypes.UseSkill) PendingSkill = null;
            if (UiText.SkillRefused(rejected.Reason) is { Length: > 0 } skillWhy) SetFlash(skillWhy, now);
            Version++;
            return;
        }
        // A stun (T-501): say why the tap did nothing.
        if (UiText.Held(rejected.Reason) is { Length: > 0 } held) SetFlash(held, now);
        if (rejected.Intent == MessageTypes.Move && Marker is { } marker && rejected.Cell is { } cell && marker.X == cell.X && marker.Y == cell.Y)
        {
            Marker = null;
            Version++;
        }
        else if (rejected.Intent == MessageTypes.Attack && Target == rejected.Target)
        {
            Target = null;
            Version++;
        }
    }

    /// <summary><c>attack_stopped</c>: the auto-attack is over.</summary>
    public void OnAttackStopped(AttackStoppedMessage stopped)
    {
        if (Target != stopped.Target) return;
        Target = null;
        Version++;
    }

    /// <summary><c>you_died</c>.</summary>
    public void OnYouDied()
    {
        PendingSkill = null;
        SetDead(true);
        Marker = null;
        Target = null;
        Version++;
    }

    /// <summary><c>respawned</c>.</summary>
    public void OnRespawned() => SetDead(false);

    /// <summary>Own vitals. A snapshot after a resume may show the character already dead (ADR-002 row 102): HP 0 is the death screen, however it was learned.</summary>
    public void OnVitals(VitalsRecord vitals)
    {
        if (vitals.MaxHp <= 0) return;
        SetDead(vitals.Hp <= 0);
    }

    /// <summary>A delta's <c>hits</c> this client is part of become the flash line.</summary>
    public void OnHits(System.Collections.Generic.IReadOnlyList<HitRecord> hits, WorldView world, double now)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (hit.Attacker == world.OwnId) SetFlash(UiText.YouHit(NameOfEntity(world, hit.Target), hit.Amount, hit.Result), now);
            else if (hit.Target == world.OwnId) SetFlash(UiText.HitYou(NameOfEntity(world, hit.Attacker), hit.Amount, hit.Result), now);
        }
    }

    /// <summary>A delta's new ground items that this client may take become the flash line.</summary>
    public void OnAppeared(System.Collections.Generic.IReadOnlyList<EntityRecord> appeared, double now)
    {
        for (var i = 0; i < appeared.Count; i++)
        {
            var record = appeared[i];
            if (record.Kind == EntityKinds.Item && record.Mine == true) SetFlash(UiText.Dropped(_bundle.NameOf(record.DisplayKey), record.Quantity), now);
        }
    }

    /// <summary><c>level_up</c>.</summary>
    public void OnLevelUp(LevelUpMessage level, double now)
    {
        LevelUpSerial++;
        if (level.BaseLevel is { } baseLevel) SetFlash(UiText.LevelUp(baseLevel), now);
        else if (level.JobLevel is { } jobLevel) SetFlash(UiText.JobLevelUp(jobLevel), now);
    }

    /// <summary>Per frame: timers, the marker ending when the walk arrives, and a target leaving view. Allocates nothing.</summary>
    public void Tick(WorldView world, double now)
    {
        if (Marker is { } marker && (now >= _markerEnds || Arrived(world.Own, marker)))
        {
            Marker = null;
            Version++;
        }
        if (Target is { } target && world.Find(target) is null)
        {
            Target = null;
            Version++;
        }
        if (Flash.Length > 0 && now >= _flashEnds)
        {
            Flash = string.Empty;
            Version++;
        }
    }

    /// <summary>Clears everything (a new login).</summary>
    public void Reset()
    {
        Marker = null;
        Target = null;
        PendingSkill = null;
        Dead = false;
        Flash = string.Empty;
        Version++;
    }

    private static bool IsSkillIntent(string intent) =>
        intent == MessageTypes.UseSkill || intent == MessageTypes.LearnSkill || intent == MessageTypes.ChangeJob;

    private static bool IsItemIntent(string intent) =>
        intent == MessageTypes.PickUp || intent == MessageTypes.UseItem || intent == MessageTypes.Equip || intent == MessageTypes.Unequip || intent == MessageTypes.DropItem
        || intent == MessageTypes.InsertCard || intent == MessageTypes.Refine || intent == MessageTypes.ShopBuy || intent == MessageTypes.ShopSell;

    private static bool Arrived(EntityView? own, GridCell marker) =>
        own is not null && !own.Next.HasValue && !own.IsWalking && own.Cell.X == marker.X && own.Cell.Y == marker.Y;

    private string NameOfEntity(WorldView world, long id) => _bundle.NameOf(world.Find(id)?.DisplayKey);

    private void SetDead(bool dead)
    {
        if (Dead == dead) return;
        Dead = dead;
        Version++;
    }

    private void SetFlash(string text, double now)
    {
        Flash = text;
        _flashEnds = now + _config.FlashSeconds;
        Version++;
    }
}
