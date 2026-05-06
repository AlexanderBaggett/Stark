namespace compiler.FeatureTests;

public sealed class StringsFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void AsciiStringsUseConcreteRuntimeAbiThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law ascii Echo(ascii text) {
                return text;
            }

            finite law ascii Run() {
                return Echo("Hi");
            }
            """);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc noundef %stark_ascii @Echo(%stark_ascii noundef %arg_text)", llvm);
        Assert.Contains(
            "ret %stark_ascii { ptr getelementptr inbounds nuw ([3 x i8], ptr @.str.0, i32 0, i32 0), i64 2 }",
            llvm);
    }

    [Fact]
    public void TextSlicesStayZeroCopyViewsThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn ascii SliceAscii(ascii text, i32[-2147483648 2147483647] start, i32[-2147483648 2147483647] length) {
                return text[start, length];
            }

            fn unicode SliceUnicode(unicode text, i32[-2147483648 2147483647] start, i32[-2147483648 2147483647] length) {
                return text[start, length];
            }
            """);

        Assert.Contains("define fastcc noundef %stark_ascii @SliceAscii(%stark_ascii noundef %arg_text, i32 noundef %arg_start, i32 noundef %arg_length)", llvm);
        Assert.Contains("getelementptr i8, ptr", llvm);
        Assert.Contains("insertvalue %stark_ascii", llvm);

        Assert.Contains("define fastcc noundef %stark_unicode @SliceUnicode(%stark_unicode noundef %arg_text, i32 noundef %arg_start, i32 noundef %arg_length)", llvm);
        Assert.Contains("getelementptr i32, ptr", llvm);
        Assert.Contains("insertvalue %stark_unicode", llvm);
    }

    [Fact]
    public void SingleElementTextIndexingReturnsUnitLengthViewsThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn ascii PickAscii(ascii text, i32[-2147483648 2147483647] index) {
                return text[index];
            }

            fn unicode PickUnicode(unicode text, i32[-2147483648 2147483647] index) {
                return text[index];
            }
            """);

        Assert.Contains("define fastcc noundef %stark_ascii @PickAscii(%stark_ascii noundef %arg_text, i32 noundef %arg_index)", llvm);
        Assert.Contains("define fastcc noundef %stark_unicode @PickUnicode(%stark_unicode noundef %arg_text, i32 noundef %arg_index)", llvm);
        Assert.Contains("getelementptr i8, ptr", llvm);
        Assert.Contains("getelementptr i32, ptr", llvm);
        Assert.Contains("insertvalue %stark_ascii", llvm);
        Assert.Contains("insertvalue %stark_unicode", llvm);
        Assert.Contains(", i64 1, 1", llvm);
    }

    [Fact]
    public void ExplicitAsciiLiteralToUnicodeConversionUsesUnicodeStaticDataThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law unicode Run() {
                return (unicode)"Hello";
            }
            """);

        Assert.Contains("define fastcc noundef %stark_unicode @Run()", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds nuw ([6 x i32], ptr @.str.", llvm);
        Assert.DoesNotContain("Unsupported SSA conversion from 'ascii' to 'unicode'", llvm);
    }

    [Fact]
    public void OwnedTextCoreTypesNeedNoModuleImport()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            unsafe fn i64[-9223372036854775808 9223372036854775807] Run() {
                stack mut i8[-128 127][8] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                stack mut Ascii asciiOwned = new Ascii() {
                    Data = &asciiBuffer[0],
                    Length = 0,
                    Capacity = 8
                };

                stack mut i32[-2147483648 2147483647][4] unicodeBuffer = { 0, 0, 0, 0 };
                stack mut Unicode unicodeOwned = new Unicode() {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 4
                };

                return asciiOwned.Capacity + unicodeOwned.Capacity;
            }
            """);

        Assert.Contains("%Ascii = type { ptr, i64, i64 }", llvm);
        Assert.Contains("%Unicode = type { ptr, i64, i64 }", llvm);
        Assert.Contains("define fastcc noundef i64 @Run()", llvm);
    }

    [Fact]
    public void OwnedAsciiConcatenationUsesMemcpyAndViewProjection()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public unsafe finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

            public unsafe fn ascii Run() {
                stack mut i8[-128 127][16] buffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                stack mut Ascii owned = new Ascii() {
                    Data = &buffer[0],
                    Length = 0,
                    Capacity = 16
                };
                if (!TryConcatAscii(&owned, "Stark", " IO")) {
                    return "";
                }

                return AsciiView(owned);
            }
            """);

        Assert.Contains("%Ascii = type { ptr, i64, i64 }", llvm);
        Assert.Contains("define fastcc noundef i1 @TryConcatAscii(", llvm);
        Assert.Contains("define fastcc noundef %stark_ascii @AsciiView(", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr %concat_data, ptr %concat_left_data, i64 %concat_left_bytes, i1 false)", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr %concat_right_dest, ptr %concat_right_data, i64 %concat_right_bytes, i1 false)", llvm);
        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64(", llvm);
        Assert.DoesNotContain("%concat_left_index = phi i64", llvm);
        Assert.Contains("call fastcc i1 @TryConcatAscii(", llvm);
        Assert.Contains("call fastcc %stark_ascii @AsciiView(", llvm);
    }

    [Fact]
    public void FixedCapacityAsciiConcatenationLowersToStackStorageAndConcatTrap()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law i64[-9223372036854775808 9223372036854775807] AsciiLength(ascii source);
            public unsafe finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

            public unsafe fn i64[-9223372036854775808 9223372036854775807] Run() {
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
                return AsciiLength(AsciiView(combined));
            }
            """);

        Assert.Contains("call fastcc i1 @TryConcatAscii(", llvm);
        Assert.Contains("call fastcc %stark_ascii @AsciiView(", llvm);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", llvm);
        Assert.Contains("[4 x i8]", llvm);
    }

    [Fact]
    public void FixedCapacityUnicodeConcatenationLowersToStackStorageAndConcatTrap()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law unicode UnicodeView(Unicode source);
            public finite law i64[-9223372036854775808 9223372036854775807] UnicodeLength(unicode source);
            public unsafe finite bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            public unsafe fn i64[-9223372036854775808 9223372036854775807] Run() {
                stack mut i32[-2147483648 2147483647][2] leftStorage = { 65, 0 };
                stack mut Unicode left = new Unicode() {
                    Data = &leftStorage[0],
                    Length = 1,
                    Capacity = 2
                };
                stack mut i32[-2147483648 2147483647][2] rightStorage = { 66, 0 };
                stack mut Unicode right = new Unicode() {
                    Data = &rightStorage[0],
                    Length = 1,
                    Capacity = 2
                };
                stack Unicode combined[4] = left + right;
                return UnicodeLength(UnicodeView(combined));
            }
            """);

        Assert.Contains("call fastcc i1 @TryConcatUnicode(", llvm);
        Assert.Contains("call fastcc %stark_unicode @UnicodeView(", llvm);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", llvm);
        Assert.Contains("[4 x i32]", llvm);
    }

    [Fact]
    public void FixedCapacityAsciiInterpolationFormatsIntoStackStorage()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law i64[-9223372036854775808 9223372036854775807] AsciiLength(ascii source);
            public unsafe finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe finite bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32[-2147483648 2147483647] value);

            public unsafe fn i64[-9223372036854775808 9223372036854775807] Run() {
                stack i32[-2147483648 2147483647] score = 42;
                stack Ascii label[32] = $"Score: {score}";
                return AsciiLength(AsciiView(label));
            }
            """);

        Assert.Contains("call fastcc i1 @TryFormatI32Ascii(", llvm);
        Assert.Contains("call fastcc i1 @TryConcatAscii(", llvm);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", llvm);
        Assert.Contains("[32 x i8]", llvm);
    }

    [Fact]
    public void FixedCapacityUnicodeInterpolationFormatsIntoStackStorage()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law unicode UnicodeView(Unicode source);
            public finite law i64[-9223372036854775808 9223372036854775807] UnicodeLength(unicode source);
            public unsafe finite bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);
            public unsafe finite bool TryFormatI32Unicode(rawmutptr<Unicode> destination, i32[-2147483648 2147483647] value);

            public unsafe fn i64[-9223372036854775808 9223372036854775807] Run() {
                stack i32[-2147483648 2147483647] score = 42;
                stack Unicode label[32] = $"Score: {score}";
                return UnicodeLength(UnicodeView(label));
            }
            """);

        Assert.Contains("call fastcc i1 @TryFormatI32Unicode(", llvm);
        Assert.Contains("call fastcc i1 @TryConcatUnicode(", llvm);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", llvm);
        Assert.Contains("[32 x i32]", llvm);
    }

    [Fact]
    public void TextViewPointerAndLengthBuiltinsEmitConcreteDefinitions()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public unsafe finite law rawptr<i8[-128 127]> AsciiData(ascii source);
            public finite law i64[-9223372036854775808 9223372036854775807] AsciiLength(ascii source);
            public unsafe finite law rawptr<i32[-2147483648 2147483647]> UnicodeData(unicode source);
            public finite law i64[-9223372036854775808 9223372036854775807] UnicodeLength(unicode source);

            public unsafe fn i64[-9223372036854775808 9223372036854775807] Run(ascii asciiText, unicode unicodeText) {
                if (AsciiData(asciiText) == null) {
                    return -1;
                }

                if (UnicodeData(unicodeText) == null) {
                    return -2;
                }

                return AsciiLength(asciiText) + UnicodeLength(unicodeText);
            }
            """);

        Assert.Contains("define fastcc noundef ptr @AsciiData(", llvm);
        Assert.Contains("define fastcc noundef i64 @AsciiLength(", llvm);
        Assert.Contains("define fastcc noundef ptr @UnicodeData(", llvm);
        Assert.Contains("define fastcc noundef i64 @UnicodeLength(", llvm);
        Assert.Contains("call fastcc ptr @AsciiData(", llvm);
        Assert.Contains("call fastcc ptr @UnicodeData(", llvm);
        Assert.Contains("call fastcc i64 @AsciiLength(", llvm);
        Assert.Contains("call fastcc i64 @UnicodeLength(", llvm);
    }
}
