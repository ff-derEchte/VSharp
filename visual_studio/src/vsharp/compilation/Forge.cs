using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Channels;

namespace VSharpCompiler
{

    //used to generate the interfaces and and the classes with their given implementation
    record ClassForge(InterfaceForge IFroge, IModuleBuilder Builder)
    {
        readonly SemaphoreSlim mutex = new(1, 1);
        readonly Dictionary<Tp.Object, ClassDefintion> classChache = [];
        readonly Dictionary<Tp.Object, TypeInfo> interfaceCache = [];
        readonly static MethodAttributes attr = MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        public async ValueTask<ConstructorBuilder> CraftClass(Tp.Object tp, (string, Tp)[] fields, Signature modPath, ILookup lookup)
        {
            await mutex.WaitAsync();
            try
            {
                if (classChache.TryGetValue(tp, out var clazz))
                {
                    return clazz.Constructor;
                }

                clazz = await CreateClass(fields, tp, modPath, lookup);
                classChache[tp] = clazz;
                return clazz.Constructor;
            }
            finally
            {
                mutex.Release();
            }


            throw new NotImplementedException();
        }

        async ValueTask<ClassDefintion> CreateClass((string, Tp)[] fields, Tp.Object tp, Signature modPath, ILookup lookup)
        {
            var className = TranslateSignature(fields);
            var builder = await Builder.DefineType(
                modPath.Join(new Signature(className)).Sig,
                TypeAttributes.Class | TypeAttributes.Public
            );

            var constructor = CreateConstructor(builder, fields, out var fieldMap);

            var interfaces = IFroge.FindSubsets(tp, lookup);
            foreach (var iface in interfaces)
            {
                var iFace = await FindInterface(iface);
                ImplementInteface(builder, iFace, iface, fieldMap);
            }

            var info = await Builder.CreateType(builder);

            return new ClassDefintion(info, constructor);
        }

        ConstructorBuilder CreateConstructor(TypeBuilder classBuilder, (string, Tp)[] fields, out Dictionary<string, FieldInfo> fieldMap)
        {
            fieldMap = [];
            var constructor = classBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                fields.Select(it => it.Item2.ToPhysicalType()).ToArray()
            );

            var gen = constructor.GetILGenerator();
            int i = 0;
            foreach (var (name, tp) in fields)
            {
                i++;
                var field = classBuilder.DefineField($"_{name}", tp.ToPhysicalType(), FieldAttributes.Private);
                gen.Emit(OpCodes.Ldarg_0);
                gen.Emit(OpCodes.Ldarg, (byte)i);
                gen.Emit(OpCodes.Stfld, field);
                fieldMap[name] = field;
            }

            return constructor;
        }

        void ImplementInteface(TypeBuilder classBuilder, TypeInfo iFace, Tp.Object iFaceTp, Dictionary<string, FieldInfo> fieldMap)
        {
            classBuilder.AddInterfaceImplementation(iFace);

            foreach (var (propName, propTp) in iFaceTp.Entries)
            {
                ImplementProperty(classBuilder, propName, propTp, iFace, fieldMap[propName]);
            }

        }

        void ImplementProperty(TypeBuilder classBuilder, string name, Tp tp, TypeInfo iFace, FieldInfo field)
        {
            var getter = ImplementGetter(classBuilder, name, tp, field);
            var setter = ImplementSetter(classBuilder, name, tp, field);

            var propertyBuilder = classBuilder.DefineProperty(name, PropertyAttributes.None, tp.ToPhysicalType(), null);
            propertyBuilder.SetGetMethod(getter);
            propertyBuilder.SetSetMethod(setter);

            classBuilder.DefineMethodOverride(getter, iFace.GetMethod($"get_{name}")!);
            classBuilder.DefineMethodOverride(setter, iFace.GetMethod($"set{name}")!);
        }

        MethodBuilder ImplementGetter(TypeBuilder classBuilder, string name, Tp tp, FieldInfo field)
        {
            var methodBuilder = classBuilder.DefineMethod(
                $"get_{name}",
                MethodAttributes.Public,
                tp.ToPhysicalType(),
                Type.EmptyTypes
            );
            var gen = methodBuilder.GetILGenerator();

            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldfld, field);
            gen.Emit(OpCodes.Ret);

            return methodBuilder;
        }

        MethodBuilder ImplementSetter(TypeBuilder classBuilder, string name, Tp tp, FieldInfo field)
        {
            var methodBuilder = classBuilder.DefineMethod(
                $"set{name}",
                MethodAttributes.Public,
                null,
                [tp.ToPhysicalType()]
            );
            var gen = methodBuilder.GetILGenerator();

            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldarg_1);
            gen.Emit(OpCodes.Stfld, field);
            gen.Emit(OpCodes.Ret);

            return methodBuilder;
        }

        async ValueTask<TypeInfo> FindInterface(Tp.Object obj)
        {
            if (interfaceCache.TryGetValue(obj, out var tp))
            {
                return tp;
            }

            var intefaceName = TranslateSignature(obj.Entries.Select(it => (it.Key, it.Value)).ToArray());

            var iBuilder = await Builder.DefineType(intefaceName, TypeAttributes.Interface | TypeAttributes.Public);
            foreach (var (propName, propTp) in obj.Entries)
            {
                iBuilder.DefineMethod(
                    $"get_{propName}",
                    attr,
                    propTp.ToPhysicalType(),
                    Type.EmptyTypes
                );

                // Define the "set" method
                iBuilder.DefineMethod(
                    $"set_{propName}",
                    attr,
                    null,
                    [propTp.ToPhysicalType()]
                );
            }
            var result = await Builder.CreateType(iBuilder);
            interfaceCache[obj] = result;
            return result;
        }

        string TranslateSignature((string, Tp)[] fields)
        {
            var result = new StringBuilder();
            foreach (var (name, tp) in fields)
            {
                result.Append(name);
                result.Append('_');
                result.Append(ToSignatureString(tp));
                result.Append("__");
            }

            return result.ToString();
        }

        static string ToSignatureString(Tp tp)
        {
            throw new NotImplementedException();
        }
    }

    record ClassDefintion(TypeInfo TypeBuilder, ConstructorBuilder Constructor);

    //This class will be populated with ever object type ever used in every signature and when an object is created
    //in the secnd compilation stage it will implement the given interfaces for every subset
    //that occured in any signature to be usable as such
    public class InterfaceForge
    {
        Node node = new();
        Task? runTask;

        readonly Channel<Tp.Object> chan = Channel.CreateUnbounded<Tp.Object>();

        public void Populate(Tp.Object obj)
        {
            _ = chan.Writer.WriteAsync(obj);
        }

        public void Run()
        {
            runTask = Task.Run(RunAsync);
        }

        async void RunAsync()
        {
            await foreach (var obj in chan.Reader.ReadAllAsync())
            {
                node.Populate(obj.Entries.Select(it => (it.Key, it.Value)), obj);
            }
        }

        public async Task Finalize()
        {
            chan.Writer.Complete();
            await (runTask ?? throw new InvalidOperationException("Cannot finalize twice or finalize before task is executed"));

            //traverse throguh the tie to actually generate the interfaces

        }

        public HashSet<Tp.Object> FindSubsets(Tp.Object obj, ILookup lookup)
        {
            return node.Lookup(obj.Entries.Select(it => (it.Key, it.Value)), lookup);
        }

        struct Node()
        {
            readonly Dictionary<string, Dictionary<Tp, Node>> entries = [];

            Tp.Object? tp = null;

            public void Populate(IEnumerable<(string, Tp)> values, Tp.Object obj)
            {
                if (!values.Any())
                {
                    tp = obj;
                    return;
                }
                foreach (var (name, tp) in values)
                {
                    if (!entries.TryGetValue(name, out var tps))
                    {
                        tps = [];
                        entries[name] = tps;
                    }
                    if (!tps.TryGetValue(tp, out var node))
                    {
                        node = new Node();
                        tps[tp] = node;
                    }
                    node.Populate(values.Where(it => it.Item1 != name), obj);
                }
            }

            public HashSet<Tp.Object> Lookup(IEnumerable<(string, Tp)> values, ILookup lookup)
            {
                var set = new HashSet<Tp.Object>();
                Lookup(values, set, lookup);
                return set;
            }

            void Lookup(IEnumerable<(string, Tp)> values, HashSet<Tp.Object> matches, ILookup lookup)
            {
                if (tp != null)
                {
                    matches.Add(tp);
                }
                foreach (var (name, tp) in values)
                {
                    if (!entries.TryGetValue(name, out var tps))
                    {
                        continue;
                    }
                    //check for fast direct matches
                    if (tps.TryGetValue(tp, out Node n))
                    {
                        n.Lookup(values.Where(it => it.Item1 != name), matches, lookup);
                        continue;
                    }
                    //check for slower subset matches
                    foreach (var (selectorTp, node) in tps)
                    {
                        if (selectorTp.TypeCheckAndExtractTypeArgs(tp, [], lookup))
                        {
                            node.Lookup(values.Where(it => it.Item1 != name), matches, lookup);
                        }
                    }
                }
            }
        }
    }

}