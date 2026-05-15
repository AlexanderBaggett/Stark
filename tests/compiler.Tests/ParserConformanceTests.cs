using Stark.Parsing;

namespace compiler.Tests;

public sealed class ParserConformanceTests
{
    public static TheoryData<string, string> ValidPrograms => new()
    {
        {
            "top level imports module and ordinary parameters",
            """
            import Core.Math
            export import Core.Text
            module Demo.Api

            public fn i32[min max] Sum(i32[min max] left, i32[min max] right,) {
                return left + right;
            }
            """
        },
        {
            "callable values unsafe blocks and explicit lambda captures",
            """
            module Demo

            unsafe fn void Danger();
            fn void RegisterZero(fnptr<fn u32[0 2 ** 31 - 1]()> callback);
            fn void RegisterOne(fnptr<fn u32[0 2 ** 31 - 1](u32[0 2 ** 31 - 1])> callback);

            fn void Run(u32[0 2 ** 31 - 1] scale, u32[0 2 ** 31 - 1] token, u32[0 2 ** 31 - 1] sharedState) {
                stack fnptr<fn u32[0 2 ** 31 - 1](u32[0 2 ** 31 - 1])> callback = Transform;
                RegisterZero(() => 0);
                RegisterOne(capture(copy scale, read token, move sharedState) (u32[0 2 ** 31 - 1] index) => {
                    return index;
                });

                unsafe {
                    Danger();
                    RegisterZero(capture(unsafe addr token, unsafe shared sharedState) () => 0);
                }
            }

            fn u32[0 2 ** 31 - 1] Transform(u32[0 2 ** 31 - 1] value) {
                return value;
            }
            """
        },
        {
            "function pointer memory contracts parse",
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn void RegisterOverlap(fnptr<fn void(borrow mut Box, borrow mut Box) where overlap(arg0, arg1)> callback);
            fn void RegisterSame(fnptr<fn void(borrow mut Box, borrow mut Box) where same(arg0, arg1)> callback);
            fn void RegisterFinite(fnptr<finite i32[min max](i32[min max])> callback);
            fn void RegisterLaw(fnptr<law bool(borrow Box)> callback);
            fn void RegisterFiniteLaw(fnptr<finite law i32[min max](i32[min max])> callback);
            """
        },
        {
            "closure type forms parse for inline borrow and heap callbacks",
            """
            module Demo

            struct Ui {
                i32[min max] Frame;
            }

            struct Packet {
                i32[min max] Code;
            }

            struct Button {
                heap closure<fn void()> OnClick;
                heap closure<mut fn void(i32[min max])> OnValue;
            }

            inline fn void Horizontal(
                borrow mut Ui ui,
                inline closure<fn void(borrow mut Ui)> body) {
                body(ui);
                return;
            }

            fn void WithPanel(
                borrow mut Ui ui,
                borrow closure<fn void(borrow mut Ui)> body) {
                body(ui);
                return;
            }

            fn void PushEvent(
                mut borrow closure<mut fn void(i32[min max])> sink,
                i32[min max] value) {
                sink(value);
                return;
            }

            fn heap closure<fn i32[min max](i32[min max])> MakeAdder(i32[min max] offset) {
                return heap capture(copy offset) (i32[min max] value) => value + offset;
            }

            fn Packet BuildWith(inline closure<once fn Packet()> producer) {
                return producer();
            }

            fn void RegisterOverlap(
                borrow closure<fn void(borrow mut Ui, borrow mut Ui) where overlap(arg0, arg1)> body);
            """
        },
        {
            "constant interpolated text literal",
            """
            module Demo

            finite law ascii Label() {
                return $"Score: {100}";
            }
            """
        },
        {
            "fixed-capacity stack text concatenation declaration",
            """
            module Demo

            fn void Join(Ascii left, Ascii right, Unicode wideLeft, Unicode wideRight) {
                stack Ascii combined[4096] = left + right;
                stack Unicode wideCombined[4096] = wideLeft + wideRight;
            }
            """
        },
        {
            "ffi varargs function declaration",
            """
            module Native

            public ffi varargs fn i32[min max] printf(ascii format);
            """
        },
        {
            "record members support fields methods constructors and generics",
            """
            module Models

            record Pair<T>(T Left, T Right) {
                mut i32[min max] Count;

                Pair(T left, T right) {
                    ;
                }

                fn i32[min max] Width() {
                    return 2;
                }

                internal fn i32[min max] HiddenWidth() {
                    return 2;
                }

                static finite law Pair<T> Empty(T value) {
                    return new Pair<T>(value, value);
                }

                drop {
                    ;
                }
            }

            struct Buffer {
                rawptr<i8[min max]> Ptr;

                mut drop {
                    self.Ptr = null;
                }
            }
            """
        },
        {
            "alias declarations parse with visibility and generics",
            """
            module Types

            public alias Byte = i8[min max];
            internal alias BufferView<T> = borrow T[];
            """
        },
        {
            "traits and doctrines accept constraints and semicolon bodies",
            """
            module Contracts

            trait Parser<T> {
                law T Parse(ascii text) where T: Node, Printable;
            }

            doctrine Numbers<T> {
                finite law T Clamp(T value, T min, T max) where T: Ordered;
            }
            """
        },
        {
            "complex nested type syntax parses",
            """
            module Types

            unsafe fn void Accept(
                borrow rawptr<i8[min max]>[] buffers,
                shared Matrix<Vector<i32[min max][4]>> table,
                out i32[min max][4][2] lanes,
                frozen u8[0 max][] levels)
            {
                return;
            }
            """
        },
        {
            "all loop behaviors and empty statements parse",
            """
            module Flow

            fn void Run(bool flag) {
                ;

                while infinite (flag) ;

                while non-deterministic (flag) {
                    break;
                }

                for non-deterministic (; flag; ) ;

                for willexit (stack mut i32[min max] i = 0; i < 4; i += 1, i += 2) {
                    continue;
                }
            }
            """
        },
        {
            "memory separation contracts parse",
            """
            module Memory

            unsafe fn void Copy(
                rawptr<i32[min max]> source,
                rawmutptr<i32[min max]> destination)
                where disjoint(source[0, 4], destination[0, 4])
                where overlap(source, destination) {
                if disjoint(source, destination) {
                    return;
                }

                assume disjoint(source, destination) {
                    return;
                }

                while willexit independent (false) {
                    break;
                }

                for willexit independent (stack mut u32[0 2 ** 31 - 1] index = 0; index < 4; index += 1) {
                    continue;
                }
            }

            unsafe fn void SameMemory(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                where same(left, right) {
                return;
            }
            """
        },
        {
            "switch sections may contain multiple labels and guards",
            """
            module Branching

            fn i32[min max] Pick(i32[min max] state) {
                switch w99 (state) {
                    case 0:
                    case 1:
                        return 1;
                    case var value when value > 10:
                        return value;
                    default:
                        return 0;
                }
            }
            """
        },
        {
            "postfix chains and trailing comma argument lists parse",
            """
            module Access

            struct Leaf {
                i32[min max] Value;
            }

            struct Node {
                Leaf[2] Leaves;
            }

            fn Node GetNode(i32[min max] index, i32[min max] count,) {
                return new Node();
            }

            fn i32[min max] Read() {
                return GetNode(1, 2,).Leaves[0].Value;
            }
            """
        },
        {
            "target typed object creation parses",
            """
            module AllocationSyntax

            struct Box {
                i32[min max] Value;

                Box(i32[min max] value) {
                    ;
                }
            }

            fn Box Make(i32[min max] value) {
                stack Box empty = new();
                stack Box constructed = new(value);
                stack Box initialized = new() { Value = value };
                return new(value);
            }
            """
        },
        {
            "assignment precedence and unary operators parse",
            """
            module Operators

            fn i32[min max] Compute(i32[min max] left, i32[min max] middle, i32[min max] right, bool flag) {
                stack mut i32[min max] value = left + middle * right << 1;
                value &= left ^ middle | right;
                value = flag ? value : left;
                return ~value;
            }
            """
        },
        {
            "qualified names may be used directly as expressions",
            """
            import Core.Math
            module Demo

            fn i32[min max] Read() {
                return Core.Math.Constants.Value;
            }
            """
        },
        {
            "object and array initializers allow trailing commas",
            """
            module Init

            struct Item {
                i32[min max] Value;
            }

            fn void Run() {
                stack Item item = new Item() { Value = 1, };
                stack i32[min max][3] numbers = { 1, 2, 3, };
            }
            """
        },
        {
            "ffi raw pointer nesting parses",
            """
            module Interop

            export unsafe ffi fn rawptr<rawmutptr<i8[min max]>> Transform(rawptr<rawptr<i8[min max]>> input);
            """
        },
        {
            "negative range constraints combine with fixed arrays",
            """
            module Ranges

            fn void Accept(i32[-10 10][4] buckets) {
                return;
            }
            """
        },
        {
            "type relative integer range endpoints parse",
            """
            module Ranges

            fn void Accept(
                i32[min max] signedValue,
                u64[0 2 ** 63 - 1] nonNegative,
                u8[min 127] bytePrefix)
            {
                return;
            }
            """
        },
        {
            "constant arithmetic integer range endpoints parse",
            """
            module Ranges

            fn void Accept(
                u64[10 ** 2 10 ** 10] decimalPowers,
                u24[2 ** 4 2 ** 16] binaryPowers,
                u32[1024 * 1024 1024 * 1024 * 1024] sizes,
                i32[(1 + 2) * 3 20 / 2 + 1] parenthesized)
            {
                return;
            }
            """
        },
        {
            "all supported integer and floating point widths parse in source types",
            """
            module Scalars

            fn void Accept(
                i8[min max] a,
                i16[min max] b,
                i24[min max] c,
                i32[min max] d,
                i48[min max] e,
                i64[min max] f,
                i96[-1 1] g,
                i128[-1 1] h,
                i192[-1 1] i,
                i256[-1 1] j,
                i384[-1 1] k,
                i512[-1 1] l,
                i768[-1 1] m,
                i1024[-1 1] n,
                u8[0 max] ua,
                u16[0 max] ub,
                u24[0 max] uc,
                u32[0 max] ud,
                u48[0 max] ue,
                u64[0 max] uf,
                u96[0 max] ug,
                u128[0 max] uh,
                u192[0 max] ui,
                u256[0 max] uj,
                u384[0 max] uk,
                u512[0 max] ul,
                u768[0 max] um,
                u1024[0 max] un,
                f16 half,
                f32 single,
                f64 doubleValue,
                f80 extended,
                f128 quad)
            {
                return;
            }
            """
        }
    };

    public static TheoryData<string, string> InvalidPrograms => new()
    {
        {
            "wildcard imports are not part of the grammar",
            """
            import Core.*
            module Demo
            """
        },
        {
            "top level imports and module declarations do not end in semicolons",
            """
            import Core.Math;
            module Demo;
            """
        },
        {
            "function parameters do not support default values",
            """
            module Demo

            fn i32[min max] Sum(i32[min max] left = 1, i32[min max] right) {
                return left + right;
            }
            """
        },
        {
            "alias declarations require an equals sign and target type",
            """
            module Demo

            alias Bytes;
            """
        },
        {
            "bare integer widths are not valid source types",
            """
            module Demo

            fn i32 Run(i32 value) {
                return value;
            }
            """
        },
        {
            "function pointer kinds use finite law order",
            """
            module Demo

            fn void Register(fnptr<law finite i32[min max]()> callback);
            """
        },
        {
            "unsupported numeric width spellings are not valid builtin types",
            """
            module Demo

            fn void Run(i40[0 1] count, f256 amount) {
                return;
            }
            """
        },
        {
            "type is no longer the alias declaration keyword",
            """
            module Demo

            type Bytes = i8[min max];
            """
        },
        {
            "doctrine members may not use fn",
            """
            module Demo

            doctrine Numbers {
                fn i32[min max] Identity(i32[min max] value);
            }
            """
        },
        {
            "var patterns require a capture identifier",
            """
            module Demo

            fn i32[min max] Run(i32[min max] value) {
                switch (value) {
                    case var:
                        return 1;
                    default:
                        return 0;
                }
            }
            """
        },
        {
            "where clauses require at least one constrained type",
            """
            module Demo

            fn T Echo<T>(T value) where T: {
                return value;
            }
            """
        },
        {
            "for headers still require both semicolons",
            """
            module Demo

            fn void Run(bool flag) {
                for willexit (stack i32[min max] i = 0) {
                    ;
                }
            }
            """
        },
        {
            "legacy loop behavior alias spelling is no longer accepted",
            """
            module Demo

            fn void Run(bool flag) {
                for nondeterministic (; flag; ) {
                    ;
                }
            }
            """
        },
        {
            "object initializers require an expression on the right side",
            """
            module Demo

            struct Item {
                i32[min max] Value;
            }

            fn void Run() {
                stack Item item = new Item() { Value = };
            }
            """
        },
        {
            "raw pointer types require a closing generic bracket",
            """
            module Demo

            unsafe fn void Run(rawptr<i32[min max] value) {
                return;
            }
            """
        },
        {
            "top level declarations accept only one visibility modifier",
            """
            module Demo

            public export fn void Run();
            """
        },
        {
            "loop behavior must use the supported token forms",
            """
            module Demo

            fn void Run(bool flag) {
                while non deterministic (flag) {
                    break;
                }
            }
            """
        },
        {
            "argument lists still require comma separators",
            """
            module Demo

            fn void Write(i32[min max] left, i32[min max] right) {
                return;
            }

            fn void Run() {
                Write(1 2);
            }
            """
        }
    };

    [Theory]
    [MemberData(nameof(ValidPrograms))]
    public void ValidProgramsParse(string scenario, string source)
    {
        var result = StarkSyntax.ParseCompilationUnit(source);

        Assert.True(
            result.Succeeded,
            $"{scenario} should parse successfully, but reported {result.SyntaxErrorCount} syntax error(s).");
        Assert.Equal(0, result.SyntaxErrorCount);
    }

    [Theory]
    [MemberData(nameof(InvalidPrograms))]
    public void InvalidProgramsDoNotParse(string scenario, string source)
    {
        var result = StarkSyntax.ParseCompilationUnit(source);

        Assert.False(
            result.Succeeded,
            $"{scenario} should fail to parse, but the parser reported success.");
        Assert.True(
            result.SyntaxErrorCount > 0,
            $"{scenario} should report at least one syntax error.");
    }
}
