using System;

namespace Game.Shared.Formulas
{
    // Tree-walking evaluator. Every node returns a finite double: any NaN or ±Infinity an operation
    // would produce becomes 0 (see Formula's remarks), so results are deterministic and never poison callers.

    internal abstract class Node
    {
        protected Node(int depth)
        {
            Depth = depth;
        }

        /// <summary>Height of this subtree; leaves are 1.</summary>
        public int Depth { get; }

        public abstract double Evaluate(IFormulaContext context);

        protected static int DepthOf(Node a) => a.Depth + 1;

        protected static int DepthOf(Node a, Node b) => Math.Max(a.Depth, b.Depth) + 1;

        public static double Finite(double value) => double.IsFinite(value) ? value : 0;

        public static double Bool(bool value) => value ? 1 : 0;

        public static bool Truthy(double value) => value != 0;
    }

    internal sealed class ConstantNode : Node
    {
        public ConstantNode(double value)
            : base(1)
        {
            Value = Finite(value);
        }

        public double Value { get; }

        public override double Evaluate(IFormulaContext context) => Value;
    }

    internal sealed class VariableNode : Node
    {
        private readonly int _slot;

        public VariableNode(int slot)
            : base(1)
        {
            _slot = slot;
        }

        public override double Evaluate(IFormulaContext context) => Finite(context.GetVariable(_slot));
    }

    internal enum UnaryOp
    {
        Negate,
        Not,
    }

    internal sealed class UnaryNode : Node
    {
        private readonly UnaryOp _op;
        private readonly Node _operand;

        public UnaryNode(UnaryOp op, Node operand)
            : base(DepthOf(operand))
        {
            _op = op;
            _operand = operand;
        }

        public bool IsFoldable => _operand is ConstantNode;

        public override double Evaluate(IFormulaContext context)
        {
            var value = _operand.Evaluate(context);
            return _op == UnaryOp.Negate ? -value : Bool(!Truthy(value));
        }
    }

    internal enum BinaryOp
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Equal,
        NotEqual,
        And,
        Or,
    }

    internal sealed class BinaryNode : Node
    {
        private readonly BinaryOp _op;
        private readonly Node _left;
        private readonly Node _right;

        public BinaryNode(BinaryOp op, Node left, Node right)
            : base(DepthOf(left, right))
        {
            _op = op;
            _left = left;
            _right = right;
        }

        public bool IsFoldable => _left is ConstantNode && _right is ConstantNode;

        public override double Evaluate(IFormulaContext context)
        {
            var a = _left.Evaluate(context);
            switch (_op)
            {
                case BinaryOp.And: return Bool(Truthy(a) && Truthy(_right.Evaluate(context)));
                case BinaryOp.Or: return Bool(Truthy(a) || Truthy(_right.Evaluate(context)));
            }

            var b = _right.Evaluate(context);
            switch (_op)
            {
                case BinaryOp.Add: return Finite(a + b);
                case BinaryOp.Subtract: return Finite(a - b);
                case BinaryOp.Multiply: return Finite(a * b);
                case BinaryOp.Divide: return b == 0 ? 0 : Finite(a / b);
                case BinaryOp.Modulo: return b == 0 ? 0 : Finite(a % b);
                case BinaryOp.Less: return Bool(a < b);
                case BinaryOp.LessOrEqual: return Bool(a <= b);
                case BinaryOp.Greater: return Bool(a > b);
                case BinaryOp.GreaterOrEqual: return Bool(a >= b);
                case BinaryOp.Equal: return Bool(a == b);
                case BinaryOp.NotEqual: return Bool(a != b);
                default: return 0;
            }
        }
    }

    internal sealed class ConditionalNode : Node
    {
        private readonly Node _condition;
        private readonly Node _whenTrue;
        private readonly Node _whenFalse;

        public ConditionalNode(Node condition, Node whenTrue, Node whenFalse)
            : base(Math.Max(condition.Depth, Math.Max(whenTrue.Depth, whenFalse.Depth)) + 1)
        {
            _condition = condition;
            _whenTrue = whenTrue;
            _whenFalse = whenFalse;
        }

        public override double Evaluate(IFormulaContext context) =>
            Truthy(_condition.Evaluate(context)) ? _whenTrue.Evaluate(context) : _whenFalse.Evaluate(context);
    }

    internal enum FunctionKind
    {
        Min,
        Max,
        Floor,
        Ceil,
        Round,
        Clamp,
        Pow,
        Sqrt,
    }

    internal sealed class CallNode : Node
    {
        private readonly FunctionKind _kind;
        private readonly Node[] _args;

        public CallNode(FunctionKind kind, Node[] args)
            : base(MaxDepth(args) + 1)
        {
            _kind = kind;
            _args = args;
        }

        public bool IsFoldable
        {
            get
            {
                foreach (var arg in _args)
                {
                    if (!(arg is ConstantNode)) return false;
                }
                return true;
            }
        }

        public override double Evaluate(IFormulaContext context)
        {
            var args = _args;
            switch (_kind)
            {
                case FunctionKind.Min:
                {
                    var result = args[0].Evaluate(context);
                    for (var i = 1; i < args.Length; i++) result = Math.Min(result, args[i].Evaluate(context));
                    return result;
                }
                case FunctionKind.Max:
                {
                    var result = args[0].Evaluate(context);
                    for (var i = 1; i < args.Length; i++) result = Math.Max(result, args[i].Evaluate(context));
                    return result;
                }
                case FunctionKind.Floor: return Math.Floor(args[0].Evaluate(context));
                case FunctionKind.Ceil: return Math.Ceiling(args[0].Evaluate(context));
                case FunctionKind.Round: return Math.Round(args[0].Evaluate(context), MidpointRounding.AwayFromZero);
                case FunctionKind.Clamp:
                {
                    // Not Math.Clamp: it throws when min > max. Here the lower bound wins.
                    var value = args[0].Evaluate(context);
                    var min = args[1].Evaluate(context);
                    var max = args[2].Evaluate(context);
                    return Math.Max(min, Math.Min(value, max));
                }
                case FunctionKind.Pow: return Finite(Math.Pow(args[0].Evaluate(context), args[1].Evaluate(context)));
                case FunctionKind.Sqrt:
                {
                    var value = args[0].Evaluate(context);
                    return value < 0 ? 0 : Math.Sqrt(value);
                }
                default: return 0;
            }
        }

        private static int MaxDepth(Node[] args)
        {
            var depth = 0;
            foreach (var arg in args) depth = Math.Max(depth, arg.Depth);
            return depth;
        }
    }
}
