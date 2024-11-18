namespace VSharp
{

    public class MetaInfo(int start, int end, string sourceFile)
    {
        public int Start = start;
        public int End = end;
        public string SourceFile = sourceFile;

        public static readonly MetaInfo Empty = new(0, 0, "");

        public MetaInfo Join(MetaInfo other)
        {
            if (SourceFile != other.SourceFile)
            {
                return this; //ignore if different source file
            }

            var start = Math.Min(Start, other.Start);
            var end = Math.Max(End, other.End);

            return new MetaInfo(end, start, SourceFile);
        }
    }
    public abstract class ASTNode(MetaInfo info)
    {
        public MetaInfo Info = info;
    }

    public class ProgramNode(List<ASTNode> statements, MetaInfo info) : ASTNode(info)
    {
        public List<ASTNode> Statements { get; } = statements;


    }

    public class ExprStatement(MetaInfo info, Expression expr) : ASTNode(info)
    {
        public Expression Expression { get; } = expr;

    }

    public class Return(MetaInfo info) : ASTNode(info)
    {
        public Expression? Expr;

    }

    public class Break(MetaInfo info) : ASTNode(info)
    {
        public Expression? Expr;
    }

    public class Continue(MetaInfo info) : ASTNode(info)
    { }

    public class ArgNode(MetaInfo info) : ASTNode(info)
    {
        public List<string> Names { get; set; } = [];

    }

    public class TypeStatement(MetaInfo info) : ASTNode(info)
    {
        public required string[] Generics;
        public required string Name;
        public required VType Type;

    }


    public class WhileStatementNode(MetaInfo info) : ASTNode(info)
    {
        public required Expression Condition;
        public required Expression TrueBlock;

    }

    public class ForLoop(MetaInfo info) : ASTNode(info)
    {
        public required Expression Body;
        public required Expression Parent;
        public required string ItemName;
    }

    public class SetStatementNode(string name, Expression expr, MetaInfo info) : ASTNode(info)
    {
        public string VariableName { get; } = name;
        public Expression Expression { get; } = expr;
    }

    public abstract class ImportStatement(MetaInfo info) : ASTNode(info)
    {
        public required ImportSource Source;

        public class Selection(MetaInfo info) : ImportStatement(info)
        {
            public required string[] Symbols;
        }

        public class Binding(MetaInfo info) : ImportStatement(info)
        {
            public required string name;
        }
    }

    public abstract record ImportSource
    {

        public abstract string DefaultName();
        public record Path(string RelativePath) : ImportSource()
        {
            public override string DefaultName()
            {
                int nameStart = RelativePath.LastIndexOf('/') + 1;
                int nameEnd = RelativePath.LastIndexOf('.');

                if (nameEnd == -1)
                {
                    nameEnd = RelativePath.Length;
                }

                return RelativePath[nameStart..nameEnd];
            }
        }

        public record Namespace(string NameSpaceName) : ImportSource()
        {
            public override string DefaultName()
            {
                int nameStart = NameSpaceName.LastIndexOf('.') + 1;
                return NameSpaceName[nameStart..];
            }
        }
    }

    public class PropertyAssignment(MetaInfo info) : ASTNode(info)
    {
        public required Expression Parent;
        public required string Name;
        public required Expression Value;
    }

    public class IndexAssignment(MetaInfo info) : ASTNode(info)
    {
        public required Expression Parent;
        public required Expression Index;
        public required Expression Value;
    }

    public abstract class Expression(MetaInfo info)
    {
        public MetaInfo Info = info;
    }

    public class ConstString(MetaInfo info) : Expression(info)
    {
        public required string Value { get; set; }
    }

    public class ConstInt(MetaInfo info) : Expression(info)
    {
        public required int Value { get; set; }
    }

    public class ConstBool(MetaInfo info) : Expression(info)
    {
        public required bool Value { get; set; }
    }

    public class ConstDouble(MetaInfo info) : Expression(info)
    {
        public required double Value { get; set; }
    }

    public class Not(MetaInfo info) : Expression(info)
    {
        public required Expression Value { get; set; }
    }

    public class HasElementCheck(MetaInfo info) : Expression(info)
    {
        public required Expression Item;
        public required Expression Container;
    }

    public class TypeCheck(MetaInfo info) : Expression(info)
    {
        public required Expression Item;
        public required VType Type;
    }


    public class IdentifierNode(string name, MetaInfo info) : Expression(info)
    {
        public string Name { get; } = name;
    }

    public class ConstArray(List<Expression> expressions, MetaInfo info) : Expression(info)
    {
        public List<Expression> Expressions = expressions;

    }

    public class ConstObject(MetaInfo info) : Expression(info)
    {
        public required Dictionary<object, Expression> Entries { get; set; }

    }



    public class ConstFunction(MetaInfo info) : Expression(info)
    {
        public required List<(string, VType?)> Args;
        public required Expression Body;
        public required string[] Generics;
        public VType? ReturnType;
    }


    public class BinaryOperationNode(Expression left, string operatorSymbol, Expression right, MetaInfo info) : Expression(info)
    {
        public Expression Left { get; } = left;
        public string Operator { get; } = operatorSymbol;
        public Expression Right { get; } = right;
    }

    public class PropertyAccess(MetaInfo info) : Expression(info)
    {
        public required Expression Parent;
        public required string Name;
    }


    public class MethodCall(MetaInfo info) : Expression(info)
    {
        public required Expression Parent { get; set; }
        public required string Name { get; set; }
        public required List<Expression> Args { get; set; }
        public VType[] Generics = [];

    }

    public class LogicalNode(Expression left, Token op, Expression right, MetaInfo info) : Expression(info)
    {
        public Expression Left { get; } = left;
        public Token Operator { get; } = op;
        public Expression Right { get; } = right;
    }


    public class Invokation(MetaInfo info) : Expression(info)
    {
        public VType[] Generics = [];
        public required Expression[] Args;
        public required Expression Parent;

    }


    public class IfNode(MetaInfo info) : Expression(info)
    {
        public required Expression Condition { get; set; }
        public required Expression TrueBlock { get; set; } //allow for not only blocks to be if bodies
        public required Expression FalseBlock { get; set; }
    }

    public class Indexing(MetaInfo info) : Expression(info)
    {
        public required Expression Parent;
        public required Expression Index;
    }

    public class BlockNode(List<ASTNode> statements, MetaInfo info) : Expression(info)
    {
        public List<ASTNode> Statements { get; } = statements;
    }

    public abstract record VType(MetaInfo Info)
    {

        public VType Join(VType other)
        {
            if (this == other)
            {
                return this;
            }
            if (this is Union u1 && other is Union u2)
            {
                return new Union([.. u1.Types, .. u2.Types], u1.Info.Join(u2.Info));
            }
            if (this is Union u)
            {
                return new Union([.. u.Types, other], u.Info.Join(other.Info));
            }
            if (other is Union union)
            {
                return new Union([.. union.Types, this], union.Info.Join(Info));
            }
            return new Union([this, other], Info.Join(other.Info));
        }

        public VType Intersect(VType other)
        {
            if (this == other)
            {
                return this;
            }
            if (this is Intersection i1 && other is Intersection i2)
            {
                return new Intersection([.. i1.Types, .. i2.Types], i1.Info.Join(i2.Info));
            }
            if (this is Intersection i3)
            {
                return new Intersection([.. i3.Types, other], i3.Info.Join(other.Info));
            }
            if (other is Intersection i4)
            {
                return new Intersection([.. i4.Types, this], i4.Info.Join(Info));
            }

            return new Intersection([this, other], Info.Join(other.Info));
        }
        public record Union(HashSet<VType> Types, MetaInfo Info) : VType(Info);

        public record Intersection(HashSet<VType> Types, MetaInfo Info) : VType(Info);
        public record Normal(string[] Type, VType[] Generics, MetaInfo Info) : VType(Info);

        public record Func(VType[] Args, VType ReturnType, MetaInfo Info) : VType(Info);

        public record Array(VType ItemType, MetaInfo Info) : VType(Info);
        public record Object(Dictionary<string, VType> Entires, MetaInfo Info) : VType(Info);

    }


}