using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class DynTraitObjectRuntimeTests
{
    [Fact]
    public async Task BorrowDynTraitObjectDispatchesPolymorphicallyAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Two distinct concrete types stand behind the same `borrow dyn Shape`, and
        // the dispatch happens through a function parameter so it cannot be folded to
        // a constant. The result (4*4 + 3*5 = 31) proves the right method runs for
        // each concrete value via the vtable.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            dyn trait Shape
            {
                finite law i32[min max] Area(borrow Self self);
            }

            struct Square : Shape
            {
                i32[min max] Side;

                finite law i32[min max] Area(borrow Square self)
                {
                    return self.Side * self.Side;
                }
            }

            struct Box : Shape
            {
                i32[min max] Width;
                i32[min max] Height;

                finite law i32[min max] Area(borrow Box self)
                {
                    return self.Width * self.Height;
                }
            }

            finite law i32[min max] AreaOf(borrow dyn Shape shape)
            {
                return shape.Area();
            }

            export fn i32[min max] main()
            {
                stack Square square = new Square() { Side = 4 };
                stack Box box = new Box() { Width = 3, Height = 5 };
                stack borrow dyn Shape first = square;
                stack borrow dyn Shape second = box;
                return AreaOf(first) + AreaOf(second);
            }
            """);

        Assert.Equal(31, exitCode);
    }

    [Fact]
    public async Task OwnedHeapDynTraitObjectAllocatesDispatchesAndDrops()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Each `heap dyn Speaker` boxes a fresh value on the heap, owns it, dispatches
        // dynamically, and at scope exit drops the box through the vtable Drop slot
        // (running the type's drop and freeing the box). Two distinct concrete types
        // exercise two vtables and two drop thunks. Result 4 + 5 = 9.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            struct Dog : Speaker
            {
                i32[min max] Volume;

                finite law i32[min max] Speak(borrow Dog self)
                {
                    return self.Volume;
                }
            }

            struct Cat : Speaker
            {
                i32[min max] Pitch;

                finite law i32[min max] Speak(borrow Cat self)
                {
                    return self.Pitch;
                }
            }

            export fn i32[min max] main()
            {
                stack heap dyn Speaker first = new Dog() { Volume = 4 };
                stack heap dyn Speaker second = new Cat() { Pitch = 5 };
                return first.Speak() + second.Speak();
            }
            """);

        Assert.Equal(9, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-dyn-trait-runtime-");
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
            await process.WaitForExitAsync();

            var processOutput = await process.StandardOutput.ReadToEndAsync();
            var processError = await process.StandardError.ReadToEndAsync();
            Assert.Equal(string.Empty, processOutput);
            Assert.Equal(string.Empty, processError);
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
