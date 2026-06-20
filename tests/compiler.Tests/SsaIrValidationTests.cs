using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaIrValidationTests
{
    private static readonly StarkTypeSymbol I32 = StarkTypeSymbols.Integer(32);
    private static readonly StarkTypeSymbol UnsizedInteger = new(StarkTypeKind.Integer, "integer");

    [Fact]
    public void UndefinedSsaValueReferenceFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Add,
                    new SsaValueReference("missing", I32),
                    new SsaIntegerConstant(1, I32),
                    I32,
                    "missing + 1"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "value reference '%missing'", "not defined");
    }

    [Fact]
    public void UnsupportedSsaConversionFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaConvertRValue(
                    new SsaStringConstant("hello", StarkTypeSymbols.Ascii),
                    StarkTypeSymbols.Unicode,
                    "unicode(\"hello\")"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "conversion from 'ascii' to 'unicode'", "not supported by SSA LLVM emission");
    }

    [Fact]
    public void UnsizedIntegerConversionFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaConvertRValue(
                    new SsaIntegerConstant(1, UnsizedInteger),
                    StarkTypeSymbols.Float(32),
                    "f32(1)"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "conversion source type 'integer'", "concrete integer");
    }

    [Fact]
    public void CompileTimeIntegerConstantFailsBeforeLlvmEmission()
    {
        var function = BuildReturningFunction(
            StarkTypeSymbols.CompileTimeInteger,
            new SsaIntegerConstant(BigInteger.One << 1024, StarkTypeSymbols.CompileTimeInteger));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "integer constant value", "concrete integer storage type", "integer");
    }

    [Fact]
    public void IntegerConstantOutsideStorageFailsBeforeLlvmEmission()
    {
        var i8 = StarkTypeSymbols.Integer(8);
        var function = BuildReturningFunction(
            i8,
            new SsaIntegerConstant(300, i8));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "integer constant value '300'", "does not fit storage type 'i8'");
    }

    [Fact]
    public void IntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmission()
    {
        var ranged = StarkTypeSymbols.Integer(8, 0, 10);
        var function = BuildReturningFunction(
            ranged,
            new SsaIntegerConstant(44, ranged));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "integer constant value '44'", "outside effective range 'i8[0 10]'");
    }

    [Fact]
    public void UnsupportedFloatConversionTargetFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaConvertRValue(
                    new SsaIntegerConstant(1, I32),
                    StarkTypeSymbols.Float(24),
                    "f24(1)"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "conversion target type 'f24'", "supported LLVM float");
    }

    [Fact]
    public void DirectCallAbiMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Add",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Add(1)"))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Add",
            "Add",
            I32,
            I32,
            [
                new AbiParameterSymbol("left", "left", I32, I32, AbiParameterKind.Direct),
                new AbiParameterSymbol("right", "right", I32, I32, AbiParameterKind.Direct)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "ABI parameter count mismatch for 'Add'", "expected 2", "got 1");
    }

    [Fact]
    public void DirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmission()
    {
        var address = new SsaGlobalAddressValue("value", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true));
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Identity",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Identity(1)",
                    IndirectArgumentAddresses: [address]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Identity",
            "Identity",
            I32,
            I32,
            [new AbiParameterSymbol("value", "value", I32, I32, AbiParameterKind.Direct)],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "call 'Identity' argument 1", "direct ABI parameter", "indirect argument address");
    }

    [Fact]
    public void DirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission()
    {
        var boolPointer = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true);
        var address = new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, boolPointer);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Consume",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Consume(1)",
                    IndirectArgumentAddresses: [address]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Consume",
            "Consume",
            I32,
            I32,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    I32,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    AbiParameterKind.IndirectIn)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "call 'Consume' argument 1 indirect address pointee", "bool", "i32");
    }

    [Fact]
    public void DirectCallPromotedUnknownLocalFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Consume",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Consume(1)",
                    IndirectArgumentLocalNames: ["missing"]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Consume",
            "Consume",
            I32,
            I32,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    I32,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    AbiParameterKind.IndirectIn)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "call 'Consume' argument 1", "promotes unknown local or parameter 'missing'");
    }

    [Fact]
    public void DirectCallIndirectAddressAndPromotedStorageShapesAreAccepted()
    {
        var parameter = new TypedParameterSymbol("input", I32);
        var address = new SsaGlobalAddressValue("value", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true));
        var function = BuildVoidFunction(
            [
                new SsaAllocateLocalInstruction("scratch", I32),
                new SsaValueInstruction(
                    "v0",
                    new SsaCallRValue(
                        "Consume",
                        [new SsaIntegerConstant(1, I32)],
                        I32,
                        "Consume(1)",
                        IndirectArgumentAddresses: [address])),
                new SsaValueInstruction(
                    "v1",
                    new SsaCallRValue(
                        "Consume",
                        [new SsaIntegerConstant(2, I32)],
                        I32,
                        "Consume(2)",
                        IndirectArgumentLocalNames: ["scratch"])),
                new SsaValueInstruction(
                    "v2",
                    new SsaCallRValue(
                        "Consume",
                        [new SsaValueReference("arg_input", I32)],
                        I32,
                        "Consume(input)",
                        IndirectArgumentLocalNames: ["input"]))
            ],
            [parameter]);
        var abi = BuildAbiModel(
            new AbiFunctionSignature(
                "Run",
                "Run",
                StarkTypeSymbols.Void,
                StarkTypeSymbols.Void,
                [new AbiParameterSymbol("input", "arg_input", I32, I32, AbiParameterKind.Direct)],
                IsFfi: false),
            new AbiFunctionSignature(
                "Consume",
                "Consume",
                I32,
                I32,
                [
                    new AbiParameterSymbol(
                        "value",
                        "value",
                        I32,
                        StarkTypeSymbols.RawPointer(I32, isMutable: false),
                        AbiParameterKind.IndirectIn)
                ],
                IsFfi: false));

        var diagnostics = Validate(function, abi);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void IndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 5);
        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            [arrayType]);
        var boolPointer = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true);
        var address = new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, boolPointer);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaIndirectCallRValue(
                    new SsaFunctionAddressValue("Consume", functionPointerType),
                    [new SsaZeroInitializerValue(arrayType)],
                    I32,
                    "op(value)",
                    IndirectArgumentAddresses: [address]))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "indirect call argument 1 indirect address pointee", "bool", "i32[5]");
    }

    [Fact]
    public void IndirectCallLargeByvalAddressShapeIsAccepted()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 5);
        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            [arrayType]);
        var address = new SsaGlobalAddressValue("values", arrayType, StarkTypeSymbols.RawPointer(arrayType, isMutable: true));
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaIndirectCallRValue(
                    new SsaFunctionAddressValue("Consume", functionPointerType),
                    [new SsaZeroInitializerValue(arrayType)],
                    I32,
                    "op(values)",
                    IndirectArgumentAddresses: [address]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Consume",
            "Consume",
            I32,
            I32,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    arrayType,
                    StarkTypeSymbols.RawPointer(arrayType, isMutable: false),
                    AbiParameterKind.IndirectIn)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FfiTextReturnFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "CurrentName",
                    [],
                    StarkTypeSymbols.Ascii,
                    "CurrentName()",
                    SourceReturnType: StarkTypeSymbols.Ascii))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "CurrentName",
            "CurrentName",
            StarkTypeSymbols.Ascii,
            StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            [],
            IsFfi: true));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "FFI text-view return", "CurrentName", "raw pointer plus explicit length/status");
    }

    [Fact]
    public void MirOnlyTempLocalStorageFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("scratch", I32, StorageClass: "temp")
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "local 'scratch'", "invalid storage class 'temp'");
    }

    [Fact]
    public void NonHeapDeallocationFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("scratch", I32),
            new SsaDeallocateLocalInstruction("scratch", I32, StorageClass: "stack")
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "local 'scratch'", "invalid deallocation storage class 'stack'");
    }

    [Fact]
    public void LocalUseWithoutAllocationFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaStoreLocalInstruction(
                "scratch",
                I32,
                new SsaIntegerConstant(1, I32))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "local 'scratch'", "used before it is allocated in SSA");
    }

    [Fact]
    public void FunctionMissingAbiFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([]);
        var abi = new AbiModel("Demo", new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "function 'Run'", "missing ABI lowering");
    }

    [Fact]
    public void DuplicateSsaValueDefinitionFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaIntegerConstant(1, I32))),
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaIntegerConstant(2, I32)))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "SSA value '%v0'", "defined more than once");
    }

    [Fact]
    public void PhiIncomingTypeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [new SsaPhiIncoming(0, new SsaBoolConstant(true))])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "phi 'v0' incoming value", "bool", "i32");
    }

    [Fact]
    public void PhiIncomingFromNonPredecessorFailsBeforeLlvmEmission()
    {
        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [new SsaPhiIncoming(2, new SsaIntegerConstant(1, I32))])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])),
            new SsaBasicBlock(
                2,
                "other",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "phi 'v0' incoming predecessor block '2'", "does not branch to block '1'");
    }

    [Fact]
    public void PhiMissingIncomingFailsBeforeLlvmEmission()
    {
        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    [2, 1],
                    Condition: new SsaBoolConstant(true))),
            new SsaBasicBlock(
                1,
                "side",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [2])),
            new SsaBasicBlock(
                2,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [new SsaPhiIncoming(0, new SsaIntegerConstant(1, I32))])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "phi 'v0'", "missing an incoming value for predecessor block '1'");
    }

    [Fact]
    public void PhiDuplicateIncomingFailsBeforeLlvmEmission()
    {
        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [
                            new SsaPhiIncoming(0, new SsaIntegerConstant(1, I32)),
                            new SsaPhiIncoming(0, new SsaIntegerConstant(2, I32))
                        ])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "phi 'v0'", "more than one incoming value for predecessor block '0'");
    }

    [Fact]
    public void PhiWithoutIncomingFailsBeforeLlvmEmission()
    {
        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "phi 'v0'", "requires at least one incoming value");
    }

    [Fact]
    public void FunctionAbiReturnMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildReturningFunction(I32, new SsaIntegerConstant(0, I32));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Bool,
            StarkTypeSymbols.Bool,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "function 'Run' ABI source return", "bool", "i32");
        AssertDiagnostic(diagnostics, "STK5002", "function 'Run' ABI LLVM return", "bool", "i32");
    }

    [Fact]
    public void FunctionAbiRetborrowPointerReturnIsAccepted()
    {
        var retborrowI32 = I32 with
        {
            BorrowKind = StarkBorrowKind.RetBorrow,
            IsMutableView = true,
            DisplayName = "retborrow mut i32"
        };
        var pointerType = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var function = BuildReturningFunction(
            retborrowI32,
            new SsaGlobalAddressValue("value", I32, pointerType));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            retborrowI32,
            pointerType,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FunctionAbiParameterCountMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([], parameters: [new TypedParameterSymbol("value", I32)]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "function 'Run' ABI user parameter count mismatch", "expected 1", "got 0");
    }

    [Fact]
    public void FunctionAbiParameterTypeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([], parameters: [new TypedParameterSymbol("value", I32)]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    StarkTypeSymbols.Bool,
                    StarkTypeSymbols.Bool,
                    AbiParameterKind.Direct)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "function 'Run' ABI parameter 1 source type", "bool", "i32");
    }

    [Fact]
    public void FunctionAbiSretShapeMismatchFailsBeforeLlvmEmission()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildReturningFunction(arrayType, new SsaZeroInitializerValue(arrayType));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            arrayType,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol(
                    "ret",
                    "ret",
                    StarkTypeSymbols.Bool,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true),
                    AbiParameterKind.SRet)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "function 'Run' sret source type", "bool", "i32[4]");
        AssertDiagnostic(diagnostics, "STK5002", "function 'Run' sret LLVM parameter pointee", "bool", "i32[4]");
    }

    [Fact]
    public void FunctionAbiSretShapeAccepted()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildReturningFunction(arrayType, new SsaZeroInitializerValue(arrayType));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            arrayType,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol(
                    "ret",
                    "ret",
                    arrayType,
                    StarkTypeSymbols.RawPointer(arrayType, isMutable: true),
                    AbiParameterKind.SRet)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmission()
    {
        var valueParameter = new TypedParameterSymbol("value", I32);
        var function = BuildVoidFunction(
            [
                new SsaValueInstruction(
                    "v0",
                    new SsaAddressOfParameterRValue(
                        "value",
                        I32,
                        StarkTypeSymbols.RawPointer(I32, isMutable: true),
                        "&value"))
            ],
            [valueParameter]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "address-of parameter 'value'", "missing ABI user-parameter lowering");
    }

    [Fact]
    public void UnsupportedSsaInstructionFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([new UnsupportedSsaInstruction()]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unsupported SSA instruction type", nameof(UnsupportedSsaInstruction));
    }

    [Fact]
    public void UnsupportedSsaRValueFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction("v0", new UnsupportedSsaRValue())
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unsupported SSA rvalue type", nameof(UnsupportedSsaRValue));
    }

    [Fact]
    public void UnsupportedSsaValueFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction("v0", new SsaUseRValue(new UnsupportedSsaValue()))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unsupported SSA value type", nameof(UnsupportedSsaValue));
    }

    [Fact]
    public void UnsupportedSsaTerminatorFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction(
            [],
            terminator: new SsaTerminator((SsaTerminatorKind)999, []));

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unsupported SSA terminator kind", "999");
    }

    [Fact]
    public void DynamicStorageElementLayoutFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageAllocationRValue(
                    new SsaIntegerConstant(1, I32),
                    StarkTypeSymbols.Dynamic(StarkTypeSymbols.Void),
                    DynamicStorageAllocationKind.Runtime,
                    "new dynamic void"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "dynamic storage allocation", "concrete element layout", "void");
    }

    [Fact]
    public void DynamicStorageCapacityWidthFailsBeforeLlvmEmission()
    {
        var i128 = StarkTypeSymbols.Integer(128);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageAllocationRValue(
                    new SsaIntegerConstant(1, i128),
                    StarkTypeSymbols.Dynamic(I32),
                    DynamicStorageAllocationKind.Runtime,
                    "new dynamic i32"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "dynamic storage capacity", "width '128'", "not supported");
    }

    [Fact]
    public void DynamicStorageReserveAddressShapeFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageReserveRValue(
                    new SsaIntegerConstant(0, I32),
                    StarkTypeSymbols.Dynamic(I32),
                    new SsaIntegerConstant(1, I32),
                    DynamicStorageAllocationKind.Runtime,
                    "reserve"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "dynamic storage Reserve address", "must be a raw pointer", "i32");
    }

    [Fact]
    public void DynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmission()
    {
        var boolStorage = StarkTypeSymbols.Dynamic(StarkTypeSymbols.Bool);
        var i32Storage = StarkTypeSymbols.Dynamic(I32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageMoveAtRValue(
                    new SsaGlobalAddressValue("values", boolStorage, StarkTypeSymbols.RawPointer(boolStorage, isMutable: true)),
                    i32Storage,
                    new SsaIntegerConstant(0, I32),
                    I32,
                    "move_at"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "dynamic storage MoveAt address pointee", "dynamic bool", "dynamic i32");
    }

    [Fact]
    public void ElementAddressBasePointeeMismatchFailsBeforeLlvmEmission()
    {
        var boolArray = StarkTypeSymbols.FixedArray(StarkTypeSymbols.Bool, fixedLength: 4);
        var i32Array = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaElementAddressRValue(
                    new SsaGlobalAddressValue("values", boolArray, StarkTypeSymbols.RawPointer(boolArray, isMutable: true)),
                    i32Array,
                    new SsaIntegerConstant(0, I32),
                    ConstantIndex: null,
                    StarkTypeSymbols.RawPointer(I32, isMutable: true),
                    "values[index]"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "element address base pointee", "bool[4]", "i32[4]");
    }

    [Fact]
    public void ElementAddressResultPointeeMismatchFailsBeforeLlvmEmission()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaElementAddressRValue(
                    new SsaGlobalAddressValue("values", arrayType, StarkTypeSymbols.RawPointer(arrayType, isMutable: true)),
                    arrayType,
                    new SsaIntegerConstant(0, I32),
                    ConstantIndex: null,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true),
                    "values[index]"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "element address result pointee", "bool", "i32");
    }

    [Fact]
    public void UnsupportedUnaryShapeFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUnaryRValue(
                    SsaUnaryOperator.LogicalNot,
                    new SsaIntegerConstant(1, I32),
                    I32,
                    "!1"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unary operator 'LogicalNot'", "bool operand and bool result");
    }

    [Fact]
    public void UnsizedIntegerUnaryFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUnaryRValue(
                    SsaUnaryOperator.Negate,
                    new SsaIntegerConstant(1, UnsizedInteger),
                    UnsizedInteger,
                    "-1"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unary operator 'Negate' result type 'integer'", "concrete integer");
    }

    [Fact]
    public void UnsupportedFloatUnaryFailsBeforeLlvmEmission()
    {
        var f24 = StarkTypeSymbols.Float(24);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUnaryRValue(
                    SsaUnaryOperator.Negate,
                    new SsaFloatConstant("1.0", f24),
                    f24,
                    "-1.0"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "unary operator 'Negate' result type 'f24'", "supported LLVM float");
    }

    [Fact]
    public void BinaryOperandShapeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Add,
                    new SsaIntegerConstant(1, I32),
                    new SsaBoolConstant(true),
                    I32,
                    "1 + true"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "binary operator 'Add' right operand", "bool", "i32");
    }

    [Fact]
    public void WrappingFloatOperatorFailsBeforeLlvmEmission()
    {
        var f32 = StarkTypeSymbols.Float(32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.WrappingAdd,
                    new SsaFloatConstant("1.0", f32),
                    new SsaFloatConstant("2.0", f32),
                    f32,
                    "1.0 +% 2.0"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "wrapping integer operator 'WrappingAdd'", "concrete integer", "f32");
    }

    [Fact]
    public void UnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmission()
    {
        var f24 = StarkTypeSymbols.Float(24);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Exponent,
                    new SsaFloatConstant("2.0", f24),
                    new SsaFloatConstant("3.0", f24),
                    f24,
                    "2.0 ** 3.0"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "exponent operator result type 'f24'", "not supported by LLVM emission");
    }

    [Fact]
    public void ComparisonResultShapeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Equal,
                    new SsaIntegerConstant(1, I32),
                    new SsaIntegerConstant(2, I32),
                    I32,
                    "1 == 2"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "comparison operator 'Equal'", "bool result", "i32");
    }

    [Fact]
    public void FixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmission()
    {
        var sliceArrayType = StarkTypeSymbols.FixedArray(StarkTypeSymbols.Slice(I32), fixedLength: 2);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.LessThan,
                    new SsaZeroInitializerValue(sliceArrayType),
                    new SsaZeroInitializerValue(sliceArrayType),
                    StarkTypeSymbols.Bool,
                    "left < right"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "comparison operator 'LessThan'", "fixed-array operand", "not supported by ordered comparison helper lowering");
    }

    [Fact]
    public void NamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmission()
    {
        var sliceField = new FieldSymbol("Items", StarkTypeSymbols.Slice(I32));
        var namedType = new NamedTypeSymbol(
            "BadComparable",
            DeclarationKind.Struct,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal)
            {
                [sliceField.Name] = sliceField
            },
            [sliceField]);
        var valueType = StarkTypeSymbols.Named("BadComparable");
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.GreaterThan,
                    new SsaZeroInitializerValue(valueType),
                    new SsaZeroInitializerValue(valueType),
                    StarkTypeSymbols.Bool,
                    "left > right"))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModel(namedType));

        AssertDiagnostic(diagnostics, "STK5002", "comparison operator 'GreaterThan'", "named aggregate operand", "not supported by ordered comparison helper lowering");
    }

    [Fact]
    public void ExtractFieldResultMismatchFailsBeforeLlvmEmission()
    {
        var pairType = StarkTypeSymbols.Named("Pair");
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractFieldRValue(
                    new SsaZeroInitializerValue(pairType),
                    "Flag",
                    1,
                    I32,
                    "pair.Flag"))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModel(BuildPairType()));

        AssertDiagnostic(diagnostics, "STK5002", "field extraction 'Flag' result", "bool", "i32");
    }

    [Fact]
    public void ExtractFieldNameIndexMismatchFailsBeforeLlvmEmission()
    {
        var pairType = StarkTypeSymbols.Named("Pair");
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractFieldRValue(
                    new SsaZeroInitializerValue(pairType),
                    "Count",
                    0,
                    I32,
                    "pair.Count"))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModel(BuildPairType()));

        AssertDiagnostic(diagnostics, "STK5002", "field extraction field 'Count' index '0'", "field 'Value'");
    }

    [Fact]
    public void ExtractIndexOutOfRangeIsUnrepresentable()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SsaExtractIndexRValue(
            new SsaZeroInitializerValue(arrayType),
            2,
            IndexedElementOperationFamily.FixedArrayElement,
            I32,
            "values[2]"));
    }

    [Fact]
    public void InsertIndexValueMismatchIsUnrepresentable()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 2);

        Assert.Throws<ArgumentException>(() => new SsaInsertIndexRValue(
            new SsaZeroInitializerValue(arrayType),
            1,
            IndexedElementOperationFamily.FixedArrayElement,
            new SsaBoolConstant(true),
            arrayType,
            "values[1] = true"));
    }

    [Fact]
    public void SliceViewIndexExtractionIsAccepted()
    {
        var sliceType = StarkTypeSymbols.Slice(I32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractIndexRValue(
                    new SsaZeroInitializerValue(sliceType),
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    "values.Data")),
            new SsaValueInstruction(
                "v1",
                new SsaExtractIndexRValue(
                    new SsaZeroInitializerValue(sliceType),
                    1,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.Integer(64),
                    "values.Length"))
        ]);

        var diagnostics = Validate(function);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SliceViewNoOpRetypeSupportsImmutableComponentExtraction()
    {
        var mutableSliceType = StarkTypeSymbols.ApplyQualifiers(StarkTypeSymbols.Slice(I32), isMutableView: true);
        var sliceType = StarkTypeSymbols.Slice(I32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "view",
                new SsaConvertRValue(
                    new SsaZeroInitializerValue(mutableSliceType),
                    sliceType,
                    "view:i32[]")),
            new SsaValueInstruction(
                "data",
                new SsaExtractIndexRValue(
                    new SsaValueReference("view", sliceType),
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    "view.Data"))
        ]);

        var diagnostics = Validate(function);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void IndexOperationFamilyMismatchIsUnrepresentable()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 2);

        Assert.Throws<ArgumentException>(() => new SsaExtractIndexRValue(
            new SsaZeroInitializerValue(arrayType),
            0,
            IndexedElementOperationFamily.ViewComponent,
            I32,
            "values[0]"));
    }

    [Fact]
    public void TextViewIndexExtractionIsAccepted()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractIndexRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
                    "text.Data")),
            new SsaValueInstruction(
                "v1",
                new SsaExtractIndexRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    1,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.Integer(64),
                    "text.Length"))
        ]);

        var diagnostics = Validate(function);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TextViewFieldExtractionIsAccepted()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractFieldRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    "data",
                    0,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
                    "text.data")),
            new SsaValueInstruction(
                "v1",
                new SsaExtractFieldRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    "length",
                    1,
                    StarkTypeSymbols.Integer(64),
                    "text.length"))
        ]);

        var diagnostics = Validate(function);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SelectArmShapeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaSelectRValue(
                    new SsaBoolConstant(true),
                    new SsaIntegerConstant(1, I32),
                    new SsaBoolConstant(false),
                    I32,
                    "select"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "select false arm", "bool", "i32");
    }

    [Fact]
    public void UnsupportedSwitchConditionFailsBeforeLlvmEmission()
    {
        var function = BuildSwitchFunction(
            new SsaStringConstant("value", StarkTypeSymbols.Ascii),
            [new SsaSwitchCase("\"value\"", 1, new SsaStringConstant("value", StarkTypeSymbols.Ascii))]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "switch condition type 'ascii'", "bool or a concrete integer");
    }

    [Fact]
    public void UnsizedIntegerSwitchConditionFailsBeforeLlvmEmission()
    {
        var function = BuildSwitchFunction(
            new SsaIntegerConstant(0, UnsizedInteger),
            [new SsaSwitchCase("0", 1, new SsaIntegerConstant(0, UnsizedInteger))]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "switch condition type 'integer'", "concrete integer");
    }

    [Fact]
    public void SwitchCaseShapeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildSwitchFunction(
            new SsaIntegerConstant(0, I32),
            [new SsaSwitchCase("true", 1, new SsaBoolConstant(true))]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "switch case 'true' match value", "bool", "i32");
    }

    [Fact]
    public void SliceCreationShapeMismatchFailsBeforeLlvmEmission()
    {
        var unknownLengthArray = StarkTypeSymbols.FixedArray(I32, fixedLength: null);
        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("values", unknownLengthArray),
            new SsaValueInstruction(
                "v0",
                new SsaMakeSliceFromLocalRValue(
                    "values",
                    unknownLengthArray,
                    StarkTypeSymbols.Slice(I32),
                    "values[]"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "slice creation from local 'values'", "known length");
    }

    [Fact]
    public void IndirectLoadAddressShapeFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaLoadIndirectRValue(
                    new SsaIntegerConstant(0, I32),
                    I32,
                    "*0"))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "indirect load address", "must be a raw pointer", "i32");
    }

    [Fact]
    public void IndirectStoreAcceptsQualifiedPointeeShape()
    {
        var outBool = StarkTypeSymbols.Bool with
        {
            DisplayName = "out bool",
            InitializationKind = StarkInitializationKind.Out
        };
        var outBoolPointer = StarkTypeSymbols.RawPointer(outBool, isMutable: true);
        var function = BuildVoidFunction([
            new SsaStoreIndirectInstruction(
                new SsaGlobalAddressValue("flag", outBool, outBoolPointer),
                outBool,
                new SsaBoolConstant(true))
        ]);

        var diagnostics = Validate(function);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void CopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true)),
                new SsaGlobalAddressValue("value", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true)),
                I32)
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "copy destination address pointee", "bool", "i32");
    }

    [Fact]
    public void CopyMemorySourcePointeeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("destination", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true)),
                new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true)),
                I32)
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "copy source address pointee", "bool", "i32");
    }

    [Fact]
    public void CopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmission()
    {
        var voidPointer = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Void, isMutable: true);
        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("destination", StarkTypeSymbols.Void, voidPointer),
                new SsaGlobalAddressValue("source", StarkTypeSymbols.Void, voidPointer),
                StarkTypeSymbols.Void)
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "copy memory", "concrete non-empty layout", "void");
    }

    [Fact]
    public void CopyMemoryAllowsFixedArrayElementPointers()
    {
        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 64);
        var elementPointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("destination", I32, elementPointer),
                new SsaGlobalAddressValue("source", I32, elementPointer),
                arrayType)
        ]);

        var diagnostics = Validate(function);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ScopedNoAliasProofCarrierAcceptsMatchingParameterRoots()
    {
        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.RuntimeDisjointCondition,
            "runtime-disjoint-0",
            ["param:left", "param:right"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmission()
    {
        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.RuntimeDisjointCondition,
            "runtime-disjoint-0",
            ["param:left", "param:right"],
            proofRoots: ["param:left", "param:other"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        AssertDiagnostic(diagnostics, "STK5002", "roots do not match", "runtime-disjoint-0");
        AssertDiagnostic(diagnostics, "STK5002", "unknown parameter memory-root key 'param:other'");
    }

    [Fact]
    public void ScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmission()
    {
        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.RuntimeDisjointCondition,
            "runtime-disjoint-0",
            ["param:left", "param:left"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        AssertDiagnostic(diagnostics, "STK5002", "contains blank or duplicate memory roots");
    }

    [Fact]
    public void ScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmission()
    {
        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = new ScopedNoAliasGroup(
            "runtime-disjoint-0",
            ["param:left", "param:right"],
            new AliasProofCarrier(
                AliasProofCarrierKind.RuntimeDisjointCondition,
                "wrong-proof",
                ["param:left", "param:right"]));
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        AssertDiagnostic(diagnostics, "STK5002", "alias proof carrier 'wrong-proof'", "does not match scoped noalias group 'runtime-disjoint-0'");
    }

    [Fact]
    public void ScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmission()
    {
        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.UnsafeAssumeDisjoint,
            "unsafe-assume-disjoint-0",
            ["param:left", "param:missing"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        AssertDiagnostic(diagnostics, "STK5002", "unknown parameter memory-root key 'param:missing'");
    }

    [Fact]
    public void StringConstantNonTextTypeFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaStringConstant("hello", I32)))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "string constant", "ascii/unicode", "i32");
    }

    [Fact]
    public void TextDataAddressPointeeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaTextDataAddressValue(
                    "hello",
                    StarkTypeSymbols.Ascii,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false))))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "text data address result pointee", "i32", "i8");
    }

    [Fact]
    public void GlobalAddressPointeeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaGlobalAddressValue(
                    "flag",
                    StarkTypeSymbols.Bool,
                    StarkTypeSymbols.RawPointer(I32, isMutable: true))))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "global address result pointee", "i32", "bool");
    }

    [Fact]
    public void UnknownGlobalLoadFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaLoadGlobalRValue("Missing", I32))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModelWithGlobals());

        AssertDiagnostic(diagnostics, "STK5002", "global load", "unknown global 'Missing'");
    }

    [Fact]
    public void KnownGlobalLoadTypeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaLoadGlobalRValue("Flag", I32))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Flag", StarkTypeSymbols.Bool, GlobalBindingKind.Immutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "global load 'Flag' result", "i32", "bool");
    }

    [Fact]
    public void KnownGlobalAddressTypeMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaGlobalAddressValue(
                    "Flag",
                    I32,
                    StarkTypeSymbols.RawPointer(I32, isMutable: true))))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Flag", StarkTypeSymbols.Bool, GlobalBindingKind.Immutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "global address 'Flag' pointee", "i32", "bool");
    }

    [Fact]
    public void StoreToImmutableGlobalFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaStoreGlobalInstruction(
                "Limit",
                I32,
                new SsaIntegerConstant(1, I32))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Limit", I32, GlobalBindingKind.Const));

        var diagnostics = Validate(function, typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "global store target 'Limit'", "must be mutable", "Const");
    }

    [Fact]
    public void StoreGlobalValueMismatchFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaStoreGlobalInstruction(
                "Counter",
                I32,
                new SsaBoolConstant(true))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Counter", I32, GlobalBindingKind.Mutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "global store 'Counter' value", "bool", "i32");
    }

    [Fact]
    public void StoreMutableKnownGlobalPassesSsaValidation()
    {
        var function = BuildVoidFunction([
            new SsaStoreGlobalInstruction(
                "Counter",
                I32,
                new SsaIntegerConstant(1, I32))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Counter", I32, GlobalBindingKind.Mutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmission()
    {
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaFunctionAddressValue("Run", StarkTypeSymbols.RawPointer(I32, isMutable: false))))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "function address 'Run'", "function-pointer type", "rawptr<i32>");
    }

    [Fact]
    public void FunctionAddressMissingAbiFailsBeforeLlvmEmission()
    {
        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            []);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaFunctionAddressValue("Missing", functionPointerType)))
        ]);

        var diagnostics = Validate(function);

        AssertDiagnostic(diagnostics, "STK5002", "function address target 'Missing'", "missing ABI lowering");
    }

    [Fact]
    public void FunctionAddressSignatureMismatchFailsBeforeLlvmEmission()
    {
        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            [I32]);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaFunctionAddressValue("Read", functionPointerType)))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Read",
            "Read",
            StarkTypeSymbols.Bool,
            StarkTypeSymbols.Bool,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        AssertDiagnostic(diagnostics, "STK5002", "function address 'Read' return type", "bool", "i32");
        AssertDiagnostic(diagnostics, "STK5002", "function address 'Read' parameter count mismatch", "expected 1", "got 0");
    }

    [Fact]
    public void SystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmission()
    {
        var typeModel = BuildTypeModelForModule(
            "System.Math",
            [
                new TypedFunctionSignature(
                    "Sin",
                    I32,
                    [new TypedParameterSymbol("value", I32)])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "System.Math builtin 'Sin'", "floating-point return type");
    }

    [Fact]
    public void SystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmission()
    {
        var i16 = StarkTypeSymbols.Integer(16);
        var typeModel = BuildTypeModelForModule(
            "System.BitOperations",
            [
                new TypedFunctionSignature(
                    "PopCount",
                    i16,
                    [new TypedParameterSymbol("value", i16)])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "System.BitOperations builtin 'PopCount'", "supports only 'i32' and 'i64'");
    }

    [Fact]
    public void SystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmission()
    {
        var allocator = BuildNamedType("System.Memory.Allocator", [new FieldSymbol("Kind", StarkTypeSymbols.Integer(8, isUnsigned: true))]);
        var allocation = BuildNamedType(
            "System.Memory.Allocation",
            [
                new FieldSymbol("Pointer", StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true)),
                new FieldSymbol("ByteLength", StarkTypeSymbols.Integer(64, isUnsigned: true)),
                new FieldSymbol("Alignment", StarkTypeSymbols.Integer(64, isUnsigned: true))
            ]);
        var typeModel = BuildTypeModelForModule(
            "System.Memory",
            [
                new TypedFunctionSignature(
                    "Allocate",
                    StarkTypeSymbols.Named("System.Memory.Allocation"),
                    [
                        new TypedParameterSymbol("allocator", StarkTypeSymbols.Named("System.Memory.Allocator")),
                        new TypedParameterSymbol("byteLength", StarkTypeSymbols.Integer(64, isUnsigned: true)),
                        new TypedParameterSymbol("alignment", StarkTypeSymbols.Integer(64, isUnsigned: true))
                    ])
            ],
            allocator,
            allocation);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "System.Memory Allocation", "Pointer, ByteLength, Alignment, and Allocator fields");
    }

    [Fact]
    public void SystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidation()
    {
        var typeModel = BuildTypeModelForModule(
            "System.Collections",
            [
                new TypedFunctionSignature(
                    "__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__ascii",
                    StarkTypeSymbols.Integer(64, isUnsigned: true),
                    [new TypedParameterSymbol("value", StarkTypeSymbols.Ascii with { BorrowKind = StarkBorrowKind.Borrow })],
                    SourceName: "System.Collections.DictionaryKey.Hash",
                    TemplateName: "System.Collections.DictionaryKey.Hash",
                    TypeArguments: [StarkTypeSymbols.Ascii])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        Assert.DoesNotContain(diagnostics, static diagnostic =>
            diagnostic.Code == "STK5002"
            && diagnostic.Message.Contains("System.Collections DictionaryKey builtin", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmission()
    {
        var sliceType = StarkTypeSymbols.Slice(StarkTypeSymbols.Integer(8));
        var typeModel = BuildTypeModelForModule(
            "System.Collections",
            [
                new TypedFunctionSignature(
                    "__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__i8_slice",
                    StarkTypeSymbols.Integer(64, isUnsigned: true),
                    [new TypedParameterSymbol("value", sliceType with { BorrowKind = StarkBorrowKind.Borrow })],
                    SourceName: "System.Collections.DictionaryKey.Hash",
                    TemplateName: "System.Collections.DictionaryKey.Hash",
                    TypeArguments: [sliceType])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "System.Collections DictionaryKey builtin", "does not support key type 'i8[]'");
    }

    [Fact]
    public void SystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmission()
    {
        var mutableParts = BuildNamedType(
            "System.Runtime.MutableByteSliceParts",
            [
                new FieldSymbol("Data", StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true)),
                new FieldSymbol("Length", StarkTypeSymbols.Integer(64, isUnsigned: true))
            ]);
        var typeModel = BuildTypeModelForModule(
            "System.Runtime",
            [
                new TypedFunctionSignature(
                    "GetMutableByteSliceParts",
                    StarkTypeSymbols.Named("System.Runtime.MutableByteSliceParts"),
                    [new TypedParameterSymbol("source", StarkTypeSymbols.Slice(StarkTypeSymbols.Integer(8)) with { BorrowKind = StarkBorrowKind.Borrow })])
            ],
            mutableParts);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        AssertDiagnostic(diagnostics, "STK5002", "System.Runtime byte slice parts builtin 'GetMutableByteSliceParts'", "mut borrow i8[]");
    }

    [Fact]
    public void ValidSystemBuiltinSignaturesPassSsaValidation()
    {
        var i64 = StarkTypeSymbols.Integer(64, isUnsigned: true);
        var typeModel = BuildTypeModelForModule(
            "System.BitOperations",
            [
                new TypedFunctionSignature(
                    "RotateLeft",
                    i64,
                    [
                        new TypedParameterSymbol("value", i64),
                        new TypedParameterSymbol("amount", i64)
                    ])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        Assert.Empty(diagnostics);
    }

    private static IReadOnlyList<CompilerDiagnostic> Validate(
        SsaFunction function,
        AbiModel? abiModel = null,
        TypeCheckModel? typeModel = null)
    {
        var state = new CompilationState(new CompilationInput("module Demo"), new CompilerOptions());
        var context = new CompilerPassContext(state);
        new SsaIrValidator(
            context,
            new SsaIrModule("Demo", [function]),
            abiModel ?? BuildAbiModel(),
            typeModel).Validate();
        return context.Diagnostics.Items;
    }

    private static TypeCheckModel BuildTypeModel(params NamedTypeSymbol[] namedTypes)
    {
        return new TypeCheckModel(
            "Demo",
            namedTypes.ToDictionary(static namedType => namedType.Name, StringComparer.Ordinal),
            new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal),
            new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal),
            [],
            []);
    }

    private static TypeCheckModel BuildTypeModelWithGlobals(params TypedGlobalSymbol[] globals)
    {
        return new TypeCheckModel(
            "Demo",
            new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal),
            new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal),
            globals.ToDictionary(static global => global.Name, StringComparer.Ordinal),
            [],
            []);
    }

    private static TypeCheckModel BuildTypeModelForModule(
        string moduleName,
        IReadOnlyList<TypedFunctionSignature> functions,
        params NamedTypeSymbol[] namedTypes)
    {
        return new TypeCheckModel(
            moduleName,
            namedTypes.ToDictionary(static namedType => namedType.Name, StringComparer.Ordinal),
            new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            functions.ToDictionary(static function => function.Name, StringComparer.Ordinal),
            new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal),
            [],
            []);
    }

    private static NamedTypeSymbol BuildNamedType(string name, IReadOnlyList<FieldSymbol> fields)
    {
        return new NamedTypeSymbol(
            name,
            DeclarationKind.Struct,
            fields.ToDictionary(static field => field.Name, StringComparer.Ordinal),
            fields);
    }

    private static NamedTypeSymbol BuildPairType()
    {
        return BuildNamedType(
            "Pair",
            [
                new FieldSymbol("Value", I32),
                new FieldSymbol("Flag", StarkTypeSymbols.Bool)
            ]);
    }

    private static AbiModel BuildAbiModel(params AbiFunctionSignature[] functions)
    {
        var abiFunctions = functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);
        if (!abiFunctions.ContainsKey("Run"))
        {
            abiFunctions["Run"] = new AbiFunctionSignature(
                "Run",
                "Run",
                StarkTypeSymbols.Void,
                StarkTypeSymbols.Void,
                [],
                IsFfi: false);
        }

        return new AbiModel("Demo", abiFunctions);
    }

    private static IReadOnlyList<TypedParameterSymbol> BuildPointerParameters(StarkTypeSymbol pointer)
    {
        return
        [
            new TypedParameterSymbol("left", pointer),
            new TypedParameterSymbol("right", pointer)
        ];
    }

    private static AbiModel BuildRunPointerAbi(StarkTypeSymbol pointer)
    {
        return BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol("left", "arg_left", pointer, pointer, AbiParameterKind.Direct),
                new AbiParameterSymbol("right", "arg_right", pointer, pointer, AbiParameterKind.Direct)
            ],
            IsFfi: false));
    }

    private static ScopedNoAliasGroup BuildAliasProofGroup(
        AliasProofCarrierKind kind,
        string proofId,
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? proofRoots = null)
    {
        return new ScopedNoAliasGroup(
            proofId,
            roots,
            new AliasProofCarrier(kind, proofId, proofRoots ?? roots));
    }

    private static SsaFunction BuildVoidFunction(
        IReadOnlyList<SsaInstruction> instructions,
        IReadOnlyList<TypedParameterSymbol>? parameters = null,
        SsaTerminator? terminator = null)
    {
        return new SsaFunction(
            "Run",
            StarkTypeSymbols.Void,
            Parameters: parameters ?? [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks:
            [
                new SsaBasicBlock(
                    0,
                    "entry",
                    Phis: [],
                    Instructions: instructions,
                    Terminator: terminator ?? new SsaTerminator(SsaTerminatorKind.Return, []))
            ],
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private static SsaFunction BuildReturningFunction(
        StarkTypeSymbol returnType,
        SsaValue returnValue,
        IReadOnlyList<SsaInstruction>? instructions = null,
        IReadOnlyList<TypedParameterSymbol>? parameters = null)
    {
        return new SsaFunction(
            "Run",
            returnType,
            Parameters: parameters ?? [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks:
            [
                new SsaBasicBlock(
                    0,
                    "entry",
                    Phis: [],
                    Instructions: instructions ?? [],
                    Terminator: new SsaTerminator(SsaTerminatorKind.Return, [], Value: returnValue))
            ],
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private static SsaFunction BuildVoidMultiBlockFunction(params SsaBasicBlock[] blocks)
    {
        return new SsaFunction(
            "Run",
            StarkTypeSymbols.Void,
            Parameters: [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks: blocks,
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private static SsaFunction BuildSwitchFunction(
        SsaValue condition,
        IReadOnlyList<SsaSwitchCase> switchCases)
    {
        return new SsaFunction(
            "Run",
            StarkTypeSymbols.Void,
            Parameters: [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks:
            [
                new SsaBasicBlock(
                    0,
                    "entry",
                    Phis: [],
                    Instructions: [],
                    Terminator: new SsaTerminator(
                        SsaTerminatorKind.Switch,
                        [1, 2],
                        Condition: condition,
                        SwitchCases: switchCases,
                        DefaultTarget: 2)),
                new SsaBasicBlock(
                    1,
                    "case",
                    Phis: [],
                    Instructions: [],
                    Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])),
                new SsaBasicBlock(
                    2,
                    "default",
                    Phis: [],
                    Instructions: [],
                    Terminator: new SsaTerminator(SsaTerminatorKind.Return, []))
            ],
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private sealed record UnsupportedSsaInstruction : SsaInstruction;

    private sealed record UnsupportedSsaRValue()
        : SsaRValue(StarkTypeSymbols.Integer(32), "unsupported-rvalue");

    private sealed record UnsupportedSsaValue()
        : SsaValue(StarkTypeSymbols.Integer(32), "unsupported-value");

    private static void AssertDiagnostic(
        IReadOnlyList<CompilerDiagnostic> diagnostics,
        string code,
        params string[] messageFragments)
    {
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
