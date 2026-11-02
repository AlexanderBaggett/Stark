using System.Numerics;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelinePathFactsTests
{
    public static IEnumerable<object[]> Paths()
    {
        foreach (var windows in new[] { false, true })
        {
            yield return Row("", 0, 0, 0, 0, 0, 0, false, false);
            yield return Row("alpha/beta.txt", 14, 14, 0, 6, 10, 5, true, false);
            yield return Row("alpha/beta/", 11, 10, 0, 6, 6, 5, false, false);
            yield return Row(".hidden", 7, 7, 0, 0, 0, 0, false, false);
            yield return Row("a.", 2, 2, 0, 0, 1, 0, true, false);
            yield return Row("/", 1, 1, 1, 1, 0, 1, false, !windows);
            yield return Row("///", 3, 1, 1, 1, 0, 1, false, !windows);
            yield return Row("/.x", 3, 3, 1, 1, 1, 1, false, !windows);
            yield return Row("C:", 2, 2, windows ? 2 : 0, windows ? 2 : 0, windows ? 1 : 0, windows ? 2 : 0, false, false);
            yield return Row("C:file.txt", 10, 10, windows ? 2 : 0, windows ? 2 : 0, 6, windows ? 2 : 0, true, false);
            yield return Row("C:/", 3, 2, windows ? 3 : 0, windows ? 2 : 0, windows ? 1 : 0, windows ? 3 : 0, false, windows);
            yield return Row("C:/file.txt", 11, 11, windows ? 3 : 0, 3, 7, windows ? 3 : 2, true, windows);
            yield return Row("C:\\file.txt", 11, 11, windows ? 3 : 0, windows ? 3 : 0, 7, windows ? 3 : 0, true, windows);
            yield return Row("C:.hidden", 9, 9, windows ? 2 : 0, windows ? 2 : 0, 2, windows ? 2 : 0, !windows, false);
            yield return Row("\\\\server\\share", 14, 14, windows ? 1 : 0, windows ? 9 : 0, windows ? 9 : 0, windows ? 8 : 0, false, false);

            object[] Row(string path, int length, int end, int root, int segment, int extension, int directory, bool hasExtension, bool absolute)
                => [windows, path, length, end, root, segment, extension, directory, hasExtension, absolute];
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void LiteralFactsMatchCurrentTargetContract(bool windows, string path, int length, int end,
        int root, int segment, int extension, int directory, bool hasExtension, bool absolute)
    {
        var source = "module System.IO.Path\n" + CurrentPathFactsDeclaration() + "\n" + """
            public finite law PathFacts GetConstFacts(const ascii path);
            fn PathFacts Probe()
            {
                return GetConstFacts(
            """ + JsonSerializer.Serialize(path) + ");\n}";
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(
            TargetInfo: new LlvmTargetInfo(windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu", null),
            StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var probe = Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions, f => f.Name == "Probe");
        var values = probe.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>().Select(i => i.Value).ToArray();
        Assert.DoesNotContain(values, v => v is SsaCallRValue);
        var fields = ReturnedFields(probe);
        Assert.Equal(JsonSerializer.Serialize(path), Assert.IsType<SsaStringConstant>(fields["Path"]).LiteralText);
        foreach (var (name, expected) in new[] { ("Length", length), ("End", end), ("RootLength", root),
                     ("SegmentStart", segment), ("ExtensionStart", extension), ("DirectoryLength", directory) })
            Assert.Equal(new BigInteger(expected), Assert.IsType<SsaIntegerConstant>(fields[name]).Value);
        Assert.Equal(hasExtension, Assert.IsType<SsaBoolConstant>(fields["HasExtension"]).Value);
        Assert.Equal(absolute, Assert.IsType<SsaBoolConstant>(fields["Absolute"]).Value);
    }

    [Theory]
    [InlineData(false, "C:file.txt", "C:file", "", ".txt")]
    [InlineData(true, "C:file.txt", "file", "C:", ".txt")]
    [InlineData(false, "C:/", "C:", "", "")]
    [InlineData(true, "C:/", "", "C:/", "")]
    [InlineData(false, "C:.hidden", "C:", "", ".hidden")]
    [InlineData(true, "C:.hidden", ".hidden", "C:", "")]
    public void LiteralProjectionsUseTheSameRootFacts(bool windows, string path, string basename, string directory, string extension)
    {
        var literal = JsonSerializer.Serialize(path);
        var source = $$"""
            module System.IO.Path
            public finite law ascii BaseNameConst(const ascii path);
            public finite law ascii DirectoryNameConst(const ascii path);
            public finite law ascii ExtensionConst(const ascii path);
            fn ascii Base()
            {
                return BaseNameConst({{literal}});
            }
            fn ascii Directory()
            {
                return DirectoryNameConst({{literal}});
            }
            fn ascii Ext()
            {
                return ExtensionConst({{literal}});
            }
            """;
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(
            TargetInfo: new LlvmTargetInfo(windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu", null),
            StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var ssa = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
        foreach (var (name, text) in new[] { ("Base", basename), ("Directory", directory), ("Ext", extension) })
        {
            var function = Assert.Single(ssa.Functions, f => f.Name == name);
            var block = Assert.Single(function.Blocks, b => b.Terminator.Kind == SsaTerminatorKind.Return);
            Assert.Equal(JsonSerializer.Serialize(text), Assert.IsType<SsaStringConstant>(block.Terminator.Value).LiteralText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualStdlibFactsSurviveThroughLatePassesAndAccessorCalls(bool windows)
    {
        var stdlib = CompilerPipelineTestSupport.StandardLibrarySourceRoot();
        var target = new LlvmTargetInfo(windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
            import System.IO.Path
            module Demo
            export fn PathFacts LiteralRecord()
            {
                return GetFacts("C:/file.txt");
            }
            export fn i64[min max] Probe()
            {
                stack PathFacts facts = GetFacts("C:/file.txt");
                stack PathFacts copied = facts;
                return copied.PathLength() + copied.RootNameLength() + copied.ExtensionLength()
                    + copied.BaseNameLength() + copied.DirectoryNameLength()
                    + (copied.IsAbsolute() ? 100 : 0);
            }
            """), new CompilerOptions(ModuleResolver: new TargetAwareStdLibModuleResolver(
                new FileSystemModuleResolver(stdlib), [stdlib], target), TargetInfo: target));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var body = System.Text.RegularExpressions.Regex.Match(llvm, @"(?ms)^define [^\n]*@Probe\([^\n]*\n\{.*?^}");
        Assert.True(body.Success);
        Assert.DoesNotContain("@System_IO_Path_GetFacts(", body.Value, StringComparison.Ordinal);
        var ssa = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
        var record = Assert.Single(ssa.Functions, f => f.Name == "LiteralRecord");
        var fields = ReturnedFields(record);
        Assert.Equal(new BigInteger(windows ? 3 : 0), Assert.IsType<SsaIntegerConstant>(fields["RootLength"]).Value);
        Assert.Equal(windows, Assert.IsType<SsaBoolConstant>(fields["Absolute"]).Value);
        Assert.Equal(new BigInteger(11), Assert.IsType<SsaIntegerConstant>(fields["Length"]).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceFreePackageFactsRetainTargetFieldsWhileDynamicInputsStayCalls(bool windows)
    {
        var directory = Directory.CreateTempSubdirectory("stark-nine-field-package-");
        try
        {
            var target = new LlvmTargetInfo(windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu", null);
            var library = DefaultCompilerPipeline.Create().Run(new CompilationInput("module System.IO.Path\n"
                + CurrentPathFactsDeclaration() + "\npublic finite law PathFacts GetFacts(ascii path);",
                Path.Combine(directory.FullName, "Path.stark")), new CompilerOptions(TargetInfo: target));
            Assert.True(library.Succeeded, string.Join(Environment.NewLine, library.Diagnostics));
            var manifest = PackageImageBuilder.Create(library, Path.Combine(directory.FullName, "libPath.a"));
            File.WriteAllText(Path.Combine(directory.FullName, "libPath.starkpkg.json"), manifest.ToJson());
            var consumer = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                import System.IO.Path
                module Demo
                fn PathFacts LiteralRecord()
                {
                    return GetFacts("C:/file.txt");
                }
                fn PathFacts DynamicRecord(ascii path)
                {
                    return GetFacts(path);
                }
                """, Path.Combine(directory.FullName, "Demo.stark")), new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName), TargetInfo: target,
                    StopAfterPassId: "arithmetic-fold-ssa"));
            Assert.True(consumer.Succeeded, string.Join(Environment.NewLine, consumer.Diagnostics));
            var ssa = consumer.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var fields = ReturnedFields(Assert.Single(ssa.Functions, f => f.Name == "LiteralRecord"));
            Assert.Equal(new BigInteger(windows ? 3 : 0), Assert.IsType<SsaIntegerConstant>(fields["RootLength"]).Value);
            Assert.Equal(windows, Assert.IsType<SsaBoolConstant>(fields["Absolute"]).Value);
            var dynamic = Assert.Single(ssa.Functions, f => f.Name == "DynamicRecord");
            Assert.Contains(dynamic.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>(),
                i => i.Value is SsaCallRValue call && call.FunctionName.Contains("GetFacts", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, SsaValue> ReturnedFields(SsaFunction function)
    {
        var definitions = function.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>()
            .ToDictionary(i => i.ResultName, i => i.Value);
        var fields = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var value = Assert.Single(function.Blocks, b => b.Terminator.Kind == SsaTerminatorKind.Return).Terminator.Value!;
        while (value is SsaValueReference reference)
        {
            switch (definitions[reference.Name])
            {
                case SsaInsertFieldRValue insert:
                    fields.TryAdd(insert.FieldName, insert.Value);
                    value = insert.Target;
                    break;
                case SsaUseRValue use:
                    value = use.Value;
                    break;
                default:
                    Assert.Fail("Returned facts must retain their constant fields.");
                    break;
            }
        }
        Assert.IsType<SsaZeroInitializerValue>(value);
        // Cleanup may omit stores of the seed's zero-valued fields.
        foreach (var name in new[] { "Length", "End", "RootLength", "SegmentStart", "ExtensionStart", "DirectoryLength" })
            fields.TryAdd(name, new SsaIntegerConstant(0, StarkTypeSymbols.Integer(64)));
        foreach (var name in new[] { "HasExtension", "Absolute" })
            fields.TryAdd(name, new SsaBoolConstant(false));
        return fields;
    }

    private static string CurrentPathFactsDeclaration()
    {
        var path = Path.Combine(CompilerPipelineTestSupport.StandardLibrarySourceRoot(), "System", "IO", "Path.stark");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("public struct PathFacts", StringComparison.Ordinal);
        var end = source.IndexOf("internal finite law PathFacts EmptyPathFacts", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
