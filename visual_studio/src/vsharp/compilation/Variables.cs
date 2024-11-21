using System.Collections.Immutable;
using VSharp;

namespace VSharpCompiler
{

    //High level variable manager (uses a binidng type aproach to allow for bindings to fields, physical varaibles or arguments)
    public interface IVariables
    {
        public IVariables Clone();

        public TypedInstruction GetVar(string name);
        internal void SetVar(IBinding binding, string name);
        public TypedInstruction SetVar(TypedInstruction value, string name);
    }

    public class Variables : IVariables
    {
        internal Variables(IVarMan varMan)
        {
            this.varMan = varMan;
        }

        internal static Variables FromArguments(IEnumerable<(string, Tp)> args, IVarMan varMan)
        {
            var variables = new Variables(varMan);
            int slot = 0;
            foreach (var (name, type) in args)
            {
                variables.SetVar(new ArgBinding(slot, type, varMan), name);
                slot++;
            }

            return variables;
        }
        readonly IVarMan varMan;
        readonly Dictionary<string, IBinding> variables = [];

        internal Variables(IVarMan varMan, Dictionary<string, IBinding> variables) : this(varMan)
        {
            this.variables = variables;
        }

        public IVariables Clone()
        {
            return new Variables(varMan, variables.ToDictionary());
        }

        public TypedInstruction GetVar(string name)
        {
            return variables[name].GetVar();
        }

        public TypedInstruction SetVar(TypedInstruction value, string name)
        {
            if (variables.TryGetValue(name, out IBinding? binding))
            {
                return binding.SetVar(value);
            }
            else
            {
                var varBinding = new VariableBinding(varMan, value.Type);
                variables[name] = varBinding;
                return varBinding.SetVar(value);
            }
        }

        public void SetVar(IBinding binding, string name)
        {
            variables[name] = binding;
        }
    }

    public interface IBinding
    {
        public TypedInstruction SetVar(TypedInstruction Value);
        public TypedInstruction GetVar();
        public Tp Type { get; }
    }

    public class ArgBinding : IBinding
    {
        internal ArgBinding(int slot, Tp tp, IVarMan? varMan)
        {
            this.varMan = varMan;
            this.slot = slot;
            this.tp = tp;
            this.varBinding = null;
        }

        IVarMan? varMan;
        VariableBinding? varBinding;
        int slot;
        Tp tp;

        public Tp Type => throw new NotImplementedException();

        public TypedInstruction SetVar(TypedInstruction Value)
        {
            if (varBinding != null)
            {
                return varBinding.SetVar(Value);
            }
            if (varMan != null)
            {
                varBinding = new VariableBinding(varMan, Value.Type);
                return varBinding.SetVar(Value);
            }

            throw new Exception("Cannot reassign argument");
        }

        public TypedInstruction GetVar()
        {
            if (varBinding != null)
            {
                return varBinding.GetVar();
            }

            return new TypedInstruction.LoadArg(slot, tp, MetaInfo.Empty);
        }
    }

    public class VariableBinding : IBinding
    {
        internal VariableBinding(IVarMan varMan, Tp tp)
        {
            slot = varMan.Allocate(tp);
            this.tp = tp;
            this.varMan = varMan;
        }
        readonly IVarMan varMan;
        int slot;
        Tp tp;
        public Tp Type => tp;

        public TypedInstruction SetVar(TypedInstruction Value)
        {
            if (Value.Type == tp)
            {
                return new TypedInstruction.SetVar(slot, Value, MetaInfo.Empty);
            }

            tp = Value.Type;
            varMan.Free(slot);
            slot = varMan.Allocate(Value.Type);

            return new TypedInstruction.SetVar(slot, Value, MetaInfo.Empty);
        }

        public TypedInstruction GetVar()
        {
            return new TypedInstruction.LoadVar(slot, tp, MetaInfo.Empty);
        }
    }

    interface IVarMan
    {
        public int Allocate(Tp type);
        public void Free(int slot);
        public int MaxVarCount();
        public VarFrame ToVarFrame();
    }


    public record VarFrame(ImmutableDictionary<int, Tp> Entries, int VarCount);

    class VarMan : IVarMan
    {
        readonly Dictionary<Tp, Stack<int>> freeSlots = [];
        readonly Dictionary<int, Tp> entries = [];
        int maxVar = 0;
        public int Allocate(Tp type)
        {
            if (freeSlots.TryGetValue(type, out Stack<int>? s))
            {
                if (s.Count != 0)
                {
                    return s.Pop();
                }
            }

            int slot = maxVar;
            maxVar++;
            entries[slot] = type;
            return slot;
        }

        public void Free(int slot)
        {
            Tp tp = entries[slot];
            if (freeSlots.TryGetValue(tp, out Stack<int>? s))
            {
                s.Push(slot);
            }
            else
            {
                var stack = new Stack<int>();
                stack.Push(slot);
                freeSlots[tp] = stack;
            }
        }

        public int MaxVarCount()
        {
            return maxVar;
        }

        public VarFrame ToVarFrame()
        {
            return new VarFrame(entries.ToImmutableDictionary(), maxVar);
        }
    }

}