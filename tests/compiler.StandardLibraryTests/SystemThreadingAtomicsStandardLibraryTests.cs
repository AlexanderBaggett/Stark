using Stark.Compiler;

namespace compiler.StandardLibraryTests;

/// <summary>
/// Atomics (docs/Self-host-Prep/12-atomics.md): the System.Threading atomic types are
/// stdlib structs whose semicolon-bodied methods lower through compiler-known builtins
/// onto LLVM atomic instructions (atomicrmw / cmpxchg / load atomic / store atomic),
/// seq-cst only. Tier 1 (8/16/32/64-bit + bool) must lower to single hardware
/// instructions; these tests guard both the runtime semantics and that lowering shape.
/// </summary>
public sealed class SystemThreadingAtomicsStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task AtomicI64SingleThreadedOperationSemanticsAreExactAtRuntime()
    {
        // Every operation returns the PREVIOUS value (fetch-and-modify semantics).
        // Value progression: Store(5) -> Add(3)=5 -> Sub(1)=8 -> And(255)=7 -> Or(8)=7
        // -> Xor(2)=15 -> Exchange(42)=13 -> CompareExchange(42,100) swaps -> Load()=100.
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-single-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

            export fn i32[min max] main()
            {
                Counter.Store(5);
                if (Counter.Add(3) != 5)
                {
                    return 1;
                }

                if (Counter.Sub(1) != 8)
                {
                    return 2;
                }

                if (Counter.And(255) != 7)
                {
                    return 3;
                }

                if (Counter.Or(8) != 7)
                {
                    return 4;
                }

                if (Counter.Xor(2) != 15)
                {
                    return 5;
                }

                if (Counter.Exchange(42) != 13)
                {
                    return 6;
                }

                if (!Counter.CompareExchange(42, 100))
                {
                    return 7;
                }

                if (Counter.CompareExchange(42, 555))
                {
                    return 8;
                }

                if (Counter.Load() != 100)
                {
                    return 9;
                }

                return 0;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AtomicI64TwoThreadCounterIsExactAtRuntime()
    {
        // The canonical safe-sharing demo from doc 12 §3: two threads bump a shared
        // static atomic counter; the total is exact on every run. A racy load+store
        // increment would lose updates; only an indivisible atomic add can pass this.
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-threads-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

            fn i32[min max] Worker()
            {
                stack mut i32[min max] i = 0;
                while willexit (i < 100000)
                {
                    Counter.Add(1);
                    i += 1;
                }

                return 0;
            }

            export fn i32[min max] main()
            {
                stack mut System.Threading.Thread a = new(Worker);
                stack mut System.Threading.Thread b = new(Worker);
                a.Join();
                b.Join();

                if (Counter.Load() == 200000)
                {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AtomicBoolFlagHandoffAcrossThreadsWorksAtRuntime()
    {
        // AtomicBool as a release flag: the worker publishes data through an AtomicI64,
        // then raises the Ready flag; main spins until it observes the flag, then must
        // see the published value (seq-cst ordering guarantees visibility).
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-bool-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI64 Payload = new System.Threading.AtomicI64(0);
            static mut System.Threading.AtomicBool Ready = new System.Threading.AtomicBool(false);

            fn i32[min max] Publisher()
            {
                Payload.Store(12345);
                Ready.Store(true);
                return 0;
            }

            export fn i32[min max] main()
            {
                // Single-threaded AtomicBool semantics first.
                stack mut System.Threading.AtomicBool flag = new System.Threading.AtomicBool(false);
                if (flag.Load())
                {
                    return 1;
                }

                if (flag.Exchange(true))
                {
                    return 2;
                }

                if (!flag.Load())
                {
                    return 3;
                }

                if (!flag.CompareExchange(true, false))
                {
                    return 4;
                }

                if (flag.CompareExchange(true, false))
                {
                    return 5;
                }

                // Cross-thread flag handoff.
                stack mut System.Threading.Thread publisher = new(Publisher);
                stack mut i64[min max] spins = 0;
                while willexit (!Ready.Load() && spins < 1000000000)
                {
                    spins += 1;
                }

                if (!Ready.Load())
                {
                    return 6;
                }

                if (Payload.Load() != 12345)
                {
                    return 7;
                }

                publisher.Join();
                return 0;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AllTier1AtomicWidthsHaveExactSemanticsAtRuntime()
    {
        // Every tier-1 atomic type (8/16/32-bit signed + unsigned, plus the 64-bit
        // unsigned sibling) shares the same fetch-and-modify contract; this exercises
        // each width end-to-end including negative values (signed) and full-range
        // values (unsigned).
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-widths-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI8 CounterI8 = new System.Threading.AtomicI8(-5);
            static mut System.Threading.AtomicI16 CounterI16 = new System.Threading.AtomicI16(-1000);
            static mut System.Threading.AtomicI32 CounterI32 = new System.Threading.AtomicI32(-100000);
            static mut System.Threading.AtomicU8 CounterU8 = new System.Threading.AtomicU8(200);
            static mut System.Threading.AtomicU16 CounterU16 = new System.Threading.AtomicU16(60000);
            static mut System.Threading.AtomicU32 CounterU32 = new System.Threading.AtomicU32(4000000000);
            static mut System.Threading.AtomicU64 CounterU64 = new System.Threading.AtomicU64(18000000000000000000);

            export fn i32[min max] main()
            {
                // Signed widths preserve sign through load/add/exchange.
                if (CounterI8.Load() != -5)
                {
                    return 1;
                }

                if (CounterI8.Add(10) != -5)
                {
                    return 2;
                }

                if (CounterI8.Load() != 5)
                {
                    return 3;
                }

                if (CounterI16.Add(1) != -1000)
                {
                    return 4;
                }

                if (CounterI16.Load() != -999)
                {
                    return 5;
                }

                if (CounterI32.Sub(1) != -100000)
                {
                    return 6;
                }

                if (CounterI32.Load() != -100001)
                {
                    return 7;
                }

                // Unsigned widths hold their full range.
                if (CounterU8.Load() != 200)
                {
                    return 8;
                }

                if (CounterU8.Add(55) != 200)
                {
                    return 9;
                }

                if (CounterU8.Load() != 255)
                {
                    return 10;
                }

                if (CounterU16.Load() != 60000)
                {
                    return 11;
                }

                if (CounterU32.Exchange(123) != 4000000000)
                {
                    return 12;
                }

                if (CounterU64.Load() != 18000000000000000000)
                {
                    return 13;
                }

                if (!CounterU64.CompareExchange(18000000000000000000, 42))
                {
                    return 14;
                }

                if (CounterU64.Load() != 42)
                {
                    return 15;
                }

                return 0;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Tier2ContainerAtomicWidthsHaveExactSemanticsAtRuntime()
    {
        // 24/48/96/128-bit atomics store their value sign/zero-extended in a power-of-2
        // container. This exercises the extension round-trip: negative values, values
        // wider than the next-smaller hardware width, wrap-around at the value width,
        // and compare-exchange equality across the container boundary.
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-tier2-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI24 Counter24 = new System.Threading.AtomicI24(-5);
            static mut System.Threading.AtomicI48 Counter48 = new System.Threading.AtomicI48(0);
            static mut System.Threading.AtomicU24 CounterU24 = new System.Threading.AtomicU24(16000000);
            static mut System.Threading.AtomicI128 Counter128 = new System.Threading.AtomicI128(0);

            export fn i32[min max] main()
            {
                // Signed i24: negative values survive the container extension round-trip.
                if (Counter24.Load() != -5)
                {
                    return 1;
                }

                if (Counter24.Add(10) != -5)
                {
                    return 2;
                }

                if (Counter24.Load() != 5)
                {
                    return 3;
                }

                // Wrap-around at the i24 boundary: max + 1 wraps to min.
                Counter24.Store(8388607);
                if (Counter24.Add(1) != 8388607)
                {
                    return 4;
                }

                if (Counter24.Load() != -8388608)
                {
                    return 5;
                }

                // Compare-exchange across the container boundary with negative values.
                if (!Counter24.CompareExchange(-8388608, -1))
                {
                    return 6;
                }

                if (Counter24.CompareExchange(-8388608, 7))
                {
                    return 7;
                }

                if (Counter24.Load() != -1)
                {
                    return 8;
                }

                // i48: values wider than 32 bits.
                Counter48.Store(140737488355327);
                if (Counter48.Add(-140737488355327) != 140737488355327)
                {
                    return 9;
                }

                if (Counter48.Load() != 0)
                {
                    return 10;
                }

                // u24: full unsigned range with zero-extension.
                if (CounterU24.Load() != 16000000)
                {
                    return 11;
                }

                if (CounterU24.Exchange(16777215) != 16000000)
                {
                    return 12;
                }

                if (CounterU24.Load() != 16777215)
                {
                    return 13;
                }

                // i128: values wider than 64 bits through cmpxchg16b-class instructions.
                Counter128.Store(18446744073709551616);
                if (Counter128.Load() != 18446744073709551616)
                {
                    return 14;
                }

                if (Counter128.Add(1) != 18446744073709551616)
                {
                    return 15;
                }

                if (Counter128.Load() != 18446744073709551617)
                {
                    return 16;
                }

                if (!Counter128.CompareExchange(18446744073709551617, -18446744073709551616))
                {
                    return 17;
                }

                if (Counter128.Load() != -18446744073709551616)
                {
                    return 18;
                }

                if (Counter128.And(0) != -18446744073709551616)
                {
                    return 19;
                }

                if (Counter128.Load() != 0)
                {
                    return 20;
                }

                return 0;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Tier2AtomicTwoThreadCountersAreExactAtRuntime()
    {
        // The Add compare-exchange retry loop (i24) and the 128-bit cmpxchg16b path
        // must both stay exact under real thread contention.
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-tier2-threads-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI24 Counter24 = new System.Threading.AtomicI24(0);
            static mut System.Threading.AtomicI128 Counter128 = new System.Threading.AtomicI128(0);

            fn i32[min max] Worker()
            {
                stack mut i32[min max] i = 0;
                while willexit (i < 50000)
                {
                    Counter24.Add(1);
                    Counter128.Add(1);
                    i += 1;
                }

                return 0;
            }

            export fn i32[min max] main()
            {
                stack mut System.Threading.Thread a = new(Worker);
                stack mut System.Threading.Thread b = new(Worker);
                a.Join();
                b.Join();

                if (Counter24.Load() != 100000)
                {
                    return 1;
                }

                if (Counter128.Load() != 100000)
                {
                    return 2;
                }

                return 0;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Tier2ContainerAtomicsLowerToLockFreeContainerInstructions()
    {
        // Tier-2 lowering shape (doc 12 §4): everything except Add/Sub is a single
        // container-wide instruction; Add/Sub use a compare-exchange retry loop; the
        // 128-bit container carries the cmpxchg16b target feature on x86-64.
        var llvm = EmitAtomicsAppLlvm(
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI24 Counter24 = new System.Threading.AtomicI24(0);
            static mut System.Threading.AtomicU128 Counter128 = new System.Threading.AtomicU128(0);

            export fn i32[min max] main()
            {
                Counter24.Store(5);
                stack i24[min max] previous24 = Counter24.Add(3);
                stack bool swapped24 = Counter24.CompareExchange(8, 1);
                stack i24[min max] final24 = Counter24.Load();

                Counter128.Store(5);
                stack u128[0 max] previous128 = Counter128.Add(3);
                stack bool swapped128 = Counter128.CompareExchange(8, 1);
                stack u128[0 max] final128 = Counter128.Load();
                return 0;
            }
            """);

        // i24 operations work on its i32 container.
        Assert.Contains("load atomic i32", llvm, StringComparison.Ordinal);
        Assert.Contains("store atomic i32", llvm, StringComparison.Ordinal);
        Assert.Contains("cmpxchg ptr %arg_self, i32", llvm, StringComparison.Ordinal);

        // 128-bit operations work on the full i128 container and carry +cx16 on x86-64.
        Assert.Contains("load atomic i128", llvm, StringComparison.Ordinal);
        Assert.Contains("store atomic i128", llvm, StringComparison.Ordinal);
        Assert.Contains("cmpxchg ptr %arg_self, i128", llvm, StringComparison.Ordinal);
        Assert.Contains("\"target-features\"=\"+cx16\"", llvm, StringComparison.Ordinal);

        // The compare-exchange retry loop exists only inside Add/Sub bodies; everything
        // else stays a straight-line single instruction.
        AssertCasLoopsOnlyInArithmeticBuiltins(llvm);
    }

    private static void AssertCasLoopsOnlyInArithmeticBuiltins(string llvm)
    {
        string? currentAtomicBuiltin = null;
        var sawCasLoop = false;
        foreach (var line in llvm.Split('\n'))
        {
            if (line.StartsWith("define ", StringComparison.Ordinal))
            {
                currentAtomicBuiltin = line.Contains("_Atomic", StringComparison.Ordinal) ? line : null;
                continue;
            }

            if (currentAtomicBuiltin is null)
            {
                continue;
            }

            if (line.StartsWith("}", StringComparison.Ordinal))
            {
                currentAtomicBuiltin = null;
                continue;
            }

            if (line.StartsWith("cas_loop:", StringComparison.Ordinal))
            {
                sawCasLoop = true;
                Assert.True(
                    currentAtomicBuiltin.Contains("_Add(", StringComparison.Ordinal)
                        || currentAtomicBuiltin.Contains("_Sub(", StringComparison.Ordinal),
                    $"Compare-exchange retry loop found outside Add/Sub: {currentAtomicBuiltin}");
            }
        }

        Assert.True(sawCasLoop, "Expected at least one Add/Sub compare-exchange retry loop in the emitted LLVM IR.");
    }

    [Fact]
    public async Task Tier3EmbeddedLockAtomicWidthsHaveExactSemanticsAtRuntime()
    {
        // 192-1024-bit atomics serialize through their embedded spinlock. This exercises
        // values far beyond 128 bits (2^150, 2^200), every operation, and the
        // compare-exchange hit/miss paths.
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-tier3-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI256 Counter256 = new System.Threading.AtomicI256(0);
            static mut System.Threading.AtomicU1024 Counter1024 = new System.Threading.AtomicU1024(1);

            export fn i32[min max] main()
            {
                // 2^150 is far beyond any hardware atomic width.
                Counter256.Store(1427247692705959881058285969449495136382746624);
                if (Counter256.Load() != 1427247692705959881058285969449495136382746624)
                {
                    return 1;
                }

                if (Counter256.Add(1) != 1427247692705959881058285969449495136382746624)
                {
                    return 2;
                }

                if (Counter256.Load() != 1427247692705959881058285969449495136382746625)
                {
                    return 3;
                }

                if (Counter256.Sub(1427247692705959881058285969449495136382746625) != 1427247692705959881058285969449495136382746625)
                {
                    return 4;
                }

                if (Counter256.Load() != 0)
                {
                    return 5;
                }

                // Negative values across the full 256-bit width.
                Counter256.Store(-1427247692705959881058285969449495136382746624);
                if (Counter256.Load() != -1427247692705959881058285969449495136382746624)
                {
                    return 6;
                }

                if (!Counter256.CompareExchange(-1427247692705959881058285969449495136382746624, 77))
                {
                    return 7;
                }

                if (Counter256.CompareExchange(-1427247692705959881058285969449495136382746624, 88))
                {
                    return 8;
                }

                if (Counter256.Load() != 77)
                {
                    return 9;
                }

                if (Counter256.Exchange(-1) != 77)
                {
                    return 10;
                }

                if (Counter256.Load() != -1)
                {
                    return 11;
                }

                // 1024-bit unsigned with a value around 2^200.
                Counter1024.Store(1606938044258990275541962092341162602522202993782792835301376);
                if (Counter1024.Load() != 1606938044258990275541962092341162602522202993782792835301376)
                {
                    return 12;
                }

                if (Counter1024.Or(1) != 1606938044258990275541962092341162602522202993782792835301376)
                {
                    return 13;
                }

                if (Counter1024.Load() != 1606938044258990275541962092341162602522202993782792835301377)
                {
                    return 14;
                }

                return 0;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Tier3EmbeddedLockAtomicTwoThreadCounterIsExactAtRuntime()
    {
        // The embedded spinlock must serialize concurrent RMW operations exactly, the
        // same observable contract as the lock-free tiers.
        var exitCode = await CompileAndRunAtomicsAppAsync(
            "stark-stdlib-atomics-tier3-threads-",
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI256 Counter = new System.Threading.AtomicI256(0);

            fn i32[min max] Worker()
            {
                stack mut i32[min max] i = 0;
                while willexit (i < 20000)
                {
                    Counter.Add(1);
                    i += 1;
                }

                return 0;
            }

            export fn i32[min max] main()
            {
                stack mut System.Threading.Thread a = new(Worker);
                stack mut System.Threading.Thread b = new(Worker);
                a.Join();
                b.Join();

                if (Counter.Load() == 40000)
                {
                    return 0;
                }

                return 1;
            }
            """);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Tier3EmbeddedLockAtomicsLowerToSpinlockProtectedOperations()
    {
        // Tier-3 lowering shape (doc 12 §4): the struct layout carries the lock word
        // (visible in the type definition), operations spin on it with seq-cst
        // exchange, and the value accesses happen inside the critical section.
        var llvm = EmitAtomicsAppLlvm(
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI256 Counter = new System.Threading.AtomicI256(0);

            export fn i32[min max] main()
            {
                Counter.Store(5);
                stack i256[min max] previous = Counter.Add(3);
                stack bool swapped = Counter.CompareExchange(8, 1);
                stack i256[min max] finalValue = Counter.Load();
                return 0;
            }
            """);

        // The struct embeds the value (i256) and the lock word (i32) — visible layout.
        Assert.Contains("%System_Threading_AtomicI256 = type { i256, i32 }", llvm, StringComparison.Ordinal);

        // Operations serialize through the embedded seq-cst spinlock.
        Assert.Contains("atomicrmw xchg ptr %atomic_lock_addr, i32 1 seq_cst", llvm, StringComparison.Ordinal);
        Assert.Contains("store atomic i32 0, ptr %atomic_lock_addr seq_cst", llvm, StringComparison.Ordinal);

        // The value operations work at the full declared width.
        Assert.Contains("load i256, ptr %arg_self", llvm, StringComparison.Ordinal);
        Assert.Contains("store i256", llvm, StringComparison.Ordinal);

        // The global static reserves space for both the value and the lock word.
        Assert.Contains("@Counter = global %System_Threading_AtomicI256", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicI64Tier1BuiltinsLowerToSingleAtomicInstructions()
    {
        // Tier-1 widths must lower to single LLVM atomic instructions (doc 12 §4/§5):
        // never a CAS loop, never a lock, never a libcall.
        var llvm = EmitAtomicsAppLlvm(
            """
            import System.Threading
            module App

            static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

            export fn i32[min max] main()
            {
                Counter.Store(5);
                stack i64[min max] previousAdd = Counter.Add(3);
                stack i64[min max] previousSub = Counter.Sub(1);
                stack i64[min max] previousAnd = Counter.And(255);
                stack i64[min max] previousOr = Counter.Or(8);
                stack i64[min max] previousXor = Counter.Xor(2);
                stack i64[min max] previousExchange = Counter.Exchange(42);
                stack bool swapped = Counter.CompareExchange(42, 100);
                stack i64[min max] finalValue = Counter.Load();
                return (i32[min max])(finalValue - finalValue);
            }
            """);

        // Each operation is a single seq-cst atomic instruction on the i64 value.
        Assert.Contains("atomicrmw add ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("atomicrmw sub ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("atomicrmw and ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("atomicrmw or ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("atomicrmw xor ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("atomicrmw xchg ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("cmpxchg ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("load atomic i64", llvm, StringComparison.Ordinal);
        Assert.Contains("store atomic i64", llvm, StringComparison.Ordinal);

        // Every atomic operation inside the builtin bodies is seq-cst, and each body is
        // a leaf single-instruction definition: no calls (libcall fallback), no branches
        // (CAS loop), no locks.
        AssertAtomicBuiltinBodies(llvm, "@System_Threading_AtomicI64_");
    }

    private static void AssertAtomicBuiltinBodies(string llvm, string functionNamePrefix)
    {
        var foundAtomicBuiltinDefinition = false;
        var inAtomicBuiltinBody = false;
        foreach (var line in llvm.Split('\n'))
        {
            if (line.StartsWith("define ", StringComparison.Ordinal)
                && line.Contains(functionNamePrefix, StringComparison.Ordinal))
            {
                foundAtomicBuiltinDefinition = true;
                inAtomicBuiltinBody = true;
                continue;
            }

            if (!inAtomicBuiltinBody)
            {
                continue;
            }

            if (line.StartsWith("}", StringComparison.Ordinal))
            {
                inAtomicBuiltinBody = false;
                continue;
            }

            // Single-instruction guarantee: no calls, no control flow inside the body.
            Assert.DoesNotContain(" call ", line, StringComparison.Ordinal);
            Assert.DoesNotContain(" br ", line, StringComparison.Ordinal);

            // Seq-cst guarantee on every atomic instruction in the body.
            if (line.Contains("atomicrmw ", StringComparison.Ordinal)
                || line.Contains("load atomic ", StringComparison.Ordinal)
                || line.Contains("store atomic ", StringComparison.Ordinal))
            {
                Assert.Contains("seq_cst", line, StringComparison.Ordinal);
            }

            if (line.Contains("cmpxchg ", StringComparison.Ordinal))
            {
                Assert.Contains("seq_cst seq_cst", line, StringComparison.Ordinal);
            }
        }

        Assert.True(
            foundAtomicBuiltinDefinition,
            $"Expected at least one defined atomic builtin with prefix '{functionNamePrefix}' in the emitted LLVM IR.");
    }

    private static string EmitAtomicsAppLlvm(string appSource)
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibAtomicsEmission.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(appSource, appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        return result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
    }

    private static async Task<int> CompileAndRunAtomicsAppAsync(string tempDirectoryPrefix, string appSource)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return 0;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory(tempDirectoryPrefix);
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(appPath, appSource);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
            return execution.ExitCode;
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
