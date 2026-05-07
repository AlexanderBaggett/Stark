using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class MidLevelIrRuntimeTests
{
    [Fact]
    public async Task NonAsciiTextLiteralsPreferAsciiOverloadsAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn i32[-2147483648 2147483647] Pick(ascii text) {
                return 1;
            }

            fn i32[-2147483648 2147483647] Pick(unicode text) {
                return 2;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                return Pick("caf\u00E9");
            }
            """);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task CompileTimeTextConcatenationMatchesSingleLiteralAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                if ("Score: " + "100" == "Score: 100") {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FixedCapacityTextConcatenationCopiesIntoStackStorageAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law i64[-9223372036854775808 9223372036854775807] AsciiLength(ascii source);
            public finite law unicode UnicodeView(Unicode source);
            public finite law i64[-9223372036854775808 9223372036854775807] UnicodeLength(unicode source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
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

                if (AsciiLength(AsciiView(combined)) == 2 && UnicodeLength(UnicodeView(wideCombined)) == 2) {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task TextConcatenationRejectsNegativeDestinationCapacityAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module System.Text

            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            unsafe fn bool RejectsAsciiNegativeCapacity() {
                stack mut i8[-128 127][4] storage = { 0, 0, 0, 0 };
                stack mut Ascii destination = new Ascii() {
                    Data = &storage[0],
                    Length = 0,
                    Capacity = -1
                };

                return !TryConcatAscii(&destination, "A", "B");
            }

            unsafe fn bool RejectsUnicodeNegativeCapacity() {
                stack mut i32[-2147483648 2147483647][4] storage = { 0, 0, 0, 0 };
                stack mut Unicode destination = new Unicode() {
                    Data = &storage[0],
                    Length = 0,
                    Capacity = -1
                };

                return !TryConcatUnicode(&destination, (unicode)"A", (unicode)"B");
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                if (RejectsAsciiNegativeCapacity() && RejectsUnicodeNegativeCapacity()) {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FixedCapacityInterpolatedTextFormatsIntoStackStorageAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law i64[-9223372036854775808 9223372036854775807] AsciiLength(ascii source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

            public unsafe fn bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32[-2147483648 2147483647] value) {
                if (value == 42) {
                    return TryConcatAscii(destination, "", "42");
                }

                return false;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack i32[-2147483648 2147483647] score = 42;
                stack Ascii label[32] = $"Score: {score}";
                if (AsciiLength(AsciiView(label)) == 9) {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FixedCapacityUnicodeInterpolatedTextFormatsIntoStackStorageAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module System.Text

            public finite law unicode UnicodeView(Unicode source);
            public finite law i64[-9223372036854775808 9223372036854775807] UnicodeLength(unicode source);
            public unsafe fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            public unsafe fn bool TryFormatI32Unicode(rawmutptr<Unicode> destination, i32[-2147483648 2147483647] value) {
                if (value == 42) {
                    return TryConcatUnicode(destination, (unicode)"", (unicode)"42");
                }

                return false;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack i32[-2147483648 2147483647] score = 42;
                stack Unicode label[32] = $"Score: {score}";
                if (UnicodeLength(UnicodeView(label)) == 9) {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task CompileTimeInterpolatedTextMatchesSingleLiteralAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                const score = 100;
                if ($"Score: {score}" == "Score: 100") {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task StrictFpFunctionsCompileAndRunAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            strictfp fn f32 Add(f32 left, f32 right) {
                return left + right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                return (i32[-2147483648 2147483647])Add(1.0, 2.0);
            }
            """);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task FloatingPointArithmeticChainsCompileAndRunAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            strictfp fn f64 Compute(f32 left, i32[-2147483648 2147483647] middle, f64 right, f32 divisor) {
                return left + middle * right / divisor - 1.0;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                return (i32[-2147483648 2147483647])Compute(1.5, 2, 3.0, 0.5);
            }
            """);

        Assert.Equal(12, exitCode);
    }

    [Fact]
    public async Task HeapStorageUsesAllocatorLoweringAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task SystemMemoryReallocatePreservesAllocatorProvenanceAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module System.Memory

            public struct Allocator {
                u8[0 127] Kind;

                static finite law Allocator Default() {
                    return new Allocator() {
                        Kind = 0
                    };
                }

                finite law bool IsDefault(borrow Allocator self) {
                    return self.Kind == 0;
                }
            }

            internal struct Allocation {
                rawmutptr<i8[-128 127]> Pointer;
                u64[0 max] ByteLength;
                u64[1 max] Alignment;
                Allocator Allocator;
            }

            internal fn Allocation Allocate(Allocator allocator, u64[0 max] byteLength, u64[1 max] alignment);
            internal fn Allocation Reallocate(Allocation allocation, u64[0 max] byteLength, u64[1 max] alignment);
            internal fn void Free(Allocation allocation);

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Allocator allocator = new Allocator() {
                    Kind = 42
                };
                stack Allocation allocation = Allocate(allocator, 16, 8);
                stack Allocation grown = Reallocate(allocation, 32, 8);
                stack u8[0 127] kind = grown.Allocator.Kind;
                Free(grown);

                if (kind == 42) {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FixedArrayParameterDynamicIndexReadsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn i32[-2147483648 2147483647] Read(i32[-2147483648 2147483647][3] values, i32[-2147483648 2147483647] index) {
                return values[index];
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack i32[-2147483648 2147483647][3] values = { 4, 7, 9 };
                return Read(values, 1);
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task PartialFixedArrayInitializersInsideObjectInitializersZeroFillTrailingElementsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647][3] Values;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Box box = new Box() { Values = { 4, 7 } };
                return box.Values[0] + box.Values[1] + box.Values[2];
            }
            """);

        Assert.Equal(11, exitCode);
    }

    [Fact]
    public async Task SingleElementAsciiTextIndexReadsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn i32[-2147483648 2147483647] Pick(ascii text, i32[-2147483648 2147483647] index) {
                switch (text[index]) {
                    case 'b':
                        return 7;
                    default:
                        return 0;
                }
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                return Pick("abc", 1);
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task EmptyTextSlicesPreserveFullViewAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn ascii Whole(ascii text) {
                return text[];
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                switch (Whole("abc")[1]) {
                    case 'b':
                        return 7;
                    default:
                        return 0;
                }
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RecordEqualityAndInequalityCompareScalarFieldsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }

            fn bool Same(Pair left, Pair right) {
                return left == right;
            }

            fn bool Different(Pair left, Pair right) {
                return left != right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Pair sameLeft = new Pair(1, 2);
                stack Pair sameRight = new Pair(1, 2);
                stack Pair differentLeft = new Pair(1, 2);
                stack Pair differentRight = new Pair(2, 1);

                if (Same(sameLeft, sameRight) && Different(differentLeft, differentRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RecordOrderedComparisonsAreLexicographicAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            record Many(i32[-2147483648 2147483647] A, i32[-2147483648 2147483647] B, i32[-2147483648 2147483647] C, i32[-2147483648 2147483647] D, i32[-2147483648 2147483647] E) { }

            fn bool Less(Many left, Many right) {
                return left < right;
            }

            fn bool LessOrEqual(Many left, Many right) {
                return left <= right;
            }

            fn bool Greater(Many left, Many right) {
                return left > right;
            }

            fn bool GreaterOrEqual(Many left, Many right) {
                return left >= right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Many lessLeft = new Many(1, 2, 3, 4, 5);
                stack Many lessRight = new Many(1, 2, 3, 4, 6);
                stack Many lessOrEqualLeft = new Many(1, 2, 3, 4, 5);
                stack Many lessOrEqualRight = new Many(1, 2, 3, 4, 5);
                stack Many greaterLeft = new Many(1, 2, 3, 4, 6);
                stack Many greaterRight = new Many(1, 2, 3, 4, 5);
                stack Many greaterOrEqualLeft = new Many(1, 2, 3, 4, 5);
                stack Many greaterOrEqualRight = new Many(1, 2, 3, 4, 5);
                stack Many topLeft = new Many(2, 0, 0, 0, 0);
                stack Many topRight = new Many(1, 9, 9, 9, 9);

                if (Less(lessLeft, lessRight)
                    && LessOrEqual(lessOrEqualLeft, lessOrEqualRight)
                    && Greater(greaterLeft, greaterRight)
                    && GreaterOrEqual(greaterOrEqualLeft, greaterOrEqualRight)
                    && GreaterOrEqual(topLeft, topRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task FixedArrayEqualityAndInequalityCompareScalarElementsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn bool Same(i32[-2147483648 2147483647][2] left, i32[-2147483648 2147483647][2] right) {
                return left == right;
            }

            fn bool Different(i32[-2147483648 2147483647][2] left, i32[-2147483648 2147483647][2] right) {
                return left != right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack i32[-2147483648 2147483647][2] sameLeft = { 1, 2 };
                stack i32[-2147483648 2147483647][2] sameRight = { 1, 2 };
                stack i32[-2147483648 2147483647][2] differentLeft = { 1, 2 };
                stack i32[-2147483648 2147483647][2] differentRight = { 2, 1 };

                if (Same(sameLeft, sameRight) && Different(differentLeft, differentRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task VoidConditionalMemberCallStatementsExecuteAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Counter {
                i32[-2147483648 2147483647] Value;

                fn void Set(borrow mut Counter self, i32[-2147483648 2147483647] next) {
                    self.Value = next;
                    return;
                }
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack mut Counter left = new Counter() { Value = 0 };
                stack mut Counter right = new Counter() { Value = 0 };
                true ? left.Set(7) : right.Set(9);
                return left.Value + right.Value;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task EnumEqualityAndInequalityCompareTagAndPayloadAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            enum Token {
                None,
                Number(i32[-2147483648 2147483647]),
            }

            fn bool Same(Token left, Token right) {
                return left == right;
            }

            fn bool Different(Token left, Token right) {
                return left != right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Token sameLeft = Token.Number(7);
                stack Token sameRight = Token.Number(7);
                stack Token differentPayloadLeft = Token.Number(7);
                stack Token differentPayloadRight = Token.Number(9);
                stack Token differentVariantLeft = Token.Number(7);
                stack Token differentVariantRight = Token.None;

                if (Same(sameLeft, sameRight)
                    && Different(differentPayloadLeft, differentPayloadRight)
                    && Different(differentVariantLeft, differentVariantRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task EnumOrderedComparisonsAreLexicographicAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            enum Token {
                None,
                Number(i32[-2147483648 2147483647]),
                Pair(i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            fn bool Less(Token left, Token right) {
                return left < right;
            }

            fn bool LessOrEqual(Token left, Token right) {
                return left <= right;
            }

            fn bool Greater(Token left, Token right) {
                return left > right;
            }

            fn bool GreaterOrEqual(Token left, Token right) {
                return left >= right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Token lessPayloadLeft = Token.Number(7);
                stack Token lessPayloadRight = Token.Number(9);
                stack Token lessVariantLeft = Token.None;
                stack Token lessVariantRight = Token.Number(0);
                stack Token greaterPayloadLeft = Token.Pair(1, 9);
                stack Token greaterPayloadRight = Token.Pair(1, 7);
                stack Token greaterVariantLeft = Token.Pair(0, 0);
                stack Token greaterVariantRight = Token.Number(99);
                stack Token lessOrEqualLeft = Token.Number(5);
                stack Token lessOrEqualRight = Token.Number(5);
                stack Token greaterOrEqualLeft = Token.Pair(2, 0);
                stack Token greaterOrEqualRight = Token.Pair(2, 0);

                if (Less(lessPayloadLeft, lessPayloadRight)
                    && Less(lessVariantLeft, lessVariantRight)
                    && Greater(greaterPayloadLeft, greaterPayloadRight)
                    && Greater(greaterVariantLeft, greaterVariantRight)
                    && LessOrEqual(lessOrEqualLeft, lessOrEqualRight)
                    && GreaterOrEqual(greaterOrEqualLeft, greaterOrEqualRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task LargerScalarizableAggregatesCompareAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            record Many(i32[-2147483648 2147483647] A, i32[-2147483648 2147483647] B, i32[-2147483648 2147483647] C, i32[-2147483648 2147483647] D, i32[-2147483648 2147483647] E) { }

            enum Token {
                None,
                Many(i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            fn bool SameRecord(Many left, Many right) {
                return left == right;
            }

            fn bool DifferentRecord(Many left, Many right) {
                return left != right;
            }

            fn bool SameArray(i32[-2147483648 2147483647][5] left, i32[-2147483648 2147483647][5] right) {
                return left == right;
            }

            fn bool DifferentArray(i32[-2147483648 2147483647][5] left, i32[-2147483648 2147483647][5] right) {
                return left != right;
            }

            fn bool SameToken(Token left, Token right) {
                return left == right;
            }

            fn bool DifferentToken(Token left, Token right) {
                return left != right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack Many sameRecordLeft = new Many(1, 2, 3, 4, 5);
                stack Many sameRecordRight = new Many(1, 2, 3, 4, 5);
                stack Many differentRecordLeft = new Many(1, 2, 3, 4, 5);
                stack Many differentRecordRight = new Many(1, 2, 3, 4, 6);

                stack i32[-2147483648 2147483647][5] sameArrayLeft = { 1, 2, 3, 4, 5 };
                stack i32[-2147483648 2147483647][5] sameArrayRight = { 1, 2, 3, 4, 5 };
                stack i32[-2147483648 2147483647][5] differentArrayLeft = { 1, 2, 3, 4, 5 };
                stack i32[-2147483648 2147483647][5] differentArrayRight = { 1, 2, 3, 4, 6 };

                stack Token sameTokenLeft = Token.Many(1, 2, 3, 4, 5);
                stack Token sameTokenRight = Token.Many(1, 2, 3, 4, 5);
                stack Token differentTokenLeft = Token.Many(1, 2, 3, 4, 5);
                stack Token differentTokenRight = Token.Many(1, 2, 3, 4, 6);

                if (SameRecord(sameRecordLeft, sameRecordRight)
                    && DifferentRecord(differentRecordLeft, differentRecordRight)
                    && SameArray(sameArrayLeft, sameArrayRight)
                    && DifferentArray(differentArrayLeft, differentArrayRight)
                    && SameToken(sameTokenLeft, sameTokenRight)
                    && DifferentToken(differentTokenLeft, differentTokenRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task TextAndAggregateTextEqualityCompareContentsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            record Label(ascii Tag, unicode Word) { }

            fn bool SameAscii(ascii left, ascii right) {
                return left == right;
            }

            fn bool DifferentUnicode(unicode left, unicode right) {
                return left != right;
            }

            fn bool SameLabel(Label left, Label right) {
                return left == right;
            }

            fn bool DifferentLabel(Label left, Label right) {
                return left != right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                if (SameAscii("cab!"[1, 2], "zab?"[1, 2])
                    && DifferentUnicode(((unicode)"caf\u00E9!")[0, 4], ((unicode)"cafe?")[0, 4])
                    && SameLabel(
                        new Label("cab!"[1, 2], ((unicode)"caf\u00E9!")[0, 4]),
                        new Label("zab?"[1, 2], ((unicode)"caf\u00E9?")[0, 4]))
                    && DifferentLabel(
                        new Label("cab!"[1, 2], ((unicode)"caf\u00E9!")[0, 4]),
                        new Label("zbx?"[1, 2], ((unicode)"cafe?")[0, 4]))) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task SliceAndAggregateSliceEqualityCompareViewIdentityAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            record Window(i32[-2147483648 2147483647][] Items, i32[-2147483648 2147483647] Count) { }

            fn bool SameSliceSelf(i32[-2147483648 2147483647][] value) {
                return value == value;
            }

            fn bool DifferentSlice(i32[-2147483648 2147483647][] left, i32[-2147483648 2147483647][] right) {
                return left != right;
            }

            fn bool SameWindowSelf(Window value) {
                return value == value;
            }

            fn bool DifferentWindow(Window left, Window right) {
                return left != right;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack i32[-2147483648 2147483647][3] selfValues = { 1, 2, 3 };
                stack i32[-2147483648 2147483647][] selfView = selfValues;

                stack i32[-2147483648 2147483647][3] leftValues = { 1, 2, 3 };
                stack i32[-2147483648 2147483647][3] rightValues = { 1, 2, 3 };
                stack i32[-2147483648 2147483647][] leftView = leftValues;
                stack i32[-2147483648 2147483647][] rightView = rightValues;

                stack i32[-2147483648 2147483647][3] selfWindowValues = { 1, 2, 3 };
                stack i32[-2147483648 2147483647][3] leftWindowValues = { 1, 2, 3 };
                stack i32[-2147483648 2147483647][3] rightWindowValues = { 1, 2, 3 };

                if (SameSliceSelf(selfView)
                    && DifferentSlice(leftView, rightView)
                    && SameWindowSelf(new Window(selfWindowValues, 3))
                    && DifferentWindow(
                        new Window(leftWindowValues, 3),
                        new Window(rightWindowValues, 3))) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task ReassigningEnumDropsOnlyThePreviousActivePayloadAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Token {
                End,
                Text(Resource),
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack mut Token token = Token.Text(new Resource() { Value = 1 });
                token = Token.End;
                return Counter;
            }
            """);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task NestedAggregateDropsCascadeThroughStructFieldsAndEnumPayloadsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Token {
                End,
                Text(Resource),
            }

            struct Holder {
                Token Token;
                Resource Backup;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                {
                    stack Holder holder = new Holder() {
                        Token = Token.Text(new Resource() { Value = 3 }),
                        Backup = new Resource() { Value = 4 }
                    };
                }

                return Counter;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task SwitchPatternCaptureDropsMovedEnumPayloadAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Result<T> {
                Missing,
                Removed(T),
            }

            fn Result<Resource> Make() {
                return Result<Resource>.Removed(new Resource() { Value = 3 });
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                {
                    stack Result<Resource> result = Make();
                    switch (result) {
                        case Result<Resource>.Missing:
                            return 1;
                        case Result<Resource>.Removed(var removed):
                            if (Counter != 0 || removed.Value != 3) {
                                return 2;
                            }
                    }
                }

                return Counter;
            }
            """);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task MutableBorrowReceiverCallsObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Counter {
                i32[-2147483648 2147483647] Value;

                fn void Reset(borrow mut Counter self) {
                    self.Value = 0;
                    return;
                }

                fn void ResetThenAdd(borrow mut Counter self, i32[-2147483648 2147483647] value) {
                    self.Reset();
                    self.Value += value;
                    return;
                }

                fn i32[-2147483648 2147483647] Current(borrow Counter self) {
                    return self.Value;
                }
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack mut Counter counter = new Counter() { Value = 9 };
                counter.ResetThenAdd(7);
                return counter.Current();
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task NestedMutableBorrowReceiverCallsMutateStoredFieldAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Counter {
                u32[0 max] Value;

                fn void Increment(mut borrow Counter self) {
                    self.Value += 1;
                }
            }

            struct Holder {
                Counter Inner;

                fn void Bump(mut borrow Holder self) {
                    self.Inner.Increment();
                }

                finite law u32[0 max] Get(borrow Holder self) {
                    return self.Inner.Value;
                }
            }

            export unsafe ffi fn i32[min max] main() {
                stack mut Holder holder = new Holder() {
                    Inner = new Counter() {
                        Value = 0
                    }
                };

                holder.Bump();
                holder.Bump();
                return (i32[min max])holder.Get();
            }
            """);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task OutArgumentsWriteBackToCallerLocalsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn bool WriteValue(out u32[0 max] value) {
                value = 7;
                return true;
            }

            export unsafe ffi fn i32[min max] main() {
                stack mut u32[0 max] value = 1;
                if (!WriteValue(value)) {
                    return 99;
                }

                return (i32[min max])value;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task SwitchExpressionCallOnEnumIsEvaluatedOnceAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            enum Status {
                Ok,
                Err(i32[-2147483648 2147483647]),
            }

            fn Status Next() {
                Counter += 1;
                return Status.Ok;
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                switch (Next()) {
                    case Status.Ok:
                        return Counter;
                    case Status.Err(var error):
                        return error;
                }
            }
            """);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task AggregateAndEnumSwitchPatternsMatchTextLeavesAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            record Label(ascii Tag, unicode Word) { }

            enum Token {
                Empty,
                Text { Tag: ascii, Word: unicode },
            }

            fn bool MatchLabel(Label value) {
                switch (value) {
                    case Label("ab", var word):
                        return word == (unicode)"cat";
                    default:
                        return false;
                }
            }

            fn bool MatchToken(Token value) {
                switch (value) {
                    case Token.Text { Tag: "ab", Word: var word }:
                        return word == (unicode)"cat";
                    case Token.Empty:
                        return false;
                    default:
                        return false;
                }
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                if (MatchLabel(new Label("ab", (unicode)"cat"))
                    && MatchToken(Token.Text { Tag: "ab", Word: (unicode)"cat" })
                    && !MatchLabel(new Label("zz", (unicode)"cat"))
                    && !MatchToken(Token.Text { Tag: "zz", Word: (unicode)"cat" })) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RawPointerIndexedFieldAddressesObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Buffer {
                i8[-128 127][16] Storage;
                i64[-9223372036854775808 9223372036854775807] WritePos;
            }

            unsafe fn void Put(rawmutptr<Buffer> buffer, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                *(&(*buffer).Storage[index]) = value;
                return;
            }

            unsafe fn i32[-2147483648 2147483647] Read(rawmutptr<Buffer> buffer, i64[-9223372036854775808 9223372036854775807] index) {
                return (i32[-2147483648 2147483647])*(&(*buffer).Storage[index]);
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack mut Buffer buffer = new Buffer();
                Put(&buffer, 3, (i8[-128 127])90);
                return Read(&buffer, 3);
            }
            """);

        Assert.Equal(90, exitCode);
    }

    [Fact]
    public async Task RawPointerIndexedElementsObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            unsafe fn void Put(rawmutptr<i8[-128 127]> data, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                *(&data[index]) = value;
                return;
            }

            unsafe fn i32[-2147483648 2147483647] Read(rawmutptr<i8[-128 127]> data, i64[-9223372036854775808 9223372036854775807] index) {
                return (i32[-2147483648 2147483647])*(&data[index]);
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                stack mut i8[-128 127][16] buffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                Put(&buffer[0], 4, (i8[-128 127])77);
                return Read(&buffer[0], 4);
            }
            """);

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task BorrowReceiverIndexedFieldAddressesObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Buffer {
                i8[-128 127][16] Storage;

                unsafe fn void Put(borrow mut Buffer self, u8[0 15] index, i8[min max] value) {
                    *(&self.Storage[index]) = value;
                    return;
                }

                unsafe fn i32[min max] Read(borrow Buffer self, u8[0 15] index) {
                    return (i32[min max])*(&self.Storage[index]);
                }
            }

            export unsafe ffi fn i32[min max] main() {
                stack mut Buffer buffer = new Buffer();
                buffer.Put(5, (i8[min max])65);
                return buffer.Read(5);
            }
            """);

        Assert.Equal(65, exitCode);
    }

    [Fact]
    public async Task LargeAggregateByValueCallsAndReturnsWorkAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
            }

            fn Big Make(i64[-9223372036854775808 9223372036854775807] seed) {
                return new Big() { A = seed, B = seed + 1, C = seed + 2 };
            }

            fn i32[-2147483648 2147483647] Read(Big value) {
                return (i32[-2147483648 2147483647])(value.A + value.C);
            }

            export unsafe ffi fn i32[-2147483648 2147483647] main() {
                return Read(Make(20));
            }
            """);

        Assert.Equal(42, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-mir-enum-drop-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-exe", "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var standardOutput = await process!.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(string.Empty, standardOutput);
            Assert.Equal(string.Empty, standardError);
            return process.ExitCode;
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
