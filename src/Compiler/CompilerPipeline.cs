using System.Diagnostics;

namespace Stark.Compiler;

public sealed record CompilationInput(string SourceText, string? FilePath = null);

public sealed record CompilerOptions(
    bool EmitLlvmIr = false,
    bool ContinueAfterErrors = false,
    IModuleResolver? ModuleResolver = null,
    string? StopAfterPassId = null,
    LlvmTargetInfo? TargetInfo = null,
    bool QualifyModuleSymbols = false);

public readonly record struct ArtifactKey<T>(string Name);

public sealed class ArtifactStore
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public void Set<T>(ArtifactKey<T> key, T value)
    {
        _values[key.Name] = value;
    }

    public bool TryGet<T>(ArtifactKey<T> key, out T? value)
    {
        if (_values.TryGetValue(key.Name, out var boxed) && boxed is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public T GetRequired<T>(ArtifactKey<T> key)
    {
        if (!TryGet(key, out T? value) || value is null)
        {
            throw new InvalidOperationException($"Required compiler artifact '{key.Name}' is missing.");
        }

        return value;
    }
}

public enum CompilerPhase
{
    Parsing = 0,
    SyntaxModel = 1,
    Declarations = 2,
    ModuleResolution = 3,
    Symbols = 4,
    Semantics = 5,
    Typing = 6,
    Lowering = 7,
    CodeGeneration = 8
}

public enum PassExecutionMode
{
    SkipOnErrors,
    RunAlways
}

public enum PassExecutionStatus
{
    Executed,
    Skipped,
    Failed
}

public sealed record PassExecutionRecord(
    string PassId,
    CompilerPhase Phase,
    PassExecutionStatus Status,
    TimeSpan Duration,
    int DiagnosticsAdded);

public interface ICompilerPass
{
    string Id { get; }

    CompilerPhase Phase { get; }

    PassExecutionMode ExecutionMode { get; }

    IReadOnlyList<string> Dependencies { get; }

    void Execute(CompilerPassContext context);
}

public sealed class CompilerPassContext
{
    internal CompilerPassContext(CompilationState state)
    {
        State = state;
    }

    internal CompilationState State { get; }

    public CompilationInput Input => State.Input;

    public CompilerOptions Options => State.Options;

    public DiagnosticBag Diagnostics => State.Diagnostics;

    public ArtifactStore Artifacts => State.Artifacts;
}

internal sealed class CompilationState
{
    public CompilationState(CompilationInput input, CompilerOptions options)
    {
        Input = input;
        Options = options;
    }

    public CompilationInput Input { get; }

    public CompilerOptions Options { get; }

    public DiagnosticBag Diagnostics { get; } = new();

    public ArtifactStore Artifacts { get; } = new();

    public List<PassExecutionRecord> Executions { get; } = [];
}

public sealed class CompilationResult
{
    internal CompilationResult(CompilationState state)
    {
        Diagnostics = state.Diagnostics.Items;
        Artifacts = state.Artifacts;
        Executions = state.Executions;
    }

    public IReadOnlyList<CompilerDiagnostic> Diagnostics { get; }

    public ArtifactStore Artifacts { get; }

    public IReadOnlyList<PassExecutionRecord> Executions { get; }

    public bool Succeeded => Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

public sealed class CompilerPipelineBuilder
{
    private readonly List<ICompilerPass> _passes = [];

    public CompilerPipelineBuilder Add(ICompilerPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        if (_passes.Any(existing => string.Equals(existing.Id, pass.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"A compiler pass with id '{pass.Id}' is already registered.");
        }

        _passes.Add(pass);
        return this;
    }

    public CompilerPipeline Build()
    {
        return new CompilerPipeline(ResolveExecutionOrder(_passes));
    }

    private static IReadOnlyList<ICompilerPass> ResolveExecutionOrder(IReadOnlyList<ICompilerPass> passes)
    {
        var byId = passes.ToDictionary(pass => pass.Id, StringComparer.Ordinal);
        var inDegree = passes.ToDictionary(pass => pass.Id, static _ => 0, StringComparer.Ordinal);
        var dependents = passes.ToDictionary(pass => pass.Id, static _ => new List<string>(), StringComparer.Ordinal);
        var registrationOrder = passes
            .Select((pass, index) => (pass.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);

        foreach (var pass in passes)
        {
            foreach (var dependency in pass.Dependencies)
            {
                if (!byId.ContainsKey(dependency))
                {
                    throw new InvalidOperationException(
                        $"Compiler pass '{pass.Id}' depends on unknown pass '{dependency}'.");
                }

                inDegree[pass.Id]++;
                dependents[dependency].Add(pass.Id);
            }
        }

        var ready = new List<ICompilerPass>(
            passes.Where(pass => inDegree[pass.Id] == 0));
        var ordered = new List<ICompilerPass>(passes.Count);

        while (ready.Count > 0)
        {
            ready.Sort((left, right) =>
            {
                var phase = left.Phase.CompareTo(right.Phase);
                if (phase != 0)
                {
                    return phase;
                }

                return registrationOrder[left.Id].CompareTo(registrationOrder[right.Id]);
            });

            var current = ready[0];
            ready.RemoveAt(0);
            ordered.Add(current);

            foreach (var dependent in dependents[current.Id])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    ready.Add(byId[dependent]);
                }
            }
        }

        if (ordered.Count != passes.Count)
        {
            throw new InvalidOperationException("Compiler pass graph contains a dependency cycle.");
        }

        return ordered;
    }
}

public sealed class CompilerPipeline
{
    private readonly IReadOnlyList<ICompilerPass> _passes;

    internal CompilerPipeline(IReadOnlyList<ICompilerPass> passes)
    {
        _passes = passes;
    }

    public CompilationResult Run(CompilationInput input, CompilerOptions? options = null)
    {
        var state = new CompilationState(input, options ?? new CompilerOptions());
        var context = new CompilerPassContext(state);

        foreach (var pass in _passes)
        {
            if (state.Diagnostics.HasErrors && pass.ExecutionMode == PassExecutionMode.SkipOnErrors)
            {
                state.Executions.Add(new PassExecutionRecord(
                    pass.Id,
                    pass.Phase,
                    PassExecutionStatus.Skipped,
                    TimeSpan.Zero,
                    0));

                if (string.Equals(state.Options.StopAfterPassId, pass.Id, StringComparison.Ordinal))
                {
                    break;
                }

                continue;
            }

            var diagnosticsBefore = state.Diagnostics.Count;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                pass.Execute(context);
                stopwatch.Stop();

                state.Executions.Add(new PassExecutionRecord(
                    pass.Id,
                    pass.Phase,
                    PassExecutionStatus.Executed,
                    stopwatch.Elapsed,
                    state.Diagnostics.Count - diagnosticsBefore));

                if (string.Equals(state.Options.StopAfterPassId, pass.Id, StringComparison.Ordinal))
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                state.Diagnostics.Error(
                    "STK9999",
                    $"Pass '{pass.Id}' crashed: {ex.Message}",
                    pass.Id);

                state.Executions.Add(new PassExecutionRecord(
                    pass.Id,
                    pass.Phase,
                    PassExecutionStatus.Failed,
                    stopwatch.Elapsed,
                    state.Diagnostics.Count - diagnosticsBefore));

                if (!state.Options.ContinueAfterErrors)
                {
                    break;
                }
            }
        }

        return new CompilationResult(state);
    }
}
