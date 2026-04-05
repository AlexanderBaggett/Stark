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
        Assert.Contains("define fastcc %stark_ascii @Echo(%stark_ascii %arg_text)", llvm);
        Assert.Contains(
            "ret %stark_ascii { ptr getelementptr inbounds ([3 x i8], ptr @.str.0, i32 0, i32 0), i64 2 }",
            llvm);
    }

    [Fact]
    public void TextSlicesStayZeroCopyViewsThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn ascii SliceAscii(ascii text, i32 start, i32 length) {
                return text[start, length];
            }

            fn unicode SliceUnicode(unicode text, i32 start, i32 length) {
                return text[start, length];
            }
            """);

        Assert.Contains("define fastcc %stark_ascii @SliceAscii(%stark_ascii %arg_text, i32 %arg_start, i32 %arg_length)", llvm);
        Assert.Contains("getelementptr inbounds i8, ptr", llvm);
        Assert.Contains("insertvalue %stark_ascii", llvm);

        Assert.Contains("define fastcc %stark_unicode @SliceUnicode(%stark_unicode %arg_text, i32 %arg_start, i32 %arg_length)", llvm);
        Assert.Contains("getelementptr inbounds i32, ptr", llvm);
        Assert.Contains("insertvalue %stark_unicode", llvm);
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

        Assert.Contains("define fastcc %stark_unicode @Run()", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds ([6 x i32], ptr @.str.", llvm);
        Assert.DoesNotContain("Unsupported SSA conversion from 'ascii' to 'unicode'", llvm);
    }

    [Fact]
    public void OwnedTextCoreTypesNeedNoModuleImport()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn i64 Run() {
                stack mut i8[8] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                stack mut Ascii asciiOwned = new Ascii() {
                    Data = &asciiBuffer[0],
                    Length = 0,
                    Capacity = 8
                };

                stack mut i32[4] unicodeBuffer = { 0, 0, 0, 0 };
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
        Assert.Contains("define fastcc i64 @Run()", llvm);
    }

    [Fact]
    public void OwnedAsciiConcatenationUsesExplicitCopyLoopAndViewProjection()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

            public fn ascii Run() {
                stack mut i8[16] buffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
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
        Assert.Contains("define fastcc i1 @TryConcatAscii(", llvm);
        Assert.Contains("define fastcc %stark_ascii @AsciiView(", llvm);
        Assert.Contains("%concat_left_index = phi i64", llvm);
        Assert.Contains("load i8, ptr %concat_left_src", llvm);
        Assert.DoesNotContain("@llvm.memcpy", llvm);
        Assert.Contains("call fastcc i1 @TryConcatAscii(", llvm);
        Assert.Contains("call fastcc %stark_ascii @AsciiView(", llvm);
    }

    [Fact]
    public void TextViewPointerAndLengthBuiltinsEmitConcreteDefinitions()
    {
        var llvm = CompileToLlvm(
            """
            module System.Text

            public finite law rawptr<i8> AsciiData(ascii source);
            public finite law i64 AsciiLength(ascii source);
            public finite law rawptr<i32> UnicodeData(unicode source);
            public finite law i64 UnicodeLength(unicode source);

            public fn i64 Run() {
                if (AsciiData("text") == null) {
                    return -1;
                }

                if (UnicodeData((unicode)"text") == null) {
                    return -2;
                }

                return AsciiLength("text") + UnicodeLength((unicode)"text");
            }
            """);

        Assert.Contains("define fastcc ptr @AsciiData(", llvm);
        Assert.Contains("define fastcc i64 @AsciiLength(", llvm);
        Assert.Contains("define fastcc ptr @UnicodeData(", llvm);
        Assert.Contains("define fastcc i64 @UnicodeLength(", llvm);
        Assert.Contains("call fastcc ptr @AsciiData(", llvm);
        Assert.Contains("call fastcc ptr @UnicodeData(", llvm);
        Assert.Contains("call fastcc i64 @AsciiLength(", llvm);
        Assert.Contains("call fastcc i64 @UnicodeLength(", llvm);
    }
}
