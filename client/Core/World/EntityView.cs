using System;
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
    private double _attackAt = double.NaN;
    private double _hitAt = double.NaN;

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

    /// <summary>True while dead.</summary>
    public bool Dead { get; private set; }

    /// <summary>A ground item's quantity.</summary>
    public int Quantity { get; private set; }

    /// <summary>A ground item: this client may pick it up now.</summary>
    public bool Mine { get; private set; }

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
        _attackAt = _hitAt = double.NaN;
        Kind = record.Kind;
        DisplayKey = record.DisplayKey;
        Cell = record.Cell;
        Next = record.Next;
        Speed = record.Speed;
        Hp = record.Hp;
        Dead = record.Dead;
        Quantity = record.Quantity;
        Mine = record.Mine ?? false;
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
        if ((fields & ChangedFields.Hp) != 0) { Hp = change.Hp; LookDirty = true; }
        if ((fields & ChangedFields.Dead) != 0) { Dead = change.Dead; LookDirty = true; }
        if ((fields & ChangedFields.Mine) != 0) { Mine = change.Mine; LookDirty = true; }
        if ((fields & ChangedFields.Speed) != 0) Speed = change.Speed;

        var moved = (fields & (ChangedFields.Cell | ChangedFields.Next)) != 0;
        if (!moved) return;
        var wasWalking = Next.HasValue;
        if ((fields & ChangedFields.Cell) != 0) Cell = change.Cell;
        if ((fields & ChangedFields.Next) != 0) Next = change.Next;

        if (Next is { } next && Speed > 0) StartStep(next, now, delay, snapDistance, wasWalking);
        else Settle(now, delay, snapDistance, glideSpeed);
    }

    /// <summary>
    /// A <c>hits</c> entry named this entity as attacker, seen at view time <paramref name="now"/>. The attack animation waits until render time catches up with
    /// it, like every other change, so it lines up with the movement on screen.
    /// </summary>
    internal void QueueAttack(double now) => _attackAt = now;

    /// <summary>A <c>hits</c> entry named this entity as the target, seen at view time <paramref name="now"/>.</summary>
    internal void QueueHit(double now) => _hitAt = now;

    /// <summary>Walks the queue to render time and turns toward the facing. Allocates nothing.</summary>
    internal void Advance(double renderTime, double dt, double turnRate)
    {
        if (_attackAt <= renderTime)
        {
            AttackCount++;
            _attackAt = double.NaN;
        }
        if (_hitAt <= renderTime)
        {
            HitCount++;
            _hitAt = double.NaN;
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
