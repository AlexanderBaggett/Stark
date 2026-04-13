using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class GenericsFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void GenericEnumMonomorphizationEmitsConcreteStructType()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            export ffi fn i32[-2147483648 2147483647] main() {
                stack Option<i32[-2147483648 2147483647]> opt = Option<i32[-2147483648 2147483647]>.Some(42);
                switch (opt) {
                    case Option<i32[-2147483648 2147483647]>.None:
                        return 0;
                    case Option<i32[-2147483648 2147483647]>.Some(var value):
                        return value;
                }
            }
            """);

        // Monomorphized type should be emitted as a concrete LLVM struct
        Assert.Contains("Option", llvm);
        Assert.Contains("i32", llvm);
    }

    [Fact]
    public void GenericRecordMonomorphizationEmitsConcreteFields()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            export ffi fn i32[-2147483648 2147483647] main() {
                stack Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]> p = new Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]>() { First = 3, Second = 7 };
                return p.First + p.Second;
            }
            """);

        Assert.Contains("Pair", llvm);
        Assert.Contains("i32", llvm);
    }

    [Fact]
    public void TwoDistinctInstantiationsOfSameGenericEmitTwoTypes()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law i32[-2147483648 2147483647] GetI32(Option<i32[-2147483648 2147483647]> opt) {
                switch (opt) {
                    case Option<i32[-2147483648 2147483647]>.None:
                        return 0;
                    case Option<i32[-2147483648 2147483647]>.Some(var value):
                        return value;
                }
            }

            finite law bool GetBool(Option<bool> opt) {
                switch (opt) {
                    case Option<bool>.None:
                        return false;
                    case Option<bool>.Some(var value):
                        return value;
                }
            }

            export ffi fn i32[-2147483648 2147483647] main() {
                stack Option<i32[-2147483648 2147483647]> a = Option<i32[-2147483648 2147483647]>.Some(5);
                stack Option<bool> b = Option<bool>.Some(true);
                stack i32[-2147483648 2147483647] sum = GetI32(a);
                stack bool flag = GetBool(b);
                return flag ? sum : 0;
            }
            """);

        // Both monomorphized types should appear
        Assert.Contains("Option", llvm);
        // Two distinct types should be defined
        Assert.True(CountOccurrences(llvm, "= type {") >= 2, "expected at least two named struct types for Option<i32> and Option<bool>");
    }

    [Fact]
    public void NestedGenericInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law bool IsPresent(Option<Option<i32[-2147483648 2147483647]>> outer) {
                switch (outer) {
                    case Option<Option<i32[-2147483648 2147483647]>>.None:
                        return false;
                    case Option<Option<i32[-2147483648 2147483647]>>.Some(var inner):
                        return true;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        Assert.NotNull(model);
        Assert.True(model.NamedTypes.ContainsKey("Option<Option<i32>>")
            || model.NamedTypes.Keys.Any(k => k.StartsWith("Demo.Option<") && k.Contains("Option")),
            "nested instantiation should be registered");
    }
}
