using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemTextStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile() => _suite.StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile();

    [Fact]
    public void StdLibSourcePromotedTextLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedTextLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Text
                import System.Memory
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

                fn u64[0 2 ** 63 - 1] GrowAndRead()
                {
                    stack mut System.Text.OwnedAscii text = new();
                    if (!Ok(text.Reserve(8)) || !Ok(text.AppendAscii("abc")) || !Ok(text.AppendI64(42)))
                    {
                        return 0;
                    }

                    stack i8[min max][] view = text.AsSlice();
                    return (u64[0 2 ** 63 - 1])view[4];
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                // Target Linux explicitly: this test asserts the libc-free dynamic-storage
                // lowering, and only the Linux OS allocation shim is syscall-backed (macOS
                // legitimately bottoms out in libc malloc, Windows in HeapAlloc).
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("@__stark_runtime_try_realloc", llvm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedTextAppendsUseTailRegionMemoryHelpers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Text.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var asciiSliceBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedAscii_AppendSlice(",
            "Expected OwnedAscii.AppendSlice to lower as a defined function.");
        var asciiLiteralBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedAscii_AppendAscii(",
            "Expected OwnedAscii.AppendAscii to lower as a defined function.");
        var unicodeSliceBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedUnicode_AppendSlice(",
            "Expected OwnedUnicode.AppendSlice to lower as a defined function.");
        var unicodeLiteralBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedUnicode_AppendUnicode(",
            "Expected OwnedUnicode.AppendUnicode to lower as a defined function.");
        var asciiSliceDisjointBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedAscii_AppendSliceDisjoint(",
            "Expected OwnedAscii.AppendSliceDisjoint to lower as a defined function.");
        var asciiLiteralDisjointBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedAscii_AppendAsciiDisjoint(",
            "Expected OwnedAscii.AppendAsciiDisjoint to lower as a defined function.");
        var asciiConstBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedAscii_AppendConstAscii(",
            "Expected OwnedAscii.AppendConstAscii to lower as a defined function.");
        var asciiConstDisjointBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedAscii_AppendConstAsciiDisjoint(",
            "Expected OwnedAscii.AppendConstAsciiDisjoint to lower as a defined function.");
        var unicodeSliceDisjointBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedUnicode_AppendSliceDisjoint(",
            "Expected OwnedUnicode.AppendSliceDisjoint to lower as a defined function.");
        var unicodeLiteralDisjointBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedUnicode_AppendUnicodeDisjoint(",
            "Expected OwnedUnicode.AppendUnicodeDisjoint to lower as a defined function.");
        var unicodeConstBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedUnicode_AppendConstUnicode(",
            "Expected OwnedUnicode.AppendConstUnicode to lower as a defined function.");
        var unicodeConstDisjointBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @OwnedUnicode_AppendConstUnicodeDisjoint(",
            "Expected OwnedUnicode.AppendConstUnicodeDisjoint to lower as a defined function.");

        Assert.Contains("@OwnedAscii_AppendSliceDisjoint", asciiSliceBody, StringComparison.Ordinal);
        Assert.Contains("@OwnedAscii_AppendAsciiDisjoint", asciiLiteralBody, StringComparison.Ordinal);
        Assert.Contains("@OwnedAscii_AppendConstAsciiDisjoint", asciiConstBody, StringComparison.Ordinal);
        Assert.Contains("@OwnedUnicode_AppendSliceDisjoint", unicodeSliceBody, StringComparison.Ordinal);
        Assert.Contains("@OwnedUnicode_AppendUnicodeDisjoint", unicodeLiteralBody, StringComparison.Ordinal);
        Assert.Contains("@OwnedUnicode_AppendConstUnicodeDisjoint", unicodeConstBody, StringComparison.Ordinal);

        Assert.Contains("@llvm.memcpy.p0.p0.i64", asciiSliceDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", asciiLiteralDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", asciiConstDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", unicodeSliceDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", unicodeLiteralDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", unicodeConstDisjointBody, StringComparison.Ordinal);
        Assert.Contains("icmp eq ptr", asciiLiteralDisjointBody, StringComparison.Ordinal);
        Assert.Contains("icmp eq ptr", asciiConstDisjointBody, StringComparison.Ordinal);
        Assert.Contains("icmp eq ptr", unicodeLiteralDisjointBody, StringComparison.Ordinal);
        Assert.Contains("icmp eq ptr", unicodeConstDisjointBody, StringComparison.Ordinal);
        Assert.Contains("slot_snapshot", asciiLiteralBody, StringComparison.Ordinal);
        Assert.Contains("slot_snapshot", asciiConstBody, StringComparison.Ordinal);
        Assert.Contains("slot_snapshot", unicodeLiteralBody, StringComparison.Ordinal);
        Assert.Contains("slot_snapshot", unicodeConstBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", asciiLiteralBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", asciiConstBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", unicodeLiteralBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", unicodeConstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", asciiSliceDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", asciiLiteralDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", asciiConstDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", unicodeSliceDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", unicodeLiteralDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", unicodeConstDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", asciiSliceDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", asciiLiteralDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", asciiConstDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", unicodeSliceDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", unicodeLiteralDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", unicodeConstDisjointBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedWideUnicodeIntegerFormattingWritesUnicodeDirectly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Text.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var i1024Body = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryFormatI1024Unicode(",
            "Expected TryFormatI1024Unicode to lower as a defined function.");
        var u1024Body = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryFormatU1024Unicode(",
            "Expected TryFormatU1024Unicode to lower as a defined function.");
        var i128Body = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryFormatI128Unicode(",
            "Expected TryFormatI128Unicode to lower as a defined function.");
        var u128Body = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryFormatU128Unicode(",
            "Expected TryFormatU128Unicode to lower as a defined function.");
        var signedAsciiU1024Body = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryFormatSignedU1024Ascii(",
            "Expected TryFormatSignedU1024Ascii to lower as a defined function.");
        var signedUnicodeU1024Body = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryFormatSignedU1024Unicode(",
            "Expected TryFormatSignedU1024Unicode to lower as a defined function.");
        var writeReversedU1024Body = ExtractDefinedFunctionText(
            llvm,
            "@WriteReversedU1024AsciiDigits(",
            "Expected WriteReversedU1024AsciiDigits to lower as a defined function.");

        Assert.Contains("@TryFormatSignedU1024Unicode", i1024Body, StringComparison.Ordinal);
        Assert.Contains("@TryFormatSignedU1024Unicode", u1024Body, StringComparison.Ordinal);
        Assert.Contains("@TryFormatSignedU128Unicode", i128Body, StringComparison.Ordinal);
        Assert.Contains("@TryFormatSignedU128Unicode", u128Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryFormatI1024Ascii", i1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryFormatU1024Ascii", u1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryFormatI128Ascii", i128Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryFormatU128Ascii", u128Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryConvertAsciiToUnicode", i1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryConvertAsciiToUnicode", u1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryConvertAsciiToUnicode", i128Body, StringComparison.Ordinal);
        Assert.DoesNotContain("@TryConvertAsciiToUnicode", u128Body, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca [309 x i8]", u1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca [39 x i8]", u128Body, StringComparison.Ordinal);
        Assert.Contains("@WriteReversedU1024AsciiDigits", signedAsciiU1024Body, StringComparison.Ordinal);
        Assert.Contains("@WriteReversedU1024AsciiDigits", signedUnicodeU1024Body, StringComparison.Ordinal);
        Assert.Contains("@DivideU1024FormatLimbsBy10", writeReversedU1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("udiv i1024", signedAsciiU1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("urem i1024", signedAsciiU1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("udiv i1024", signedUnicodeU1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("urem i1024", signedUnicodeU1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("udiv i1024", writeReversedU1024Body, StringComparison.Ordinal);
        Assert.DoesNotContain("urem i1024", writeReversedU1024Body, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedWideIntegerParsingUsesConstantCutoffs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Text.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        foreach (var functionName in new[] { "ParseI1024Ascii", "ParseI1024Unicode", "ParseU1024Ascii", "ParseU1024Unicode" })
        {
            var body = ExtractDefinedFunctionText(
                llvm,
                $"define fastcc void @{functionName}(",
                $"Expected {functionName} to lower as a defined function.");

            Assert.DoesNotContain("udiv i1024", body, StringComparison.Ordinal);
            Assert.DoesNotContain("urem i1024", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StdLibSourcePromotedTextEncodingHelpersUseBoundedRawPointerRegions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var modulePath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Text.stark");
        var source = File.ReadAllText(modulePath);

        Assert.Contains("rawptr<i8[min max]>[length] data", source, StringComparison.Ordinal);
        Assert.Contains("rawmutptr<i8[min max]>[capacity] destination", source, StringComparison.Ordinal);
        Assert.Contains("rawmutptr<i16[min max]>[capacity] destination", source, StringComparison.Ordinal);
        Assert.Contains("rawptr<i16[min max]>[sourceLength] source", source, StringComparison.Ordinal);
        Assert.Contains("where disjoint(source, destination[0, capacity])", source, StringComparison.Ordinal);
        Assert.Contains("decoded = TryDecodeUtf8CodePoint", source, StringComparison.Ordinal);
        Assert.Contains("finite law retborrow i8[min max][] AsSlice(borrow OwnedAscii self)", source, StringComparison.Ordinal);
        Assert.Contains("finite law retborrow i32[min max][] AsSlice(borrow OwnedUnicode self)", source, StringComparison.Ordinal);
        Assert.Contains("finite law retborrow i16[min max][] AsSlice(borrow OwnedUtf16 self)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagedStdLibTryFormatSurfaceCanBeConsumedWithoutSource()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-text-package-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                import System.Text
                module App

                unsafe fn bool ProbeAscii(rawmutptr<Ascii> destination)
                {
                    return System.Text.TryFormatI1024Ascii(destination, (i1024[min max])(-(2 ** 1023)))
                        && System.Text.TryFormatU1024Ascii(destination, (u1024[0 max])(2 ** 1024 - 1));
                }

                unsafe fn bool ProbeUnicode(rawmutptr<Unicode> destination)
                {
                    return System.Text.TryFormatI192Unicode(destination, (i192[min max])(-(2 ** 191)))
                        && System.Text.TryFormatU768Unicode(destination, (u768[0 max])(2 ** 768 - 1));
                }

                unsafe fn bool ProbeFloat(rawmutptr<Ascii> asciiDestination, rawmutptr<Unicode> unicodeDestination)
                {
                    return System.Text.TryFormatF64Ascii(asciiDestination, -12.5)
                        && System.Text.TryFormatF32Unicode(unicodeDestination, 3.25f);
                }

                fn bool ProbeOwnedText()
                {
                    stack i64[min max] count = 42;
                    stack f64 ratio = 3.5;
                    stack bool ready = true;
                    stack bool stopped = false;
                    stack i32[min max] smallCount = (i32[min max])(-(2 ** 31));
                    stack u32[0 max] smallUnsigned = (u32[0 max])(2 ** 32 - 1);
                    stack i96[min max] wideCount = (i96[min max])(-(2 ** 95));
                    stack u96[0 max] wideUnsigned = (u96[0 max])(2 ** 96 - 1);
                    return OwnedAsciiLength(count.ToAscii(), 2)
                        && OwnedAsciiLength(smallCount.ToAscii(), 11)
                        && OwnedAsciiLength(ready.ToAscii(), 4)
                        && OwnedAsciiLength(System.Text.Encoding.UTF8.ToAscii(), 4)
                        && OwnedAsciiLength(System.Text.TextError.Overflow.ToAscii(), 8)
                        && OwnedUnicodeLength(ratio.ToUnicode(), 8)
                        && OwnedUnicodeLength(smallUnsigned.ToUnicode(), 10)
                        && OwnedAsciiLength(wideCount.ToAscii(), 30)
                        && OwnedUnicodeLength(wideUnsigned.ToUnicode(), 29)
                        && OwnedUnicodeLength(stopped.ToUnicode(), 5)
                        && OwnedUnicodeLength(System.Text.Encoding.UTF16.ToUnicode(), 5)
                        && OwnedUnicodeLength(System.Text.TextError.Overflow.ToUnicode(), 8)
                        && OwnedAsciiLength("Score: " + count.ToAscii(), 9)
                        && OwnedUnicodeLength((unicode)"Value: " + ratio.ToUnicode(), 15);
                }

                unsafe fn bool ProbeFixedConcat()
                {
                    stack mut i8[min max][2] leftStorage =
                    {
                        65, 0
                    };
                    stack mut Ascii left = new Ascii()
                    {
                        Data = &leftStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack mut i8[min max][2] rightStorage =
                    {
                        66, 0
                    };
                    stack mut Ascii right = new Ascii()
                    {
                        Data = &rightStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack Ascii combined[4] = left + right;

                    stack mut i32[min max][2] wideLeftStorage =
                    {
                        65, 0
                    };
                    stack mut Unicode wideLeft = new Unicode()
                    {
                        Data = &wideLeftStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack mut i32[min max][2] wideRightStorage =
                    {
                        66, 0
                    };
                    stack mut Unicode wideRight = new Unicode()
                    {
                        Data = &wideRightStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack Unicode wideCombined[4] = wideLeft + wideRight;
                    return System.Text.AsciiLength(System.Text.AsciiView(combined)) == 2
                        && System.Text.UnicodeLength(System.Text.UnicodeView(wideCombined)) == 2;
                }

                fn bool ProbeFixedInterpolation()
                {
                    stack i64[min max] count = 42;
                    stack Ascii label[32] = $"Score: {count}";
                    stack Unicode wideLabel[32] = $"Score: {count}";
                    return System.Text.AsciiLength(System.Text.AsciiView(label)) == 9
                        && System.Text.UnicodeLength(System.Text.UnicodeView(wideLabel)) == 9;
                }

                fn bool ProbeParse()
                {
                    return ReadParsedBool(System.Text.ParseBoolAscii("true"))
                        && !ReadParsedBool(System.Text.ParseBoolAscii("false"))
                        && IsInvalidBool(System.Text.ParseBoolUnicode((unicode)"True"))
                        && ReadParsedEncoding(System.Text.ParseEncodingAscii("UTF16")) == System.Text.Encoding.UTF16
                        && ReadParsedEncoding(System.Text.ParseEncodingUnicode((unicode)"UTF32")) == System.Text.Encoding.UTF32
                        && IsInvalidEncoding(System.Text.ParseEncodingAscii("utf8"))
                        && ReadParsedTextError(System.Text.ParseTextErrorAscii("Overflow")) == System.Text.TextError.Overflow
                        && ReadParsedTextError(System.Text.ParseTextErrorUnicode((unicode)"InvalidFormat")) == System.Text.TextError.InvalidFormat
                        && IsInvalidTextError(System.Text.ParseTextErrorUnicode((unicode)"Unknown"))
                        && ReadParsedI64(System.Text.ParseI64Ascii("-9223372036854775808"), 0) == (i64[min max])(-(2 ** 63))
                        && ReadParsedU64(System.Text.ParseU64Unicode((unicode)"18446744073709551615"), 0) == (u64[0 max])(2 ** 64 - 1)
                        && IsOverflowU64(System.Text.ParseU64Ascii("18446744073709551616"))
                        && ReadParsedI24(System.Text.ParseI24Unicode((unicode)"-8388608"), 0) == (i24[min max])(-(2 ** 23))
                        && ReadParsedU48(System.Text.ParseU48Ascii("281474976710655"), 0) == (u48[0 max])(2 ** 48 - 1)
                        && ReadParsedI96(System.Text.ParseI96Ascii("-39614081257132168796771975168"), 0) == (i96[min max])(-(2 ** 95))
                        && ReadParsedU96(System.Text.ParseU96Unicode((unicode)"79228162514264337593543950335"), 0) == (u96[0 max])(2 ** 96 - 1)
                        && IsOverflowI8(System.Text.ParseI8Ascii("-129"))
                        && IsOverflowU32(System.Text.ParseU32Unicode((unicode)"4294967296"))
                        && IsOverflowI96(System.Text.ParseI96Ascii("-39614081257132168796771975169"))
                        && IsOverflowU96(System.Text.ParseU96Ascii("79228162514264337593543950336"));
                }

                unsafe fn bool ProbeEnumFormat()
                {
                    stack mut i8[min max][16] asciiStorage;
                    stack mut Ascii asciiText = new Ascii()
                    {
                        Data = &asciiStorage[0],
                        Length = 0,
                        Capacity = 16
                    };
                    stack mut i32[min max][16] unicodeStorage;
                    stack mut Unicode unicodeText = new Unicode()
                    {
                        Data = &unicodeStorage[0],
                        Length = 0,
                        Capacity = 16
                    };
                    return System.Text.TryFormatEncodingAscii(&asciiText, System.Text.Encoding.UTF16)
                        && asciiText.Length == 5
                        && System.Text.TryFormatTextErrorAscii(&asciiText, System.Text.TextError.InvalidFormat)
                        && asciiText.Length == 13
                        && System.Text.TryFormatEncodingUnicode(&unicodeText, System.Text.Encoding.Binary)
                        && unicodeText.Length == 6
                        && System.Text.TryFormatTextErrorUnicode(&unicodeText, System.Text.TextError.Overflow)
                        && unicodeText.Length == 8;
                }

                fn bool OwnedAsciiLength(System.Memory.MemoryResult<System.Text.OwnedAscii> result, u64[0 2 ** 63 - 1] expected)
                {
                    switch (result)
                    {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == expected;
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                fn bool OwnedUnicodeLength(System.Memory.MemoryResult<System.Text.OwnedUnicode> result, u64[0 2 ** 63 - 1] expected)
                {
                    switch (result)
                    {
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var value):
                            return value.Length() == expected;
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var error):
                            return false;
                    }
                }

                fn bool ReadParsedBool(System.Text.TextResult<bool> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<bool>.Ok(var value):
                            return value;
                        case System.Text.TextResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn bool IsInvalidBool(System.Text.TextResult<bool> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<bool>.Ok(var value):
                            return false;
                        case System.Text.TextResult<bool>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn System.Text.Encoding ReadParsedEncoding(System.Text.TextResult<System.Text.Encoding> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                            return value;
                        case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                            return System.Text.Encoding.Binary;
                    }
                }

                fn System.Text.TextError ReadParsedTextError(System.Text.TextResult<System.Text.TextError> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                            return value;
                        case System.Text.TextResult<System.Text.TextError>.Err(var error):
                            return System.Text.TextError.InvalidFormat;
                    }
                }

                fn bool IsInvalidEncoding(System.Text.TextResult<System.Text.Encoding> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                            return false;
                        case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn bool IsInvalidTextError(System.Text.TextResult<System.Text.TextError> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                            return false;
                        case System.Text.TextResult<System.Text.TextError>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn i64[min max] ReadParsedI64(System.Text.TextResult<i64[min max]> result, i64[min max] fallback)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<i64[min max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i64[min max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u64[0 max] ReadParsedU64(System.Text.TextResult<u64[0 max]> result, u64[0 max] fallback)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<u64[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u64[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i24[min max] ReadParsedI24(System.Text.TextResult<i24[min max]> result, i24[min max] fallback)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<i24[min max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i24[min max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u48[0 max] ReadParsedU48(System.Text.TextResult<u48[0 max]> result, u48[0 max] fallback)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<u48[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u48[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i96[min max] ReadParsedI96(System.Text.TextResult<i96[min max]> result, i96[min max] fallback)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<i96[min max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i96[min max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u96[0 max] ReadParsedU96(System.Text.TextResult<u96[0 max]> result, u96[0 max] fallback)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<u96[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u96[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn bool IsOverflowI8(System.Text.TextResult<i8[min max]> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<i8[min max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i8[min max]>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU32(System.Text.TextResult<u32[0 max]> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<u32[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u32[0 max]>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU64(System.Text.TextResult<u64[0 max]> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<u64[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u64[0 max]>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowI96(System.Text.TextResult<i96[min max]> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<i96[min max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i96[min max]>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU96(System.Text.TextResult<u96[0 max]> result)
                {
                    switch (result)
                    {
                        case System.Text.TextResult<u96[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u96[0 max]>.Err(var error):
                            switch (error)
                            {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }
                """;
            await File.WriteAllTextAsync(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(ModuleResolver: new FileSystemModuleResolver(packageDirectory)));

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

    private static string ExtractDefinedFunctionText(string llvm, string signaturePrefix, string missingMessage)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, missingMessage);

        var bodyStart = llvm.IndexOf('{', functionStart);
        Assert.True(bodyStart > functionStart, $"Expected '{signaturePrefix}' to include a function body.");

        var depth = 0;
        for (var index = bodyStart; index < llvm.Length; index++)
        {
            var current = llvm[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return llvm.Substring(functionStart, index - functionStart + 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected '{signaturePrefix}' body to terminate in emitted LLVM.");
    }
}
