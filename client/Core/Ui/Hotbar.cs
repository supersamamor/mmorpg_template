using System;

namespace Game.Client.Core.Ui;

/// <summary>What one hotbar slot shows. Empty until T-502 binds skills (the seam, <see cref="IHotbarContent"/>).</summary>
public readonly struct HotbarSlotView
{
    /// <summary>Creates a slot view.</summary>
    public HotbarSlotView(string? displayKey, double cooldown, bool usable)
    {
        DisplayKey = displayKey;
        Cooldown = cooldown;
        Usable = usable;
    }

    /// <summary>The <c>display_key</c> of what the slot holds, named through the bundle; null for an empty slot.</summary>
    public string? DisplayKey { get; }

    /// <summary>The share of the cooldown still to run, 0 (ready) to 1. The server owns cooldowns (CLAUDE.md rule 1); this only shows what it said.</summary>
    public double Cooldown { get; }

    /// <summary>False when the slot cannot be used now (greyed out).</summary>
    public bool Usable { get; }

    /// <summary>True when nothing is bound.</summary>
    public bool IsEmpty => DisplayKey is null;
}

/// <summary>
/// The seam T-502 fills, part one: what the slots hold. The hotbar redraws a slot only when <see cref="Version"/> changes, so an implementation raises
/// it when a binding, a cooldown or usability changes, and not otherwise.
/// </summary>
public interface IHotbarContent
{
    /// <summary>Counts changes to anything the slots show.</summary>
    int Version { get; }

    /// <summary>The slot at <paramref name="index"/>, 0 to <c>SlotCount - 1</c>.</summary>
    HotbarSlotView Slot(int index);
}

/// <summary>
/// The seam T-502 fills, part two: what pressing a slot does. T-502 sends the skill intent from here; this task sends nothing.
/// </summary>
public interface IHotbarActivator
{
    /// <summary>The slot was pressed and holds something usable.</summary>
    void Activate(int index);
}

/// <summary>What pressing a slot came to.</summary>
public enum HotbarPress
{
    /// <summary>There is no such slot.</summary>
    OutOfRange,

    /// <summary>The slot is empty: only feedback shows.</summary>
    Empty,

    /// <summary>The slot holds something that cannot be used now.</summary>
    Unusable,

    /// <summary>The slot was activated.</summary>
    Activated,
}

/// <summary>The hotbar: a number of slots, what they hold, and what a press does. Nothing is bound at first, so every press is feedback only.</summary>
public sealed class HotbarModel
{
    private IHotbarContent _content = EmptyContent.Instance;
    private IHotbarActivator? _activator;
    private int _bindings;

    /// <summary>Creates a hotbar with <paramref name="slotCount"/> empty slots.</summary>
    public HotbarModel(int slotCount) => SlotCount = Math.Max(0, slotCount);

    /// <summary>How many slots there are (client config).</summary>
    public int SlotCount { get; }

    /// <summary>Changes whenever the content or its binding does, so the view redraws only on a change.</summary>
    public int Version => _bindings * 1_000_003 + _content.Version;

    /// <summary>The slot at <paramref name="index"/>; an empty view for an index out of range.</summary>
    public HotbarSlotView Slot(int index) => index >= 0 && index < SlotCount ? _content.Slot(index) : default;

    /// <summary>T-502 binds what the slots hold and what a press does. Binding null goes back to empty slots.</summary>
    public void Bind(IHotbarContent? content, IHotbarActivator? activator)
    {
        _content = content ?? EmptyContent.Instance;
        _activator = activator;
        _bindings++;
    }

    /// <summary>A slot was pressed. Only a usable, filled slot reaches the activator.</summary>
    public HotbarPress Press(int index)
    {
        if (index < 0 || index >= SlotCount) return HotbarPress.OutOfRange;
        var slot = _content.Slot(index);
        if (slot.IsEmpty) return HotbarPress.Empty;
        if (!slot.Usable) return HotbarPress.Unusable;
        _activator?.Activate(index);
        return HotbarPress.Activated;
    }

    private sealed class EmptyContent : IHotbarContent
    {
        public static readonly EmptyContent Instance = new();

        public int Version => 0;

        public HotbarSlotView Slot(int index) => default;
    }
}

/// <summary>Where the slots go: a row, or more rows if the screen is too narrow for one at the smallest target size.</summary>
public readonly struct HotbarGrid
{
    /// <summary>Creates a grid.</summary>
    public HotbarGrid(int columns, int rows, double slotDp)
    {
        Columns = columns;
        Rows = rows;
        SlotDp = slotDp;
    }

    /// <summary>Slots per row.</summary>
    public int Columns { get; }

    /// <summary>Number of rows.</summary>
    public int Rows { get; }

    /// <summary>Side of one slot, in dp (at least the platform's minimum).</summary>
    public double SlotDp { get; }
}

/// <summary>The hotbar's layout arithmetic (client config: slot count, rows and size; T-211).</summary>
public static class HotbarLayout
{
    /// <summary>
    /// Lays <paramref name="slotCount"/> slots out in at least <paramref name="wantedRows"/> rows, each slot <paramref name="wantedSlotDp"/> wide. If a row does not
    /// fit in <paramref name="availableDp"/>, slots shrink, but never below <paramref name="minSlotDp"/>; if they still do not fit, there are more rows.
    /// </summary>
    public static HotbarGrid Compute(int slotCount, int wantedRows, double wantedSlotDp, double minSlotDp, double gapDp, double availableDp)
    {
        if (slotCount <= 0) return new HotbarGrid(0, 0, Math.Max(minSlotDp, wantedSlotDp));
        var rows = Math.Max(1, Math.Min(slotCount, wantedRows));
        var slot = Math.Max(minSlotDp, wantedSlotDp);
        while (true)
        {
            var columns = (slotCount + rows - 1) / rows;
            var needed = columns * slot + (columns - 1) * gapDp;
            if (needed <= availableDp + 1e-9) return new HotbarGrid(columns, rows, slot);

            // Shrink toward the minimum first.
            var fitting = (availableDp - (columns - 1) * gapDp) / columns;
            if (fitting >= minSlotDp - 1e-9) return new HotbarGrid(columns, rows, Math.Max(minSlotDp, fitting));
            if (rows >= slotCount) return new HotbarGrid(columns, rows, minSlotDp);
            rows++;
            slot = Math.Max(minSlotDp, wantedSlotDp);
        }
    }
}
