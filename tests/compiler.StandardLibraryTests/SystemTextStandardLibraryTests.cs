using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemTextStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    private const string ExperimentalTextProgram = """
        import System.Text
        import System.Memory
        module ExperimentalTextParity

        const ascii ConstAsciiSuffix = "Const";
        const ascii ConstUnicodeAsciiSuffix = " AZ";

        fn bool Ok(System.Memory.MemoryStatus status) {
            switch (status) {
                case System.Memory.MemoryStatus.Ok:
                    return true;
                case System.Memory.MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool TooLargeStatus(System.Memory.MemoryStatus status) {
            switch (status) {
                case System.Memory.MemoryStatus.Ok:
                    return false;
                case System.Memory.MemoryStatus.Err(var error):
                    switch (error) {
                        case System.Memory.MemoryError.OutOfMemory:
                            return false;
                        case System.Memory.MemoryError.TooLarge:
                            return true;
                        case System.Memory.MemoryError.InvalidLayout:
                            return false;
                    }
            }
        }

        fn bool TooLargeAsciiResult(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                    return false;
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                    switch (error) {
                        case System.Memory.MemoryError.OutOfMemory:
                            return false;
                        case System.Memory.MemoryError.TooLarge:
                            return true;
                        case System.Memory.MemoryError.InvalidLayout:
                            return false;
                    }
            }
        }

        fn bool ReadParsedBool(System.Text.TextResult<bool> result, bool expected) {
            switch (result) {
                case System.Text.TextResult<bool>.Ok(var value):
                    return value == expected;
                case System.Text.TextResult<bool>.Err(var error):
                    return false;
            }
        }

        fn bool IsInvalidBool(System.Text.TextResult<bool> result) {
            switch (result) {
                case System.Text.TextResult<bool>.Ok(var value):
                    return false;
                case System.Text.TextResult<bool>.Err(var error):
                    switch (error) {
                        case System.Text.TextError.InvalidFormat:
                            return true;
                        case System.Text.TextError.Overflow:
                            return false;
                    }
            }
        }

        fn System.Text.Encoding ReadParsedEncoding(System.Text.TextResult<System.Text.Encoding> result) {
            switch (result) {
                case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                    return value;
                case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                    return System.Text.Encoding.Binary;
            }
        }

        fn bool IsInvalidEncoding(System.Text.TextResult<System.Text.Encoding> result) {
            switch (result) {
                case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                    return false;
                case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                    switch (error) {
                        case System.Text.TextError.InvalidFormat:
                            return true;
                        case System.Text.TextError.Overflow:
                            return false;
                    }
            }
        }

        fn System.Text.TextError ReadParsedTextError(System.Text.TextResult<System.Text.TextError> result) {
            switch (result) {
                case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                    return value;
                case System.Text.TextResult<System.Text.TextError>.Err(var error):
                    return System.Text.TextError.InvalidFormat;
            }
        }

        fn i64[-9223372036854775808 9223372036854775807] ReadParsedI64(
            System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]> result,
            i64[-9223372036854775808 9223372036854775807] fallback) {
            switch (result) {
                case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Ok(var value):
                    return value;
                case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Err(var error):
                    return fallback;
            }
        }

        fn u64[0 max] ReadParsedU64(System.Text.TextResult<u64[0 max]> result, u64[0 max] fallback) {
            switch (result) {
                case System.Text.TextResult<u64[0 max]>.Ok(var value):
                    return value;
                case System.Text.TextResult<u64[0 max]>.Err(var error):
                    return fallback;
            }
        }

        fn i96[min max] ReadParsedI96(System.Text.TextResult<i96[min max]> result, i96[min max] fallback) {
            switch (result) {
                case System.Text.TextResult<i96[min max]>.Ok(var value):
                    return value;
                case System.Text.TextResult<i96[min max]>.Err(var error):
                    return fallback;
            }
        }

        fn u96[0 max] ReadParsedU96(System.Text.TextResult<u96[0 max]> result, u96[0 max] fallback) {
            switch (result) {
                case System.Text.TextResult<u96[0 max]>.Ok(var value):
                    return value;
                case System.Text.TextResult<u96[0 max]>.Err(var error):
                    return fallback;
            }
        }

        fn bool IsOverflowI8(System.Text.TextResult<i8[-128 127]> result) {
            switch (result) {
                case System.Text.TextResult<i8[-128 127]>.Ok(var value):
                    return false;
                case System.Text.TextResult<i8[-128 127]>.Err(var error):
                    switch (error) {
                        case System.Text.TextError.InvalidFormat:
                            return false;
                        case System.Text.TextError.Overflow:
                            return true;
                    }
            }
        }

        fn bool IsOverflowU32(System.Text.TextResult<u32[0 max]> result) {
            switch (result) {
                case System.Text.TextResult<u32[0 max]>.Ok(var value):
                    return false;
                case System.Text.TextResult<u32[0 max]>.Err(var error):
                    switch (error) {
                        case System.Text.TextError.InvalidFormat:
                            return false;
                        case System.Text.TextError.Overflow:
                            return true;
                    }
            }
        }

        fn bool OwnedAsciiLength(System.Memory.MemoryResult<System.Text.OwnedAscii> result, i64[0 max] expected) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                    return value.Length() == expected;
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                    return false;
            }
        }

        fn bool OwnedUnicodeLength(System.Memory.MemoryResult<System.Text.OwnedUnicode> result, i64[0 max] expected) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var value):
                    return value.Length() == expected;
                case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var error):
                    return false;
            }
        }

        fn bool AsciiOwnedMatches(
            mut borrow System.Text.OwnedAscii text,
            i64[0 max] expectedLength,
            i8[-128 127] expectedFirst,
            i8[-128 127] expectedLast) {
            stack i8[-128 127][] view = text.AsSlice();
            if (text.Length() != expectedLength) {
                return false;
            }

            return view[0] == expectedFirst && view[(i64[0 max])(expectedLength - 1)] == expectedLast;
        }

        fn bool UnicodeOwnedMatches(
            mut borrow System.Text.OwnedUnicode text,
            i64[0 max] expectedLength,
            i32[-2147483648 2147483647] expectedFirst,
            i32[-2147483648 2147483647] expectedLast) {
            stack i32[-2147483648 2147483647][] view = text.AsSlice();
            if (text.Length() != expectedLength) {
                return false;
            }

            return view[0] == expectedFirst && view[(i64[0 max])(expectedLength - 1)] == expectedLast;
        }

        fn bool ProbeOwnedAscii() {
            stack mut System.Text.OwnedAscii text = new();
            if (!Ok(text.Reserve(16)) || !Ok(text.AppendAscii("Score: ")) || !Ok(text.AppendI64(-42)) || !Ok(text.AppendByte((i8[-128 127])33))) {
                return false;
            }

            if (text.Length() != 11 || text.Capacity() < 16) {
                return false;
            }

            stack i8[-128 127][] view = text.AsSlice();
            if (view[0] != (i8[-128 127])83 || view[10] != (i8[-128 127])33) {
                return false;
            }

            stack ascii aliasView = text.View();
            if (!Ok(text.AppendAscii(aliasView)) || text.Length() != 22) {
                return false;
            }

            stack i8[-128 127][] aliased = text.AsSlice();
            if (aliased[11] != (i8[-128 127])83 || aliased[21] != (i8[-128 127])33) {
                return false;
            }

            if (!Ok(text.AppendConstAscii(ConstAsciiSuffix)) || text.Length() != 27) {
                return false;
            }

            return text.AsSlice()[22] == (i8[-128 127])67 && text.AsSlice()[26] == (i8[-128 127])116;
        }

        fn bool ProbeOwnedUnicode() {
            stack mut System.Text.OwnedUnicode text = new();
            if (!Ok(text.Reserve(16)) || !Ok(text.AppendUnicode((unicode)"Value: ")) || !Ok(text.AppendI64(100))) {
                return false;
            }

            if (text.Length() != 10 || text.Capacity() < 16) {
                return false;
            }

            stack i32[-2147483648 2147483647][] view = text.AsSlice();
            if (view[0] != 86 || view[9] != 48) {
                return false;
            }

            stack mut System.Text.OwnedUnicode suffix = new();
            if (!Ok(suffix.AppendAscii(" AZ")) || !Ok(text.AppendSlice(suffix.AsSlice(), suffix.Length()))) {
                return false;
            }

            stack i32[-2147483648 2147483647][] appended = text.AsSlice();
            if (text.Length() != 13 || appended[10] != 32 || appended[12] != 90) {
                return false;
            }

            stack unicode aliasView = text.View();
            if (!Ok(text.AppendUnicode(aliasView)) || text.Length() != 26) {
                return false;
            }

            stack i32[-2147483648 2147483647][] aliased = text.AsSlice();
            if (aliased[13] != 86 || aliased[25] != 90) {
                return false;
            }

            if (!Ok(text.AppendConstUnicode((unicode)" ok")) || text.Length() != 29) {
                return false;
            }

            if (!Ok(text.AppendConstAscii(ConstUnicodeAsciiSuffix)) || text.Length() != 32) {
                return false;
            }

            stack i32[-2147483648 2147483647][] constAppended = text.AsSlice();
            return constAppended[26] == 32 && constAppended[31] == 90;
        }

        export unsafe ffi fn i32[min max] main() {
            if (!ProbeOwnedAscii()) {
                return 1;
            }

            if (!ProbeOwnedUnicode()) {
                return 2;
            }

            stack mut System.Text.OwnedAscii asciiValue = new();
            if (!Ok(asciiValue.AppendI64((i32[-2147483648 2147483647])-2147483648))
                || !AsciiOwnedMatches(asciiValue, 11, (i8[-128 127])45, (i8[-128 127])56)) {
                return 3;
            }

            stack mut System.Text.OwnedAscii unsignedAscii = new();
            if (!Ok(unsignedAscii.AppendU64((u64[0 max])18446744073709551615))
                || !AsciiOwnedMatches(unsignedAscii, 20, (i8[-128 127])49, (i8[-128 127])53)) {
                return 4;
            }

            stack mut System.Text.OwnedUnicode unicodeValue = new();
            if (!Ok(unicodeValue.AppendBool(false))
                || !UnicodeOwnedMatches(unicodeValue, 5, 102, 101)) {
                return 5;
            }

            stack mut System.Text.OwnedUnicode converted = new();
            if (!Ok(converted.AppendAscii("AZ"))
                || !UnicodeOwnedMatches(converted, 2, 65, 90)) {
                return 6;
            }

            if (!ReadParsedBool(System.Text.ParseBoolAscii("true"), true)
                || !ReadParsedBool(System.Text.ParseBoolUnicode((unicode)"false"), false)
                || !IsInvalidBool(System.Text.ParseBoolAscii("True"))) {
                return 7;
            }

            if (ReadParsedEncoding(System.Text.ParseEncodingAscii("UTF16")) != System.Text.Encoding.UTF16
                || ReadParsedEncoding(System.Text.ParseEncodingUnicode((unicode)"UTF32")) != System.Text.Encoding.UTF32
                || !IsInvalidEncoding(System.Text.ParseEncodingAscii("utf8"))) {
                return 8;
            }

            if (ReadParsedTextError(System.Text.ParseTextErrorAscii("Overflow")) != System.Text.TextError.Overflow
                || ReadParsedTextError(System.Text.ParseTextErrorUnicode((unicode)"InvalidFormat")) != System.Text.TextError.InvalidFormat) {
                return 9;
            }

            if (ReadParsedI64(System.Text.ParseI64Ascii("-9223372036854775808"), 0) != -(2**63)
                || ReadParsedU64(System.Text.ParseU64Unicode((unicode)"18446744073709551615"), 0) != (u64[0 max])((2**64) - 1)
                || IsOverflowI8(System.Text.ParseI8Ascii("-129")) == false
                || IsOverflowU32(System.Text.ParseU32Unicode((unicode)"4294967296")) == false) {
                return 10;
            }

            if (ReadParsedI96(System.Text.ParseI96Ascii("-39614081257132168796771975168"), 0) != -(2**95)
                || ReadParsedU96(System.Text.ParseU96Unicode((unicode)"79228162514264337593543950335"), 0) != (u96[0 max])((2**96) - 1)) {
                return 11;
            }

            stack mut i8[-128 127][320] asciiStorage;
            stack mut Ascii formattedAscii = new Ascii() {
                Data = &asciiStorage[0],
                Length = 0,
                Capacity = 320
            };
            if (!System.Text.TryFormatEncodingAscii(&formattedAscii, System.Text.Encoding.UTF8)
                || formattedAscii.Length != 4
                || !System.Text.TryFormatI1024Ascii(&formattedAscii, -(2**1023))
                || formattedAscii.Length != 309) {
                return 12;
            }

            stack mut i32[-2147483648 2147483647][320] unicodeStorage;
            stack mut Unicode formattedUnicode = new Unicode() {
                Data = &unicodeStorage[0],
                Length = 0,
                Capacity = 320
            };
            if (!System.Text.TryFormatTextErrorUnicode(&formattedUnicode, System.Text.TextError.Overflow)
                || formattedUnicode.Length != 8
                || !System.Text.TryFormatU1024Unicode(&formattedUnicode, (u1024[0 max])((2**1024) - 1))
                || formattedUnicode.Length != 309
                || *(&formattedUnicode.Data[0]) != 49
                || *(&formattedUnicode.Data[308]) != 53
                || !System.Text.TryFormatI1024Unicode(&formattedUnicode, -(2**1023))
                || formattedUnicode.Length != 309
                || *(&formattedUnicode.Data[0]) != 45
                || *(&formattedUnicode.Data[308]) != 56
                || !System.Text.TryFormatI128Unicode(&formattedUnicode, -(2**127))
                || formattedUnicode.Length != 40
                || *(&formattedUnicode.Data[0]) != 45
                || *(&formattedUnicode.Data[39]) != 56
                || !System.Text.TryFormatU128Unicode(&formattedUnicode, (u128[0 max])((2**128) - 1))
                || formattedUnicode.Length != 39
                || *(&formattedUnicode.Data[0]) != 51
                || *(&formattedUnicode.Data[38]) != 53
                || !System.Text.TryFormatI1024Unicode(&formattedUnicode, (i1024[min max])0)
                || formattedUnicode.Length != 1
                || *(&formattedUnicode.Data[0]) != 48
                || !System.Text.TryFormatU1024Unicode(&formattedUnicode, (u1024[0 max])(10**300))
                || formattedUnicode.Length != 301
                || *(&formattedUnicode.Data[0]) != 49
                || *(&formattedUnicode.Data[300]) != 48) {
                return 13;
            }

            if (!OwnedAsciiLength(System.Text.ToAscii((i32[-2147483648 2147483647])-2147483648), 11)
                || !OwnedAsciiLength(System.Text.ToAscii(System.Text.Encoding.Binary), 6)
                || !OwnedUnicodeLength(System.Text.ToUnicode((u96[0 max])((2**96) - 1)), 29)
                || !OwnedUnicodeLength(System.Text.ToUnicode(System.Text.TextError.Overflow), 8)) {
                return 14;
            }

            stack mut Unicode unicodeBuffer = new Unicode() {
                Data = &unicodeStorage[0],
                Length = 0,
                Capacity = 320
            };
            if (!System.Text.TryConvertAsciiToUnicode(&unicodeBuffer, "caf\u00E9")
                || unicodeBuffer.Length != 4
                || *(&unicodeBuffer.Data[3]) != 233) {
                return 15;
            }

            stack mut System.Text.OwnedAscii tooLarge = new();
            if (!Ok(tooLarge.AppendByte((i8[-128 127])65))
                || !TooLargeStatus(tooLarge.Reserve((i64[0 max])((2**63) - 1)))
                || !TooLargeAsciiResult(System.Text.ConcatAscii("x", (i64[0 max])((2**63) - 1), System.Text.ToAscii((i32[-2147483648 2147483647])1)))) {
                return 16;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile() => _suite.StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile();

    [Fact]
    public void StdLibSourceExperimentalTextLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalTextLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Text
                import System.Memory
                module Demo

                fn bool Ok(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn i64[0 max] GrowAndRead() {
                    stack mut System.Text.OwnedAscii text = new();
                    if (!Ok(text.Reserve(8)) || !Ok(text.AppendAscii("abc")) || !Ok(text.AppendI64(42))) {
                        return 0;
                    }

                    stack i8[-128 127][] view = text.AsSlice();
                    return (i64[0 max])view[4];
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

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
    public void StdLibSourceExperimentalTextAppendsUseTailRegionMemoryHelpers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Text.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EmitLlvmIr: true,
                OptimizationLevel: CompilerOptimizationLevel.O3));

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

        Assert.Contains("@__stark_inline_clone_System_Memory_InitializeBytesDisjoint", asciiSliceDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_InitializeBytesFromPointerDisjoint", asciiLiteralDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_InitializeBytesFromPointerDisjoint", asciiConstDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_InitializeCodePointsDisjoint", unicodeSliceDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_InitializeCodePointsFromPointerDisjoint", unicodeLiteralDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_InitializeCodePointsFromPointerDisjoint", unicodeConstDisjointBody, StringComparison.Ordinal);
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
    public void StdLibSourceExperimentalWideUnicodeIntegerFormattingWritesUnicodeDirectly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Text.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EmitLlvmIr: true,
                OptimizationLevel: CompilerOptimizationLevel.O3));

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
    }

    [Fact]
    public void StdLibSourceExperimentalTextEncodingHelpersUseBoundedRawPointerRegions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var modulePath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Text.stark");
        var source = File.ReadAllText(modulePath);

        Assert.Contains("rawptr<i8[-128 127]>[length] data", source, StringComparison.Ordinal);
        Assert.Contains("rawmutptr<i8[-128 127]>[capacity] destination", source, StringComparison.Ordinal);
        Assert.Contains("rawmutptr<i16[-32768 32767]>[capacity] destination", source, StringComparison.Ordinal);
        Assert.Contains("rawptr<i16[-32768 32767]>[sourceLength] source", source, StringComparison.Ordinal);
        Assert.Contains("where disjoint(source, destination[0, capacity])", source, StringComparison.Ordinal);
        Assert.Contains("decoded = TryDecodeUtf8CodePoint", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagedStdLibTryFormatSurfaceCanBeConsumedWithoutSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-text-package-");
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

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted package image:", stdout.ToString());
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                module App

                fn bool ProbeAscii(rawmutptr<Ascii> destination) {
                    return System.Text.TryFormatI1024Ascii(destination, -(2**1023))
                        && System.Text.TryFormatU1024Ascii(destination, (u1024[0 max])((2**1024) - 1));
                }

                fn bool ProbeUnicode(rawmutptr<Unicode> destination) {
                    return System.Text.TryFormatI192Unicode(destination, -(2**191))
                        && System.Text.TryFormatU768Unicode(destination, (u768[0 max])((2**768) - 1));
                }

                fn bool ProbeFloat(rawmutptr<Ascii> asciiDestination, rawmutptr<Unicode> unicodeDestination) {
                    return System.Text.TryFormatF64Ascii(asciiDestination, -12.5)
                        && System.Text.TryFormatF32Unicode(unicodeDestination, 3.25f);
                }

                fn bool ProbeOwnedText() {
                    stack i64[-9223372036854775808 9223372036854775807] count = 42;
                    stack f64 ratio = 3.5;
                    stack bool ready = true;
                    stack bool stopped = false;
                    stack i32[-2147483648 2147483647] smallCount = -2147483648;
                    stack u32[0 max] smallUnsigned = (u32[0 max])4294967295;
                    stack i96[min max] wideCount = -(2**95);
                    stack u96[0 max] wideUnsigned = (u96[0 max])((2**96) - 1);
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

                fn bool ProbeFixedConcat() {
                    stack mut i8[-128 127][2] leftStorage = { 65, 0 };
                    stack mut Ascii left = new Ascii() {
                        Data = &leftStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack mut i8[-128 127][2] rightStorage = { 66, 0 };
                    stack mut Ascii right = new Ascii() {
                        Data = &rightStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack Ascii combined[4] = left + right;

                    stack mut i32[-2147483648 2147483647][2] wideLeftStorage = { 65, 0 };
                    stack mut Unicode wideLeft = new Unicode() {
                        Data = &wideLeftStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack mut i32[-2147483648 2147483647][2] wideRightStorage = { 66, 0 };
                    stack mut Unicode wideRight = new Unicode() {
                        Data = &wideRightStorage[0],
                        Length = 1,
                        Capacity = 2
                    };
                    stack Unicode wideCombined[4] = wideLeft + wideRight;
                    return System.Text.AsciiLength(System.Text.AsciiView(combined)) == 2
                        && System.Text.UnicodeLength(System.Text.UnicodeView(wideCombined)) == 2;
                }

                fn bool ProbeFixedInterpolation() {
                    stack i64[-9223372036854775808 9223372036854775807] count = 42;
                    stack Ascii label[32] = $"Score: {count}";
                    stack Unicode wideLabel[32] = $"Score: {count}";
                    return System.Text.AsciiLength(System.Text.AsciiView(label)) == 9
                        && System.Text.UnicodeLength(System.Text.UnicodeView(wideLabel)) == 9;
                }

                fn bool ProbeParse() {
                    return ReadParsedBool(System.Text.ParseBoolAscii("true"))
                        && !ReadParsedBool(System.Text.ParseBoolAscii("false"))
                        && IsInvalidBool(System.Text.ParseBoolUnicode((unicode)"True"))
                        && ReadParsedEncoding(System.Text.ParseEncodingAscii("UTF16")) == System.Text.Encoding.UTF16
                        && ReadParsedEncoding(System.Text.ParseEncodingUnicode((unicode)"UTF32")) == System.Text.Encoding.UTF32
                        && IsInvalidEncoding(System.Text.ParseEncodingAscii("utf8"))
                        && ReadParsedTextError(System.Text.ParseTextErrorAscii("Overflow")) == System.Text.TextError.Overflow
                        && ReadParsedTextError(System.Text.ParseTextErrorUnicode((unicode)"InvalidFormat")) == System.Text.TextError.InvalidFormat
                        && IsInvalidTextError(System.Text.ParseTextErrorUnicode((unicode)"Unknown"))
                        && ReadParsedI64(System.Text.ParseI64Ascii("-9223372036854775808"), 0) == -(2**63)
                        && ReadParsedU64(System.Text.ParseU64Unicode((unicode)"18446744073709551615"), 0) == (u64[0 max])((2**64) - 1)
                        && IsOverflowU64(System.Text.ParseU64Ascii("18446744073709551616"))
                        && ReadParsedI24(System.Text.ParseI24Unicode((unicode)"-8388608"), 0) == -(2**23)
                        && ReadParsedU48(System.Text.ParseU48Ascii("281474976710655"), 0) == (u48[0 max])((2**48) - 1)
                        && ReadParsedI96(System.Text.ParseI96Ascii("-39614081257132168796771975168"), 0) == -(2**95)
                        && ReadParsedU96(System.Text.ParseU96Unicode((unicode)"79228162514264337593543950335"), 0) == (u96[0 max])((2**96) - 1)
                        && IsOverflowI8(System.Text.ParseI8Ascii("-129"))
                        && IsOverflowU32(System.Text.ParseU32Unicode((unicode)"4294967296"))
                        && IsOverflowI96(System.Text.ParseI96Ascii("-39614081257132168796771975169"))
                        && IsOverflowU96(System.Text.ParseU96Ascii("79228162514264337593543950336"));
                }

                fn bool ProbeEnumFormat() {
                    stack mut i8[-128 127][16] asciiStorage;
                    stack mut Ascii asciiText = new Ascii() {
                        Data = &asciiStorage[0],
                        Length = 0,
                        Capacity = 16
                    };
                    stack mut i32[-2147483648 2147483647][16] unicodeStorage;
                    stack mut Unicode unicodeText = new Unicode() {
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

                fn bool OwnedAsciiLength(System.Memory.MemoryResult<System.Text.OwnedAscii> result, i64[-9223372036854775808 9223372036854775807] expected) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == expected;
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                fn bool OwnedUnicodeLength(System.Memory.MemoryResult<System.Text.OwnedUnicode> result, i64[-9223372036854775808 9223372036854775807] expected) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var value):
                            return value.Length() == expected;
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var error):
                            return false;
                    }
                }

                fn bool ReadParsedBool(System.Text.TextResult<bool> result) {
                    switch (result) {
                        case System.Text.TextResult<bool>.Ok(var value):
                            return value;
                        case System.Text.TextResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn bool IsInvalidBool(System.Text.TextResult<bool> result) {
                    switch (result) {
                        case System.Text.TextResult<bool>.Ok(var value):
                            return false;
                        case System.Text.TextResult<bool>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn System.Text.Encoding ReadParsedEncoding(System.Text.TextResult<System.Text.Encoding> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                            return value;
                        case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                            return System.Text.Encoding.Binary;
                    }
                }

                fn System.Text.TextError ReadParsedTextError(System.Text.TextResult<System.Text.TextError> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                            return value;
                        case System.Text.TextResult<System.Text.TextError>.Err(var error):
                            return System.Text.TextError.InvalidFormat;
                    }
                }

                fn bool IsInvalidEncoding(System.Text.TextResult<System.Text.Encoding> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                            return false;
                        case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn bool IsInvalidTextError(System.Text.TextResult<System.Text.TextError> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                            return false;
                        case System.Text.TextResult<System.Text.TextError>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn i64[-9223372036854775808 9223372036854775807] ReadParsedI64(System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]> result, i64[-9223372036854775808 9223372036854775807] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Err(var error):
                            return fallback;
                    }
                }

                fn u64[0 max] ReadParsedU64(System.Text.TextResult<u64[0 max]> result, u64[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u64[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u64[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i24[-8388608 8388607] ReadParsedI24(System.Text.TextResult<i24[-8388608 8388607]> result, i24[-8388608 8388607] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i24[-8388608 8388607]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i24[-8388608 8388607]>.Err(var error):
                            return fallback;
                    }
                }

                fn u48[0 max] ReadParsedU48(System.Text.TextResult<u48[0 max]> result, u48[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u48[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u48[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i96[min max] ReadParsedI96(System.Text.TextResult<i96[min max]> result, i96[min max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i96[min max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i96[min max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u96[0 max] ReadParsedU96(System.Text.TextResult<u96[0 max]> result, u96[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u96[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u96[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn bool IsOverflowI8(System.Text.TextResult<i8[-128 127]> result) {
                    switch (result) {
                        case System.Text.TextResult<i8[-128 127]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i8[-128 127]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU32(System.Text.TextResult<u32[0 max]> result) {
                    switch (result) {
                        case System.Text.TextResult<u32[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u32[0 max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU64(System.Text.TextResult<u64[0 max]> result) {
                    switch (result) {
                        case System.Text.TextResult<u64[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u64[0 max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowI96(System.Text.TextResult<i96[min max]> result) {
                    switch (result) {
                        case System.Text.TextResult<i96[min max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i96[min max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU96(System.Text.TextResult<u96[0 max]> result) {
                    switch (result) {
                        case System.Text.TextResult<u96[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u96[0 max]>.Err(var error):
                            switch (error) {
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

    [Fact]
    public async Task SourceStdLibExperimentalTextExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-text-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalTextProgram);

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

    [Fact]
    public async Task SourceImportedStdLibTryFormatExecutableWritesText()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-text-format-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module Demo

                fn bool ReadParsedBool(System.Text.TextResult<bool> result) {
                    switch (result) {
                        case System.Text.TextResult<bool>.Ok(var value):
                            return value;
                        case System.Text.TextResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn bool IsInvalidBool(System.Text.TextResult<bool> result) {
                    switch (result) {
                        case System.Text.TextResult<bool>.Ok(var value):
                            return false;
                        case System.Text.TextResult<bool>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn System.Text.Encoding ReadParsedEncoding(System.Text.TextResult<System.Text.Encoding> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                            return value;
                        case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                            return System.Text.Encoding.Binary;
                    }
                }

                fn System.Text.TextError ReadParsedTextError(System.Text.TextResult<System.Text.TextError> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                            return value;
                        case System.Text.TextResult<System.Text.TextError>.Err(var error):
                            return System.Text.TextError.InvalidFormat;
                    }
                }

                fn bool IsInvalidEncoding(System.Text.TextResult<System.Text.Encoding> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.Encoding>.Ok(var value):
                            return false;
                        case System.Text.TextResult<System.Text.Encoding>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn bool IsInvalidTextError(System.Text.TextResult<System.Text.TextError> result) {
                    switch (result) {
                        case System.Text.TextResult<System.Text.TextError>.Ok(var value):
                            return false;
                        case System.Text.TextResult<System.Text.TextError>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn i64[-9223372036854775808 9223372036854775807] ReadParsedI64(System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]> result, i64[-9223372036854775808 9223372036854775807] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Err(var error):
                            return fallback;
                    }
                }

                fn u64[0 max] ReadParsedU64(System.Text.TextResult<u64[0 max]> result, u64[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u64[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u64[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i8[-128 127] ReadParsedI8(System.Text.TextResult<i8[-128 127]> result, i8[-128 127] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i8[-128 127]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i8[-128 127]>.Err(var error):
                            return fallback;
                    }
                }

                fn i24[-8388608 8388607] ReadParsedI24(System.Text.TextResult<i24[-8388608 8388607]> result, i24[-8388608 8388607] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i24[-8388608 8388607]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i24[-8388608 8388607]>.Err(var error):
                            return fallback;
                    }
                }

                fn i48[-140737488355328 140737488355327] ReadParsedI48(System.Text.TextResult<i48[-140737488355328 140737488355327]> result, i48[-140737488355328 140737488355327] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i48[-140737488355328 140737488355327]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i48[-140737488355328 140737488355327]>.Err(var error):
                            return fallback;
                    }
                }

                fn u8[0 max] ReadParsedU8(System.Text.TextResult<u8[0 max]> result, u8[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u8[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u8[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u32[0 max] ReadParsedU32(System.Text.TextResult<u32[0 max]> result, u32[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u32[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u32[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u48[0 max] ReadParsedU48(System.Text.TextResult<u48[0 max]> result, u48[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u48[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u48[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i96[min max] ReadParsedI96(System.Text.TextResult<i96[min max]> result, i96[min max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i96[min max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i96[min max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u96[0 max] ReadParsedU96(System.Text.TextResult<u96[0 max]> result, u96[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u96[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u96[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn i192[min max] ReadParsedI192(System.Text.TextResult<i192[min max]> result, i192[min max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<i192[min max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<i192[min max]>.Err(var error):
                            return fallback;
                    }
                }

                fn u192[0 max] ReadParsedU192(System.Text.TextResult<u192[0 max]> result, u192[0 max] fallback) {
                    switch (result) {
                        case System.Text.TextResult<u192[0 max]>.Ok(var value):
                            return value;
                        case System.Text.TextResult<u192[0 max]>.Err(var error):
                            return fallback;
                    }
                }

                fn bool IsInvalidI64(System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]> result) {
                    switch (result) {
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return true;
                                case System.Text.TextError.Overflow:
                                    return false;
                            }
                    }
                }

                fn bool IsOverflowI64(System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]> result) {
                    switch (result) {
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i64[-9223372036854775808 9223372036854775807]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowI8(System.Text.TextResult<i8[-128 127]> result) {
                    switch (result) {
                        case System.Text.TextResult<i8[-128 127]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i8[-128 127]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU32(System.Text.TextResult<u32[0 max]> result) {
                    switch (result) {
                        case System.Text.TextResult<u32[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u32[0 max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU64(System.Text.TextResult<u64[0 max]> result) {
                    switch (result) {
                        case System.Text.TextResult<u64[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u64[0 max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowI96(System.Text.TextResult<i96[min max]> result) {
                    switch (result) {
                        case System.Text.TextResult<i96[min max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<i96[min max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                fn bool IsOverflowU96(System.Text.TextResult<u96[0 max]> result) {
                    switch (result) {
                        case System.Text.TextResult<u96[0 max]>.Ok(var value):
                            return false;
                        case System.Text.TextResult<u96[0 max]>.Err(var error):
                            switch (error) {
                                case System.Text.TextError.InvalidFormat:
                                    return false;
                                case System.Text.TextError.Overflow:
                                    return true;
                            }
                    }
                }

                unsafe fn bool OwnedAsciiMatches(System.Memory.MemoryResult<System.Text.OwnedAscii> result, i64[-9223372036854775808 9223372036854775807] expectedLength, i8[-128 127] expectedFirst, i8[-128 127] expectedLast) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            stack i64[-9223372036854775808 9223372036854775807] actualLength = value.Length();
                            stack rawptr<i8[-128 127]> data = System.Text.AsciiData(value.View());
                            return actualLength == expectedLength
                                && data != null
                                && *(&data[0]) == expectedFirst
                                && *(&data[expectedLength - 1]) == expectedLast;
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                unsafe fn bool OwnedUnicodeMatches(System.Memory.MemoryResult<System.Text.OwnedUnicode> result, i64[-9223372036854775808 9223372036854775807] expectedLength, i32[-2147483648 2147483647] expectedFirst, i32[-2147483648 2147483647] expectedLast) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var value):
                            stack i64[-9223372036854775808 9223372036854775807] actualLength = value.Length();
                            stack rawptr<i32[-2147483648 2147483647]> data = System.Text.UnicodeData(value.View());
                            return actualLength == expectedLength
                                && data != null
                                && *(&data[0]) == expectedFirst
                                && *(&data[expectedLength - 1]) == expectedLast;
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var error):
                            return false;
                    }
                }

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i8[-128 127][320] buffer;
                    stack mut i32[-2147483648 2147483647][320] unicodeBuffer;
                    stack mut Ascii formatted = new Ascii() {
                        Data = &buffer[0],
                        Length = 0,
                        Capacity = 320
                    };
                    stack mut Unicode wide = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 320
                    };

                    if (!System.Text.TryFormatI32Ascii(&formatted, -2147483648)) {
                        return 1;
                    }

                    stack rawptr<i8[-128 127]> data = formatted.Data;
                    if (data == null) {
                        return 2;
                    }

                    if (formatted.Length != 11) {
                        return 3;
                    }

                    if (*(&data[0]) != (i8[-128 127])45) {
                        return 4;
                    }

                    if (*(&data[10]) != (i8[-128 127])56) {
                        return 5;
                    }

                    if (!System.Text.TryFormatI64Ascii(&formatted, -9223372036854775808)) {
                        return 6;
                    }

                    stack rawptr<i8[-128 127]> i64Data = formatted.Data;
                    if (i64Data == null) {
                        return 7;
                    }

                    if (formatted.Length != 20
                        || *(&i64Data[0]) != (i8[-128 127])45
                        || *(&i64Data[19]) != (i8[-128 127])56) {
                        return 8;
                    }

                    if (!System.Text.TryFormatBoolAscii(&formatted, true)) {
                        return 9;
                    }

                    stack rawptr<i8[-128 127]> trueData = formatted.Data;
                    if (formatted.Length != 4
                        || trueData == null
                        || *(&trueData[0]) != (i8[-128 127])116
                        || *(&trueData[3]) != (i8[-128 127])101) {
                        return 10;
                    }

                    if (!System.Text.TryFormatBoolAscii(&formatted, false)) {
                        return 11;
                    }

                    stack rawptr<i8[-128 127]> falseData = formatted.Data;
                    if (formatted.Length != 5
                        || falseData == null
                        || *(&falseData[0]) != (i8[-128 127])102
                        || *(&falseData[4]) != (i8[-128 127])101) {
                        return 12;
                    }

                    if (!System.Text.TryFormatI64Unicode(&wide, -9223372036854775808)) {
                        return 13;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideData = wide.Data;
                    if (wide.Length != 20
                        || wideData == null
                        || *(&wideData[0]) != 45
                        || *(&wideData[19]) != 56) {
                        return 14;
                    }

                    if (!System.Text.TryFormatBoolUnicode(&wide, false)) {
                        return 15;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideBoolData = wide.Data;
                    if (wide.Length != 5
                        || wideBoolData == null
                        || *(&wideBoolData[0]) != 102
                        || *(&wideBoolData[4]) != 101) {
                        return 16;
                    }

                    if (!System.Text.TryFormatU32Ascii(&formatted, (u32[0 max])4294967295)) {
                        return 17;
                    }

                    stack rawptr<i8[-128 127]> u32Data = formatted.Data;
                    if (formatted.Length != 10
                        || u32Data == null
                        || *(&u32Data[0]) != (i8[-128 127])52
                        || *(&u32Data[9]) != (i8[-128 127])53) {
                        return 18;
                    }

                    if (!System.Text.TryFormatU64Ascii(&formatted, (u64[0 max])18446744073709551615)) {
                        return 19;
                    }

                    stack rawptr<i8[-128 127]> u64Data = formatted.Data;
                    if (formatted.Length != 20
                        || u64Data == null
                        || *(&u64Data[0]) != (i8[-128 127])49
                        || *(&u64Data[1]) != (i8[-128 127])56
                        || *(&u64Data[19]) != (i8[-128 127])53) {
                        return 20;
                    }

                    if (!System.Text.TryFormatU64Unicode(&wide, (u64[0 max])18446744073709551615)) {
                        return 21;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideU64Data = wide.Data;
                    if (wide.Length != 20
                        || wideU64Data == null
                        || *(&wideU64Data[0]) != 49
                        || *(&wideU64Data[1]) != 56
                        || *(&wideU64Data[19]) != 53) {
                        return 22;
                    }

                    if (!System.Text.TryFormatI8Ascii(&formatted, -128)) {
                        return 23;
                    }

                    stack rawptr<i8[-128 127]> i8Data = formatted.Data;
                    if (formatted.Length != 4
                        || i8Data == null
                        || *(&i8Data[0]) != (i8[-128 127])45
                        || *(&i8Data[3]) != (i8[-128 127])56) {
                        return 24;
                    }

                    if (!System.Text.TryFormatU8Ascii(&formatted, (u8[0 max])255)) {
                        return 25;
                    }

                    stack rawptr<i8[-128 127]> u8Data = formatted.Data;
                    if (formatted.Length != 3
                        || u8Data == null
                        || *(&u8Data[0]) != (i8[-128 127])50
                        || *(&u8Data[2]) != (i8[-128 127])53) {
                        return 26;
                    }

                    if (!System.Text.TryFormatI16Unicode(&wide, -32768)) {
                        return 27;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideI16Data = wide.Data;
                    if (wide.Length != 6
                        || wideI16Data == null
                        || *(&wideI16Data[0]) != 45
                        || *(&wideI16Data[5]) != 56) {
                        return 28;
                    }

                    if (!System.Text.TryFormatU16Unicode(&wide, (u16[0 max])65535)) {
                        return 29;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideU16Data = wide.Data;
                    if (wide.Length != 5
                        || wideU16Data == null
                        || *(&wideU16Data[0]) != 54
                        || *(&wideU16Data[4]) != 53) {
                        return 30;
                    }

                    if (!System.Text.TryFormatI24Ascii(&formatted, -8388608)) {
                        return 31;
                    }

                    stack rawptr<i8[-128 127]> i24Data = formatted.Data;
                    if (formatted.Length != 8
                        || i24Data == null
                        || *(&i24Data[0]) != (i8[-128 127])45
                        || *(&i24Data[7]) != (i8[-128 127])56) {
                        return 32;
                    }

                    if (!System.Text.TryFormatU24Ascii(&formatted, (u24[0 max])16777215)) {
                        return 33;
                    }

                    stack rawptr<i8[-128 127]> u24Data = formatted.Data;
                    if (formatted.Length != 8
                        || u24Data == null
                        || *(&u24Data[0]) != (i8[-128 127])49
                        || *(&u24Data[7]) != (i8[-128 127])53) {
                        return 34;
                    }

                    if (!System.Text.TryFormatI48Unicode(&wide, -140737488355328)) {
                        return 35;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideI48Data = wide.Data;
                    if (wide.Length != 16
                        || wideI48Data == null
                        || *(&wideI48Data[0]) != 45
                        || *(&wideI48Data[15]) != 56) {
                        return 36;
                    }

                    if (!System.Text.TryFormatU48Unicode(&wide, (u48[0 max])281474976710655)) {
                        return 37;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideU48Data = wide.Data;
                    if (wide.Length != 15
                        || wideU48Data == null
                        || *(&wideU48Data[0]) != 50
                        || *(&wideU48Data[14]) != 53) {
                        return 38;
                    }

                    if (!System.Text.TryFormatI128Ascii(&formatted, -(2**127))) {
                        return 39;
                    }

                    stack rawptr<i8[-128 127]> i128Data = formatted.Data;
                    if (formatted.Length != 40
                        || i128Data == null
                        || *(&i128Data[0]) != (i8[-128 127])45
                        || *(&i128Data[39]) != (i8[-128 127])56) {
                        return 40;
                    }

                    if (!System.Text.TryFormatU128Ascii(&formatted, (u128[0 max])((2**128) - 1))) {
                        return 41;
                    }

                    stack rawptr<i8[-128 127]> u128Data = formatted.Data;
                    if (formatted.Length != 39
                        || u128Data == null
                        || *(&u128Data[0]) != (i8[-128 127])51
                        || *(&u128Data[38]) != (i8[-128 127])53) {
                        return 42;
                    }

                    if (!System.Text.TryFormatI96Unicode(&wide, -(2**95))) {
                        return 43;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideI96Data = wide.Data;
                    if (wide.Length != 30
                        || wideI96Data == null
                        || *(&wideI96Data[0]) != 45
                        || *(&wideI96Data[29]) != 56) {
                        return 44;
                    }

                    if (!System.Text.TryFormatU96Unicode(&wide, (u96[0 max])((2**96) - 1))) {
                        return 45;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideU96Data = wide.Data;
                    if (wide.Length != 29
                        || wideU96Data == null
                        || *(&wideU96Data[0]) != 55
                        || *(&wideU96Data[28]) != 53) {
                        return 46;
                    }

                    if (!System.Text.TryFormatI1024Ascii(&formatted, -(2**1023))) {
                        return 47;
                    }

                    stack rawptr<i8[-128 127]> i1024Data = formatted.Data;
                    if (i1024Data == null) {
                        return 48;
                    }

                    if (formatted.Length != 309) {
                        return 49;
                    }

                    if (*(&i1024Data[0]) != (i8[-128 127])45) {
                        return 50;
                    }

                    if (*(&i1024Data[308]) != (i8[-128 127])56) {
                        return 51;
                    }

                    if (!System.Text.TryFormatU1024Ascii(&formatted, (u1024[0 max])((2**1024) - 1))) {
                        return 52;
                    }

                    stack rawptr<i8[-128 127]> u1024Data = formatted.Data;
                    if (formatted.Length != 309
                        || u1024Data == null
                        || *(&u1024Data[0]) != (i8[-128 127])49
                        || *(&u1024Data[308]) != (i8[-128 127])53) {
                        return 53;
                    }

                    if (!System.Text.TryFormatI192Unicode(&wide, -(2**191))) {
                        return 54;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideI192Data = wide.Data;
                    if (wide.Length != 59
                        || wideI192Data == null
                        || *(&wideI192Data[0]) != 45
                        || *(&wideI192Data[58]) != 56) {
                        return 55;
                    }

                    if (!System.Text.TryFormatU768Unicode(&wide, (u768[0 max])((2**768) - 1))) {
                        return 56;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideU768Data = wide.Data;
                    if (wide.Length != 232
                        || wideU768Data == null
                        || *(&wideU768Data[0]) != 49
                        || *(&wideU768Data[231]) != 53) {
                        return 57;
                    }

                    if (!ReadParsedBool(System.Text.ParseBoolAscii("true"))) {
                        return 58;
                    }

                    if (ReadParsedBool(System.Text.ParseBoolAscii("false"))) {
                        return 59;
                    }

                    if (!ReadParsedBool(System.Text.ParseBoolUnicode((unicode)"true"))) {
                        return 60;
                    }

                    if (!IsInvalidBool(System.Text.ParseBoolAscii("True"))) {
                        return 61;
                    }

                    if (!IsInvalidBool(System.Text.ParseBoolUnicode((unicode)""))) {
                        return 62;
                    }

                    if (ReadParsedI64(System.Text.ParseI64Ascii("-9223372036854775808"), 0) != -(2**63)) {
                        return 63;
                    }

                    if (ReadParsedI64(System.Text.ParseI64Unicode((unicode)"9223372036854775807"), 0) != (2**63) - 1) {
                        return 64;
                    }

                    if (ReadParsedU64(System.Text.ParseU64Ascii("18446744073709551615"), 0) != (u64[0 max])((2**64) - 1)) {
                        return 65;
                    }

                    if (!IsInvalidI64(System.Text.ParseI64Ascii("12x"))) {
                        return 66;
                    }

                    if (!IsOverflowI64(System.Text.ParseI64Ascii("9223372036854775808"))) {
                        return 67;
                    }

                    if (!IsOverflowU64(System.Text.ParseU64Unicode((unicode)"18446744073709551616"))) {
                        return 68;
                    }

                    if (ReadParsedI8(System.Text.ParseI8Ascii("-128"), 0) != -128) {
                        return 69;
                    }

                    if (ReadParsedI24(System.Text.ParseI24Unicode((unicode)"-8388608"), 0) != -(2**23)) {
                        return 70;
                    }

                    if (ReadParsedI48(System.Text.ParseI48Ascii("-140737488355328"), 0) != -(2**47)) {
                        return 71;
                    }

                    if (ReadParsedU8(System.Text.ParseU8Unicode((unicode)"255"), 0) != (u8[0 max])((2**8) - 1)) {
                        return 72;
                    }

                    if (ReadParsedU32(System.Text.ParseU32Ascii("4294967295"), 0) != (u32[0 max])((2**32) - 1)) {
                        return 73;
                    }

                    if (ReadParsedU48(System.Text.ParseU48Unicode((unicode)"281474976710655"), 0) != (u48[0 max])((2**48) - 1)) {
                        return 74;
                    }

                    if (!IsOverflowI8(System.Text.ParseI8Ascii("-129"))) {
                        return 75;
                    }

                    if (!IsOverflowU32(System.Text.ParseU32Unicode((unicode)"4294967296"))) {
                        return 76;
                    }

                    stack i96[min max] parsedI96 = ReadParsedI96(System.Text.ParseI96Ascii("-39614081257132168796771975168"), 0);
                    if (!System.Text.TryFormatI96Ascii(&formatted, parsedI96)) {
                        return 77;
                    }

                    stack rawptr<i8[-128 127]> parsedI96Data = formatted.Data;
                    if (formatted.Length != 30) {
                        return 78;
                    }

                    if (parsedI96Data == null
                        || *(&parsedI96Data[0]) != (i8[-128 127])45
                        || *(&parsedI96Data[29]) != (i8[-128 127])56) {
                        return 78;
                    }

                    stack u96[0 max] parsedU96 = ReadParsedU96(System.Text.ParseU96Unicode((unicode)"79228162514264337593543950335"), 0);
                    if (!System.Text.TryFormatU96Ascii(&formatted, parsedU96)) {
                        return 79;
                    }

                    stack rawptr<i8[-128 127]> parsedU96Data = formatted.Data;
                    if (formatted.Length != 29
                        || parsedU96Data == null
                        || *(&parsedU96Data[0]) != (i8[-128 127])55
                        || *(&parsedU96Data[28]) != (i8[-128 127])53) {
                        return 80;
                    }

                    stack i192[min max] parsedI192 = ReadParsedI192(System.Text.ParseI192Ascii("-42"), 0);
                    if (!System.Text.TryFormatI192Ascii(&formatted, parsedI192)) {
                        return 91;
                    }

                    stack rawptr<i8[-128 127]> parsedI192Data = formatted.Data;
                    if (formatted.Length != 3
                        || parsedI192Data == null
                        || *(&parsedI192Data[0]) != (i8[-128 127])45
                        || *(&parsedI192Data[2]) != (i8[-128 127])50) {
                        return 92;
                    }

                    stack u192[0 max] parsedU192 = ReadParsedU192(System.Text.ParseU192Unicode((unicode)"42"), 0);
                    if (!System.Text.TryFormatU192Ascii(&formatted, parsedU192)) {
                        return 93;
                    }

                    stack rawptr<i8[-128 127]> parsedU192Data = formatted.Data;
                    if (formatted.Length != 2
                        || parsedU192Data == null
                        || *(&parsedU192Data[0]) != (i8[-128 127])52
                        || *(&parsedU192Data[1]) != (i8[-128 127])50) {
                        return 94;
                    }

                    if (!IsOverflowI96(System.Text.ParseI96Ascii("-39614081257132168796771975169"))) {
                        return 81;
                    }

                    if (!IsOverflowU96(System.Text.ParseU96Ascii("79228162514264337593543950336"))) {
                        return 82;
                    }

                    if (!System.Text.TryFormatF64Ascii(&formatted, -12.5)) {
                        return 83;
                    }

                    stack rawptr<i8[-128 127]> f64Data = formatted.Data;
                    if (formatted.Length != 10
                        || f64Data == null
                        || *(&f64Data[0]) != (i8[-128 127])45
                        || *(&f64Data[3]) != (i8[-128 127])46
                        || *(&f64Data[4]) != (i8[-128 127])53
                        || *(&f64Data[9]) != (i8[-128 127])48) {
                        return 84;
                    }

                    if (!System.Text.TryFormatF32Unicode(&wide, 3.25f)) {
                        return 85;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> f32WideData = wide.Data;
                    if (wide.Length != 8
                        || f32WideData == null
                        || *(&f32WideData[0]) != 51
                        || *(&f32WideData[1]) != 46
                        || *(&f32WideData[2]) != 50
                        || *(&f32WideData[7]) != 48) {
                        return 86;
                    }

                    stack i64[-9223372036854775808 9223372036854775807] ownedCount = 42;
                    if (!OwnedAsciiMatches(ownedCount.ToAscii(), 2, (i8[-128 127])52, (i8[-128 127])50)) {
                        return 87;
                    }

                    stack f64 ownedRatio = 1.25;
                    if (!OwnedAsciiMatches(ownedRatio.ToAscii(), 8, (i8[-128 127])49, (i8[-128 127])48)) {
                        return 88;
                    }

                    stack u64[0 max] ownedUnsigned = 99;
                    if (!OwnedUnicodeMatches(ownedUnsigned.ToUnicode(), 2, 57, 57)) {
                        return 89;
                    }

                    stack f64 ownedWideRatio = 3.5;
                    if (!OwnedUnicodeMatches(ownedWideRatio.ToUnicode(), 8, 51, 48)) {
                        return 90;
                    }

                    stack bool ownedFlag = true;
                    if (!OwnedAsciiMatches(ownedFlag.ToAscii(), 4, (i8[-128 127])116, (i8[-128 127])101)) {
                        return 109;
                    }

                    stack bool ownedWideFlag = false;
                    if (!OwnedUnicodeMatches(ownedWideFlag.ToUnicode(), 5, 102, 101)) {
                        return 110;
                    }

                    if (!OwnedAsciiMatches(System.Text.Encoding.UTF8.ToAscii(), 4, (i8[-128 127])85, (i8[-128 127])56)) {
                        return 111;
                    }

                    if (!OwnedAsciiMatches(System.Text.TextError.Overflow.ToAscii(), 8, (i8[-128 127])79, (i8[-128 127])119)) {
                        return 112;
                    }

                    if (!OwnedUnicodeMatches(System.Text.Encoding.UTF16.ToUnicode(), 5, 85, 54)) {
                        return 113;
                    }

                    if (!OwnedUnicodeMatches(System.Text.TextError.InvalidFormat.ToUnicode(), 13, 73, 116)) {
                        return 114;
                    }

                    stack i32[-2147483648 2147483647] ownedI32 = -2147483648;
                    if (!OwnedAsciiMatches(ownedI32.ToAscii(), 11, (i8[-128 127])45, (i8[-128 127])56)) {
                        return 115;
                    }

                    stack u32[0 max] ownedU32 = (u32[0 max])4294967295;
                    if (!OwnedUnicodeMatches(ownedU32.ToUnicode(), 10, 52, 53)) {
                        return 116;
                    }

                    stack i96[min max] ownedI96 = -(2**95);
                    if (!OwnedAsciiMatches(ownedI96.ToAscii(), 30, (i8[-128 127])45, (i8[-128 127])56)) {
                        return 117;
                    }

                    stack u96[0 max] ownedU96 = (u96[0 max])((2**96) - 1);
                    if (!OwnedUnicodeMatches(ownedU96.ToUnicode(), 29, 55, 53)) {
                        return 118;
                    }

                    stack i64[-9223372036854775808 9223372036854775807] score = 100;
                    if (!OwnedAsciiMatches("Score: " + score.ToAscii(), 10, (i8[-128 127])83, (i8[-128 127])48)) {
                        return 95;
                    }

                    stack f64 wideScore = 3.5;
                    if (!OwnedUnicodeMatches((unicode)"Value: " + wideScore.ToUnicode(), 15, 86, 48)) {
                        return 96;
                    }

                    if (!System.Text.TryFormatEncodingAscii(&formatted, System.Text.Encoding.UTF16)) {
                        return 97;
                    }

                    stack rawptr<i8[-128 127]> encodingData = formatted.Data;
                    if (formatted.Length != 5
                        || encodingData == null
                        || *(&encodingData[0]) != (i8[-128 127])85
                        || *(&encodingData[4]) != (i8[-128 127])54) {
                        return 98;
                    }

                    if (!System.Text.TryFormatTextErrorAscii(&formatted, System.Text.TextError.InvalidFormat)) {
                        return 99;
                    }

                    stack rawptr<i8[-128 127]> errorData = formatted.Data;
                    if (formatted.Length != 13
                        || errorData == null
                        || *(&errorData[0]) != (i8[-128 127])73
                        || *(&errorData[12]) != (i8[-128 127])116) {
                        return 100;
                    }

                    if (!System.Text.TryFormatEncodingUnicode(&wide, System.Text.Encoding.Binary)) {
                        return 101;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideEncodingData = wide.Data;
                    if (wide.Length != 6
                        || wideEncodingData == null
                        || *(&wideEncodingData[0]) != 66
                        || *(&wideEncodingData[5]) != 121) {
                        return 102;
                    }

                    if (!System.Text.TryFormatTextErrorUnicode(&wide, System.Text.TextError.Overflow)) {
                        return 103;
                    }

                    stack rawmutptr<i32[-2147483648 2147483647]> wideErrorData = wide.Data;
                    if (wide.Length != 8
                        || wideErrorData == null
                        || *(&wideErrorData[0]) != 79
                        || *(&wideErrorData[7]) != 119) {
                        return 104;
                    }

                    if (ReadParsedEncoding(System.Text.ParseEncodingUnicode((unicode)"UTF32")) != System.Text.Encoding.UTF32) {
                        return 105;
                    }

                    if (!IsInvalidEncoding(System.Text.ParseEncodingAscii("utf8"))) {
                        return 106;
                    }

                    if (ReadParsedTextError(System.Text.ParseTextErrorAscii("Overflow")) != System.Text.TextError.Overflow) {
                        return 107;
                    }

                    if (!IsInvalidTextError(System.Text.ParseTextErrorUnicode((unicode)"Unknown"))) {
                        return 108;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stderr.ToString());

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            await process!.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
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

