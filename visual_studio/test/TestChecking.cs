namespace test;

using System.Reflection;
using System.Reflection.Emit;
using VSharp;
using VSharpCompiler;
public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        string code = """
        import { Console } from System
        Console.WriteLine("Hello World")
""";

        var lexer = new Lexer(code);
        var tokens = lexer.Tokenize();

        var node = new Parser(tokens, code).Parse();
        var checker = new Checker([typeof(Console).Assembly]);
        var name  = new AssemblyName("project");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var module = assemblyBuilder.DefineDynamicModule("x");

        var result = checker.CheckProgram(node, new Signature("main"), module);
    }


    private void runCode(string code) {

    }
}