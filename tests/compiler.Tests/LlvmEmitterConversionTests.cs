using System.Numerics;
using Stark.Compiler;
using Stark.Parsing;

namespace compiler.Tests;

public sealed class LlvmEmitterConversionTests
{
    [Fact]
    public void FloatToIntegerConversionEmitsFptosi()
    {
        var llvm = EmitSingleConversion(
            StarkTypeSymbols.Integer(32),
            new SsaFloatConstant("3.5", StarkTypeSymbols.Float(32)));

        Assert.Contains("define", llvm);
        Assert.Contains("@Run()", llvm);
        Assert.Contains("fptosi float 3.5 to i32", llvm);
        Assert.Contains("ret i32", llvm);
    }

    [Fact]
    public void IntegerToRawPointerConversionEmitsInttoptr()
    {
        var targetType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false);
        var llvm = EmitSingleConversion(
            targetType,
            new SsaIntegerConstant(new BigInteger(1024), StarkTypeSymbols.Integer(64)));

        Assert.Contains("define", llvm);
        Assert.Contains("@Run()", llvm);
        Assert.Contains("inttoptr i64 1024 to ptr", llvm);
        Assert.Contains("ret ptr", llvm);
    }

    [Fact]
    public void RawPointerToIntegerConversionEmitsPtrtoint()
    {
        var sourceType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false);
        var llvm = EmitSingleConversion(
            StarkTypeSymbols.Integer(64),
            new SsaNullConstant(sourceType));

        Assert.Contains("define", llvm);
        Assert.Contains("@Run()", llvm);
        Assert.Contains("ptrtoint ptr null to i64", llvm);
        Assert.Contains("ret i64", llvm);
    }

    [Fact]
    public void RawPointerToRawPointerConversionIsLlvmNoOp()
    {
        var sourceType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: true);
        var targetType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false);
        var llvm = EmitSingleConversion(
            targetType,
            new SsaNullConstant(sourceType));

        Assert.Contains("define", llvm);
        Assert.Contains("@Run()", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw i8, ptr null, i64 0", llvm);
        Assert.DoesNotContain("ptrtoint", llvm);
        Assert.DoesNotContain("inttoptr", llvm);
        Assert.Contains("ret ptr null", llvm);
    }

    [Fact]
    public void AsciiToUnicodeReinterpretCastIsNotLowered()
    {
        var llvm = EmitSingleConversion(
            StarkTypeSymbols.Unicode,
            new SsaStringConstant("hello", StarkTypeSymbols.Ascii));

        Assert.DoesNotContain("define", llvm);
        Assert.Contains("@Run()", llvm);
        Assert.Contains("LLVM body emission fallback for Run: Unsupported SSA conversion from 'ascii' to 'unicode'.", llvm);
        Assert.Contains("declare fastcc noundef %stark_unicode @Run()", llvm);
    }

    [Fact]
    public void UnicodeToAsciiReinterpretCastIsNotLowered()
    {
        var llvm = EmitSingleConversion(
            StarkTypeSymbols.Ascii,
            new SsaStringConstant("\"\\u03B1\"", StarkTypeSymbols.Unicode));

        Assert.DoesNotContain("define", llvm);
        Assert.Contains("@Run()", llvm);
        Assert.Contains("LLVM body emission fallback for Run: Unsupported SSA conversion from 'unicode' to 'ascii'.", llvm);
        Assert.Contains("declare fastcc noundef %stark_ascii @Run()", llvm);
    }

    private static string EmitSingleConversion(
        StarkTypeSymbol targetType,
        SsaValue operand)
    {
        var effectModel = new FunctionEffectModel(
            "Demo",
            new Dictionary<string, FunctionEffectProfile>(StringComparer.Ordinal)
            {
                ["Run"] = new FunctionEffectProfile(
                    "Run",
                    StarkFunctionKind.Fn,
                    ReadsArgumentMemory: false,
                    IsPure: true,
                    NoSync: true,
                    NoFree: true,
                    NoUnwind: true,
                    WillReturn: true,
                    MustProgress: true,
                    UseFastCallingConvention: true,
                    IsFfi: false,
                    IsHot: false,
                    IsCold: false,
                    InlinePreference.NoInline,
                    IsStrictFp: false)
            });
        var typeModel = new TypeCheckModel(
            "Demo",
            NamedTypes: new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal),
            TypeAliases: new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            Functions: new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal)
            {
                ["Run"] = new TypedFunctionSignature("Run", targetType, [])
            },
            Globals: new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal),
            Literals: [],
            ObjectCreations: []);
        var abiModel = new AbiModel(
            "Demo",
            new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal)
            {
                ["Run"] = new AbiFunctionSignature(
                    "Run",
                    "Run",
                    targetType,
                    targetType,
                    [],
                    IsFfi: false)
            });
        var ssa = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    targetType,
                    [],
                    HasBody: true,
                    SupportsDirectCodeGeneration: true,
                    EntryBlockId: 0,
                    Blocks:
                    [
                        new SsaBasicBlock(
                            0,
                            "bb0_entry",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaConvertRValue(
                                        operand,
                                        targetType,
                                        "convert"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", targetType)))
                    ])
            ]);
        var syntaxModel = new SyntaxModel(
            "Demo",
            [],
            [
                new TopLevelDeclarationModel(
                    "Run",
                    DeclarationKind.Function,
                    StarkVisibility.Module,
                        new FunctionDeclarationModel(
                            "Run",
                            StarkFunctionKind.Fn,
                            targetType.DisplayName,
                            [],
                            new FunctionModifierSet(
                                InlinePreference.NoInline,
                                HasExplicitInlinePreference: true,
                                IsHot: false,
                                IsCold: false,
                                IsFfi: false,
                                IsStrictFp: false),
                            HasBody: true))
            ]);

        return new LlvmIrEmitter(
            new CompilationInput("module Demo"),
            StarkSyntax.ParseCompilationUnit("module Demo"),
            syntaxModel,
            effectModel,
            typeModel,
            EnumLayoutBuilder.Build(typeModel),
            abiModel,
            ssa).Emit().Text;
    }
}
