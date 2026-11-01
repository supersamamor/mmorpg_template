using Game.Shared.Definitions;

namespace Game.Content.Registry;

/// <summary>Non-generic view of a <see cref="Registry{T}"/>, for code that treats all registries uniformly (e.g. cross-reference checks).</summary>
public interface IRegistry
{
    /// <summary>The definition record type stored in this registry.</summary>
    Type DefinitionType { get; }

    /// <summary>Number of definitions.</summary>
    int Count { get; }

    /// <summary>True if a definition with this id exists.</summary>
    bool Contains(string id);

    /// <summary>Every definition, ordered by id (ordinal).</summary>
    IEnumerable<IDefinition> Definitions { get; }
}
