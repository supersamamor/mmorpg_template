namespace Game.Shared.Definitions
{
    /// <summary>
    /// A content definition stored in a registry under a namespaced string id (VOCABULARY.md "Id namespaces").
    /// Each implementing record also exposes a <c>const string IdPrefix</c>; see <see cref="DefinitionIdPrefixes"/>
    /// for a type-keyed lookup (C# 9 has no static interface members).
    /// </summary>
    public interface IDefinition
    {
        /// <summary>Namespaced id, e.g. <c>item.red_potion</c>. Lowercase <c>a-z0-9_</c> after one prefix.</summary>
        string Id { get; }
    }
}
