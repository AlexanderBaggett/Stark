using Stark.Compiler;
using System.Text;
using Xunit.Abstractions;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    private readonly ITestOutputHelper _output;

    public MidLevelIrLoweringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        using var logScope = CompilerLogOutput.Push(new TestOutputWriter(_output), DiagnosticSeverity.Info);
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static MidLevelIrModule GetMir(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        return mir;
    }

    internal static void AssertMirHasNoNullLoweringArtifacts(MidLevelIrModule mir)
    {
        foreach (var function in mir.Functions.Where(static function => function.Blocks.Count > 0))
        {
            foreach (var block in function.Blocks)
            {
                foreach (var statement in block.Statements)
                {
                    AssertWellFormedStatement(function, statement);
                }

                AssertWellFormedTerminator(function, block.Terminator);
            }
        }
    }

    private static bool IsDirectCallStatement(MidLevelIrStatement statement, string functionName)
    {
        return statement.Value is MidLevelIrCallRValue directValueCall
                   && string.Equals(directValueCall.FunctionName, functionName, StringComparison.Ordinal)
               || statement.Call is MidLevelIrDirectCallStatementOperation directStatementCall
                   && string.Equals(directStatementCall.FunctionName, functionName, StringComparison.Ordinal);
    }

    private static IEnumerable<MidLevelIrCallRValue> DirectValueCalls(MidLevelIrFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>();
    }

    private static IEnumerable<MidLevelIrDirectCallStatementOperation> DirectStatementCalls(MidLevelIrFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Call)
            .OfType<MidLevelIrDirectCallStatementOperation>();
    }

    private static void AssertWellFormedStatement(MidLevelIrFunction function, MidLevelIrStatement statement)
    {
        switch (statement.Kind)
        {
            case MidLevelIrStatementKind.StorageLive:
            case MidLevelIrStatementKind.StorageDead:
                Assert.False(string.IsNullOrWhiteSpace(statement.TargetName));
                Assert.NotNull(statement.TargetType);
                Assert.Null(statement.Value);
                Assert.Null(statement.Address);
                Assert.Null(statement.Call);
                return;
            case MidLevelIrStatementKind.ArenaFrameEnter:
            case MidLevelIrStatementKind.ArenaFrameLeave:
                Assert.Null(statement.TargetName);
                Assert.Null(statement.TargetType);
                Assert.Null(statement.Value);
                Assert.Null(statement.Address);
                Assert.Null(statement.Call);
                return;
            case MidLevelIrStatementKind.Assign:
                Assert.False(string.IsNullOrWhiteSpace(statement.TargetName));
                Assert.NotNull(statement.TargetType);
                Assert.NotNull(statement.Value);
                Assert.Null(statement.Address);
                Assert.Null(statement.Call);
                AssertWellFormedRValue(function, statement.Value);
                return;
            case MidLevelIrStatementKind.StoreIndirect:
                Assert.Null(statement.TargetName);
                Assert.NotNull(statement.TargetType);
                Assert.NotNull(statement.Address);
                Assert.NotNull(statement.Value);
                Assert.Null(statement.Call);
                AssertWellFormedOperand(function, statement.Address);
                AssertWellFormedRValue(function, statement.Value);
                return;
            case MidLevelIrStatementKind.Evaluate:
                Assert.Null(statement.TargetName);
                Assert.Null(statement.TargetType);
                Assert.Null(statement.Address);
                Assert.True((statement.Value is null) != (statement.Call is null));
                if (statement.Value is not null)
                {
                    AssertWellFormedRValue(function, statement.Value);
                }
                else
                {
                    AssertWellFormedCall(function, statement.Call!);
                }

                return;
            default:
                throw new Xunit.Sdk.XunitException($"Unhandled MIR statement kind '{statement.Kind}' in '{function.Name}'.");
        }
    }

    private static void AssertWellFormedTerminator(MidLevelIrFunction function, MidLevelIrTerminator terminator)
    {
        switch (terminator.Kind)
        {
            case MidLevelIrTerminatorKind.Goto:
            case MidLevelIrTerminatorKind.Unreachable:
                Assert.Null(terminator.Condition);
                Assert.Null(terminator.Value);
                return;
            case MidLevelIrTerminatorKind.Branch:
                Assert.NotNull(terminator.Condition);
                AssertWellFormedOperand(function, terminator.Condition);
                Assert.Null(terminator.Value);
                return;
            case MidLevelIrTerminatorKind.Switch:
                Assert.NotNull(terminator.Condition);
                AssertWellFormedOperand(function, terminator.Condition);
                foreach (var switchCase in terminator.SwitchCases ?? [])
                {
                    if (!switchCase.IsDefault)
                    {
                        Assert.NotNull(switchCase.MatchValue);
                        AssertWellFormedOperand(function, switchCase.MatchValue);
                    }
                }

                return;
            case MidLevelIrTerminatorKind.Return:
                if (function.ReturnType.Kind == StarkTypeKind.Void)
                {
                    Assert.Null(terminator.Value);
                }
                else if (terminator.ValueText is not null)
                {
                    Assert.NotNull(terminator.Value);
                    AssertWellFormedOperand(function, terminator.Value);
                }

                Assert.Null(terminator.Condition);
                return;
            default:
                throw new Xunit.Sdk.XunitException($"Unhandled MIR terminator kind '{terminator.Kind}' in '{function.Name}'.");
        }
    }

    private static void AssertWellFormedRValue(MidLevelIrFunction function, MidLevelIrRValue value)
    {
        Assert.NotNull(value.Type);
        Assert.False(string.IsNullOrWhiteSpace(value.Text));
        switch (value)
        {
            case MidLevelIrUseRValue use:
                AssertWellFormedOperand(function, use.Operand);
                return;
            case MidLevelIrUnaryRValue unary:
                AssertWellFormedOperand(function, unary.Operand);
                return;
            case MidLevelIrBinaryRValue binary:
                AssertWellFormedOperand(function, binary.Left);
                AssertWellFormedOperand(function, binary.Right);
                return;
            case MidLevelIrCallRValue directCall:
                Assert.NotEqual(StarkTypeKind.Void, directCall.Type.Kind);
                Assert.All(directCall.Arguments, argument => AssertWellFormedOperand(function, argument));
                Assert.All(directCall.PostCallDynamicLengthCommits ?? [], commit =>
                {
                    AssertWellFormedOperand(function, commit.StorageAddress);
                    AssertWellFormedOperand(function, commit.InitializedLength);
                });
                return;
            case MidLevelIrIndirectCallRValue indirectCall:
                Assert.NotEqual(StarkTypeKind.Void, indirectCall.Type.Kind);
                AssertWellFormedOperand(function, indirectCall.Target);
                Assert.All(indirectCall.Arguments, argument => AssertWellFormedOperand(function, argument));
                return;
            case MidLevelIrConvertRValue convert:
                AssertWellFormedOperand(function, convert.Operand);
                return;
            case MidLevelIrExtractFieldRValue field:
                AssertWellFormedOperand(function, field.Target);
                return;
            case MidLevelIrInsertFieldRValue field:
                AssertWellFormedOperand(function, field.Target);
                AssertWellFormedOperand(function, field.Value);
                return;
            case MidLevelIrExtractIndexRValue index:
                AssertWellFormedOperand(function, index.Target);
                return;
            case MidLevelIrInsertIndexRValue index:
                AssertWellFormedOperand(function, index.Target);
                AssertWellFormedOperand(function, index.Value);
                return;
            case MidLevelIrMakeSliceFromLocalRValue:
                return;
            case MidLevelIrMakeSliceFromPointerRValue pointerSlice:
                AssertWellFormedOperand(function, pointerSlice.Pointer);
                AssertWellFormedOperand(function, pointerSlice.Length);
                return;
            case MidLevelIrDynamicStorageAllocationRValue allocation:
                AssertWellFormedOperand(function, allocation.Capacity);
                return;
            case MidLevelIrDynamicStorageFreeRValue free:
                AssertWellFormedOperand(function, free.Storage);
                return;
            case MidLevelIrHeapStorageFreeRValue free:
                AssertWellFormedOperand(function, free.Pointer);
                return;
            case MidLevelIrDynamicStorageReserveRValue reserve:
                AssertWellFormedOperand(function, reserve.StorageAddress);
                AssertWellFormedOperand(function, reserve.AdditionalCapacity);
                return;
            case MidLevelIrDynamicStorageTryReserveRValue reserve:
                AssertWellFormedOperand(function, reserve.StorageAddress);
                AssertWellFormedOperand(function, reserve.AdditionalCapacity);
                return;
            case MidLevelIrDynamicStorageTryReserveCapacityRValue reserve:
                AssertWellFormedOperand(function, reserve.StorageAddress);
                AssertWellFormedOperand(function, reserve.TargetCapacity);
                return;
            case MidLevelIrDynamicStorageMoveLastRValue move:
                AssertWellFormedOperand(function, move.StorageAddress);
                return;
            case MidLevelIrDynamicStorageMoveAtRValue move:
                AssertWellFormedOperand(function, move.StorageAddress);
                AssertWellFormedOperand(function, move.Index);
                return;
            case MidLevelIrLoadSliceElementRValue load:
                AssertWellFormedOperand(function, load.Slice);
                AssertWellFormedOperand(function, load.Index);
                return;
            case MidLevelIrTextSliceRValue textSlice:
                AssertWellFormedOperand(function, textSlice.TextValue);
                AssertWellFormedOperand(function, textSlice.Start);
                AssertWellFormedOperand(function, textSlice.Length);
                return;
            case MidLevelIrAddressOfLocalRValue:
            case MidLevelIrAddressOfParameterRValue:
                return;
            case MidLevelIrFieldAddressRValue address:
                AssertWellFormedOperand(function, address.Address);
                return;
            case MidLevelIrElementAddressRValue address:
                AssertWellFormedOperand(function, address.Address);
                if (address.Index is not null)
                {
                    AssertWellFormedOperand(function, address.Index);
                }
                else
                {
                    Assert.NotNull(address.ConstantIndex);
                }

                return;
            case MidLevelIrSliceElementAddressRValue address:
                AssertWellFormedOperand(function, address.Slice);
                AssertWellFormedOperand(function, address.Index);
                return;
            case MidLevelIrLoadIndirectRValue load:
                AssertWellFormedOperand(function, load.Address);
                return;
            case MidLevelIrDynVTableSlotRValue slot:
                AssertWellFormedOperand(function, slot.VtablePointer);
                Assert.True(slot.SlotIndex >= 0);
                return;
            default:
                throw new Xunit.Sdk.XunitException($"Unhandled MIR rvalue '{value.GetType().Name}' in '{function.Name}'.");
        }
    }

    private static void AssertWellFormedCall(MidLevelIrFunction function, MidLevelIrCallStatementOperation call)
    {
        Assert.NotNull(call.ReturnType);
        Assert.False(string.IsNullOrWhiteSpace(call.Text));
        switch (call)
        {
            case MidLevelIrDirectCallStatementOperation directCall:
                Assert.All(directCall.Arguments, argument => AssertWellFormedOperand(function, argument));
                Assert.All(directCall.PostCallDynamicLengthCommits ?? [], commit =>
                {
                    AssertWellFormedOperand(function, commit.StorageAddress);
                    AssertWellFormedOperand(function, commit.InitializedLength);
                });
                return;
            case MidLevelIrIndirectCallStatementOperation indirectCall:
                AssertWellFormedOperand(function, indirectCall.Target);
                Assert.All(indirectCall.Arguments, argument => AssertWellFormedOperand(function, argument));
                return;
            default:
                throw new Xunit.Sdk.XunitException($"Unhandled MIR call statement '{call.GetType().Name}' in '{function.Name}'.");
        }
    }

    private static void AssertWellFormedOperand(MidLevelIrFunction function, MidLevelIrOperand operand)
    {
        Assert.NotNull(operand);
        Assert.NotNull(operand.Type);
        Assert.False(string.IsNullOrWhiteSpace(operand.Text), $"MIR operand in '{function.Name}' has no text.");
        switch (operand)
        {
            case MidLevelIrObjectConstructionOperand construction:
                Assert.Equal(construction.Facts.CreatedType, construction.Type);
                Assert.Equal(construction.Facts.CreatedType, construction.Value.Type);
                Assert.False(string.IsNullOrWhiteSpace(construction.Facts.TypeName));
                if (construction.Facts.Kind is MidLevelIrObjectConstructionKind.PrimaryConstructor
                    or MidLevelIrObjectConstructionKind.ExplicitConstructor
                    or MidLevelIrObjectConstructionKind.ConstructorAndInitializer)
                {
                    Assert.NotNull(construction.Facts.Constructor);
                }

                if (construction.Facts.Kind is MidLevelIrObjectConstructionKind.ExplicitConstructor)
                {
                    Assert.False(string.IsNullOrWhiteSpace(construction.Facts.ConstructorBodyKey));
                }

                Assert.All(construction.Facts.InitializerMembers, member =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(member.FieldName));
                    Assert.True(member.FieldIndex >= 0);
                    Assert.NotNull(member.FieldType);
                    Assert.NotEqual(StarkTypeKind.Error, member.FieldType.Kind);
                });
                AssertWellFormedOperand(function, construction.Value);
                return;
            case MidLevelIrEnumConstructionOperand construction:
                Assert.Equal(construction.Facts.EnumType, construction.Type);
                Assert.Equal(construction.Facts.EnumType, construction.Value.Type);
                Assert.NotNull(construction.Facts.Layout);
                Assert.NotNull(construction.Facts.Variant);
                Assert.Equal(construction.Facts.Variant.Fields.Count, construction.Facts.PayloadFields.Count);
                Assert.All(construction.Facts.PayloadFields, field =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(field.FieldName));
                    Assert.True(field.FieldIndex >= 0);
                    Assert.NotNull(field.FieldType);
                    Assert.True(field.StorageFieldIndex >= 0);
                    Assert.False(string.IsNullOrWhiteSpace(field.StorageFieldName));
                    Assert.NotNull(field.StorageFieldType);
                });
                AssertWellFormedOperand(function, construction.Value);
                return;
        }
    }

    private sealed class TestOutputWriter(ITestOutputHelper output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            output.WriteLine(value ?? string.Empty);
        }
    }
}
