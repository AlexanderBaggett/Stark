using Stark.Compiler;
using Stark.Parsing;

namespace compiler.PipelineTests;

internal static class CompilerPipelineTestSupport
{
internal static IReadOnlyList<System.Numerics.BigInteger> CollectMirIntegerConstants(MidLevelIrFunction function)
{
    var values = new List<System.Numerics.BigInteger>();

    foreach (var block in function.Blocks)
    {
        foreach (var statement in block.Statements)
        {
            if (statement.Value is null)
            {
                continue;
            }

            switch (statement.Value)
            {
                case MidLevelIrUseRValue { Operand: MidLevelIrIntegerConstantOperand integerOperand }:
                    values.Add(integerOperand.Value);
                    break;
                case MidLevelIrConvertRValue { Operand: MidLevelIrIntegerConstantOperand convertedOperand }:
                    values.Add(convertedOperand.Value);
                    break;
                case MidLevelIrInsertFieldRValue { Value: MidLevelIrIntegerConstantOperand fieldOperand }:
                    values.Add(fieldOperand.Value);
                    break;
            }
        }

        if (block.Terminator.Value is MidLevelIrIntegerConstantOperand operand)
        {
            values.Add(operand.Value);
        }
    }

    return values;
}


internal static StarkPackageModuleManifest WithEffectiveLegacyCompilerSectionCopies(StarkPackageModuleManifest module)
{
    return module with
    {
        TypedInterface = module.EffectiveTypedInterface,
        CompilerFacts = module.EffectiveCompilerFacts,
        GenericTemplates = module.EffectiveGenericTemplates
    };
}
}

internal sealed class ThrowingPass : ICompilerPass
{
    public string Id => "throwing-pass";

    public CompilerPhase Phase => CompilerPhase.Parsing;

    public PassExecutionMode ExecutionMode => PassExecutionMode.RunAlways;

    public IReadOnlyList<string> Dependencies => [];

    public void Execute(CompilerPassContext context)
    {
        throw new InvalidOperationException("boom");
    }
}


internal sealed class DocumentOnlyModuleResolver : IModuleSourceResolver, IModuleDocumentResolver
{
    private readonly Dictionary<string, LoadedModuleDocument> _documents;

    public DocumentOnlyModuleResolver(params LoadedModuleDocument[] documents)
    {
        _documents = documents.ToDictionary(static document => document.Reference.ModuleName, StringComparer.Ordinal);
    }

    public int SourceLoadAttempts { get; private set; }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        if (_documents.TryGetValue(moduleName, out var document))
        {
            module = document.Reference;
            return true;
        }

        module = default!;
        return false;
    }

    public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
    {
        SourceLoadAttempts++;
        sourceText = string.Empty;
        filePath = null;
        return false;
    }

    public bool TryLoadModuleDocument(ResolvedModuleReference module, LlvmTargetInfo? targetInfo, out LoadedModuleDocument document)
    {
        _ = targetInfo;
        return _documents.TryGetValue(module.ModuleName, out document!);
    }
}
