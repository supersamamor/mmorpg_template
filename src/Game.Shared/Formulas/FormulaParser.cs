using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Shared.Vocabulary;

namespace Game.Shared.Formulas
{
    /// <summary>
    /// Hand-written recursive-descent parser (grammar in <see cref="Formula"/>'s remarks).
    /// Resolves variables to slots and folds constant subtrees while it builds the tree.
    /// </summary>
    internal sealed class FormulaParser
    {
        private enum TokenKind
        {
            End,
            Number,
            Identifier,
            Plus,
            Minus,
            Star,
            Slash,
            Percent,
            Less,
            LessOrEqual,
            Greater,
            GreaterOrEqual,
            EqualEqual,
            NotEqual,
            Bang,
            AndAnd,
            OrOr,
            Question,
            Colon,
            Comma,
            LeftParen,
            RightParen,
        }

        private readonly string _text;
        private readonly FormulaSymbols _symbols;
        private readonly List<FormulaError> _errors = new List<FormulaError>();
        private readonly List<string> _allVariables = new List<string>();
        private readonly List<string> _knownVariables = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        private int _pos;
        private int _nesting;
        private TokenKind _kind;
        private int _start;
        private double _number;

        private FormulaParser(string text, FormulaSymbols symbols)
        {
            _text = text;
            _symbols = symbols;
        }

        public static FormulaParseResult Parse(string? text, FormulaSymbols symbols)
        {
            if (text == null || text.Trim().Length == 0)
                return Failed(new FormulaError(FormulaErrorCodes.Empty, 0, 0, "Formula is empty."));
            if (text.Length > FormulaLimits.MaxLength)
            {
                return Failed(new FormulaError(FormulaErrorCodes.TooLong, FormulaLimits.MaxLength, text.Length - FormulaLimits.MaxLength,
                    "Formula is " + text.Length + " characters long; the limit is " + FormulaLimits.MaxLength + "."));
            }

            var parser = new FormulaParser(text, symbols);
            Node? root = null;
            try
            {
                parser.Next();
                root = parser.ParseExpression();
                if (parser._kind != TokenKind.End) parser.Fail(FormulaErrorCodes.Syntax, "Unexpected " + parser.Describe() + ".");
            }
            catch (SyntaxFailure failure)
            {
                parser._errors.Add(failure.Error);
            }

            parser._errors.Sort((a, b) => a.Position.CompareTo(b.Position));
            var formula = parser._errors.Count == 0 && root != null
                ? new Formula(text, root, symbols, new VocabularySet(parser._knownVariables.ToArray()))
                : null;
            return new FormulaParseResult(formula, parser._errors.ToArray(), parser._allVariables.ToArray());
        }

        private static FormulaParseResult Failed(FormulaError error) =>
            new FormulaParseResult(null, new[] { error }, Array.Empty<string>());

        // expression := conditional
        private Node ParseExpression() => ParseConditional();

        // conditional := or ( '?' conditional ':' conditional )?      (right-associative)
        private Node ParseConditional()
        {
            var condition = ParseOr();
            if (_kind != TokenKind.Question) return condition;

            var position = _start;
            Next();
            Enter(position);
            var whenTrue = ParseConditional();
            Expect(TokenKind.Colon, "':' to complete '?'");
            var whenFalse = ParseConditional();
            Leave();

            if (condition is ConstantNode constant) return Node.Truthy(constant.Value) ? whenTrue : whenFalse;
            return Checked(new ConditionalNode(condition, whenTrue, whenFalse), position);
        }

        // or := and ( '||' and )*
        private Node ParseOr()
        {
            var left = ParseAnd();
            while (_kind == TokenKind.OrOr)
            {
                var position = _start;
                Next();
                left = MakeBinary(BinaryOp.Or, left, ParseAnd(), position);
            }
            return left;
        }

        // and := comparison ( '&&' comparison )*
        private Node ParseAnd()
        {
            var left = ParseComparison();
            while (_kind == TokenKind.AndAnd)
            {
                var position = _start;
                Next();
                left = MakeBinary(BinaryOp.And, left, ParseComparison(), position);
            }
            return left;
        }

        // comparison := additive ( ('<' | '<=' | '>' | '>=' | '==' | '!=') additive )?   (not chainable)
        private Node ParseComparison()
        {
            var left = ParseAdditive();
            if (!TryComparison(out var op)) return left;

            var position = _start;
            Next();
            var node = MakeBinary(op, left, ParseAdditive(), position);
            if (TryComparison(out _)) Fail(FormulaErrorCodes.Syntax, "Comparisons cannot be chained; combine them with '&&'.");
            return node;
        }

        // additive := multiplicative ( ('+' | '-') multiplicative )*
        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (_kind == TokenKind.Plus || _kind == TokenKind.Minus)
            {
                var op = _kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Subtract;
                var position = _start;
                Next();
                left = MakeBinary(op, left, ParseMultiplicative(), position);
            }
            return left;
        }

        // multiplicative := unary ( ('*' | '/' | '%') unary )*
        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (_kind == TokenKind.Star || _kind == TokenKind.Slash || _kind == TokenKind.Percent)
            {
                var op = _kind == TokenKind.Star ? BinaryOp.Multiply : _kind == TokenKind.Slash ? BinaryOp.Divide : BinaryOp.Modulo;
                var position = _start;
                Next();
                left = MakeBinary(op, left, ParseUnary(), position);
            }
            return left;
        }

        // unary := ('-' | '+' | '!') unary | primary
        private Node ParseUnary()
        {
            if (_kind != TokenKind.Minus && _kind != TokenKind.Plus && _kind != TokenKind.Bang) return ParsePrimary();

            var kind = _kind;
            var position = _start;
            Next();
            Enter(position);
            var operand = ParseUnary();
            Leave();
            if (kind == TokenKind.Plus) return operand;

            var node = new UnaryNode(kind == TokenKind.Minus ? UnaryOp.Negate : UnaryOp.Not, operand);
            return node.IsFoldable ? Fold(node) : Checked(node, position);
        }

        // primary := number | identifier | identifier '(' arguments? ')' | '(' expression ')'
        private Node ParsePrimary()
        {
            var position = _start;
            switch (_kind)
            {
                case TokenKind.Number:
                {
                    var value = _number;
                    Next();
                    return new ConstantNode(value);
                }
                case TokenKind.Identifier:
                {
                    var name = _text.Substring(_start, _pos - _start);
                    Next();
                    return _kind == TokenKind.LeftParen ? ParseCall(name, position) : ResolveVariable(name, position);
                }
                case TokenKind.LeftParen:
                {
                    Next();
                    Enter(position);
                    var inner = ParseExpression();
                    Expect(TokenKind.RightParen, "')'");
                    Leave();
                    return inner;
                }
                default:
                    Fail(FormulaErrorCodes.Syntax, "Expected a number, variable, function call or '(' but found " + Describe() + ".");
                    return null!;
            }
        }

        // arguments := expression ( ',' expression )*
        private Node ParseCall(string name, int position)
        {
            Next(); // '('
            Enter(position);
            var args = new List<Node>();
            if (_kind != TokenKind.RightParen)
            {
                args.Add(ParseExpression());
                while (_kind == TokenKind.Comma)
                {
                    Next();
                    args.Add(ParseExpression());
                }
            }
            Expect(TokenKind.RightParen, "',' or ')' in the call to '" + name + "'");
            Leave();

            if (!TryGetFunction(name, out var kind, out var minArgs, out var maxArgs))
            {
                var hint = _symbols.TryGetSlot(name, out _) ? " ('" + name + "' is a variable, not a function)" : "";
                AddError(FormulaErrorCodes.UnknownFunction, position, name.Length,
                    "Unknown function '" + name + "'" + hint + ". Allowed: " + string.Join(", ", FormulaFunctions.All) + ".");
                return new ConstantNode(0);
            }
            if (args.Count < minArgs || args.Count > maxArgs)
            {
                var expected = minArgs == maxArgs ? minArgs.ToString(CultureInfo.InvariantCulture)
                    : maxArgs == int.MaxValue ? "at least " + minArgs
                    : minArgs + " to " + maxArgs;
                AddError(FormulaErrorCodes.ArgumentCount, position, name.Length,
                    "'" + name + "' takes " + expected + " argument(s) but got " + args.Count + ".");
                return new ConstantNode(0);
            }

            var node = new CallNode(kind, args.ToArray());
            return node.IsFoldable ? Fold(node) : Checked(node, position);
        }

        private Node ResolveVariable(string name, int position)
        {
            if (_seen.Add(name)) _allVariables.Add(name);
            if (_symbols.TryGetSlot(name, out var slot))
            {
                if (!_knownVariables.Contains(name)) _knownVariables.Add(name);
                return new VariableNode(slot);
            }

            var hint = TryGetFunction(name, out _, out _, out _) ? " ('" + name + "' is a function; call it with arguments)" : "";
            AddError(FormulaErrorCodes.UnknownVariable, position, name.Length,
                "Unknown variable '" + name + "'" + hint + ". See VOCABULARY.md \"Formula variables (whitelist)\".");
            return new ConstantNode(0);
        }

        /// <summary>Maps a whitelisted function name (<see cref="FormulaFunctions"/>) to its implementation and arity.</summary>
        internal static bool TryGetFunction(string name, out FunctionKind kind, out int minArgs, out int maxArgs)
        {
            switch (name)
            {
                case FormulaFunctions.Min: kind = FunctionKind.Min; minArgs = 2; maxArgs = int.MaxValue; return true;
                case FormulaFunctions.Max: kind = FunctionKind.Max; minArgs = 2; maxArgs = int.MaxValue; return true;
                case FormulaFunctions.Floor: kind = FunctionKind.Floor; minArgs = maxArgs = 1; return true;
                case FormulaFunctions.Ceil: kind = FunctionKind.Ceil; minArgs = maxArgs = 1; return true;
                case FormulaFunctions.Round: kind = FunctionKind.Round; minArgs = maxArgs = 1; return true;
                case FormulaFunctions.Clamp: kind = FunctionKind.Clamp; minArgs = maxArgs = 3; return true;
                case FormulaFunctions.Pow: kind = FunctionKind.Pow; minArgs = maxArgs = 2; return true;
                case FormulaFunctions.Sqrt: kind = FunctionKind.Sqrt; minArgs = maxArgs = 1; return true;
                default: kind = default; minArgs = maxArgs = 0; return false;
            }
        }

        private Node MakeBinary(BinaryOp op, Node left, Node right, int position)
        {
            var node = new BinaryNode(op, left, right);
            return node.IsFoldable ? Fold(node) : Checked(node, position);
        }

        private static Node Fold(Node node) => new ConstantNode(node.Evaluate(FormulaContext.None));

        private Node Checked(Node node, int position)
        {
            if (node.Depth > FormulaLimits.MaxDepth) FailAt(FormulaErrorCodes.TooDeep, position, 1, TooDeepMessage);
            return node;
        }

        private void Enter(int position)
        {
            if (++_nesting > FormulaLimits.MaxDepth) FailAt(FormulaErrorCodes.TooDeep, position, 1, TooDeepMessage);
        }

        private void Leave() => _nesting--;

        private static string TooDeepMessage => "Formula is nested more than " + FormulaLimits.MaxDepth + " levels deep.";

        private bool TryComparison(out BinaryOp op)
        {
            switch (_kind)
            {
                case TokenKind.Less: op = BinaryOp.Less; return true;
                case TokenKind.LessOrEqual: op = BinaryOp.LessOrEqual; return true;
                case TokenKind.Greater: op = BinaryOp.Greater; return true;
                case TokenKind.GreaterOrEqual: op = BinaryOp.GreaterOrEqual; return true;
                case TokenKind.EqualEqual: op = BinaryOp.Equal; return true;
                case TokenKind.NotEqual: op = BinaryOp.NotEqual; return true;
                default: op = default; return false;
            }
        }

        private void Expect(TokenKind kind, string what)
        {
            if (_kind != kind) Fail(FormulaErrorCodes.Syntax, "Expected " + what + " but found " + Describe() + ".");
            Next();
        }

        private void AddError(string code, int position, int length, string message) =>
            _errors.Add(new FormulaError(code, position, length, message));

        private void Fail(string code, string message) => FailAt(code, _start, Math.Max(_pos - _start, _kind == TokenKind.End ? 0 : 1), message);

        private static void FailAt(string code, int position, int length, string message) =>
            throw new SyntaxFailure(new FormulaError(code, position, length, message));

        private string Describe() =>
            _kind == TokenKind.End ? "the end of the formula" : "'" + _text.Substring(_start, _pos - _start) + "'";

        // Lexer: reads the next token into _kind/_start/_pos/_number.
        private void Next()
        {
            var text = _text;
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
            _start = _pos;
            if (_pos >= text.Length)
            {
                _kind = TokenKind.End;
                return;
            }

            var c = text[_pos];
            if (IsDigit(c) || (c == '.' && _pos + 1 < text.Length && IsDigit(text[_pos + 1])))
            {
                while (_pos < text.Length && IsDigit(text[_pos])) _pos++;
                if (_pos < text.Length && text[_pos] == '.')
                {
                    _pos++;
                    if (_pos >= text.Length || !IsDigit(text[_pos]))
                        FailAt(FormulaErrorCodes.Syntax, _start, _pos - _start, "A decimal point must be followed by digits.");
                    while (_pos < text.Length && IsDigit(text[_pos])) _pos++;
                }
                if (_pos < text.Length && IsIdentifierChar(text[_pos]))
                    FailAt(FormulaErrorCodes.Syntax, _pos, 1, "Unexpected '" + text[_pos] + "' after a number.");
                _number = double.Parse(text.AsSpan(_start, _pos - _start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                _kind = TokenKind.Number;
                return;
            }
            if (IsIdentifierStart(c))
            {
                while (_pos < text.Length && IsIdentifierChar(text[_pos])) _pos++;
                _kind = TokenKind.Identifier;
                return;
            }

            _pos++;
            var next = _pos < text.Length ? text[_pos] : '\0';
            switch (c)
            {
                case '+': _kind = TokenKind.Plus; return;
                case '-': _kind = TokenKind.Minus; return;
                case '*': _kind = TokenKind.Star; return;
                case '/': _kind = TokenKind.Slash; return;
                case '%': _kind = TokenKind.Percent; return;
                case '?': _kind = TokenKind.Question; return;
                case ':': _kind = TokenKind.Colon; return;
                case ',': _kind = TokenKind.Comma; return;
                case '(': _kind = TokenKind.LeftParen; return;
                case ')': _kind = TokenKind.RightParen; return;
                case '<': _kind = Pair(next, '=', TokenKind.LessOrEqual, TokenKind.Less); return;
                case '>': _kind = Pair(next, '=', TokenKind.GreaterOrEqual, TokenKind.Greater); return;
                case '!': _kind = Pair(next, '=', TokenKind.NotEqual, TokenKind.Bang); return;
                case '=' when next == '=': _pos++; _kind = TokenKind.EqualEqual; return;
                case '&' when next == '&': _pos++; _kind = TokenKind.AndAnd; return;
                case '|' when next == '|': _pos++; _kind = TokenKind.OrOr; return;
                case '=':
                    FailAt(FormulaErrorCodes.Syntax, _start, 1, "Unexpected '='; use '==' to compare.");
                    return;
                default:
                    FailAt(FormulaErrorCodes.Syntax, _start, 1, "Unexpected character '" + c + "'.");
                    return;
            }
        }

        private TokenKind Pair(char next, char second, TokenKind two, TokenKind one)
        {
            if (next != second) return one;
            _pos++;
            return two;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsIdentifierStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

        private static bool IsIdentifierChar(char c) => IsIdentifierStart(c) || IsDigit(c);

        private sealed class SyntaxFailure : Exception
        {
            public SyntaxFailure(FormulaError error)
            {
                Error = error;
            }

            public FormulaError Error { get; }
        }
    }
}
