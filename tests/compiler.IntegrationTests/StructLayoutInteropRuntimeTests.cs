using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class StructLayoutInteropRuntimeTests
{
    [Fact]
    public async Task CStructLayoutAttributesMatchNativeCFixturesAtRuntime()
    {
        if (OperatingSystem.IsWindows() || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var exitCode = await CompileAndRunWithNativeFixtureAsync(
            """
            module Demo

            [StructLayout(C)]
            struct NormalRecord
            {
                u8[0 max] Tag;
                u32[0 max] Length;
                u16[0 max] Code;
            }

            [StructLayout(C), Pack(1), Align(4)]
            struct PackedRecord
            {
                u8[0 max] Tag;
                u32[0 max] Length;
                u16[0 max] Code;
            }

            [StructLayout(Explicit), Align(4)]
            struct WordParts
            {
                [FieldOffset(0)] u32[0 max] Whole;
                [FieldOffset(0)] u16[0 max] Low;
                [FieldOffset(2)] u16[0 max] High;
            }

            unsafe ffi(c) fn i32[min max] stark_check_layout(
                NormalRecord normal,
                PackedRecord packed,
                WordParts word);

            export unsafe fn i32[min max] main()
            {
                stack NormalRecord normal = new NormalRecord()
                {
                    Tag = 17,
                    Length = 287454020,
                    Code = 21862
                };
                stack PackedRecord packed = new PackedRecord()
                {
                    Tag = 34,
                    Length = 1432778632,
                    Code = 30600
                };
                stack WordParts word = new WordParts()
                {
                    Whole = 305419896
                };

                return stark_check_layout(normal, packed, word);
            }
            """,
            """
            #include <stdint.h>
            #include <stddef.h>

            #if !defined(__clang__) && !defined(__GNUC__)
            #error "This fixture uses GNU/Clang packed/aligned attributes."
            #endif

            struct NormalRecord {
                uint8_t Tag;
                uint32_t Length;
                uint16_t Code;
            };

            struct __attribute__((packed, aligned(4))) PackedRecord {
                uint8_t Tag;
                uint32_t Length;
                uint16_t Code;
            };

            struct WordParts {
                uint32_t Whole;
            };

            int stark_check_layout(
                struct NormalRecord normal,
                struct PackedRecord packed,
                struct WordParts word)
            {
                if (sizeof(struct NormalRecord) != 12) return 1;
                if (_Alignof(struct NormalRecord) != 4) return 2;
                if (offsetof(struct NormalRecord, Tag) != 0) return 3;
                if (offsetof(struct NormalRecord, Length) != 4) return 4;
                if (offsetof(struct NormalRecord, Code) != 8) return 5;
                if (normal.Tag != 17) return 6;
                if (normal.Length != 287454020u) return 16;
                if (normal.Code != 21862u) return 26;

                if (sizeof(struct PackedRecord) != 8) return 7;
                if (_Alignof(struct PackedRecord) != 4) return 8;
                if (offsetof(struct PackedRecord, Tag) != 0) return 9;
                if (offsetof(struct PackedRecord, Length) != 1) return 10;
                if (offsetof(struct PackedRecord, Code) != 5) return 11;
                if (packed.Tag != 34 || packed.Length != 1432778632u || packed.Code != 30600u) return 12;

                if (sizeof(struct WordParts) != 4) return 13;
                if (_Alignof(struct WordParts) != 4) return 14;
                if (word.Whole != 305419896u) return 15;

                return 0;
            }
            """,
            targetInfo);

        Assert.Equal(0, exitCode);
    }

    private static async Task<int> CompileAndRunWithNativeFixtureAsync(
        string starkSource,
        string nativeSource,
        LlvmTargetInfo targetInfo)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-struct-layout-interop-");
        var starkPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var nativePath = Path.Combine(tempDirectory.FullName, "LayoutFixture.c");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(starkPath, starkSource);
            await File.WriteAllTextAsync(nativePath, nativeSource);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    starkPath,
                    "--emit-exe",
                    "-o", outputPath,
                    "--native-source", nativePath,
                    "--target", targetInfo.Triple
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, compileExitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
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
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
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
