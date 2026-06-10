using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemTextInterningStandardLibraryTests
{
    private const string TextInterningProgram = """
        import System.Collections
        import System.Memory
        import System.Text
        import System.Text.Interning
        module Demo

        fn bool Ok(System.Memory.MemoryStatus status)
        {
            switch (status)
            {
                case System.Memory.MemoryStatus.Ok:
                    return true;
                case System.Memory.MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool ReadTypeId(System.Text.Interning.TextInternerResult<System.Text.Interning.TypeId> result)
        {
            switch (result)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.TypeId>.Err(var error):
                    return false;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.TypeId>.Ok(var value):
                    return value.Index() == 0;
            }
        }

        fn bool ReadModuleId(System.Text.Interning.TextInternerResult<System.Text.Interning.ModuleId> result)
        {
            switch (result)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.ModuleId>.Err(var error):
                    return false;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.ModuleId>.Ok(var value):
                    return value.Index() == 0;
            }
        }

        fn bool ReadPackageId(System.Text.Interning.TextInternerResult<System.Text.Interning.PackageId> result)
        {
            switch (result)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.PackageId>.Err(var error):
                    return false;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.PackageId>.Ok(var value):
                    return value.Index() == 0;
            }
        }

        fn bool ReadFieldId(System.Text.Interning.TextInternerResult<System.Text.Interning.FieldId> result)
        {
            switch (result)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.FieldId>.Err(var error):
                    return false;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.FieldId>.Ok(var value):
                    return value.Index() == 0;
            }
        }

        fn bool ReadMemberId(System.Text.Interning.TextInternerResult<System.Text.Interning.MemberId> result)
        {
            switch (result)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.MemberId>.Err(var error):
                    return false;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.MemberId>.Ok(var value):
                    return value.Index() == 0;
            }
        }

        fn bool ReadArtifactKeyId(System.Text.Interning.TextInternerResult<System.Text.Interning.ArtifactKeyId> result)
        {
            switch (result)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.ArtifactKeyId>.Err(var error):
                    return false;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.ArtifactKeyId>.Ok(var value):
                    return value.Index() == 0;
            }
        }

        fn i32[min max] UseOwnedAsciiRangeCopy()
        {
            stack mut System.Text.OwnedAscii source = new();
            stack System.Memory.MemoryStatus sourceStatus = source.AppendAscii("zzalphazz");
            if (!Ok(sourceStatus))
            {
                return 201;
            }

            stack i8[min max][] sourceBytes = source.AsSlice();
            if (sourceBytes[2] != 97)
            {
                return 210;
            }

            if (sourceBytes[3] != 108)
            {
                return 211;
            }

            stack mut System.Text.OwnedAscii copy = new();
            stack System.Memory.MemoryStatus copyStatus = System.Text.AppendOwnedAsciiRange(copy, source, 2, 5);
            if (!Ok(copyStatus))
            {
                return 202;
            }

            if (copy.Length() != 5)
            {
                return 203;
            }

            stack i8[min max][] copiedBytes = copy.AsSlice();
            if (copiedBytes[0] != 97)
            {
                return 204;
            }

            if (copiedBytes[1] != 108)
            {
                return 205;
            }

            if (copiedBytes[2] != 112)
            {
                return 206;
            }

            if (copiedBytes[3] != 104)
            {
                return 207;
            }

            if (copiedBytes[4] != 97)
            {
                return 208;
            }

            if (!System.Text.Equals(copy.View(), "alpha"))
            {
                return 209;
            }

            return 0;
        }

        fn i32[min max] UseSymbolInterner()
        {
            stack mut System.Text.Interning.SymbolInterner symbols = new();
            if (!symbols.IsEmpty())
            {
                return 10;
            }

            stack System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId> firstResult =
                symbols.Intern("alpha");
            switch (firstResult)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Err(var firstError):
                    return 11;
                case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Ok(var first):
                    stack System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId> secondResult =
                        symbols.Intern("alpha");
                    switch (secondResult)
                    {
                        case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Err(var secondError):
                            return 12;
                        case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Ok(var second):
                            stack System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId> betaResult =
                                symbols.Intern("beta");
                            switch (betaResult)
                            {
                                case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Err(var betaError):
                                    return 13;
                                case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Ok(var beta):
                                    stack mut System.Text.Interning.SymbolId found = first;
                                    stack mut u32[0 max] payload = 0;
                                    stack mut System.Collections.Dictionary<System.Text.Interning.SymbolId, u32[0 max]> map = new();

                                    if (first.Index() != 0)
                                    {
                                        return 14;
                                    }

                                    if (second.Index() != 0)
                                    {
                                        return 15;
                                    }

                                    if (beta.Index() != 1)
                                    {
                                        return 16;
                                    }

                                    if (!System.Text.Interning.SymbolId.Equals(first, second))
                                    {
                                        return 17;
                                    }

                                    if (System.Text.Interning.SymbolId.Equals(first, beta))
                                    {
                                        return 18;
                                    }

                                    if (System.Text.Interning.SymbolId.Compare(first, beta) != System.Collections.Ordering.Less)
                                    {
                                        return 19;
                                    }

                                    if (symbols.Count() != 2)
                                    {
                                        return 20;
                                    }

                                    if (!symbols.Contains("alpha") || !symbols.Contains("beta") || symbols.Contains("missing"))
                                    {
                                        return 21;
                                    }

                                    if (!symbols.TryGet("alpha", found) || !System.Text.Interning.SymbolId.Equals(found, first))
                                    {
                                        return 22;
                                    }

                                    stack System.Text.Interning.TextInternerResult<System.Text.OwnedAscii> copiedNameResult =
                                        symbols.CopyName(first);
                                    switch (copiedNameResult)
                                    {
                                        case System.Text.Interning.TextInternerResult<System.Text.OwnedAscii>.Err(var copyError):
                                            return 230;
                                        case System.Text.Interning.TextInternerResult<System.Text.OwnedAscii>.Ok(var copiedNameValue):
                                            stack mut System.Text.OwnedAscii copiedName = copiedNameValue;
                                            if (copiedName.Length() != 5)
                                            {
                                                return 231;
                                            }

                                            stack i8[min max][] copiedBytes = copiedName.AsSlice();
                                            if (copiedBytes[0] != 97)
                                            {
                                                return 233;
                                            }

                                            if (copiedBytes[1] != 108)
                                            {
                                                return 234;
                                            }

                                            if (copiedBytes[2] != 112)
                                            {
                                                return 235;
                                            }

                                            if (copiedBytes[3] != 104)
                                            {
                                                return 236;
                                            }

                                            if (copiedBytes[4] != 97)
                                            {
                                                return 237;
                                            }

                                            if (!System.Text.Equals(copiedName.View(), "alpha"))
                                            {
                                                return 232;
                                            }
                                    }

                                    if (!Ok(map.Set(first, 77)))
                                    {
                                        return 24;
                                    }

                                    if (!map.TryGet(found, payload))
                                    {
                                        return 25;
                                    }

                                    if (payload != 77)
                                    {
                                        return 26;
                                    }

                                    return 0;
                            }
                    }
            }
        }

        fn bool UseOtherInternerDomains()
        {
            stack mut System.Text.Interning.TypeInterner types = new();
            stack mut System.Text.Interning.ModuleInterner modules = new();
            stack mut System.Text.Interning.PackageInterner packages = new();
            stack mut System.Text.Interning.FieldInterner fields = new();
            stack mut System.Text.Interning.MemberInterner members = new();
            stack mut System.Text.Interning.ArtifactKeyInterner artifacts = new();

            return ReadTypeId(types.Intern("Widget"))
                && ReadModuleId(modules.Intern("Core.Text"))
                && ReadPackageId(packages.Intern("System"))
                && ReadFieldId(fields.Intern("Length"))
                && ReadMemberId(members.Intern("Format"))
                && ReadArtifactKeyId(artifacts.Intern("libSystem.starkpkg"));
        }

        export fn i32[min max] main()
        {
            stack i32[min max] rangeCopyStatus = UseOwnedAsciiRangeCopy();
            if (rangeCopyStatus != 0)
            {
                return rangeCopyStatus;
            }

            stack i32[min max] symbolStatus = UseSymbolInterner();
            if (symbolStatus != 0)
            {
                return symbolStatus;
            }

            if (!UseOtherInternerDomains())
            {
                return 100;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceTextInterningSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibTextInterning.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(TextInterningProgram, appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceTextInternerIdsAreNominallyDistinct()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibTextInternerIdMismatch.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Text.Interning
                module Demo

                fn void AcceptSymbol(SymbolId id)
                {
                    return;
                }

                fn void Use()
                {
                    stack mut TypeInterner types = new();
                    stack TextInternerResult<TypeId> result = types.Intern("Widget");
                    switch (result)
                    {
                        case TextInternerResult<TypeId>.Err(var error):
                            return;
                        case TextInternerResult<TypeId>.Ok(var typeId):
                            AcceptSymbol(typeId);
                            return;
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains("TypeId", StringComparison.Ordinal)
                && diagnostic.Message.Contains("SymbolId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceStdLibTextInterningExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-text-interning-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, TextInterningProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = tempDirectory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            await process!.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
            Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync());
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for stdlib integration tests.");
    }
}
