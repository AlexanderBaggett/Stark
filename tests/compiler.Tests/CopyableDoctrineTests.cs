using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Structural copyability: enums whose variants carry no owning payloads,
/// and structs/records built from copyable fields, copy out of places
/// (fields, indexed dynamic slots, locals) instead of moving. Owning
/// payloads, destructors, and the builtin owned-text structs stay move-only.
/// Motivated by the Stage 1 lexer's TokenKind accessors.
/// </summary>
public sealed class CopyableDoctrineTests
{
    [Fact]
    public void TagEnumAndStructOfTagEnumCopyOutOfPlacesAndKeepTheSourceUsable()
    {
        const string source = """
            module Demo

            enum Kind
            {
                Alpha,
                Beta,
                Gamma,
            }

            struct Item
            {
                Kind Tag;
                u64[0 2 ** 63 - 1] Start;
            }

            struct Table
            {
                dynamic Item Items;

                finite law Kind KindAt(borrow Table self, u64[0 2 ** 63 - 1] index)
                {
                    if (index >= self.Items.Length)
                    {
                        return Kind.Alpha;
                    }

                    return self.Items[index].Tag;
                }
            }

            fn bool Append(mut borrow Table table, Kind kind, u64[0 2 ** 63 - 1] start)
            {
                if (!table.Items.TryReserve(1))
                {
                    return false;
                }

                stack mut Item item = new();
                item.Tag = kind;
                item.Start = start;
                init table.Items[table.Items.Length] = item;
                return true;
            }

            export fn i32[min max] main()
            {
                stack Kind a = Kind.Beta;
                stack Kind b = a;
                if (a != Kind.Beta)
                {
                    return 1;
                }

                stack mut Item item = new();
                item.Tag = Kind.Gamma;
                stack Item copy = item;
                if (item.Tag != Kind.Gamma)
                {
                    return 2;
                }

                stack mut Table table = new();
                if (!Append(table, Kind.Beta, 3))
                {
                    return 3;
                }

                if (table.KindAt(0) != Kind.Beta)
                {
                    return 4;
                }

                if (b != Kind.Beta || copy.Tag != Kind.Gamma)
                {
                    return 5;
                }

                return 0;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void EnumsWithOwningPayloadsStayMoveOnlyAtIndexedPlaces()
    {
        const string source = """
            module Demo

            struct Payload
            {
                dynamic i64[min max] Items;
            }

            enum Owning
            {
                Empty,
                Holding(Payload),
            }

            struct Row
            {
                Owning State;
            }

            struct Rows
            {
                dynamic Row Entries;

                fn bool StateAtIsEmpty(borrow Rows self, u64[0 2 ** 63 - 1] index)
                {
                    if (index >= self.Entries.Length)
                    {
                        return true;
                    }

                    stack Owning copied = self.Entries[index].State;
                    switch (copied)
                    {
                        case Owning.Empty:
                            return true;
                        case Owning.Holding(var payload):
                            return false;
                    }
                }
            }

            export fn i32[min max] main()
            {
                return 0;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions());

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4203"
                && diagnostic.Message.Contains("Cannot move a non-tail dynamic storage slot of type 'Owning'", StringComparison.Ordinal));
    }

    [Fact]
    public void WhereCopyableBoundAcceptsCopyableAndRejectsOwningOrDestructorTypes()
    {
        var accepted = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                enum Kind
                {
                    Alpha,
                    Beta,
                }

                fn bool Duplicate<T>(T value)
                    where Copyable(T)
                {
                    stack T first = value;
                    stack T second = value;
                    return true;
                }

                fn bool Forwarded<T>(T value)
                    where Copyable(T)
                {
                    return Duplicate(value);
                }

                export fn i32[min max] main()
                {
                    stack Kind kind = Kind.Alpha;
                    if (!Forwarded(kind))
                    {
                        return 1;
                    }

                    return 0;
                }
                """),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(
            accepted.Succeeded,
            string.Join(Environment.NewLine, accepted.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var rejected = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                struct Owning
                {
                    dynamic i64[min max] Items;
                }

                fn bool Duplicate<T>(T value)
                    where Copyable(T)
                {
                    return true;
                }

                fn bool MissingForward<T>(T value)
                {
                    return Duplicate(value);
                }

                export fn i32[min max] main()
                {
                    stack mut Owning owning = new();
                    if (!Duplicate(owning))
                    {
                        return 1;
                    }

                    return 0;
                }
                """),
            new CompilerOptions());

        Assert.False(rejected.Succeeded);
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("requires where Copyable(Owning)", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Responsible field chain: Owning.Items", StringComparison.Ordinal));
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("MissingForward", StringComparison.Ordinal)
                && diagnostic.Message.Contains("must declare `where Copyable(T)`", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyableAssertionAttributeChecksStructurallyAtTheDefinition()
    {
        var accepted = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                [Copyable]
                enum Kind
                {
                    Alpha,
                    Beta,
                }

                [Copyable]
                struct Span
                {
                    Kind Tag;
                    u64[0 2 ** 63 - 1] Start;
                }

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(
            accepted.Succeeded,
            string.Join(Environment.NewLine, accepted.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var rejected = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                [Copyable]
                struct Bad
                {
                    dynamic i64[min max] Items;
                }

                [Copyable]
                struct BadDtor
                {
                    i32[min max] Value;

                    drop
                    {
                    }
                }

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rejected.Succeeded);
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3051"
                && diagnostic.Message.Contains("Responsible field chain: Bad.Items", StringComparison.Ordinal));
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3051"
                && diagnostic.Message.Contains("has a destructor", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEvaluationCopiesCopyableValuesConsistently()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                enum Kind
                {
                    Alpha,
                    Beta,
                }

                struct Pair
                {
                    Kind First;
                    Kind Second;
                }

                const Pair Both = new Pair()
                {
                    First = Kind.Alpha,
                    Second = Kind.Beta
                };

                finite law bool ComptimeCopies()
                {
                    // Repeated reads of const-aggregate projections: each read
                    // is a copy and the source stays readable (const globals
                    // keep frozen provenance, so values compare in place).
                    return Both.First == Kind.Alpha
                        && Both.First == Kind.Alpha
                        && Both.Second == Kind.Beta
                        && Both.Second != Kind.Alpha;
                }

                export fn i32[min max] main()
                {
                    if (!ComptimeCopies())
                    {
                        return 1;
                    }

                    return 0;
                }
                """),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StructsWithDestructorsStayMoveOnly()
    {
        const string source = """
            module Demo

            struct WithDtor
            {
                i64[min max] Value;

                drop
                {
                }
            }

            export fn i32[min max] main()
            {
                stack WithDtor a = new();
                stack WithDtor b = a;
                if (a.Value != 0)
                {
                    return 1;
                }

                return 0;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions());

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Message.Contains("moved", StringComparison.Ordinal));
    }

    [Fact]
    public void DestructorFlagRoundTripsThroughPackageImageFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-copyable-destructor-facts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Handle
                    {
                        i32[min max] Descriptor;

                        drop
                        {
                        }
                    }

                    public struct Plain
                    {
                        i32[min max] Value;
                    }

                    public fn Handle MakeHandle()
                    {
                        return new Handle()
                        {
                            Descriptor = 1
                        };
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(
                result.Succeeded,
                string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedModule = new ResolvedPackageModule(
                $"/virtual/{facadeModule.ModuleName}.starkpkg.json",
                $"/virtual/lib{facadeModule.ModuleName}.a",
                new StarkPackageManifest(facadeModule.ModuleName, $"lib{facadeModule.ModuleName}.a", [facadeModule]),
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var handle = facts.NamedTypes["Facade.Handle"];
            var plain = facts.NamedTypes["Facade.Plain"];
            Assert.True(handle.HasDestructor);
            Assert.False(plain.HasDestructor);

            // The loaded symbols feed the same copyability resolver ownership
            // validation uses: the destructor-bearing import stays move-only.
            var copyability = new CopyabilityFacts(facts.NamedTypes);
            Assert.False(copyability.IsCopyable(StarkTypeSymbols.Named("Facade.Handle")));
            Assert.True(copyability.IsCopyable(StarkTypeSymbols.Named("Facade.Plain")));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void FixedArrayOfCopyableElementsIsCopyable()
    {
        // A fixed array of copyable elements is an inline value aggregate, so it
        // is copyable: the struct asserts [Copyable], and the array (and a struct
        // holding it) can be read out of a place twice without a move error.
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                [Copyable]
                struct Board
                {
                    i32[min max][4] Cells;
                }

                finite law i32[min max] Sum(Board board)
                {
                    stack Board copy = board;
                    return board.Cells[0] + copy.Cells[0];
                }

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FixedArrayOfMoveOnlyElementsStaysMoveOnly()
    {
        // A fixed array of a non-copyable element type is itself move-only, so the
        // [Copyable] assertion on a struct holding one fails with the field chain.
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                struct Owner
                {
                    dynamic i32[min max] Items;
                }

                [Copyable]
                struct Bad
                {
                    Owner[2] Owners;
                }

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3051"
                && diagnostic.Message.Contains("Owners", StringComparison.Ordinal));
    }

    [Fact]
    public void SliceAndFunctionPointerFieldsAreCopyable()
    {
        // A slice is a non-owning {ptr, len} view and a function pointer is a bare
        // code address — both own nothing, so they are copyable and a struct that
        // holds one stays copyable. (Both were previously move-only off the bare-
        // local fast path, making the wrapping struct move-only.)
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                [Copyable]
                struct View
                {
                    i32[min max][] Data;
                }

                [Copyable]
                struct Callback
                {
                    fnptr<fn i32[min max](i32[min max])> Fn;
                }

                finite law bool ReuseView(View view)
                {
                    stack View copy = view;
                    return view.Data.Length == copy.Data.Length;
                }

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
