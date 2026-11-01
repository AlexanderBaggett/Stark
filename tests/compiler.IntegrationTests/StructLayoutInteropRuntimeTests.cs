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

            [StructLayout(C)]
            struct Color
            {
                u8[0 max] R;
                u8[0 max] G;
                u8[0 max] B;
                u8[0 max] A;
            }

            unsafe ffi(c) fn i32[min max] stark_check_layout(
                NormalRecord normal,
                PackedRecord packed,
                WordParts word);
            unsafe ffi(c) fn Color stark_roundtrip_color(Color color);

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
                stack Color color = new Color()
                {
                    R = 230,
                    G = 41,
                    B = 55,
                    A = 255
                };
                stack Color returned = stark_roundtrip_color(color);
                if (returned.R != 7) return 31;
                if (returned.G != 11) return 32;
                if (returned.B != 13) return 33;
                if (returned.A != 17) return 34;

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

            struct Color {
                uint8_t R;
                uint8_t G;
                uint8_t B;
                uint8_t A;
            };

            struct Color stark_roundtrip_color(struct Color color)
            {
                struct Color failed = { 0, 0, 0, 0 };
                struct Color succeeded = { 7, 11, 13, 17 };
                if (color.R != 230 || color.G != 41 || color.B != 55 || color.A != 255) return failed;
                return succeeded;
            }

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

    [Fact]
    public async Task NestedAlignedEnumPayloadsMatchNativeOffsetsAndArrayStride()
    {
        if (OperatingSystem.IsWindows() || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)) return;
        var exitCode = await CompileAndRunWithNativeFixtureAsync(
            """
            module NestedLayout
            [StructLayout(C), Align(64)]
            struct Payload { u32[0 max] Number; bool Flag; }
            enum Slot { Free, Occupied(Payload), }
            struct Wrapper { u8[0 max] Marker; Slot Item; u64[0 max] Tail; }
            struct GlobalBox { u8[0 max] Marker; Payload Value; u64[0 max] Tail; }
            static GlobalBox Shared = new GlobalBox()
            {
                Marker = 11,
                Value = new Payload() { Number = 305419896, Flag = true },
                Tail = 72623859790382856
            };
            unsafe ffi(c) fn i32[min max] check_nested(rawptr<u8[0 max]> rows, u64[0 max] stride);
            unsafe ffi(c) fn i32[min max] check_global(rawptr<u8[0 max]> value, u64[0 max] size);
            export unsafe fn i32[min max] main()
            {
                stack mut Wrapper[2] rows;
                rows[0] = new Wrapper() { Marker = Shared.Marker, Item = Slot.Occupied(Shared.Value), Tail = Shared.Tail };
                rows[1] = new Wrapper()
                {
                    Marker = 22,
                    Item = Slot.Occupied(new Payload() { Number = 2271560481, Flag = false }),
                    Tail = 578437695752307201
                };
                stack i32[min max] globalResult = check_global((rawptr<u8[0 max]>)(&Shared), sizeof(GlobalBox));
                if (globalResult != 0) return globalResult;
                return check_nested((rawptr<u8[0 max]>)(&rows[0]), sizeof(Wrapper));
            }
            """,
            """
            #include <stdint.h>
            #include <stddef.h>
            #include <stdbool.h>
            struct __attribute__((aligned(64))) Payload { uint32_t Number; bool Flag; };
            struct Slot { uint8_t Tag; struct Payload Value; };
            struct Wrapper { uint8_t Marker; struct Slot Item; uint64_t Tail; };
            struct GlobalBox { uint8_t Marker; struct Payload Value; uint64_t Tail; };
            int check_global(const void *address, uint64_t size) {
                const struct GlobalBox *value = address;
                if (size != sizeof(struct GlobalBox)) return 7;
                if (value->Marker != 11 || value->Value.Number != 305419896u || !value->Value.Flag) return 8;
                if (value->Tail != UINT64_C(72623859790382856)) return 9;
                return 0;
            }
            int check_nested(const void *address, uint64_t stride) {
                const struct Wrapper *rows = address;
                if (stride != sizeof(struct Wrapper)) return 1;
                if (rows[0].Marker != 11 || rows[1].Marker != 22) return 2;
                if (rows[0].Item.Tag != 1 || rows[1].Item.Tag != 1) return 3;
                if (rows[0].Item.Value.Number != 305419896u || rows[1].Item.Value.Number != 2271560481u) return 4;
                if (!rows[0].Item.Value.Flag || rows[1].Item.Value.Flag) return 5;
                if (rows[0].Tail != UINT64_C(72623859790382856) || rows[1].Tail != UINT64_C(578437695752307201)) return 6;
                return 0;
            }
            """, targetInfo);
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

            Assert.True(compileExitCode == 0, stdout + Environment.NewLine + stderr);
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
