using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class NativeStringDependencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxExplicitStrlenRemainsCallable(bool package)
    {
        if (!OperatingSystem.IsLinux() || !NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-explicit-strlen-");
        try
        {
            var library = Path.Combine(directory.FullName, "StringProbe.stark");
            await File.WriteAllTextAsync(library, """
                module StringProbe
                unsafe ffi(c) fn rawptr<i8[min max]> ProbeInput();
                unsafe ffi(c) fn u64[0 max] strlen(rawptr<i8[min max]> value);
                public unsafe noinline fn u64[0 max] Length() { return strlen(ProbeInput()); }
                """);
            var native = Path.Combine(directory.FullName, "input.c");
            await File.WriteAllTextAsync(native, "const char *ProbeInput(void) { return \"runtime bytes\"; }");
            if (package)
            {
                await CompileAsync([library, "--emit-lib", "--no-stark-path", "--target", target.Triple,
                    "-o", Path.Combine(directory.FullName, "libStringProbe.a")]);
                File.Delete(library);
            }
            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, """
                import StringProbe
                module Consumer
                export unsafe fn i32[min max] main()
                {
                    if (Length() != 13) { return 1; }
                    return 0;
                }
                """);
            var executable = Path.Combine(directory.FullName, "app");
            await CompileAsync([app, "--emit-exe", "--no-stark-path", "--target", target.Triple,
                "-I", directory.FullName, "--native-source", native, "-o", executable]);
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"{await output}{await error}");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task CompileAsync(string[] arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await CompilerCli.RunAsync(arguments, new StringReader(""), output, error);
        Assert.True(result == 0, $"{output}{Environment.NewLine}{error}");
    }
}
