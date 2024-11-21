using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using VSharp;
namespace VSharpCompiler
{


    class ScopedNames
    {
        public ScopedNames? Parent;
        public HashSet<string> Names = [];

        public bool Has(string name)
        {
            return Names.Contains(name) || (Parent?.Has(name) ?? false);
        }

        public void Insert(string name)
        {
            Names.Add(name);
        }

        public ScopedNames Child()
        {
            return new ScopedNames { Parent = this };
        }
    }

    class CompilationException(MetaInfo info, string message) : Exception(message)
    {
        public MetaInfo Info = info;
    }

    public interface IModuleBuilder
    {
        ValueTask<TypeBuilder> DefineType(string name, TypeAttributes attr);
        ValueTask<TypeInfo> CreateType(TypeBuilder builder);
    }





    public class Checker(HashSet<Assembly> assemblies, InterfaceForge forge)
    {
        readonly HashSet<Assembly> assemblies = assemblies;

        readonly InterfaceForge forge = forge;

        class Ctx()
        {

            public static async Task<Ctx> FromModuleBuilder(Signature modulePath, IModuleBuilder builder)
            {
                var typeBuilder = await builder.DefineType(modulePath.StructName(), TypeAttributes.Class | TypeAttributes.Public);
                return new Ctx(modulePath, typeBuilder);
            }
            public Ctx(Signature modulePath, TypeBuilder builder) : this()
            {
                CurrentType = builder;
            }
            public TypeBuilder CurrentType;

            public Dictionary<string, IRFunction> Functions = [];
            public Dictionary<string, ModuleDescriptor> Imports = [];

            public HashSet<string> Arguments = [];
            public HashSet<string> OldArgs = [];

            public Dictionary<string, IRTypeDefinition> Types = [];

            public static async Task<Ctx> WithPrimtiives(Signature modulePath, IModuleBuilder builder)
            {
                var instance = await FromModuleBuilder(modulePath, builder);
                instance.Types = Tp.Primitives.ToDictionary();
                return instance;
            }

            public Ctx Child()
            {
                return new Ctx { CurrentType = CurrentType, Functions = Functions.ToDictionary(), Imports = Imports.ToDictionary(), Types = Types.ToDictionary() };
            }

            public Ctx Child(HashSet<string> args)
            {
                return new Ctx { CurrentType = CurrentType, Functions = Functions.ToDictionary(), Imports = Imports.ToDictionary(), Arguments = args, OldArgs = [.. Arguments], Types = Types.ToDictionary() };
            }
        }

        public async Task<IRModule> CheckProgram(ProgramNode program, Signature modulePath, IModuleBuilder builder)
        {
            ScopedNames names = new();

            Ctx ctx = await Ctx.WithPrimtiives(modulePath, builder);
            var init = program.Statements.Select(it => CheckStatement(it, names, ctx));
            return new IRModule(ctx.Functions, ctx.Types, new Instruction.Block([.. init], program.Info), ctx.CurrentType);
        }

        Instruction CheckStatement(ASTNode statement, ScopedNames names, Ctx ctx) => statement switch
        {
            ExprStatement est => CheckExpression(est.Expression, names, ctx),
            SetStatementNode set => CheckSetStatement(set, names, ctx),
            TypeStatement typeStatement => CheckTypeStatement(typeStatement, ctx),
            WhileStatementNode whileStatement => CheckWhileStatement(whileStatement, names, ctx),
            ForLoop forLoop => CheckForStatement(forLoop, names, ctx),
            ImportStatement importStatement => CheckImportStatement(importStatement, names, ctx),
            _ => throw Error(statement.Info, "Unhandled case " + statement.ToString()),
        };

        Instruction CheckImportStatement(ImportStatement importStatement, ScopedNames names, Ctx ctx)
        {
            var desc = ModuleDescriptor.FromImportSource(importStatement.Source);
            switch (importStatement)
            {
                case ImportStatement.Binding b:
                    ctx.Imports[b.name] = desc;
                    break;
                case ImportStatement.Selection s:
                    foreach (var symbol in s.Symbols)
                    {
                        ctx.Imports[symbol] = new ModuleDescriptor.SymbolAccess(symbol, desc);
                    }
                    break;
                default:
                    throw new Exception("");
            }
            return new Instruction.Block([], importStatement.Info);
        }

        Instruction.For CheckForStatement(ForLoop forLoop, ScopedNames names, Ctx ctx)
        {
            var iterator = CheckExpression(forLoop.Parent, names, ctx);
            var scope = names.Child();
            scope.Insert(forLoop.ItemName);
            var body = CheckExpression(forLoop.Body, scope, ctx);
            return new Instruction.For(forLoop.ItemName, iterator, body, forLoop.Info);
        }

        Instruction.While CheckWhileStatement(WhileStatementNode whileStatement, ScopedNames names, Ctx ctx)
        {
            var condition = CheckExpression(whileStatement.Condition, names, ctx);
            var body = CheckExpression(whileStatement.TrueBlock, names, ctx);

            return new Instruction.While(condition, body, whileStatement.Info);
        }

        Instruction.Block CheckTypeStatement(TypeStatement typeStatement, Ctx ctx)
        {
            int genericCount = typeStatement.Generics.Length;
            //create a map of generic names to their indecies
            var generics = typeStatement.Generics
                .Select((value, index) => new { value, index })
                .ToDictionary(x => x.value, x => x.index); ;

            var result = CheckType(typeStatement.Type, ctx, generics);

            ctx.Types[typeStatement.Name] = new IRTypeDefinition(result, genericCount);
            return new Instruction.Block([], typeStatement.Info);
        }

        void CheckFunction()
        {

        }

        Instruction CheckSetStatement(SetStatementNode set, ScopedNames names, Ctx ctx)
        {
            if (set.Expression is ConstFunction func)
            {
                var scope = names.Child();
                var childCtx = ctx.Child(func.Args.Select(it => it.Item1).ToHashSet());

                //transform function body
                Instruction body = CheckExpression(func.Body, names.Child(), ctx);

                //create a map of generic names to their indecies
                var generics = func.Generics
                    .Select((value, index) => new { value, index })
                    .ToDictionary(x => x.value, x => x.index); ;

                //vaidate the type annoations for the function (with the gneerics)
                var args = func.Args.Select(
                    it => (it.Item1, it.Item2 != null ? CheckType(it.Item2, ctx, generics) : null)
                ).ToArray();

                //validate the type annoation for the return type
                var returnType = func.ReturnType != null ? CheckType(func.ReturnType, ctx, generics) : null;

                //construct the function object
                ctx.Functions[set.VariableName] = new(args, body, returnType, func.Generics.Length, ctx.CurrentType.DefineMethod(set.VariableName, MethodAttributes.Public | MethodAttributes.Static));
                return new Instruction.Block([], func.Info);
            }
            else
            {
                Instruction result = CheckExpression(set.Expression, names, ctx);
                names.Insert(set.VariableName);
                return new Instruction.SetVar(set.VariableName, result, set.Info);
            }
        }

        Instruction CheckExpression(Expression expr, ScopedNames names, Ctx ctx)
        {
            return expr switch
            {
                ConstString s => new Instruction.ConstStr(s.Value, s.Info),
                ConstDouble d => new Instruction.ConstDouble(d.Value, d.Info),
                ConstBool b => new Instruction.ConstBool(b.Value, b.Info),
                ConstArray array => new Instruction.ConstArray(array.Expressions.Select(it => CheckExpression(it, names, ctx)).ToArray(), array.Info),
                ConstObject obj => new Instruction.ConstObject(obj.Entries.ToDictionary(it => it.Key, it => CheckExpression(it.Value, names, ctx)), obj.Info),
                IdentifierNode identifier => CheckIdentifier(identifier, names, ctx),
                BlockNode block => CheckBlock(block, names, ctx),
                Invokation invk => CheckInvokation(invk, names, ctx),
                IfNode ifNode => CheckIf(ifNode, names, ctx),
                TypeCheck typeCheck => CheckTypeChecking(typeCheck, names, ctx),
                Indexing indexing => CheckIndexing(indexing, names, ctx),
                BinaryOperationNode binaryOp => CheckBinaryOp(binaryOp, names, ctx),
                MethodCall call => CheckMethodCall(call, names, ctx),
                _ => throw Error(expr.Info, "Unhandled expression type: " + expr),
            };
        }

        Instruction.RuntimeTypeCheck CheckTypeChecking(TypeCheck check, ScopedNames names, Ctx ctx)
        {
            var expr = CheckExpression(check.Item, names, ctx);
            var type = CheckType(check.Type, ctx, []);
            return new Instruction.RuntimeTypeCheck(expr, type, check.Info);
        }

        Instruction.Indexing CheckIndexing(Indexing indexing, ScopedNames names, Ctx ctx)
        {
            var parent = CheckExpression(indexing.Parent, names, ctx);
            var index = CheckExpression(indexing.Index, names, ctx);

            return new Instruction.Indexing(parent, index, indexing.Info);
        }

        Instruction.If CheckIf(IfNode ifNode, ScopedNames names, Ctx ctx)
        {
            var cond = CheckExpression(ifNode.Condition, names, ctx);
            var body = CheckExpression(ifNode.TrueBlock, names, ctx);
            var elseBody = CheckExpression(ifNode.FalseBlock, names, ctx);
            return new Instruction.If(cond, body, elseBody, ifNode.Info);
        }

        Instruction.Invoke CheckInvokation(Invokation invokation, ScopedNames names, Ctx ctx)
        {
            var parent = CheckExpression(invokation.Parent, names, ctx);
            var typeArguments = invokation.Generics.Select(it => CheckType(it, ctx, []));
            var args = invokation.Args.Select(it => CheckExpression(it, names, ctx));
            return new Instruction.Invoke(parent, [.. args], [.. typeArguments], invokation.Info);
        }

        Instruction.MethodCall CheckMethodCall(MethodCall call, ScopedNames names, Ctx ctx)
        {
            var parent = CheckExpression(call.Parent, names, ctx);
            var typeArguments = call.Generics.Select(it => CheckType(it, ctx, []));
            var args = call.Args.Select(it => CheckExpression(it, names, ctx));

            return new Instruction.MethodCall(parent, call.Name, [.. args], [.. typeArguments], call.Info);

        }

        Instruction.Block CheckBlock(BlockNode block, ScopedNames names, Ctx ctx)
        {
            var child = names.Child();
            var childCtx = ctx.Child();
            return new Instruction.Block(block.Statements.Select(it => CheckStatement(it, child, childCtx)).ToArray(), block.Info);
        }

        Instruction CheckBinaryOp(BinaryOperationNode node, ScopedNames names, Ctx ctx)
        {
            var left = CheckExpression(node.Left, names, ctx);
            var right = CheckExpression(node.Right, names, ctx);

            var mathOp = MathOpFromString(node.Operator);
            if (mathOp != null)
            {
                return new Instruction.Math(left, right, (MathOp)mathOp, node.Info);
            }
            var comparsionOp = ComparisonOpFromString(node.Operator);
            if (comparsionOp != null)
            {
                return new Instruction.Comparison(left, right, (ComparisonOp)comparsionOp, node.Info);
            }

            throw Error(node.Info, "Invalid operator for binary operation");
        }

        static MathOp? MathOpFromString(string op) => op switch
        {
            "+" => MathOp.Add,
            "-" => MathOp.Sub,
            "*" => MathOp.Mul,
            "/" => MathOp.Div,
            _ => null
        };

        static ComparisonOp? ComparisonOpFromString(string op) => op switch
        {
            "==" => ComparisonOp.Eq,
            "!=" => ComparisonOp.Neq,
            "<" => ComparisonOp.St,
            "<=" => ComparisonOp.Ste,
            ">" => ComparisonOp.Gt,
            ">=" => ComparisonOp.Gte,
            _ => null
        };

        Instruction CheckIdentifier(IdentifierNode node, ScopedNames names, Ctx ctx)
        {
            if (names.Has(node.Name))
            {
                return new Instruction.Binding(node.Name, node.Info);
            }
            if (ctx.Imports.TryGetValue(node.Name, out ModuleDescriptor? value))
            {
                return new Instruction.Module(value, node.Info);
            }

            throw Error(node.Info, "Invalid identifier (possibly varaible used before declaration): " + node.Name);
        }

        Tp CheckType(VType type, Ctx ctx, Dictionary<string, int> generics)
        {
            switch (type)
            {
                case VType.Array array:
                    return new Tp.Array(CheckType(array.ItemType, ctx, generics));
                case VType.Object obj:
                    var entries = obj.Entires.ToDictionary(
                        kvp => kvp.Key,
                        kvp => CheckType(kvp.Value, ctx, generics)
                    );
                    return new Tp.Object(entries);
                case VType.Union union:
                    return new Tp.Union(union.Types.Select(it => CheckType(it, ctx, generics)).ToArray());
                case VType.Intersection intersection:
                    return new Tp.Intersection(intersection.Types.Select(it => CheckType(it, ctx, generics)).ToArray());
                case VType.Normal normal:
                    return CheckNormalType(normal, ctx, generics);
                default:
                    throw Error(type.Info, "Unsuported Type");
            }
        }

        Tp CheckNormalType(VType.Normal tp, Ctx ctx, Dictionary<string, int> generics)
        {
            var args = tp.Generics.Select(it => CheckType(it, ctx, generics));

            //handle empty name
            if (tp.Type.Length == 0)
            {
                throw Error(tp.Info, "Invalid Empty Type Signature");
            }

            if (tp.Type.Length == 1)
            {
                string name = tp.Type[0];

                //generic reference
                if (generics.TryGetValue(name, out int idx))
                {
                    return new Tp.Generic(idx);
                }

                //reference to locally defined type alias
                var result = ctx.Types[name];
                if (result != null)
                {
                    if (result.GenericCount != args.Count())
                    {
                        throw Error(tp.Info, "Invalid generc count supplied");
                    }
                    return result.Tp.WithTypeArguments([.. args]);
                }
            }

            //accessing imported module
            if (ctx.Imports.TryGetValue(tp.Type[0], out ModuleDescriptor? module))
            {
                switch (module)
                {
                    case ModuleDescriptor.Native native:
                        var fullSignature = native.Signature + string.Join('.', tp.Type[1..]);
                        var type = GetType(tp.Info, fullSignature);
                        return new Tp.Nominal(type, [.. args]);
                    case ModuleDescriptor.Script script:
                        return new Tp.ImportItemRef(
                            script.RelativePath,
                            tp.Type[1..],
                            [.. args]
                        );
                }
            }

            throw Error(tp.Info, "Invaid Type Signature (You might have forgotten to import)");
        }

        Type GetType(MetaInfo info, string name)
        {
            foreach (var assembly in assemblies)
            {
                var result = assembly.GetType(name);
                if (result != null)
                {
                    return result;
                }
            }
            throw Error(info, "No class with specified name found in provided namespace");
        }

        static CompilationException Error(MetaInfo info, string message)
        {
            return new CompilationException(info, message);
        }
    }
}