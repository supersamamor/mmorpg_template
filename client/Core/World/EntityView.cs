using System;
using System.Collections.Generic;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Client.Core.World;

/// <summary>
/// One entity as the client knows it (the last record and changes the server sent), plus where it is drawn now. Walking is a short
/// queue of timed steps in a fixed array, so advancing allocates nothing. Nothing here predicts: every step comes from the server.
/// </summary>
public sealed class EntityView
{
    private const int StepCapacity = 6;

    private readonly Step[] _steps = new Step[StepCapacity];
    private int _stepCount;
    private double _targetYaw;
    private double _deathAt = double.NaN;

    /// <summary>Entity id.</summary>
    public long Id { get; private set; }

    /// <summary>An <see cref="EntityKinds"/> term.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>The content id to look up in the bundle, or null.</summary>
    public string? DisplayKey { get; private set; }

    /// <summary>The last cell it reached.</summary>
    public GridCell Cell { get; private set; }

    /// <summary>The cell it is walking to, or null.</summary>
    public GridCell? Next { get; private set; }

    /// <summary>Cells per second while walking.</summary>
    public double Speed { get; private set; }

    /// <summary>HP fraction 0–1, or null for an entity without HP.</summary>
    public double? Hp { get; private set; }

    /// <summary>Counts every change to <see cref="Hp"/> (and each time the entity is placed), so a target frame redraws its bar only when the fraction moved.</summary>
    public int HpVersion { get; private set; }

    /// <summary>True while dead, as the server says. The picture waits for the killing blow: see <see cref="DeadShown"/>.</summary>
    public bool Dead { get; private set; }

    /// <summary>True once the death is on screen: the server said dead and the last pending hit on this entity has landed (at once for an entity that appears dead).</summary>
    public bool DeadShown { get; private set; }

    /// <summary>Set the moment <see cref="DeadShown"/> goes up from a death seen live; the presenter plays the death effect and clears it.</summary>
    public bool DeathFxPending { get; set; }

    /// <summary>A ground item's quantity.</summary>
    public int Quantity { get; private set; }

    /// <summary>A ground item: this client may pick it up now.</summary>
    public bool Mine { get; private set; }

    /// <summary>A player: the weapon type it holds (<c>unarmed</c> for none), as the server says; null for every other kind.</summary>
    public string? Weapon { get; private set; }

    /// <summary>A player: the item ids it wears that have a worn visual (look each one up in the bundle); empty for none and for other kinds.</summary>
    public IReadOnlyList<string> Worn { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// A player: its looks as ids (T-705), from the snapshot or appeared entry that introduced it; fixed until it leaves view (a change never carries it). Null for every other kind. An id
    /// the bundle does not have is kept as it is; <c>AppearanceCatalog</c> draws the default for it.
    /// </summary>
    public AppearanceRecord? Appearance { get; private set; }

    /// <summary>A player: the outfits it shows by slot (T-708), as the server says; empty for none and for other kinds. A new list means a change.</summary>
    public IReadOnlyList<ShownOutfit> Outfit { get; private set; } = Array.Empty<ShownOutfit>();

    /// <summary>Counts changes to <see cref="Weapon"/> and <see cref="Worn"/>, so a visual re-dresses only when they changed (T-403).</summary>
    public int LookVersion { get; private set; }

    /// <summary>
    /// Its statuses (T-501), in application order, each with the view-clock time it ends: the server sends the time left only when a status starts,
    /// stacks, is refreshed or ends, and the client counts down itself.
    /// </summary>
    public IReadOnlyList<ShownStatus> Statuses => _statuses;

    /// <summary>Moves whenever <see cref="Statuses"/> changed (the HUD and the markers redraw only then).</summary>
    public int StatusVersion { get; private set; }

    /// <summary>True while some status is a debuff (the marker's colour).</summary>
    public bool HasDebuff { get; private set; }

    /// <summary>Status ids that started on it since the presenter last looked (their vfx plays once); cleared by the presenter.</summary>
    public List<string> StartedStatuses { get; } = new();

    private readonly List<ShownStatus> _statuses = new();

    /// <summary>Drawn position on the X axis, in world units (cell <c>x</c> is centred at <c>x + 0.5</c>).</summary>
    public double X { get; private set; }

    /// <summary>Drawn position on the Z axis, in world units (cell <c>y</c> is centred at <c>y + 0.5</c>).</summary>
    public double Z { get; private set; }

    /// <summary>Drawn rotation around the up axis, in radians: 0 faces north, and it decreases clockwise seen from above.</summary>
    public double Yaw { get; private set; }

    /// <summary>The drawn position or rotation changed since the presenter last cleared this.</summary>
    public bool PoseDirty { get; set; }

    /// <summary>Kind, HP, dead or mine changed since the presenter last cleared this.</summary>
    public bool LookDirty { get; set; }

    /// <summary>Counts the attacks this entity made that have reached render time; a visual plays an attack clip when it goes up.</summary>
    public int AttackCount { get; private set; }

    /// <summary>Counts the hits this entity took that have reached render time; a visual plays a hit clip when it goes up.</summary>
    public int HitCount { get; private set; }

    /// <summary>The presenter's visual for this entity. Owned by the presenter.</summary>
    public object? Visual { get; set; }

    /// <summary>True while a step is queued or being walked.</summary>
    public bool IsWalking => _stepCount > 0;

    internal void Reset(EntityRecord record, double now, double delay)
    {
        Id = record.Id;
        _stepCount = 0;
        AttackCount = HitCount = 0;
        _deathAt = double.NaN;
        DeathFxPending = false;
        Kind = record.Kind;
        DisplayKey = record.DisplayKey;
        Cell = record.Cell;
        Next = record.Next;
        Speed = record.Speed;
        Hp = record.Hp;
        HpVersion++;
        Dead = DeadShown = record.Dead;
        Quantity = record.Quantity;
        Mine = record.Mine ?? false;
        Weapon = record.Weapon;
        Worn = record.Worn ?? Array.Empty<string>();
        Appearance = record.Appearance;
        Outfit = record.Outfit ?? Array.Empty<ShownOutfit>();
        LookVersion++;
        StartedStatuses.Clear();
        SetStatuses(record.Statuses, now, announce: false);
        X = Center(record.Cell.X);
        Z = Center(record.Cell.Y);
        Yaw = _targetYaw = YawOf(record.Facing, 0);
        PoseDirty = LookDirty = true;
        // A snapshot catches it somewhere inside the step: start moving right away instead of holding back the render delay.
        if (record.Next is { } next && record.Speed > 0) QueueStep(Center(record.Cell.X), Center(record.Cell.Y), next, record.Speed, now - delay);
    }

    internal void Apply(ChangeRecord change, double now, double delay, double snapDistance, double glideSpeed)
    {
        var fields = change.Fields;
        if ((fields & ChangedFields.Facing) != 0 && change.Facing is not null) _targetYaw = YawOf(change.Facing, _targetYaw);
        if ((fields & ChangedFields.DisplayKey) != 0) { DisplayKey = change.DisplayKey; LookDirty = true; }
        if ((fields & ChangedFields.Hp) != 0)
        {
            if (Hp != change.Hp) HpVersion++;
            Hp = change.Hp;
            LookDirty = true;
        }
        if ((fields & ChangedFields.Dead) != 0)
        {
            Dead = change.Dead;
            LookDirty = true;
            if (!change.Dead)
            {
                DeadShown = false;
                _deathAt = double.NaN;
            }
            else if (!DeadShown)
            {
                _deathAt = now + delay; // at render time, or later when a hit on it is still to land (WorldView)
            }
        }
        if ((fields & ChangedFields.Mine) != 0) { Mine = change.Mine; LookDirty = true; }
        if ((fields & ChangedFields.Look) != 0)
        {
            Weapon = change.Weapon;
            Worn = change.Worn ?? Array.Empty<string>();
            LookVersion++;
            LookDirty = true;
        }
        if ((fields & ChangedFields.Outfit) != 0)
        {
            Outfit = change.Outfit ?? Array.Empty<ShownOutfit>();
            LookDirty = true;
        }
        if ((fields & ChangedFields.Statuses) != 0) SetStatuses(change.Statuses, now, announce: true);
        if ((fields & ChangedFields.Speed) != 0) Speed = change.Speed;

        var moved = (fields & (ChangedFields.Cell | ChangedFields.Next)) != 0;
        if (!moved) return;
        var wasWalking = Next.HasValue;
        if ((fields & ChangedFields.Cell) != 0) Cell = change.Cell;
        if ((fields & ChangedFields.Next) != 0) Next = change.Next;

        if (Next is { } next && Speed > 0) StartStep(next, now, delay, snapDistance, wasWalking);
        else Settle(now, delay, snapDistance, glideSpeed);
    }

    /// <summary>The attack swing starts on screen (<c>CombatTimeline</c> decides when).</summary>
    private void SetStatuses(IReadOnlyList<StatusRecord>? records, double now, bool announce)
    {
        if (announce && records is not null)
        {
            foreach (var record in records)
            {
                var known = false;
                foreach (var shown in _statuses) known |= shown.Id == record.Id;
                if (!known) StartedStatuses.Add(record.Id);
            }
        }
        _statuses.Clear();
        HasDebuff = false;
        if (records is not null)
        {
            foreach (var record in records)
            {
                _statuses.Add(new ShownStatus(record.Id, record.Stacks, now + record.LeftMs / 1000.0, record.Debuff));
                HasDebuff |= record.Debuff;
            }
        }
        StatusVersion++;
        LookDirty = true;
    }

    internal void MarkAttack() => AttackCount++;

    /// <summary>A hit lands on this entity on screen.</summary>
    internal void MarkHit() => HitCount++;

    /// <summary>Delays the death picture until <paramref name="at"/> (view time) if that is later than already planned.</summary>
    internal void DelayDeath(double at)
    {
        if (Dead && !DeadShown && at > _deathAt) _deathAt = at;
    }

    /// <summary>Walks the queue to render time, shows a death whose time came, and turns toward the facing. Allocates nothing.</summary>
    internal void Advance(double now, double renderTime, double dt, double turnRate)
    {
        if (Dead && !DeadShown && now >= _deathAt)
        {
            DeadShown = true;
            DeathFxPending = true;
            LookDirty = true;
            _deathAt = double.NaN;
        }
        if (_stepCount > 0) AdvanceSteps(renderTime);
        if (Yaw != _targetYaw)
        {
            var difference = Math.IEEERemainder(_targetYaw - Yaw, 2 * Math.PI); // the shortest way round
            var turn = turnRate * dt;
            Yaw = Math.Abs(difference) <= turn ? _targetYaw : Yaw + Math.Sign(difference) * turn;
            PoseDirty = true;
        }
    }

    /// <summary>A new step from <see cref="Cell"/> to <paramref name="next"/> began on the server.</summary>
    private void StartStep(GridCell next, double now, double delay, double snapDistance, bool wasWalking)
    {
        var fromX = Center(Cell.X);
        var fromZ = Center(Cell.Y);
        var renderTime = now - delay;
        if (_stepCount > 0 && _steps[_stepCount - 1].ToX == fromX && _steps[_stepCount - 1].ToZ == fromZ)
        {
            // It continues from where the queue ends. Inside a walk it follows on with no gap; a late delta picks up at the render time, so
            // the walker waits at the cell rather than jumps. A step behind schedule is shortened, so a burst of deltas catches up.
            var lastEnd = _steps[_stepCount - 1].End;
            var start = Math.Max(lastEnd, wasWalking ? renderTime : now);
            QueueStep(fromX, fromZ, next, Speed, start, Math.Max(0, start - now));
            return;
        }

        var offX = fromX - X;
        var offZ = fromZ - Z;
        if (_stepCount == 0 && offX == 0 && offZ == 0)
        {
            // From rest: the render delay holds it back like everything else.
            QueueStep(fromX, fromZ, next, Speed, wasWalking ? renderTime : now);
            return;
        }

        // The path changed under it (a new order, or a correction): head for the new cell from where it is drawn, without a jump.
        if (Math.Sqrt(offX * offX + offZ * offZ) > snapDistance)
        {
            Snap(fromX, fromZ);
            QueueStep(fromX, fromZ, next, Speed, renderTime);
            return;
        }
        var fromDrawnX = X;
        var fromDrawnZ = Z;
        _stepCount = 0;
        QueueStep(fromDrawnX, fromDrawnZ, next, Speed, renderTime);
    }

    /// <summary>It stopped on <see cref="Cell"/>. Normally the queue already ends there; otherwise it was moved, so glide or snap.</summary>
    private void Settle(double now, double delay, double snapDistance, double glideSpeed)
    {
        var x = Center(Cell.X);
        var z = Center(Cell.Y);
        if (_stepCount > 0 && _steps[_stepCount - 1].ToX == x && _steps[_stepCount - 1].ToZ == z) return;
        if (_stepCount == 0 && X == x && Z == z) return;
        var offX = x - X;
        var offZ = z - Z;
        var distance = Math.Sqrt(offX * offX + offZ * offZ);
        if (distance > snapDistance)
        {
            Snap(x, z);
            return;
        }
        _stepCount = 0;
        var speed = Speed > 0 ? Speed : glideSpeed;
        _steps[0] = new Step(X, Z, x, z, now - delay, distance / speed);
        _stepCount = 1;
    }

    private void Snap(double x, double z)
    {
        _stepCount = 0;
        X = x;
        Z = z;
        PoseDirty = true;
    }

    /// <summary>Queues a step to a cell; a diagonal is √2 cells long, so it takes √2 as long. <paramref name="behind"/> is taken off its time (never below half).</summary>
    private void QueueStep(double fromX, double fromZ, GridCell to, double speed, double start, double behind = 0)
    {
        var toX = Center(to.X);
        var toZ = Center(to.Y);
        var dx = toX - fromX;
        var dz = toZ - fromZ;
        var duration = Math.Sqrt(dx * dx + dz * dz) / speed;
        duration = Math.Max(duration * 0.5, duration - behind);
        if (_stepCount == StepCapacity)
        {
            Array.Copy(_steps, 1, _steps, 0, StepCapacity - 1);
            _stepCount--;
        }
        _steps[_stepCount++] = new Step(fromX, fromZ, toX, toZ, start, duration);
    }

    private void AdvanceSteps(double renderTime)
    {
        var finished = 0;
        while (finished + 1 < _stepCount && _steps[finished + 1].Start <= renderTime) finished++;
        if (finished > 0)
        {
            Array.Copy(_steps, finished, _steps, 0, _stepCount - finished);
            _stepCount -= finished;
        }

        ref readonly var step = ref _steps[0];
        double x, z;
        if (renderTime <= step.Start)
        {
            x = step.FromX;
            z = step.FromZ;
        }
        else if (renderTime >= step.End)
        {
            x = step.ToX;
            z = step.ToZ;
            if (_stepCount == 1) _stepCount = 0;
        }
        else
        {
            var t = (renderTime - step.Start) / step.Duration;
            x = step.FromX + (step.ToX - step.FromX) * t;
            z = step.FromZ + (step.ToZ - step.FromZ) * t;
        }
        if (x != X || z != Z)
        {
            X = x;
            Z = z;
            PoseDirty = true;
        }
    }

    private static double Center(int cell) => cell + 0.5;

    /// <summary>The rotation of a <c>Directions</c> term; <paramref name="fallback"/> if the term is unknown.</summary>
    private static double YawOf(string facing, double fallback)
    {
        var index = GridDirection.IndexOf(facing);
        return index < 0 ? fallback : -index * Math.PI / 4;
    }

    private readonly struct Step
    {
        public Step(double fromX, double fromZ, double toX, double toZ, double start, double duration)
        {
            FromX = fromX;
            FromZ = fromZ;
            ToX = toX;
            ToZ = toZ;
            Start = start;
            Duration = duration;
        }

        public readonly double FromX, FromZ, ToX, ToZ, Start, Duration;

        public double End => Start + Duration;
    }
}

/// <summary>One status as the client shows it: id (name, icon and vfx come from the bundle), stacks, when it ends on the view clock, debuff or buff.</summary>
public readonly record struct ShownStatus(string Id, int Stacks, double EndsAt, bool Debuff)
{
    /// <summary>Whole seconds left at <paramref name="now"/>, rounded up, never below 0.</summary>
    public int SecondsLeft(double now) => (int)Math.Max(0, Math.Ceiling(EndsAt - now - 1e-9));
}
