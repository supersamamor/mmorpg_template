namespace Game.Server.World.Admin;

/// <summary>What an admin step saw or did on the tick: null when it was refused (with <see cref="Error"/>), else the values it read or changed.</summary>
/// <param name="Error">An "Admin errors" term when the step refused, else null.</param>
/// <param name="Values">What the step reports (ids and numbers only: it goes into the audit row's before or after).</param>
public sealed record AdminStepResult(string? Error, IReadOnlyDictionary<string, object?> Values)
{
    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    /// <summary>A step that went through.</summary>
    public static AdminStepResult Ok(IReadOnlyDictionary<string, object?> values) => new(null, values);

    /// <summary>A step that refused; nothing was changed.</summary>
    public static AdminStepResult Refused(string error) => new(error, None);
}

/// <summary>
/// One step of an admin command that runs on the tick of the instance holding <see cref="IIntent.Entity"/> (ARCHITECTURE §12.1: admin commands go through the game like player
/// intents). It is routed and validated like any intent: dropped (and the caller told <c>timeout</c>, see <see cref="Done"/>) when the entity is not in the world, routed to
/// <see cref="AdminSystem"/> otherwise, even when the entity is dead. <see cref="Run"/> is called on the tick thread with the tick's context and the entity: it may read and
/// change the entity and publish events, and must not do I/O.
/// </summary>
/// <param name="Entity">The entity the step is about.</param>
/// <param name="Action">The "Admin actions" term of the command (for the log).</param>
/// <param name="Run">The step.</param>
public sealed record AdminIntent(EntityId Entity, string Action, Func<TickContext, Entity, AdminStepResult> Run) : IDeadEntityIntent
{
    private readonly TaskCompletionSource<AdminStepResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the step has run on the tick (continuations never run on the tick thread).</summary>
    public Task<AdminStepResult> Done => _done.Task;

    internal void Complete(AdminStepResult result) => _done.TrySetResult(result);

    internal void Fail(Exception e) => _done.TrySetException(e);
}

/// <summary>
/// Runs the admin steps routed to this instance (T-804), in arrival order, each exactly once. A step that throws is logged and its caller told; the tick goes on. Its place in the
/// pipeline is last but one, before the AOI: a step sees what this tick's systems made, and what it changes is in this tick's view messages.
/// </summary>
public sealed class AdminSystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(AdminIntent)];

    /// <inheritdoc />
    public string Name => "admin";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        foreach (var intent in context.Intents)
        {
            if (intent is not AdminIntent step) continue;
            if (!context.Instance.TryGetEntity(step.Entity, out var entity))
            {
                step.Complete(AdminStepResult.Refused(Game.Shared.Vocabulary.AdminErrors.UnknownTarget));
                continue;
            }
            try
            {
                step.Complete(step.Run(context, entity));
            }
            catch (Exception e)
            {
                context.Log.Error($"[{context.Instance.MapId}] admin step '{step.Action}' for {step.Entity} failed at tick {context.TickNumber}", e);
                step.Fail(e);
            }
        }
    }
}
