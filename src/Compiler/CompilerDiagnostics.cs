namespace Stark.Compiler;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record SourceLocation(string? FilePath, int Line, int Column)
{
    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return $"{Line}:{Column}";
        }

        return $"{FilePath}:{Line}:{Column}";
    }
}

public sealed record CompilerDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Stage = null,
    SourceLocation? Location = null)
{
    public override string ToString()
    {
        var prefix = Location is null ? string.Empty : $"{Location}: ";
        var stage = string.IsNullOrWhiteSpace(Stage) ? string.Empty : $" [{Stage}]";
        return $"{prefix}{Severity.ToString().ToLowerInvariant()} {Code}{stage}: {Message}";
    }
}

public sealed class DiagnosticBag
{
    private readonly List<CompilerDiagnostic> _diagnostics = [];

    public IReadOnlyList<CompilerDiagnostic> Items => _diagnostics;

    public bool HasErrors => _diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public int Count => _diagnostics.Count;

    public void Add(CompilerDiagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
    }

    public void AddRange(IEnumerable<CompilerDiagnostic> diagnostics)
    {
        _diagnostics.AddRange(diagnostics);
    }

    public CompilerDiagnostic Error(
        string code,
        string message,
        string? stage = null,
        SourceLocation? location = null)
    {
        var diagnostic = new CompilerDiagnostic(code, DiagnosticSeverity.Error, message, stage, location);
        Add(diagnostic);
        return diagnostic;
    }

    public CompilerDiagnostic Info(
        string code,
        string message,
        string? stage = null,
        SourceLocation? location = null)
    {
        var diagnostic = new CompilerDiagnostic(code, DiagnosticSeverity.Info, message, stage, location);
        Add(diagnostic);
        return diagnostic;
    }
}
