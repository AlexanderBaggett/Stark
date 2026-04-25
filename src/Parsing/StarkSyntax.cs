using Antlr4.Runtime;

namespace Stark.Parsing;

public sealed record ParseDiagnostic(int Line, int Column, string Message);

public sealed record ParseResult(
    StarkParser.CompilationUnitContext Root,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    string SourceText)
{
    public int SyntaxErrorCount => Diagnostics.Count;
    public bool Succeeded => SyntaxErrorCount == 0;
}

public sealed record ExpressionParseResult(
    StarkParser.ExpressionContext Root,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    string SourceText)
{
    public int SyntaxErrorCount => Diagnostics.Count;
    public bool Succeeded => SyntaxErrorCount == 0;
}

public static class StarkSyntax
{
    public static ParseResult ParseCompilationUnit(string source)
    {
        var input = new AntlrInputStream(source);
        var lexer = new StarkLexer(input);
        var tokens = new CommonTokenStream(lexer);
        var parser = new StarkParser(tokens);
        var errors = new CountingErrorListener();

        lexer.RemoveErrorListeners();
        parser.RemoveErrorListeners();

        lexer.AddErrorListener(errors);
        parser.AddErrorListener(errors);

        var root = parser.compilationUnit();
        tokens.Fill();
        ValidateTextLiterals(tokens, errors);
        return new ParseResult(root, errors.Diagnostics, source);
    }

    public static ExpressionParseResult ParseExpression(string source)
    {
        var input = new AntlrInputStream(source);
        var lexer = new StarkLexer(input);
        var tokens = new CommonTokenStream(lexer);
        var parser = new StarkParser(tokens);
        var errors = new CountingErrorListener();

        lexer.RemoveErrorListeners();
        parser.RemoveErrorListeners();

        lexer.AddErrorListener(errors);
        parser.AddErrorListener(errors);

        var root = parser.expression();
        if (tokens.LA(1) != StarkParser.Eof)
        {
            var token = tokens.LT(1);
            errors.AddDiagnostic(
                token.Line,
                token.Column + 1,
                $"Unexpected text '{token.Text}' after the interpolation expression.");
        }

        tokens.Fill();
        ValidateTextLiterals(tokens, errors);
        return new ExpressionParseResult(root, errors.Diagnostics, source);
    }

    private static void ValidateTextLiterals(CommonTokenStream tokens, CountingErrorListener errors)
    {
        foreach (var token in tokens.GetTokens())
        {
            if (token.Type == StarkLexer.StringLiteral)
            {
                ValidateTextLiteral(token, TextLiteralKind.String, errors);
            }
            else if (token.Type == StarkLexer.CharacterLiteral)
            {
                ValidateTextLiteral(token, TextLiteralKind.Character, errors);
            }
        }
    }

    private static void ValidateTextLiteral(IToken token, TextLiteralKind kind, CountingErrorListener errors)
    {
        if (token.Text is null
            || TextLiteralDecoder.TryDecode(token.Text, kind, out _, out var diagnostic))
        {
            return;
        }

        errors.AddDiagnostic(
            token.Line,
            token.Column + 1 + diagnostic.Offset,
            diagnostic.Message);
    }

    private sealed class CountingErrorListener : IAntlrErrorListener<int>, IAntlrErrorListener<IToken>
    {
        private readonly List<ParseDiagnostic> _diagnostics = [];

        public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;

        public void AddDiagnostic(int line, int column, string message)
        {
            _diagnostics.Add(new ParseDiagnostic(line, column, message));
        }

        public void SyntaxError(
            TextWriter output,
            IRecognizer recognizer,
            IToken offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            _diagnostics.Add(new ParseDiagnostic(line, charPositionInLine + 1, msg));
        }

        public void SyntaxError(
            TextWriter output,
            IRecognizer recognizer,
            int offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            _diagnostics.Add(new ParseDiagnostic(line, charPositionInLine + 1, msg));
        }
    }
}
