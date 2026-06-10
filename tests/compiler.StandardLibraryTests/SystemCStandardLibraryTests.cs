using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCStandardLibraryTests : StandardLibraryTestSuite
{
    private const string CStringProgram = """
        import System.C
        import System.Text
        module App

        static mut bool ForeignReleased = false;

        unsafe ffi(c) fn void ReleaseForeign(rawmutptr<System.C.c_char> data)
        {
            ForeignReleased = data != null;
            return;
        }

        fn bool OwnedRoundTripWorks()
        {
            stack System.C.CStringResult<System.C.OwnedCStr> created = System.C.FromAscii("hello");
            switch (created)
            {
                case System.C.CStringResult<System.C.OwnedCStr>.Err(var error):
                    return false;
                case System.C.CStringResult<System.C.OwnedCStr>.Ok(var value):
                    stack mut System.C.OwnedCStr cText = value;
                    if (cText.Length() != 5 || cText.IsEmpty())
                    {
                        return false;
                    }

                    unsafe
                    {
                        if (cText.Data() == null)
                        {
                            return false;
                        }

                        stack System.C.CStr view = cText.View();
                        if (view.Length() != 5 || view.IsEmpty())
                        {
                            return false;
                        }

                        stack System.C.CStringResult<System.Text.OwnedAscii> asciiResult =
                            System.C.ToAscii(view);
                        switch (asciiResult)
                        {
                            case System.C.CStringResult<System.Text.OwnedAscii>.Err(var asciiError):
                                return false;
                            case System.C.CStringResult<System.Text.OwnedAscii>.Ok(var asciiValue):
                                stack mut System.Text.OwnedAscii asciiText = asciiValue;
                                stack i8[min max][] bytes = asciiText.AsSlice();
                                if (asciiText.Length() != 5 || bytes[0] != 104 || bytes[4] != 111)
                                {
                                    return false;
                                }
                        }

                        stack System.C.CStringResult<System.Text.OwnedUnicode> unicodeResult =
                            System.C.ToUnicodeUtf8(view);
                        switch (unicodeResult)
                        {
                            case System.C.CStringResult<System.Text.OwnedUnicode>.Err(var unicodeError):
                                return false;
                            case System.C.CStringResult<System.Text.OwnedUnicode>.Ok(var unicodeValue):
                                stack mut System.Text.OwnedUnicode unicodeText = unicodeValue;
                                stack i32[min max][] codePoints = unicodeText.AsSlice();
                                if (unicodeText.Length() != 5 || codePoints[0] != 104 || codePoints[4] != 111)
                                {
                                    return false;
                                }
                        }
                    }

                    return true;
            }
        }

        fn bool MutableBufferRoundTripWorks()
        {
            stack System.C.CStringResult<System.C.CCharBuffer> created = System.C.NewCCharBuffer(4);
            switch (created)
            {
                case System.C.CStringResult<System.C.CCharBuffer>.Err(var error):
                    return false;
                case System.C.CStringResult<System.C.CCharBuffer>.Ok(var value):
                    stack mut System.C.CCharBuffer buffer = value;
                    if (buffer.Capacity() != 4 || buffer.IsEmpty())
                    {
                        return false;
                    }

                    unsafe
                    {
                        stack rawmutptr<System.C.c_char> data = buffer.Data();
                        if (data == null)
                        {
                            return false;
                        }

                        *(&data[0]) = 111;
                        *(&data[1]) = 107;
                        *(&data[2]) = 0;
                        *(&data[3]) = 33;

                        stack System.C.CStringResult<System.C.CStr> viewResult = buffer.TryAsCStr();
                        switch (viewResult)
                        {
                            case System.C.CStringResult<System.C.CStr>.Err(var viewError):
                                return false;
                            case System.C.CStringResult<System.C.CStr>.Ok(var view):
                                if (view.Length() != 2)
                                {
                                    return false;
                                }

                                stack System.C.CStringResult<System.Text.OwnedAscii> asciiResult =
                                    System.C.ToAscii(view);
                                switch (asciiResult)
                                {
                                    case System.C.CStringResult<System.Text.OwnedAscii>.Err(var asciiError):
                                        return false;
                                    case System.C.CStringResult<System.Text.OwnedAscii>.Ok(var asciiValue):
                                        stack mut System.Text.OwnedAscii asciiText = asciiValue;
                                        stack i8[min max][] bytes = asciiText.AsSlice();
                                        return asciiText.Length() == 2 && bytes[0] == 111 && bytes[1] == 107;
                                }
                        }
                    }
            }
        }

        fn bool ForeignOwnedCopyAndDisposeWorks()
        {
            stack mut System.C.c_char[6] storage =
            {
                104, 101, 108, 108, 111, 0
            };

            unsafe
            {
                ForeignReleased = false;

                stack System.C.CStringResult<System.C.ForeignOwnedCStr> created =
                    System.C.TryFromForeignOwnedRaw(&storage[0]);
                switch (created)
                {
                    case System.C.CStringResult<System.C.ForeignOwnedCStr>.Err(var error):
                        return false;
                    case System.C.CStringResult<System.C.ForeignOwnedCStr>.Ok(var value):
                        stack mut System.C.ForeignOwnedCStr foreign = value;
                        if (foreign.IsNull() || foreign.Data() == null)
                        {
                            return false;
                        }

                        stack System.C.CStringResult<System.Text.OwnedAscii> copied =
                            System.C.CopyForeignOwnedAsciiAndDispose(foreign, ReleaseForeign, 6);
                        if (!foreign.IsNull() || !ForeignReleased)
                        {
                            return false;
                        }

                        switch (copied)
                        {
                            case System.C.CStringResult<System.Text.OwnedAscii>.Err(var copyError):
                                return false;
                            case System.C.CStringResult<System.Text.OwnedAscii>.Ok(var asciiValue):
                                stack mut System.Text.OwnedAscii asciiText = asciiValue;
                                stack i8[min max][] bytes = asciiText.AsSlice();
                                return asciiText.Length() == 5 && bytes[0] == 104 && bytes[4] == 111;
                        }
                }
            }
        }

        fn bool BoundedRawScanReportsMissingTerminator()
        {
            stack mut System.C.c_char[3] storage =
            {
                65, 66, 67
            };

            unsafe
            {
                stack System.C.CStringResult<System.C.CStr> result =
                    System.C.TryFromRawBounded(&storage[0], 3);
                switch (result)
                {
                    case System.C.CStringResult<System.C.CStr>.Ok(var value):
                        return false;
                    case System.C.CStringResult<System.C.CStr>.Err(var error):
                        switch (error)
                        {
                            case System.C.CStringError.MissingTerminator:
                                return true;
                            case System.C.CStringError.NullPointer:
                                return false;
                            case System.C.CStringError.InteriorNull:
                                return false;
                            case System.C.CStringError.InvalidUtf8:
                                return false;
                            case System.C.CStringError.TooLarge:
                                return false;
                            case System.C.CStringError.Memory(var memoryError):
                                return false;
                            case System.C.CStringError.Text(var textError):
                                return false;
                        }
                }
            }
        }

        export unsafe fn i32[min max] main()
        {
            if (!OwnedRoundTripWorks())
            {
                return 1;
            }

            if (!MutableBufferRoundTripWorks())
            {
                return 2;
            }

            if (!BoundedRawScanReportsMissingTerminator())
            {
                return 3;
            }

            if (!ForeignOwnedCopyAndDisposeWorks())
            {
                return 4;
            }

            return 0;
        }
        """;

    [Fact]
    public void SystemCSourceSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "SystemCStringSurface.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(CStringProgram, appPath),
            new CompilerOptions(
                StopAfterPassId: "emit-llvm",
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
    }

    [Fact]
    public async Task SystemCStringHelpersWorkAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-system-c-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, CStringProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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
