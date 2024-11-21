using System.Reflection.Emit;

namespace VSharpCompiler
{
    static class Codegen
    {

        public static async Task CompileModule(TypedIRModule module)
        {
            var tasks = module.Functions.Select(func => Task.Run(() => CompileFunction(func)));
            await Task.WhenAll(tasks);
        }

        public static void CompileFunction(TypedIRFunction func)
        {
            func.Builder.SetParameters(func.Args.Select(static it => it.Item2!.ToPhysicalType()).ToArray());
            func.Builder.SetReturnType(func.ReturnType.ToPhysicalType());
            var gen = func.Builder.GetILGenerator();
            CompileInstruction(func.Body, gen);
        }

        public static void CompileInstruction(TypedInstruction instruction, ILGenerator gen)
        {
            switch (instruction)
            {
                case TypedInstruction.ConstStr str:
                    gen.Emit(OpCodes.Ldstr, str.Value);
                    break;
                case TypedInstruction.ConstBool bl:
                    gen.Emit(bl.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    break;
                case TypedInstruction.ConstInt i:
                    gen.Emit(OpCodes.Ldc_I4, i.Value);
                    break;
                case TypedInstruction.ConstDouble dbl:
                    gen.Emit(OpCodes.Ldc_R8, dbl.Value);
                    break;
                case TypedInstruction.LoadArg arg:
                    gen.Emit(OpCodes.Ldarg, arg.Idx);
                    break;
                case TypedInstruction.LoadVar var:
                    gen.Emit(OpCodes.Ldloc, var.Idx);
                    break;
                case TypedInstruction.Math math:
                    CompileInstruction(math.First, gen);
                    CompileInstruction(math.Second, gen);
                    var opc = math.Op switch
                    {
                        MathOp.Add => OpCodes.Add,
                        MathOp.Sub => OpCodes.Sub,
                        MathOp.Mul => OpCodes.Mul,
                        MathOp.Div => OpCodes.Div,
                        _ => throw new Exception("Unreachable")
                    };

                    gen.Emit(opc);
                    break;
                case TypedInstruction.TypeCast conv:
                    CompileInstruction(conv.Parent, gen);
                    gen.Emit(OpCodes.Conv_R8);
                    break;
                case TypedInstruction.If ifIns:
                    //load condition on the stack
                    CompileInstruction(ifIns.Condition, gen);
                    var skip = gen.DefineLabel();
                    var end = gen.DefineLabel();

                    //if false skip executing the body
                    gen.Emit(OpCodes.Brfalse, skip);
                    CompileInstruction(ifIns.Body, gen);
                    //skip the else body since the body executed
                    gen.Emit(OpCodes.Br, end);

                    gen.MarkLabel(skip);
                    CompileInstruction(ifIns.ElseBody, gen);
                    gen.MarkLabel(end);
                    break;
                case TypedInstruction.While whileIns:
                    skip = gen.DefineLabel();
                    var begining = gen.DefineLabel();
                    gen.MarkLabel(begining);
                    CompileInstruction(whileIns.Condition, gen);
                    gen.Emit(OpCodes.Brfalse, skip);
                    CompileInstruction(whileIns.Body, gen);
                    if (whileIns.Body.Type != Tp.Void)
                    {
                        gen.Emit(OpCodes.Pop);
                    }
                    gen.Emit(OpCodes.Br, begining);
                    gen.MarkLabel(skip);
                    break;
                case TypedInstruction.Block block:
                    int count = 0;
                    foreach (var ins in block.Instructions)
                    {
                        CompileInstruction(ins, gen);
                        if (ins.Type != Tp.Void && count != block.Instructions.Length - 1)
                        {
                            gen.Emit(OpCodes.Pop);
                        }
                        count++;
                    }
                    break;
            }

        }
    }
}