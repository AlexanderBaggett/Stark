using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemJsonStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceJsonHelpersTypeCheckAndLower()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibJson.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Json
                module Demo

                unsafe fn u64[0 2 ** 63 - 1] CountNodes(ascii payload)
                {
                    switch (System.Json.ParseText(payload))
                    {
                        case System.Json.JsonResult<System.Json.JsonDocument>.Err(var error):
                            return 0;
                        case System.Json.JsonResult<System.Json.JsonDocument>.Ok(var document):
                            return document.Count();
                    }
                }

                unsafe fn u64[0 2 ** 63 - 1] WriteProbe()
                {
                    stack mut System.Json.JsonWriter writer = new();
                    stack System.Json.JsonStatus begin = writer.BeginObject();
                    stack System.Json.JsonStatus key = writer.Key("id");
                    stack System.Json.JsonStatus value = writer.TextValue("probe");
                    stack System.Json.JsonStatus end = writer.EndObject();
                    return writer.Length();
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("i64 @CountNodes(", llvm, StringComparison.Ordinal);
        Assert.Contains("i64 @WriteProbe(", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Json_ParseText(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceJsonParseAndWriteRoundTripExecute()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return;
        }

        var sourceRoot = Path.Combine(FindRepositoryRoot(), "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-json-roundtrip-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """"
                import System.Json
                import System.Testing
                module Demo

                export unsafe fn i32[min max] main()
                {
                    stack ascii payload = "{\"protocolVersion\":1,\"responses\":[{\"id\":\"t1\",\"succeeded\":true,\"diagnostics\":[{\"code\":\"STK1001\",\"message\":\"bad \\u00e9 parse\\n\"}],\"count\":-42}]}";
                    switch (System.Json.ParseText(payload))
                    {
                        case System.Json.JsonResult<System.Json.JsonDocument>.Err(var error):
                            return 1;
                        case System.Json.JsonResult<System.Json.JsonDocument>.Ok(var captured):
                            stack mut System.Json.JsonDocument document = captured;
                            stack mut u64[0 2 ** 63 - 1] responses = 0;
                            if (!document.TryFindMember(0, "responses", responses))
                            {
                                return 2;
                            }

                            if (document.KindAt(responses) != System.Json.JsonKind.Array)
                            {
                                return 3;
                            }

                            stack mut u64[0 2 ** 63 - 1] first = 0;
                            if (!document.TryChildAt(responses, 0, first))
                            {
                                return 4;
                            }

                            stack mut u64[0 2 ** 63 - 1] succeeded = 0;
                            if (!document.TryFindMember(first, "succeeded", succeeded) || !document.BoolAt(succeeded))
                            {
                                return 5;
                            }

                            stack mut u64[0 2 ** 63 - 1] count = 0;
                            if (!document.TryFindMember(first, "count", count))
                            {
                                return 6;
                            }

                            switch (document.I64At(count))
                            {
                                case System.Json.JsonResult<i64[min max]>.Err(var countError):
                                    return 7;
                                case System.Json.JsonResult<i64[min max]>.Ok(var countValue):
                                    if (countValue != -42)
                                    {
                                        return 8;
                                    }
                            }

                            stack mut u64[0 2 ** 63 - 1] diagnostics = 0;
                            if (!document.TryFindMember(first, "diagnostics", diagnostics))
                            {
                                return 9;
                            }

                            stack mut u64[0 2 ** 63 - 1] diagnostic = 0;
                            if (!document.TryChildAt(diagnostics, 0, diagnostic))
                            {
                                return 10;
                            }

                            stack mut u64[0 2 ** 63 - 1] message = 0;
                            if (!document.TryFindMember(diagnostic, "message", message))
                            {
                                return 11;
                            }

                            stack ascii decoded = document.TextAt(message);
                            if (!System.Testing.Contains(decoded, "bad "))
                            {
                                return 12;
                            }

                            stack mut System.Json.JsonWriter writer = new();
                            stack System.Json.JsonStatus s1 = writer.BeginObject();
                            stack System.Json.JsonStatus s2 = writer.Key("id");
                            stack System.Json.JsonStatus s3 = writer.TextValue("line1\nline2 \"quoted\"");
                            stack System.Json.JsonStatus s4 = writer.Key("n");
                            stack System.Json.JsonStatus s5 = writer.I64Value(-9876543210);
                            stack System.Json.JsonStatus s6 = writer.EndObject();
                            stack ascii written = writer.View();
                            if (!System.Testing.Contains(written, "\\n"))
                            {
                                return 13;
                            }

                            switch (System.Json.ParseText(written))
                            {
                                case System.Json.JsonResult<System.Json.JsonDocument>.Err(var reparseError):
                                    return 14;
                                case System.Json.JsonResult<System.Json.JsonDocument>.Ok(var reparsedCaptured):
                                    stack mut System.Json.JsonDocument reparsed = reparsedCaptured;
                                    stack mut u64[0 2 ** 63 - 1] n = 0;
                                    if (!reparsed.TryFindMember(0, "n", n))
                                    {
                                        return 15;
                                    }

                                    switch (reparsed.I64At(n))
                                    {
                                        case System.Json.JsonResult<i64[min max]>.Err(var nError):
                                            return 16;
                                        case System.Json.JsonResult<i64[min max]>.Ok(var nValue):
                                            if (nValue != -9876543210)
                                            {
                                                return 17;
                                            }
                                    }
                            }

                            return 0;
                    }
                }
                """");

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
            var appStdout = await process!.StandardOutput.ReadToEndAsync();
            var appStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(
                process.ExitCode == 0,
                $"json round-trip probe exited {process.ExitCode}{Environment.NewLine}{appStdout}{Environment.NewLine}{appStderr}");
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
}
