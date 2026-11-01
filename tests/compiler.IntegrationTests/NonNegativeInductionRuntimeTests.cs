using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class NonNegativeInductionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedRecurrencesMatchIndependentCFromSourceAndSourceFreePackage(bool package)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-inductive-sign-");
        try
        {
            var librarySource = Path.Combine(directory.FullName, "ProofKernels.stark");
            var app = Path.Combine(directory.FullName, "App.stark");
            var native = Path.Combine(directory.FullName, "Reference.c");
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(librarySource, Kernels);
            await File.WriteAllTextAsync(app, Consumer);
            await File.WriteAllTextAsync(native, Reference);
            async Task Compile(params string[] arguments)
            {
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                var exit = await CompilerCli.RunAsync(arguments, new StringReader(string.Empty), stdout, stderr);
                Assert.True(exit == 0, $"{stdout}{Environment.NewLine}{stderr}");
            }
            if (package)
            {
                var archive = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "ProofKernels.lib" : "libProofKernels.a");
                await Compile(librarySource, "--emit-lib", "--no-stark-path", "--target", target.Triple, "-o", archive);
                File.Delete(librarySource);
            }
            await Compile(app, "--emit-exe", "--no-stark-path", "--target", target.Triple,
                "-I", directory.FullName, "--native-source", native, "-o", executable);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {await stdoutTask}{await stderrTask}");
        }
        finally { directory.Delete(recursive: true); }
    }

    private const string Kernels = """
        module ProofKernels

        public noinline finite law i64[min max] Positive(u32[0 max] seed, u8[0 64] count)
        {
            stack mut i64[min max] total = (i64[min max])seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                stack i64[min max] value = total + (i64[min max])i;
                total = (total + value * 5 - value / 7 + value % 13) % 123457;
            }
            return total;
        }

        public noinline finite law i64[min max] Signed(i32[min max] seed, u8[0 64] count)
        {
            stack mut i64[min max] total = (i64[min max])seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                stack i64[min max] value = total + (i64[min max])i;
                total = (total + value * 5 - value / 7 + value % 13) % 123457;
            }
            return total;
        }

        public noinline finite law i64[min max] Wrapped(i32[min max] seed, u8[0 64] count)
        {
            stack mut i64[min max] total = (i64[min max])seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                total = (total *% 2305843009213693951 +% 1) % 123457;
            }
            return total;
        }

        public unsafe noinline finite law i64[min max] Truncated(i32[min max] seed, u8[0 64] count)
        {
            stack mut i64[min max] total = (i64[min max])seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                total = ((i64[min max])(i32[min max])(total * 1000000)) % 123457;
            }
            return total;
        }
        """;

    private const string Consumer = """
        import ProofKernels
        module ProofConsumer
        unsafe ffi finite i64[min max] Seed(u8[0 7] index, bool negative);
        unsafe ffi finite law u8[0 64] Count(u8[0 5] index);
        unsafe ffi finite law i64[min max] Reference(i64[min max] seed, u8[0 64] count, u8[0 2] mode);

        export unsafe ffi fn i32[min max] main()
        {
            for willexit (stack mut u8[0 8] s = 0; s < 8; s += 1)
            {
                stack i64[min max] positive = Seed((u8[0 7])s, false);
                stack i64[min max] signedValue = Seed((u8[0 7])s, true);
                for willexit (stack mut u8[0 6] c = 0; c < 6; c += 1)
                {
                    stack u8[0 64] count = Count((u8[0 5])c);
                    if (ProofKernels.Positive((u32[0 max])positive, count) != Reference(positive, count, 0)) { return 1; }
                    if (ProofKernels.Signed((i32[min max])signedValue, count) != Reference(signedValue, count, 0)) { return 2; }
                    if (ProofKernels.Wrapped((i32[min max])signedValue, count) != Reference(signedValue, count, 1)) { return 3; }
                    if (ProofKernels.Truncated((i32[min max])signedValue, count) != Reference(signedValue, count, 2)) { return 4; }
                }
            }
            return 0;
        }
        """;

    private const string Reference = """
        #include <stdint.h>
        #include <string.h>
        static volatile uint64_t salt;
        __attribute__((noinline)) int64_t Seed(uint8_t index, _Bool negative) {
            static const int64_t positive[8]={0,1,88,89,12345,2147483647,2147483648LL,4294967295LL};
            static const int64_t signed_values[8]={INT32_MIN,-12345,-89,-1,0,1,12345,INT32_MAX};
            return (negative ? signed_values[index] : positive[index]) + salt;
        }
        uint8_t Count(uint8_t index) {
            static const uint8_t counts[6]={0,1,2,7,31,64}; return counts[index];
        }
        __attribute__((noinline)) int64_t Reference(int64_t seed, uint8_t count, uint8_t mode) {
            int64_t total=seed%89;
            for (unsigned i=0;i<count;++i) {
                if (mode==1) {
                    uint64_t bits=(uint64_t)total*UINT64_C(2305843009213693951)+1;
                    int64_t wrapped; memcpy(&wrapped,&bits,sizeof(wrapped)); total=wrapped%123457;
                } else if (mode==2) {
                    uint32_t bits=(uint32_t)(total*1000000);
                    int32_t narrowed; memcpy(&narrowed,&bits,sizeof(narrowed)); total=(int64_t)narrowed%123457;
                } else {
                    int64_t value=total+(int64_t)i;
                    total=(total+value*5-value/7+value%13)%123457;
                }
            }
            return total;
        }
        """;
}
