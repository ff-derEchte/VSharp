using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using VSharp;

string Version = "0.2.1";

    void Main(string[] args)
    {
        /*
        if (args.Length == 0)
        {
           UsageDialog();
           return; 
        }
        
        switch(args[0])
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
        */
        RunFile("../../../../examples/test.vshrp");
    }

void RunFile(string fileName)
{
    string input;
    try 
    {
        input = File.ReadAllText(fileName);
    } catch(IOException e)
    {
        Console.WriteLine("Exception occured while reading the file: " + e);
        return;
    }
    Interpreter interpreter = new();

    try 
    {
        Lexer lexer = new(input);
        List<Token> tokens = lexer.Tokenize();

        Parser parser = new(tokens);
        ProgramNode program = parser.Parse();
        interpreter.Interpret(program);
    } catch(Exception e)
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
