using System.Reflection;
using System.Reflection.Emit;
using VSharp;

namespace VSharpCompiler
{
    class Compilation(HashSet<Assembly> assemblies, IModuleBuilder moduleBuilder)
    {
        HashSet<Assembly> assemblies = assemblies;
        IModuleBuilder builder = moduleBuilder;

        public async void Compile(Dictionary<Signature, ProgramNode> sourceFiles)
        {
            //first stage compilation
            var tasks = new List<Task<(Signature, IRModule)>>(sourceFiles.Count);
            var checker = new Checker(assemblies);

            foreach (var (sig, ast) in sourceFiles)
            {
                var task = Task.Run(async () => (sig, await checker.CheckProgram(ast, sig, builder)));
                tasks.Add(task);
            }

            var modules = await Task.WhenAll(tasks); //wait for everything to be checked

            //create lookup
            var lookup = new IRLookup(assemblies, modules.ToDictionary());

            //second stage compilation
            var irTasks = new List<Task<(Signature, TypedIRModule)>>(modules.Count());
            foreach (var (sig, mod) in modules)
            {
                var task = Task.Run(async () => (sig, await mod.InferTypes(lookup)));
                irTasks.Add(task);
            }

            var typedModules = await Task.WhenAll(irTasks);

            //codegen
            throw new NotImplementedException();

        }
    }

    record SynchronizedModuleBuilder(ModuleBuilder Builder) : IModuleBuilder
    {
        readonly SemaphoreSlim mutex = new(1, 1);
        public async Task<TypeBuilder> DefineType(string name, TypeAttributes attr)
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