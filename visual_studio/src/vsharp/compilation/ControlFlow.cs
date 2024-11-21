using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Versioning;
using VSharp;

namespace VSharpCompiler
{
    class Compilation(HashSet<Assembly> assemblies, IModuleBuilder moduleBuilder)
    {
        HashSet<Assembly> assemblies = assemblies;
        IModuleBuilder builder = moduleBuilder;

        public async Task Compile(Dictionary<Signature, ProgramNode> sourceFiles)
        {
            var forge = new InterfaceForge();
            forge.Run();

            //first stage compilation
            var modules = await CompileFirstStage(sourceFiles, forge);

            //wait for forge to be finalized 
            await forge.Finalize();

            var classForge = new ClassForge(forge, builder);

            //create lookup
            var lookup = new IRLookup(assemblies, modules.ToDictionary(), classForge);

            //compile second stage
            var typedModules = await CompileSecondStage(lookup, modules);

            //codegen
            await CompileCodeGen(typedModules);
        }

        async Task CompileCodeGen((Signature, TypedIRModule)[] modules)
        {
            var tasks = new List<Task>(modules.Length);
            foreach (var (sig, mod) in modules)
            {
                var task = Task.Run(async () => await Codegen.CompileModule(mod));
                tasks.Add(task);
            }
            await Task.WhenAll(tasks);
        }

        async Task<(Signature, IRModule)[]> CompileFirstStage(Dictionary<Signature, ProgramNode> sourceFiles, InterfaceForge forge)
        {
            var tasks = new List<Task<(Signature, IRModule)>>(sourceFiles.Count);
            var checker = new Checker(assemblies, forge);

            foreach (var (sig, ast) in sourceFiles)
            {
                var task = Task.Run(async () => (sig, await checker.CheckProgram(ast, sig, builder)));
                tasks.Add(task);
            }

            return await Task.WhenAll(tasks); //wait for everything to be checked
        }

        async Task<(Signature, TypedIRModule)[]> CompileSecondStage(ILookup lookup, (Signature, IRModule)[] modules)
        {
            //second stage compilation
            var irTasks = new List<Task<(Signature, TypedIRModule)>>(modules.Count());
            foreach (var (sig, mod) in modules)
            {
                var task = Task.Run(async () => (sig, await mod.InferTypes(lookup)));
                irTasks.Add(task);
            }

            return await Task.WhenAll(irTasks);
        }
    }

    record SynchronizedModuleBuilder(ModuleBuilder Builder) : IModuleBuilder
    {
        readonly SemaphoreSlim mutex = new(1, 1);

        public async ValueTask<TypeInfo> CreateType(TypeBuilder builder)
        {
            await mutex.WaitAsync();
            try
            {
                return builder.CreateTypeInfo();
            }
            finally
            {
                mutex.Release();
            }
        }

        public async ValueTask<TypeBuilder> DefineType(string name, TypeAttributes attr)
        {
            await mutex.WaitAsync();
            try
            {
                return Builder.DefineType(name, attr);
            }
            finally
            {
                mutex.Release();
            }
        }
    }
}