
namespace VSharpCompiler
{

    public abstract record Instruction 
    {
        public record Math(Instruction First, Instruction Second,MathOp Op) : Instruction {}
        public record Invoke(Instruction Expr, Instruction[] Args) : Instruction {}
        public record ConstStr(string Value) : Instruction;
        public record ConstInt(int Value) : Instruction;
        public record ConstDouble(double Value) : Instruction;
        public record ConstBool(bool Value) : Instruction;
        public record ConstArray(Instruction[] Items) : Instruction;
        public record Binding(string Name) : Instruction;
    }

    public abstract record ImportNode
    {
        public record Native(string Signature);

        public record Script(string RelativePath);
    }

    public enum MathOp 
    {
        Add,
        Sub,
        Mul,
        Div
    }

    public abstract record TypedInstruction(Tp Tp)
    {
    }

    public abstract record Tp {
        public record Nominal(string Signature, Tp[] TypeArguments);
        public record Primtive(EPrimitive Type) : Tp;

        public enum EPrimitive {
            I32,
            I64,
            F32,
            F64,
            Bool,
        }
    

        public record Union(Tp[] Tps) : Tp;
        public record Intersection(Tp[] Tps) : Tp;

        public record Object(Dictionary<string, Tp> Entries) : Tp;
        public record Array(Tp Entries) : Tp;

    }

    

}

