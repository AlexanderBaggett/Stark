using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void AcceptedBoundOperationsDoNotProduceNullMirArtifacts()
    {
        const string source = """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public unsafe finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

            enum Status {
                Ok,
                Err(i32[min max]),
            }

            struct Box {
                i32[min max] Value;

                fn i32[min max] Get(borrow Box self) {
                    return self.Value;
                }
            }

            fn i32[min max] Inc(i32[min max] value) {
                return value + 1;
            }

            unsafe fn i32[min max] Apply(fnptr<fn i32[min max](i32[min max])> op, i32[min max] value) {
                return op(value);
            }

            fn i32[min max] Score(Status status) {
                switch (status) {
                    case Status.Ok:
                        return 1;
                    case Status.Err(var error):
                        return error;
                }
            }

            unsafe fn i32[min max] Run(bool flag) {
                stack mut Box box = new Box() { Value = 3 };
                stack mut i32[min max][2] values = { 4, 5 };
                stack fnptr<fn i32[min max](i32[min max])> lambda = (i32[min max] value) => Inc(value);
                stack closure<finite law i32[min max](i32[min max])> closureOp =
                    (i32[min max] value) => value + 6;
                stack mut dynamic u32[0 max] items = new(1);
                stack i64[min max] boxSize = sizeof(Box);
                stack Ascii label[8] = $"ok";
                stack Ascii joined[12] = label + "!";
                values[0] = Inc(box.Get());
                items.Reserve(1);
                if (flag) {
                    return values[0] + Apply(Inc, 2) + Apply(lambda, 3) + closureOp(6) + Score(Status.Err(4));
                }

                return values[1] + Score(Status.Ok);
            }
            """;

        var result = Compile(source, new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        FallbackLogAssertions.AssertNoFallbackLogs(result, "accepted bound-operation MIR lowering");
        var mir = GetMir(result);
        AssertMirHasNoNullLoweringArtifacts(mir);
        Assert.All(
            mir.Functions.Where(static function => function.HasBody),
            static function => Assert.True(function.SupportsDirectCodeGeneration, $"{function.Name} should lower directly."));
    }
}
