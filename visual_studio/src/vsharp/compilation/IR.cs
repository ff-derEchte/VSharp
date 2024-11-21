
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Data;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using VSharp;

namespace VSharpCompiler
{


    record IRLookup(HashSet<Assembly> Assemblies, Dictionary<Signature, IRModule> Modules, ClassForge Fogre) : ILookup
    {

        public async ValueTask<Candidate> Call(ModuleDescriptor moduleName, string functionName, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info)
        {
            return moduleName switch
            {
                ModuleDescriptor.Native n => NativeCall(n.Signature, functionName, args, typeArguments, info),
                ModuleDescriptor.Script path => await ScriptCall(path.RelativePath, functionName, args, typeArguments, info),
                ModuleDescriptor.SymbolAccess sa => SymbolAcccessCall(sa, functionName, args, typeArguments, info),
                _ => throw new Exception("Unreachable"),
            };
        }

        Candidate SymbolAcccessCall(ModuleDescriptor.SymbolAccess symbolAccess, string funcName, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info)
        {
            return symbolAccess.Parent switch
            {
                ModuleDescriptor.Native n => NativeCall(n.Signature.Join(new Signature(symbolAccess.SymbolName)), funcName, args, typeArguments, info),
                ModuleDescriptor.Script s => throw new Exception("Const variable export not yet implemented"),
                _ => throw new NotImplementedException()
            };

        }

        private async Task<Candidate> ScriptCall(
            string scriptPath,
            string functionName,
            TypedInstruction[] args,
            Tp[] typeArguments,
            MetaInfo info
        )
        {
            if (!Modules.TryGetValue(Signature.FromSlashNotation(scriptPath), out IRModule? mod))
            {
                throw TypeError.New(info, "No module found " + scriptPath);
            }
            if (!mod.Functions.TryGetValue(functionName, out IRFunction? func))
            {
                throw TypeError.New(info, "No function found " + functionName);
            }

            var retrunType = await func.ValidateOrThrow(args, typeArguments, this, info);

            return new Candidate(func.Info, retrunType);
        }

        private Candidate NativeCall(
            Signature sig,
            string functionName,
            TypedInstruction[] args,
            Tp[] typeArguments,
            MetaInfo info
        )
        {
            var result = FindType(sig)
                ?? throw TypeError.New(info, "Signature " + sig + " doesnt exist");
            var method = result.GetMethod(functionName, BindingFlags.Static | BindingFlags.Public, args.Select(it => it.Type.ToPhysicalType()).ToArray())
                ?? throw TypeError.New(info, "No method found with given argument types");
            var returnType = new Tp.Nominal(method.ReturnType, []);
            return new Candidate(method, returnType);
        }

        private Type? FindType(Signature sig) => Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .FirstOrDefault(t => t.Namespace == sig.ModuleName() && t.Name == sig.StructName());

        public ValueTask<Candidate> Call(Tp Instance, string functionName, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info)
        {
            throw new NotImplementedException();
        }

        public async ValueTask<(ConstructorBuilder, Tp.Object)> NewObj((string, TypedInstruction)[] items)
        {
            var tp = new Tp.Object(items.ToDictionary(
                it => it.Item1,
                it => it.Item2.Type
            ));

            return (await Fogre.CraftClass(tp, items.Select(it => (it.Item1, it.Item2.Type)).ToArray()), tp;
        }

        public async ValueTask<Candidate> CallDirect(ModuleDescriptor.SymbolAccess desc, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info)
        {
            return desc.Parent switch
            {
                ModuleDescriptor.Native n => NativeCall(n.Signature, desc.SymbolName, args, typeArguments, info),
                ModuleDescriptor.Script s => await ScriptCall(s.RelativePath, desc.SymbolName, args, typeArguments, info),
                _ => throw new NotImplementedException(),
            };
        }
    }

    public record Candidate(MethodInfo Info, Tp RetrunType);

    public interface ILookup
    {

        ValueTask<(ConstructorBuilder, Tp.Object)> NewObj((string, TypedInstruction)[] items);

        ValueTask<Candidate> Call(ModuleDescriptor moduleName, string functionName, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info);

        ValueTask<Candidate> Call(Tp Instance, string functionName, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info);

        ValueTask<Candidate> CallDirect(ModuleDescriptor.SymbolAccess desc, TypedInstruction[] args, Tp[] typeArguments, MetaInfo info);
    }

    public readonly struct Signature
    {

        private readonly string sig;

        public string Sig => sig;

        public Signature(string sig)
        {
            string pattern = @"^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)*$";

            if (!Regex.IsMatch(sig, pattern))
            {
                throw new Exception("Invalid string " + sig);
            }
            this.sig = sig;
        }

        public static Signature FromSlashNotation(string input)
        {
            return new Signature(input.Replace("/", "."));
        }

        //privatre constuctor that bypasses redundant validation
        private Signature(string sig, bool _)
        {
            this.sig = sig;
        }

        public Signature Join(Signature other)
        {
            return new Signature(sig + "." + other.sig, true); //use private constructor to not revalidate it again
        }


        public string StructName()
        {
            int start = sig.LastIndexOf('.') + 1;

            return sig[start..];
        }

        public string ModuleName()
        {
            int end = sig.LastIndexOf('.');
            if (end < 0)
            {
                throw new Exception("Signature does not ahve ModuleName");
            }

            return sig[..end];
        }


    }

    public record FunctionHandle : IFunctionHandle
    {
        Tp? tp = null;

        public void AppendReturnType(Tp tp)
        {
            if (this.tp == null)
            {
                this.tp = tp;
            }
            else
            {
                this.tp = this.tp.Join(tp);
            }
        }

        public Tp GetReturnType(Tp defaultTp)
        {
            if (tp == null)
            {
                return defaultTp;
            }
            else
            {
                return tp.Join(defaultTp);
            }
        }
    }

    public interface IFunctionHandle
    {
        public void AppendReturnType(Tp tp);
    }


    public record IRFunction((string, Tp?)[] Args, Instruction Body, Tp? ReturnType, int TypeArgumentCount, MethodBuilder Info)
    {

        TypedIRFunction? compiledBody = null;

        public async ValueTask<Tp> ValidateOrThrow(TypedInstruction[] args, Tp[] explicitTypeArguments, ILookup lookup, MetaInfo info)
        {
            if (Args.Length != args.Length)
            {
                throw TypeError.New(info, "Invalid argument count");
            }

            Tp[] generics = InferGenerics(args, explicitTypeArguments, lookup);

            return (await GetReturnType(lookup)).WithTypeArguments(generics) ?? Tp.Any;
        }

        private Tp[] InferGenerics(TypedInstruction[] args, Tp[] explicitTypeArguments, ILookup lookup)
        {
            if (explicitTypeArguments.Length == TypeArgumentCount)
            {
                return explicitTypeArguments; //type arguments are already explicitly provided
            }

            var typeArguments = new Tp[TypeArgumentCount];
            Array.Copy(explicitTypeArguments, typeArguments, explicitTypeArguments.Length);

            foreach (var ((_, arg), (actualArg, info)) in Args.Zip(args))
            {
                if (arg?.TypeCheckAndExtractTypeArgs(actualArg, typeArguments, lookup) == false)
                {
                    throw TypeError.New(info, TypeError.MissmatchMessage(arg, actualArg));
                }
            }
            return typeArguments;
        }

        private async ValueTask<Tp> GetReturnType(ILookup lookup)
        {
            if (ReturnType != null)
            {
                return ReturnType;
            }
            else
            {
                await compile(lookup);
                return compiledBody!.ReturnType;
            }
        }

        private async ValueTask compile(ILookup lookup)
        {
            if (compiledBody != null)
            {
                return;
            }
            var varMan = new VarMan();
            var variables = Variables.FromArguments(Args.Select(it => (it.Item1, it.Item2 ?? Tp.Any)), varMan);
            var handle = new FunctionHandle();
            var body = await Body.InferTypes(lookup, handle, variables);

            var frame = varMan.ToVarFrame();
            var actualReturnType = handle.GetReturnType(body.Type);

            Tp returnType;
            if (ReturnType != null)
            {
                if (!ReturnType.TypeCheckAndExtractTypeArgs(actualReturnType, new Tp[TypeArgumentCount], lookup))
                {
                    throw TypeError.New(body.Info, "Actual return type does not match specified type");
                }
                returnType = ReturnType;
            }
            else
            {
                returnType = actualReturnType;
            }
            compiledBody = new TypedIRFunction(Args, returnType, body, frame, Info);
        }

        public async ValueTask<TypedIRFunction> InferTypes(ILookup lookup)
        {
            await compile(lookup);
            return compiledBody!;
        }
    }

    public record IRTypeDefinition(Tp Tp, int GenericCount);

    public record IRModule(Dictionary<string, IRFunction> Functions, Dictionary<string, IRTypeDefinition> Types, Instruction Init, TypeBuilder Builder)
    {
        public async ValueTask<TypedIRModule> InferTypes(ILookup lookup)
        {
            var handle = new FunctionHandle();
            var variables = new Variables(new VarMan());
            var initInsturction = await Init.InferTypes(lookup, handle, variables);

            var functions = await Task.WhenAll(Functions.Select(it => it.Value.InferTypes(lookup).AsTask()));

            return new TypedIRModule(functions, initInsturction, Builder);
        }
    }

    public class TypeError(MetaInfo info, string message) : Exception(message)
    {
        public MetaInfo Info = info;

        public static TypeError New(MetaInfo info, string message)
        {
            return new TypeError(info, message);
        }

        public static string MissmatchMessage(Tp type, Tp actual)
        {
            return "Type missmatch expected " + type.ToString() + "but got " + actual.ToString(); //todo
        }
    }

    public record TypedIRFunction((string, Tp?)[] Args, Tp ReturnType, TypedInstruction Body, VarFrame VarFrame, MethodBuilder Builder);

    public record TypedIRModule(TypedIRFunction[] Functions, TypedInstruction Init, TypeBuilder Builder);
    public abstract record Instruction(MetaInfo Info)
    {

        public abstract ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables);
        public record Math(Instruction First, Instruction Second, MathOp Op, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                var first = await First.InferTypes(lookup, handle, variables);
                var second = await Second.InferTypes(lookup, handle, variables);

                // check if const evaluation is possible
                if (first is TypedInstruction.Const c1 && second is TypedInstruction.Const c2)
                {
                    return Eval.EvaluateMath(c1, First.Info, c2, Second.Info, Op); //perform const evaluation
                }

                //if there are any non numeric values exist early since thsi isnt permited
                if (!first.Type.IsNumeric())
                {
                    throw TypeError.New(First.Info, $"Expected Numeric Type but got `{first.Type}`");
                }

                if (!second.Type.IsNumeric())
                {
                    throw TypeError.New(Second.Info, $"Expected Numeric Type but got `{second.Type}`");
                }

                //shortcut exit if no type casting is needed
                if (first.Type == second.Type)
                {
                    return new TypedInstruction.Math(first, second, Op, first.Type, Info);
                }

                bool is64bit = first.Type.Is64BitNmber() || second.Type.Is64BitNmber();
                bool isFloat = first.Type.IsFloatingPoint() || second.Type.IsFloatingPoint();

                var resultType = Tp.NumberType(is64bit ? 8 : 4, isFloat);

                if (first.Type != resultType)
                {
                    first = new TypedInstruction.TypeCast(first, resultType, Info);
                }

                if (second.Type != resultType)
                {
                    second = new TypedInstruction.TypeCast(second, resultType, Info);
                }

                return new TypedInstruction.Math(first, second, Op, resultType, Info);
            }
        }

        public record Comparison(Instruction First, Instruction Second, ComparisonOp Op, MetaInfo Info) : Instruction(Info)
        {
            public override ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                throw new NotImplementedException();
            }
        }

        public record Invoke(Instruction Expr, Instruction[] Args, Tp[] TypeArguments, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                //handles the scenario where a function symbol is imported and directly invoked like this
                //example
                //import { WriteLine } from System.Console
                //WriteLine("Hello World") //handled here
                if (Expr is Module m)
                {
                    var args = await Task.WhenAll(Args.Select(it => it.InferTypes(lookup, handle, variables).AsTask()));
                    if (m.Mod is not ModuleDescriptor.SymbolAccess sa)
                    {
                        throw TypeError.New(Info, "Cannot invoke module");
                    }
                    var candidate = await lookup.CallDirect(sa, args, TypeArguments, Info);
                    return new TypedInstruction.ModuleCall(candidate.Info, args, candidate.RetrunType, Info);
                }

                //hanlde invokation of lambdas and runtime function references
                throw new NotImplementedException();
            }
        }

        public record MethodCall(Instruction Parent, string Name, Instruction[] Args, Tp[] TypeArguments, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {

                if (Parent is Module m)
                {
                    //module call:
                    var args = await Task.WhenAll(Args.Select(async it => await it.InferTypes(lookup, handle, variables)).ToArray());
                    var (function, returnType) = await lookup.Call(m.Mod, Name, args, TypeArguments, Info);
                    return new TypedInstruction.ModuleCall(function, args, returnType, Info);
                }
                else
                {
                    //actual method call:
                    var parent = await Parent.InferTypes(lookup, handle, variables);
                    var args = await Task.WhenAll(Args.Select(async it => await it.InferTypes(lookup, handle, variables)).ToArray());

                    var (function, returnType) = await lookup.Call(parent.Type, Name, args, TypeArguments, Info);

                    return new TypedInstruction.MethodCall(function, parent, args, returnType, Info);
                }


            }
        }

        public record Indexing(Instruction Parent, Instruction Index, MetaInfo Info) : Instruction(Info)
        {
            public override ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                throw new NotImplementedException();
            }
        }

        public record RuntimeTypeCheck(Instruction Expr, Tp Type, MetaInfo Info) : Instruction(Info)
        {
            public override ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                throw new NotImplementedException();
            }
        }

        public record ContainsCheck(Instruction Container, Instruction Item, MetaInfo Info) : Instruction(Info)
        {
            public override ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                throw new NotImplementedException();
            }
        }

        public record If(Instruction Condition, Instruction Body, Instruction ElseBody, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                var condition = await Condition.InferTypes(lookup, handle, variables);

                if (condition.Type != Tp.Bool)
                {
                    throw TypeError.New(Condition.Info, "Condition must be of type boolean but is of type " + condition.Type);
                }

                var body = await Body.InferTypes(lookup, handle, variables);
                var elseBody = await Body.InferTypes(lookup, handle, variables);


                return new TypedInstruction.If(condition, body, elseBody, Info);
            }
        }

        public record ConstStr(string Value, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                return new TypedInstruction.ConstStr(Value, Info);
            }
        }

        public record ConstInt(int Value, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                return new TypedInstruction.ConstInt(Value, Info);
            }
        }

        public record ConstDouble(double Value, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                return new TypedInstruction.ConstDouble(Value, Info);
            }
        }

        public record ConstBool(bool Value, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                return new TypedInstruction.ConstBool(Value, Info);
            }
        }

        public record ConstArray(Instruction[] Items, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                var items = await Task.WhenAll(Items.Select(async it => await it.InferTypes(lookup, handle, variables)).ToArray());

                var itemType = items.Select(it => it.Type).Aggregate((a, b) => a.Join(b));

                return new TypedInstruction.ConstArray(items, itemType, Info);
            }
        }

        public record ConstObject(Dictionary<object, Instruction> Entries, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                //compile arguments
                var entries = await Task.WhenAll(Entries
                    .Select(async it => ((string)it.Key, await it.Value.InferTypes(lookup, handle, variables)))
                    .ToArray());
                //get the underlying class defintiion
                var (constructor, tp) = await lookup.NewObj(entries);

                return new TypedInstruction.ConstObj(constructor, entries.Select(it => it.Item2).ToArray(), Info, tp);
            }
        }

        public record Binding(string Name, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                return variables.GetVar(Name);
            }
        }

        public record Module(ModuleDescriptor Mod, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                throw TypeError.New(Info, "Illegal use of Module. Cannot use Module as an expression (yet (companions mgiht be added for modules to make this possible))");
            }
        }

        public record SetVar(string Name, Instruction Value, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                var value = await Value.InferTypes(lookup, handle, variables);
                return variables.SetVar(value, Name);
            }
        }

        public record Block(Instruction[] Instructions, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                var scope = variables.Clone();
                var instructions = await Task.WhenAll(
                    Instructions.Select(async it => await it.InferTypes(lookup, handle, scope)).ToArray()
                );
                return new TypedInstruction.Block(instructions, Info);
            }
        }

        public record While(Instruction Condition, Instruction Body, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                return new TypedInstruction.While(await Condition.InferTypes(lookup, handle, variables), await Body.InferTypes(lookup, handle, variables), Info);
            }
        }

        public record For(string Name, Instruction Iterator, Instruction Body, MetaInfo Info) : Instruction(Info)
        {
            public override async ValueTask<TypedInstruction> InferTypes(ILookup lookup, IFunctionHandle handle, IVariables variables)
            {
                throw new NotImplementedException();
            }
        }
    }


    public abstract record TypedInstruction(Tp Type, MetaInfo Info)
    {

        public abstract record Const(Tp Type, MetaInfo Info) : TypedInstruction(Type, Info)
        {
            public abstract object ConstValue { get; }
        }
        public record ConstStr(string Value, MetaInfo Info) : Const(Tp.Str, Info)
        {
            public override object ConstValue => Value;
        }

        public record ConstObj(ConstructorBuilder Constructor, TypedInstruction[] Items, MetaInfo Info, Tp.Object Tp) : TypedInstruction(Tp, Info);

        public record ConstArray(TypedInstruction[] Items, Tp ItemType, MetaInfo Info) : TypedInstruction(new Tp.Array(ItemType), Info);

        public record ConstInt(int Value, MetaInfo Info) : Const(Tp.I32, Info)
        {
            public override object ConstValue => Value;
        }

        public record ConstDouble(double Value, MetaInfo Info) : Const(Tp.F64, Info)
        {
            public override object ConstValue => Value;
        }

        public record ConstBool(bool Value, MetaInfo Info) : Const(Tp.Bool, Info)
        {
            public override object ConstValue => Value;
        }

        public record Math(TypedInstruction First, TypedInstruction Second, MathOp Op, Tp Type, MetaInfo Info) : TypedInstruction(Type, Info);

        public record TypeCast(TypedInstruction Parent, Tp TargetType, MetaInfo Info) : TypedInstruction(TargetType, Info);

        public record If(TypedInstruction Condition, TypedInstruction Body, TypedInstruction ElseBody, MetaInfo Info) : TypedInstruction(Body.Type.Join(ElseBody.Type), Info);
        public record While(TypedInstruction Condition, TypedInstruction Body, MetaInfo Info) : TypedInstruction(Tp.Void, Info);
        public record Block(TypedInstruction[] Instructions, MetaInfo Info) : TypedInstruction(Instructions.Length > 0 ? Instructions.Last().Type : Tp.Void, Info);
        public record LoadVar(int Idx, Tp Type, MetaInfo Info) : TypedInstruction(Type, Info);
        public record SetVar(int Idx, TypedInstruction Value, MetaInfo Info) : TypedInstruction(Tp.Void, Info);
        public record LoadArg(int Idx, Tp Type, MetaInfo Info) : TypedInstruction(Type, Info);

        public record ModuleCall(MethodInfo FunctionInfo, TypedInstruction[] Args, Tp ReturnType, MetaInfo Info) : TypedInstruction(ReturnType, Info);
        public record MethodCall(MethodInfo FunctionInfo, TypedInstruction Instance, TypedInstruction[] Args, Tp ReturnType, MetaInfo Info) : TypedInstruction(ReturnType, Info);

    }


    public abstract record ModuleDescriptor
    {

        public static ModuleDescriptor FromImportSource(ImportSource source)
        {
            return source switch
            {
                ImportSource.Namespace n => new Native(new Signature(n.NameSpaceName)),
                ImportSource.Path p => new Script(p.RelativePath),
                _ => throw new Exception("Unreachable"),
            };
        }

        public record SymbolAccess(string SymbolName, ModuleDescriptor Parent) : ModuleDescriptor;
        public record Native(Signature Signature) : ModuleDescriptor;

        public record Script(string RelativePath) : ModuleDescriptor;
    }

    public enum MathOp
    {
        Add,
        Sub,
        Mul,
        Div
    }


    public enum ComparisonOp
    {
        Eq,
        Neq,
        Gt,
        Gte,
        St,
        Ste
    }

    public abstract record Tp
    {

        public static readonly Nominal I32 = new(typeof(int), []);
        public static readonly Nominal I64 = new(typeof(long), []);
        public static readonly Nominal F32 = new(typeof(float), []);
        public static readonly Nominal F64 = new(typeof(double), []);
        public static readonly Nominal Bool = new(typeof(bool), []);
        public static readonly Nominal Str = new(typeof(string), []);
        public static readonly Nominal Any = new(typeof(object), []);
        public static readonly Special Void = new(ESpecial.Void);
        public static readonly Tp[] NumericTypes = [I32, I64, F32, F64];
        public static readonly ImmutableDictionary<string, IRTypeDefinition> Primitives = new[]
        {
            ("int", new IRTypeDefinition(I32, 0)),
            ("bool", new IRTypeDefinition(Bool, 0)),
            ( "i32", new IRTypeDefinition(I32, 0)),
            ( "f64", new IRTypeDefinition(F64, 0)),
            ( "i64", new IRTypeDefinition(I64, 0)),
            ( "f32", new IRTypeDefinition(F32, 0)),
            ( "str", new IRTypeDefinition(Str, 0)),
        }.ToImmutableDictionary(it => it.Item1, it => it.Item2);

        public static Tp NumberType(int bytes, bool IsFloatingPoint)
        {
            return bytes switch
            {
                8 => IsFloatingPoint ? F64 : I64,
                4 => IsFloatingPoint ? F32 : I32,
                _ => throw new Exception("No such number type available"),
            };
        }

        public bool IsNumeric()
        {
            return NumericTypes.Contains(this) || (this is Union u && u.Tps.All(it => it.IsNumeric()));
        }

        public bool Is64BitNmber()
        {
            return this == Tp.I64 || this == Tp.F64;
        }

        public bool IsFloatingPoint()
        {
            return this == F32 || this == F64;
        }

        public abstract Tp WithTypeArguments(Tp[] typeArguments);
        public abstract Type ToPhysicalType();

        public abstract bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup);

        public Tp Join(Tp other)
        {
            if (this is Union u1)
            {
                if (other is Union u2)
                {
                    return new Union([.. u1.Tps, .. u2.Tps]);
                }
                else
                {
                    return new Union([.. u1.Tps, other]);
                }
            }
            else if (other is Union)
            {
                return other.Join(this);
            }
            else
            {
                return new Union([this, other]);
            }
        }

        public Tp Intersect(Tp other)
        {
            if (this is Intersection i1)
            {
                if (other is Intersection i2)
                {
                    return new Intersection([.. i1.Tps, .. i2.Tps]);
                }
                else
                {
                    return new Intersection([.. i1.Tps, other]);
                }
            }
            else if (other is Intersection)
            {
                return other.Join(this);
            }
            else
            {
                return new Intersection([this, other]);
            }
        }
        public record Special(ESpecial Kind) : Tp
        {
            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] output, ILookup lookup) => populatedType == this;

            public override Type ToPhysicalType()
            {
                return Kind switch
                {
                    ESpecial.Void => typeof(void),
                    _ => typeof(object),
                };
            }

            public override Tp WithTypeArguments(Tp[] typeArguments)
            {
                return this;
            }
        }

        public enum ESpecial
        {
            Void,
            Null,
            Never
        }

        public record Nominal(Type Type, Tp[] TypeArguments) : Tp
        {
            public override Type ToPhysicalType()
            {
                return Type;
            }


            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                if (
                    populatedType is not Nominal n ||
                    n.Type != Type ||
                    TypeArguments.Length != n.TypeArguments.Length
                )
                {
                    return false;
                }

                foreach (var (tp, ptp) in TypeArguments.Zip(n.TypeArguments))
                {

                    if (!tp.TypeCheckAndExtractTypeArgs(ptp, tpArgsOut, lookup))
                    {
                        return false;
                    }
                }
                return true;
            }

            public override Tp WithTypeArguments(Tp[] typeArguments)
            {
                return new Nominal(Type, [.. TypeArguments.Select(it => it.WithTypeArguments(typeArguments))]);
            }
        }

        public record ImportItemRef(string ScriptPath, string[] RelativeAccessPath, Tp[] TypeArguemnts) : Tp
        {
            public override Type ToPhysicalType()
            {
                throw new NotImplementedException();
            }

            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                throw new NotImplementedException();
            }

            public override Tp WithTypeArguments(Tp[] typeArguments)
            {
                return new ImportItemRef(ScriptPath, RelativeAccessPath, [.. TypeArguemnts.Select(it => it.WithTypeArguments(typeArguments))]);
            }
        }

        public record Generic(int Id) : Tp
        {
            public override Type ToPhysicalType()
            {
                throw new NotImplementedException();
            }

            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                if (tpArgsOut[Id] != null)
                {
                    tpArgsOut[Id] = tpArgsOut[Id].Join(populatedType);
                }
                else
                {
                    tpArgsOut[Id] = populatedType;
                }
                return true;
            }

            public override Tp WithTypeArguments(Tp[] TypeArguments)
            {
                return TypeArguments[Id];
            }
        }

        public record Union(Tp[] Tps) : Tp
        {
            public override Type ToPhysicalType()
            {
                return typeof(object);
            }


            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                if (populatedType is not Union u)
                {
                    foreach (var tp in Tps)
                    {
                        var copy = (Tp[])tpArgsOut.Clone();
                        if (!tp.TypeCheckAndExtractTypeArgs(populatedType, copy, lookup))
                        {
                            continue;
                        }

                        System.Array.Copy(copy, tpArgsOut, copy.Length);
                        return true;
                    }
                    return false;
                }
                else
                {
                    return u.Tps.All(it => TypeCheckAndExtractTypeArgs(it, tpArgsOut, lookup));
                }
            }

            public override Tp WithTypeArguments(Tp[] TypeArguments)
            {
                return new Union([.. Tps.Select(it => it.WithTypeArguments(TypeArguments))]);
            }
        }

        public record Intersection(Tp[] Tps) : Tp
        {
            public override Type ToPhysicalType()
            {
                return typeof(object);
            }

            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                if (populatedType is not Intersection i)
                {
                    return Tps.All(it => TypeCheckAndExtractTypeArgs(populatedType, tpArgsOut, lookup));
                }
                else
                {
                    return i.Tps.All(it => TypeCheckAndExtractTypeArgs(it, tpArgsOut, lookup));
                }
            }

            public override Tp WithTypeArguments(Tp[] TypeArguments)
            {
                return new Intersection([.. Tps.Select(it => it.WithTypeArguments(TypeArguments))]);
            }
        }

        public record Object(Dictionary<string, Tp> Entries) : Tp
        {
            public override Type ToPhysicalType()
            {
                return typeof(VSharpObject);
            }

            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                if (populatedType is Object o)
                {
                    foreach (var (name, tp) in Entries)
                    {
                        if (!o.Entries.TryGetValue(name, out Tp? value) || !tp.TypeCheckAndExtractTypeArgs(value, tpArgsOut, lookup))
                        {
                            return false;
                        }
                    }
                    return true;

                }
                else if (populatedType is Union u)
                {
                    return u.Tps.All(it => TypeCheckAndExtractTypeArgs(it, tpArgsOut, lookup));
                }
                else if (populatedType is Intersection i)
                {
                    var actualEntries = new HashSet<string>();
                    foreach (var itp in i.Tps) //for every variant
                    {
                        if (itp is not Object obj) continue;

                        foreach (var (name, propType) in obj.Entries)
                        {
                            //check if it is relevant to us
                            if (!Entries.TryGetValue(name, out Tp? fieldType)) continue;

                            var clone = (Tp[])tpArgsOut.Clone();

                            //perfor the typecheck
                            if (!fieldType.TypeCheckAndExtractTypeArgs(propType, clone, lookup)) continue;

                            //if true actually apply the changes to the actual array (if not its thrown away)
                            System.Array.Copy(clone, tpArgsOut, clone.Length);

                            //add the name to the checked types
                            actualEntries.Add(name);
                        }
                    }

                    return actualEntries.Count == Entries.Count; //if we successfully checked every property its true
                }
                else
                {
                    return false;
                }
            }

            public override Tp WithTypeArguments(Tp[] TypeArguments)
            {
                return new Object(Entries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.WithTypeArguments(TypeArguments)));
            }
        }

        public record Array(Tp ItemType) : Tp
        {
            public override Type ToPhysicalType()
            {
                return typeof(List<object?>);
            }

            public override bool TypeCheckAndExtractTypeArgs(Tp populatedType, Tp[] tpArgsOut, ILookup lookup)
            {
                if (populatedType is Array a)
                {
                    return ItemType.TypeCheckAndExtractTypeArgs(a.ItemType, tpArgsOut, lookup);
                }
                else if (populatedType is Union u)
                {
                    return u.Tps.All(it => TypeCheckAndExtractTypeArgs(it, tpArgsOut, lookup));
                }
                else if (populatedType is Intersection i)
                {
                    foreach (var itp in i.Tps)
                    {
                        var clone = (Tp[])tpArgsOut.Clone();
                        if (!TypeCheckAndExtractTypeArgs(itp, clone, lookup)) continue;

                        System.Array.Copy(clone, tpArgsOut, clone.Length);
                        return true;
                    }
                    return false;
                }
                return false;
            }

            public override Tp WithTypeArguments(Tp[] TypeArguments)
            {
                return new Array(ItemType.WithTypeArguments(TypeArguments));
            }
        }
    }



}

