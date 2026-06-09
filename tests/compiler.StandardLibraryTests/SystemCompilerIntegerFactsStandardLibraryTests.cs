using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCompilerIntegerFactsStandardLibraryTests
{
    private const string IntegerFactsProgram = """
        import System.Compiler.IntegerFacts
        module Demo

        fn u1024[0 max] ReadU1024(
            System.Compiler.IntegerFacts.IntegerFactResult<u1024[0 max]> result,
            u1024[0 max] fallback)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<u1024[0 max]>.Ok(var value):
                    return value;
                case System.Compiler.IntegerFacts.IntegerFactResult<u1024[0 max]>.Err(var error):
                    return fallback;
            }
        }

        fn i1024[min max] ReadI1024(
            System.Compiler.IntegerFacts.IntegerFactResult<i1024[min max]> result,
            i1024[min max] fallback)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<i1024[min max]>.Ok(var value):
                    return value;
                case System.Compiler.IntegerFacts.IntegerFactResult<i1024[min max]>.Err(var error):
                    return fallback;
            }
        }

        fn bool IsU1024Error(
            System.Compiler.IntegerFacts.IntegerFactResult<u1024[0 max]> result,
            System.Compiler.IntegerFacts.IntegerFactError expected)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<u1024[0 max]>.Ok(var value):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<u1024[0 max]>.Err(var error):
                    return error == expected;
            }
        }

        fn bool IsI1024Error(
            System.Compiler.IntegerFacts.IntegerFactResult<i1024[min max]> result,
            System.Compiler.IntegerFacts.IntegerFactError expected)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<i1024[min max]>.Ok(var value):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<i1024[min max]>.Err(var error):
                    return error == expected;
            }
        }

        fn bool CheckStorage(
            System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerStorageClass> result,
            bool isUnsigned,
            u16[1 1024] bitWidth)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerStorageClass>.Err(var error):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerStorageClass>.Ok(var storage):
                    return storage.IsUnsigned == isUnsigned && storage.BitWidth == bitWidth;
            }
        }

        fn bool CheckStorageViolation(
            System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerStorageSuggestion> result,
            bool isUnsigned,
            u16[1 1024] bitWidth,
            System.Compiler.IntegerFacts.IntegerRangeStorageViolationKind violation)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerStorageSuggestion>.Err(var error):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerStorageSuggestion>.Ok(var suggestion):
                    return suggestion.Storage.IsUnsigned == isUnsigned
                        && suggestion.Storage.BitWidth == bitWidth
                        && suggestion.Violation == violation;
            }
        }

        fn bool CheckTag(
            System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerTagType> result,
            u16[1 1024] bitWidth,
            u1024[0 max] maxTag)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerTagType>.Err(var error):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.IntegerTagType>.Ok(var tag):
                    return tag.BitWidth == bitWidth && tag.MaxTag == maxTag;
            }
        }

        fn bool CheckKnownBits(
            System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits> result,
            u16[1 1024] bitWidth,
            u1024[0 max] knownZero,
            u1024[0 max] knownOne)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits>.Err(var error):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits>.Ok(var value):
                    return value.BitWidth == bitWidth
                        && value.KnownZero == knownZero
                        && value.KnownOne == knownOne;
            }
        }

        fn bool IsKnownBitsError(
            System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits> result,
            System.Compiler.IntegerFacts.IntegerFactError expected)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits>.Ok(var value):
                    return false;
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits>.Err(var error):
                    return error == expected;
            }
        }

        fn System.Compiler.IntegerFacts.KnownBits ReadKnownBits(
            System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits> result)
        {
            switch (result)
            {
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits>.Ok(var value):
                    return value;
                case System.Compiler.IntegerFacts.IntegerFactResult<System.Compiler.IntegerFacts.KnownBits>.Err(var error):
                    return new System.Compiler.IntegerFacts.KnownBits()
                    {
                        BitWidth = 1,
                        KnownZero = 1,
                        KnownOne = 0
                    };
            }
        }

        export fn i32[min max] main()
        {
            if (ReadU1024(System.Compiler.IntegerFacts.BitMask(12), 0) != 4095)
            {
                return 1;
            }

            if (ReadU1024(System.Compiler.IntegerFacts.BitMask(1024), 0) != (u1024[0 max])(2 ** 1024 - 1))
            {
                return 2;
            }

            if (ReadI1024(System.Compiler.IntegerFacts.SignedMinForBitWidth(8), 0) != -128)
            {
                return 3;
            }

            if (ReadI1024(System.Compiler.IntegerFacts.SignedMaxForBitWidth(8), 0) != 127)
            {
                return 4;
            }

            if (!System.Compiler.IntegerFacts.FitsUnsigned(255, 8)
                || System.Compiler.IntegerFacts.FitsUnsigned(256, 8))
            {
                return 5;
            }

            if (!System.Compiler.IntegerFacts.FitsSigned(-128, 8)
                || !System.Compiler.IntegerFacts.FitsSigned(127, 8)
                || System.Compiler.IntegerFacts.FitsSigned(128, 8))
            {
                return 6;
            }

            if (!IsU1024Error(
                    System.Compiler.IntegerFacts.TrySignedToUnsigned(-1),
                    System.Compiler.IntegerFacts.IntegerFactError.NegativeUnsigned))
            {
                return 7;
            }

            if (!IsI1024Error(
                    System.Compiler.IntegerFacts.TryUnsignedToSigned((u1024[0 max])(2 ** 1023)),
                    System.Compiler.IntegerFacts.IntegerFactError.Overflow))
            {
                return 8;
            }

            if (!CheckStorage(
                    System.Compiler.IntegerFacts.SmallestSignedStorageForRange(0, 255),
                    true,
                    8))
            {
                return 9;
            }

            if (!CheckStorage(
                    System.Compiler.IntegerFacts.SmallestSignedStorageForRange(-129, 128),
                    false,
                    16))
            {
                return 10;
            }

            if (!CheckStorageViolation(
                    System.Compiler.IntegerFacts.GetStorageViolation(32, false, 0, 7),
                    true,
                    8,
                    System.Compiler.IntegerFacts.IntegerRangeStorageViolationKind.NonNegativeSigned))
            {
                return 11;
            }

            if (!CheckTag(System.Compiler.IntegerFacts.TagTypeForVariantCount(0), 8, 0)
                || !CheckTag(System.Compiler.IntegerFacts.TagTypeForVariantCount(257), 16, 256))
            {
                return 12;
            }

            if (ReadU1024(
                    System.Compiler.IntegerFacts.CheckedAddUnsigned(
                        (u1024[0 max])(2 ** 1024 - 2),
                        1),
                    0) != (u1024[0 max])(2 ** 1024 - 1))
            {
                return 13;
            }

            if (!IsU1024Error(
                    System.Compiler.IntegerFacts.CheckedAddUnsigned(
                        (u1024[0 max])(2 ** 1024 - 1),
                        1),
                    System.Compiler.IntegerFacts.IntegerFactError.Overflow))
            {
                return 14;
            }

            if (ReadI1024(
                    System.Compiler.IntegerFacts.CheckedSubSigned(
                        -1,
                        (i1024[min max])(-(2 ** 1023))),
                    0) != (i1024[min max])(2 ** 1023 - 1))
            {
                return 15;
            }

            if (!IsI1024Error(
                    System.Compiler.IntegerFacts.CheckedAddSigned(
                        (i1024[min max])(2 ** 1023 - 1),
                        1),
                    System.Compiler.IntegerFacts.IntegerFactError.Overflow))
            {
                return 16;
            }

            if (ReadU1024(System.Compiler.IntegerFacts.CheckedShiftLeftUnsigned(1, 63, 64), 0)
                != (u1024[0 max])(2 ** 63))
            {
                return 17;
            }

            if (!IsU1024Error(
                    System.Compiler.IntegerFacts.CheckedShiftLeftUnsigned(1, 64, 64),
                    System.Compiler.IntegerFacts.IntegerFactError.Overflow))
            {
                return 18;
            }

            if (ReadU1024(System.Compiler.IntegerFacts.ShiftLeftMaskedUnsigned(1, 64, 64), 99) != 0)
            {
                return 19;
            }

            if (!CheckKnownBits(System.Compiler.IntegerFacts.KnownBitsFromUnsignedConstant(10, 4), 4, 5, 10))
            {
                return 20;
            }

            stack System.Compiler.IntegerFacts.KnownBits left =
                ReadKnownBits(System.Compiler.IntegerFacts.KnownBitsFromUnsignedConstant(10, 4));
            stack System.Compiler.IntegerFacts.KnownBits right =
                ReadKnownBits(System.Compiler.IntegerFacts.KnownBitsFromUnsignedConstant(12, 4));

            if (!CheckKnownBits(System.Compiler.IntegerFacts.KnownBitsAnd(left, right), 4, 7, 8))
            {
                return 21;
            }

            if (!CheckKnownBits(System.Compiler.IntegerFacts.KnownBitsOr(left, right), 4, 1, 14))
            {
                return 22;
            }

            if (!CheckKnownBits(System.Compiler.IntegerFacts.KnownBitsXor(left, right), 4, 9, 6))
            {
                return 23;
            }

            if (!CheckKnownBits(System.Compiler.IntegerFacts.KnownBitsShiftLeft(left, 1), 4, 11, 4))
            {
                return 24;
            }

            if (!CheckKnownBits(System.Compiler.IntegerFacts.KnownBitsLogicalShiftRight(left, 1), 4, 10, 5))
            {
                return 25;
            }

            if (!IsKnownBitsError(
                    System.Compiler.IntegerFacts.CreateKnownBits(4, 1, 1),
                    System.Compiler.IntegerFacts.IntegerFactError.ConflictingKnownBits))
            {
                return 26;
            }

            if (ReadU1024(System.Compiler.IntegerFacts.TwosComplementNormalize(-1, 8), 0) != 255)
            {
                return 27;
            }

            if (ReadI1024(System.Compiler.IntegerFacts.SignExtendUnsigned(255, 8), 0) != -1)
            {
                return 28;
            }

            return 0;
        }
        """;

    [Fact]
    public async Task SourceStdLibCompilerIntegerFactsExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-integer-facts-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, IntegerFactsProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
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
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
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
    public async Task PackagedStdLibCompilerIntegerFactsWorksWithoutSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-integer-facts-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg.json");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-pkg", "--package-library-file", libraryFileName, "-o", manifestPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted package image:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(manifestPath));

            await File.WriteAllTextAsync(appPath, IntegerFactsProgram);
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(IntegerFactsProgram, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(packageDirectory),
                    StopAfterPassId: "type-check"));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
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

    private static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }
}
