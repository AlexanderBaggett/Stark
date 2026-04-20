using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeCheckingTests
{
    [Fact]
    public void IntegerExponentiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run() {
                return 2 ** 3;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void TypeRelativeIntegerRangeEndpointsResolveAgainstContainingIntegerType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Signed(i32[min max] value) {
                return value;
            }

            fn i64[0 max] NonNegative(i64[0 max] value) {
                return value;
            }

            fn u8[min 127] BytePrefix(u8[min 127] value) {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var signed = typeCheckModel.Functions["Signed"];
        AssertIntegerRange(signed.ReturnType, 32, new BigInteger(int.MinValue), new BigInteger(int.MaxValue));
        AssertIntegerRange(signed.Parameters[0].Type, 32, new BigInteger(int.MinValue), new BigInteger(int.MaxValue));

        var nonNegative = typeCheckModel.Functions["NonNegative"];
        AssertIntegerRange(nonNegative.ReturnType, 64, BigInteger.Zero, new BigInteger(long.MaxValue));
        AssertIntegerRange(nonNegative.Parameters[0].Type, 64, BigInteger.Zero, new BigInteger(long.MaxValue));

        var bytePrefix = typeCheckModel.Functions["BytePrefix"];
        AssertIntegerRange(bytePrefix.ReturnType, 8, BigInteger.Zero, new BigInteger(127));
        AssertIntegerRange(bytePrefix.Parameters[0].Type, 8, BigInteger.Zero, new BigInteger(127));
    }

    [Fact]
    public void ImportedModulePublicMembersResolveByFinalName()
    {
        var result = Compile(
            """
            import Lib.Foundation
            module Demo

            fn i32[0 max] Use() {
                stack Box box = new() { Value = Identity(Answer) };
                stack Status status = Status.Ok;
                switch (status) {
                    case Status.Ok:
                        return box.Value;
                    case Status.Err:
                        return Worker.Value();
                }
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Lib.Foundation", "Lib/Foundation.stark"),
                        """
                        module Lib.Foundation

                        public const i32[0 max] Answer = 41;

                        public struct Box {
                            i32[0 max] Value;
                        }

                        public enum Status {
                            Ok,
                            Err
                        }

                        public struct Worker {
                            static finite law i32[0 max] Value() {
                                return 7;
                            }
                        }

                        public fn i32[0 max] Identity(i32[0 max] value) {
                            return value;
                        }
                        """,
                        "Lib/Foundation.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void AmbiguousImportedTypeFinalNamesRequireQualification()
    {
        var result = Compile(
            """
            import Left
            import Right
            module Demo

            fn i32[0 max] Use() {
                stack Value value = new() { X = 1 };
                return value.X;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Left", "Left.stark"),
                        """
                        module Left

                        public struct Value {
                            i32[0 max] X;
                        }
                        """,
                        "Left.stark"),
                    (
                        new ResolvedModuleReference("Right", "Right.stark"),
                        """
                        module Right

                        public struct Value {
                            i32[0 max] X;
                        }
                        """,
                        "Right.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3004"
                && diagnostic.Message.Contains("Imported type name 'Value'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Left.Value", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Right.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointsResolveAtCompileTime()
    {
        var result = Compile(
            """
            module Demo

            fn i32[10**2 10**10] DecimalPowers(i32[10**2 10**10] value) {
                return value;
            }

            fn i32[2**4 2**16] BinaryPowers(i32[2**4 2**16] value) {
                return value;
            }

            fn i64[1024 * 1024 1024 * 1024 * 1024] Sizes(i64[1024 * 1024 1024 * 1024 * 1024] value) {
                return value;
            }

            fn i32[(1 + 2) * 3 20 / 2 + 1] MixedArithmetic(i32[(1 + 2) * 3 20 / 2 + 1] value) {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var decimalPowers = typeCheckModel.Functions["DecimalPowers"];
        AssertIntegerRange(decimalPowers.ReturnType, 32, new BigInteger(100), BigInteger.Parse("10000000000"));
        AssertIntegerRange(decimalPowers.Parameters[0].Type, 32, new BigInteger(100), BigInteger.Parse("10000000000"));

        var binaryPowers = typeCheckModel.Functions["BinaryPowers"];
        AssertIntegerRange(binaryPowers.ReturnType, 32, new BigInteger(16), new BigInteger(65536));
        AssertIntegerRange(binaryPowers.Parameters[0].Type, 32, new BigInteger(16), new BigInteger(65536));

        var sizes = typeCheckModel.Functions["Sizes"];
        AssertIntegerRange(sizes.ReturnType, 64, new BigInteger(1048576), new BigInteger(1073741824));
        AssertIntegerRange(sizes.Parameters[0].Type, 64, new BigInteger(1048576), new BigInteger(1073741824));

        var mixedArithmetic = typeCheckModel.Functions["MixedArithmetic"];
        AssertIntegerRange(mixedArithmetic.ReturnType, 32, new BigInteger(9), new BigInteger(11));
        AssertIntegerRange(mixedArithmetic.Parameters[0].Type, 32, new BigInteger(9), new BigInteger(11));
    }

    [Fact]
    public void UnsupportedIntegerRangeEndpointIdentifiersAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[foo max] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("Integer range endpoint 'foo'", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointOverflowIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 2**2048] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("overflowed", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointDivisionByZeroIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 10 / 0] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("divide by zero", StringComparison.Ordinal));
    }

    [Fact]
    public void ReversedTypeRelativeIntegerRangeEndpointsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[max min] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("lower bound", StringComparison.Ordinal)
                && diagnostic.Message.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void ReversedConstantArithmeticIntegerRangeEndpointsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[2**8 2**4] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("lower bound", StringComparison.Ordinal)
                && diagnostic.Message.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeRelativeIntegerEndpointNamesAreRejectedOutsideIntegerRanges()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Bad() {
                return min;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'min'", StringComparison.Ordinal));
    }

    [Fact]
    public void BitwiseXorRequiresIntegerOperands()
    {
        var result = Compile(
            """
            module Demo

            finite law f32 Run() {
                return 1.0 ^ 2.0;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("integer operands", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitArithmeticOperatorsTypeCheckWithoutPlaceholderDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                stack mut i32[-2147483648 2147483647] value = left;
                value +%= right;
                stack i32[-2147483648 2147483647] product = left *| right;
                return -%value +% product +| 3;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void StrictFpModifierTypeChecksNowThatLoweringExists()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f32 Run(f32 left, f32 right) {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void FloatingPointArithmeticChainsTypeCheckAcrossMixedNumericOperands()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f64 Run(f32 left, i32[-2147483648 2147483647] middle, f64 right, f32 divisor) {
                return left + middle * right / divisor - 1.0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitConversionsPointerOperatorsAndSliceViewsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run(i64[-9223372036854775808 9223372036854775807] bits, ascii text) {
                stack mut i32[-2147483648 2147483647] value = 7;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                stack rawptr<i32[-2147483648 2147483647]> readonlyPtr = (rawptr<i32[-2147483648 2147483647]>)ptr;
                *ptr = (i32[-2147483648 2147483647])bits;
                stack i64[-9223372036854775808 9223372036854775807] address = (i64[-9223372036854775808 9223372036854775807])ptr;
                stack rawmutptr<i32[-2147483648 2147483647]> roundTrip = (rawmutptr<i32[-2147483648 2147483647]>)address;
                stack i32[-2147483648 2147483647][2] values = { 1, 2 };
                stack i32[-2147483648 2147483647][] view = (i32[-2147483648 2147483647][])values;
                return *roundTrip + view[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void TextEscapeLiteralsPreferUtf8BackedAsciiUnlessExplicitlyConverted()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii AsciiString() {
                return "\0\b\t\n\f\r\\\"\'";
            }

            finite law ascii AsciiChar() {
                return '\x41';
            }

            finite law unicode UnicodeString() {
                return (unicode)"\xC9";
            }

            finite law unicode UnicodeChar() {
                return (unicode)'\u03B1';
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstGlobalAggregateProjectionsCanBindToFrozenParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            struct Holder {
                Box Item;
            }

            const Holder Current = new Holder() { Item = new Box() { Value = 7 } };

            fn i32[-2147483648 2147483647] Read(frozen Box box) {
                return 7;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Read(Current.Item);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ExplicitLiteralTextConversionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            finite law unicode Widen() {
                return (unicode)"Hello";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EmptyTextSlicesTypeCheckAsSameTextKind()
    {
        var result = Compile(
            """
            module Demo

            fn ascii SliceAscii(ascii text) {
                return text[];
            }

            fn unicode SliceUnicode(unicode text) {
                return text[];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayLengthsAcceptConstantArithmeticExpressions()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647][1 + 2] values) {
                return values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayInitializersCanOmitTrailingElements()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][3] values = { 1, 2 };
                return values[0] + values[1] + values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ScalarizableNamedAggregatesAreOrderedComparable()
    {
        var result = Compile(
            """
            module Demo

            record Many(i32[-2147483648 2147483647] A, i32[-2147483648 2147483647] B, i32[-2147483648 2147483647] C, i32[-2147483648 2147483647] D, i32[-2147483648 2147483647] E) { }

            fn bool Less(Many left, Many right) {
                return left < right;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ScalarizableEnumsAreOrderedComparable()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                None,
                Many(i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            fn bool Less(Token left, Token right) {
                return left < right;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitNonAsciiLiteralToAsciiConversionTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Run() {
                return (ascii)"\u03B1";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FrozenReachableViewsTypeCheckAsReadonlyAliases()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            struct PtrBox {
                rawmutptr<i32[-2147483648 2147483647]> Ptr;
            }

            finite law void Run(frozen Box box, frozen PtrBox ptrBox) {
                stack rawptr<frozen i32[-2147483648 2147483647]> valuePtr = &box.Value;
                stack rawptr<frozen i32[-2147483648 2147483647]> readonlyPtr = ptrBox.Ptr;
                stack bool same = *valuePtr == *readonlyPtr;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void AggregateSwitchPatternsTypeCheckOnScalarFields()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }

            finite law i32[-2147483648 2147483647] Run(Pair value) {
                switch (value) {
                    case Pair(1, var right):
                        return right;
                    case Pair(_, _):
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NamedAggregateWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }

            finite law i32[-2147483648 2147483647] Run(Pair value) {
                switch (value) {
                    case var whole:
                        return whole.Left;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NestedAggregateWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }
            record Outer(Pair Values, i32[-2147483648 2147483647] Tail) { }

            finite law i32[-2147483648 2147483647] Run(Outer value) {
                switch (value) {
                    case Outer(Pair capture, var tail):
                        return capture.Right + tail;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EnumWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                Empty,
                Pair(i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            finite law i32[-2147483648 2147483647] Run(Token value) {
                switch (value) {
                    case Token.Pair capture:
                        switch (capture) {
                            case Token.Pair(var left, var right):
                                return left + right;
                            default:
                                return 0;
                        }
                    default:
                        return -1;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void GuardedSwitchLabelsDoNotContributeToReachabilityCoverage()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run(bool value, bool allow) {
                switch (value) {
                    case true when allow:
                        return 1;
                    case true:
                        return 2;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3019");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NestedAggregateSwitchPatternsTypeCheckOnScalarLeaves()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }
            record Outer(Pair Values, i32[-2147483648 2147483647] Tail) { }

            finite law i32[-2147483648 2147483647] Run(Outer value) {
                switch (value) {
                    case Outer(Pair(1, var right), var tail):
                        return right + tail;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EnumSwitchPatternsTypeCheckOnCasePayloadCaptures()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32[-2147483648 2147483647]),
                Move { X: i32[-2147483648 2147483647], Y: i32[-2147483648 2147483647] },
            }

            finite law i32[-2147483648 2147483647] Run(Token token) {
                switch (token) {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move { X: var x, Y: var y }:
                        return x + y;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    // ---- Generic type instantiation ----

    [Fact]
    public void GenericEnumInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law bool HasValue(Option<i32[-2147483648 2147483647]> opt) {
                switch (opt) {
                    case Option<i32[-2147483648 2147483647]>.None:
                        return false;
                    case Option<i32[-2147483648 2147483647]>.Some(var value):
                        return value > 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Option"), "generic template should be registered");
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Option<i32>"), "monomorphized type should be registered");
        var monomorphized = typeCheckModel.NamedTypes["Option<i32>"];
        Assert.Equal(DeclarationKind.Enum, monomorphized.Kind);
        Assert.Equal(2, monomorphized.Variants.Count);
        Assert.True(typeCheckModel.NamedTypes["Option"].IsGeneric, "template should be marked generic");
    }

    [Fact]
    public void GenericRecordInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            finite law i32[-2147483648 2147483647] Sum(Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]> pair) {
                return pair.First + pair.Second;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Pair<i32,i32>"), "monomorphized pair should be registered");
        var concrete = typeCheckModel.NamedTypes["Pair<i32,i32>"];
        Assert.Equal(2, concrete.OrderedFields.Count);
        Assert.Equal(StarkTypeKind.Integer, concrete.OrderedFields[0].Type.Kind);
        Assert.Equal(StarkTypeKind.Integer, concrete.OrderedFields[1].Type.Kind);
    }

    [Fact]
    public void GenericRecordPrimaryConstructorInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            finite law i32[-2147483648 2147483647] Sum() {
                stack Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]> pair = new Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]>(3, 4);
                return pair.First + pair.Second;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void GenericTypeUsesRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            fn bool Accept(Pair<i32[-2147483648 2147483647], bool> pair) {
                return pair.Second;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.TypeTriggers);
        Assert.Equal("Pair<i32,bool>", trigger.TypeName);
        Assert.Equal(["i32", "bool"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void NestedGenericTypesInsideContainersMonomorphizeAndRecordTriggers()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            fn i32[-2147483648 2147483647] Read(rawptr<Pair<i32[-2147483648 2147483647], bool>> ptr) {
                if ((*ptr).Second) {
                    return (*ptr).First;
                }

                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Pair<i32,bool>"));
        Assert.Contains(typeCheckModel.TypeTriggers, static trigger => trigger.TypeName == "Pair<i32,bool>");
    }

    [Fact]
    public void GenericFunctionBodiesCanUseTheirTypeParametersInLocalTypes()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                stack T copy = value;
                return copy;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.TryGetValue("Identity", out var signature));
        Assert.True(signature.IsGeneric);
        Assert.Equal(["T"], signature.GenericParams);
        Assert.Equal("T", signature.ReturnType.DisplayName);
        Assert.Equal("T", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericMethodBodiesCanUseTheirTypeParametersInLocalTypes()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn T Echo<T>(T value) {
                    stack T copy = value;
                    return copy;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.TryGetValue("Box.Echo", out var signature));
        Assert.True(signature.IsGeneric);
        Assert.Equal(["T"], signature.GenericParams);
        Assert.Equal("T", signature.ReturnType.DisplayName);
        Assert.Equal("T", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericFunctionCallsRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647] value = 42;
                return Identity(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Identity", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(trigger.Signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericMethodCallsRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn T Echo<T>(borrow Box self, T value) {
                    return value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = new Box();
                stack i32[-2147483648 2147483647] value = 42;
                return box.Echo(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Box.Echo", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal(2, trigger.Signature.Parameters.Count);
        Assert.Equal("borrow Box", trigger.Signature.Parameters[0].Type.DisplayName);
        Assert.Equal("i32", trigger.Signature.Parameters[1].Type.DisplayName);
    }

    [Fact]
    public void GenericMethodsOnGenericTypesRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            struct Box<T> {
                T Value;

                fn T Echo(borrow Box<T> self, T value) {
                    return value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box<i32[-2147483648 2147483647]> box = new Box<i32[-2147483648 2147483647]>() { Value = 1 };
                stack i32[-2147483648 2147483647] value = 42;
                return box.Echo(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Box.Echo", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal(2, trigger.Signature.Parameters.Count);
        Assert.Equal("borrow Box<i32>", trigger.Signature.Parameters[0].Type.DisplayName);
        Assert.Equal("i32", trigger.Signature.Parameters[1].Type.DisplayName);
    }

    [Fact]
    public void RepeatedGenericFunctionCallsReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return Identity(left) + Identity(right);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Identity", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void RepeatedGenericTypeUsesReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            record Pair<T>(T Value) { }

            fn i32[-2147483648 2147483647] Add(Pair<i32[-2147483648 2147483647]> left, Pair<i32[-2147483648 2147483647]> right) {
                return left.Value + right.Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.TypeTriggers);
        Assert.Equal("Pair<i32>", trigger.TypeName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void ConcreteOverloadsBeatMatchingGenericInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Parse(i32[-2147483648 2147483647] value) {
                return value;
            }

            fn T Parse<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647] value = 42;
                return Parse(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.InstantiationTriggers);
    }

    [Fact]
    public void TypeAliasesResolveToTheirUnderlyingTypes()
    {
        var result = Compile(
            """
            module Demo

            alias Byte = i8[-128 127];

            fn Byte Inc(Byte value) {
                return value + 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Byte"));
        Assert.True(typeCheckModel.Functions.TryGetValue("Inc", out var signature));
        Assert.Equal("i8", signature.ReturnType.DisplayName);
        Assert.Equal("i8", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericTypeAliasesSubstituteIntoTheirUnderlyingTypes()
    {
        var result = Compile(
            """
            module Demo

            alias Ptr<T> = rawptr<T>;

            fn i32[-2147483648 2147483647] Read(Ptr<i32[-2147483648 2147483647]> value) {
                return *value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Ptr"));
        var parameter = Assert.Single(typeCheckModel.Functions["Read"].Parameters).Type;
        Assert.Equal(StarkTypeKind.RawPointer, parameter.Kind);
        Assert.NotNull(parameter.ElementType);
        Assert.Equal("i32", parameter.ElementType!.DisplayName);
    }

    [Fact]
    public void GenericTypeWithWrongArgCountIsAnError()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law void Bad(Option<i32[-2147483648 2147483647], bool> opt) {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static d => d.Code == "STK3019");
    }

    [Fact]
    public void GenericEnumVariantFieldTypeIsSubstituted()
    {
        var result = Compile(
            """
            module Demo

            enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            finite law i32[-2147483648 2147483647] Unwrap(Result<i32[-2147483648 2147483647], bool> res) {
                switch (res) {
                    case Result<i32[-2147483648 2147483647], bool>.Ok(var value):
                        return value;
                    case Result<i32[-2147483648 2147483647], bool>.Err(var err):
                        if (err) {
                            return -1;
                        }
                        return -1;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Result<i32,bool>"));
        var concrete = typeCheckModel.NamedTypes["Result<i32,bool>"];
        var okVariant = concrete.Variants.Single(static v => v.Name == "Ok");
        Assert.Equal(StarkTypeKind.Integer, okVariant.Fields[0].Type.Kind);
        var errVariant = concrete.Variants.Single(static v => v.Name == "Err");
        Assert.Equal(StarkTypeKind.Bool, errVariant.Fields[0].Type.Kind);
    }

    [Fact]
    public void NonGenericTypeWithTypeArgumentsIsAnError()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32[-2147483648 2147483647] X, i32[-2147483648 2147483647] Y) { }

            finite law void Bad(Point<i32[-2147483648 2147483647]> p) {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static d => d.Code == "STK3019");
    }

    [Fact]
    public void TopLevelOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Parse(i32[-2147483648 2147483647] value) {
                return value;
            }

            fn bool Parse(bool value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Parse(42);
            }

            fn bool RunBool() {
                return Parse(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Overloads.TryGetValue("Parse", out var overloads));
        Assert.Equal(2, overloads.Count);
        Assert.Equal(
            2,
            typeCheckModel.Functions.Keys.Count(static name => name.StartsWith("Parse#(", StringComparison.Ordinal)));
    }

    [Fact]
    public void MethodOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Counter {
                i32[-2147483648 2147483647] Value;

                fn i32[-2147483648 2147483647] Scale(borrow Counter self, i32[-2147483648 2147483647] factor) {
                    return self.Value * factor;
                }

                fn i32[-2147483648 2147483647] Scale(borrow Counter self, bool doubleIt) {
                    if (doubleIt) {
                        return self.Value * 2;
                    }

                    return self.Value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Counter counter = new Counter() { Value = 3 };
                return counter.Scale(4);
            }

            fn i32[-2147483648 2147483647] RunBool() {
                stack Counter counter = new Counter() { Value = 3 };
                return counter.Scale(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Overloads.TryGetValue("Counter.Scale", out var overloads));
        Assert.Equal(2, overloads.Count);
        Assert.Equal(
            2,
            typeCheckModel.Functions.Keys.Count(static name => name.StartsWith("Counter.Scale#(", StringComparison.Ordinal)));
    }

    [Fact]
    public void TargetTypedObjectCreationResolvesFromDestinationType()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;

                Box() {
                    self.Value = 0;
                }

                Box(i32[min max] value) {
                    self.Value = value;
                }
            }

            fn Box Make(i32[min max] value) {
                return new(value);
            }

            fn i32[min max] Run(i32[min max] value) {
                stack Box empty = new();
                stack Box initialized = new() { Value = value };
                stack mut Box assigned = new(value);
                assigned = new(value);
                return assigned.Value + empty.Value + initialized.Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var objectCreations = typeCheckModel.ObjectCreations.ToArray();
        Assert.Equal(5, objectCreations.Length);
        Assert.All(objectCreations, static objectCreation => Assert.Equal("Box", objectCreation.CreatedType.DisplayName));
        Assert.Equal(5, objectCreations.Count(static objectCreation => objectCreation.Constructor is not null));
        Assert.Single(objectCreations, static objectCreation => objectCreation.Members.Count == 1);
    }

    [Fact]
    public void TargetTypedObjectCreationResolvesAllocatorTakingConstructorOverload()
    {
        var result = Compile(
            """
            module Demo

            struct Allocator {
                i32[0 255] Tag;
            }

            struct List {
                i32[0 max] Capacity;

                List() {
                    self.Capacity = 0;
                }

                List(Allocator allocator) {
                    self.Capacity = allocator.Tag;
                }
            }

            fn List MakeDefault() {
                return new();
            }

            fn List MakeCustom(Allocator allocator) {
                return new(allocator);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var constructors = typeCheckModel.ObjectCreations
            .Where(static objectCreation => objectCreation.CreatedType.DisplayName == "List")
            .Select(static objectCreation => objectCreation.Constructor)
            .ToArray();

        Assert.Contains(constructors, static constructor => constructor is { Parameters.Count: 0 });
        Assert.Contains(constructors, static constructor => constructor is { Parameters.Count: 1 }
            && constructor.Parameters[0].Type.DisplayName == "Allocator");
    }

    [Fact]
    public void TargetTypedObjectCreationRequiresNamedDestinationType()
    {
        var result = Compile(
            """
            module Demo

            fn void MissingTarget() {
                new();
            }

            fn void NonNamedTarget() {
                stack i32[min max] value = new();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("requires an expected named target type", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("requires a named target type", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal));
    }

    private static void AssertIntegerRange(StarkTypeSymbol type, int bitWidth, BigInteger min, BigInteger max)
    {
        Assert.Equal(StarkTypeKind.Integer, type.Kind);
        Assert.Equal(bitWidth, type.BitWidth);
        Assert.Equal((BigInteger?)min, type.RangeMin);
        Assert.Equal((BigInteger?)max, type.RangeMax);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }
}
