// See https://aka.ms/new-console-template for more information
using VSharp;

void RunCode(string path) {
    Lexer lexer = new(File.ReadAllText(path));

    List<Token> tokens = lexer.Tokenize();

    Parser parser = new(tokens);
    ProgramNode program = parser.Parse();
    new Interpreter().Interpret(program);
}


RunCode("../../../examples/test.vshrp");