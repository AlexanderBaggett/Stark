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
                uint32_t payload_input(void)
                {
                    static volatile uint32_t next = 37;
                    next = (next * 17 + 3) & 127;
                    return next + 7;
                }
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
                unsafe ffi fn u32[7 max] payload_input();

                noinline unsafe finite void CopyPositive(mut borrow dynamic u32[7 max] values) { init values[1] = values[0]; }
                noinline unsafe finite void CopyNegative(mut borrow dynamic i16[min -7] values) { init values[1] = values[0]; }
                noinline unsafe finite void CopyBoolean(mut borrow dynamic bool values) { init values[1] = values[0]; }
                noinline unsafe finite void CopyFloat(mut borrow dynamic f64 values) { init values[1] = values[0]; }
                noinline unsafe fn u64[0 max] ScalarCopiesNoRead()
                {
                    stack u32[7 max] input = payload_input();
                    stack mut dynamic u32[7 max] positive = new(2);
                    stack mut dynamic i16[min -7] negative = new(2);
                    stack mut dynamic bool flags = new(2);
                    stack mut dynamic f64 floats = new(2);
                    init positive[0] = input;
                    init negative[0] = -19;
                    init flags[0] = true;
                    init floats[0] = (f64)input;
                    CopyPositive(positive);
                    CopyNegative(negative);
                    CopyBoolean(flags);
                    CopyFloat(floats);
                    return (u64[0 max])positive.Length + (u64[0 max])negative.Length
                        + (u64[0 max])flags.Length + (u64[0 max])floats.Length;
                }
                noinline unsafe fn bool ScalarCopiesRead(bool observe)
                {
                    stack u32[7 max] input = payload_input();
                    stack mut dynamic u32[7 max] values = new(2);
                    init values[0] = input;
                    CopyPositive(values);
                    if (observe) { return values[1] == input; }
                    return values.Length == 2;
                }
                noinline unsafe fn bool ScalarCopiesThenUnusedMoves()
                {
                    stack mut dynamic u32[7 max] values = new(2);
                    init values[0] = payload_input();
                    CopyPositive(values);
                    while willexit (values.Length > 0) { stack u32[7 max] unused = values.MoveLast(); }
                    return values.Length == 0;
                }
                noinline unsafe fn u32[7 max] ScalarCopyToExternal(rawmutptr<u32[7 max]> output)
                {
                    stack u32[7 max] input = payload_input();
                    stack mut dynamic u32[7 max] values = new(2);
                    init values[0] = input;
                    CopyPositive(values);
                    *output = values[1];
                    return input;
                }

                struct HeaderOwner
                {
                    dynamic u8[0 max] Bytes;
                    u32[0 max] Marker;
                    HeaderOwner() { self.Bytes = new(); self.Marker = 17; }
                }
                inline unsafe finite void FillHeaderOwner<T>(borrow T tag, mut borrow HeaderOwner owner)
                {
                    init owner.Bytes[0] = 37;
                }
                noinline unsafe fn u64[0 max] ReplaceHeaderOnly()
                {
                    stack mut HeaderOwner owner = new();
                    stack bool tag = true;
                    owner.Bytes = new(8);
                    FillHeaderOwner(tag, owner);
                    owner.Marker = 29;
                    owner.Bytes = new(16);
                    FillHeaderOwner(tag, owner);
                    return (u64[0 max])owner.Bytes.Length + (u64[0 max])owner.Marker;
                }
                noinline unsafe fn bool ReplaceAndRead(bool observe)
                {
                    stack mut HeaderOwner owner = new();
                    stack bool tag = true;
                    owner.Bytes = new(8);
                    FillHeaderOwner(tag, owner);
                    owner.Marker = 29;
                    if (observe && owner.Bytes[0] != 37) { return false; }
                    owner.Bytes = new(16);
                    FillHeaderOwner(tag, owner);
                    return owner.Bytes[0] == 37 && owner.Bytes.Length == 1 && owner.Marker == 29;
                }

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
                    if (ReplaceHeaderOnly() != 30 || !ReplaceAndRead(true) || !ReplaceAndRead(false)) { return 8; }
                    if (ScalarCopiesNoRead() != 8 || !ScalarCopiesRead(true) || !ScalarCopiesRead(false)) { return 9; }
                    stack mut u32[7 max] written = 7;
                    stack u32[7 max] expected = ScalarCopyToExternal(&written);
                    if (expected != written || !ScalarCopiesThenUnusedMoves()) { return 10; }
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
