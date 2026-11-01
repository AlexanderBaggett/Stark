using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class IntegerCallFactsRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScalarCallFactsPreserveSignedAndWrappingResults(bool package)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-call-facts-");
        try
        {
            var library = Path.Combine(directory.FullName, "IntegerKernels.stark");
            var app = Path.Combine(directory.FullName, "App.stark");
            var reference = Path.Combine(directory.FullName, "Reference.c");
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(library, Library);
            await File.WriteAllTextAsync(app, Consumer);
            await File.WriteAllTextAsync(reference, Reference);
            async Task Compile(params string[] arguments)
            {
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                var exit = await CompilerCli.RunAsync(arguments, new StringReader(""), stdout, stderr);
                Assert.True(exit == 0, $"{stdout}{Environment.NewLine}{stderr}");
            }
            if (package)
            {
                var archive = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "IntegerKernels.lib" : "libIntegerKernels.a");
                await Compile(library, "--emit-lib", "--no-stark-path", "--target", target.Triple, "-o", archive);
                File.Delete(library);
            }
            await Compile(app, "--emit-exe", "--no-stark-path", "--target", target.Triple, "-I", directory.FullName,
                "--native-source", reference, "-o", executable);
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {await stdoutTask}{await stderrTask}");
        }
        finally { directory.Delete(recursive: true); }
    }

    private const string Library = """
        module IntegerKernels
        public noinline finite law i64[min max] Step(i64[min max] left, i64[min max] right)
        {
            return (left * 7 + right) % 65537;
        }
        public unsafe noinline finite law i64[min max] Narrow(u16[0 1088] value)
        {
            return (i64[min max])(i8[min max])value;
        }
        public unsafe noinline finite law i64[min max] UnsignedByte(u16[0 1088] value)
        {
            return (i64[min max])(u8[0 max])value;
        }
        public unsafe noinline finite law u64[0 max] ToUnsigned(i8[min max] value)
        {
            return (u64[0 max])value;
        }
        """;

    private const string Consumer = """
        import IntegerKernels
        module CallConsumer
        unsafe ffi finite i64[min max] Seed(u8[0 7] index, bool negative);
        unsafe ffi finite law u8[0 64] Count(u8[0 5] index);
        unsafe ffi finite law u16[0 1088] Small(u8[0 8] index);
        unsafe ffi finite law i64[min max] Reference(i64[min max] seed, u8[0 64] count);
        unsafe ffi finite law i64[min max] ReferenceByte(u16[0 1088] value, bool sign);
        unsafe ffi finite law u64[0 max] ReferenceUnsigned(u16[0 1088] value);

        noinline finite law i64[min max] Reduce(i64[min max] seed, u8[0 64] count)
        {
            stack mut i64[min max] total = seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                total = (total + Step((i64[min max])i, total % 23)) % 1000003;
            }
            return total;
        }
        noinline finite law i64[min max] Positive(u32[0 max] seed, u8[0 64] count)
        {
            stack mut i64[min max] total = (i64[min max])seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                total = (total + Step((i64[min max])i, total % 23)) % 1000003;
            }
            return total;
        }
        export unsafe ffi finite i32[min max] main()
        {
            for willexit (stack mut u8[0 8] s = 0; s < 8; s += 1)
            {
                stack i64[min max] positive = Seed((u8[0 7])s, false);
                stack i64[min max] negative = Seed((u8[0 7])s, true);
                for willexit (stack mut u8[0 6] c = 0; c < 6; c += 1)
                {
                    stack u8[0 64] count = Count((u8[0 5])c);
                    if (Positive((u32[0 max])positive, count) != Reference(positive, count)) { return 1; }
                    if (Reduce(negative, count) != Reference(negative, count)) { return 2; }
                }
            }
            for willexit (stack mut u8[0 9] i = 0; i < 9; i += 1)
            {
                stack u16[0 1088] value = Small((u8[0 8])i);
                if (Narrow(value) != ReferenceByte(value, true)) { return 3; }
                if (UnsignedByte(value) != ReferenceByte(value, false)) { return 4; }
                stack i8[min max] low = (i8[min max])value;
                if (ToUnsigned(low) != ReferenceUnsigned(value)) { return 5; }
            }
            return 0;
        }
        """;

    private const string Reference = """
        #include <stdint.h>
        #include <string.h>
        static volatile int64_t salt;
        __attribute__((noinline)) int64_t Seed(uint8_t index, _Bool negative) {
            static const int64_t p[8]={0,1,88,89,12345,INT32_MAX,2147483648LL,4294967295LL};
            static const int64_t n[8]={INT32_MIN,-65537,-89,-1,0,1,12345,INT32_MAX};
            return (negative?n[index]:p[index])+salt;
        }
        uint8_t Count(uint8_t index) { static const uint8_t c[6]={0,1,2,7,31,64}; return c[index]; }
        uint16_t Small(uint8_t index) { static const uint16_t s[9]={0,1,127,128,129,255,256,1023,1088}; return s[index]; }
        __attribute__((noinline)) int64_t Reference(int64_t seed,uint8_t count) {
            int64_t total=seed%89;
            for(unsigned i=0;i<count;++i) total=(total+((int64_t)i*7+total%23)%65537)%1000003;
            return total;
        }
        int64_t ReferenceByte(uint16_t value,_Bool sign) {
            uint8_t bits=(uint8_t)value; int8_t low; memcpy(&low,&bits,sizeof(low));
            return sign?(int64_t)low:(int64_t)bits;
        }
        uint64_t ReferenceUnsigned(uint16_t value) {
            uint8_t bits=(uint8_t)value; int8_t low; memcpy(&low,&bits,sizeof(low)); return (uint64_t)low;
        }
        """;
}
