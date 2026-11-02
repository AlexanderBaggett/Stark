# Full unmodified benchmark rerun — November 2, 2026

All **75 runnable canonical benchmarks** were rebuilt with one unchanged compiler and measured one at a time. Each case has one untimed warmup and **1,000 randomized interleaved C/Rust/Stark rounds**: **225,000 measured processes**. Two compile-only surfaces are inventoried but not timed. The README and compiler/library/benchmark sources were left unchanged.

C = 1.0000. Lower runtime ratios mean less elapsed process time. These are arithmetic means per executable; the aggregate gives each scenario equal weight using a geometric mean. No benchmark is excluded for a poor result.

| Aggregate | Ratio |
| --- | ---: |
| Stark / C, geometric mean | 0.861239 |
| Rust / C, geometric mean | 1.252668 |
| Stark / Rust, geometric mean | 0.687524 |

| Comparison | Stark >1% faster | Within ±1% | Stark >1% slower |
| --- | ---: | ---: | ---: |
| versus C | 58 | 15 | 2 |
| versus Rust | 75 | 0 | 0 |

The ±1% band is a descriptive counting rule, not a statistical equivalence test. The exact ratios and strict-lower-mean counts are in aggregate.json. The runtime-weighted ratio of sums is also saved separately; it is not substituted for the equally weighted geometric mean.

## Measurement and limitations

- Repository commit: `470497967b44343e69ff5d7a1c80a0174407c935`; Release C# bootstrap compiler SHA-256: `dd42cb704c5395d16afc6ab507bee11ff8af9836fca7567d57114b21b89f7ce3`. Both benchmark and standard library are compiled from Stark source. This is not a self-hosted-compiler comparison.
- Target: Linux x86-64, AMD Ryzen 7 5700G. CPU 0 affinity; passive cooling/temperature cap and shared-host activity apply. Exclusive host access was unconfirmed. Machine and exact tool versions/hashes are saved in machine.json.
- C: `clang -O3 -DNDEBUG -std=c17`. Rust: `rustc -C opt-level=3 -C debug-assertions=no -C overflow-checks=no`. Stark uses the ordinary fixed optimized pipeline and production ThinLTO/backend settings. No CPU-specific flags were added to one baseline.
- Builds and validation finished before each timing window. No own build, debugger or tracing overlapped timing. stdout went to the null device; stderr went to per-language regular files truncated outside the measured interval. All measured processes exited successfully, and executable hashes remained unchanged.
- UTC campaign window: 2026-11-02T14:09:09.349982+00:00 through 2026-11-02T14:49:46.071242+00:00. Individual windows/seeds are saved. The 23 cases outside the old 52-item queue ran first; all 75 appear in the aggregate.
- Peak observed build/validation process-group RSS: 1,222,451,200 bytes, under a 2 GiB guard. Managed heap limit was 1 GiB, with one compiler worker. Two standalone System.Memory fixtures were staged byte-for-byte inside a development SDK source root to satisfy namespace ownership; canonical source files and compiler code were not edited.
- Native inspection found return-only benchmark bodies in 6 Stark, 2 C and 4 Rust cases. Process startup is included; those entries do not measure steady-state kernel throughput.
- Seeds in the result files randomize language order; they do not replace program inputs. Some canonical microkernels use the native process ID, which differs per process. The same source-level workload is preserved, but those runs do not have identical bit-for-bit input seeds.
- The suite contains related microbenchmarks; equal weight per scenario is not a representative weighting of real applications.
- Canonical programs are not uniformly equivalent work: allocation lifetimes/payload checks, randomized versus custom hashing, buffering, character encoding, optimizer barriers, and constant specialization differ. Each case keeps its original semantics and checks. Treat the aggregate as an observation of these programs, not a general compiler/language ranking. See workload-notes.json and the note field in individual-results.csv.

## Individual results

| Benchmark | Stark µs | C µs | Rust µs | Stark / C | Rust / C | Stark / Rust |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| allocator/ArenaAllocator | 1877.953 | 1958.317 | 2215.667 | 0.958963 | 1.131414 | 0.847579 |
| allocator/HeapLocalBucketReuse | 586.920 | 1021.157 | 1301.547 | 0.574760 | 1.274580 | 0.450940 |
| allocator/MemoryCopyFill | 1351.547 | 1378.019 | 1664.277 | 0.980790 | 1.207731 | 0.812093 |
| allocator/MemoryDynamicReserveGrowth | 776.136 | 1047.452 | 1522.419 | 0.740975 | 1.453449 | 0.509805 |
| allocator/SystemMemoryBucketReallocate | 687.472 | 1005.670 | 1302.812 | 0.683596 | 1.295467 | 0.527683 |
| allocator/SystemMemoryFallbackReallocate | 624.705 | 1010.051 | 1377.734 | 0.618489 | 1.364025 | 0.453429 |
| collections/DictionaryInsert | 626.670 | 619.592 | 1001.411 | 1.011424 | 1.616242 | 0.625787 |
| collections/DictionaryLookup | 715.587 | 737.474 | 1822.055 | 0.970323 | 2.470672 | 0.392736 |
| collections/DictionaryMixed | 650.596 | 1362.939 | 1058.280 | 0.477348 | 0.776469 | 0.614768 |
| collections/DictionaryRemove | 589.587 | 595.151 | 941.064 | 0.990651 | 1.581220 | 0.626511 |
| collections/DictionaryUpdate | 603.486 | 609.386 | 992.189 | 0.990318 | 1.628179 | 0.608237 |
| collections/LinkedListBuildClear | 619.101 | 724.436 | 972.909 | 0.854598 | 1.342989 | 0.636340 |
| collections/LinkedListChurn | 559.456 | 555.053 | 841.988 | 1.007933 | 1.516950 | 0.664447 |
| collections/LinkedListPopOnly | 636.181 | 738.840 | 985.354 | 0.861054 | 1.333650 | 0.645637 |
| collections/LinkedListPush | 625.608 | 721.519 | 958.199 | 0.867071 | 1.328030 | 0.652900 |
| collections/LinkedListReservedPush | 629.215 | 750.760 | 990.706 | 0.838104 | 1.319604 | 0.635118 |
| collections/ListGrowth | 557.037 | 566.449 | 787.450 | 0.983384 | 1.390152 | 0.707394 |
| collections/ListIteration | 567.635 | 577.381 | 815.173 | 0.983121 | 1.411847 | 0.696337 |
| collections/QueueChurn | 644.946 | 680.744 | 840.745 | 0.947413 | 1.235039 | 0.767112 |
| collections/QueueDequeue | 662.221 | 783.823 | 909.742 | 0.844860 | 1.160647 | 0.727921 |
| collections/QueueGrowth | 562.614 | 583.112 | 795.519 | 0.964848 | 1.364266 | 0.707228 |
| collections/StackGrowth | 564.086 | 570.817 | 795.119 | 0.988208 | 1.392949 | 0.709436 |
| console/ConsoleWrites | 738.618 | 633.307 | 970.256 | 1.166287 | 1.532046 | 0.761261 |
| console/ConsoleWritesBuffered | 610.506 | 634.363 | 859.529 | 0.962392 | 1.354948 | 0.710279 |
| io/DirectoryEnumeration | 5357.960 | 5511.309 | 6795.365 | 0.972176 | 1.232986 | 0.788473 |
| io/FileBufferedReadWrite | 1858.330 | 2012.985 | 5109.626 | 0.923171 | 2.538332 | 0.363692 |
| io/FileSystemPathTranscode | 1201.249 | 1204.033 | 1574.963 | 0.997688 | 1.308074 | 0.762716 |
| micro/AbstractionGenericWrapper | 2515.840 | 3201.656 | 3066.237 | 0.785793 | 0.957703 | 0.820498 |
| micro/AbstractionHandWritten | 2521.146 | 3206.811 | 3077.122 | 0.786185 | 0.959558 | 0.819320 |
| micro/AbstractionLawWrapper | 2516.121 | 3197.475 | 3070.260 | 0.786909 | 0.960214 | 0.819514 |
| micro/AggregatePhiFieldForwarding | 2013.647 | 2484.638 | 2305.408 | 0.810439 | 0.927865 | 0.873445 |
| micro/AlgebraicIdentitySimplification | 4687.338 | 4762.785 | 5050.546 | 0.984159 | 1.060419 | 0.928085 |
| micro/Arithmetic | 1944.575 | 2232.258 | 2533.657 | 0.871125 | 1.135020 | 0.767497 |
| micro/BitwiseRangePruning | 1828.548 | 2246.283 | 2113.167 | 0.814033 | 0.940739 | 0.865312 |
| micro/BranchSelectPredication | 3732.976 | 3745.575 | 4032.367 | 0.996636 | 1.076568 | 0.925753 |
| micro/Branching | 999.200 | 1006.174 | 1173.875 | 0.993069 | 1.166672 | 0.851198 |
| micro/Calls | 1488.255 | 1835.823 | 2076.425 | 0.810674 | 1.131059 | 0.716739 |
| micro/DeadStackFieldStore | 2549.811 | 2830.513 | 3151.664 | 0.900830 | 1.113460 | 0.809036 |
| micro/DirectCallInlining | 2528.180 | 3216.485 | 3090.269 | 0.786007 | 0.960760 | 0.818110 |
| micro/ExplicitArithmeticRangePruning | 1803.958 | 2287.199 | 2136.887 | 0.788719 | 0.934281 | 0.844199 |
| micro/FactDrivenBranchPruning | 5294.026 | 5358.337 | 5710.560 | 0.987998 | 1.065734 | 0.927059 |
| micro/FunctionPointerDevirtualization | 2384.681 | 3099.461 | 3347.211 | 0.769386 | 1.079933 | 0.712438 |
| micro/GlobalScalarLoadForwarding | 1437.786 | 1628.745 | 1915.568 | 0.882757 | 1.176101 | 0.750580 |
| micro/IndependentRawPointerRegions | 1482.673 | 1490.786 | 1747.347 | 0.994558 | 1.172098 | 0.848528 |
| micro/IndependentSliceAdd | 1483.673 | 1489.445 | 1745.434 | 0.996125 | 1.171869 | 0.850031 |
| micro/MemoryAccess | 540.155 | 656.977 | 912.907 | 0.822182 | 1.389557 | 0.591686 |
| micro/NullBranchPruning | 1453.101 | 1666.613 | 1722.119 | 0.871889 | 1.033305 | 0.843787 |
| micro/PureCallGlobalForwarding | 1533.833 | 1558.928 | 1825.915 | 0.983903 | 1.171263 | 0.840036 |
| micro/ReadonlyOtherLocalFieldStore | 2111.771 | 2115.772 | 2375.828 | 0.998109 | 1.122913 | 0.888857 |
| micro/StackFieldBranchForwarding | 1826.075 | 2303.888 | 2109.308 | 0.792606 | 0.915543 | 0.865722 |
| micro/StackFieldLoadForwarding | 2297.226 | 2437.424 | 2664.771 | 0.942481 | 1.093273 | 0.862073 |
| micro/StackNestedFieldForwarding | 2306.479 | 2514.778 | 2791.420 | 0.917170 | 1.110007 | 0.826274 |
| micro/StackScalarLoadForwarding | 2291.432 | 2429.123 | 2696.025 | 0.943317 | 1.109876 | 0.849930 |
| micro/TailCalls | 1001.133 | 1031.835 | 1281.800 | 0.970245 | 1.242253 | 0.781037 |
| micro/TextLiteralLengthPruning | 3013.911 | 3516.857 | 3887.458 | 0.856990 | 1.105379 | 0.775291 |
| network/TcpLoopbackThroughput | 3369.900 | 3396.509 | 3687.384 | 0.992166 | 1.085639 | 0.913900 |
| network/TcpScatterGatherLoopback | 3410.266 | 3451.512 | 3749.230 | 0.988050 | 1.086257 | 0.909591 |
| runtime/RuntimeBufferDynamic | 771.178 | 805.900 | 1202.295 | 0.956915 | 1.491866 | 0.641421 |
| runtime/RuntimeBufferFixed | 936.861 | 948.825 | 1197.998 | 0.987390 | 1.262612 | 0.782022 |
| text/AsciiToUnicodeConversion | 546.013 | 556.327 | 803.160 | 0.981462 | 1.443684 | 0.679832 |
| text/AsciiToUnicodeConversionRuntime | 2199.595 | 3924.966 | 5650.298 | 0.560411 | 1.439579 | 0.389288 |
| text/AsciiToUnicodeConversionTinyLiteral | 541.158 | 548.360 | 788.617 | 0.986866 | 1.438137 | 0.686211 |
| text/AsciiToUnicodeLargeLiteralSpecialization | 539.802 | 550.975 | 795.622 | 0.979720 | 1.444025 | 0.678465 |
| text/AsciiToUnicodeWideningKernel | 553.271 | 557.777 | 807.626 | 0.991922 | 1.447938 | 0.685058 |
| text/ConstantIntegerFormatting | 585.671 | 2948.754 | 3224.629 | 0.198616 | 1.093556 | 0.181624 |
| text/OwnedPathAllocation | 603.788 | 666.434 | 916.561 | 0.905997 | 1.375320 | 0.658753 |
| text/OwnedTextAllocation | 680.813 | 775.841 | 1061.371 | 0.877517 | 1.368028 | 0.641447 |
| text/PathFacts | 555.491 | 589.208 | 802.065 | 0.942775 | 1.361258 | 0.692577 |
| text/PathJoin | 544.624 | 539.834 | 772.498 | 1.008871 | 1.430990 | 0.705016 |
| text/PathNormalize | 542.731 | 540.881 | 778.131 | 1.003422 | 1.438636 | 0.697481 |
| text/PathQueries | 543.397 | 547.773 | 786.017 | 0.992011 | 1.434932 | 0.691329 |
| text/PathRepeatedSmallOps | 554.155 | 551.432 | 798.493 | 1.004937 | 1.448034 | 0.694001 |
| text/TextConcatCopy | 553.945 | 559.544 | 864.903 | 0.989993 | 1.545729 | 0.640470 |
| text/TextParsing | 885.420 | 956.001 | 1212.772 | 0.926171 | 1.268589 | 0.730079 |
| text/UnicodeFormatting | 621.428 | 2959.593 | 3266.952 | 0.209971 | 1.103852 | 0.190217 |

## Saved evidence

- [individual-results.csv](individual-results.csv): 75 scenario rows, means, ratios, seeds/windows and caveats.
- [language-results.csv](language-results.csv): 225 language rows, 1,000 observations each, min/mean/median/max, coefficient of variation, file sizes and source/executable hashes.
- [raw-samples.json.gz](raw-samples.json.gz): all 225,000 measured times and the original measurement records, including before/after executable hashes.
- [aggregate.json](aggregate.json): exact aggregate calculations, counts, medians and scope.
- [machine.json](machine.json) and [manifest.json](manifest.json): compiler/toolchain/machine metadata, full inventory and unchanged-source hashes.
- [build-provenance.json.gz](build-provenance.json.gz): build/verification commands, memory reports, preflights/output hashes and fixture-staging details.
- [measurement-methods.json.gz](measurement-methods.json.gz): exact archived driver, build runner, memory guard and inspector text. The scripts record original workspace paths; adapt those paths when reproducing elsewhere.
- Untracked local native executables, initial/reference/post-LTO LLVM, disassembly and logs remain under benchmarks/codegen/full-rerun-20261102/.

[Independent verification](verification.json) and [post-run machine state](machine-after.json) are saved alongside the data.
