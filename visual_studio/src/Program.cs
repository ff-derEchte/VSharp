using System.Reflection;
using System.Reflection.Emit;
using VSharp;
using VSharpCompiler;

string Version = "0.2.1";
//Main(["run", "../../../../examples/test.vshrp"]);

string code = """
import { Console } from System
Console.WriteLine("Hello World")
""";

var lexer = new Lexer(code);
var tokens = lexer.Tokenize();

var node = new Parser(tokens, code).Parse();
var checker = new Checker([typeof(Console).Assembly]);
var name = new AssemblyName("project");
var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
var module = assemblyBuilder.DefineDynamicModule("x");

var result = await checker.CheckProgram(node, new Signature("main"), new SynchronizedModuleBuilder(module));
Console.WriteLine(result);
void Main(string[] args)
{
    if (args.Length == 0)
    {
        UsageDialog();
        return;
    }

    switch (args[0])
    {
        case "--version":
            Console.WriteLine("VSharp version: " + Version);
            break;
        case "run":
            if (args.Length < 2)
            {
                UsageDialog();
                return;
            }
            string fileName = args[1];
            RunFile(fileName);
            break;
    }
}

void DisplayError(string source, string fileName, string stage, MetaInfo info, string message)
{
    Console.WriteLine($"An error occured during {stage}:");
    int start = info.Start;
    for (; start > 0; start--)
    {
        if (source[start] == '\n')
        {
            break;
        }
    }

    int end = info.End;
    for (; end < source.Length; end++)
    {
        if (source[end] == '\n')
        {
            break;
        }
    }
    string section = source[start..end];
    Console.WriteLine(section);
    for (int i = -1; i != start - info.Start; i--)
    {
        Console.Write(" ");
    }
    for (int i = 1; i <= info.End - info.Start + 1; i++)
    {
        Console.Write("^");
    }
    Console.WriteLine();
    Console.WriteLine(message);
    int lineNumber = 1; //starting at 1 🤮
    for (int i = 0; i <= start; i++)
    {
        if (source[i] == '\n')
        {
            lineNumber++;
        }
    }
    Console.WriteLine($"{fileName},Line:{lineNumber},Col:{info.Start - start}-{info.End - start}");
}

void RunFile(string fileName)
{
    string input;
    try
    {
        input = File.ReadAllText(fileName);
    }
    catch (IOException e)
    {
        Console.WriteLine("Exception occured while reading the file: " + e);
        return;
    }
    Interpreter interpreter = new();

    try
    {
        Lexer lexer = new(input);
        List<Token> tokens = lexer.Tokenize();

        Parser parser = new(tokens, fileName);
        ProgramNode program = parser.Parse();
        interpreter.Interpret(program);
    }
    catch (ParserError e)
    {
        DisplayError(input, fileName, "Parsing", e.Info, e.Message);
    }
    catch (ExecutionError e)
    {
        DisplayError(input, fileName, "Execution", e.Info, e.Message);
    }
    catch (Exception e)
    {
        Console.WriteLine("Failed to execute program: " + e);
    }

}

void UsageDialog()
{
    Console.WriteLine("usage: ");
    Console.WriteLine(" --version          display your V# version");
    Console.WriteLine(" run  <file_name>   run the project");
}

