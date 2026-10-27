using System.Numerics;
using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemTextWideIntegerParseRuntimeTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task UnicodeWideIntegerParsingPreservesBoundariesAndErrorOrder()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var signedLimit = BigInteger.One << 1023;
        var unsignedLimit = BigInteger.One << 1024;
        var source = """
            import System.Text
            module WideParse
            fn bool Signed(unicode text, i1024[min max] expected) {
                switch (System.Text.ParseI1024Unicode(text)) {
                    case System.Text.TextResult<i1024[min max]>.Ok(var value): return value == expected;
                    case System.Text.TextResult<i1024[min max]>.Err(var error): return false;
                }
            }
            fn bool Unsigned(unicode text, u1024[0 max] expected) {
                switch (System.Text.ParseU1024Unicode(text)) {
                    case System.Text.TextResult<u1024[0 max]>.Ok(var value): return value == expected;
                    case System.Text.TextResult<u1024[0 max]>.Err(var error): return false;
                }
            }
            fn bool SignedError(unicode text, System.Text.TextError expected) {
                switch (System.Text.ParseI1024Unicode(text)) {
                    case System.Text.TextResult<i1024[min max]>.Ok(var value): return false;
                    case System.Text.TextResult<i1024[min max]>.Err(var error): return error == expected;
                }
            }
            fn bool UnsignedError(unicode text, System.Text.TextError expected) {
                switch (System.Text.ParseU1024Unicode(text)) {
                    case System.Text.TextResult<u1024[0 max]>.Ok(var value): return false;
                    case System.Text.TextResult<u1024[0 max]>.Err(var error): return error == expected;
                }
            }
            export fn i32[min max] main() {
                if (!Signed((unicode)"SIGNED_MAX", (i1024[min max])(2 ** 1023 - 1))) { return 1; }
                if (!Signed((unicode)"-SIGNED_LIMIT", (i1024[min max])(-(2 ** 1023)))) { return 2; }
                if (!Unsigned((unicode)"UNSIGNED_MAX", (u1024[0 max])(2 ** 1024 - 1))) { return 3; }
                if (!Signed((unicode)"ZERO_PREFIXSIGNED_MAX", (i1024[min max])(2 ** 1023 - 1))) { return 4; }
                if (!Unsigned((unicode)"ZERO_PREFIXUNSIGNED_MAX", (u1024[0 max])(2 ** 1024 - 1))) { return 5; }
                if (!SignedError((unicode)"SIGNED_LIMIT", System.Text.TextError.Overflow)) { return 6; }
                if (!SignedError((unicode)"-SIGNED_OVERFLOW", System.Text.TextError.Overflow)) { return 7; }
                if (!UnsignedError((unicode)"UNSIGNED_LIMIT", System.Text.TextError.Overflow)) { return 8; }
                if (!SignedError((unicode)"SIGNED_LIMITx", System.Text.TextError.Overflow)) { return 9; }
                if (!UnsignedError((unicode)"xUNSIGNED_LIMIT", System.Text.TextError.InvalidFormat)) { return 10; }
                return 0;
            }
            """.Replace("ZERO_PREFIX", new string('0', 310), StringComparison.Ordinal)
                .Replace("SIGNED_OVERFLOW", (signedLimit + 1).ToString(), StringComparison.Ordinal)
                .Replace("UNSIGNED_LIMIT", unsignedLimit.ToString(), StringComparison.Ordinal)
                .Replace("UNSIGNED_MAX", (unsignedLimit - 1).ToString(), StringComparison.Ordinal)
                .Replace("SIGNED_LIMIT", signedLimit.ToString(), StringComparison.Ordinal)
                .Replace("SIGNED_MAX", (signedLimit - 1).ToString(), StringComparison.Ordinal);
        var directory = Directory.CreateTempSubdirectory("stark-wide-parse-");
        try
        {
            var input = Path.Combine(directory.FullName, "App.stark");
            var output = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(input, source);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var root = Path.Combine(FindRepositoryRoot(), "stdlib", "src");
            var compile = await CompilerCli.RunAsync([input, "--emit-exe", "-I", root, "-o", output, "--target", target.Triple], new StringReader(string.Empty), stdout, stderr);
            Assert.True(compile == 0, stdout + Environment.NewLine + stderr);
            var execution = await RunProcessWithUtf8StdinAsync(output, directory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
        }
        finally { directory.Delete(recursive: true); }
    }
}
