using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathPayloadLivenessTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task PayloadLivenessPreservesReadersAliasesReturnedOwnersAndGrowth()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target))
        {
            return;
        }

        var package = await SharedStdlibPackage.GetDirectoryAsync();
        var directory = Directory.CreateTempSubdirectory("stark-path-payload-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var observer = Path.Combine(directory.FullName, "observer.c");
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(observer, """
                #include <stdint.h>
                int32_t inspect_payload(const signed char *data, uint64_t length)
                {
                    const char expected[] = "alpha/beta/gamma.txt";
                    if (length != 20) return 1;
                    for (uint64_t i = 0; i < length; ++i)
                        if (data[i] != expected[i]) return 2;
                    return 0;
                }
                """);
            await File.WriteAllTextAsync(source, """
                import System.IO.Path
                import System.Text
                import System.Memory
                module App

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok: return true;
                        case MemoryStatus.Err(var error): return false;
                    }
                }
                unsafe ffi fn i32[min max] inspect_payload(rawptr<i8[min max]> data, u64[0 max] length);

                noinline unsafe fn bool BytesAreCorrect(mut borrow OwnedAscii output)
                {
                    stack ascii text = output.View();
                    return AsciiLength(text) == 20
                        && inspect_payload(AsciiData(text), 20) == 0;
                }
                noinline unsafe fn bool Fill(mut borrow OwnedAscii output)
                {
                    return Ok(TryJoin(output, "alpha", "beta.txt"))
                        && Ok(TryJoin(output, "alpha/beta", "gamma.txt"));
                }
                noinline unsafe fn u64[0 max] LengthOnly()
                {
                    stack mut OwnedAscii output = new();
                    if (!Ok(output.Reserve(64))) { return 0; }
                    stack mut u64[0 max] sum = 0;
                    for willexit (stack mut u8[0 8] i = 0; i < 8; i += 1)
                    {
                        if (!Fill(output)) { return 0; }
                        sum += output.Length();
                    }
                    return sum;
                }
                noinline unsafe fn bool ReadEachIteration()
                {
                    stack mut OwnedAscii output = new();
                    if (!Ok(output.Reserve(64))) { return false; }
                    for willexit (stack mut u8[0 8] i = 0; i < 8; i += 1)
                    {
                        if (!Fill(output) || !BytesAreCorrect(output)) { return false; }
                    }
                    return true;
                }
                noinline unsafe fn bool ConditionalRead(bool observe)
                {
                    stack mut OwnedAscii output = new();
                    if (!Fill(output)) { return false; }
                    if (observe) { return BytesAreCorrect(output); }
                    return output.Length() == 20;
                }
                noinline unsafe fn OwnedAscii ReturnedOwner()
                {
                    stack mut OwnedAscii output = new();
                    Fill(output);
                    return output;
                }
                noinline unsafe fn bool IndirectReader(fnptr<unsafe fn bool(mut borrow OwnedAscii)> read)
                {
                    stack mut OwnedAscii output = new();
                    if (!Fill(output)) { return false; }
                    return read(output);
                }
                noinline unsafe fn bool GrowAndRead()
                {
                    stack mut OwnedAscii output = new();
                    if (!Ok(output.Reserve(4))) { return false; }
                    if (!Fill(output)) { return false; }
                    return BytesAreCorrect(output);
                }
                noinline unsafe fn bool OverlappingView()
                {
                    stack mut OwnedAscii output = new();
                    if (!Ok(TryJoin(output, "alpha", "beta"))) { return false; }
                    stack ascii sourceView = output.View();
                    if (!Ok(TryJoin(output, sourceView, "gamma.txt"))) { return false; }
                    return BytesAreCorrect(output);
                }
                export unsafe fn i32[min max] main()
                {
                    if (LengthOnly() != 160) { return 1; }
                    if (!ReadEachIteration()) { return 2; }
                    if (!ConditionalRead(true) || !ConditionalRead(false)) { return 3; }
                    stack mut OwnedAscii returned = ReturnedOwner();
                    if (!BytesAreCorrect(returned)) { return 4; }
                    if (!IndirectReader(BytesAreCorrect)) { return 5; }
                    if (!GrowAndRead()) { return 6; }
                    if (!OverlappingView()) { return 7; }
                    return 0;
                }
                """);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [source, "--emit-exe", "-I", package, "-o", executable, "--target", target.Triple, "--native-source", observer],
                new StringReader(string.Empty), stdout, stderr);
            Assert.True(exit == 0, stdout + Environment.NewLine + stderr);
            var execution = await RunProcessWithUtf8StdinAsync(executable, directory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
