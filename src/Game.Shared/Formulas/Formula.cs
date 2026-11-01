using System;
using Game.Shared.Vocabulary;

namespace Game.Shared.Formulas
{
    /// <summary>
    /// A formula compiled from a content expression string (ARCHITECTURE §6.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Grammar</b>, loosest binding first:</para>
    /// <code>
    /// expression     := conditional
    /// conditional    := or ( '?' conditional ':' conditional )?        right-associative
    /// or             := and ( '||' and )*
    /// and            := comparison ( '&amp;&amp;' comparison )*
    /// comparison     := additive ( ('&lt;' | '&lt;=' | '&gt;' | '&gt;=' | '==' | '!=') additive )?   not chainable
    /// additive       := multiplicative ( ('+' | '-') multiplicative )*
    /// multiplicative := unary ( ('*' | '/' | '%') unary )*
    /// unary          := ('-' | '+' | '!') unary | primary
    /// primary        := number | variable | function '(' arguments? ')' | '(' expression ')'
    /// number         := digits ( '.' digits )? | '.' digits           no exponent, no sign (use unary '-')
    /// </code>
    /// <para>Variables and functions must be whitelisted (VOCABULARY.md; <see cref="FormulaSymbols"/>,
    /// <see cref="FormulaFunctions"/>). Functions: <c>min(a, b, ...)</c>, <c>max(a, b, ...)</c>, <c>floor(x)</c>,
    /// <c>ceil(x)</c>, <c>round(x)</c> (halves away from zero), <c>clamp(x, lo, hi)</c> (if <c>lo &gt; hi</c> the result
    /// is <c>lo</c>), <c>pow(x, y)</c>, <c>sqrt(x)</c>. There is no random function: a chance formula returns a
    /// probability and the caller rolls with an injected <c>IRandom</c>.</para>
    /// <para><b>Truth values.</b> Comparisons, <c>!</c>, <c>&amp;&amp;</c> and <c>||</c> return 1 or 0. Any non-zero value
    /// is true. <c>&amp;&amp;</c>, <c>||</c> and <c>?:</c> evaluate only the operands they need.</para>
    /// <para><b>Undefined arithmetic is defined as 0.</b> Division or modulo by zero returns 0; <c>sqrt</c> of a negative
    /// number returns 0; any other operation (or variable value) that would be NaN or ±Infinity becomes 0. So every
    /// result is a finite number and evaluation never throws.</para>
    /// <para><b>Limits.</b> At most <see cref="FormulaLimits.MaxLength"/> characters and an expression tree at most
    /// <see cref="FormulaLimits.MaxDepth"/> deep, which bounds parser and evaluator recursion.</para>
    /// <para><b>Cost.</b> Constant subtrees are folded at compile time, so <c>"17"</c> or <c>"60 * 1000"</c> compile to a
    /// single constant (<see cref="IsConstant"/>). Evaluation walks a small tree of sealed nodes and allocates nothing.</para>
    /// </remarks>
    public sealed class Formula : IFormula
    {
        private readonly Node _root;

        internal Formula(string source, Node root, FormulaSymbols symbols, VocabularySet variables)
        {
            Source = source;
            _root = root;
            Symbols = symbols;
            Variables = variables;
        }

        /// <summary>The source text.</summary>
        public string Source { get; }

        /// <summary>The symbol table variables were resolved against; evaluate with a context built on the same one.</summary>
        public FormulaSymbols Symbols { get; }

        /// <inheritdoc />
        public VocabularySet Variables { get; }

        /// <summary>True when the formula folded to a constant; it then ignores the context.</summary>
        public bool IsConstant => _root is ConstantNode;

        /// <summary>Gets the value of a constant formula.</summary>
        /// <param name="value">The constant, or 0.</param>
        /// <returns>True if <see cref="IsConstant"/>.</returns>
        public bool TryGetConstant(out double value)
        {
            if (_root is ConstantNode constant)
            {
                value = constant.Value;
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>Parses and compiles <paramref name="text"/>, checking every variable and function against the whitelist.</summary>
        /// <param name="text">Expression source.</param>
        /// <param name="symbols">Allowed variables; <see cref="FormulaSymbols.Default"/> when null.</param>
        /// <returns>The compiled formula, or the errors with character positions.</returns>
        public static FormulaParseResult Parse(string? text, FormulaSymbols? symbols = null) =>
            FormulaParser.Parse(text, symbols ?? FormulaSymbols.Default);

        /// <summary>Like <see cref="Parse"/> but throws <see cref="FormulaException"/> on errors. For content that has already been validated.</summary>
        /// <param name="text">Expression source.</param>
        /// <param name="symbols">Allowed variables; <see cref="FormulaSymbols.Default"/> when null.</param>
        public static Formula Compile(string? text, FormulaSymbols? symbols = null)
        {
            var result = Parse(text, symbols);
            return result.Formula ?? throw new FormulaException(text, result.Errors);
        }

        /// <summary>A formula that always returns <paramref name="value"/> (non-finite values become 0).</summary>
        /// <param name="value">The constant.</param>
        public static Formula Constant(double value)
        {
            var node = new ConstantNode(value);
            return new Formula(node.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), node,
                FormulaSymbols.Default, new VocabularySet());
        }

        /// <inheritdoc />
        public double Evaluate(IFormulaContext context) => _root.Evaluate(context ?? throw new ArgumentNullException(nameof(context)));

        /// <inheritdoc />
        public override string ToString() => Source;
    }
}
