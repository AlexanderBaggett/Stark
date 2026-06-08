using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class ThreadSafetyLawFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void LawFactsDeriveStructurallyAndApplyUnsafeReferenceDefaultsAndOverrides()
    {
        var result = Compile(
            """
            module Demo

            struct Plain
            {
                i32[min max] Value;
            }

            struct PointerBox
            {
                rawptr<i32[min max]> Pointer;
            }

            struct StoredBorrowBox
            {
                storeborrow Plain Value;
            }

            [Grant(Shareable)]
            struct SharedHandle
            {
                rawptr<i32[min max]> Pointer;
            }

            [Deny(Transferable)]
            struct LocalToken
            {
                i64[min max] Slot;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var model = GetTypeModel(result);

        Assert.True(model.ThreadSafetyLawFacts["Plain"].Transferable.Holds);
        Assert.True(model.ThreadSafetyLawFacts["Plain"].Shareable.Holds);

        var pointerFacts = model.ThreadSafetyLawFacts["PointerBox"];
        Assert.False(pointerFacts.Transferable.Holds);
        Assert.False(pointerFacts.Shareable.Holds);
        Assert.Contains(pointerFacts.Transferable.FailureReasons, static failure =>
            failure.Kind == ThreadSafetyLawFailureKind.RawPointer
            && failure.Path.SequenceEqual(["Pointer"]));

        var storedBorrowFacts = model.ThreadSafetyLawFacts["StoredBorrowBox"];
        Assert.False(storedBorrowFacts.Transferable.Holds);
        Assert.False(storedBorrowFacts.Shareable.Holds);
        Assert.Contains(storedBorrowFacts.Shareable.FailureReasons, static failure =>
            failure.Kind == ThreadSafetyLawFailureKind.StoredBorrow
            && failure.Path.SequenceEqual(["Value"]));

        var sharedHandleFacts = model.ThreadSafetyLawFacts["SharedHandle"];
        Assert.False(sharedHandleFacts.Transferable.Holds);
        Assert.True(sharedHandleFacts.Shareable.Holds);

        var localTokenFacts = model.ThreadSafetyLawFacts["LocalToken"];
        Assert.False(localTokenFacts.Transferable.Holds);
        Assert.True(localTokenFacts.Shareable.Holds);
    }

    [Fact]
    public void ConditionalGrantsPropagateThroughGenericInstantiation()
    {
        var result = Compile(
            """
            module Demo

            struct Audited
            {
                [Grant(Transferable)]
                rawptr<i32[min max]> Pointer;
            }

            struct RawHolder
            {
                rawptr<i32[min max]> Pointer;
            }

            struct Sync<T>
            {
                [Grant(Shareable) where Transferable(T)]
                T Payload;
            }

            struct Uses
            {
                Sync<Audited> Good;
                Sync<RawHolder> Bad;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var model = GetTypeModel(result);

        var templateShareable = model.ThreadSafetyLawFacts["Sync"].Shareable;
        Assert.True(templateShareable.Holds);
        Assert.True(templateShareable.IsConditional);
        var templateRequirement = Assert.Single(templateShareable.RequiredPredicates);
        Assert.Equal(ThreadSafetyLawNames.Transferable, templateRequirement.LawName);
        Assert.Equal("T", templateRequirement.Type.DisplayName);

        Assert.True(model.ThreadSafetyLawFacts["Sync<Audited>"].Shareable.Holds);
        Assert.False(model.ThreadSafetyLawFacts["Sync<RawHolder>"].Shareable.Holds);
        Assert.Contains(model.ThreadSafetyLawFacts["Sync<RawHolder>"].Shareable.FailureReasons, static failure =>
            failure.Kind == ThreadSafetyLawFailureKind.RawPointer
            && failure.Path.SequenceEqual(["Payload", "Pointer"]));
    }

    [Fact]
    public void ConflictingLawAttributesAreTypeCheckErrors()
    {
        var result = Compile(
            """
            module Demo

            struct Cache
            {
                [Grant(Shareable)]
                [Deny(Shareable)]
                i32[min max] Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3050"
            && diagnostic.Message.Contains("field 'Cache.Value' both grants and denies Shareable", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemThreadingAtomicsReceiveIntrinsicLawGrant()
    {
        var result = Compile(
            """
            module System.Threading

            struct AtomicI64
            {
                rawptr<i32[min max]> Backing;
            }

            struct Holder
            {
                AtomicI64 Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var model = GetTypeModel(result);

        Assert.True(model.ThreadSafetyLawFacts["AtomicI64"].Transferable.Holds);
        Assert.True(model.ThreadSafetyLawFacts["AtomicI64"].Shareable.Holds);
        Assert.True(model.ThreadSafetyLawFacts["Holder"].Transferable.Holds);
        Assert.True(model.ThreadSafetyLawFacts["Holder"].Shareable.Holds);
    }

    [Fact]
    public void FunctionLawPredicatesAreEnforcedAtCallSites()
    {
        var result = Compile(
            """
            module Demo

            struct Plain
            {
                i32[min max] Value;
            }

            fn void RequireBoth<T>(T value) where Transferable(T), Shareable(T)
            {
            }

            fn void Run()
            {
                stack Plain plain = new()
                {
                    Value = 1
                };
                RequireBoth(plain);
                RequireBoth<i32[min max]>(2);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void GenericFunctionLawPredicatesMustBePropagatedThroughOpenCalls()
    {
        var result = Compile(
            """
            module Demo

            fn void RequireTransfer<T>(T value) where Transferable(T)
            {
            }

            fn void Relay<T>(T value) where Transferable(T)
            {
                RequireTransfer(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void MissingGenericFunctionLawPredicatePropagationIsATypeCheckError()
    {
        var result = Compile(
            """
            module Demo

            fn void RequireTransfer<T>(T value) where Transferable(T)
            {
            }

            fn void Relay<T>(T value)
            {
                RequireTransfer(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3049"
            && diagnostic.Message.Contains("requires where Transferable(T)", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Relay", StringComparison.Ordinal)
            && diagnostic.Message.Contains("must declare `where Transferable(T)`", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionLawPredicateFailureNamesResponsibleFieldChain()
    {
        var result = Compile(
            """
            module Demo

            struct RawHolder
            {
                rawptr<i32[min max]> Pointer;
            }

            fn void RequireTransfer<T>(T value) where Transferable(T)
            {
            }

            fn void Run()
            {
                stack RawHolder raw = new()
                {
                    Pointer = null
                };
                RequireTransfer(raw);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3049"
            && diagnostic.Message.Contains("requires where Transferable(RawHolder)", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Responsible field chain: RawHolder.Pointer", StringComparison.Ordinal)
            && diagnostic.Message.Contains("raw pointer", StringComparison.Ordinal));
    }

    [Fact]
    public void MethodLawPredicatesAreEnforcedAtCallSites()
    {
        var result = Compile(
            """
            module Demo

            struct LocalOnly
            {
                [Deny(Shareable)]
                i32[min max] Value;
            }

            struct Gate
            {
                fn void Broadcast(LocalOnly value) where Shareable(LocalOnly)
                {
                }
            }

            fn void Run()
            {
                stack Gate gate = new();
                stack LocalOnly local = new()
                {
                    Value = 1
                };
                gate.Broadcast(local);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3049"
            && diagnostic.Message.Contains("requires where Shareable(LocalOnly)", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Responsible field chain: LocalOnly.Value", StringComparison.Ordinal)
            && diagnostic.Message.Contains("explicitly denies Shareable", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportedSourceFunctionLawPredicatesAreEnforcedAtCallSites()
    {
        var result = Compile(
            """
            import Worker
            module Demo

            struct RawHolder
            {
                rawptr<i32[min max]> Pointer;
            }

            fn void Run()
            {
                stack RawHolder raw = new()
                {
                    Pointer = null
                };
                Worker.RequireTransfer(raw);
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Worker", "/virtual/Worker.stark", IsExternal: false),
                        """
                        module Worker

                        public fn void RequireTransfer<T>(T value) where Transferable(T)
                        {
                        }
                        """,
                        "/virtual/Worker.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3049"
            && diagnostic.Message.Contains("Worker.RequireTransfer", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Transferable(RawHolder)", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Responsible field chain: RawHolder.Pointer", StringComparison.Ordinal));
    }

    private static TypeCheckModel GetTypeModel(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        return model!;
    }
}
