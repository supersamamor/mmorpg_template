using Game.Shared.World;

namespace Game.Server.World.Combat;

/// <summary>
/// A player asks to cast <c>SkillId</c> (T-502) at <c>Level</c> (0: the learned level) on the entity <c>Target</c> or the ground <c>Cell</c>, as the
/// skill's <c>target</c> says; a friendly entity skill without a target is cast on the caster. Combat checks it (<c>SkillRefused</c> otherwise), walks the
/// player into range like an auto-attack, then casts for the cast time standing still. A new order cancels a cast.
/// </summary>
public sealed record UseSkill(EntityId Entity, string SkillId, int Level, EntityId? Target, GridCell? Cell) : IIntent;

/// <summary>A player asks to spend one skill point on <c>SkillId</c> (T-502). Refused with <c>SkillLearnRefused</c>.</summary>
public sealed record LearnSkill(EntityId Entity, string SkillId) : IIntent;

/// <summary>A player asks to raise primary stat <c>StatId</c> by <c>Amount</c> steps of one (T-503). Refused with <c>StatRaiseRefused</c>.</summary>
public sealed record RaiseStat(EntityId Entity, string StatId, int Amount) : IIntent;

/// <summary>A player asks to change to <c>JobId</c> (T-502). Refused with <c>JobChangeRefused</c>.</summary>
public sealed record ChangeJob(EntityId Entity, string JobId) : IIntent;
