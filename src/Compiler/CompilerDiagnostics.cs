namespace Stark.Compiler;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record CompilerLogEntry(
    DiagnosticSeverity Severity,
    string Category,
    string EventId,
    string Message,
    string Stage,
    string SymbolName,
    string Operation,
    SourceLocation Location,
    IReadOnlyDictionary<string, string> Data)
{
    public override string ToString()
    {
        return $"{Location}: {Severity.ToString().ToLowerInvariant()} {Category}:{EventId} [{Stage}]/{Operation} ({SymbolName}): {Message}";
    }
}

public static class CompilerLogData
{
    public static IReadOnlyDictionary<string, string> Empty { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Create(params (string Key, string? Value)[] fields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            values[key] = value;
        }

        return values.Count == 0 ? Empty : values;
    }
}

public sealed record SourceLocation(string? FilePath, int Line, int Column)
{
    public static SourceLocation Synthetic(string? filePath = null)
    {
        return new SourceLocation(filePath, 1, 1);
    }

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

    public CompilerDiagnostic Warning(
        string code,
        string message,
        string? stage = null,
        SourceLocation? location = null)
    {
        var diagnostic = new CompilerDiagnostic(code, DiagnosticSeverity.Warning, message, stage, location);
        Add(diagnostic);
        return diagnostic;
    }
}

public sealed class CompilerLogBag
{
    private readonly List<CompilerLogEntry> _logs = [];

    public IReadOnlyList<CompilerLogEntry> Items => _logs;

    public int Count => _logs.Count;

    public void Add(CompilerLogEntry log)
    {
        _logs.Add(log);
    }

    public void AddRange(IEnumerable<CompilerLogEntry> logs)
    {
        _logs.AddRange(logs);
    }

    public CompilerLogEntry Info(
        string category,
        string eventId,
        string message,
        string stage,
        string symbolName,
        string operation,
        SourceLocation location,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var log = new CompilerLogEntry(DiagnosticSeverity.Info, category, eventId, message, stage, symbolName, operation, location, data ?? CompilerLogData.Empty);
        Add(log);
        return log;
    }

    public CompilerLogEntry Warning(
        string category,
        string eventId,
        string message,
        string stage,
        string symbolName,
        string operation,
        SourceLocation location,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var log = new CompilerLogEntry(DiagnosticSeverity.Warning, category, eventId, message, stage, symbolName, operation, location, data ?? CompilerLogData.Empty);
        Add(log);
        return log;
    }

    public CompilerLogEntry Error(
        string category,
        string eventId,
        string message,
        string stage,
        string symbolName,
        string operation,
        SourceLocation location,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var log = new CompilerLogEntry(DiagnosticSeverity.Error, category, eventId, message, stage, symbolName, operation, location, data ?? CompilerLogData.Empty);
        Add(log);
        return log;
    }
}
