using System.Text;

namespace Stark.Compiler;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum CompilerLogVerbosity
{
    Normal,
    Verbose
}

public enum CompilerLogKind
{
    Pipeline,
    Symbol,
    Decision,
    Gap
}

public enum CompilerLogOutcome
{
    None,
    Continued,
    Rewritten,
    Dropped,
    Bypassed,
    Skipped,
    Stopped,
    Unsupported,
    Emitted
}

public sealed record CompilerLogEntry(
    DiagnosticSeverity Severity,
    CompilerLogVerbosity Verbosity,
    CompilerLogKind Kind,
    string Category,
    string EventId,
    string Message,
    string Stage,
    string SymbolName,
    string Operation,
    SourceLocation Location,
    CompilerLogOutcome Outcome,
    IReadOnlyDictionary<string, string> Data)
{
    public override string ToString()
    {
        return $"{Location}: {Severity.ToString().ToLowerInvariant()} {Kind.ToString().ToLowerInvariant()} {Category}:{EventId} [{Stage}]/{Operation} ({SymbolName}): {Message}";
    }

    public string FormatForConsole(CompilerLogVerbosity outputVerbosity)
    {
        var metadata = new List<string>
        {
            FormatSeverity(Severity),
            Kind.ToString().ToLowerInvariant()
        };

        if (!string.Equals(Category, Kind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            metadata.Add(Category);
        }

        metadata.Add($"stage={Stage}");

        if (!ShouldSuppressSymbol())
        {
            metadata.Add($"symbol={SymbolName}");
        }

        if (ShouldIncludeLocation())
        {
            metadata.Add($"src={FormatShortLocation(Location)}");
        }

        if (outputVerbosity >= CompilerLogVerbosity.Verbose || Kind == CompilerLogKind.Gap)
        {
            metadata.Add($"op={Operation}");
        }

        if (Outcome != CompilerLogOutcome.None)
        {
            metadata.Add($"outcome={Outcome.ToString().ToLowerInvariant()}");
        }

        if (Data.TryGetValue("feature", out var feature))
        {
            metadata.Add($"feature={feature}");
        }

        if (outputVerbosity >= CompilerLogVerbosity.Verbose)
        {
            foreach (var field in Data.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                if (string.Equals(field.Key, "feature", StringComparison.Ordinal))
                {
                    continue;
                }

                metadata.Add($"{field.Key}={field.Value}");
            }
        }

        return $"{Message} [{string.Join(" ", metadata)}]";
    }

    private static string FormatSeverity(DiagnosticSeverity severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Info => "info",
            DiagnosticSeverity.Warning => "warn",
            DiagnosticSeverity.Error => "error",
            _ => severity.ToString().ToLowerInvariant()
        };
    }

    private bool ShouldSuppressSymbol()
    {
        return Kind == CompilerLogKind.Pipeline
               && (string.Equals(SymbolName, "<compilation>", StringComparison.Ordinal)
                   || (Location.Line == 1 && Location.Column == 1));
    }

    private bool ShouldIncludeLocation()
    {
        if (Kind == CompilerLogKind.Pipeline)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(Location.FilePath) || Location.Line != 1 || Location.Column != 1;
    }

    private static string FormatShortLocation(SourceLocation location)
    {
        var fileName = string.IsNullOrWhiteSpace(location.FilePath)
            ? null
            : Path.GetFileName(location.FilePath);

        return string.IsNullOrWhiteSpace(fileName)
            ? $"{location.Line}:{location.Column}"
            : $"{fileName}:{location.Line}:{location.Column}";
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

public sealed record SourceLocation(string? FilePath, int Line, int Column, int EndLine = 0, int EndColumn = 0)
{
    public static SourceLocation Synthetic(string? filePath = null)
    {
        return new SourceLocation(filePath, 1, 1);
    }

    public int ResolvedEndLine => EndLine > 0 ? EndLine : Line;

    public int ResolvedEndColumn => EndColumn > 0 ? EndColumn : Column;

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
    private CompilerLogContext? _context;

    public IReadOnlyList<CompilerLogEntry> Items => _logs;

    public int Count => _logs.Count;

    public void Add(CompilerLogEntry log)
    {
        _logs.Add(log);
        CompilerLogOutput.Write(log);
    }

    public void AddRange(IEnumerable<CompilerLogEntry> logs)
    {
        _logs.AddRange(logs);
    }

    public IDisposable PushContext(
        string? stage = null,
        string? symbolName = null,
        string? operation = null,
        SourceLocation? location = null)
    {
        var previous = _context;
        _context = new CompilerLogContext(
            stage ?? previous?.Stage,
            symbolName ?? previous?.SymbolName,
            operation ?? previous?.Operation,
            location ?? previous?.Location);
        return new RestoreContext(this, previous);
    }

    public CompilerLogEntry Info(
        string category,
        string eventId,
        string message,
        string? stage = null,
        string? symbolName = null,
        string? operation = null,
        SourceLocation? location = null,
        IReadOnlyDictionary<string, string>? data = null,
        CompilerLogKind? kind = null,
        CompilerLogOutcome outcome = CompilerLogOutcome.None,
        CompilerLogVerbosity verbosity = CompilerLogVerbosity.Normal)
    {
        return Add(
            DiagnosticSeverity.Info,
            category,
            eventId,
            message,
            stage,
            symbolName,
            operation,
            location,
            data,
            kind,
            outcome,
            verbosity);
    }

    public CompilerLogEntry Warning(
        string category,
        string eventId,
        string message,
        string? stage = null,
        string? symbolName = null,
        string? operation = null,
        SourceLocation? location = null,
        IReadOnlyDictionary<string, string>? data = null,
        CompilerLogKind? kind = null,
        CompilerLogOutcome outcome = CompilerLogOutcome.None,
        CompilerLogVerbosity verbosity = CompilerLogVerbosity.Normal)
    {
        return Add(
            DiagnosticSeverity.Warning,
            category,
            eventId,
            message,
            stage,
            symbolName,
            operation,
            location,
            data,
            kind,
            outcome,
            verbosity);
    }

    public CompilerLogEntry Error(
        string category,
        string eventId,
        string message,
        string? stage = null,
        string? symbolName = null,
        string? operation = null,
        SourceLocation? location = null,
        IReadOnlyDictionary<string, string>? data = null,
        CompilerLogKind? kind = null,
        CompilerLogOutcome outcome = CompilerLogOutcome.None,
        CompilerLogVerbosity verbosity = CompilerLogVerbosity.Normal)
    {
        return Add(
            DiagnosticSeverity.Error,
            category,
            eventId,
            message,
            stage,
            symbolName,
            operation,
            location,
            data,
            kind,
            outcome,
            verbosity);
    }

    public CompilerLogEntry GapWarning(
        string category,
        string eventId,
        string message,
        string featureTag,
        string? reason = null,
        string? stage = null,
        string? symbolName = null,
        string? operation = null,
        SourceLocation? location = null,
        CompilerLogOutcome outcome = CompilerLogOutcome.Unsupported,
        CompilerLogVerbosity verbosity = CompilerLogVerbosity.Normal,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var mergedData = MergeData(
            data,
            ("feature", featureTag),
            ("reason", reason));

        return Warning(
            category,
            eventId,
            message,
            stage,
            symbolName,
            operation,
            location,
            mergedData,
            kind: CompilerLogKind.Gap,
            outcome: outcome,
            verbosity: verbosity);
    }

    private CompilerLogEntry Add(
        DiagnosticSeverity severity,
        string category,
        string eventId,
        string message,
        string? stage,
        string? symbolName,
        string? operation,
        SourceLocation? location,
        IReadOnlyDictionary<string, string>? data,
        CompilerLogKind? kind,
        CompilerLogOutcome outcome,
        CompilerLogVerbosity verbosity)
    {
        var resolvedStage = stage ?? _context?.Stage ?? "<unknown-stage>";
        var resolvedSymbolName = symbolName ?? _context?.SymbolName ?? "<unknown-symbol>";
        var resolvedOperation = operation ?? _context?.Operation ?? "<unknown-operation>";
        var resolvedLocation = location ?? _context?.Location ?? SourceLocation.Synthetic();
        var resolvedKind = kind ?? InferKind(category);
        var log = new CompilerLogEntry(
            severity,
            verbosity,
            resolvedKind,
            category,
            eventId,
            message,
            resolvedStage,
            resolvedSymbolName,
            resolvedOperation,
            resolvedLocation,
            outcome,
            data ?? CompilerLogData.Empty);
        Add(log);
        return log;
    }

    private static CompilerLogKind InferKind(string category)
    {
        return category switch
        {
            "pipeline" => CompilerLogKind.Pipeline,
            "symbol" => CompilerLogKind.Symbol,
            "decision" => CompilerLogKind.Decision,
            "gap" => CompilerLogKind.Gap,
            _ => CompilerLogKind.Decision
        };
    }

    private static IReadOnlyDictionary<string, string> MergeData(
        IReadOnlyDictionary<string, string>? existing,
        params (string Key, string? Value)[] fields)
    {
        if ((existing is null || existing.Count == 0) && fields.All(static field => string.IsNullOrWhiteSpace(field.Value)))
        {
            return CompilerLogData.Empty;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var field in existing)
            {
                merged[field.Key] = field.Value;
            }
        }

        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            merged[key] = value;
        }

        return merged.Count == 0 ? CompilerLogData.Empty : merged;
    }

    private sealed record CompilerLogContext(
        string? Stage,
        string? SymbolName,
        string? Operation,
        SourceLocation? Location);

    private sealed class RestoreContext(CompilerLogBag owner, CompilerLogContext? previous) : IDisposable
    {
        public void Dispose()
        {
            owner._context = previous;
        }
    }
}

internal static class CompilerLogOutput
{
    private static readonly AsyncLocal<CompilerLogOutputScope?> CurrentScope = new();
    private static readonly object WriteGate = new();

    public static IDisposable Push(
        TextWriter writer,
        DiagnosticSeverity minimumSeverity,
        CompilerLogVerbosity maximumVerbosity = CompilerLogVerbosity.Normal,
        IReadOnlySet<string>? categories = null,
        IReadOnlySet<string>? stages = null,
        IReadOnlySet<CompilerLogKind>? kinds = null)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new CompilerLogOutputScope(writer, minimumSeverity, maximumVerbosity, categories, stages, kinds);
        return new RestoreScope(previous);
    }

    public static void Write(CompilerLogEntry log)
    {
        var scope = CurrentScope.Value;
        var writer = scope?.Writer ?? Console.Error;
        var minimumSeverity = scope?.MinimumSeverity ?? DiagnosticSeverity.Warning;
        var maximumVerbosity = scope?.MaximumVerbosity ?? CompilerLogVerbosity.Normal;

        if ((int)log.Severity < (int)minimumSeverity)
        {
            return;
        }

        if ((int)log.Verbosity > (int)maximumVerbosity)
        {
            return;
        }

        if (scope?.Categories is { Count: > 0 } categories
            && !categories.Contains(log.Category))
        {
            return;
        }

        if (scope?.Stages is { Count: > 0 } stages
            && !stages.Contains(log.Stage))
        {
            return;
        }

        if (scope?.Kinds is { Count: > 0 } kinds
            && !kinds.Contains(log.Kind))
        {
            return;
        }

        lock (WriteGate)
        {
            writer.WriteLine(log.FormatForConsole(maximumVerbosity));
        }
    }

    private sealed record CompilerLogOutputScope(
        TextWriter Writer,
        DiagnosticSeverity MinimumSeverity,
        CompilerLogVerbosity MaximumVerbosity,
        IReadOnlySet<string>? Categories,
        IReadOnlySet<string>? Stages,
        IReadOnlySet<CompilerLogKind>? Kinds);

    private sealed class RestoreScope(CompilerLogOutputScope? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentScope.Value = previous;
        }
    }
}
