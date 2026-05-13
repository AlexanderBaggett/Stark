using Stark.Compiler;

namespace compiler.Tests;

public sealed class LoweringContractValidationTests
{
    private const string ContractSource = """
        module Demo

        enum Status {
            Ok,
            Err(i32[min max]),
        }

        struct Box {
            i32[min max] Value;

            fn i32[min max] Get(borrow Box self) {
                return self.Value;
            }

            fn bool Fill(borrow Box self, out i32[min max] value) {
                value = self.Value;
                return true;
            }
        }

        fn i32[min max] Inc(i32[min max] value) {
            return value + 1;
        }

        unsafe fn i32[min max] Apply(fnptr<fn i32[min max](i32[min max])> op, i32[min max] value) {
            return op(value);
        }

        fn bool Write(out i32[min max] value) {
            value = 9;
            return true;
        }

        unsafe fn bool ApplyOut(fnptr<fn bool(out i32[min max])> op, out i32[min max] value) {
            return op(value);
        }

        fn i32[min max] Choose(bool flag) {
            switch (flag) {
                case true:
                    return 1;
                case false:
                    return 0;
            }
        }

        fn i32[min max] Score(Status status) {
            switch (status) {
                case Status.Ok:
                    return 1;
                case Status.Err(var error):
                    return error;
            }
        }

        unsafe fn i32[min max] Run() {
            stack mut Box box = new Box() { Value = 3 };
            stack mut i32[min max][2] values = { 4, 5 };
            stack fnptr<fn i32[min max](i32[min max])> lambda = (i32[min max] value) => Inc(value);
            stack mut dynamic u32[0 max] items = new(1);
            stack i64[min max] boxSize = sizeof(Box);
            stack i64[min max] boxAlign = alignof(Box);
            values[0] = Inc(box.Get());
            items.Reserve(1);
            if (!box.Fill(values[1])) {
                return 0;
            }
            if (!ApplyOut(Write, values[1])) {
                return 0;
            }
            return values[0] + values[1] + Apply(Inc, 2) + Apply(lambda, 3) + Choose(true) + Score(Status.Err(4));
        }
        """;

    [Fact]
    public void ValidateLoweringContractRunsBeforeHirAndAcceptsTypedOperationFacts()
    {
        var result = Compile(ContractSource, "validate-lowering-contract");

        AssertSucceeded(result);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoweringContractValidation, out LoweringContractValidationModel? model));
        Assert.NotNull(model);
        Assert.Equal("Demo", model.ModuleName);
        Assert.True(model.CheckedFunctionCount >= 4);
        Assert.True(model.CheckedCallCount >= 5);
        Assert.True(model.CheckedIndexAccessCount >= 2);
        Assert.True(model.CheckedObjectCreationCount >= 2);
        Assert.True(model.CheckedLambdaCount >= 1);
        Assert.True(model.CheckedTypeLayoutExpressionCount >= 2);
        Assert.True(model.CheckedDynamicStorageOperationCount >= 1);
        Assert.True(model.CheckedSwitchCount >= 2);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "validate-lowering-contract"
                && execution.Status == PassExecutionStatus.Executed);
        Assert.DoesNotContain(
            result.Executions,
            execution => execution.PassId == "lower-hir"
                && execution.Status == PassExecutionStatus.Executed);
    }

    [Fact]
    public void MissingTypedCallFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var brokenModel = typeModel with { DirectCallRecords = [] };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("typed call facts", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingTypedIndexFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var brokenModel = typeModel with { IndexAccessRecords = [] };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("typed indexing facts", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDynamicStorageOperationFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var brokenModel = typeModel with { DynamicStorageOperationRecords = [] };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("dynamic-storage operation", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSwitchFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var brokenModel = typeModel with { SwitchRecords = [] };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("typed switch-lowering facts", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingEnumPatternFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var brokenModel = typeModel with { EnumPatternRecords = [] };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("typed enum or aggregate-pattern facts", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedDirectCallArityFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var corruptedCalls = typeModel.DirectCalls
            .Select(static record => record with
            {
                Signature = record.Signature with
                {
                    Parameters = []
                }
            })
            .ToArray();
        var brokenModel = typeModel with { DirectCallRecords = corruptedCalls };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("arity mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedDirectCallArgumentAddressFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        Assert.Contains(typeModel.DirectCalls, static record => record.Arguments.Any(static argument => argument.RequiresAddressable));
        var corruptedCalls = typeModel.DirectCalls
            .Select(static record => record with
            {
                ArgumentRecords = record.Arguments
                    .Select(static argument => argument.RequiresAddressable
                        ? argument with
                        {
                            ArgumentIsAddressable = false,
                            ArgumentIsMutable = false
                        }
                        : argument)
                    .ToArray()
            })
            .ToArray();
        var brokenModel = typeModel with { DirectCallRecords = corruptedCalls };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires an addressable argument", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires a mutable argument", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedMemberCallExplicitArgumentAddressFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        Assert.Contains(typeModel.MemberCalls, static record => record.Arguments.Any(static argument => !argument.IsReceiver && argument.RequiresAddressable));
        var corruptedCalls = typeModel.MemberCalls
            .Select(static record => record with
            {
                ArgumentRecords = record.Arguments
                    .Select(static argument => !argument.IsReceiver && argument.RequiresAddressable
                        ? argument with
                        {
                            ArgumentIsAddressable = false,
                            ArgumentIsMutable = false
                        }
                        : argument)
                    .ToArray()
            })
            .ToArray();
        var brokenModel = typeModel with { MemberCallRecords = corruptedCalls };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires an addressable argument", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires a mutable argument", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedIndirectCallArgumentAddressFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        Assert.Contains(typeModel.IndirectCalls, static record => record.Arguments.Any(static argument => argument.RequiresAddressable));
        var corruptedCalls = typeModel.IndirectCalls
            .Select(static record => record with
            {
                ArgumentRecords = record.Arguments
                    .Select(static argument => argument.RequiresAddressable
                        ? argument with
                        {
                            ArgumentIsAddressable = false,
                            ArgumentIsMutable = false
                        }
                        : argument)
                    .ToArray()
            })
            .ToArray();
        var brokenModel = typeModel with { IndirectCallRecords = corruptedCalls };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires an addressable argument", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires a mutable argument", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedIndexArityFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var corruptedIndexes = typeModel.IndexAccesses
            .Select(static record => record with { IndexCount = record.IndexCount + 1 })
            .ToArray();
        var brokenModel = typeModel with { IndexAccessRecords = corruptedIndexes };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("Typed index fact", StringComparison.Ordinal)
                && diagnostic.Message.Contains("arity mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedDynamicStorageOperationNameFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var corruptedOperations = typeModel.DynamicStorageOperations
            .Select(static record => record with { OperationName = "MoveLast" })
            .ToArray();
        var brokenModel = typeModel with { DynamicStorageOperationRecords = corruptedOperations };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("Typed dynamic-storage operation fact names", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedDynamicStorageReceiverAddressFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var corruptedOperations = typeModel.DynamicStorageOperations
            .Select(static record => record with
            {
                ReceiverIsAddressable = false,
                ReceiverIsMutable = false
            })
            .ToArray();
        var brokenModel = typeModel with { DynamicStorageOperationRecords = corruptedOperations };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires an addressable dynamic receiver", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("requires a mutable dynamic receiver", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedLayoutQueryTargetFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var corruptedLayouts = typeModel.TypeLayoutExpressions
            .Select(static record => record with { TargetType = StarkTypeSymbols.Named("MissingLayout") })
            .ToArray();
        var brokenModel = typeModel with { TypeLayoutExpressionRecords = corruptedLayouts };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("no concrete layout is available", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptedSwitchFamilyFactsFailBeforeMir()
    {
        var (loadedModules, typeModel) = CompileThroughOwnership(ContractSource);
        var corruptedSwitches = typeModel.Switches
            .Select(static record => record with
            {
                Family = string.Equals(record.Family, SwitchLoweringFamilies.Native, StringComparison.Ordinal)
                    ? SwitchLoweringFamilies.Guarded
                    : SwitchLoweringFamilies.Native
            })
            .ToArray();
        var brokenModel = typeModel with { SwitchRecords = corruptedSwitches };
        var context = CreateValidationContext(ContractSource);

        new LoweringContractValidator(context, loadedModules, brokenModel).Validate();

        Assert.Contains(
            context.Diagnostics.Items,
            diagnostic => diagnostic.Code == "STK5003"
                && diagnostic.Stage == "validate-lowering-contract"
                && diagnostic.Message.Contains("Typed switch fact records lowering family", StringComparison.Ordinal));
    }

    private static (LoadedModuleSet LoadedModules, TypeCheckModel TypeModel) CompileThroughOwnership(string source)
    {
        var result = Compile(source, "ownership-validate");
        AssertSucceeded(result);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.NotNull(loadedModules);
        Assert.NotNull(typeModel);
        return (loadedModules, typeModel);
    }

    private static CompilerPassContext CreateValidationContext(string source)
    {
        var state = new CompilationState(new CompilationInput(source, "Demo.stark"), new CompilerOptions());
        return new CompilerPassContext(state);
    }

    private static CompilationResult Compile(string source, string stopAfterPassId)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source, "Demo.stark"),
            new CompilerOptions(StopAfterPassId: stopAfterPassId));
    }

    private static void AssertSucceeded(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
