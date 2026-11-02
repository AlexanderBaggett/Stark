using System.Diagnostics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageStructuralFactExpressionTests
{
    [Fact]
    public async Task NestedStructuralFactsEvaluateAfterSourceFreeGenericInstantiation()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-compound-facts-");
        try
        {
            var source = Path.Combine(directory.FullName, "Facts.stark");
            var archive = Path.Combine(directory.FullName, "libFacts.a");
            await File.WriteAllTextAsync(source, """
                module Facts
                public struct Owner { public dynamic u8[0 max] Bytes; }
                public finite law bool Both<A, B>()
                {
                    return comptime (System.Compiler.IsCopyable<A>() && System.Compiler.IsCopyable<B>());
                }
                public finite law bool Either<A, B>()
                {
                    return comptime (System.Compiler.IsCopyable<A>() || System.Compiler.IsCopyable<B>());
                }
                public finite law u64[0 max] WidthSquared<T>()
                {
                    return comptime (System.Compiler.TypeIntegerBitWidth<T>() ** 2);
                }
                public finite law bool Neither<A, B>()
                {
                    return comptime (!(System.Compiler.IsCopyable<A>() || System.Compiler.IsCopyable<B>()));
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [source, "--emit-lib", "--target", target.Triple, "-o", archive],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            File.Delete(source);

            var app = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(app, """
                import Facts
                module App
                export finite i32[min max] main()
                {
                    if (!Both<u32[0 max], u64[0 max]>()) { return 1; }
                    if (Both<Owner, u32[0 max]>()) { return 2; }
                    if (Both<u32[0 max], Owner>()) { return 3; }
                    if (Both<Owner, Owner>()) { return 4; }
                    if (!Either<Owner, u32[0 max]>()) { return 5; }
                    if (!Either<u32[0 max], Owner>()) { return 6; }
                    if (Either<Owner, Owner>()) { return 7; }
                    if (!Neither<Owner, Owner>()) { return 8; }
                    if (Neither<Owner, u32[0 max]>()) { return 9; }
                    if (WidthSquared<u8[0 max]>() != 64) { return 10; }
                    if (WidthSquared<u32[0 max]>() != 1024) { return 11; }
                    return 0;
                }
                """);
            output.GetStringBuilder().Clear();
            errors.GetStringBuilder().Clear();
            exit = await CompilerCli.RunAsync(
                [app, "--emit-exe", "--target", target.Triple, "-I", directory.FullName, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) { UseShellExecute = false });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
