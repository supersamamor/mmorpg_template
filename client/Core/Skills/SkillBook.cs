using System;
using System.Collections.Generic;
using Game.Client.Core.Bundle;
using Game.Client.Core.Ui;
using Game.Shared.Protocol;

namespace Game.Client.Core.Skills;

/// <summary>Where a skill stands in the tree for the player (T-502).</summary>
public enum SkillState
{
    /// <summary>Not learned, and a requirement or a point is missing.</summary>
    Locked,

    /// <summary>One point would raise it now.</summary>
    Learnable,

    /// <summary>Learned, and not at its highest level.</summary>
    Learned,

    /// <summary>At the highest level the tree allows.</summary>
    Maxed,
}

/// <summary>One line of the skill tree panel.</summary>
/// <param name="JobId">The job whose tree has it (the current job or a parent).</param>
/// <param name="SkillId">The skill.</param>
/// <param name="Level">Learned level (0 when not learned).</param>
/// <param name="Max">Highest level the tree allows.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Missing">Requirements not met yet (empty when none), as skill id and level.</param>
public sealed record SkillRow(string JobId, string SkillId, int Level, int Max, SkillState State, IReadOnlyList<SkillNeed> Missing);

/// <summary>
/// The player's own skills as the server last said them (T-502, <c>skills</c> and <c>cooldown</c>): job, unspent points, learned levels, cooldowns on the
/// view clock and the jobs it may change to. Nothing here decides anything: the tree rows say what the panel shows, the server checks every learn and cast.
/// <see cref="Version"/> moves on every change and, while a cooldown runs, on every <see cref="Update"/>, so a sweep redraws each frame and a quiet bar costs nothing.
/// </summary>
public sealed class SkillBook : IHotbarContent
{
    private readonly DisplayBundle _bundle;
    private readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cooldown> _cooldowns = new(StringComparer.Ordinal);
    private readonly List<string> _active = new();
    private readonly List<string> _jobs = new();
    private readonly List<SkillRow> _rows = new();
    private double _now;
    private int _version;
    private int _slotSerial;

    /// <summary>Creates an empty book (nothing learned until the server says).</summary>
    public SkillBook(DisplayBundle bundle) => _bundle = bundle;

    /// <summary>The player's job id, or empty before the first <c>skills</c> message.</summary>
    public string JobId { get; private set; } = string.Empty;

    /// <summary>Unspent skill points.</summary>
    public int Points { get; private set; }

    /// <summary>True once the server has sent the list.</summary>
    public bool Known => JobId.Length > 0;

    /// <summary>The jobs the player may change to right now.</summary>
    public IReadOnlyList<string> ChangeableJobs => _jobs;

    /// <summary>Counts changes (and cooldown frames), so a screen redraws only when this moved.</summary>
    public int Version => _version;

    /// <inheritdoc />
    int IHotbarContent.Version => _version + _slotSerial;

    /// <summary>The learned level of a skill (0 when not learned).</summary>
    public int Level(string skillId) => _levels.TryGetValue(skillId, out var level) ? level : 0;

    /// <summary>The learned active skills in tree order: what the skill bar shows.</summary>
    public IReadOnlyList<string> ActiveSkills => _active;

    /// <summary>The rows of the skill tree for the current job and its parents, parents first, in content order.</summary>
    public IReadOnlyList<SkillRow> Rows => _rows;

    /// <summary>Takes the server's list: replaces everything but keeps the cooldowns already running (the list carries the ones left).</summary>
    public void Apply(SkillsMessage message, double now)
    {
        _now = now;
        JobId = message.Job;
        Points = message.Points;
        _levels.Clear();
        foreach (var level in message.Levels) _levels[level.Skill] = level.Level;
        _jobs.Clear();
        _jobs.AddRange(message.Jobs);
        _cooldowns.Clear();
        foreach (var cooldown in message.Cooldowns)
            if (cooldown.Ms > 0) _cooldowns[cooldown.Skill] = new Cooldown(now + cooldown.Ms / 1000.0, cooldown.Ms / 1000.0);
        Rebuild();
    }

    /// <summary>A skill went on cooldown for <c>ms</c> milliseconds, counted from now on the view clock.</summary>
    public void ApplyCooldown(CooldownMessage message, double now)
    {
        _now = now;
        if (message.Ms <= 0) return;
        _cooldowns[message.Skill] = new Cooldown(now + message.Ms / 1000.0, message.Ms / 1000.0);
        _version++;
    }

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        JobId = string.Empty;
        Points = 0;
        _levels.Clear();
        _cooldowns.Clear();
        _jobs.Clear();
        Rebuild();
    }

    /// <summary>Advances the cooldown clock; the version moves for every frame in which a cooldown runs or has just ended.</summary>
    public void Update(double now)
    {
        _now = now;
        if (_cooldowns.Count == 0) return;
        List<string>? over = null;
        foreach (var pair in _cooldowns)
            if (pair.Value.Ends <= now) (over ??= new List<string>()).Add(pair.Key);
        if (over is not null)
            foreach (var id in over) _cooldowns.Remove(id);
        _version++;
    }

    /// <summary>Seconds left on a skill's cooldown (0 when ready).</summary>
    public double CooldownLeft(string skillId) => _cooldowns.TryGetValue(skillId, out var c) ? Math.Max(0, c.Ends - _now) : 0;

    /// <summary>The fraction of the cooldown still to run, 1 just after the cast and 0 when ready.</summary>
    public double CooldownFraction(string skillId) =>
        _cooldowns.TryGetValue(skillId, out var c) && c.Duration > 0 ? Math.Clamp((c.Ends - _now) / c.Duration, 0, 1) : 0;

    /// <summary>True while any cooldown runs.</summary>
    public bool AnyCooldown => _cooldowns.Count > 0;

    // ---- the skill bar ----

    /// <inheritdoc />
    public HotbarSlotView Slot(int index)
    {
        if (index < 0 || index >= _active.Count) return default;
        var id = _active[index];
        var left = CooldownFraction(id);
        return new HotbarSlotView(id, left, left <= 0);
    }

    /// <summary>The skill on a bar slot, or null for an empty one.</summary>
    public string? SkillAt(int index) => index >= 0 && index < _active.Count ? _active[index] : null;

    // ---- the tree ----

    private void Rebuild()
    {
        _version++;
        _slotSerial++;
        _rows.Clear();
        _active.Clear();
        var chain = new List<JobDisplay>();
        var guard = 0;
        for (var job = _bundle.Job(JobId); job is not null && guard++ < 16; job = _bundle.Job(job.Parent)) chain.Insert(0, job);
        foreach (var job in chain)
        {
            foreach (var entry in job.Skills)
            {
                var skill = _bundle.Skill(entry.SkillId);
                if (skill is null) continue;
                var max = Math.Min(skill.MaxLevel, entry.MaxLevel ?? skill.MaxLevel);
                var level = Level(entry.SkillId);
                var missing = new List<SkillNeed>();
                foreach (var need in entry.Requires)
                    if (Level(need.SkillId) < need.Level) missing.Add(need);
                var state = level >= max ? SkillState.Maxed
                    : missing.Count == 0 && Points > 0 ? SkillState.Learnable
                    : level > 0 ? SkillState.Learned
                    : SkillState.Locked;
                _rows.Add(new SkillRow(job.Id, entry.SkillId, level, max, state, missing));
                if (level > 0 && !skill.Passive) _active.Add(entry.SkillId);
            }
        }
    }

    private readonly record struct Cooldown(double Ends, double Duration);
}
