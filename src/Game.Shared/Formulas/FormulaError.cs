using System;
using System.Collections.Generic;

namespace Game.Shared.Formulas
{
    /// <summary>Stable error codes for <see cref="FormulaError.Code"/>; content validation reports them as-is.</summary>
    public static class FormulaErrorCodes
    {
        /// <summary>The formula is empty or only whitespace.</summary>
        public const string Empty = "formula.empty";

        /// <summary>The formula is longer than <see cref="FormulaLimits.MaxLength"/>.</summary>
        public const string TooLong = "formula.too_long";

        /// <summary>The expression tree is deeper than <see cref="FormulaLimits.MaxDepth"/>.</summary>
        public const string TooDeep = "formula.too_deep";

        /// <summary>The text is not a valid expression.</summary>
        public const string Syntax = "formula.syntax";

        /// <summary>An identifier is not a whitelisted variable.</summary>
        public const string UnknownVariable = "formula.unknown_variable";

        /// <summary>A called name is not a whitelisted function.</summary>
        public const string UnknownFunction = "formula.unknown_function";

        /// <summary>A function was called with the wrong number of arguments.</summary>
        public const string ArgumentCount = "formula.argument_count";
    }

    /// <summary>One problem in a formula's source text.</summary>
    /// <param name="Code">One of <see cref="FormulaErrorCodes"/>.</param>
    /// <param name="Position">0-based character index into the source.</param>
    /// <param name="Length">Number of characters the error covers (at least 1, except at the end of the text).</param>
    /// <param name="Message">Human-readable description, without the position.</param>
    public sealed record FormulaError(string Code, int Position, int Length, string Message)
    {
        /// <summary>1-based column, for messages.</summary>
        public int Column => Position + 1;

        /// <inheritdoc />
        public override string ToString() => "column " + Column + ": " + Message;
    }

    /// <summary>Outcome of <see cref="Formula.Parse(string?, FormulaSymbols?)"/>.</summary>
    public sealed class FormulaParseResult
    {
        internal FormulaParseResult(Formula? formula, IReadOnlyList<FormulaError> errors, IReadOnlyList<string> variables)
        {
            Formula = formula;
            Errors = errors;
            Variables = variables;
        }

        /// <summary>The compiled formula, or null when there are errors.</summary>
        public Formula? Formula { get; }

        /// <summary>Every error found; empty on success. Syntax errors stop parsing, so at most one is reported;
        /// unknown names and argument-count errors are all collected.</summary>
        public IReadOnlyList<FormulaError> Errors { get; }

        /// <summary>Every identifier used as a variable, whitelisted or not, in order of first appearance
        /// (up to the first syntax error).</summary>
        public IReadOnlyList<string> Variables { get; }

        /// <summary>True when the formula compiled.</summary>
        public bool Success => Formula != null;
    }

    /// <summary>Thrown by <see cref="Formula.Compile"/> when the text does not compile.</summary>
    public sealed class FormulaException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="source">Formula text.</param>
        /// <param name="errors">Errors found.</param>
        public FormulaException(string? source, IReadOnlyList<FormulaError> errors)
            : base("Invalid formula '" + source + "': " + string.Join("; ", errors))
        {
            Errors = errors;
        }

        /// <summary>Errors found.</summary>
        public IReadOnlyList<FormulaError> Errors { get; }
    }

    /// <summary>Hard limits that keep hostile or broken content from exhausting the stack or memory.</summary>
    public static class FormulaLimits
    {
        /// <summary>Maximum source length in characters.</summary>
        public const int MaxLength = 1024;

        /// <summary>Maximum expression-tree depth (also bounds parser and evaluator recursion).
        /// A left-associative chain counts one level per operator, so <c>a + b + c</c> is depth 3.</summary>
        public const int MaxDepth = 64;
    }
}
