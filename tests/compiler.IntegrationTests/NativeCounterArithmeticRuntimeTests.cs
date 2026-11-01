using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class NativeCounterArithmeticRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CounterArithmeticPreservesNarrowBoundariesAndFallbacks(bool package)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-counter-arithmetic-");
        try
        {
            var kernel = Path.Combine(directory.FullName, "CounterKernel.stark");
            var app = Path.Combine(directory.FullName, "App.stark");
            var reference = Path.Combine(directory.FullName, "Reference.c");
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(kernel, Kernel);
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
                var archive = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "CounterKernel.lib" : "libCounterKernel.a");
                await Compile(kernel, "--emit-lib", "--no-stark-path", "--target", target.Triple, "-o", archive);
                File.Delete(kernel);
            }
            await Compile(app, "--emit-exe", "--no-stark-path", "--target", target.Triple, "-I", directory.FullName,
                "--native-source", reference, "-o", executable);
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {await stdoutTask}{await stderrTask}");
        }
        finally { directory.Delete(recursive: true); }
    }

    private const string Kernel = """
        module CounterKernel

        noinline finite law u32[0 max] Observe(u24[0 300000] value)
        {
            return (u32[0 max])value;
        }

        public unsafe noinline finite law i64[min max] Sum(u32[0 max] salt, u24[0 300000] stop)
        {
            stack mut i64[min max] total = (i64[min max])salt;
            stack mut u8[0 max][8] bytes = {0};
            for willexit (stack mut u24[0 300000] i = 0; i < 300000; i += 1)
            {
                if (i == stop) { break; }
                total += (i64[min max])(i % 17) + (i64[min max])(i / 7) + (i64[min max])(i % 400001);
                stack i24[min max] signedValue = (i24[min max])i;
                total += (i64[min max])(signedValue % 23);
                total += (i64[min max])Observe(i);
                bytes[(u8[0 7])(i % 8)] = (u8[0 max])(i % 127);
                stack i24[min max] shifted = signedValue - 150000;
                total += (i64[min max])(shifted % -13);
                stack i8[min max] low = (i8[min max])i;
                total += (i64[min max])(low % -5);
                stack u24[0 max] wrapped = (u24[0 max])i +% (u24[0 max])16777215;
                total += (i64[min max])(wrapped % (u24[0 max])19);
            }
            return total + (i64[min max])bytes[0] * 3 + (i64[min max])bytes[7] * 5;
        }
        """;

    private const string Consumer = """
        import CounterKernel
        module CounterConsumer
        unsafe ffi finite u32[0 max] Seed(u8[0 4] index);
        unsafe ffi finite law u32[0 max] Stop(u8[0 5] index);
        unsafe ffi finite law i64[min max] Reference(u32[0 max] seed, u32[0 max] count);
        unsafe ffi finite void Failure(i64[min max] actual, i64[min max] expected, u32[0 max] count);

        export unsafe ffi finite i32[min max] main()
        {
            for willexit (stack mut u8[0 5] s = 0; s < 5; s += 1)
            {
                stack u32[0 max] seed = Seed((u8[0 4])s);
                for willexit (stack mut u8[0 6] c = 0; c < 6; c += 1)
                {
                    stack u32[0 max] count = Stop((u8[0 5])c);
                    stack i64[min max] actual = Sum(seed, (u24[0 300000])count);
                    stack i64[min max] expected = Reference(seed, count);
                    if (actual != expected) { Failure(actual, expected, count); return 1; }
                }
            }
            return 0;
        }
        """;

    private const string Reference = """
        #include <stdint.h>
        #include <string.h>
        #include <stdio.h>
        static volatile uint32_t salt;
        __attribute__((noinline)) uint32_t Seed(uint8_t index) {
            static const uint32_t seeds[5]={0,1,127,UINT32_C(2147483648),UINT32_MAX};
            return seeds[index]+salt;
        }
        uint32_t Stop(uint8_t index) {
            static const uint32_t stops[6]={0,1,7,257,65537,300000}; return stops[index];
        }
        void Failure(int64_t actual, int64_t expected, uint32_t count) {
            fprintf(stderr,"count=%u actual=%lld expected=%lld\n",count,(long long)actual,(long long)expected);
        }
        __attribute__((noinline)) int64_t Reference(uint32_t seed, uint32_t count) {
            int64_t total=seed; uint8_t bytes[8]={0};
            for (uint32_t i=0;i<count;++i) {
                total += i%17+i/7+i%400001;
                total += i%23;
                total += i;
                bytes[i%8]=(uint8_t)(i%127);
                total += ((int32_t)i-150000)%-13;
                uint8_t bits=(uint8_t)i; int8_t low; memcpy(&low,&bits,sizeof(low));
                total += low%-5;
                total += ((i+UINT32_C(16777215))&UINT32_C(16777215))%19;
            }
            return total+bytes[0]*3+bytes[7]*5;
        }
        """;
}
