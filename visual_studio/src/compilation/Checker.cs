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
    }

    

    class Checker
    {

        public Dictionary<string, ImportNode> Imports = [];

        public Instruction CheckExpression(Expression expr, ScopedNames names)
        {
            return expr switch
            {
                ConstString s => new Instruction.ConstStr(s.Value),
                ConstDouble d => new Instruction.ConstDouble(d.Value),
                ConstBool b => new Instruction.ConstBool(b.Value),
                ConstArray array => new Instruction.ConstArray(array.Expressions.Select(it => CheckExpression(it, names)).ToArray()),
                IdentifierNode identifier => CheckIdentifier(identifier, names),
                _ => throw new Exception(""),
            };
        }

        public Instruction CheckIdentifier(IdentifierNode node, ScopedNames names)
        {
            if (names.Has(node.Name))
            {
                return new Instruction.Binding(node.Name);
            }
            if (Imports.ContainsKey(node.Name))
            {
            }

            throw new NotImplementedException();
        }
    }

}