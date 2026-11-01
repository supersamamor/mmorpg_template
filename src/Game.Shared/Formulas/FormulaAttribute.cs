using System;

namespace Game.Shared.Formulas
{
    /// <summary>
    /// Marks a definition property that holds a formula string (or, for a dictionary, whose values are formula strings).
    /// Nothing reads it at runtime. A test reflects over the definitions and fails when a marked property is not
    /// returned by <see cref="FormulaSources"/>, so a new formula field cannot slip past validation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
    public sealed class FormulaAttribute : Attribute
    {
    }
}
