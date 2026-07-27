using Stark.Compiler;

namespace compiler.Tests;

public sealed class RecursiveInlineLayoutValidationTests
{
    [Fact]
    public void DirectRecursiveStructFieldIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct Node
            {
                Node Next;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3056", "Recursive inline layout", "Node -> Node", "Node.Next", "rawptr<T>");
    }

    [Fact]
    public void MutuallyRecursiveStructFieldsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            struct A
            {
                B Next;
            }

            struct B
            {
                A Prev;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3056", "A -> B -> A", "A.Next", "B.Prev");
    }

    [Fact]
    public void RecursiveEnumPayloadIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum List
            {
                Empty,
                Cons(List),
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3056", "List -> List", "List.Cons#0");
    }

    [Fact]
    public void GenericInlineRecursionAtUseSiteIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            struct Node
            {
                Box<Node> Boxed;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3056", "Node", "Box<Node>", "Node.Boxed", "Box<Node>.Value");
    }

    [Fact]
    public void PointerAndDynamicSelfReferencesAreAllowed()
    {
        var result = Compile(
            """
            module Demo

            struct Node
            {
                rawmutptr<Node> Next;
                dynamic Node Children;
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3056");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "type-check"));
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
