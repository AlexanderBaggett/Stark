using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class MidLevelIrRuntimeTests
{
    [Fact]
    public async Task NonAsciiTextLiteralsPreferAsciiOverloadsAtRuntime()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn i32 Pick(ascii text) {
                return 1;
            }

            fn i32 Pick(unicode text) {
                return 2;
            }

            export ffi fn i32 main() {
                return Pick("caf\u00E9");
            }
            """);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ReassigningEnumDropsOnlyThePreviousActivePayloadAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32 Counter = 0;

            fn void Bump(i32 value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32 Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Token {
                End,
                Text(Resource),
            }

            export ffi fn i32 main() {
                stack mut Token token = Token.Text(new Resource() { Value = 1 });
                token = Token.End;
                return Counter;
            }
            """);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task MutableBorrowReceiverCallsObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Counter {
                i32 Value;

                fn void Reset(borrow mut Counter self) {
                    self.Value = 0;
                    return;
                }

                fn void ResetThenAdd(borrow mut Counter self, i32 value) {
                    self.Reset();
                    self.Value += value;
                    return;
                }

                fn i32 Current(borrow Counter self) {
                    return self.Value;
                }
            }

            export ffi fn i32 main() {
                stack mut Counter counter = new Counter() { Value = 9 };
                counter.ResetThenAdd(7);
                return counter.Current();
            }
            """);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task SwitchExpressionCallOnEnumIsEvaluatedOnceAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32 Counter = 0;

            enum Status {
                Ok,
                Err(i32),
            }

            fn Status Next() {
                Counter += 1;
                return Status.Ok;
            }

            export ffi fn i32 main() {
                switch (Next()) {
                    case Status.Ok:
                        return Counter;
                    case Status.Err(var error):
                        return error;
                }
            }
            """);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RawPointerIndexedFieldAddressesObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Buffer {
                i8[16] Storage;
                i64 WritePos;
            }

            fn void Put(rawmutptr<Buffer> buffer, i64 index, i8 value) {
                *(&(*buffer).Storage[index]) = value;
                return;
            }

            fn i32 Read(rawmutptr<Buffer> buffer, i64 index) {
                return (i32)*(&(*buffer).Storage[index]);
            }

            export ffi fn i32 main() {
                stack mut Buffer buffer = new Buffer();
                Put(&buffer, 3, (i8)90);
                return Read(&buffer, 3);
            }
            """);

        Assert.Equal(90, exitCode);
    }

    [Fact]
    public async Task RawPointerIndexedElementsObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn void Put(rawmutptr<i8> data, i64 index, i8 value) {
                *(&data[index]) = value;
                return;
            }

            fn i32 Read(rawmutptr<i8> data, i64 index) {
                return (i32)*(&data[index]);
            }

            export ffi fn i32 main() {
                stack mut i8[16] buffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                Put(&buffer[0], 4, (i8)77);
                return Read(&buffer[0], 4);
            }
            """);

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task BorrowReceiverIndexedFieldAddressesObserveSharedStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            struct Buffer {
                i8[16] Storage;

                fn void Put(borrow mut Buffer self, i64 index, i8 value) {
                    *(&self.Storage[index]) = value;
                    return;
                }

                fn i32 Read(borrow Buffer self, i64 index) {
                    return (i32)*(&self.Storage[index]);
                }
            }

            export ffi fn i32 main() {
                stack mut Buffer buffer = new Buffer();
                buffer.Put(5, (i8)65);
                return buffer.Read(5);
            }
            """);

        Assert.Equal(65, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-mir-enum-drop-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-exe", "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var standardOutput = await process!.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(string.Empty, standardOutput);
            Assert.Equal(string.Empty, standardError);
            return process.ExitCode;
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
