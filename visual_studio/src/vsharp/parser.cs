using System.Net.Http.Headers;
using Microsoft.VisualBasic;

namespace VSharp
{

    class ParserError(MetaInfo info, string message) : Exception(message)
    {
        public MetaInfo Info { get; } = info;
    }
   
    public class Parser(List<Token> tokens, string sourceFile)
    {
        private readonly List<Token> _tokens = tokens;
        readonly string sourceFile;
        private int _position = 0;

        public ProgramNode Parse()
        {
            List<ASTNode> statements = [];
            var meta = Info();
            while (Peek().Type != TokenType.EndOfInput)
            {
                statements.Add(ParseStatement());
            }
            
            return new ProgramNode(statements, meta.Finish());
        }

        private class InfoBuilder(int start, Parser instance)
        {
            public int Start = start;
            private readonly Parser instance = instance;
            
            public MetaInfo Finish()
            {
                return new MetaInfo(Start, instance.Current().End, instance.sourceFile);
            }
        }

        private InfoBuilder Info() 
        {
            return new InfoBuilder(Peek().Pos, this);
        }

        private ASTNode ParseStatement()
        {
            Token current = Peek();
            return current.Type switch
            {
                TokenType.KeywordSet => ParseSetStatement(),
                TokenType.KeywordWhile => ParseWhileStatement(),
                TokenType.KeywordFunc => ParseFuncStatement(),
                TokenType.KeywordFor => ParseForLoop(),
                TokenType.KeywordReturn => ParseReturn(),
                TokenType.KeywordBreak => ParseBreak(),
                TokenType.KeywordContinue => ParseContinue(),
                TokenType.KeywordType => ParseTypeStatement(),
                TokenType.KeywordImport => ParseImport(),
                _ => ParseExprStatemet(),//allow for expressions to be statements in this case:
                                                          //if statements and function calls are expresions
            };
        }

        private ExprStatement ParseExprStatemet()
        {
            var expr = ParseExpression();
            return new ExprStatement(expr.Info, expr);
        }

        private ParserError Error(InfoBuilder meta, string name)
        {
            return new ParserError(meta.Finish(), name);
        }

        private ParserError Error(string message)
        {
            return new ParserError(Info().Finish(), message);
        }

        private TypeStatement ParseTypeStatement()
        {
            var meta = Info();
            Consume(TokenType.KeywordType, "Expected the type keyword");
            string name = Consume(TokenType.Identifier, "Expected type name").Value;
            string[] generics = [];
            if (Peek().Type == TokenType.Less)
            {
                generics = ParseGenerics();
            }
            Consume(TokenType.Assignment, "Exepected `=` operator");
            VType type = ParseType();

            return new TypeStatement(meta.Finish()) {
                Generics = generics,
                Name = name,
                Type = type
            };
        }

        private Return ParseReturn()
        {
            var meta = Info();
            Consume(TokenType.KeywordReturn, "Expected return keyowrd");
            if (Peek().Type == TokenType.RightBrace || Peek().Type == TokenType.EndOfInput)
            {
                return new Return(meta.Finish()) { Expr = null };
            }  else {
                var expr =  ParseExpression();
                return new Return(meta.Finish()) { Expr = expr};
            }
        }
        
        private Break ParseBreak()
        {
            var meta = Info();
            Consume(TokenType.KeywordBreak, "Expected break keyowrd");
            if (Peek().Type == TokenType.RightBrace || Peek().Type == TokenType.EndOfInput)
            {
                return new Break(meta.Finish()) { Expr = null };
            }  else {
                Expression value = ParseExpression();
                return new Break(meta.Finish()) { Expr = value };
            }
        }

        private Continue ParseContinue()
        {
            var meta = Info();
            Consume(TokenType.KeywordContinue, "Expected continue keyword");
            return new Continue(meta.Finish());
        }

        private ForLoop ParseForLoop()
        {
            var meta = Info();
            Consume(TokenType.KeywordFor, "Expected keyword for");
            Consume(TokenType.LeftParen, "Expected ( after for");
            string name = Consume(TokenType.Identifier, "Expected itemname in for loop").Value;
            Consume(TokenType.KeywordIn, "Expected `in`");
            Expression parent = ParseExpression();
            Consume(TokenType.RightParen, "Expected ) after for");
            Expression body = ParseBlockNode();
            return new ForLoop(meta.Finish()) { Body = body, ItemName = name, Parent = parent };
        }
        private Expression ParseArray() 
        {
            var meta = Info();
            Consume(TokenType.SquareOpen, "");
            if (Peek().Type == TokenType.SquareClose) 
            {
                Consume(TokenType.SquareClose, "Expeced `]`");
                return new ConstArray([], meta.Finish());
            }

            if (Peek2().Type == TokenType.Assignment)
            {
                return ParseObject(meta);
            }

            List<Expression> expressions = []; 

            while (true) 
            {
                expressions.Add(ParseExpression());
                switch (NextToken().Type) 
                {
                    case TokenType.Comma:
                        continue;
                    case TokenType.SquareClose:
                        return new ConstArray(expressions, meta.Finish());
                    default:
                        throw Error("Expected `,` or `]`");
                }
            }
        }

        private ConstObject ParseObject(InfoBuilder meta) 
        {
            Dictionary<object, Expression> entries = [];
            while (true)
            {
                object key = ParseExpression() switch {
                    IdentifierNode n => n.Name,
                    ConstString s => s.Value,
                    ConstInt i => i.Value,
                    _ => throw Error(meta, "Invalid key")
                };
                Consume(TokenType.Assignment, "Expected =");
                Expression value = ParseExpression();
                entries[key] = value;
                switch (NextToken().Type)
                {
                    case TokenType.Comma:
                        continue;
                    case TokenType.SquareClose:
                        return new ConstObject(meta.Finish()) { Entries = entries };
                    default:
                        throw Error("Expected , or ]");
                }
            }
        }


        private ASTNode ParseFuncStatement()
        {
            var meta = Info();
            Consume(TokenType.KeywordFunc, "Expected 'func' keyword");

            string[] generics = [];
            if (Peek().Type == TokenType.Less)
            {
                generics = ParseGenerics();
            }

            Expression identifier = ParseCall(false); //disallow invoke expressions to be art of this
            var args = ParseArgs();

            VType? returnType = null;
            if (Peek().Type == TokenType.Colon) 
            {
                NextToken();
                returnType = ParseType();
            }
            
            BlockNode block = ParseBlockNode();
            ConstFunction func = new(meta.Finish())
            {
                Args = args,
                Body = block,
                ReturnType = returnType,
                Generics = generics,
            };

            return identifier switch
            {
                IdentifierNode i => new SetStatementNode
                (
                    i.Name,
                    func,
                    meta.Finish()
                ),
                Indexing idx => new IndexAssignment(meta.Finish())
                {
                    Index = idx.Index,
                    Parent = idx.Parent,
                    Value = func
                },
                MethodCall pa => new PropertyAssignment(meta.Finish())
                {
                    Parent = pa.Parent,
                    Name = pa.Name,
                    Value = func
                },
                _ => throw Error(meta, "Cannot assign to provided expression"),
            };
        }

        private string[] ParseGenerics()
        {
            Consume(TokenType.Less, "Expected generic definitions");
            if (Peek().Type == TokenType.Greater)
            {
                return [];
            }

            List<string> generic_names = [];
            while(true)
            {
                generic_names.Add(Consume(TokenType.Identifier, "Expected generic name").Value);
                Token next = NextToken();
                switch (next.Type)
                {
                case TokenType.Comma:
                    continue;
                case TokenType.Greater:
                    return [..generic_names];
                default:
                    throw Error("Expected , or >");
                }
            }
        }

        private List<(string, VType?)> ParseArgs()
        {
            List<(string, VType?)> args = [];
            Consume(TokenType.LeftParen, "Expected '(' after 'func'");

            if (Peek().Type== TokenType.RightParen)
            {
                NextToken();
                return args;
            }

            while (true)
            {
                string name = Consume(TokenType.Identifier, "Expected argument name").Value;
                VType? type = null;
                if (Peek().Type == TokenType.Colon) 
                {
                    NextToken();
                    type = ParseType();
                }
                args.Add((name, type));

                Token next = NextToken();
                switch (next.Type) {
                case TokenType.Comma:
                    continue;
                case TokenType.RightParen:
                    return args;
                default:
                    throw Error("Unexpected token");
                }
            }
        }

        private	VType ParseType()
        {
            var meta = Info();
            Token next = NextToken();
            VType type;
            switch (next.Type) 
            {
            case TokenType.SquareOpen:
                type = ParseArrayOrObjectType(meta);
                break;
            case TokenType.KeywordFunc:
                type = ParseFunctionType(meta);
                break;
            case TokenType.LeftParen:
                type = ParseType();
                Consume(TokenType.RightParen, "Expected )");
                break;
            case TokenType.Identifier:
                List<string> identifiers = [next.Value];
                while(Peek().Type == TokenType.Dot) 
                {
                    NextToken();
                    identifiers.Add(Consume(TokenType.Identifier, "Expected identifier").Value);                    
                }
                VType[] generics = [];
                if (Peek().Type == TokenType.Less)
                {
                    generics = ParseTypeArguments();
                }

                type = new VType.Normal(identifiers.ToArray(), generics, meta.Finish());
                break;
            default:
                throw Error($"Invalid character while parsing a type {next}");
            }
            
            if (Peek().Type == TokenType.Or)
            {
                NextToken();
                return type.Join(ParseType());
            }
            if (Peek().Type == TokenType.And)
            {
                NextToken();
                return type.Intersect(ParseType());
            }
            return type;
        }

        private VType ParseFunctionType(InfoBuilder meta)
        {
            VType[] types = ParseTypeFuntionArgs();
            Consume(TokenType.Colon, "Expected return type");
            VType returnType = ParseType();

            return new VType.Func(types, returnType, meta.Finish());
        }

        private VType[] ParseTypeFuntionArgs()
        {
            Consume(TokenType.LeftParen, "Expected (");

            if (Peek().Type == TokenType.RightParen)
            {
                NextToken();
                return [];
            } 
            List<VType> types = [];

            while(true)
            {
                types.Add(ParseType());
                Token next = NextToken();
                switch (next.Type)
                {
                case TokenType.Comma:
                    continue;
                case TokenType.RightParen:
                    return [.. types];
                default:
                    throw Error("Expected , or )");
                }
            }
        }

        private VType[] ParseTypeArguments()
        {
            Consume(TokenType.Less, "Expected type arguments");
            List<VType> types = [];

            while(true) 
            {
                types.Add(ParseType());
                Token next = NextToken();
                switch (next.Type){
                case TokenType.Comma:
                    continue;
                case TokenType.Greater:
                    return [.. types];
                default: 
                    throw Error("Expected , or >");
                }
            }
        }

        private VType ParseArrayOrObjectType(InfoBuilder meta)
        {
            if (Peek().Type == TokenType.SquareClose)
            {
                return new VType.Object([], meta.Finish());
            }
            if (Peek2().Type == TokenType.Colon) 
            {
                //object
                Dictionary<string, VType> entries = [];
            
                while(true) {
                    string name = Consume(TokenType.Identifier, "Expected identifier").Value;
                    Consume(TokenType.Colon, "Expected colon in type defintion");
                    var type = ParseType();
                    entries[name] = type;
                
                    Token next = NextToken();
                    switch (next.Type) 
                    {
                    case TokenType.SquareClose:
                        return new VType.Object(entries, meta.Finish());
                    case TokenType.Comma:
                        continue;
                    default:
                        throw Error($"Expected ] or , and got {next.Value}");
                    }
                }
            } else {
                //array
                VType itemType = ParseType();
                Consume(TokenType.SquareClose, "");
                return new VType.Array(itemType, meta.Finish());
            }
           
        }


        private WhileStatementNode ParseWhileStatement()
        {
            var meta = Info();
            Consume(TokenType.KeywordWhile, "Expected 'while' keyword");
            Consume(TokenType.LeftParen, "Expected '(' after 'while'");
            var condition = ParseLogicalExpression();
            Consume(TokenType.RightParen, "Expected ')' after condition");
            var trueBlock = ParseBlockNode();
            return new WhileStatementNode(meta.Finish())
            {
                Condition = condition,
                TrueBlock = trueBlock
            };
        }

        private IfNode ParseIfStatement()
        {
            var meta = Info();
            Consume(TokenType.KeywordIf, "Expected 'if' keyword");
            Consume(TokenType.LeftParen, "Expected '(' after 'if'");
            var condition = ParseLogicalExpression();
            Consume(TokenType.RightParen, "Expected ')' after condition");
            var trueBlock = ParseBlockNode();
            var ifNode = new IfNode(meta.Finish())
            {
                Condition = condition,
                TrueBlock = trueBlock,
                FalseBlock = new BlockNode([], Info().Finish())
            };
            if (Peek().Type == TokenType.KeywordElse)
            {
                Consume(TokenType.KeywordElse, "Expected 'else' keyword");
                var falseBlock = ParseBlockNode();
                ifNode.FalseBlock = falseBlock;
                ifNode.Info = meta.Finish();
            }
            return ifNode;
        }


        private Expression ParseLogicalExpression()
        {
            var meta = Info();
            Expression node = ParseComparison();

            while (Peek().Type == TokenType.LogicalOr || Peek().Type == TokenType.LogicalAnd)
            {
                Token logicalOp = NextToken();
                Expression right = ParseComparison();
                node = new LogicalNode(node, logicalOp, right, meta.Finish());
            }

            return node;
        }

        private Expression ParseComparison()
        {
            var meta = Info();
            Expression node = ParseTerm();

            if (Peek().Type == TokenType.Greater || Peek().Type == TokenType.Less ||
                Peek().Type == TokenType.Equal || Peek().Type == TokenType.NotEqual ||
                Peek().Type == TokenType.GreaterEqual || Peek().Type == TokenType.LessEqual)
            {
                Token comparisonOp = NextToken();
                Expression right = ParseComparison();
                node = new BinaryOperationNode(node, comparisonOp.Value, right, meta.Finish());
            }

            return node;
        }

        private BlockNode ParseBlockNode()
        {
            var meta = Info();
            List<ASTNode> statements = [];

            Consume(TokenType.LeftBrace, "Expected '{'");

            while (!IsAtEnd() && Peek().Type != TokenType.RightBrace)
            {
                statements.Add(ParseStatement());
            }

            Consume(TokenType.RightBrace, "Expected '}'");

            return new BlockNode(statements, meta.Finish());
        }

        private ASTNode ParseSetStatement()
        {
            var meta = Info();
            Consume(TokenType.KeywordSet, "Expected 'set' keyword.");
            Expression assignee = ParseExpression();
            Consume(TokenType.Assignment, "Expected '=' after variable name.");
            Expression expression = ParseExpression();
            return assignee switch
            {
                IdentifierNode identifier => new SetStatementNode(identifier.Name, expression, meta.Finish()),
                PropertyAccess pa => new PropertyAssignment(meta.Finish()) { Name = pa.Name, Parent = pa.Parent, Value = expression },
                Indexing indexing => new IndexAssignment(meta.Finish()) { Index = indexing.Index, Parent = indexing.Parent, Value = expression },
                _ => throw Error(meta, "Invalid syntax cannot set expr on the left side"),
            };
        }


        private Expression ParseExpression()
        {
            return ParseComparison();
        }

        private Expression ParseTerm()
        {
            var meta = Info();
            Expression node = ParseFactor();

            if (Peek().Type == TokenType.Operator && (Peek().Value == "+" || Peek().Value == "-"))
            {
                Token operatorToken = NextToken();
                Expression right = ParseTerm();
                node = new BinaryOperationNode(node, operatorToken.Value, right, meta.Finish());
            }

            return node;
        }

        private Expression ParseFactor()
        {
            var meta = Info();
            Expression node = ParseCall();

            if (Peek().Type == TokenType.Operator && (Peek().Value == "*" || Peek().Value == "/"))
            {
                Token operatorToken = NextToken();
                Expression right = ParseFactor();
                node = new BinaryOperationNode(node, operatorToken.Value, right, meta.Finish());
            }

            return node;
        }

        private Expression ParseCall(bool allowCalls = true)
        {
            var meta = Info();
            Expression node = ParsePrimary();


            while ((Peek().Type == TokenType.LeftParen && allowCalls) || Peek().Type == TokenType.Dot || Peek().Type == TokenType.SquareOpen || Peek().Type == TokenType.KeywordIn || Peek().Type == TokenType.KeywordIs)
            {
                Token next = Peek();
                if (next.Type == TokenType.Dot)
                {
                    NextToken();
                    string name = Consume(TokenType.Identifier, "").Value;
                    node = new PropertyAccess(meta.Finish()) { Parent = node, Name = name};
                } 

                if (next.Type == TokenType.KeywordIn)
                {
                    NextToken();
                    Expression parent = ParseExpression();
                    node = new HasElementCheck(meta.Finish()) { Item = node, Container = parent };
                }

                if (next.Type == TokenType.KeywordIs)
                {
                    NextToken();
                    VType type = ParseType();
                    node = new TypeCheck(meta.Finish()) { Item = node, Type = type };
                }

                if (next.Type == TokenType.LeftParen)
                {
                    List<Expression> args = ParseCallingArgs();
                    if (node is PropertyAccess n)
                    {
                        node = new MethodCall(meta.Finish()) { Args = args, Name = n.Name, Parent = n.Parent };
                    } else 
                    {
                        node = new Invokation(meta.Finish()) { Args = [..args], Parent = node };
                    }
                }

                if (next.Type == TokenType.SquareOpen)
                {
                    NextToken();
                    Expression index = ParseExpression();
                    Consume(TokenType.SquareClose, "Expected `]`");
                    node = new Indexing(meta.Finish()) { Parent = node, Index = index };
                }
               
            }

            return node;
        }

        private List<Expression> ParseCallingArgs()
        {
            Consume(TokenType.LeftParen, "Expected (");

            if (Peek().Type == TokenType.RightParen)
            {
                NextToken();
                return [];
            }

            List<Expression> arguments = [];
            while (true)
            {
                arguments.Add(ParseExpression());
                Token next = NextToken();
                switch (next.Type) 
                {
                    case TokenType.Comma:
                        continue;
                    case TokenType.RightParen:
                        return arguments;
                    default:
                        throw Error($"Expected , or ) but got {next}");
                }
            }
        }


        private Expression ParsePrimary()
        {
            var meta = Info();
            Token current = Peek();

            switch (current.Type)
            {
                case TokenType.KeywordTrue:
                    NextToken();
                    return new ConstBool(meta.Finish()) { Value = true };
                case TokenType.KeywordFalse:
                    NextToken();
                    return new ConstBool(meta.Finish()) { Value = false };
                case TokenType.IntegerLiteral:
                    NextToken();
                    return new ConstInt(meta.Finish()) { Value = int.Parse(current.Value)};
                case TokenType.FloatLiteral:
                    NextToken();
                    return new ConstDouble(meta.Finish()) { Value = double.Parse(current.Value) };
                case TokenType.StringLiteral:
                    NextToken();
                    return new ConstString(meta.Finish()) { Value = current.Value };
                case TokenType.SquareOpen:
                    return ParseArray();
                case TokenType.Identifier:
                    NextToken();
                    return new IdentifierNode(current.Value, meta.Finish());
                case TokenType.KeywordIf:
                    return ParseIfStatement();
                case TokenType.KeywordFunc:
                    return ParseAnonymousFunc();
                case TokenType.ExclamationMark:
                    NextToken();
                    return new Not(meta.Finish()) { Value = ParseCall() };
                case TokenType.LeftParen:
                    NextToken();
                    Expression expr = ParseExpression();
                    Consume(TokenType.RightParen, "Expected closing brace");
                    return expr;
                default:
                    throw Error(meta, $"Unexpected token: {current}");
            }
        }

        private ImportStatement ParseImport() 
        {
            Consume(TokenType.KeywordImport, "Expected import keywords");

            var meta = Info();
            if (Peek().Type == TokenType.LeftBrace) 
            {
                Consume(TokenType.LeftBrace, "Expected left brace");
                var symbols = ParseImportNames();

                Consume(TokenType.KeywordFrom, "Expected from keyword");
                var source = ParseImportSource();

                return new ImportStatement.Selection(meta.Finish()) {
                    Source = source,
                    Symbols = symbols
                };

            } else 
            {
                var source = ParseImportSource();
                string name = source.DefaultName();
                if (Peek().Type == TokenType.KeywordAs) 
                {
                    NextToken();
                    name = Consume(TokenType.Identifier, "Expected identifier").Value;
                }

                return new ImportStatement.Binding(meta.Finish()) {
                    name = name,
                    Source = source
                };
            }
        }

        private ImportSource ParseImportSource() 
        {
            var tk = Peek();

            switch(tk.Type) 
            {
            case TokenType.StringLiteral:
                NextToken();
                return new ImportSource.Path(tk.Value);
            case TokenType.Identifier:
                return new ImportSource.Namespace(ParseSymbol());
            default:
                throw Error("Invalid token: " + tk.Type	);
            }
        }

        private string ParseSymbol() {
            var parts = new List<string>();
            while(true) {
                parts.Add(Consume(TokenType.Identifier, "expected identifier").Value);

                if (Peek().Type != TokenType.Dot) {
                    return string.Join(" ", parts);
                }

                Consume(TokenType.Dot, "Expected dot");
            }
        }

        private string[] ParseImportNames() {
            if (Peek().Type == TokenType.RightBrace) {
                return [];
            }
            List<string> symbols = [];
            while(true) {
                string symbol = ParseSymbol();  
                symbols.Add(symbol);
                
                var next = NextToken();
                switch(next.Type) {
                case TokenType.Comma:
                    continue;
                case TokenType.RightBrace:
                    return [.. symbols];
                default:
                    throw Error("Expected , or }");
                }
            } 
        }

        private ConstFunction ParseAnonymousFunc() 
        {
            var meta = Info();
            Consume(TokenType.KeywordFunc, "Expected func keyword");
            string[] generics = [];
            if (Peek().Type == TokenType.Less)
            {
                generics = ParseGenerics();
            }

            var args = ParseArgs();
            VType? returnType = null;
            if (Peek().Type == TokenType.Colon) 
            {
                NextToken();
                returnType = ParseType();
            }
            Expression body = ParseBlockNode();

            return new ConstFunction(meta.Finish()) { Args = args, Body = body, ReturnType = returnType, Generics = generics };
        }

        private Token Consume(TokenType type, string errorMessage)
        {
            if (Peek().Type == type)
            {
                return NextToken();
            }

            throw Error(errorMessage + Peek().Type);
        }

        private Token Peek()
        {
            if (IsAtEnd())throw new Exception("Ran out of tokens");
            return _tokens[_position];
        }

        private Token Peek2()
        {
            if (_position + 1 >= _tokens.Count)
            {
                throw new Exception("Ran out of tokens");
            }
            return _tokens[_position + 1];
        }

        private Token NextToken()
        {
            if (!IsAtEnd()) _position++;
            return _tokens[_position - 1];
        }


        private Token Current()
        {
            if (_position == 0) {
                return new Token(TokenType.EndOfInput, "", 0);
            }
           return  _tokens[_position - 1];
        }
        private bool IsAtEnd()
        {
            return _position >= _tokens.Count;
        }
    }

}
