namespace Game.Server.World;

/// <summary>
/// One step of an instance's tick (movement, AI, combat, ...). Each instance gets its own system objects, built from a
/// <see cref="SystemPipeline"/>, and calls them on its own thread in registration order, so a system may keep
/// per-instance state in fields without locking. Systems never call each other: they read and change entities, react
/// to the intents routed to them and publish events (hard rule 7).
/// </summary>
public interface ITickSystem
{
    /// <summary>Name used in logs (e.g. <c>movement</c>).</summary>
    string Name { get; }

    /// <summary>
    /// The intent types this system owns. An intent is routed to the system that lists its exact runtime type; each
    /// type has at most one owner per pipeline. Intents of a type nobody owns are dropped and counted.
    /// </summary>
    IReadOnlyCollection<Type> IntentTypes { get; }

    /// <summary>Runs this system for one tick. <see cref="TickContext.Intents"/> holds the intents routed to it.</summary>
    void Tick(TickContext context);
}

/// <summary>
/// The ordered list of systems every instance runs. Holds factories, not systems, so each instance gets fresh
/// objects. Register in the order the tick should run them.
/// </summary>
public sealed class SystemPipeline
{
    private readonly List<Func<ITickSystem>> _factories = new();

    /// <summary>Appends a system factory; returns this pipeline for chaining.</summary>
    public SystemPipeline Add(Func<ITickSystem> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(factory);
        return this;
    }

    /// <summary>Number of registered systems.</summary>
    public int Count => _factories.Count;

    /// <summary>
    /// Builds one instance's systems in order. Throws <see cref="InvalidOperationException"/> when two systems claim the
    /// same intent type or two systems share a name.
    /// </summary>
    public IReadOnlyList<ITickSystem> Build()
    {
        var systems = _factories.Select(f => f() ?? throw new InvalidOperationException("A system factory returned null.")).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var owners = new Dictionary<Type, string>();
        foreach (var system in systems)
        {
            if (!names.Add(system.Name)) throw new InvalidOperationException($"Two systems are named '{system.Name}'.");
            foreach (var type in system.IntentTypes)
            {
                if (!typeof(IIntent).IsAssignableFrom(type))
                    throw new InvalidOperationException($"System '{system.Name}' lists {type.Name}, which is not an IIntent.");
                if (!owners.TryAdd(type, system.Name))
                    throw new InvalidOperationException($"Intent {type.Name} is claimed by both '{owners[type]}' and '{system.Name}'.");
            }
        }
        return systems;
    }
}
