using Stark.Compiler;
using static compiler.StandardLibraryTests.SystemCollectionsTestPrograms;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsDictionaryStandardLibraryTests : StandardLibraryTestSuite
{
    private const string PromotedDictionaryProgram = """
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool IsPowerOfTwo(u64[0 2 ** 63 - 1] value)
        {
            if (value == 0)
            {
                return false;
            }

            stack u64[0 2 ** 63 - 1] mask = (u64[0 2 ** 63 - 1])(value - 1);
            return (value & mask) == 0;
        }

        fn void Bump(i32[min max] value)
        {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource
        {
            i32[min max] Value;

            drop
            {
                Bump(self.Value);
            }
        }

        export unsafe fn i32[min max] main()
        {
            stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity()))
            {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = i;
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i * 5);
                if (!Ok(dictionary.Set(key, value)) || !IsPowerOfTwo(dictionary.Capacity()))
                {
                    return 2;
                }
            }

            if (dictionary.Count() != 128 || dictionary.IsEmpty())
            {
                return 3;
            }

            stack mut i64[min max] checksum = 0;
            stack mut u32[0 2 ** 31 - 1] found = 0;
            stack u32[0 2 ** 31 - 1] refKey = 7;
            stack u64[0 2 ** 63 - 1] refIndex = dictionary.FindIndex(refKey);
            if (!dictionary.ContainsIndex(refIndex) || dictionary.GetAtIndex(refIndex) != 35)
            {
                return 25;
            }

            dictionary.GetMutAtIndex(refIndex) = 36;
            if (dictionary.GetAtIndex(refIndex) != 36)
            {
                return 26;
            }

            dictionary.GetMutAtIndex(refIndex) = 35;
            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = i;
                if (!dictionary.ContainsKey(key) || !dictionary.TryGet(key, found) || found != (u32[0 2 ** 31 - 1])(i * 5))
                {
                    return 4;
                }

                checksum += (i64[min max])found;
            }

            if (checksum != 40640)
            {
                return 5;
            }

            stack u32[0 2 ** 31 - 1] updateKey = 64;
            if (!Ok(dictionary.Set(updateKey, 999)) || !dictionary.TryGet(updateKey, found) || found != 999 || dictionary.Count() != 128)
            {
                return 6;
            }

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                if (!dictionary.Remove(key))
                {
                    return 7;
                }
            }

            if (dictionary.Count() != 64)
            {
                return 8;
            }

            stack u32[0 2 ** 31 - 1] removedKey = 65;
            if (!dictionary.TryRemove(removedKey, found) || found != 325 || dictionary.ContainsKey(removedKey) || dictionary.Count() != 63)
            {
                return 9;
            }

            stack u32[0 2 ** 31 - 1] tombstoneKey = 4096;
            if (!Ok(dictionary.Set(tombstoneKey, 12345)) || !dictionary.TryGet(tombstoneKey, found) || found != 12345)
            {
                return 10;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> clustered = new();
                stack u32[0 2 ** 31 - 1] clusterKeyOne = 1;
                stack u32[0 2 ** 31 - 1] clusterKeyTwo = 9;
                stack u32[0 2 ** 31 - 1] clusterKeyThree = 17;
                stack u32[0 2 ** 31 - 1] clusterKeyFour = 25;
                if (!Ok(clustered.Reserve(4))
                    || !Ok(clustered.Set(clusterKeyOne, 10))
                    || !Ok(clustered.Set(clusterKeyTwo, 90))
                    || !Ok(clustered.Set(clusterKeyThree, 170)))
                    {
                        return 27;
                }

                if (!clustered.Remove(clusterKeyOne)
                    || clustered.ContainsKey(clusterKeyOne)
                    || !clustered.ContainsIndex(1)
                    || !clustered.TryGet(clusterKeyTwo, found)
                    || found != 90
                    || !clustered.TryGet(clusterKeyThree, found)
                    || found != 170
                    || clustered.Count() != 2)
                    {
                        return 28;
                }

                if (!Ok(clustered.Set(clusterKeyFour, 250))
                    || !clustered.TryGet(clusterKeyFour, found)
                    || found != 250
                    || clustered.Count() != 3)
                    {
                        return 29;
                }
            }

            dictionary.Clear();
            if (!dictionary.IsEmpty() || dictionary.Count() != 0 || dictionary.ContainsKey(tombstoneKey))
            {
                return 11;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> drops = new();
                stack u32[0 2 ** 31 - 1] keyOne = 1;
                stack u32[0 2 ** 31 - 1] keyTwo = 2;
                stack u32[0 2 ** 31 - 1] keyThree = 3;
                stack u32[0 2 ** 31 - 1] keyFour = 4;
                if (!Ok(drops.Set(keyOne, new Resource()
                {
                    Value = 10
                }
                ))
                    || !Ok(drops.Set(keyTwo, new Resource()
                    {
                        Value = 20
                    }
                    ))
                    || !Ok(drops.Set(keyOne, new Resource()
                    {
                        Value = 30
                    }
                    )))
                    {
                        return 12;
                }

                if (DropCounter != 10)
                {
                    return 13;
                }

                if (!drops.Remove(keyTwo))
                {
                    return 14;
                }

                if (DropCounter != 30)
                {
                    return 15;
                }

                {
                    stack DictionaryRemoveResult<Resource> removedResult = drops.RemoveMove(keyOne);
                    switch (removedResult)
                    {
                        case DictionaryRemoveResult<Resource>.Missing:
                            return 16;
                        case DictionaryRemoveResult<Resource>.Removed(var removed):
                            if (DropCounter != 30 || removed.Value != 30)
                            {
                                return 17;
                            }
                    }
                }

                if (DropCounter != 60)
                {
                    return 18;
                }

                if (!Ok(drops.Set(keyThree, new Resource()
                {
                    Value = 40
                }
                ))
                    || !Ok(drops.Set(keyFour, new Resource()
                    {
                        Value = 50
                    }
                    ))
                    || !Ok(drops.Reserve(64)))
                    {
                        return 19;
                }

                if (DropCounter != 60)
                {
                    return 20;
                }

                drops.Clear();
                if (DropCounter != 150 || drops.Count() != 0)
                {
                    return 21;
                }
            }

            if (DropCounter != 150)
            {
                return 22;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> scopedDrops = new();
                stack u32[0 2 ** 31 - 1] scopedKey = 7;
                if (!Ok(scopedDrops.Set(scopedKey, new Resource()
                {
                    Value = 60
                }
                )))
                {
                    return 23;
                }
            }

            if (DropCounter != 210)
            {
                return 24;
            }

            return 0;
        }
        """;

    private const string TextKeyCollectionsProgram = """
        import System.Collections
        import System.Memory
        import System.Text
        module App

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool UseTextKeys()
        {
            stack ascii alpha = "alpha";
            stack ascii alphaAgain = "alpha";
            stack ascii beta = "beta";
            stack mut Dictionary<ascii, u32[0 max]> asciiMap = new();
            if (!Ok(asciiMap.Set(alpha, 11)))
            {
                return false;
            }

            stack mut u32[0 max] found = 0;
            if (!asciiMap.TryGet(alphaAgain, found)
                || found != 11
                || asciiMap.ContainsKey(beta))
            {
                return false;
            }

            stack unicode gamma = (unicode)"gamma";
            stack unicode gammaAgain = (unicode)"gamma";
            stack unicode delta = (unicode)"delta";
            stack mut HashSet<unicode> unicodeSet = new();
            if (!Ok(unicodeSet.Add(gamma)))
            {
                return false;
            }

            if (!unicodeSet.Contains(gammaAgain)
                || unicodeSet.Contains(delta))
            {
                return false;
            }

            if (Hash(alpha) != Hash(alphaAgain)
                || Hash(alpha) == Hash(beta)
                || !Equals(alpha, alphaAgain)
                || Compare(alpha, beta) != Ordering.Less)
            {
                return false;
            }

            stack mut OwnedAscii ownedAlpha = new();
            if (!Ok(ownedAlpha.AppendAscii(alpha)))
            {
                return false;
            }

            stack mut OwnedAscii ownedAlphaAgain = new();
            if (!Ok(ownedAlphaAgain.AppendAscii(alphaAgain)))
            {
                return false;
            }

            stack mut Dictionary<OwnedAscii, u32[0 max]> ownedAsciiMap = new();
            if (!Ok(ownedAsciiMap.Set(ownedAlpha, 33))
                || !ownedAsciiMap.TryGet(ownedAlphaAgain, found)
                || found != 33)
            {
                return false;
            }

            if (!TryGetAsciiKey(ownedAsciiMap, alphaAgain, found)
                || found != 33
                || ContainsAsciiKey(ownedAsciiMap, beta))
            {
                return false;
            }

            stack mut OwnedAscii ownedBeta = new();
            if (!Ok(ownedBeta.AppendAscii(beta)))
            {
                return false;
            }

            stack mut HashSet<OwnedAscii> ownedAsciiSet = new();
            if (!Ok(ownedAsciiSet.Add(ownedBeta))
                || !ContainsAsciiKey(ownedAsciiSet, beta)
                || ContainsAsciiKey(ownedAsciiSet, alpha))
            {
                return false;
            }

            stack mut OwnedUnicode ownedGamma = new();
            if (!Ok(ownedGamma.AppendUnicode(gamma)))
            {
                return false;
            }

            stack mut OwnedUnicode ownedGammaAgain = new();
            if (!Ok(ownedGammaAgain.AppendUnicode(gammaAgain)))
            {
                return false;
            }

            stack mut HashSet<OwnedUnicode> ownedUnicodeSet = new();
            if (!Ok(ownedUnicodeSet.Add(ownedGamma))
                || !ownedUnicodeSet.Contains(ownedGammaAgain))
            {
                return false;
            }

            if (!ContainsUnicodeKey(ownedUnicodeSet, gammaAgain)
                || ContainsUnicodeKey(ownedUnicodeSet, delta))
            {
                return false;
            }

            stack mut OwnedUnicode ownedMapGamma = new();
            if (!Ok(ownedMapGamma.AppendUnicode(gamma)))
            {
                return false;
            }

            stack mut Dictionary<OwnedUnicode, u32[0 max]> ownedUnicodeMap = new();
            if (!Ok(ownedUnicodeMap.Set(ownedMapGamma, 44))
                || !TryGetUnicodeKey(ownedUnicodeMap, gammaAgain, found)
                || found != 44
                || ContainsUnicodeKey(ownedUnicodeMap, delta))
            {
                return false;
            }

            return true;
        }

        export unsafe fn i32[min max] main()
        {
            if (!UseTextKeys())
            {
                return 1;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceDictionaryRawSparseStorageStaysInternalAndJustified()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System", "Collections.stark"));

        Assert.Contains("Raw pointer boundary: Dictionary keeps sparse key/value/control storage", source, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<K> Keys;", source, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<V> Values;", source, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<u8[0 2]> States;", source, StringComparison.Ordinal);
        Assert.Contains("internal System.Memory.Allocation KeysAllocation;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public rawptr", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public rawmutptr", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedDictionaryUsesSparseRawValueStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedDictionaryLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowDictionary()
                {
                    stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1)
                    {
                        stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i + 7);
                        if (!Ok(dictionary.Set(i, value)))
                        {
                            return false;
                        }
                    }

                    stack u32[0 2 ** 31 - 1] lookupKey = 17;
                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    return dictionary.Capacity() >= 32
                        && dictionary.TryGet(lookupKey, found)
                        && found == 24
                        && dictionary.Remove(lookupKey)
                        && dictionary.Count() == 31;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                // Target Linux explicitly: this test asserts the libc-free sparse storage
                // lowering, and only the Linux OS allocation shim is syscall-backed (macOS
                // legitimately bottoms out in libc malloc, Windows in HeapAlloc).
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", llvm.Text, StringComparison.Ordinal);

        var reserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_Dictionary_Reserve__u32_0_2147483647__u32_0_2147483647(",
            "Expected Dictionary.Reserve specialization to be emitted.");
        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__u32_0_2147483647__u32_0_2147483647(",
            "Expected Dictionary.TryGet specialization to be emitted.");

        Assert.Contains("@System_Memory_Allocate(", reserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("switch", tryGetBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DictionaryValueSlot", tryGetBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotedDictionaryLookupUsesGroupedControlByteProbe()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkPath = Path.Combine(repositoryRoot, "benchmarks", "collections", "DictionaryLookup.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkPath), benchmarkPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var findIndexBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindIndex__u32__u32(");
        var findInsertionBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindInsertionIndex__u32__u32(");
        var initializeBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc void @__stark_mono_fn_System_Collections__System_Collections_Dictionary_InitializeStates__u32__u32(");

        Assert.DoesNotContain("; LLVM body emission fallback", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("br i1 undef", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryStateWordAt", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("FindDictionaryEmptyStateIndex", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeDictionaryStates", llvm, StringComparison.Ordinal);
        Assert.Contains("load i64", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("72340172838076673", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("-9187201950435737472", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("TrailingZeroCount", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("load i64", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("72340172838076673", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("-9187201950435737472", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("TrailingZeroCount", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("llvm.memset.p0.i64", initializeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceDictionaryGrowthLowersThroughSharedCapacityHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibDictionaryGrowth.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowDictionary()
                {
                    stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    stack mut u32[0 2 ** 31 - 1] index = 0;
                    while willexit (index < 9)
                    {
                        stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(index + 1);
                        if (!Ok(dictionary.Set(index, value)))
                        {
                            return false;
                        }

                        index += 1;
                    }

                    stack u32[0 2 ** 31 - 1] lookupKey = 4;
                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    return dictionary.Capacity() >= 16
                        && dictionary.TryGet(lookupKey, found)
                        && found == 5;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.Contains("ComputeHashStorageGrowthCapacity", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("ComputeContiguousGrowthCapacity", llvm.Text, StringComparison.Ordinal);
        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__u32_0_2147483647__u32_0_2147483647",
            "Expected integer Dictionary.TryGet specialization to be emitted.");
        Assert.DoesNotContain(" srem i64 ", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Equals__", tryGetBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceDictionaryCustomKeysUseExplicitStaticHashAndEqualsContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibDictionaryCustomKey.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                struct Symbol
                {
                    u32[0 max] Id;

                    static finite law u64[0 max] Hash(borrow Symbol value)
                    {
                        return (u64[0 max])value.Id;
                    }

                    static finite law bool Equals(borrow Symbol left, borrow Symbol right)
                        where overlap(left, right)
                    {
                        return left.Id == right.Id;
                    }
                }

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UseCustomKeyDictionary()
                {
                    stack mut Dictionary<Symbol, u32[0 max]> dictionary = new();
                    stack Symbol first = new()
                    {
                        Id = 7
                    };
                    stack Symbol sameKey = new()
                    {
                        Id = 7
                    };
                    stack Symbol missing = new()
                    {
                        Id = 9
                    };
                    if (!Ok(dictionary.Set(first, 41)))
                    {
                        return false;
                    }

                    stack mut u32[0 max] found = 0;
                    return dictionary.TryGet(sameKey, found)
                        && found == 41
                        && !dictionary.TryGet(missing, found);
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.Contains("@Symbol_Hash", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("@Symbol_Equals", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("call fastcc i64 @Symbol_Hash", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("call fastcc i1 @Symbol_Equals", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__Symbol", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Equals__Symbol", llvm.Text, StringComparison.Ordinal);

        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__Symbol__u32",
            "Expected custom-key Dictionary.TryGet specialization to be emitted.");
        var findIndexBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindIndex__Symbol__u32",
            "Expected custom-key Dictionary.FindIndex specialization to be emitted.");
        Assert.Contains("@Symbol_Equals", findIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Hash", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Equals", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Hash", findIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Equals", findIndexBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceTextKeysUseCompilerKnownAndOwnedStaticContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibTextKeys.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(TextKeyCollectionsProgram, appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.Contains("define internal dso_local i64 @__stark_ascii_hash(%stark_ascii %value)", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local i64 @__stark_unicode_hash(%stark_unicode %value)", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local i1 @__stark_ascii_equal(%stark_ascii %left, %stark_ascii %right)", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local i1 @__stark_unicode_equal(%stark_unicode %left, %stark_unicode %right)", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("OwnedAscii_Hash", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("OwnedAscii_Equals", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("OwnedUnicode_Hash", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("OwnedUnicode_Equals", llvm.Text, StringComparison.Ordinal);

        var asciiFindIndexBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindIndex__ascii__u32",
            "Expected ascii Dictionary.FindIndex specialization to be emitted.");
        Assert.Contains("call i64 @__stark_ascii_hash", asciiFindIndexBody, StringComparison.Ordinal);
        Assert.Contains("call i1 @__stark_ascii_equal", asciiFindIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Hash", asciiFindIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Equals", asciiFindIndexBody, StringComparison.Ordinal);

        var unicodeFindIndexBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_HashSet_FindIndex__unicode",
            "Expected unicode HashSet.FindIndex specialization to be emitted.");
        Assert.Contains("call i64 @__stark_unicode_hash", unicodeFindIndexBody, StringComparison.Ordinal);
        Assert.Contains("call i1 @__stark_unicode_equal", unicodeFindIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Hash", unicodeFindIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Equals", unicodeFindIndexBody, StringComparison.Ordinal);

        var asciiDictionaryBorrowedLookupBody =
            ExtractDefinedFunctionTextContaining(llvm.Text, "FindOwnedAsciiDictionaryIndex");
        var unicodeDictionaryBorrowedLookupBody =
            ExtractDefinedFunctionTextContaining(llvm.Text, "FindOwnedUnicodeDictionaryIndex");
        var asciiSetBorrowedLookupBody =
            ExtractDefinedFunctionTextContaining(llvm.Text, "FindOwnedAsciiHashSetIndex");
        var unicodeSetBorrowedLookupBody =
            ExtractDefinedFunctionTextContaining(llvm.Text, "FindOwnedUnicodeHashSetIndex");
        foreach (var borrowedLookupBody in new[]
        {
            asciiDictionaryBorrowedLookupBody,
            unicodeDictionaryBorrowedLookupBody,
            asciiSetBorrowedLookupBody,
            unicodeSetBorrowedLookupBody
        })
        {
            Assert.DoesNotContain("@System_Memory_Allocate(", borrowedLookupBody, StringComparison.Ordinal);
            Assert.DoesNotContain("@__stark_runtime_alloc", borrowedLookupBody, StringComparison.Ordinal);
            Assert.DoesNotContain("AppendAscii", borrowedLookupBody, StringComparison.Ordinal);
            Assert.DoesNotContain("AppendUnicode", borrowedLookupBody, StringComparison.Ordinal);
            Assert.DoesNotContain("TryReserve", borrowedLookupBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedDictionaryExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-dictionary-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedDictionaryProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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

    [Fact]
    public async Task SourceStdLibTextKeyCollectionsExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-text-key-collections-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, TextKeyCollectionsProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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

    [Fact]
    public async Task PackagedStdLibCollectionsGrowMoveDropExecutableRunsWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-collections-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, CollectionsGrowthMoveDropProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, appDirectory, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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

    private static string ExtractDefinedFunctionText(string llvm, string signaturePrefix)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, $"Expected '{signaturePrefix}' definition to be emitted.");

        var bodyStart = llvm.IndexOf('{', functionStart);
        Assert.True(bodyStart > functionStart, $"Expected '{signaturePrefix}' to include a function body.");

        var depth = 0;
        for (var index = bodyStart; index < llvm.Length; index++)
        {
            var current = llvm[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return llvm.Substring(functionStart, index - functionStart + 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected '{signaturePrefix}' body to terminate in emitted LLVM.");
    }

    private static string ExtractDefinedFunctionTextContaining(string llvm, string headerNeedle)
    {
        var searchIndex = 0;
        while (searchIndex < llvm.Length)
        {
            var functionStart = llvm.IndexOf("define ", searchIndex, StringComparison.Ordinal);
            Assert.True(functionStart >= 0, $"Expected a defined LLVM function containing '{headerNeedle}' to be emitted.");

            var bodyStart = llvm.IndexOf('{', functionStart);
            Assert.True(bodyStart > functionStart, $"Expected LLVM function containing '{headerNeedle}' to include a body.");

            var header = llvm.Substring(functionStart, bodyStart - functionStart);
            if (header.Contains(headerNeedle, StringComparison.Ordinal))
            {
                var depth = 0;
                for (var index = bodyStart; index < llvm.Length; index++)
                {
                    var current = llvm[index];
                    if (current == '{')
                    {
                        depth++;
                    }
                    else if (current == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return llvm.Substring(functionStart, index - functionStart + 1);
                        }
                    }
                }

                throw new Xunit.Sdk.XunitException(
                    $"Expected LLVM function containing '{headerNeedle}' body to terminate.");
            }

            searchIndex = bodyStart + 1;
        }

        throw new Xunit.Sdk.XunitException($"Expected a defined LLVM function containing '{headerNeedle}' to be emitted.");
    }
}
