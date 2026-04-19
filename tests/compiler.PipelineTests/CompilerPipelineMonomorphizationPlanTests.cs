using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineMonomorphizationPlanTests
{
    [Fact]
    public void RootGenericInstantiationsGetFullySpelledMonomorphizationSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair<T>(T Value) { }

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32[-2147483648 2147483647] Run(Pair<i32[-2147483648 2147483647]> pair) {
                    return Identity(pair.Value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var function = Assert.Single(plan.Functions);
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
        Assert.Equal(1, function.EstimatedTopLevelStatementCount);
        Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", function.SymbolName);

        var type = Assert.Single(plan.Types);
        Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, type.Linkage);
        Assert.Equal("__stark_mono_ty_Demo__Pair__i32", type.SymbolName);
    }


    [Fact]
    public void RootSingleReturnForwarderGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn T Forward<T>(T value) {
                    return Identity(value);
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return Forward(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var forward = Assert.Single(plan.Functions, static function => function.TemplateName == "Forward");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, forward.CodeSizeHeuristic);
        Assert.True(forward.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnMemberCallForwarderGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Box(i32[-2147483648 2147483647] Value) {
                    fn i32[-2147483648 2147483647] Bump(borrow Box self, i32[-2147483648 2147483647] delta) {
                        return self.Value + delta;
                    }
                }

                fn i32[-2147483648 2147483647] Forward<T>(borrow Box box, i32[-2147483648 2147483647] delta, T tag) {
                    return box.Bump(delta);
                }

                fn i32[-2147483648 2147483647] Run(borrow Box box, i32[-2147483648 2147483647] delta) {
                    return Forward(box, delta, delta);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var forward = Assert.Single(plan.Functions, static function => function.TemplateName == "Forward");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, forward.CodeSizeHeuristic);
        Assert.True(forward.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnFieldAccessWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Inner(i32[-2147483648 2147483647] Value) { }
                record Box(Inner Inner) { }

                fn i32[-2147483648 2147483647] Read<T>(borrow Box box, T tag) {
                    return box.Inner.Value;
                }

                fn i32[-2147483648 2147483647] Run(borrow Box box) {
                    return Read(box, 0);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var read = Assert.Single(plan.Functions, static function => function.TemplateName == "Read");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, read.CodeSizeHeuristic);
        Assert.True(read.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnIndexAccessWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Box(i32[-2147483648 2147483647] Value) { }

                fn i32[-2147483648 2147483647] Read<T>(Box[2] boxes, i32[-2147483648 2147483647] index, T tag) {
                    return boxes[index].Value;
                }

                fn i32[-2147483648 2147483647] Run(Box[2] boxes, i32[-2147483648 2147483647] index) {
                    return Read(boxes, index, index);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var read = Assert.Single(plan.Functions, static function => function.TemplateName == "Read");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, read.CodeSizeHeuristic);
        Assert.True(read.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnConversionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Inner(i32[-2147483648 2147483647] Value) { }
                record Box(Inner Inner) { }

                fn i64[-9223372036854775808 9223372036854775807] Read<T>(borrow Box box, T tag) {
                    return (i64[-9223372036854775808 9223372036854775807])box.Inner.Value;
                }

                fn i64[-9223372036854775808 9223372036854775807] Run(borrow Box box) {
                    return Read(box, box.Inner.Value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var read = Assert.Single(plan.Functions, static function => function.TemplateName == "Read");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, read.CodeSizeHeuristic);
        Assert.True(read.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnAddressOfWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Buffer(i32[-2147483648 2147483647][2] Values) { }

                fn rawptr<i32[-2147483648 2147483647]> Pin<T>(borrow Buffer buffer, i32[-2147483648 2147483647] index, T tag) {
                    return &buffer.Values[index];
                }

                fn rawptr<i32[-2147483648 2147483647]> Run(borrow Buffer buffer, i32[-2147483648 2147483647] index) {
                    return Pin(buffer, index, index);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var pin = Assert.Single(plan.Functions, static function => function.TemplateName == "Pin");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, pin.CodeSizeHeuristic);
        Assert.True(pin.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnBinaryOperatorWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Inner(i32[-2147483648 2147483647] Value) { }
                record Box(Inner Inner) { }

                fn i32[-2147483648 2147483647] AddDelta<T>(borrow Box box, i32[-2147483648 2147483647] delta, T tag) {
                    return box.Inner.Value + delta;
                }

                fn i32[-2147483648 2147483647] Run(borrow Box box, i32[-2147483648 2147483647] delta) {
                    return AddDelta(box, delta, delta);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var addDelta = Assert.Single(plan.Functions, static function => function.TemplateName == "AddDelta");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, addDelta.CodeSizeHeuristic);
        Assert.True(addDelta.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void RootSingleReturnComparisonWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Inner(i32[-2147483648 2147483647] Value) { }
                record Box(Inner Inner) { }

                fn bool IsBelow<T>(borrow Box box, i32[-2147483648 2147483647] limit, T tag) {
                    return box.Inner.Value < limit;
                }

                fn bool Run(borrow Box box, i32[-2147483648 2147483647] limit) {
                    return IsBelow(box, limit, limit);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var isBelow = Assert.Single(plan.Functions, static function => function.TemplateName == "IsBelow");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, isBelow.CodeSizeHeuristic);
        Assert.True(isBelow.EstimatedBodyCost is > 2);
    }

    [Fact]
    public void RootTerminalIfSelectionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] ChooseBranch<T>(bool takeLeft, bool takeMiddle, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right, T tag) {
                    if (takeLeft) {
                        return left;
                    } else if (takeMiddle) {
                        return middle;
                    } else {
                        return right;
                    }
                }

                fn i32[-2147483648 2147483647] Run(bool takeLeft, bool takeMiddle, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right) {
                    return ChooseBranch(takeLeft, takeMiddle, left, middle, right, right);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var chooseBranch = Assert.Single(plan.Functions, static function => function.TemplateName == "ChooseBranch");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, chooseBranch.CodeSizeHeuristic);
        Assert.True(chooseBranch.EstimatedBodyCost is > 2);
    }

    [Fact]
    public void RootTerminalSwitchSelectionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] ChooseSwitch<T>(i32[-2147483648 2147483647] selector, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right, T tag) {
                    switch (selector) {
                        case 0:
                            return left;
                        case 1:
                            return middle;
                        default:
                            return right;
                    }
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] selector, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right) {
                    return ChooseSwitch(selector, left, middle, right, right);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var chooseSwitch = Assert.Single(plan.Functions, static function => function.TemplateName == "ChooseSwitch");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, chooseSwitch.CodeSizeHeuristic);
        Assert.True(chooseSwitch.EstimatedBodyCost is > 2);
    }

    [Fact]
    public void RootObjectConstructionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Inner<T> {
                    T Value;
                }

                struct Outer<T> {
                    Inner<T> Item;
                    i32[-2147483648 2147483647] Count;
                }

                fn Outer<T> Wrap<T>(T value, i32[-2147483648 2147483647] count, T tag) {
                    return new Outer<T>() {
                        Item = { Value = value },
                        Count = count
                    };
                }

                fn Outer<i32[-2147483648 2147483647]> Run(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] count) {
                    return Wrap(value, count, value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var wrap = Assert.Single(plan.Functions, static function => function.TemplateName == "Wrap");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, wrap.CodeSizeHeuristic);
        Assert.NotNull(wrap.EstimatedBodyCost);
    }

    [Fact]
    public void RootEnumConstructionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                enum Boxed<T> {
                    None,
                    Value { Data: T, Tag: i32[-2147483648 2147483647] },
                }

                fn Boxed<T> Wrap<T>(T value, i32[-2147483648 2147483647] tag, T marker) {
                    return Boxed<T>.Value { Data: value, Tag: tag };
                }

                fn Boxed<i32[-2147483648 2147483647]> Run(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] tag) {
                    return Wrap(value, tag, value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var wrap = Assert.Single(plan.Functions, static function => function.TemplateName == "Wrap");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, wrap.CodeSizeHeuristic);
        Assert.NotNull(wrap.EstimatedBodyCost);
    }

    [Fact]
    public void RootSimpleLocalUpdateWrapperGenericInstantiationsUseOptimizationSummaryForPlanning()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Inner(i32[-2147483648 2147483647] Value) { }
                record Box(Inner Inner) { }

                fn i32[-2147483648 2147483647] Bump<T>(borrow Box box, i32[-2147483648 2147483647] delta, T tag) {
                    stack mut i32[-2147483648 2147483647] current = box.Inner.Value;
                    current += delta;
                    return current;
                }

                fn i32[-2147483648 2147483647] Run(borrow Box box, i32[-2147483648 2147483647] delta) {
                    return Bump(box, delta, delta);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var bump = Assert.Single(plan.Functions, static function => function.TemplateName == "Bump");
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, bump.CodeSizeHeuristic);
        Assert.True(bump.EstimatedBodyCost is > 2);
    }


    [Fact]
    public void SourceBackedImportedGenericInstantiationsUseDefiningModuleMonomorphizationSymbols()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-mono-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Facade.stark"),
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value) {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(Facade.Pair<i32[-2147483648 2147483647]> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal(1, function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.LinkOnceOdrComdat, function.Linkage);
            Assert.Equal("__stark_mono_fn_Facade__Facade_Identity__i32", function.SymbolName);

            var type = Assert.Single(plan.Types);
            Assert.Equal(MonomorphizationLinkageKind.LinkOnceOdrComdat, type.Linkage);
            Assert.Equal("__stark_mono_ty_Facade__Facade_Pair__i32", type.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedGenericInstantiationsUseRootOwnedMonomorphizationSymbols()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(Facade.Pair<i32[-2147483648 2147483647]> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.DeclarationOnly, function.CodeSizeHeuristic);
            Assert.Null(function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Identity__i32", function.SymbolName);

            var type = Assert.Single(plan.Types);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, type.Linkage);
            Assert.Equal("__stark_mono_ty_Demo__Facade_Pair__i32", type.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedColdGenericInstantiationsPreservePackageImagePlanningFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-cold-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public cold fn T Choose<T>(T left, T right, bool takeRight) {
                    stack mut T current = left;
                    if (takeRight) {
                        current = right;
                    }

                    return current;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right, bool takeRight) {
                        return Facade.Choose(left, right, takeRight);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Choose", out var templateSummary));
            Assert.Equal(3, templateSummary.TopLevelStatementCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
            Assert.Equal(3, function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Choose__i32", function.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedTerminalSelectionGenericBodiesUseOptimizationSummaryForPlanning()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-body-complexity-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[-2147483648 2147483647] Classify<T>(i32[-2147483648 2147483647] value, T tag) {
                    switch (value) {
                        case 0:
                            return 10;
                        case 1:
                            return 11;
                        case 2:
                            return 12;
                        default:
                            return value;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                        return Facade.Classify(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Classify", out var templateSummary));
            Assert.Equal(1, templateSummary.TopLevelStatementCount);
            Assert.True(templateSummary.EstimatedBodyCost is > 2);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal(1, function.EstimatedTopLevelStatementCount);
            Assert.Equal(templateSummary.EstimatedBodyCost, function.EstimatedBodyCost);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Classify__i32", function.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public void ManifestBackedGenericPlanningUsesPublishedTemplateSummaryWhenImportedBridgeBodyIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-template-summary-corrupted-bridge-plan-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[-2147483648 2147483647] Classify<T>(i32[-2147483648 2147483647] value, T tag) {
                    switch (value) {
                        case 0:
                            return 10;
                        case 1:
                            return 11;
                        case 2:
                            return 12;
                        default:
                            return value;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.True(importedDocument.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Classify", out var templateSummary));
            Assert.Equal(1, templateSummary.TopLevelStatementCount);
            Assert.True(templateSummary.EstimatedBodyCost is > 2);
            Assert.True(templateSummary.OptimizationSummary?.IsTerminalSelectionWrapper);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                StrictIntegerSource("public fn i32 Classify<T>(i32 value, T tag);"),
                """
                public fn i32[-2147483648 2147483647] Classify<T>(i32[-2147483648 2147483647] value, T tag) {
                    stack mut i32[-2147483648 2147483647] total = value;
                    total = total + 1;
                    total = total + 2;
                    return total;
                }
                """,
                StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                        return Facade.Classify(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal(1, function.EstimatedTopLevelStatementCount);
            Assert.Equal(templateSummary.EstimatedBodyCost, function.EstimatedBodyCost);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Classify__i32", function.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedSingleReturnForwarderGenericBodiesUseOptimizationSummaryForPlanning()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-forwarder-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Forward");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnDirectCallForwarder);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.DirectCallCount);
            Assert.Equal(0, publishedTemplate.Semantics.Optimization.MemberCallCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? WithEffectiveLegacyCompilerSectionCopies(module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                }
                            }
                        })
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                        return Facade.Forward(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Forward", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnDirectCallForwarder);
            Assert.Equal(1, importedSemantics.OptimizationSummary.DirectCallCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var forward = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Forward");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, forward.CodeSizeHeuristic);
            Assert.True(forward.EstimatedBodyCost is > 2);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Forward__i32", forward.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedLargeAggregateGenericInstantiationsPreferCodeSizeReductionFromPublishedLayoutFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-layout-aware-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Big(i64[-9223372036854775808 9223372036854775807] A, i64[-9223372036854775808 9223372036854775807] B, i64[-9223372036854775808 9223372036854775807] C) { }

                public fn T Bounce<T>(T value) {
                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn Facade.Big Run(Facade.Big value) {
                        return Facade.Bounce(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.ConcreteLayouts.ContainsKey("Facade.Big"));

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
            Assert.Equal(1, function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Bounce__Facade_Big", function.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void RepeatedManifestBackedNestedGenericInstantiationsStayRootOwnedAndDeduplicatedInMonomorphizationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-nested-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                        return Facade.Forward(left) + Facade.Forward(right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Equal(2, plan.Functions.Count);

            var forward = Assert.Single(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Equal("Facade.Forward", forward.TemplateName);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, forward.Linkage);

            var identity = Assert.Single(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.Equal("Facade.Identity", identity.TemplateName);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, identity.Linkage);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedNestedGenericPlanningUsesPublishedDeferredTriggers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-published-deferred-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var rewrittenTemplates = facadeModule.GenericTemplates!.Functions
                .Select(template => template.QualifiedResolvedName == "Facade.Forward"
                    ? template with { BodyText = "{\n    return value;\n}" }
                    : template)
                .ToArray();

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(rewrittenTemplates)
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                        return Facade.Forward(left) + Facade.Forward(right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Equal(2, plan.Functions.Count);

            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Identity__i32");
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedNestedGenericTypePlanningUsesPublishedDeferredTypeTriggers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-published-deferred-generic-type-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<A, B>(A First, B Second) { }

                public fn i32[-2147483648 2147483647] Forward<T>(T value, bool flag) {
                    stack Pair<T, bool> pair = new Pair<T, bool>(value, flag);
                    return pair.Second ? 1 : 0;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var rewrittenTemplates = facadeModule.GenericTemplates!.Functions
                .Select(template => template.QualifiedResolvedName == "Facade.Forward"
                    ? template with { BodyText = "{\n    return 0;\n}" }
                    : template)
                .ToArray();

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(rewrittenTemplates)
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value, bool flag) {
                        return Facade.Forward(value, flag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Forward__i32", function.SymbolName);

            var type = Assert.Single(plan.Types);
            Assert.Equal("Facade.Pair", type.TemplateName);
            Assert.Equal("Facade.Pair<i32,bool>", type.InstantiatedTypeName);
            Assert.Equal("__stark_mono_ty_Demo__Facade_Pair__i32__bool", type.SymbolName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ColdGenericInstantiationsPreferCodeSizeReductionInThePlan()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                cold fn T Choose<T>(T left, T right, bool takeRight) {
                    stack mut T current = left;
                    if (takeRight) {
                        current = right;
                    }

                    return current;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right, bool takeRight) {
                    return Choose(left, right, takeRight);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var function = Assert.Single(plan.Functions);
        Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
        Assert.Equal(3, function.EstimatedTopLevelStatementCount);
        Assert.Equal("__stark_mono_fn_Demo__Choose__i32", function.SymbolName);
    }
}
