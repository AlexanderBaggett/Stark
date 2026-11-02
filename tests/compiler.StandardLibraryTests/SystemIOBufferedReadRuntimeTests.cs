using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOBufferedReadRuntimeTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task BufferedReadsPreserveSeekAndEndOfFileBehavior()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-buffered-read-");
        try
        {
            var input = Path.Combine(directory.FullName, "App.stark");
            var output = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "input.txt"), "abcdefghijklmnopqrstuvwxyz");
            await File.WriteAllTextAsync(input, """
                import System.IO
                import System.IO.File
                module BufferedRead
                fn File OpenOrEmpty(System.IO.IOResult<File> result) {
                    switch (result) {
                        case System.IO.IOResult<File>.Ok(var value): return value;
                        case System.IO.IOResult<File>.Err(var error): return new();
                    }
                }
                fn i64[min max] Count(System.IO.IOResult<u64[0 2 ** 63 - 1]> result) {
                    switch (result) {
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Ok(var value): return (i64[min max])value;
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Err(var error): return -1;
                    }
                }
                export unsafe fn i32[min max] main() {
                    stack mut File file = OpenOrEmpty(Open("input.txt", FileMode.Read, FileBuffering.Full));
                    stack mut i8[min max][3] data = { 0, 0, 0 };
                    if (!file.IsOpen() || Count(file.Read(data)) != 3 || data[0] != 97 || data[2] != 99) { return 1; }
                    if (Count(file.Seek((i64[min max])(-(2 ** 63)), SeekOrigin.Current)) != -1) { return 10; }
                    if (Count(file.Seek(0, SeekOrigin.Current)) != 3) { return 2; }
                    if (Count(file.Read(data)) != 3 || data[0] != 100 || data[2] != 102) { return 3; }
                    if (Count(file.Seek(-2, SeekOrigin.Current)) != 4) { return 4; }
                    if (Count(file.Read(data)) != 3 || data[0] != 101 || data[2] != 103) { return 5; }
                    if (Count(file.Seek(-1, SeekOrigin.End)) != 25) { return 6; }
                    if (Count(file.Read(data)) != 1 || data[0] != 122 || Count(file.Read(data)) != 0) { return 7; }
                    if (Count(file.Seek(0, SeekOrigin.Begin)) != 0) { return 8; }
                    if (Count(file.Read(data)) != 3 || data[0] != 97 || data[2] != 99) { return 9; }
                    stack mut File direct = OpenOrEmpty(Open("input.txt", FileMode.Read, FileBuffering.None));
                    if (!direct.IsOpen() || Count(direct.Read(data)) != 3 || data[0] != 97 || data[2] != 99) { return 11; }
                    if (Count(direct.Seek(0, SeekOrigin.Current)) != 3) { return 12; }
                    file.Close();
                    direct.Close();
                    stack mut File readWrite = OpenOrEmpty(Open("input.txt", FileMode.ReadWrite, FileBuffering.Full));
                    if (!readWrite.IsOpen() || Count(readWrite.Read(data)) != 3) { return 13; }
                    data[0] = 120; data[1] = 121; data[2] = 122;
                    if (Count(readWrite.Write(data)) != 3 || Count(readWrite.Seek(0, SeekOrigin.Begin)) != 0) { return 14; }
                    if (Count(readWrite.Read(data)) != 3 || data[0] != 97 || data[2] != 99) { return 15; }
                    if (Count(readWrite.Read(data)) != 3 || data[0] != 120 || data[2] != 122) { return 16; }
                    return 0;
                }
                """);
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
