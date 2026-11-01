using System.Diagnostics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class AsciiToUnicodeRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicConversionPreservesBytesBoundsAndDecoderFallback(bool sourceFreePackage)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-ascii-unicode-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var witness = Path.Combine(directory.FullName, "witness.c");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(source, """
                module System.Text
                public finite law ascii AsciiView(Ascii source);
                public unsafe inline finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

                export unsafe ffi finite i32[min max] Convert(
                    rawmutptr<Unicode> destination, rawmutptr<i8[min max]> data, u64[0 2 ** 63 - 1] length)
                {
                    stack Ascii owner = new Ascii() { Data = data, Length = length, Capacity = length };
                    if (TryConvertAsciiToUnicode(destination, AsciiView(owner))) { return 1; }
                    return 0;
                }

                unsafe ffi fn i32[min max] RunChecks();
                export unsafe ffi fn i32[min max] main() { return RunChecks(); }
                """);
            await File.WriteAllTextAsync(witness, """
                #define _DEFAULT_SOURCE
                #include <stdint.h>
                #include <stddef.h>
                #include <string.h>
                #if defined(__unix__) || defined(__APPLE__)
                #include <sys/mman.h>
                #include <unistd.h>
                #endif

                typedef struct { int32_t *data; int64_t length, capacity; } Unicode;
                extern int32_t Convert(Unicode *, unsigned char *, uint64_t);

                /* Independent scalar oracle: invalid sequences consume one byte. */
                static size_t decode(const unsigned char *s, size_t n, int32_t *out) {
                    size_t i = 0, written = 0;
                    while (i < n) {
                        uint32_t first = s[i], value = 0xfffd;
                        size_t width = 1, candidate = 0;
                        if (first < 128) value = first;
                        else if (first >= 194 && first <= 223) candidate = 2;
                        else if (first >= 224 && first <= 239) candidate = 3;
                        else if (first >= 240 && first <= 244) candidate = 4;
                        if (candidate && candidate <= n - i) {
                            uint32_t v = first & (127u >> candidate);
                            size_t j = 1;
                            for (; j < candidate; ++j) {
                                if ((s[i + j] & 192) != 128) break;
                                v = (v << 6) | (s[i + j] & 63);
                            }
                            uint32_t minimum = candidate == 2 ? 128 : candidate == 3 ? 2048 : 65536;
                            if (j == candidate && v >= minimum && v <= 0x10ffff && (v < 0xd800 || v > 0xdfff)) {
                                value = v; width = candidate;
                            }
                        }
                        out[written++] = (int32_t)value;
                        i += width;
                    }
                    return written;
                }

                static int check(unsigned char *source, size_t n, size_t capacity) {
                    int32_t expected[96], storage[100];
                    for (size_t i = 0; i < 100; ++i) storage[i] = 0x55555555;
                    size_t count = decode(source, n, expected);
                    Unicode destination = { storage + 1, 71, (int64_t)capacity };
                    int success = Convert(&destination, source, n);
                    if (success != (count <= capacity) || destination.length != (success ? (int64_t)count : 0)) return 1;
                    size_t written = count < capacity ? count : capacity;
                    if (storage[0] != 0x55555555) return 2;
                    for (size_t i = 0; i < written; ++i) if (storage[i + 1] != expected[i]) return 3;
                    for (size_t i = written + 1; i < 100; ++i) if (storage[i] != 0x55555555) return 4;
                    return 0;
                }

                int32_t RunChecks(void) {
                    unsigned char bytes[96];
                    for (size_t offset = 0; offset < 8; ++offset) {
                        unsigned char *source = bytes + offset;
                        for (size_t n = 0; n <= 73; ++n) {
                            for (size_t i = 0; i < n; ++i) source[i] = (unsigned char)((i * 13 + n) % 128);
                            if (check(source, n, n)) return 1;
                        }
                    }
                    const unsigned char sequences[][4] = {
                        {194, 162}, {226, 130, 172}, {240, 159, 152, 128},
                        {128}, {192, 175}, {237, 160, 128}, {244, 144, 128, 128}, {226, 130}
                    };
                    const size_t widths[] = {2, 3, 4, 1, 2, 3, 4, 2};
                    for (size_t position = 0; position < 32; ++position) {
                        for (size_t sequence = 0; sequence < 8; ++sequence) {
                            memset(bytes, 'A', sizeof(bytes));
                            memcpy(bytes + position, sequences[sequence], widths[sequence]);
                            size_t n = position + widths[sequence];
                            int32_t expected[96];
                            size_t count = decode(bytes, n, expected);
                            if (check(bytes, n, n) || check(bytes, n, count) || check(bytes, n, count - 1)) return 2;
                        }
                    }
                    int32_t output[16];
                    Unicode destination = { output, 71, 16 };
                    if (Convert(NULL, bytes, 8)) return 3;
                    if (Convert(&destination, NULL, 8) || destination.length) return 4;
                    destination.data = NULL; destination.length = 71;
                    if (Convert(&destination, bytes, 8) || destination.length) return 5;
                    if (!Convert(&destination, NULL, 0) || destination.length) return 6;
                    destination.data = output; destination.capacity = -1; destination.length = 71;
                    if (Convert(&destination, bytes, 8) || destination.length) return 7;
                    memset(output, 65, sizeof(output));
                    destination.capacity = 16; destination.length = 71;
                    if (Convert(&destination, (unsigned char *)output, 8) || destination.length) return 8;
                    for (size_t i = 0; i < sizeof(output); ++i) if (((unsigned char *)output)[i] != 65) return 9;
                    #if defined(__unix__) || defined(__APPLE__)
                    size_t page = (size_t)sysconf(_SC_PAGESIZE);
                    unsigned char *input = mmap(NULL, page * 2, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
                    unsigned char *result = mmap(NULL, page * 2, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
                    if (input == MAP_FAILED || result == MAP_FAILED) return 10;
                    if (mprotect(input + page, page, PROT_NONE) || mprotect(result + page, page, PROT_NONE)) return 11;
                    for (size_t n = 0; n <= 73; ++n) {
                        unsigned char *source = input + page - n;
                        int32_t *data = (int32_t *)(result + page) - n;
                        for (size_t i = 0; i < n; ++i) source[i] = (unsigned char)((i * 13 + n) % 128);
                        Unicode guarded = { data, 71, (int64_t)n };
                        if (!Convert(&guarded, source, n) || guarded.length != (int64_t)n) return 12;
                        for (size_t i = 0; i < n; ++i) if (data[i] != source[i]) return 13;
                    }
                    if (munmap(input, page * 2) || munmap(result, page * 2)) return 14;
                    #endif
                    return 0;
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            if (sourceFreePackage)
            {
                var program = await File.ReadAllTextAsync(source);
                await File.WriteAllTextAsync(source, program[..program.IndexOf("unsafe ffi fn i32[min max] RunChecks();", StringComparison.Ordinal)]);
                var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Converter.lib" : "libConverter.a");
                var packageExit = await CompilerCli.RunAsync(
                    [source, "--emit-lib", "--target", target.Triple, "-o", library],
                    new StringReader(""), output, errors);
                Assert.True(packageExit == 0, output + "\n" + errors);
                File.Delete(source);
                await File.WriteAllTextAsync(source, """
                    import System.Text
                    module ConversionPackageConsumer
                    unsafe ffi fn i32[min max] RunChecks();
                    export unsafe ffi fn i32[min max] main() { return RunChecks(); }
                    """);
                output.GetStringBuilder().Clear();
                errors.GetStringBuilder().Clear();
            }
            var exit = await CompilerCli.RunAsync(
                [source, "--emit-exe", "--target", target.Triple, "-I", directory.FullName,
                    "--no-stark-path", "--native-source", witness, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Equal("", await stderr);
        }
        finally { directory.Delete(recursive: true); }
    }
}
