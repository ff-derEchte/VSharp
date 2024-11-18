using System.Security.Cryptography;
using VSharp;
using VSharpLib;

namespace VSharpCompiler
{

    public class ConstEvalException(MetaInfo info, string message) : Exception(message)
    {
        public MetaInfo Info = info;
    }
    class Eval
    {
        public static TypedInstruction.Const EvaluateMath(TypedInstruction.Const first, MetaInfo metaFirst, TypedInstruction.Const second, MetaInfo metaSecond, MathOp op)
        {
            if (first is TypedInstruction.ConstStr str)
            {
                return new TypedInstruction.ConstStr(str + first.ConstValue?.ToString() ?? "null", first.Info.Join(second.Info));
            }

            if (first is TypedInstruction.ConstInt i1 && second is TypedInstruction.ConstInt i2)
            {
                int r = op switch
                {
                    MathOp.Add => i1.Value + i2.Value,
                    MathOp.Sub => i1.Value - i2.Value,
                    MathOp.Mul => i1.Value * i2.Value,
                    MathOp.Div => i1.Value / i2.Value,
                    _ => throw new Exception("Unreachable")
                };
                return new TypedInstruction.ConstInt(r, first.Info.Join(second.Info));
            }

            double v1 = first switch
            {
                TypedInstruction.ConstInt i => i.Value,
                TypedInstruction.ConstDouble d => d.Value,
                _ => throw new ConstEvalException(metaFirst, "Expected numeric value")
            };

            double v2 = second switch
            {
                TypedInstruction.ConstInt i => i.Value,
                TypedInstruction.ConstDouble d => d.Value,
                _ => throw new ConstEvalException(metaSecond, "Expected numeric vaue")
            };

            var result = op switch
            {
                MathOp.Add => v1 + v2,
                MathOp.Sub => v1 - v2,
                MathOp.Mul => v1 * v2,
                MathOp.Div => v1 / v2,
                _ => throw new Exception("Unreachable")
            };

            return new TypedInstruction.ConstDouble(result, first.Info.Join(second.Info));
        }
    }
}