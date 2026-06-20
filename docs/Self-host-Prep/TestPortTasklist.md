# C# → Stark Test Port Tasklist

_Generated 2026-06-18. One `- [ ]` per C# `[Fact]`/`[Theory]` to port from `tests/` (C# xUnit) to `tests-stark/` (Stark `stark test`). Check `[x]` when the Stark port lands._

**Scope** (per request): every standard-library test; every Stark-source→LLVM-IR test; every Stark-source→parse-error test; plus other portable, target-agnostic Stark-source compiler/language tests. **Excluded** (listed at the end, with reason): tests that enforce a specific CPU/target architecture or are tightly coupled to C# host internals.

**Progress: 2637 / 2638 ported (100%).**

_Items pre-checked `[x]` were auto-detected by name: 639 of the 1314 existing Stark test functions in `tests-stark/` share an exact name with a C# test method. This is a heuristic — verify against the actual port if in doubt._

## Summary

| Category | Ported | Total |
|---|---:|---:|
| Standard library — all stdlib tests (compiler.StandardLibraryTests) | 227 | 227 |
| LLVM IR emission (Stark source -> LLVM `.ll` text) | 614 | 615 |
| Parse / syntax-error expectations (Stark source -> parse error) | 10 | 10 |
| Parsing & syntax-model shape | 12 | 12 |
| Semantic / lowering diagnostics | 391 | 391 |
| Type checking | 159 | 159 |
| Ownership & borrow validation | 243 | 243 |
| MIR (mid-level IR) lowering | 126 | 126 |
| SSA lowering, validation & optimization | 390 | 390 |
| Compiler pipeline passes | 33 | 33 |
| Runtime / native execution | 83 | 83 |
| Package image (typed compilation behavior) | 281 | 281 |
| CLI / project driver behavior | 31 | 31 |
| Example & benchmark sources | 21 | 21 |
| Other portable compiler-behavior tests | 16 | 16 |
| **Total** | **2637** | **2638** |
| _(excluded, not ported)_ |  | _122_ |

## Standard library — all stdlib tests (compiler.StandardLibraryTests)  (227/227)

### BookSampleStandardLibraryTests  (1/1)
- [x] BookSampleStandardLibraryTests::PositiveBookSamplesCompileWithStrictIntegerRanges

### StandardLibraryGenericTests  (8/8)
- [x] StandardLibraryGenericTests::StdLibSourceGraphIncludesMilestone7ModuleLayout
- [x] StandardLibraryGenericTests::StdLibSourceTreeHasNoExperimentalModules
- [x] StandardLibraryGenericTests::StdLibSourceCommonErrorResultModelUsesCompactEnumLayouts
- [x] StandardLibraryGenericTests::StdLibPackageBuildsFromRepositorySources
- [x] StandardLibraryGenericTests::PackagedStdLibCommonErrorResultModelWorksWithoutSource
- [x] StandardLibraryGenericTests::PackagedStdLibCanBeConsumedWithoutSource
- [x] StandardLibraryGenericTests::PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences
- [x] StandardLibraryGenericTests::PackagedStdLibWindowsArchiveHasNoCrtSymbolReferences

### SystemBackendBoundaryAuditTests  (1/1)
- [x] SystemBackendBoundaryAuditTests::OptimizedStandardLibraryBenchmarksDoNotEmitOpaqueStandardLibraryFunctions

### SystemCStandardLibraryTests  (2/2)
- [x] SystemCStandardLibraryTests::SystemCSourceSurfaceCompiles
- [x] SystemCStandardLibraryTests::SystemCStringHelpersWorkAtRuntime

### SystemCollectionsDictionaryStandardLibraryTests  (9/9)
- [x] SystemCollectionsDictionaryStandardLibraryTests::StdLibSourceDictionaryRawSparseStorageStaysInternalAndJustified
- [x] SystemCollectionsDictionaryStandardLibraryTests::StdLibSourcePromotedDictionaryUsesSparseRawValueStorage
- [x] SystemCollectionsDictionaryStandardLibraryTests::PromotedDictionaryLookupUsesGroupedControlByteProbe
- [x] SystemCollectionsDictionaryStandardLibraryTests::StdLibSourceDictionaryGrowthLowersThroughSharedCapacityHelper
- [x] SystemCollectionsDictionaryStandardLibraryTests::StdLibSourceDictionaryCustomKeysUseExplicitStaticHashAndEqualsContract
- [x] SystemCollectionsDictionaryStandardLibraryTests::StdLibSourceTextKeysUseCompilerKnownAndOwnedStaticContracts
- [x] SystemCollectionsDictionaryStandardLibraryTests::SourceStdLibPromotedDictionaryExecutableRuns
- [x] SystemCollectionsDictionaryStandardLibraryTests::SourceStdLibTextKeyCollectionsExecutableRuns
- [x] SystemCollectionsDictionaryStandardLibraryTests::PackagedStdLibCollectionsGrowMoveDropExecutableRunsWithoutSource

### SystemCollectionsHashSetSortStandardLibraryTests  (8/8)
- [x] SystemCollectionsHashSetSortStandardLibraryTests::StdLibSourceSortByUsesInlineComparatorWithoutRuntimeClosureOrAllocation
- [x] SystemCollectionsHashSetSortStandardLibraryTests::StdLibSourceSortUsesOrdContractWithoutRuntimeClosureOrAllocation
- [x] SystemCollectionsHashSetSortStandardLibraryTests::StdLibSourceSortRequiresOrdConformance
- [x] SystemCollectionsHashSetSortStandardLibraryTests::StdLibSourceHashSetGrowthLowersThroughDictionaryFastPath
- [x] SystemCollectionsHashSetSortStandardLibraryTests::StdLibSourceHashSetCustomKeysUseExplicitStaticHashAndEqualsContract
- [x] SystemCollectionsHashSetSortStandardLibraryTests::SourceStdLibHashSetExecutableRuns
- [x] SystemCollectionsHashSetSortStandardLibraryTests::SourceStdLibDeterministicSortingExecutableRuns
- [x] SystemCollectionsHashSetSortStandardLibraryTests::SourceStdLibPromotedCollectionsCrossFamilyParityExecutableRuns

### SystemCollectionsStackQueueStandardLibraryTests  (5/5)
- [x] SystemCollectionsStackQueueStandardLibraryTests::StdLibSourcePromotedCollectionReservesUseSparseSlotStorage
- [x] SystemCollectionsStackQueueStandardLibraryTests::SourceStdLibPromotedStackMatchesStableStackExecutableRuns
- [x] SystemCollectionsStackQueueStandardLibraryTests::SourceStdLibPromotedQueueMatchesStableQueueExecutableRuns
- [x] SystemCollectionsStackQueueStandardLibraryTests::SourceStdLibPromotedRingQueueCandidateExecutableRuns
- [x] SystemCollectionsStackQueueStandardLibraryTests::PromotedQueueTryDequeueUsesSparseSlotRingPath

### SystemCollectionsStandardLibraryTests  (7/7)
- [x] SystemCollectionsStandardLibraryTests::StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface
- [x] SystemCollectionsStandardLibraryTests::StdLibSourcePromotedCollectionsExposeDynamicComparisonTypes
- [x] SystemCollectionsStandardLibraryTests::StdLibSourcePromotedListLowersThroughDynamicStorage
- [x] SystemCollectionsStandardLibraryTests::SourceStdLibCollectionsGrowMoveDropExecutableRuns
- [x] SystemCollectionsStandardLibraryTests::SourceStdLibPromotedListMatchesStableListExecutableRuns
- [x] SystemCollectionsStandardLibraryTests::SourceStdLibPromotedLinkedListMatchesStableLinkedListExecutableRuns
- [x] SystemCollectionsStandardLibraryTests::PromotedLinkedListReserveNodesDoesNotEagerlyBuildFreeList

### SystemCompilerIntegerFactsStandardLibraryTests  (2/2)
- [x] SystemCompilerIntegerFactsStandardLibraryTests::SourceStdLibCompilerIntegerFactsExecutableRuns
- [x] SystemCompilerIntegerFactsStandardLibraryTests::PackagedStdLibCompilerIntegerFactsWorksWithoutSource

### SystemConsoleStandardLibraryTests  (5/5)
- [x] SystemConsoleStandardLibraryTests::StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads
- [x] SystemConsoleStandardLibraryTests::StdLibSourceConsoleSupportsUnicodeInputSurface
- [x] SystemConsoleStandardLibraryTests::StdLibSourceUnicodeConsoleInputWorksAtRuntime
- [x] SystemConsoleStandardLibraryTests::PackagedStdLibConsoleReturnsIoStatusWithoutSource
- [x] SystemConsoleStandardLibraryTests::PackagedStdLibUnicodeConsoleInputWorksWithoutSource

### SystemFileSystemStandardLibraryTests  (4/4)
- [x] SystemFileSystemStandardLibraryTests::SourceStdLibFileSystemMetadataTempWalkAndLineReadingWorkOnLinux
- [x] SystemFileSystemStandardLibraryTests::SourceStdLibFileSystemGlobStreamsRecursiveMatchesOnLinux
- [x] SystemFileSystemStandardLibraryTests::SourceStdLibDirectoryReadNextInfoRawReportsEntryLengthsAndEnd
- [x] SystemFileSystemStandardLibraryTests::PackagedStdLibFileSystemDirectoryLifecycleAndQueriesWorkWithoutSource

### SystemIOFilePackagedStandardLibraryTests  (5/5)
- [x] SystemIOFilePackagedStandardLibraryTests::StdLibSourceOwnedFileFlushDrainsOnlyUserBufferAndSyncAllCallsPlatformFlush
- [x] SystemIOFilePackagedStandardLibraryTests::StdLibSourceFileBufferedAsciiAppendsUseInlineAsmCopyHelper
- [x] SystemIOFilePackagedStandardLibraryTests::PackagedStdLibOwnedFileHandleFlushesAndClosesOnDrop
- [x] SystemIOFilePackagedStandardLibraryTests::PackagedStdLibFileBufferingModesBehaveAsExpected
- [x] SystemIOFilePackagedStandardLibraryTests::PackagedStdLibOwnedFileWritesHonorExplicitTextEncodings

### SystemIOFileRuntimeStandardLibraryTests  (5/5)
- [x] SystemIOFileRuntimeStandardLibraryTests::StdLibSourceLinuxFileSeekUsesLseekSyscallPath
- [x] SystemIOFileRuntimeStandardLibraryTests::StdLibSourceLinuxFileSyncUsesFsyncSyscallPath
- [x] SystemIOFileRuntimeStandardLibraryTests::SourceStdLibFileSeekRoundTripsOnLinux
- [x] SystemIOFileRuntimeStandardLibraryTests::SourceStdLibWholeFileHelpersRoundTripOnLinux
- [x] SystemIOFileRuntimeStandardLibraryTests::SourceStdLibAtomicWholeFileHelpersReplaceOnLinux

### SystemIOFileStandardLibraryTests  (7/7)
- [x] SystemIOFileStandardLibraryTests::StdLibSourceRawFileHandleHelpersStayInternal
- [x] SystemIOFileStandardLibraryTests::StdLibSourceWholeFileHelpersUseExplicitStatusAndChunkedBuffers
- [x] SystemIOFileStandardLibraryTests::StdLibSourceAtomicWholeFileHelpersUseExclusiveCreateSyncAndMove
- [x] SystemIOFileStandardLibraryTests::StdLibSourceOwnedFileHandlesSupportAsciiAndUnicodeWriteOverloads
- [x] SystemIOFileStandardLibraryTests::StdLibSourceFileSeekSurfaceCompiles
- [x] SystemIOFileStandardLibraryTests::PackagedStdLibFileMoveDeleteAndExistsRoundTrip
- [x] SystemIOFileStandardLibraryTests::PackagedStdLibUnicodeConsoleAndRawFileWritesWorkWithoutSource

### SystemIOPathPackagedStandardLibraryTests  (3/3)
- [x] SystemIOPathPackagedStandardLibraryTests::PackagedStdLibPathCurrentDirectoryFillsCallerProvidedAsciiBuffer
- [x] SystemIOPathPackagedStandardLibraryTests::PackagedStdLibPathHelpersWorkWithoutSource
- [x] SystemIOPathPackagedStandardLibraryTests::PackagedStdLibWindowsUnicodePathsCurrentDirectoryAndOwnedUnicodeWritesWorkWithoutSource

### SystemIOPathStandardLibraryTests  (7/7)
- [x] SystemIOPathStandardLibraryTests::StdLibSourcePromotedPathLowersThroughDynamicStorage
- [x] SystemIOPathStandardLibraryTests::StdLibSourcePromotedPathTryJoinUsesTailRegionPointerCopies
- [x] SystemIOPathStandardLibraryTests::StdLibSourcePromotedPathCorrectnessSurfaceCompiles
- [x] SystemIOPathStandardLibraryTests::SourceStdLibPromotedPathCorrectnessExecutableRuns
- [x] SystemIOPathStandardLibraryTests::SourceStdLibPathTempNameAndPathHelpersUseExplicitAttempts
- [x] SystemIOPathStandardLibraryTests::SourceStdLibPathGlobMatcherHandlesSegmentsAndRecursiveStars
- [x] SystemIOPathStandardLibraryTests::StagedWindowsStdLibPathHelpersUseWindowsSeparatorsAndNormalizationRules

### SystemJsonStandardLibraryTests  (2/2)
- [x] SystemJsonStandardLibraryTests::StdLibSourceJsonHelpersTypeCheckAndLower
- [x] SystemJsonStandardLibraryTests::SourceJsonParseAndWriteRoundTripExecute

### SystemMathStandardLibraryTests  (4/4)
- [x] SystemMathStandardLibraryTests::PackagedStdLibMathIntrinsicsWorkWithoutSource
- [x] SystemMathStandardLibraryTests::PackagedStdLibFusedMultiplyAddWorksWithoutSourceWhenRuntimeSupportsIt
- [x] SystemMathStandardLibraryTests::SourceStdLibMathXorShift32PseudoRandomExecutableRuns
- [x] SystemMathStandardLibraryTests::SourceStdLibMathXorShift32PseudoRandomEmitsStraightLineBitOps

### SystemMemoryContractAuditStandardLibraryTests  (1/1)
- [x] SystemMemoryContractAuditStandardLibraryTests::StdLibOverlapSensitiveApisDeclareExplicitMemoryContracts

### SystemMemoryHelperStandardLibraryTests  (8/8)
- [x] SystemMemoryHelperStandardLibraryTests::StdLibSourceMemorySurfaceCompilesAndLowersIntrinsics
- [x] SystemMemoryHelperStandardLibraryTests::StdLibSourceMemoryModuleLowersRuntimeDisjointAppendFastPaths
- [x] SystemMemoryHelperStandardLibraryTests::StdLibSourceMemoryModuleLowersHotByteTailAppendsToIntrinsics
- [x] SystemMemoryHelperStandardLibraryTests::PackagedMemoryHelpersPreserveImportedAttributes
- [x] SystemMemoryHelperStandardLibraryTests::SourceStdLibMemoryExecutableRuns
- [x] SystemMemoryHelperStandardLibraryTests::SourceStdLibMemoryCopyExecutableRuns
- [x] SystemMemoryHelperStandardLibraryTests::SourceStdLibMemoryMoveExecutableRuns
- [x] SystemMemoryHelperStandardLibraryTests::SourceStdLibMemoryAppendDisjointExecutableRuns

### SystemMemoryStandardLibraryTests  (8/8)
- [x] SystemMemoryStandardLibraryTests::StdLibSourceMemoryModuleSupportsDefaultAllocatorSurface
- [x] SystemMemoryStandardLibraryTests::StdLibSourceMemoryAllocatorBuiltinsExposeFactsThroughInlineWrappers
- [x] SystemMemoryStandardLibraryTests::StdLibSourceMemoryModuleUsesWindowsHeapAllocatorForWindowsTarget
- [x] SystemMemoryStandardLibraryTests::SourceWindowsMemoryReallocateExecutablePreservesContentsAcrossHeapAndFallbackPaths
- [x] SystemMemoryStandardLibraryTests::SourceImportedStdLibAllocatorExecutableHasNoExplicitCAllocatorSymbolReferences
- [x] SystemMemoryStandardLibraryTests::PackagedStdLibAllocatorExecutableHasNoExplicitCAllocatorSymbolReferences
- [x] SystemMemoryStandardLibraryTests::PackagedImportSystemConsoleExecutableDoesNotPullUnusedAllocatorCSymbols
- [x] SystemMemoryStandardLibraryTests::SourceImportedImportSystemConsoleExecutableDoesNotEmitUnusedMemoryObjects

### SystemNetStandardLibraryTests  (2/2)
- [x] SystemNetStandardLibraryTests::StdLibSourceNetFoundationTypesAndCompactLayoutsTypeCheck
- [x] SystemNetStandardLibraryTests::PackagedStdLibNetFoundationTypesWorkWithoutSource

### SystemNetTcpStandardLibraryTests  (10/10)
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceNetTcpClosedHandleLifecycleTypeChecks
- [x] SystemNetTcpStandardLibraryTests::PackagedStdLibNetTcpClosedHandleLifecycleWorksWithoutSource
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceNetTcpCloseRoutesOpenHandlesThroughPlatformSocketClose
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceLinuxSocketCloseUsesCloseSyscallPath
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceLinuxTcpConnectUsesSocketAndConnectSyscallPath
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceLinuxTcpShutdownUsesShutdownSyscallPath
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceLinuxTcpReadWriteUseReadAndWriteSyscallPaths
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceLinuxTcpListenUsesSocketBindAndListenSyscallPath
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceLinuxTcpAcceptUsesAccept4SyscallPath
- [x] SystemNetTcpStandardLibraryTests::StdLibSourceWindowsTcpUsesWinsockPath

### SystemProcessStandardLibraryTests  (7/7)
- [x] SystemProcessStandardLibraryTests::StdLibSourceProcessHelpersTypeCheckAndLower
- [x] SystemProcessStandardLibraryTests::StdLibSourceProcessSpawnHelpersTypeCheckAndLower
- [x] SystemProcessStandardLibraryTests::SourceProcessExitTerminatesWithRequestedExitCode
- [x] SystemProcessStandardLibraryTests::SourceProcessRunCaptureCapturesStdoutStderrExitCodeAndEnvironment
- [x] SystemProcessStandardLibraryTests::SourceProcessRunCaptureWithInputFeedsStdinAndClosesIt
- [x] SystemProcessStandardLibraryTests::SourceProcessRunCaptureTimeoutKillsChildAndKeepsPartialOutput
- [x] SystemProcessStandardLibraryTests::PackagedStdLibProcessHelpersWorkWithoutSource

### SystemPromotedConsoleStandardLibraryTests  (4/4)
- [x] SystemPromotedConsoleStandardLibraryTests::StdLibSourcePromotedConsoleSurfaceCompiles
- [x] SystemPromotedConsoleStandardLibraryTests::StdLibSourcePromotedConsoleByteAndLineWritesUseDirectPlatformPaths
- [x] SystemPromotedConsoleStandardLibraryTests::StdLibSourceRuntimePlatformLineWritesCoalesceSmallBuffers
- [x] SystemPromotedConsoleStandardLibraryTests::SourceStdLibPromotedConsoleExecutableRuns

### SystemPromotedIOFileSystemStandardLibraryTests  (3/3)
- [x] SystemPromotedIOFileSystemStandardLibraryTests::StdLibSourcePromotedIOFileSystemSurfaceCompiles
- [x] SystemPromotedIOFileSystemStandardLibraryTests::SourceStdLibPromotedFileExecutableRuns
- [x] SystemPromotedIOFileSystemStandardLibraryTests::SourceStdLibPromotedFileSystemExecutableRuns

### SystemPromotedNetTcpStandardLibraryTests  (2/2)
- [x] SystemPromotedNetTcpStandardLibraryTests::StdLibSourcePromotedNetTcpSurfaceCompiles
- [x] SystemPromotedNetTcpStandardLibraryTests::StdLibSourcePromotedNetTcpBufferReadsUseBulkPaths

### SystemPromotedRuntimeBufferStandardLibraryTests  (5/5)
- [x] SystemPromotedRuntimeBufferStandardLibraryTests::StdLibSourcePromotedRuntimeBufferSurfaceCompiles
- [x] SystemPromotedRuntimeBufferStandardLibraryTests::StdLibSourcePromotedRuntimeBufferModuleLowersRuntimeDisjointWriteFastPaths
- [x] SystemPromotedRuntimeBufferStandardLibraryTests::StdLibSourcePromotedRuntimeBufferModuleUsesTailRegionMemoryHelpers
- [x] SystemPromotedRuntimeBufferStandardLibraryTests::StdLibSourcePromotedRuntimeBufferFixedBuffersUseInlineStorage
- [x] SystemPromotedRuntimeBufferStandardLibraryTests::SourceStdLibPromotedRuntimeBufferExecutableRuns

### SystemRangeNotationStandardLibraryTests  (1/1)
- [x] SystemRangeNotationStandardLibraryTests::StdLibSourceUsesCanonicalRangeNotation

### SystemRawPointerAuditStandardLibraryTests  (3/3)
- [x] SystemRawPointerAuditStandardLibraryTests::StdLibRawPointerUseStaysInDocumentedBoundaryFiles
- [x] SystemRawPointerAuditStandardLibraryTests::StdLibPublicRawPointerSurfaceStaysExplicitlyUnsafeAndAllowlisted
- [x] SystemRawPointerAuditStandardLibraryTests::StdLibRootDoesNotReExportPublicRawPointerSurfaceModules

### SystemRuntimeBufferStandardLibraryTests  (2/2)
- [x] SystemRuntimeBufferStandardLibraryTests::StdLibSourceRuntimeBufferModuleSupportsLinearAndRingOperations
- [x] SystemRuntimeBufferStandardLibraryTests::SourceRuntimeBufferModuleCanExecuteLinearAndRingOperations

### SystemRuntimePlatformLinuxStandardLibraryTests  (14/14)
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceLinuxFileOperationsUseSyscallBackedPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceLinuxFileExistsUsesStatSyscallPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::SourceStdLibBuildRoutesPlatformCallsThroughLinuxModuleForLinuxTargets
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceLinuxProcessHelpersUseSyscallBackedPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceLinuxEventWaitingUsesEpollSyscallPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::SourceRuntimePlatformWaitWritableUsesLinuxEpoll
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::StdLibSourceLinuxFutexSynchronizationUsesSyscallPath
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::SourceRuntimePlatformFutexWaitWakeUseLinuxSyscall
- [x] SystemRuntimePlatformLinuxStandardLibraryTests::SourceRuntimePlatformProcessExitUsesLinuxExitGroup

### SystemRuntimePlatformMacOSStandardLibraryTests  (8/8)
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::MacOSDispatchTemplateMirrorsLinuxDispatchSurface
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::StdLibSourceMacOSPlatformUsesLibSystemPosixApis
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::StdLibSourceMacOSPathMetadataUsesStatModeBits
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::StdLibSourceMacOSThreadingPreservesEntryReturnCodeThroughPthreadJoin
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::MacOSDispatchProcessExitCallsLibSystemExitWithoutSymbolCollision
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::SourceStdLibBuildRoutesPlatformCallsThroughMacOSModuleForMacOSTargets
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::SourceStdLibBuildRoutesProcessSpawnThroughMacOSModuleForMacOSTargets
- [x] SystemRuntimePlatformMacOSStandardLibraryTests::MacOSRuntimeAllocatorUsesMallocReallocAndFreeForAppleTargets

### SystemRuntimePlatformWindowsStandardLibraryTests  (16/16)
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsConsoleAndFileOperationsUseWin32Apis
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsWidePathCopiesUseInlineAsmHelper
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StagedWindowsStdLibBuildRoutesPlatformCallsThroughWindowsModule
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::RootWindowsStdLibCompileKeepsWriteBufferToHandleOnDirectMirPath
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsConsoleInputOutputUsesKernel32Apis
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsFilePrimitivesUseKernel32Apis
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsDirectoryAndMetadataUseKernel32Apis
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsPathBehaviorUsesWideNormalizationRules
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::StdLibSourceWindowsProcessHelpersUseKernel32Apis
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::WindowsDispatchProcessExitCallsKernelImportWithoutSymbolCollision
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::PackagedStdLibWindowsTargetCanBeConsumedWithoutSource
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::SourceWindowsConsoleExecutableWritesRedirectedOutputAndErrors
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::SourceWindowsProcessCaptureRunsCmdWithEnvironmentAndInput
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::SourceWindowsDirectoryEnumerationProbeCompilesAsciiUnicodeAndLongNameChecks
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::SourceWindowsPromotedDirectoryEnumerationExecutablePreservesCachedFirstEntry
- [x] SystemRuntimePlatformWindowsStandardLibraryTests::WindowsDispatchTemplateMirrorsLinuxDispatchSurface

### SystemSyscallStandardLibraryTests  (3/3)
- [x] SystemSyscallStandardLibraryTests::SystemSyscallDirectEntryPointsAreInternal
- [x] SystemSyscallStandardLibraryTests::PackagedStdLibLinuxPlatformSyscallsRemainUsableWithoutPublicSyscallSource
- [x] SystemSyscallStandardLibraryTests::SystemSyscallModuleSelectsExpectedLinuxShimPerArchitecture

### SystemTestingStandardLibraryTests  (3/3)
- [x] SystemTestingStandardLibraryTests::StdLibSourceTestingHelpersCompileWithExplicitFactRunner
- [x] SystemTestingStandardLibraryTests::SourceStdLibTestingRichAssertionsExecutableRuns
- [x] SystemTestingStandardLibraryTests::StdLibTestingModuleStaysRawPointerFreeAndExplicit

### SystemTextInterningStandardLibraryTests  (3/3)
- [x] SystemTextInterningStandardLibraryTests::StdLibSourceTextInterningSurfaceCompiles
- [x] SystemTextInterningStandardLibraryTests::StdLibSourceTextInternerIdsAreNominallyDistinct
- [x] SystemTextInterningStandardLibraryTests::SourceStdLibTextInterningExecutableRuns

### SystemTextRuntimeStandardLibraryTests  (3/3)
- [x] SystemTextRuntimeStandardLibraryTests::SourceStdLibPromotedTextExecutableRuns
- [x] SystemTextRuntimeStandardLibraryTests::SourceStdLibTextLiteralEscapingExecutableRuns
- [x] SystemTextRuntimeStandardLibraryTests::SourceImportedStdLibTryFormatExecutableWritesText

### SystemTextStandardLibraryTests  (7/7)
- [x] SystemTextStandardLibraryTests::StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile
- [x] SystemTextStandardLibraryTests::StdLibSourcePromotedTextLowersThroughDynamicStorage
- [x] SystemTextStandardLibraryTests::StdLibSourcePromotedTextAppendsUseTailRegionMemoryHelpers
- [x] SystemTextStandardLibraryTests::StdLibSourcePromotedWideUnicodeIntegerFormattingWritesUnicodeDirectly
- [x] SystemTextStandardLibraryTests::StdLibSourcePromotedWideIntegerParsingUsesConstantCutoffs
- [x] SystemTextStandardLibraryTests::StdLibSourcePromotedTextEncodingHelpersUseBoundedRawPointerRegions
- [x] SystemTextStandardLibraryTests::PackagedStdLibTryFormatSurfaceCanBeConsumedWithoutSource

### SystemThreadingAtomicsStandardLibraryTests  (12/12)
- [x] SystemThreadingAtomicsStandardLibraryTests::AtomicI64SingleThreadedOperationSemanticsAreExactAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::AtomicI64TwoThreadCounterIsExactAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::AtomicBoolFlagHandoffAcrossThreadsWorksAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::AllTier1AtomicWidthsHaveExactSemanticsAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::Tier2ContainerAtomicWidthsHaveExactSemanticsAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::Tier2AtomicTwoThreadCountersAreExactAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::Tier2ContainerAtomicsLowerToLockFreeContainerInstructions
- [x] SystemThreadingAtomicsStandardLibraryTests::PackagedStdLibAtomicsWorkWithoutSource
- [x] SystemThreadingAtomicsStandardLibraryTests::Tier3EmbeddedLockAtomicWidthsHaveExactSemanticsAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::Tier3EmbeddedLockAtomicTwoThreadCounterIsExactAtRuntime
- [x] SystemThreadingAtomicsStandardLibraryTests::Tier3EmbeddedLockAtomicsLowerToSpinlockProtectedOperations
- [x] SystemThreadingAtomicsStandardLibraryTests::AtomicI64Tier1BuiltinsLowerToSingleAtomicInstructions

### SystemThreadingStandardLibraryTests  (17/17)
- [x] SystemThreadingStandardLibraryTests::PackagedStdLibThreadingEntrySchedulerAndThreadLifecycleWorkWithoutSource
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSurfaceSupportsThreadEntryAndSchedulerCalls
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSurfaceSupportsSynchronizedGuardedState
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSynchronizedIsShareableWhenPayloadIsTransferable
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSynchronizedShareabilityRequiresTransferablePayload
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSurfaceRejectsProtectedBorrowEscapingLockedGuard
- [x] SystemThreadingStandardLibraryTests::SystemThreadingSynchronizedGuardsSharedMutableStateAtRuntime
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSurfaceSupportsMpscChannels
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingChannelHandlesCarryThreadSafetyLawFacts
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingChannelSendRequiresTransferablePayload
- [x] SystemThreadingStandardLibraryTests::SystemThreadingChannelMovesMessagesAndObservesCloseAtRuntime
- [x] SystemThreadingStandardLibraryTests::SystemThreadingChannelHandlesContendedProducersAtRuntime
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingSurfaceSupportsPayloadThreadStarts
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadPayloadStartRequiresTransferablePayload
- [x] SystemThreadingStandardLibraryTests::StdLibSourceThreadingErrorEnumsUseCompactLayouts
- [x] SystemThreadingStandardLibraryTests::StdLibSourceLinuxThreadingUsesRawCloneLifecycleAndSyscallBackedScheduler
- [x] SystemThreadingStandardLibraryTests::StdLibSourceWindowsThreadingUsesWin32LifecycleAndSchedulerCalls

## LLVM IR emission (Stark source -> LLVM `.ll` text)  (614/615)

### AddressOfByValueCopyableParameterRegressionTests  (1/1)
- [x] AddressOfByValueCopyableParameterRegressionTests::InliningAMultiBlockBorrowOfAByValueCopyableParameterValidates

### BenchmarkSourceTests  (7/7)
- [x] BenchmarkSourceTests::BenchmarkSourcesCompile
- [x] BenchmarkSourceTests::MemoryCopyFillHotLoopUsesInfallibleHelpers
- [x] BenchmarkSourceTests::WindowsDirectoryEnumerationUsesAllocationFreeInfoPath
- [x] BenchmarkSourceTests::DirectoryEnumerationDoesNotExposeLargeDirectoryPayloadAsSsaValue
- [x] BenchmarkSourceTests::FileOpenDoesNotExposeLargeFilePayloadAsSsaValue
- [x] BenchmarkSourceTests::DictionaryInsertDropFieldUpdatesDoNotStoreDeferredAggregateValues
- [x] BenchmarkSourceTests::TextFormattingBenchmarksSpecializeConstantIntegerFormatting

### BorrowingFeatureTests  (1/1)
- [x] BorrowingFeatureTests::BorrowParametersPreserveReadonlyPointerAbiThroughTheWholePipeline

### CommentTriviaTests  (1/1)
- [x] CommentTriviaTests::LineBlockAndXmlCommentsAreIgnoredBeforeLowering

### CompilerPipelineEmitLlvmTests  (23/23)
- [x] CompilerPipelineEmitLlvmTests::SourceBackedImportedInlineFunctionsEmitInternalLlvmBodyClones
- [x] CompilerPipelineEmitLlvmTests::SourceBackedImportedInlineFunctionsDeclareModulePrivateConstsUsedByClone
- [x] CompilerPipelineEmitLlvmTests::SourceBackedImportedInlineFunctionsCloneModulePrivateCalleeDependencies
- [x] CompilerPipelineEmitLlvmTests::SourceBackedImportedInlineCloneSeedsRestrictEmissionToReachableRootPath
- [x] CompilerPipelineEmitLlvmTests::SourceBackedImportedNoInlineFunctionsStayAbiDeclarations
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedColdNoInlineGenericInstantiationsPreserveTypedInterfaceModifiersWithoutCompilerFacts
- [x] CompilerPipelineEmitLlvmTests::PackageBackedImportedGenericInlineBodiesEmitReachableInlineClonesFromTypedFacts
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedImportedGenericSpecializationsUseTemplateSemanticAttributesWhenFunctionSemanticsAreMissing
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedImportedGenericSpecializationsPreserveRegionFactsWithoutSourceBodies
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedImportedPlainFnGenericsThatStrengthenToLawInlineWhenTemplateSemanticsSurviveWithoutFunctionSemantics
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedMemberCallForwarderGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedFieldAccessWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedIndexAccessWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedConversionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedAddressOfWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedBinaryOperatorWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedComparisonWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedTerminalIfSelectionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedTerminalSwitchSelectionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedObjectConstructionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedEnumConstructionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ManifestBackedLocalUpdateWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen
- [x] CompilerPipelineEmitLlvmTests::ConstGlobalDerivedLoadsEmitInvariantLoadMetadataWithoutTaggingLocalLoads

### CompilerPipelineFullIntegrationTests  (10/10)
- [x] CompilerPipelineFullIntegrationTests::PipelineFoldsImportedConstantLawCallsAcrossMirSsaAndLlvm
- [x] CompilerPipelineFullIntegrationTests::NestedAggregateRuntimeDropsDoNotTriggerUnsupportedMirLowering
- [x] CompilerPipelineFullIntegrationTests::NestedInitializersEmitLlvmWithoutUnsupportedLoweringLogs
- [x] CompilerPipelineFullIntegrationTests::ReadonlyScalarArrayGlobalsCanUseVectorizationFriendlyAlignment
- [x] CompilerPipelineFullIntegrationTests::SupportedComparisonFamiliesEmitLlvmWithoutUnsupportedLoweringLogs
- [x] CompilerPipelineFullIntegrationTests::ExpressionStatementsEmitLlvmWithoutUnsupportedLoweringLogs
- [x] CompilerPipelineFullIntegrationTests::NestedPlaceUpdatesEmitLlvmWithoutUnsupportedLoweringLogs
- [x] CompilerPipelineFullIntegrationTests::HeapAllocatorLoweringAvoidsLlvmFallbackLogs
- [x] CompilerPipelineFullIntegrationTests::UnusedTypeAliasesDoNotBlockTheCurrentPipeline
- [x] CompilerPipelineFullIntegrationTests::InternalEnumsDeriveDirectTagLayoutsAndFlowThroughTheFullPipeline

### CompilerPipelineOptimizeSsaTests  (1/1)
- [x] CompilerPipelineOptimizeSsaTests::AsciiToUnicodeLiteralSpecializationHandlesLoopBackedgePhiSuccessors

### CompilerPipelineSyntaxModelTests  (1/1)
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueCallableEmitsLlvmOptimizationBoundary

### ComptimeFeatureTests  (85/85)
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesFiniteLawCallAndLowersToImmediate
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesFiniteFunctionCallAndLowersToImmediate
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesGenericFiniteFunctionCall
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesComptimeGenericArithmeticAfterSpecialization
- [x] ComptimeFeatureTests::ComptimeGenericFiniteCallPreservesSymbolicValueUntilSpecialization
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesExplicitNumericConversions
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesExplicitNumericConversions
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesTrySuccessPath
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesTrySameErrorEarlyReturn
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesTryFromFunnelEarlyReturn
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesTryUnitStatusPropagation
- [x] ComptimeFeatureTests::ComptimeExpressionEvaluatesStaticFiniteMethodCall
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesReceiverFiniteMethodCall
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesReceiverMethodCallOnCallResult
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesReceiverMethodChain
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesTraitDefaultReceiverMethodCall
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesGenericTraitDefaultReceiverMethodCall
- [x] ComptimeFeatureTests::ComptimeExpressionCanInferConstStorage
- [x] ComptimeFeatureTests::ComptimeExpressionCanMaterializeFixedArrayConstGlobal
- [x] ComptimeFeatureTests::ComptimeFixedArrayExpressionCanLowerToRuntimeAggregate
- [x] ComptimeFeatureTests::ComptimeExpressionCanMaterializeNamedAggregateForFieldProjection
- [x] ComptimeFeatureTests::ComptimeBlockCanInitializeNamedAggregateConstGlobal
- [x] ComptimeFeatureTests::ComptimeBlockCanBuildNestedNamedAggregateAndFixedArray
- [x] ComptimeFeatureTests::ComptimeExpressionCanUseRecordPrimaryConstructor
- [x] ComptimeFeatureTests::ComptimeExpressionCanReadLocalNamedAggregateConst
- [x] ComptimeFeatureTests::ComptimeExpressionCanMaterializeTupleEnumPayload
- [x] ComptimeFeatureTests::ComptimeBlockCanInitializeNamedFieldEnumConstGlobal
- [x] ComptimeFeatureTests::ComptimeBlockCanExecuteSwitchWithEnumPatternsCapturesAndGuards
- [x] ComptimeFeatureTests::ComptimeBlockCanExecuteSwitchWithAggregateListRangeOrAndDefaultPatterns
- [x] ComptimeFeatureTests::ComptimeBlockCanExecutePatternIfConditions
- [x] ComptimeFeatureTests::ComptimeBlockCanExecutePatternWhileConditions
- [x] ComptimeFeatureTests::ComptimeBlockCanBranchOnUnitEnumEquality
- [x] ComptimeFeatureTests::ComptimeExpressionCanFoldStructLayoutFacts
- [x] ComptimeFeatureTests::ComptimeBlockCanFoldEnumLayoutFacts
- [x] ComptimeFeatureTests::ComptimeLawCallCanFoldLayoutFacts
- [x] ComptimeFeatureTests::ComptimeLayoutFactsCanInitializeConstGlobal
- [x] ComptimeFeatureTests::ComptimeStructuralFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeVisibilityStructuralFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeTypeQualifierMetadataFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeCallableQualifierMetadataFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeFieldAndEnumPayloadQualifierMetadataFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeStructuralCountFactsFoldToIntegerConstants
- [x] ComptimeFeatureTests::GenericComptimeLawCanBranchOnStructuralCountFacts
- [x] ComptimeFeatureTests::ComptimeTypeLayoutFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeScalarTypeMetadataFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeRawPointerMetadataFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeTypeElementMetadataFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeIndexedFieldLayoutFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeEnumTagFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeEnumPayloadLayoutFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeStructLayoutAttributeFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeThreadSafetyLawAttributeFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeMethodThreadSafetyLawPredicateFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeIndexedEnumVariantFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeIndexedTypeFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeFieldAndPayloadTypePredicateFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeFieldAndPayloadTypeMetadataFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeFunctionPointerStructuralFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeCallableNestedTypeArgumentStructuralFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeFunctionPointerMemoryContractFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeCallableRawPointerElementCountExpressionFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeClosureStructuralFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeClosureMemoryContractFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeIndexedNameFactsFoldToTextConstants
- [x] ComptimeFeatureTests::ComptimeEnumPayloadNameFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeEnumVariantFunnelFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeTypeGenericParameterFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeTypeComptimeArgumentFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeMethodStructuralFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeAssociatedTypeFactsFoldToConstants
- [x] ComptimeFeatureTests::ComptimeTraitConformanceFactFoldsToBoolConstant
- [x] ComptimeFeatureTests::ComptimeDynTraitStructuralFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::ComptimeDynTraitNestedTypePredicateFactsFoldToBoolConstants
- [x] ComptimeFeatureTests::GenericComptimeLawCanBranchOnTraitConformanceFact
- [x] ComptimeFeatureTests::GenericComptimeLawCanBranchOnStructuralFactsAndLayout
- [x] ComptimeFeatureTests::ComptimeBlockEvaluatesLocalStateAndOuterConst
- [x] ComptimeFeatureTests::ComptimeBlockCanInitializeTypedConstGlobal
- [x] ComptimeFeatureTests::ComptimeBlockCanMaterializeFixedArrayForIndexing
- [x] ComptimeFeatureTests::ComptimeBlockCanGenerateFixedArrayTableWithWillexitForLoop
- [x] ComptimeFeatureTests::ComptimeBlockCanExecuteIndexedForInTraversal
- [x] ComptimeFeatureTests::ComptimeBlockCanExecuteMutableForInTraversalWithNestedPlaceUpdates
- [x] ComptimeFeatureTests::ComptimeBlockCanUseWillexitWhileBreakContinueAndCompoundAssignment
- [x] ComptimeFeatureTests::ComptimeBlockCanExecuteLabeledBreakAndContinue
- [x] ComptimeFeatureTests::ComptimeStructuralFactsExposeCSourceAliasIdentity
- [x] ComptimeFeatureTests::ComptimeStructuralFactsExposeTypeAndMethodModuleNames

### FixedArrayOrderedComparisonEmissionTests  (1/1)
- [x] FixedArrayOrderedComparisonEmissionTests::FixedArrayOrderedComparisonsEmitLexicographicHelperCalls

### FloatingPointFeatureTests  (1/1)
- [x] FloatingPointFeatureTests::ConstantFloatExpressionsFoldThroughTheWholePipeline

### FunctionClassesFeatureTests  (1/1)
- [x] FunctionClassesFeatureTests::FunctionClassesFlowThroughTheWholePipelineAndPreserveTheirLlvmShapes

### FunctionKindsFeatureTests  (1/1)
- [x] FunctionKindsFeatureTests::LawFunctionEmitsLawShapedLlvmAttributes

### GenericUseSiteInstantiationRegressionTests  (1/1)
- [x] GenericUseSiteInstantiationRegressionTests::ImportedSourceOnlyInstantiationsMaterializeFromBodyUseSites

### GenericsFeatureTests  (8/8)
- [x] GenericsFeatureTests::ExplicitComptimeGenericFunctionArgumentMaterializesAsConstant
- [x] GenericsFeatureTests::ComptimeGenericValueArgumentsParticipateInFunctionIdentity
- [x] GenericsFeatureTests::ValueOnlyComptimeGenericFunctionArgumentMaterializesAsConstant
- [x] GenericsFeatureTests::GenericEnumMonomorphizationEmitsConcreteStructType
- [x] GenericsFeatureTests::GenericRecordMonomorphizationEmitsConcreteFields
- [x] GenericsFeatureTests::TwoDistinctInstantiationsOfSameGenericEmitTwoTypes
- [x] GenericsFeatureTests::OpenGenericFunctionTemplatesDoNotEmitRuntimeAbiDeclarations
- [x] GenericsFeatureTests::LargeByValueGenericSpecializationsPreserveObservableMemoryFacts

### IntegerFeatureTests  (1/1)
- [x] IntegerFeatureTests::ConstantIntegerExpressionsFoldThroughTheWholePipeline

### LlvmEmitterConversionTests  (12/12)
- [x] LlvmEmitterConversionTests::FloatToIntegerConversionEmitsFptosi
- [x] LlvmEmitterConversionTests::IntegerToRawPointerConversionEmitsInttoptr
- [x] LlvmEmitterConversionTests::RawPointerToIntegerConversionEmitsPtrtoint
- [x] LlvmEmitterConversionTests::RawPointerToRawPointerConversionIsLlvmNoOp
- [x] LlvmEmitterConversionTests::SameWidthIntegerConversionIsEmittedAsAlias
- [x] LlvmEmitterConversionTests::SameWidthFloatConversionIsEmittedAsAlias
- [x] LlvmEmitterConversionTests::FloatUseRValueIsEmittedAsAlias
- [x] LlvmEmitterConversionTests::RawPointerUseRValueIsEmittedAsAlias
- [x] LlvmEmitterConversionTests::AggregateUseRValueIsEmittedAsAlias
- [x] LlvmEmitterConversionTests::AsciiToUnicodeReinterpretCastIsNotLowered
- [x] LlvmEmitterConversionTests::UnicodeToAsciiReinterpretCastIsNotLowered
- [x] LlvmEmitterConversionTests::SourceBodyFallbackDeclarationCanBeStrictlyOmitted

### LlvmIrEmissionTests  (411/412)
- [x] LlvmIrEmissionTests::StraightLineFunctionEmitsOptimizedLlvmBody
- [x] LlvmIrEmissionTests::DynamicRawPointerStorageSliceLowersToSlicePairInLocalAndArgumentPositions
- [x] LlvmIrEmissionTests::DynamicRawPointerStorageSliceReassignmentLowersToSlicePair
- [x] LlvmIrEmissionTests::FunctionPointerCallsEmitFastccIndirectCall
- [x] LlvmIrEmissionTests::DynTraitObjectDispatchLoadsVtableSlotAndCallsIndirectly
- [x] LlvmIrEmissionTests::NonCapturingClosureValuesLowerToInvokeAndEnvironmentPair
- [x] LlvmIrEmissionTests::OptimizedFunctionItemClosurePromotionDevirtualizesAndPrunesAdapter
- [x] LlvmIrEmissionTests::CapturingClosureCopyLowersThroughEnvironmentStorage
- [x] LlvmIrEmissionTests::MutCapturingClosureWritesThroughCapturedAddress
- [x] LlvmIrEmissionTests::HeapCapturingClosureAllocatesHeapEnvironmentAndClearsNoFreeAttributes
- [x] LlvmIrEmissionTests::HeapClosureMoveCaptureDropReleasesOwnedFieldsBeforeEnvironment
- [x] LlvmIrEmissionTests::OnceHeapClosureMoveCaptureTransfersOwnedFieldAndFreesEnvironment
- [x] LlvmIrEmissionTests::OnceHeapClosureDropsUnmovedOwnedCapturesBeforeFreeingEnvironment
- [x] LlvmIrEmissionTests::OptimizedInlineClosureCallSpecializesAwayRuntimeClosure
- [x] LlvmIrEmissionTests::OptimizedInlineClosureWithNamedBorrowParameterSpecializesAwayRuntimeClosure
- [x] LlvmIrEmissionTests::OptimizedInlineClosureThroughNestedWrapperSpecializesAwayRuntimeClosure
- [x] LlvmIrEmissionTests::OptimizedInlineClosureInsideControlFlowSpecializesAwayRuntimeClosure
- [x] LlvmIrEmissionTests::OptimizedMutableInlineClosureSpecializesAwayRuntimeClosure
- [x] LlvmIrEmissionTests::OptimizedOnceInlineClosureSpecializesAwayRuntimeClosure
- [x] LlvmIrEmissionTests::FiniteLawFunctionPointerCallsEmitIndirectCallEffectAttributes
- [x] LlvmIrEmissionTests::FunctionPointerCallSiteEffectAttributesFollowPointerKind
- [x] LlvmIrEmissionTests::UnusedPlainFunctionPointerCallsAreNotRemovedBySsaCleanup
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithFiniteKnownTargetSetsEmitCalleesMetadata
- [x] LlvmIrEmissionTests::ClosureFunctionItemLocalDevirtualizesAndInlinesAdapterAtO3
- [x] LlvmIrEmissionTests::ClosureCallsWithFiniteKnownTargetSetsEmitCalleesMetadata
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithOpaqueTargetsDoNotEmitCalleesMetadata
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithSingletonKnownTargetExpressionsBecomeDirectCalls
- [x] LlvmIrEmissionTests::LawFunctionPointerCallsWithBorrowParametersEmitReadonlyMemoryEffects
- [x] LlvmIrEmissionTests::FunctionPointerCallSiteEffectAttributesComposeWithOutAndAggregateAbi
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithBorrowParametersUsePointerAbi
- [x] LlvmIrEmissionTests::FunctionPointerReturnsEmitNonnullAbiAttribute
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithOutParametersUseCallerStorage
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithInitParametersUseCallerStorage
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithRawPointerParametersEmitNoAliasCallAttributes
- [x] LlvmIrEmissionTests::FunctionPointerOverlapContractsSuppressIndirectNoAliasCallAttributes
- [x] LlvmIrEmissionTests::FunctionPointerSameContractsSuppressIndirectNoAliasBetweenSameArguments
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithLargeByValueParametersUseByvalAbi
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithLargeReturnValuesUseSretAbi
- [x] LlvmIrEmissionTests::DirectVoidStatementCallsWithOutParametersUseCallerStorage
- [x] LlvmIrEmissionTests::DirectCallsWithLargeByValueTemporaryConstructDirectlyIntoByvalCallSlot
- [x] LlvmIrEmissionTests::DirectCallsWithLargeByValueCurrentParameterUseByvalParameterPointer
- [x] LlvmIrEmissionTests::DynamicStorageAllocationIndexInitAndDropEmitRuntimeAllocatorCalls
- [x] LlvmIrEmissionTests::DynamicStorageDropEmitsElementDestructorLoopBeforeFree
- [x] LlvmIrEmissionTests::DynamicStorageElementLoadsAndStoresCarryElementAlignment
- [x] LlvmIrEmissionTests::DynamicStorageReserveEmitsDirectReallocatePath
- [x] LlvmIrEmissionTests::OptimizedDynamicStorageReserveNoopElidesRuntimeReservePath
- [x] LlvmIrEmissionTests::OptimizedDynamicStorageZeroCapacityDropElidesRuntimeFreePath
- [x] LlvmIrEmissionTests::DynamicStorageTryReserveEmitsDirectFallibleReallocatePath
- [x] LlvmIrEmissionTests::OptimizedDynamicStorageTryReserveSuccessPrunesImpossibleCapacityBranch
- [x] LlvmIrEmissionTests::DynamicStorageTryReserveCapacityEmitsExactFallibleReallocatePath
- [x] LlvmIrEmissionTests::DynamicStorageInitSliceWritesCommitOwnerLength
- [x] LlvmIrEmissionTests::OptimizedDynamicStorageInitSlicePreservesPointerDefinitionAndAlignment
- [x] LlvmIrEmissionTests::InitSliceParametersKeepReadableSliceHeaderContract
- [x] LlvmIrEmissionTests::InitSliceElementStoresDoNotReadDestinationElementBeforeWrite
- [x] LlvmIrEmissionTests::DynamicStorageMoveLastEmitsDirectLengthUpdateAndLoad
- [x] LlvmIrEmissionTests::OptimizedDynamicStorageMoveLastSkipsEmptyCheckWhenPrefixFactsProveNonEmpty
- [x] LlvmIrEmissionTests::DynamicStorageMoveAtEmitsDirectLengthUpdateLoadAndMemmove
- [x] LlvmIrEmissionTests::OptimizedDynamicStorageMoveAtSkipsBoundsCheckWhenPrefixFactsProveInBounds
- [x] LlvmIrEmissionTests::LargeDynamicStorageMoveLastIntoLocalUsesDestinationSlot
- [x] LlvmIrEmissionTests::LargeDynamicStorageMoveLastReturnCopiesDirectlyIntoSRetBuffer
- [x] LlvmIrEmissionTests::LargeDynamicStorageMoveAtReturnCopiesBeforeTailShift
- [x] LlvmIrEmissionTests::LargeDynamicStorageMoveLastIntoOutParameterUsesOutStorage
- [x] LlvmIrEmissionTests::LargeDynamicStorageMoveAtIntoOutParameterCopiesBeforeTailShift
- [x] LlvmIrEmissionTests::LargeDynamicStorageMoveLastIntoGlobalUsesGlobalStorage
- [x] LlvmIrEmissionTests::UnsafeDynamicStorageNonTailMoveEmitsDirectElementLoad
- [x] LlvmIrEmissionTests::AggregateLoadedFromDynamicSlotKeepsSnapshotAfterSlotReplacement
- [x] LlvmIrEmissionTests::SmallRangeUnsignedIntegerOperationsUseUnsignedLlvmOpcodes
- [x] LlvmIrEmissionTests::NonCapturingLambdaFunctionPointersEmitSyntheticFunctionDefinitions
- [x] LlvmIrEmissionTests::NonCapturingLambdaArgumentsLowerToFunctionPointers
- [x] LlvmIrEmissionTests::GenericSizeofAndAlignofLowerAfterConcreteInstantiation
- [x] LlvmIrEmissionTests::DebugMetadataIsEmittedForFunctionsParametersAndStackLocals
- [x] LlvmIrEmissionTests::DebugMetadataMarksBuildsAsOptimized
- [x] LlvmIrEmissionTests::ConstantBranchConditionsCanFoldAllTheWayToReturn
- [x] LlvmIrEmissionTests::BranchJoinCanOptimizeToDirectReturns
- [x] LlvmIrEmissionTests::IfWeightAnnotationEmitsBranchWeightMetadata
- [x] LlvmIrEmissionTests::SwitchWeightAnnotationEmitsDistributedBranchWeightMetadata
- [x] LlvmIrEmissionTests::ColdCallBranchTargetsInferUnlikelyBranchWeightMetadata
- [x] LlvmIrEmissionTests::GlobalsUseVisibilityAwareLinkageAndConstantKinds
- [x] LlvmIrEmissionTests::MutableGlobalsEmitRealDefinitionsStoresAndLoads
- [x] LlvmIrEmissionTests::MutableGlobalLoadsAreNotForwardedAcrossDestructorBackedCalls
- [x] LlvmIrEmissionTests::LibraryBuildQualifiesRootGlobalSymbolsAndPreservesExportNames
- [x] LlvmIrEmissionTests::ExecutableInternalizationCanMakeRootModulePrivateFunctionsLocal
- [x] LlvmIrEmissionTests::RootFunctionSymbolIsQualifiedWhenItWouldCollideWithFfiImport
- [x] LlvmIrEmissionTests::FfiVarargsDeclarationsAndCallsUseLlvmVarargFunctionType
- [x] LlvmIrEmissionTests::FfiVarargsPercentSAcceptsRawCCharPointer
- [x] LlvmIrEmissionTests::InternalizedImmutableGlobalsUseUnnamedAddrOnlyWhenAddressesStayInsignificant
- [x] LlvmIrEmissionTests::AggregateAndArrayGlobalsEmitConcreteInitializers
- [x] LlvmIrEmissionTests::IntegralAndScientificFloatConstantsEmitValidHexFloatLiterals
- [x] LlvmIrEmissionTests::GlobalsAndUnicodeStringConstantsEmitConcreteAlignment
- [x] LlvmIrEmissionTests::LongTextLiteralDataUsesVectorizationFriendlyAlignment
- [x] LlvmIrEmissionTests::ImmutableGlobalsWithoutAddressTakenEmitLocalUnnamedAddr
- [x] LlvmIrEmissionTests::ConstArithmeticGlobalsEmitConcreteInitializers
- [x] LlvmIrEmissionTests::RecordPrimaryConstructorGlobalsEmitConcreteInitializers
- [x] LlvmIrEmissionTests::ConstFixedArrayGlobalsEmitFrozenDefinitions
- [x] LlvmIrEmissionTests::NestedConstObjectGraphsEmitConcreteConstantInitializers
- [x] LlvmIrEmissionTests::NestedAggregateLiteralsFoldIntoFrozenGlobalInitializers
- [x] LlvmIrEmissionTests::MutableAggregateGlobalsEmitConcreteInitializersAndStores
- [x] LlvmIrEmissionTests::StaticGenericConstructorInitializersCanForwardAggregateArguments
- [x] LlvmIrEmissionTests::AggregateArrayFieldsEmitConcreteInitializers
- [x] LlvmIrEmissionTests::ImmutableGlobalLoadsEmitInvariantMetadataThroughFieldAndIndexChains
- [x] LlvmIrEmissionTests::OnceInitializedReadonlyStackStorageEmitsInvariantStartWithoutInvariantLoadMetadata
- [x] LlvmIrEmissionTests::ReadonlyStackDropTempsPassedByAddressDoNotEmitInvariantMetadata
- [x] LlvmIrEmissionTests::FrozenRawPointerLoadsDoNotEmitInvariantMetadataWithoutConstProvenance
- [x] LlvmIrEmissionTests::StaticReadonlyPointerLoadsDoNotConferDeepConstProvenance
- [x] LlvmIrEmissionTests::ImmutableRawPointerLocalsDoNotConferDeepConstProvenance
- [x] LlvmIrEmissionTests::ReadonlyRawPointerLocalsDoNotConferDeepConstProvenance
- [x] LlvmIrEmissionTests::IntegerLaunderedMutablePointersDoNotEmitInvariantMetadata
- [x] LlvmIrEmissionTests::TypedScalarLoadsAndStoresEmitConservativeStarkTbaa
- [x] LlvmIrEmissionTests::TypedFieldAndFixedArrayElementLoadsEmitStructPathTbaa
- [x] LlvmIrEmissionTests::RawPointerAndPointerIntegerEscapesSuppressTbaa
- [x] LlvmIrEmissionTests::ExclusiveBorrowMemoryAccessesEmitScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::SameRawPointerParametersShareAliasScopeAgainstDefaultDisjointParameters
- [x] LlvmIrEmissionTests::SameSliceParametersEmitDataAndLengthAssumes
- [x] LlvmIrEmissionTests::DynamicLocalStorageAccessesEmitScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::RawPointerMemoryAccessesDoNotEmitScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::OverlappingConstViewsDoNotInferNoAliasFromConstness
- [x] LlvmIrEmissionTests::RawPointerEscapesSuppressScopedNoAliasMetadataForAffectedAccesses
- [x] LlvmIrEmissionTests::IntegerLaunderedRawPointersSuppressScopedNoAliasMetadataForAffectedAccesses
- [x] LlvmIrEmissionTests::RawPointerConstNullGlobalsRemainExternalPlaceholders
- [x] LlvmIrEmissionTests::BitwiseXorEmitsConcreteLlvmXorInstruction
- [x] LlvmIrEmissionTests::BitwiseAndShiftExpressionsEmitConcreteLlvmInstructions
- [x] LlvmIrEmissionTests::IntegerAndBoolUnaryOperatorsEmitConcreteLlvmInstructions
- [x] LlvmIrEmissionTests::OrdinaryIntegerArithmeticEmitsSignedNoWrapFlags
- [x] LlvmIrEmissionTests::UnconstrainedUnsignedOrdinaryArithmeticEmitsUnsignedNoWrapByContract
- [x] LlvmIrEmissionTests::NonNegativeConstrainedOrdinaryArithmeticEmitsUnsignedNoWrapWhenProven
- [x] LlvmIrEmissionTests::UnsignedOrdinaryArithmeticDoesNotInventSignedNoWrapForHighBitResults
- [x] LlvmIrEmissionTests::PropagatedValueFactsEmitUnsignedNoWrapForJoinedRanges
- [x] LlvmIrEmissionTests::PropagatedValueFactsEmitNarrowerReturnRangeAttributes
- [x] LlvmIrEmissionTests::SameWidthSignednessChangingIntegerConversionsTranslateReturnRangeFacts
- [x] LlvmIrEmissionTests::ProvenNonNegativeSignedDivisionAndModuloUseUnsignedLlvmOpcodes
- [x] LlvmIrEmissionTests::OrdinaryIntegerArithmeticDoesNotEmitUnsignedNoWrapWhenNegativeValuesArePossible
- [x] LlvmIrEmissionTests::ProvenShiftLeftRangesEmitNoWrapFlags
- [x] LlvmIrEmissionTests::UnsignedIntegerOperationsUseUnsignedLlvmOpcodes
- [x] LlvmIrEmissionTests::ShiftRightAndSignedDivisionEmitExactWhenDivisibilityIsProven
- [x] LlvmIrEmissionTests::ShiftAndDivisionFlagsAreOmittedWhenProofFactsAreInsufficient
- [x] LlvmIrEmissionTests::WrappingArithmeticEmitsConcreteLlvmInstructions
- [x] LlvmIrEmissionTests::SaturatingArithmeticEmitsWideClampSequence
- [x] LlvmIrEmissionTests::ExplicitWrappingAndSaturatingArithmeticStayFlagFreeEvenWhenOrdinaryProofsExist
- [x] LlvmIrEmissionTests::ExplicitIntegerArithmeticConstantsFoldBeforeLlvmEmission
- [x] LlvmIrEmissionTests::ConstantFloatExponentExpressionsFoldBeforeLlvmEmission
- [x] LlvmIrEmissionTests::IntegerExponentExpressionsEmitInternalPowHelpers
- [x] LlvmIrEmissionTests::SmallConstantIntegerExponentExpressionsLowerToStraightLineMultiplies
- [x] LlvmIrEmissionTests::FloatLiteralArgumentsEmitValidHexFloatConstants
- [x] LlvmIrEmissionTests::LoopHeaderEmitsBackedgePhi
- [x] LlvmIrEmissionTests::ConstantLiteralSwitchesFoldBeforeLlvmEmission
- [x] LlvmIrEmissionTests::GuardedSwitchBodyEmitsCompareAndGuardBranches
- [x] LlvmIrEmissionTests::EnumSwitchExpressionCallEmitsSingleEvaluation
- [x] LlvmIrEmissionTests::LargeEnumSwitchLoadsTagWithRangeMetadataAndZeroTagsUnitCase
- [x] LlvmIrEmissionTests::CaptureSwitchPatternEmitsConcreteBody
- [x] LlvmIrEmissionTests::MultiLabelGuardedSwitchEmitsDecisionTreeBody
- [x] LlvmIrEmissionTests::OrPatternLiteralSwitchPreservesNativeSwitchLowering
- [x] LlvmIrEmissionTests::RangePatternIntegerSwitchEmitsGuardedComparisons
- [x] LlvmIrEmissionTests::RangePatternEnumPayloadSwitchEmitsPayloadComparison
- [x] LlvmIrEmissionTests::OrPatternEnumCapturesShareBodyLocal
- [x] LlvmIrEmissionTests::ComparisonChainEmitsShortCircuitBranchesAndSingleSharedEvaluation
- [x] LlvmIrEmissionTests::FloatComparisonChainsUseOrderedPredicates
- [x] LlvmIrEmissionTests::TextLiteralSwitchEmitsLengthAndByteComparisons
- [x] LlvmIrEmissionTests::LargeTextLiteralSwitchEmitsLengthPartitionedDispatch
- [x] LlvmIrEmissionTests::UnicodeTextLiteralSwitchEmitsConcreteBody
- [x] LlvmIrEmissionTests::TextSlicesEmitShiftedAsciiAndUnicodeViews
- [x] LlvmIrEmissionTests::ExplicitAsciiLiteralToUnicodeConversionEmitsUnicodeConstant
- [x] LlvmIrEmissionTests::CompileTimeTextConcatenationEmitsSingleTextConstant
- [x] LlvmIrEmissionTests::CompileTimeInterpolatedTextEmitsFoldedTextConstant
- [x] LlvmIrEmissionTests::RawAndMultilineTextLiteralsEmitExactTextConstants
- [x] LlvmIrEmissionTests::SystemTextOwnedConcatAndViewBuiltinsEmitConcreteDefinitions
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeBuiltinEmitsDirectWideningLoop
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLiteralCallSpecializesAtCallSite
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeSlicedLiteralSourceSpecializesAtCallSite
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeDynamicSliceSourceKeepsBuiltinCall
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLargeLiteralSpecializationUsesMemcpy
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeDynamicSourceKeepsBuiltinCall
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLocalLiteralSourceSpecializesAtCallSite
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeConstLiteralSourceSpecializesAtCallSite
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodePhiJoinedIdenticalLiteralSourceSpecializesAtCallSite
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeEscapedAsciiLiteralSpecializesAtCallSite
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeEmptyLiteralSpecializationAvoidsDataLoad
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLiteralSpecializationHandlesNullDestination
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLiteralSpecializationChecksCapacity
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLiteralSpecializationChecksNullDestinationData
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeLiteralSpecializationRewritesForwardPhiIncomingLabel
- [x] LlvmIrEmissionTests::SystemTextAsciiToUnicodeNonAsciiLiteralKeepsBuiltinCall
- [x] LlvmIrEmissionTests::SystemMathBuiltinsEmitConcreteDefinitionsAndLlvmIntrinsics
- [x] LlvmIrEmissionTests::SystemBitOperationsBuiltinsEmitConcreteDefinitionsAndLlvmIntrinsics
- [x] LlvmIrEmissionTests::ImportedSystemMathHardwareBuiltinsAreInternalizedIntoConsumerIr
- [x] LlvmIrEmissionTests::UnusedImportedSystemMathHardwareBuiltinsDoNotMaterializeIntoConsumerIr
- [x] LlvmIrEmissionTests::ImportedSystemBitOperationsBuiltinsAreInternalizedIntoConsumerIr
- [x] LlvmIrEmissionTests::HelloWorldStyleFfiPutsEmitsStringGlobalAndMainBody
- [x] LlvmIrEmissionTests::InternalStringFunctionsUseConcreteStringAbi
- [x] LlvmIrEmissionTests::CharacterLiteralsEmitConcreteStringValues
- [x] LlvmIrEmissionTests::UnicodeStringLiteralsUseUtf32CodeUnitLengthInRuntimeValues
- [x] LlvmIrEmissionTests::EquivalentAsciiLiteralPayloadsShareOneHelperGlobalAcrossGlobalAndFunctionUses
- [x] LlvmIrEmissionTests::EquivalentUnicodeLiteralPayloadsShareOneHelperGlobal
- [x] LlvmIrEmissionTests::PlainFnsEmitInferredPureAndFiniteAttributes
- [x] LlvmIrEmissionTests::BorrowParametersEmitCapturesAttributes
- [x] LlvmIrEmissionTests::StoreBorrowParametersEmitEscapingCaptureAttributes
- [x] LlvmIrEmissionTests::RetborrowScalarReturnsUsePointerAbiAndCanBeWrittenThrough
- [x] LlvmIrEmissionTests::ScalarAbiValuesEmitNoundefOnParametersAndReturns
- [x] LlvmIrEmissionTests::BorrowedPointerAbiValuesEmitNoundefOnParametersAndReturns
- [x] LlvmIrEmissionTests::FullyDefinedNonFfiAbiSurfacesEmitNoundef
- [x] LlvmIrEmissionTests::PointerAbiContractsDistinguishSafeBorrowsAndNullableRawPointers
- [x] LlvmIrEmissionTests::RawMutablePointerDeclarationsMayReadAndWriteArgumentMemory
- [x] LlvmIrEmissionTests::FfiRawMutablePointerCallsPropagateWrapperArgumentWrites
- [x] LlvmIrEmissionTests::ConstrainedIntegerLoadsAndCallResultsEmitRangeMetadata
- [x] LlvmIrEmissionTests::ConstrainedIntegerAbiSurfacesEmitRangeAttributes
- [x] LlvmIrEmissionTests::BranchRefinedIntegerComparisonsEmitTargetedAssumes
- [x] LlvmIrEmissionTests::BranchRefinedRawPointerNullChecksEmitNonnullAssumeBundle
- [x] LlvmIrEmissionTests::BranchRefinedPointerEqualityToKnownAddressEmitsAlignmentAssumeBundle
- [x] LlvmIrEmissionTests::BoundaryFactsAndPlainBoolBranchesDoNotEmitAssumeIntrinsic
- [x] LlvmIrEmissionTests::SignedConstrainedIntegerLoadsEmitWrappingRangeMetadata
- [x] LlvmIrEmissionTests::MemoryAttributesDistinguishArgumentAndOtherMemoryEffects
- [x] LlvmIrEmissionTests::MemoryAttributesKeepArgumentEffectsWhenSyntheticTemporariesAreNeeded
- [x] LlvmIrEmissionTests::EscapedTextLiteralsEmitDecodedBytes
- [x] LlvmIrEmissionTests::FfiStringCallsExtractPointerFromConcreteStringValues
- [x] LlvmIrEmissionTests::StructFieldAccessCanFoldToScalarReturn
- [x] LlvmIrEmissionTests::FieldAssignmentCanFoldToScalarReturn
- [x] LlvmIrEmissionTests::RegisterObjectCreationCanFoldToScalarReturn
- [x] LlvmIrEmissionTests::NonStrictFpFunctionsEmitFastMathFlagsOnBinaryOpsAndCalls
- [x] LlvmIrEmissionTests::TailPositionDirectCallsEmitTailMarkerWhenAbiCompatible
- [x] LlvmIrEmissionTests::NonStrictFpFunctionsEmitFastMathFlagsOnRemainingFloatInstructions
- [x] LlvmIrEmissionTests::NonStrictFpMultiplyAddExpressionsUseContractionFriendlyIntrinsic
- [x] LlvmIrEmissionTests::StrictFpFunctionsOptOutOfFastMathFlags
- [x] LlvmIrEmissionTests::StrictFpFunctionPointerCallsComposeWithKindedCallSiteAttributes
- [x] LlvmIrEmissionTests::StrictFpMultiplyAddExpressionsDoNotUseContractionIntrinsic
- [x] LlvmIrEmissionTests::StrictFpConversionsUseConstrainedFloatingPointIntrinsics
- [x] LlvmIrEmissionTests::UnrecoverableUnreachablePathsLowerThroughColdTrapHelper
- [x] LlvmIrEmissionTests::HeapObjectCreationUsesAllocatorLowering
- [x] LlvmIrEmissionTests::HeapFieldInitializationDoesNotReadUninitializedAggregateStorage
- [x] LlvmIrEmissionTests::SystemMemoryInternalAllocatorBuiltinsLowerToRuntimeAllocatorContract
- [x] LlvmIrEmissionTests::StackLocalsEmitInstructionAlignmentWhenLayoutsAreKnown
- [x] LlvmIrEmissionTests::AddressValueFactsEmitKnownPointeeAlignmentForIndirectLoads
- [x] LlvmIrEmissionTests::RawPointerParametersDoNotInventPointeeAlignment
- [x] LlvmIrEmissionTests::OwnedScalarArrayStoragePreservesVectorizationFriendlyAlignment
- [x] LlvmIrEmissionTests::StaticStackAndAbiTemporaryAllocasEmitInEntryBlock
- [x] LlvmIrEmissionTests::SmallRecordEqualityAndInequalityEmitScalarLeafComparisons
- [x] LlvmIrEmissionTests::SmallFixedArrayEqualityAndInequalityEmitScalarLeafComparisons
- [x] LlvmIrEmissionTests::SmallEnumEqualityAndInequalityEmitScalarLeafComparisons
- [x] LlvmIrEmissionTests::LargerRecordEqualityAndInequalityEmitScalarLeafComparisons
- [x] LlvmIrEmissionTests::LargerRecordOrderedComparisonsEmitScalarLeafLexicographicHelperCalls
- [x] LlvmIrEmissionTests::ScalarizableEnumOrderedComparisonsEmitScalarLeafLexicographicHelperCalls
- [x] LlvmIrEmissionTests::LargerFixedArrayEqualityAndInequalityEmitScalarLeafComparisons
- [x] LlvmIrEmissionTests::LargerEnumEqualityAndInequalityEmitScalarLeafComparisons
- [x] LlvmIrEmissionTests::TextEqualityAndInequalityEmitHelperCalls
- [x] LlvmIrEmissionTests::AggregatesWithTextFieldsEmitScalarLeafTextComparisons
- [x] LlvmIrEmissionTests::SliceEqualityAndInequalityEmitPointerAndLengthComparisons
- [x] LlvmIrEmissionTests::AggregatesWithSliceFieldsEmitScalarLeafSliceComparisons
- [x] LlvmIrEmissionTests::MixedCallMemberAndIndexPostfixChainsEmitCallAndExtracts
- [x] LlvmIrEmissionTests::RecordTypeUsesConcreteAggregateLayoutAndCanFoldFieldReads
- [x] LlvmIrEmissionTests::PlainObjectCreationWithoutInitializerReturnsZeroInitializedAggregate
- [x] LlvmIrEmissionTests::PrimaryRecordConstructorArgumentsEmitOrderedAggregateUpdates
- [x] LlvmIrEmissionTests::InternalAggregateParameterUsesDirectValueAbi
- [x] LlvmIrEmissionTests::BorrowedPaddedAggregateEmitsDerivedAlignmentAndLayoutFacts
- [x] LlvmIrEmissionTests::PackedCStructLayoutEmitsPackedPhysicalTypeAndUnalignedFieldAccess
- [x] LlvmIrEmissionTests::ExplicitStructLayoutEmitsByteStorageAndFieldOffsetAccess
- [x] LlvmIrEmissionTests::TypeAliasesPreserveTheUnderlyingAggregateAbi
- [x] LlvmIrEmissionTests::SmallPackedAddressableAggregateCopyUsesScalarFieldLoadsAndStores
- [x] LlvmIrEmissionTests::SmallPaddedAggregateCopyPreservesWholeAggregateTransfer
- [x] LlvmIrEmissionTests::LargeAddressableAggregateCopyUsesInlineMemcpy
- [x] LlvmIrEmissionTests::VeryLargeAddressableAggregateCopyUsesRegularMemcpy
- [x] LlvmIrEmissionTests::LargeLocalFixedArrayConstantIndexUpdateUsesScalarProjectionAccess
- [x] LlvmIrEmissionTests::LargeEnumPayloadProjectionIntoAddressableLocalUsesAddressCopy
- [x] LlvmIrEmissionTests::LargeEnumPayloadImmediateMoveIntoMethodLocalAliasesPayloadStorage
- [x] LlvmIrEmissionTests::LargeLocalAggregateInsertedIntoEnumPayloadUsesAddressCopy
- [x] LlvmIrEmissionTests::SmallEnumPayloadProjectionKeepsScalarLowering
- [x] LlvmIrEmissionTests::AddressForwardedAggregateStoreDoesNotReadEndedLocalLifetime
- [x] LlvmIrEmissionTests::LargeZeroInitializedAggregateStoresUseInlineMemset
- [x] LlvmIrEmissionTests::AggregateMoveInvalidatesAddressableSourceStorage
- [x] LlvmIrEmissionTests::AddressableAggregateConditionalUsesSingleAggregateAlloca
- [x] LlvmIrEmissionTests::InternalAggregateReturnUsesDirectValueAbi
- [x] LlvmIrEmissionTests::LargeAggregateReturnUsesSRetAbi
- [x] LlvmIrEmissionTests::LargeAggregateReturnedThroughSRetMaterializesBeforeNestedInsert
- [x] LlvmIrEmissionTests::LargeAggregateInitializerReturnMaterializesDirectlyIntoSRetBuffer
- [x] LlvmIrEmissionTests::LargeGenericEnumAggregateReturnMaterializesDirectlyIntoSRetBuffer
- [x] LlvmIrEmissionTests::LargeAggregateForwardReturnForwardsDirectCallIntoSRetBuffer
- [x] LlvmIrEmissionTests::LargeAggregateLocalForwardReturnKeepsDirectCallInSRetBuffer
- [x] LlvmIrEmissionTests::LargeAggregateReturnOfIndirectParameterSkipsEntryLoad
- [x] LlvmIrEmissionTests::LargeAggregateIndirectParameterForwardingUsesOriginalPointer
- [x] LlvmIrEmissionTests::LargeAggregateParametersUseByValueIndirectAbi
- [x] LlvmIrEmissionTests::AggregateCallReturnRegressionBenchmarkKeepsSmallAggregatesOnDirectAbi
- [x] LlvmIrEmissionTests::AggregateCallReturnRegressionBenchmarkKeepsLargeAggregatesOnIndirectAbi
- [x] LlvmIrEmissionTests::AggregateBranchJoinEmitsByValuePhiNode
- [x] LlvmIrEmissionTests::ValueReceiverMethodsLowerToDirectAggregateCalls
- [x] LlvmIrEmissionTests::BorrowReceiverMethodsLowerToPointerReceiverCalls
- [x] LlvmIrEmissionTests::IndexedFieldAddressBehindRawPointerEmitsDirectParameterGeps
- [x] LlvmIrEmissionTests::IndexedRawPointerElementsEmitDirectParameterGeps
- [x] LlvmIrEmissionTests::BorrowReceiverIndexedFieldAddressesEmitDirectReceiverGeps
- [x] LlvmIrEmissionTests::MutableBorrowedAggregateWriterEmitsWriteOnlyNoCaptureParameterFacts
- [x] LlvmIrEmissionTests::MutableBorrowReturnedSliceWritesDoNotEmitReadonlyParameterFacts
- [x] LlvmIrEmissionTests::FixedArrayInitializerAndIndexCanFoldToScalarReturn
- [x] LlvmIrEmissionTests::FixedArrayIndexAssignmentCanFoldToScalarReturn
- [x] LlvmIrEmissionTests::DynamicArrayIndexEmitsAddressBasedLoad
- [x] LlvmIrEmissionTests::NonAddressableFixedArrayDynamicIndexSpillsOnlyTheTemporarySource
- [x] LlvmIrEmissionTests::SliceParameterUsesConcreteSliceAbiAndDynamicIndexLoad
- [x] LlvmIrEmissionTests::FixedArrayBackedSliceAndKnownTextSliceCarryProvenGepFlags
- [x] LlvmIrEmissionTests::PropagatedSliceAndTextLengthFactsCarryProvenGepFlags
- [x] LlvmIrEmissionTests::FixedArrayParameterUsesDirectValueAbi
- [x] LlvmIrEmissionTests::FixedArrayParameterDynamicIndexUsesParameterSlotAddressing
- [x] LlvmIrEmissionTests::FixedArrayDynamicIndexEmitsGepFlagsOnlyWhenRangeProvesObjectBounds
- [x] LlvmIrEmissionTests::FixedArrayReturnUsesDirectValueAbi
- [x] LlvmIrEmissionTests::DynamicArrayIndexMutationEmitsAddressBasedStoreAndLoad
- [x] LlvmIrEmissionTests::SliceMutationEmitsIndirectStoreAndLoad
- [x] LlvmIrEmissionTests::ImportedStarkFunctionUsesQualifiedDependencySymbol
- [x] LlvmIrEmissionTests::ImportedSourceAsmFunctionsEmitExternalDeclarationsAndCalls
- [x] LlvmIrEmissionTests::ImportedGlobalsUseQualifiedDependencySymbolsAndFoldConstValues
- [x] LlvmIrEmissionTests::ImportedReadonlyScalarArrayDeclarationsCarryVectorizationFriendlyAlignment
- [x] LlvmIrEmissionTests::PackageImageBackedImportedReadonlyDataPreservesInvariantMetadata
- [x] LlvmIrEmissionTests::ImmutableGlobalAddressesLowerWithoutPointerCasts
- [x] LlvmIrEmissionTests::ImportedAggregateFunctionsUseCrossModuleAbiDeclarations
- [x] LlvmIrEmissionTests::ConstantImportedLawCallsFoldBeforeClosedWorldCloneEmission
- [x] LlvmIrEmissionTests::ConstantImportedLawCallsStillFoldInsideImpureCallers
- [x] LlvmIrEmissionTests::MixedLawAndNonLawRootCallersUseSelectiveImportedDoctrineLawClones
- [x] LlvmIrEmissionTests::MixedLawAndNonLawRootCallersUseSelectiveImportedTopLevelLawClones
- [x] LlvmIrEmissionTests::ImpureRootFunctionsDoNotCloneImportedLawBodiesIntoRootLlvm
- [x] LlvmIrEmissionTests::ImportedLawEntrypointsWithExplicitInlineHintDoNotSpecializeIntoClones
- [x] LlvmIrEmissionTests::ClosedWorldModulePrivateLawHelpersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateDirectCallForwardersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateMemberCallForwardersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateFieldAccessWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateIndexAccessWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateConversionWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateAddressOfWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateBinaryOperatorWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateComparisonWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateTerminalIfSelectionWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateTerminalSwitchSelectionWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateObjectConstructionWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateEnumConstructionWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::ModulePrivateLocalUpdateWrappersEmitAlwaysInline
- [x] LlvmIrEmissionTests::HotFunctionsEmitHotAttribute
- [x] LlvmIrEmissionTests::LibraryBuildQualifiesPublicRootSymbols
- [x] LlvmIrEmissionTests::ModulePrivateFunctionsLowerWithInternalLinkage
- [x] LlvmIrEmissionTests::SourceBackedGenericCallsInlineSmallConcreteMonomorphizedSymbols
- [x] LlvmIrEmissionTests::BackendOpaqueGenericFunctionsRemainOptimizationBoundariesAfterMonomorphization
- [x] LlvmIrEmissionTests::BackendOpaqueGenericTypeMethodsRemainOptimizationBoundariesAfterMonomorphization
- [x] LlvmIrEmissionTests::BackendOpaqueStructAndRecordMethodsRemainNarrowOptimizationBoundaries
- [x] LlvmIrEmissionTests::BackendOpaqueDoctrineMethodsRemainNarrowOptimizationBoundaries
- [x] LlvmIrEmissionTests::NestedSourceBackedGenericCallsInlineTransitiveSmallConcreteMonomorphizedSymbols
- [x] LlvmIrEmissionTests::ImportedSourceBackedGenericSpecializationsUseLinkOnceOdrComdatDefinitionsAndInlineSmallCalls
- [x] LlvmIrEmissionTests::ImportedSourceBackedMutableBorrowGenericSpecializationsDoNotDenyArgumentMemoryAccess
- [x] LlvmIrEmissionTests::ManifestBackedGenericCallsInlineSmallConcreteMonomorphizedSymbolsFromPackageImageTemplates
- [x] LlvmIrEmissionTests::ManifestBackedGenericInlineClosureCallSpecializesFromPackageImageTypedBody
- [x] LlvmIrEmissionTests::ManifestBackedNestedGenericCallsInlineTransitiveSmallConcreteMonomorphizedSymbolsFromPackageImageTemplates
- [x] LlvmIrEmissionTests::RepeatedManifestBackedNestedGenericCallsStayInternalAndDeduplicatedAtLlvmEmission
- [x] LlvmIrEmissionTests::LocalFixedArrayCanBeCoercedToSliceForCalls
- [x] LlvmIrEmissionTests::BorrowedSliceParametersAcceptReusableSliceViews
- [x] LlvmIrEmissionTests::BorrowedAggregateCallReusesPromotedLocalSlot
- [x] LlvmIrEmissionTests::MutableBorrowReceiverForwardingReusesOriginalParameterPointer
- [x] LlvmIrEmissionTests::MutableBorrowFieldReceiverUsesOriginalFieldAddress
- [x] LlvmIrEmissionTests::StoreBorrowFieldsUsePointerStorageAndProjectThroughBorrowedValue
- [x] LlvmIrEmissionTests::RawPointerLoopBackedgeCastsMaterializePhiIncomingValues
- [x] LlvmIrEmissionTests::ManifestBackedMutableBorrowMethodsStayWritableAtImportedDeclarations
- [x] LlvmIrEmissionTests::DoctrineLawCallsEmitDirectReadonlyNoCaptureSignatures
- [x] LlvmIrEmissionTests::ConfiguredTargetInfoIsEmittedInHeader
- [x] LlvmIrEmissionTests::ShortCircuitAndTernaryEmitBranchesAndPhi
- [x] LlvmIrEmissionTests::PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess
- [x] LlvmIrEmissionTests::RuntimeDisjointRawPointerConditionEmitsByteRangeComparisons
- [x] LlvmIrEmissionTests::RuntimeDisjointRawPointerRegionsUseElementCounts
- [x] LlvmIrEmissionTests::RuntimeDisjointBorrowConditionEmitsAddressRangeComparisons
- [x] LlvmIrEmissionTests::RuntimeDisjointSliceConditionUsesViewDataAndLength
- [x] LlvmIrEmissionTests::RuntimeDisjointTrueBranchEmitsScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::RuntimeDisjointSameBaseRawPointerSubregionsEmitScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::RuntimeDisjointSameBaseSubregionScopedNoAliasDoesNotEscapeTrueBranch
- [x] LlvmIrEmissionTests::UnsafeAssumeDisjointEmitsScopedNoAliasMetadataWithoutRuntimeCheck
- [x] LlvmIrEmissionTests::RuntimeScopedNoAliasFactsAttachToEligibleDirectCalls
- [x] LlvmIrEmissionTests::IndependentLoopLawCallsEmitAccessGroupMetadata
- [x] LlvmIrEmissionTests::FfiCallsDoNotReceiveScopedNoAliasOrAccessGroupMetadata
- [x] LlvmIrEmissionTests::DisjointRawPointerParametersEmitNoAliasAttributes
- [x] LlvmIrEmissionTests::DefaultNonOverlapRawPointerParametersEmitNoAliasAttributes
- [x] LlvmIrEmissionTests::OverlapRawPointerParametersDoNotEmitNoAliasAttributes
- [x] LlvmIrEmissionTests::ConstantBoundedRawPointerParametersEmitDereferenceabilityAttributes
- [x] LlvmIrEmissionTests::PositiveVariableBoundedRawPointerParametersEmitMinimumDereferenceabilityAttributes
- [x] LlvmIrEmissionTests::ZeroLengthBoundedRawPointerParametersDoNotEmitNonnullOrDereferenceabilityAttributes
- [x] LlvmIrEmissionTests::FunctionPointerCallsWithPositiveVariableBoundedRawPointerParametersEmitCallAttributes
- [x] LlvmIrEmissionTests::BoundedRawPointerArgumentFactsStrengthenDirectAndIndirectCallAttributes
- [x] LlvmIrEmissionTests::BoundedRawPointerRegionFactsEmitInboundsGepForProvenParameterIndexes
- [x] LlvmIrEmissionTests::RawSlicesCarryBoundedRegionFactsIntoSliceElementGepFlags
- [x] LlvmIrEmissionTests::DisjointRawPointerParameterAccessesEmitScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::DefaultNonOverlapRawPointerParameterAccessesEmitScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::ConstRawPointerParametersEmitReadonlyAttributes
- [x] LlvmIrEmissionTests::ConstRawPointerParameterLoadsDoNotEmitInvariantLoadMetadata
- [x] LlvmIrEmissionTests::RawSlicesPreserveRuntimeDisjointScopedNoAliasMetadata
- [x] LlvmIrEmissionTests::RawSlicesFromConstPointersDoNotEmitInvariantLoadMetadata
- [x] LlvmIrEmissionTests::RawSlicesFromConstPointerLocalsDoNotEmitInvariantLoadMetadata
- [x] LlvmIrEmissionTests::WillexitScalarLoopsEmitMustProgressLoopMetadata
- [x] LlvmIrEmissionTests::NonDeterministicScalarLoopsDoNotEmitMustProgressLoopMetadata
- [x] LlvmIrEmissionTests::IndependentScalarLoopsEmitMustProgressLoopMetadata
- [x] LlvmIrEmissionTests::IndependentForLoopsEmitMustProgressLoopMetadata
- [x] LlvmIrEmissionTests::IndependentSliceLoopsEmitAccessGroupAndParallelLoopMetadata
- [x] LlvmIrEmissionTests::FixedArrayToSliceCallArgumentsMaterializeSliceAbiSlots
- [x] LlvmIrEmissionTests::IndependentSliceLoopsWithConditionalsEmitAccessGroupAndParallelLoopMetadata
- [x] LlvmIrEmissionTests::IndependentSliceLoopsWithMemberProjectionsEmitAccessGroupAndParallelLoopMetadata
- [x] LlvmIrEmissionTests::IndependentBoundedRawPointerLoopsEmitAccessGroupAndParallelLoopMetadata
- [x] LlvmIrEmissionTests::IndependentBoundedRawPointerLoopsUseRuntimeRegionFactsForParallelMetadata
- [x] LlvmIrEmissionTests::BoundedRawPointerCopyLoopLowersToMemcpyWhenNoAliasIsProven
- [x] LlvmIrEmissionTests::BoundedRawPointerCopyLoopInsideNontrivialFunctionLowersToMemcpy
- [x] LlvmIrEmissionTests::BoundedRawPointerCopyLoopWithoutNoAliasProofKeepsScalarLowering
- [x] LlvmIrEmissionTests::BoundedRawPointerCopyLoopInsideNontrivialFunctionWithoutNoAliasProofKeepsScalarLowering
- [x] LlvmIrEmissionTests::BoundedRawPointerOverlapSafeTemporaryCopyLowersToMemmove
- [x] LlvmIrEmissionTests::BoundedRawPointerOverlapSafeTemporaryCopyInsideNontrivialFunctionLowersToMemmove
- [x] LlvmIrEmissionTests::BoundedRawPointerForwardBackwardMoveLoopLowersToMemmove
- [x] LlvmIrEmissionTests::SliceForwardBackwardMoveLoopLowersToMemmoveWhenByteLengthIsRepresentable
- [x] LlvmIrEmissionTests::BoundedRawPointerOverlapSafeTemporaryCopyWithTemporaryEpilogueUseKeepsScalarLowering
- [x] LlvmIrEmissionTests::BoundedRawPointerByteFillLoopLowersToMemset
- [x] LlvmIrEmissionTests::InitSliceByteFillLoopLowersToMemset
- [x] LlvmIrEmissionTests::DynamicTailInitByteFillLoopLowersToMemsetAndCommitsLength
- [x] LlvmIrEmissionTests::DynamicAppendThroughLengthByteFillLoopLowersToMemsetAndCommitsLengthOnce
- [x] LlvmIrEmissionTests::DisjointInitSliceCopyLoopLowersToMemcpy
- [x] LlvmIrEmissionTests::InitSliceOverlapSafeTemporaryCopyLowersToMemmove
- [x] LlvmIrEmissionTests::BoundedRawPointerByteFillLoopInsideNontrivialFunctionLowersToMemset
- [x] LlvmIrEmissionTests::IndependentBoundedRawPointerFillAndTransformLoopsEmitParallelMetadata
- [x] LlvmIrEmissionTests::IntegerArithmeticFoldEmitsSingleMultiplyForRepeatedUnknownAdds
- [x] LlvmIrEmissionTests::IntegerArithmeticFoldEmitsMultiplySubtractForRepeatedSubtractionChains
- [x] LlvmIrEmissionTests::IntegerArithmeticFoldLowersRepeatedProductRunsWithoutPowHelper

### LlvmTextOrderedComparisonEmissionTests  (1/1)
- [x] LlvmTextOrderedComparisonEmissionTests::TextOrderedComparisonsEmitLexicographicHelperCalls

### OutParameterDropRegressionTests  (1/1)
- [x] OutParameterDropRegressionTests::AssigningIntoAnOutParameterDoesNotFreeTheTransferredValueOnReturn

### PackageImageCallableValueTests  (4/4)
- [x] PackageImageCallableValueTests::PackageImageBackedCallableAliasDrivesIndirectCallSiteEffectAttributes
- [x] PackageImageCallableValueTests::PackageImageBackedGenericTemplateFnptrCallKeepsKindedIndirectCallAttributes
- [x] PackageImageCallableValueTests::PackageImageBackedFunctionPointerParametersTargetTypeNonCapturingLambdas
- [x] PackageImageCallableValueTests::PackageImageBackedAcceptedProgramEmitsLlvmWithoutFallbackLogs

### RawMultilineStringParityTests  (4/4)
- [x] RawMultilineStringParityTests::MultilineRawStringsStripDelimiterNewlinesAndClosingIndentation
- [x] RawMultilineStringParityTests::WhitespaceOnlyLinesShorterThanTheClosingIndentationBecomeEmptyLines
- [x] RawMultilineStringParityTests::SingleLineRawTripleQuoteStringsKeepTheirContentVerbatim
- [x] RawMultilineStringParityTests::InterpolatedRawMultilineStringsNormalizeBeforeHoleSplitting

### SsaOptimizationTests  (2/2)
- [x] SsaOptimizationTests::ConstantPropagationFoldsConstantBranchesInLlvm
- [x] SsaOptimizationTests::ConstantPropagationFoldsConstantSwitchesInLlvm

### StringsFeatureTests  (11/11)
- [x] StringsFeatureTests::AsciiStringsUseConcreteRuntimeAbiThroughTheWholePipeline
- [x] StringsFeatureTests::TextSlicesStayZeroCopyViewsThroughTheWholePipeline
- [x] StringsFeatureTests::SingleElementTextIndexingReturnsUnitLengthViewsThroughTheWholePipeline
- [x] StringsFeatureTests::ExplicitAsciiLiteralToUnicodeConversionUsesUnicodeStaticDataThroughTheWholePipeline
- [x] StringsFeatureTests::OwnedTextCoreTypesNeedNoModuleImport
- [x] StringsFeatureTests::OwnedAsciiConcatenationUsesMemcpyAndViewProjection
- [x] StringsFeatureTests::FixedCapacityAsciiConcatenationLowersToStackStorageAndConcatTrap
- [x] StringsFeatureTests::FixedCapacityUnicodeConcatenationLowersToStackStorageAndConcatTrap
- [x] StringsFeatureTests::FixedCapacityAsciiInterpolationFormatsIntoStackStorage
- [x] StringsFeatureTests::FixedCapacityUnicodeInterpolationFormatsIntoStackStorage
- [x] StringsFeatureTests::TextViewPointerAndLengthBuiltinsEmitConcreteDefinitions

### TraitsAndDoctrinesFeatureTests  (10/10)
- [x] TraitsAndDoctrinesFeatureTests::ExplicitOpsTableDispatchStaysVisibleAndIndirect
- [x] TraitsAndDoctrinesFeatureTests::DoctrineLawCallsStayDirectAndPreserveBorrowFacts
- [x] TraitsAndDoctrinesFeatureTests::GenericDoctrineMethodsPreserveStaticFiniteLawBorrowContracts
- [x] TraitsAndDoctrinesFeatureTests::TraitContractsDoNotEmitRuntimeDispatchSurface
- [x] TraitsAndDoctrinesFeatureTests::GenericTraitBoundDispatchLowersToDirectConcreteCall
- [x] TraitsAndDoctrinesFeatureTests::ImportedGenericTraitBoundDispatchLowersToDirectConcreteCall
- [x] TraitsAndDoctrinesFeatureTests::TraitDefaultMethodsDispatchToMonomorphizedDirectCalls
- [x] TraitsAndDoctrinesFeatureTests::DynTraitObjectDispatchesThroughVtablePreservingEffectContract
- [x] TraitsAndDoctrinesFeatureTests::TraitAssociatedTypeRequirementSubstitutesIntoConcreteImplementation
- [x] TraitsAndDoctrinesFeatureTests::TraitDefaultAssociatedTypeDoesNotRequireImplementationAlias

### TraversalLoopFeatureTests  (4/4)
- [x] TraversalLoopFeatureTests::ForInTraversesFixedArraysWithBorrowElements
- [x] TraversalLoopFeatureTests::ForInCanExposeCheckedIndexAndMutableElementBorrow
- [x] TraversalLoopFeatureTests::ForInTraversesSlicesWithoutIteratorAllocation
- [x] TraversalLoopFeatureTests::ForInTraversesDynamicStorage

### TryPropagationImportedModuleTests  (1/1)
- [x] TryPropagationImportedModuleTests::TryInsideImportedSourceModuleLowersThroughTheWholePipeline

### V1LoweringContractTests  (9/9)
- [x] V1LoweringContractTests::InternalScalarFunctionsUseFastccAndDirectScalarAbi
- [x] V1LoweringContractTests::InternalTextAndSliceValuesKeepConcretePtrLengthAbi
- [x] V1LoweringContractTests::FfiAsciiBoundaryDoesNotUseFastccAndLowersToPointerAbi
- [x] V1LoweringContractTests::SafeExportedMainDoesNotNeedUnsafeOrFfiAndUsesNativeCallingConvention
- [x] V1LoweringContractTests::ExportedStarkFunctionsUseNativeCallingConventionWithoutFfiUnsafe
- [x] V1LoweringContractTests::BorrowedAggregatesRemainIndirectWithReadonlyFacts
- [x] V1LoweringContractTests::SixteenByteAggregatesStayDirectByValue
- [x] V1LoweringContractTests::AggregatesLargerThanSixteenBytesUseIndirectAbi
- [x] V1LoweringContractTests::FixedArraysFollowTheSameSixteenByteThreshold

## Parse / syntax-error expectations (Stark source -> parse error)  (10/10)

### CompilerPipelineFullIntegrationTests  (1/1)
- [x] CompilerPipelineFullIntegrationTests::ParseErrorsPreventLaterPassesFromRunning

### DiagnosticRegressionTests  (3/3)
- [x] DiagnosticRegressionTests::MalformedSyntaxProducesAStableParseDiagnostic
- [x] DiagnosticRegressionTests::InvalidEscapeSequencesProduceStableParseDiagnostics
- [x] DiagnosticRegressionTests::CharacterLiteralsRequireExactlyOneDecodedCharacter

### ParserConformanceTests  (1/1)
- [x] ParserConformanceTests::InvalidProgramsDoNotParse

### ParserEdgeCaseTests  (1/1)
- [x] ParserEdgeCaseTests::InvalidProgramsDoNotParse

### ParserSmokeTests  (1/1)
- [x] ParserSmokeTests::InvalidProgramsDoNotParse

### RawMultilineStringParityTests  (3/3)
- [x] RawMultilineStringParityTests::ContentOnTheOpeningQuoteLineIsDiagnosed
- [x] RawMultilineStringParityTests::LinesIndentedLessThanTheClosingQuotesAreDiagnosed
- [x] RawMultilineStringParityTests::ContentBeforeTheClosingQuotesOnTheirLineIsDiagnosed

## Parsing & syntax-model shape  (12/12)

### CompilerPipelineFullIntegrationTests  (1/1)
- [x] CompilerPipelineFullIntegrationTests::AsmDeclarationsSelectTheMatchingTargetAndPreserveSyntaxMetadata

### CompilerPipelineSyntaxModelTests  (7/7)
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueModuleAttributeFlowsIntoSyntaxModel
- [x] CompilerPipelineSyntaxModelTests::TestingCallableAttributesFlowIntoSyntaxModelAsMetadata
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueAttributesFlowIntoCallableAndTypeSyntaxModels
- [x] CompilerPipelineSyntaxModelTests::ThreadSafetyLawPredicatesAndAttributesFlowIntoSyntaxModel
- [x] CompilerPipelineSyntaxModelTests::StructAndRecordDestructorsFlowIntoSyntaxModel
- [x] CompilerPipelineSyntaxModelTests::TypeAliasDeclarationsFlowIntoSyntaxModel
- [x] CompilerPipelineSyntaxModelTests::GenericFunctionDeclarationsCarryTypeParametersIntoSyntaxModel

### FunctionSemanticsTests  (1/1)
- [x] FunctionSemanticsTests::MemberFunctionVisibilityInheritsNarrowsAndAvoidsAccidentalExport

### ParserConformanceTests  (1/1)
- [x] ParserConformanceTests::ValidProgramsParse

### ParserEdgeCaseTests  (1/1)
- [x] ParserEdgeCaseTests::ValidProgramsParse

### ParserSmokeTests  (1/1)
- [x] ParserSmokeTests::ValidProgramsParse

## Semantic / lowering diagnostics  (391/391)

### CompilerPipelineFullIntegrationTests  (8/8)
- [x] CompilerPipelineFullIntegrationTests::AsmDeclarationsReportMissingTargetMatchesAndRejectUnsupportedV1Shapes
- [x] CompilerPipelineFullIntegrationTests::AsmDeclarationsReportMultipleMatchingTargetSpecificDefinitions
- [x] CompilerPipelineFullIntegrationTests::AsmDeclarationsRejectInvalidRegistersAndOperandBindingConflicts
- [x] CompilerPipelineFullIntegrationTests::VoidCallsUsedAsValuesFailDuringTypeCheckingBeforeMirLowering
- [x] CompilerPipelineFullIntegrationTests::EmitLlvmModesReportTypeDiagnosticsBeforeLoweringForVoidCallsUsedAsValues
- [x] CompilerPipelineFullIntegrationTests::PrivateTransitiveImportsDoNotBecomeVisibleToTheRootModule
- [x] CompilerPipelineFullIntegrationTests::UnresolvedImportsFailBeforeTypingAndLowering
- [x] CompilerPipelineFullIntegrationTests::InvalidTypedAssignmentsProduceTypeDiagnostics

### CompilerPipelineSpecializationPlanTests  (1/1)
- [x] CompilerPipelineSpecializationPlanTests::ConflictingSpecializationSymbolPlansReportAmbiguityDiagnostics

### CompilerPipelineSyntaxModelTests  (5/5)
- [x] CompilerPipelineSyntaxModelTests::UnsupportedModuleAttributesReportSyntaxModelDiagnostics
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueCallableAttributesRejectConflictingModifiers
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueCallableAttributesRejectDuplicates
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueTypeAndContractAttributesReportDiagnostics
- [x] CompilerPipelineSyntaxModelTests::MalformedThreadSafetyLawSurfaceReportsSyntaxModelDiagnostics

### ComptimeFeatureTests  (58/58)
- [x] ComptimeFeatureTests::ComptimeGenericArithmeticStillRejectsRuntimeValues
- [x] ComptimeFeatureTests::ComptimeExpressionRejectsPlainFunctionCall
- [x] ComptimeFeatureTests::ComptimeExpressionRejectsPlainReceiverMethodCall
- [x] ComptimeFeatureTests::ComptimeVisibilityStructuralFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeVisibilityStructuralFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeStructuralFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeStructuralCountFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeTypeLayoutFactsRequireConcreteLayout
- [x] ComptimeFeatureTests::ComptimeTypeLayoutFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeScalarTypeMetadataFactsRejectWrongTargets
- [x] ComptimeFeatureTests::ComptimeScalarTypeMetadataFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeRawPointerMetadataFactsRejectWrongTargets
- [x] ComptimeFeatureTests::ComptimeRawPointerMetadataFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeTypeElementMetadataFactsRejectWrongTargets
- [x] ComptimeFeatureTests::ComptimeTypeElementMetadataFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeIndexedStructuralFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeStructLayoutAttributeFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeThreadSafetyLawAttributeFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeMethodThreadSafetyLawPredicateFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeIndexedTypeFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeTypeComptimeArgumentFactsRejectWrongTargetsAndOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeFunctionPointerStructuralFactsRejectWrongTargetsAndOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeCallableNestedTypeArgumentStructuralFactsRejectWrongTargetsAndOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeClosureStructuralFactsRejectWrongTargetsAndOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeMethodStructuralFactsRejectWrongTargetsAndOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeIndexedNameFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeEnumPayloadNameFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeEnumVariantFunnelFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeTypeGenericParameterFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeIndexedStructuralFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeStructLayoutAttributeFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeThreadSafetyLawAttributeFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeMethodThreadSafetyLawPredicateFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeIndexedTypeFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeFunctionPointerStructuralFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeIndexedNameFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeEnumPayloadNameFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeEnumTagFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeEnumPayloadLayoutFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeEnumVariantFunnelFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeTypeGenericParameterFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeAssociatedTypeTargetCategoryFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeAssociatedTypeTargetMetadataFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeAssociatedTypeFactsRejectOutOfRangeIndices
- [x] ComptimeFeatureTests::ComptimeAssociatedTypeFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeTraitConformanceFactRequiresTraitArgument
- [x] ComptimeFeatureTests::ComptimeImplementedTraitStructuralFactsValidateIndexedArguments
- [x] ComptimeFeatureTests::ComptimeDynTraitTargetFactRejectsWrongTargetAndSecondArgument
- [x] ComptimeFeatureTests::ComptimeTraitConformanceFactIsRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeImplementedTraitStructuralFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeDynTraitStructuralFactsAreRejectedAtRuntime
- [x] ComptimeFeatureTests::ComptimeWillexitWhileReportsIterationBudgetExhaustion
- [x] ComptimeFeatureTests::ComptimeWillexitForReportsIterationBudgetExhaustion
- [x] ComptimeFeatureTests::ComptimeWillexitForTraversalReportsIterationBudgetExhaustion
- [x] ComptimeFeatureTests::ComptimeRecursiveFunctionCallReportsNonTerminatingEvaluation
- [x] ComptimeFeatureTests::ComptimeBlockRequiresWillexitLoops
- [x] ComptimeFeatureTests::ComptimeExpressionRejectsRuntimeDependentValue
- [x] ComptimeFeatureTests::ComptimeBlockRejectsRuntimeDependentValue

### CopyableDoctrineTests  (3/3)
- [x] CopyableDoctrineTests::WhereCopyableBoundAcceptsCopyableAndRejectsOwningOrDestructorTypes
- [x] CopyableDoctrineTests::CopyableAssertionAttributeChecksStructurallyAtTheDefinition
- [x] CopyableDoctrineTests::FixedArrayOfMoveOnlyElementsStaysMoveOnly

### DiagnosticRegressionTests  (11/11)
- [x] DiagnosticRegressionTests::SelfImportsProduceAStableFrontEndDiagnostic
- [x] DiagnosticRegressionTests::LawsRejectOutParameters
- [x] DiagnosticRegressionTests::ImmutableGlobalsRejectRebindingWithSpecificDiagnostic
- [x] DiagnosticRegressionTests::ConstGlobalsRejectMutationWithSpecificDiagnostic
- [x] DiagnosticRegressionTests::NamedAggregateWholeValueTypedCaptureMarksFollowingDefaultUnreachable
- [x] DiagnosticRegressionTests::CapturePatternsMixedWithOtherLabelsProduceAnExplicitDiagnostic
- [x] DiagnosticRegressionTests::UnreachableSwitchLabelsPointBackToTheCoveringArm
- [x] DiagnosticRegressionTests::BreakOutsideLoopOrSwitchProducesAStableSemanticDiagnostic
- [x] DiagnosticRegressionTests::ContinueOutsideLoopProducesAStableSemanticDiagnostic
- [x] DiagnosticRegressionTests::WhereDoctrinePredicateExplainsDoctrineMisuse
- [x] DiagnosticRegressionTests::WhereUnknownLawPredicateKeepsOriginalMessage

### FunctionSemanticsTests  (13/13)
- [x] FunctionSemanticsTests::LawFunctionsRejectPlainFunctionPointerCalls
- [x] FunctionSemanticsTests::LawFunctionsRejectPlainClosureCalls
- [x] FunctionSemanticsTests::NonCapturingLawLambdaBodiesRejectNonLawCalls
- [x] FunctionSemanticsTests::RuntimeTextConcatenationPreservesFunctionKindObligations
- [x] FunctionSemanticsTests::PureCallsInheritExternallyVisibleSliceWrites
- [x] FunctionSemanticsTests::InstanceMemberFunctionsCannotBeCalledThroughTypeName
- [x] FunctionSemanticsTests::StaticMemberFunctionsCannotBeCalledThroughInstance
- [x] FunctionSemanticsTests::StaticModifierIsRejectedOutsideStructAndRecordMemberFunctions
- [x] FunctionSemanticsTests::LawBodiesRejectMutableExternalStateObservationAllocationAndVisibleWrites
- [x] FunctionSemanticsTests::LawBodiesRejectExternallyVisibleImplicitDropEffects
- [x] FunctionSemanticsTests::LawBodiesRejectExternallyVisibleImplicitDropEffectsThroughDestructorCalls
- [x] FunctionSemanticsTests::MemberFunctionVisibilityCannotExceedEnclosingTypeVisibility
- [x] FunctionSemanticsTests::ExportMemberFunctionsRequireExportEnclosingTypes

### GenericsFeatureTests  (2/2)
- [x] GenericsFeatureTests::ComptimeGenericValueParameterRejectsOutOfRangeLength
- [x] GenericsFeatureTests::ComptimeGenericParametersMustUseIntegerTypes

### IfWhilePatternDiagnosticsTests  (4/4)
- [x] IfWhilePatternDiagnosticsTests::IfPatternCaptureIsNotVisibleInElseBranch
- [x] IfWhilePatternDiagnosticsTests::IfPatternWithMismatchedEnumTypeIsRejected
- [x] IfWhilePatternDiagnosticsTests::IfPatternOnNonEnumScrutineeIsRejected
- [x] IfWhilePatternDiagnosticsTests::PlainBooleanIfAndWhileStillRequireBool

### ImportedModuleAmbiguityRegressionTests  (3/3)
- [x] ImportedModuleAmbiguityRegressionTests::BareNameAmbiguousAcrossImportsReportsLocatedDiagnosticInsteadOfCrashing
- [x] ImportedModuleAmbiguityRegressionTests::QualifyingTheAmbiguousNameCompilesCleanly
- [x] ImportedModuleAmbiguityRegressionTests::LocalShadowingAnAmbiguousNameIsNotFlagged

### IntegerLiteralTypingRegressionTests  (2/2)
- [x] IntegerLiteralTypingRegressionTests::NonFittingLiteralStillRequiresExplicitCast
- [x] IntegerLiteralTypingRegressionTests::NegativeLiteralIntoUnsignedOperandStillRequiresExplicitCast

### LoweringContractValidationTests  (20/20)
- [x] LoweringContractValidationTests::MissingBoundCallOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingBoundIndexOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingBoundObjectCreationOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingBoundDynamicStorageOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingBoundTextOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingBoundSwitchOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingTypedCallFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingTypedIndexFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingDynamicStorageOperationFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingSwitchFactsFailBeforeMir
- [x] LoweringContractValidationTests::MissingEnumPatternFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedDirectCallArityFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedDirectCallArgumentAddressFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedMemberCallExplicitArgumentAddressFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedIndirectCallArgumentAddressFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedIndexArityFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedDynamicStorageOperationNameFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedDynamicStorageReceiverAddressFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedLayoutQueryTargetFactsFailBeforeMir
- [x] LoweringContractValidationTests::CorruptedSwitchFamilyFactsFailBeforeMir

### MethodSyntaxHintRegressionTests  (2/2)
- [x] MethodSyntaxHintRegressionTests::FreeFunctionCalledWithMethodSyntaxSuggestsTheFreeCallForm
- [x] MethodSyntaxHintRegressionTests::GenuinelyMissingMemberHasNoFreeFunctionHint

### ModulePrivacyEnforcementTests  (8/8)
- [x] ModulePrivacyEnforcementTests::SiblingCallingModulePrivateFunctionIsRejected
- [x] ModulePrivacyEnforcementTests::SiblingCallingPublicFunctionSucceeds
- [x] ModulePrivacyEnforcementTests::SiblingCallingInternalFunctionInSamePackageSucceeds
- [x] ModulePrivacyEnforcementTests::SameModulePrivateAccessStillCompiles
- [x] ModulePrivacyEnforcementTests::SiblingReadingModulePrivateGlobalIsRejected
- [x] ModulePrivacyEnforcementTests::SiblingReadingPublicGlobalSucceeds
- [x] ModulePrivacyEnforcementTests::SiblingNamingModulePrivateTypeInAnnotationIsRejected
- [x] ModulePrivacyEnforcementTests::SiblingNamingPublicTypeInAnnotationSucceeds

### MultiFileIntegrationTests  (1/1)
- [x] MultiFileIntegrationTests::ModulePrivateDeclarationsStayHiddenAcrossModuleBoundaries

### PackageImageCallableValueTests  (4/4)
- [x] PackageImageCallableValueTests::PackageImageBackedLawFunctionPointerParametersValidateLambdaBodies
- [x] PackageImageCallableValueTests::PackageImageBackedFunctionItemsPreserveFunctionKindObligationRejections
- [x] PackageImageCallableValueTests::PackageImageBackedUnsafeFunctionItemsDoNotPromoteToOrdinaryFunctionPointers
- [x] PackageImageCallableValueTests::PackageImageBackedUnsafeFunctionCallsRequireUnsafeContext

### SemanticValidationTests  (70/70)
- [x] SemanticValidationTests::StrictIntegerRangesRejectNonCanonicalStorageOutsideFfiBoundaries
- [x] SemanticValidationTests::StrictIntegerRangesAllowFfiAbiSignatureStorage
- [x] SemanticValidationTests::StrictIntegerRangesAllowPlatformAnnotatedAbiSignatureStorage
- [x] SemanticValidationTests::StrictIntegerRangesAllowPlatformAnnotatedAggregateFieldStorage
- [x] SemanticValidationTests::StrictIntegerRangesStillRejectNonCanonicalLocalsInsidePlatformAnnotatedFunctions
- [x] SemanticValidationTests::StrictIntegerRangesRejectUnnecessarilyWideUnsignedAndSignedRanges
- [x] SemanticValidationTests::TopLevelRegisterStorageIsRejected
- [x] SemanticValidationTests::RegisterLocalsCannotBeAddressed
- [x] SemanticValidationTests::ArenaLocalStorageIsRejectedUntilArenaLoweringExists
- [x] SemanticValidationTests::FunctionLocalStaticStorageIsRejectedUntilStaticLocalSemanticsExist
- [x] SemanticValidationTests::NestedRawPointersAreRejectedOutsideFfiBoundaries
- [x] SemanticValidationTests::NestedRawPointersAreAllowedOnFfiBoundaries
- [x] SemanticValidationTests::NestedRawPointersAreAllowedInPlatformAggregateFields
- [x] SemanticValidationTests::PublicSafeRawAllocationApisAreRejected
- [x] SemanticValidationTests::PublicSafeRawFreeApisAreRejected
- [x] SemanticValidationTests::InternalRawAllocationApisRemainAvailableForLowLevelImplementation
- [x] SemanticValidationTests::FfiRawAllocationBoundariesRemainAvailable
- [x] SemanticValidationTests::PublicSafeNonAllocationRawPointerViewsRemainAvailable
- [x] SemanticValidationTests::ConstGlobalsRejectReachableRawPointers
- [x] SemanticValidationTests::ConstSliceGlobalsRejectNonMaterializableExpressionInitializers
- [x] SemanticValidationTests::ConstGlobalsAllowPureArithmeticInitializers
- [x] SemanticValidationTests::ConstGlobalsRejectNonEvaluableFunctionCallInitializers
- [x] SemanticValidationTests::LawsCanReadConstGlobalValues
- [x] SemanticValidationTests::LawsCannotWriteGlobalState
- [x] SemanticValidationTests::LawsCannotAllocateDynamicStorage
- [x] SemanticValidationTests::LawsCannotReserveDynamicStorage
- [x] SemanticValidationTests::LawsCannotTryReserveDynamicStorage
- [x] SemanticValidationTests::LawsCannotMoveLastFromDynamicStorage
- [x] SemanticValidationTests::LawsCannotMoveAtFromDynamicStorage
- [x] SemanticValidationTests::DynamicStorageAllowsConcreteDestructibleElementTypes
- [x] SemanticValidationTests::LawsCannotCallNonLawFunctions
- [x] SemanticValidationTests::LawsCanCallPlainFnsWhenTheCompilerCanProveTheyArePure
- [x] SemanticValidationTests::LawsCanMutatePurelyLocalState
- [x] SemanticValidationTests::LawsCannotWriteThroughBorrowedMemory
- [x] SemanticValidationTests::FiniteFunctionsRejectNonWillexitLoops
- [x] SemanticValidationTests::FiniteFunctionsRejectNonDeterministicForLoops
- [x] SemanticValidationTests::InfiniteLoopsMustUseStaticallyUnconditionalConditions
- [x] SemanticValidationTests::InfiniteLoopsRejectStructuralExit
- [x] SemanticValidationTests::WillexitWhileLoopsWithUnconditionalConditionsRequireStructuralExit
- [x] SemanticValidationTests::WillexitForLoopsWithOmittedConditionsRequireStructuralExit
- [x] SemanticValidationTests::WillexitLoopsAcceptStructuralExitForUnconditionalConditions
- [x] SemanticValidationTests::BreakOutsideLoopOrSwitchIsRejected
- [x] SemanticValidationTests::ContinueOutsideLoopIsRejectedEvenInsideSwitch
- [x] SemanticValidationTests::BreakInsideSwitchIsAllowed
- [x] SemanticValidationTests::BreakToSwitchLabelIsAllowed
- [x] SemanticValidationTests::BreakToMissingLabelIsRejected
- [x] SemanticValidationTests::ContinueToSwitchLabelIsRejected
- [x] SemanticValidationTests::DuplicateActiveControlFlowLabelsAreRejected
- [x] SemanticValidationTests::LabeledBreakSatisfiesOuterWillexitLoopContract
- [x] SemanticValidationTests::WillexitLoopDoesNotTreatSwitchBreakAsALoopExit
- [x] SemanticValidationTests::InfiniteLoopAllowsSwitchBreakBecauseItOnlyExitsTheSwitch
- [x] SemanticValidationTests::FunctionModifiersRejectConflictingInlinePreferences
- [x] SemanticValidationTests::FunctionModifiersRejectHotAndColdTogether
- [x] SemanticValidationTests::FiniteFunctionsCannotCallNonFiniteFunctions
- [x] SemanticValidationTests::FiniteFunctionsCanCallPlainFnsWhenTheCompilerCanProveTheyAreFinite
- [x] SemanticValidationTests::FiniteFunctionsRejectRecursiveCallCycles
- [x] SemanticValidationTests::DoctrineLawMembersCanReadConstGlobalValues
- [x] SemanticValidationTests::ReadOnlyDropBlocksCannotMutateSelf
- [x] SemanticValidationTests::MutDropWithoutSelfMutationProducesWarning
- [x] SemanticValidationTests::MutDropCallingSelfMethodDoesNotProduceFalseWarning
- [x] SemanticValidationTests::DestructorBlocksCannotReturn
- [x] SemanticValidationTests::BaseListRejectsClassStyleInheritanceFromStruct
- [x] SemanticValidationTests::BaseListRejectsTraitMethodKindMismatch
- [x] SemanticValidationTests::BaseListRejectsTraitMethodParameterTypeMismatch
- [x] SemanticValidationTests::BaseListRejectsMissingTraitMethod
- [x] SemanticValidationTests::BaseListRejectsMissingTraitMethodFromImportedTrait
- [x] SemanticValidationTests::BaseListRejectsImportedTraitMethodSignatureMismatch
- [x] SemanticValidationTests::DynTraitObjectRejectsStaticOnlyTrait
- [x] SemanticValidationTests::DynTraitRejectsNonObjectSafeGenericMethod
- [x] SemanticValidationTests::GenericConstraintRejectsNonConformingTypeArgument

### SwitchExhaustivenessDiagnosticsTests  (9/9)
- [x] SwitchExhaustivenessDiagnosticsTests::LiteralCoveredByEarlierRangePatternIsRejectedAsUnreachable
- [x] SwitchExhaustivenessDiagnosticsTests::EnumSwitchMissingVariantIsRejected
- [x] SwitchExhaustivenessDiagnosticsTests::BoolSwitchMissingFalseIsRejected
- [x] SwitchExhaustivenessDiagnosticsTests::RangedIntegerSwitchMissingValueIsRejected
- [x] SwitchExhaustivenessDiagnosticsTests::StatementPositionEnumSwitchMissingVariantIsRejected
- [x] SwitchExhaustivenessDiagnosticsTests::WhenGuardedLabelsDoNotCountTowardCoverage
- [x] SwitchExhaustivenessDiagnosticsTests::FunctionWithoutReturnIsRejected
- [x] SwitchExhaustivenessDiagnosticsTests::IfWithoutElseReturningIsRejected
- [x] SwitchExhaustivenessDiagnosticsTests::InfiniteLoopWithBreakAsFinalStatementIsRejected

### ThreadSafetyLawFeatureTests  (6/6)
- [x] ThreadSafetyLawFeatureTests::ConflictingLawAttributesAreTypeCheckErrors
- [x] ThreadSafetyLawFeatureTests::MissingGenericFunctionLawPredicatePropagationIsATypeCheckError
- [x] ThreadSafetyLawFeatureTests::FunctionLawPredicateFailureNamesResponsibleFieldChain
- [x] ThreadSafetyLawFeatureTests::MethodLawPredicatesAreEnforcedAtCallSites
- [x] ThreadSafetyLawFeatureTests::ImportedSourceFunctionLawPredicatesAreEnforcedAtCallSites
- [x] ThreadSafetyLawFeatureTests::ThreadEntryReachabilityRejectsPlainMutableStatics

### TraitsAndDoctrinesFeatureTests  (4/4)
- [x] TraitsAndDoctrinesFeatureTests::DynTraitVtableTypeRejectsByValueAndMutablePointerUse
- [x] TraitsAndDoctrinesFeatureTests::UnsafeDynTraitFromPartsRejectsSafeUseAndWrongVtable
- [x] TraitsAndDoctrinesFeatureTests::TraitAssociatedTypeRequirementMustBeDefinedByImplementer
- [x] TraitsAndDoctrinesFeatureTests::TraitAssociatedTypeMismatchIsAConformanceError

### TryPropagationDiagnosticsTests  (11/11)
- [x] TryPropagationDiagnosticsTests::TryNestedInsideCallArgumentIsRejected
- [x] TryPropagationDiagnosticsTests::TryInFunctionWithoutPropagatableReturnIsRejected
- [x] TryPropagationDiagnosticsTests::TryOnEnumWithoutRolesIsRejected
- [x] TryPropagationDiagnosticsTests::TryMixingPayloadAndUnitFailuresIsRejected
- [x] TryPropagationDiagnosticsTests::CrossFamilyTryWithoutFromFunnelIsRejected
- [x] TryPropagationDiagnosticsTests::DuplicateFromFunnelForOneSourceTypeIsRejected
- [x] TryPropagationDiagnosticsTests::UnknownVariantAttributeIsRejected
- [x] TryPropagationDiagnosticsTests::RoleAttributeWithArgumentsIsRejected
- [x] TryPropagationDiagnosticsTests::RolesOnThreeVariantEnumAreRejected
- [x] TryPropagationDiagnosticsTests::SingleRoleWithoutItsPairIsRejected
- [x] TryPropagationDiagnosticsTests::RoleVariantWithMultiplePayloadsIsRejected

### TypeCheckingTests  (67/67)
- [x] TypeCheckingTests::CompileTimeStructuralFactsCannotPromoteToRuntimeCallableValues
- [x] TypeCheckingTests::FunctionPointerAbiIsPartOfTypeIdentity
- [x] TypeCheckingTests::SystemCCVoidIsValidOnlyBehindRawPointers
- [x] TypeCheckingTests::StructLayoutDiagnosticsRejectInvalidLayoutShapes
- [x] TypeCheckingTests::SafeBorrowsRejectMisalignedPackedFields
- [x] TypeCheckingTests::FunctionPointerOverlapTargetRejectsDefaultDisjointFunctionItems
- [x] TypeCheckingTests::LawClosureTypesRejectMutableOrOnceCapabilities
- [x] TypeCheckingTests::FunctionItemPromotionRejectsUnsatisfiedFunctionKindObligations
- [x] TypeCheckingTests::FunctionPointerPromotionRejectsAbiMismatchedFunctionItems
- [x] TypeCheckingTests::MutableClosureCallsRequireMutableClosureAccess
- [x] TypeCheckingTests::HeapClosureLambdasRequireHeapPrefixAndHeapSafeCaptures
- [x] TypeCheckingTests::InlineClosureTypesAreOnlyValidAsParameters
- [x] TypeCheckingTests::NonCapturingLambdasCannotUseOuterLocalsWithoutCaptureList
- [x] TypeCheckingTests::CapturingLambdaSyntaxIsCheckedButNotLoweredYet
- [x] TypeCheckingTests::ExplicitCaptureListDoesNotExposeUnlistedOuterLocals
- [x] TypeCheckingTests::ExplicitCaptureListsRejectDuplicateCapturedLocals
- [x] TypeCheckingTests::LambdaParametersCannotReuseCapturedNames
- [x] TypeCheckingTests::ExplicitCaptureListsReportUnknownClauseNamesAndModes
- [x] TypeCheckingTests::UnsafeLambdaCaptureModesRequireUnsafeContext
- [x] TypeCheckingTests::UnsafeLambdaCaptureModesRequireExplicitUnsafeMarkerAndRejectSafeModeMarkers
- [x] TypeCheckingTests::CopyLambdaCapturesRejectMoveOnlyBindings
- [x] TypeCheckingTests::CopyLambdaCapturesAllowTextViews
- [x] TypeCheckingTests::ReadAndCopyLambdaCapturesDoNotExposeWritableBindings
- [x] TypeCheckingTests::MutLambdaCapturesExposeWritableBindingsInLambdaBody
- [x] TypeCheckingTests::OutAndInitLambdaCapturesRejectReadsInLambdaBody
- [x] TypeCheckingTests::OutAndInitLambdaCapturesAllowWritesInLambdaBody
- [x] TypeCheckingTests::UnsafeAddrLambdaCapturesExposeReadonlyAddressNotCapturedValue
- [x] TypeCheckingTests::UnsafeSharedLambdaCapturesExposeSharedReadOnlyBindings
- [x] TypeCheckingTests::WritableLambdaCaptureModesRequireWritableBindings
- [x] TypeCheckingTests::UnsafeFunctionsRequireUnsafeContextDuringTypeChecking
- [x] TypeCheckingTests::RawPointerSignaturesRequireUnsafeFunctions
- [x] TypeCheckingTests::RawPointerLocalOperationsRequireUnsafeContext
- [x] TypeCheckingTests::FfiDeclarationsRequireUnsafeModifier
- [x] TypeCheckingTests::UnsafeFunctionItemsDoNotPromoteToOrdinaryFunctionPointers
- [x] TypeCheckingTests::UnsafeFunctionItemsPromoteToUnsafeFunctionPointersAndCallsRequireUnsafeContext
- [x] TypeCheckingTests::UnsafeFunctionItemsDoNotPromoteInReturnOrArgumentTargetPositions
- [x] TypeCheckingTests::StrictIntegerRangesRejectExplicitScalarConstWrongWidthOrSign
- [x] TypeCheckingTests::ScalarConstDeclarationsReportFriendlyNumericTypeDiagnostics
- [x] TypeCheckingTests::HugeCompileTimeIntegerConstCannotBecomeRuntimeStorage
- [x] TypeCheckingTests::HugeCompileTimeIntegerConversionReportsConcreteTargetOverflow
- [x] TypeCheckingTests::DictionaryRejectsUnprovenKeyTypes
- [x] TypeCheckingTests::HashSetRequiresExplicitHashEqualsKeyContractForNonPrimitiveKeys
- [x] TypeCheckingTests::DictionaryRejectsStaticHashEqualsContractWithoutOverlap
- [x] TypeCheckingTests::DictionaryRejectsStaticHashEqualsContractWithWrongHashReturn
- [x] TypeCheckingTests::DictionaryRejectsStaticHashEqualsContractMissingEquals
- [x] TypeCheckingTests::HashSetRejectsStaticHashEqualsContractWithWrongEqualsReturn
- [x] TypeCheckingTests::DictionaryRejectsUnprovenKeyTypesAfterGenericMonomorphization
- [x] TypeCheckingTests::AmbiguousImportedTypeFinalNamesRequireQualification
- [x] TypeCheckingTests::StrictIntegerRangesRejectSignedEndpointsOutsideBaseType
- [x] TypeCheckingTests::UnsupportedIntegerRangeEndpointIdentifiersAreRejected
- [x] TypeCheckingTests::ManualFullWidthIntegerRangeEndpointsAreRejectedInFavorOfMinMax
- [x] TypeCheckingTests::ConstantArithmeticIntegerRangeEndpointOverflowIsRejected
- [x] TypeCheckingTests::ConstantArithmeticIntegerRangeEndpointDivisionByZeroIsRejected
- [x] TypeCheckingTests::ReversedTypeRelativeIntegerRangeEndpointsAreRejected
- [x] TypeCheckingTests::UnsignedIntegerRangeEndpointsBelowZeroAreRejected
- [x] TypeCheckingTests::ReversedConstantArithmeticIntegerRangeEndpointsAreRejected
- [x] TypeCheckingTests::TypeRelativeIntegerEndpointNamesAreRejectedOutsideIntegerRanges
- [x] TypeCheckingTests::BitwiseXorRequiresIntegerOperands
- [x] TypeCheckingTests::GenericTypeWithWrongArgCountIsAnError
- [x] TypeCheckingTests::NonGenericTypeWithTypeArgumentsIsAnError
- [x] TypeCheckingTests::TargetTypedObjectCreationRequiresNamedDestinationType
- [x] TypeCheckingTests::DynamicStorageReserveRequiresMutableOwnerAndNonNegativeAdditionalCapacity
- [x] TypeCheckingTests::DynamicStorageMoveLastRequiresMutableOwnerAndNoArguments
- [x] TypeCheckingTests::DynamicStorageMoveAtRequiresMutableOwnerOneIntegerArgument
- [x] TypeCheckingTests::DynamicStorageReserveOperationsRequireMutableOwnerAndOneIntegerArgument
- [x] TypeCheckingTests::DynamicStorageOperationsRequireAddressableOwner
- [x] TypeCheckingTests::InitSliceElementsAreWriteOnlyUntilInitialized

### TypeTypingDiagnosticsTests  (72/72)
- [x] TypeTypingDiagnosticsTests::RangePatternRequiresIntegerTarget
- [x] TypeTypingDiagnosticsTests::RangePatternRejectsLowerBoundGreaterThanUpperBound
- [x] TypeTypingDiagnosticsTests::RangePatternRejectsNonOverlappingIntegerDomain
- [x] TypeTypingDiagnosticsTests::VarargsModifierRequiresFfiDeclaration
- [x] TypeTypingDiagnosticsTests::FfiVarargsRejectsArgumentsThatNeedHiddenCPromotion
- [x] TypeTypingDiagnosticsTests::FfiVarargsRejectsStarkTextForPercentSFormatArguments
- [x] TypeTypingDiagnosticsTests::FfiVarargsRejectsStarkTextForPercentSConstFormatArguments
- [x] TypeTypingDiagnosticsTests::ConstructorArgumentsAreCheckedAgainstRecordPrimaryShape
- [x] TypeTypingDiagnosticsTests::ConstructorArityMismatchReportsAvailableShapes
- [x] TypeTypingDiagnosticsTests::ExplicitConstructorsSuppressImplicitDefaultConstructor
- [x] TypeTypingDiagnosticsTests::TupleLikeEnumConstructorsRejectArityMismatchDuringTypeChecking
- [x] TypeTypingDiagnosticsTests::ObjectInitializersRejectDuplicateMembers
- [x] TypeTypingDiagnosticsTests::ObjectInitializersRejectMembersAlreadySuppliedByPrimaryConstructor
- [x] TypeTypingDiagnosticsTests::ObjectCreationRequiresExplicitFunctionPointerFields
- [x] TypeTypingDiagnosticsTests::ArrayInitializersRequireEveryFunctionPointerElement
- [x] TypeTypingDiagnosticsTests::FunctionNamesAreRejectedAsRuntimeValuesDuringTypeChecking
- [x] TypeTypingDiagnosticsTests::ExportedFunctionsRejectEnumTypesAtAbiBoundaries
- [x] TypeTypingDiagnosticsTests::FfiFunctionsRejectEnumTypesAtAbiBoundaries
- [x] TypeTypingDiagnosticsTests::FfiFunctionsRejectTextViewReturnTypes
- [x] TypeTypingDiagnosticsTests::ExportedFunctionsRejectAggregateTypesThatTransitivelyDependOnEnums
- [x] TypeTypingDiagnosticsTests::TypeAliasesShareTheUnderlyingOverloadIdentity
- [x] TypeTypingDiagnosticsTests::TypeAliasCyclesAreRejected
- [x] TypeTypingDiagnosticsTests::FfiFunctionsRejectAggregateTypesThatTransitivelyDependOnEnums
- [x] TypeTypingDiagnosticsTests::GlobalTypesRejectAggregateTypesThatTransitivelyDependOnEnums
- [x] TypeTypingDiagnosticsTests::AggregateSwitchPatternsRejectMoveOnlyFieldCaptures
- [x] TypeTypingDiagnosticsTests::FixedArrayListSwitchPatternsRejectWrongLength
- [x] TypeTypingDiagnosticsTests::SwitchOrPatternsRejectInconsistentSharedCaptures
- [x] TypeTypingDiagnosticsTests::SwitchPatternsRejectDuplicateCapturesInOneAlternative
- [x] TypeTypingDiagnosticsTests::ExhaustiveBoolSwitchRejectsLaterDefaultLabel
- [x] TypeTypingDiagnosticsTests::AggregateWildcardPatternRejectsLaterSpecificArm
- [x] TypeTypingDiagnosticsTests::BroaderAggregatePatternRejectsLaterSpecificArm
- [x] TypeTypingDiagnosticsTests::NestedAggregatePatternRejectsLaterSpecificArm
- [x] TypeTypingDiagnosticsTests::ExhaustiveEnumCasePatternsRejectLaterDefaultLabel
- [x] TypeTypingDiagnosticsTests::BroaderEnumTuplePatternRejectsLaterSpecificArm
- [x] TypeTypingDiagnosticsTests::ArrayInitializerMismatchesUseExpectedActualWording
- [x] TypeTypingDiagnosticsTests::SliceVariablesCannotUseArrayInitializerSyntax
- [x] TypeTypingDiagnosticsTests::SliceMembersCannotUseArrayInitializerSyntax
- [x] TypeTypingDiagnosticsTests::ReturnMismatchesExplainWhenExplicitConversionIsRequired
- [x] TypeTypingDiagnosticsTests::ImmutableLocalAddressCannotInitializeMutableRawPointer
- [x] TypeTypingDiagnosticsTests::ImmutableLocalReadonlyPointerCannotBeUpgradedToMutableRawPointer
- [x] TypeTypingDiagnosticsTests::ConstArrayDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers
- [x] TypeTypingDiagnosticsTests::ConstArrayDerivedReadonlyPointersCannotBeLaunderedThroughIntegers
- [x] TypeTypingDiagnosticsTests::ConstFieldDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers
- [x] TypeTypingDiagnosticsTests::ConstFieldDerivedReadonlyPointersCannotBeLaunderedThroughIntegers
- [x] TypeTypingDiagnosticsTests::MemberAssignmentsReportExpectedAndActualTypes
- [x] TypeTypingDiagnosticsTests::CallMismatchesReportArgumentPositions
- [x] TypeTypingDiagnosticsTests::ExplicitArithmeticOperatorsRequireIntegerOperands
- [x] TypeTypingDiagnosticsTests::RuntimeAsciiAndUnicodeCastsStillRequireOwningTextStorage
- [x] TypeTypingDiagnosticsTests::RuntimeUnicodeToAsciiCastsStillRequireCompileTimeTextConstants
- [x] TypeTypingDiagnosticsTests::RuntimeTextConcatenationExplainsStorageRequirement
- [x] TypeTypingDiagnosticsTests::RuntimeTextBufferConcatenationExplainsFixedCapacityRequirement
- [x] TypeTypingDiagnosticsTests::FixedTextStorageCapacityIsOnlyForStackTextBuffers
- [x] TypeTypingDiagnosticsTests::RuntimeInterpolatedTextExplainsStorageRequirement
- [x] TypeTypingDiagnosticsTests::FixedCapacityInterpolatedTextRequiresKnownFormatter
- [x] TypeTypingDiagnosticsTests::VoidReturningCallsCannotBeComparedAsValues
- [x] TypeTypingDiagnosticsTests::TextAccessRejectsMoreThanTwoIndices
- [x] TypeTypingDiagnosticsTests::DynamicStorageAccessRejectsMoreThanTwoIndices
- [x] TypeTypingDiagnosticsTests::EmptyIndexAccessProducesATypeDiagnostic
- [x] TypeTypingDiagnosticsTests::CallingANonFunctionMemberIncludesMemberContext
- [x] TypeTypingDiagnosticsTests::IndexingANonIndexableMemberIncludesMemberContext
- [x] TypeTypingDiagnosticsTests::AddressOfRequiresAnAddressableOperand
- [x] TypeTypingDiagnosticsTests::DereferenceRequiresARawPointerOperand
- [x] TypeTypingDiagnosticsTests::DoctrinesCannotBeUsedAsRuntimeValueTypes
- [x] TypeTypingDiagnosticsTests::TraitsCannotBeUsedAsRuntimeValueTypes
- [x] TypeTypingDiagnosticsTests::TraitMethodsCannotBeCalledDirectly
- [x] TypeTypingDiagnosticsTests::OverloadResolutionReportsNoMatchingCandidates
- [x] TypeTypingDiagnosticsTests::GenericCallsWithoutInferableTypeArgumentsReportNoMatchingCandidates
- [x] TypeTypingDiagnosticsTests::OverloadResolutionReportsAmbiguousCalls
- [x] TypeTypingDiagnosticsTests::MemberOverloadResolutionReportsNoMatchingCandidates
- [x] TypeTypingDiagnosticsTests::MemberOverloadResolutionReportsAmbiguousCalls
- [x] TypeTypingDiagnosticsTests::VoidCallsUsedAsValuesFailBeforeMirLowering
- [x] TypeTypingDiagnosticsTests::SemanticErrorShapedLoweringFallbackCasesFailBeforeMirLowering

### ValueContractTests  (7/7)
- [x] ValueContractTests::ContractsSeedEntryFactsAndDischargeByGuardOrForwardedContract
- [x] ValueContractTests::UnprovenCallSitesAreDiagnosedWithTheSubstitutedObligation
- [x] ValueContractTests::ConstantArgumentsDischargePureValueComparisons
- [x] ValueContractTests::MirroredComparisonsNormalizeBetweenContractAndCaller
- [x] ValueContractTests::ContractFactsDoNotLeakToUnrelatedIndexes
- [x] ValueContractTests::ArithmeticPropagationProvesComputedOffsetReads
- [x] ValueContractTests::ComputedOffsetReadsWithoutTheContractStayRejected

## Type checking  (159/159)

### CompilerPipelineFullIntegrationTests  (8/8)
- [x] CompilerPipelineFullIntegrationTests::ImportedFunctionsFromLoadedModulesParticipateInTypingAndEffects
- [x] CompilerPipelineFullIntegrationTests::DoctrineDeclarationsFlowIntoSyntaxTypeAndSemanticModels
- [x] CompilerPipelineFullIntegrationTests::TraitDeclarationsFlowIntoSyntaxTypeAndEffectModels
- [x] CompilerPipelineFullIntegrationTests::ImportedDoctrineMembersFromLoadedModulesParticipateInTypingAndEffects
- [x] CompilerPipelineFullIntegrationTests::ImportedTraitMembersFromLoadedModulesParticipateInTypingAndEffects
- [x] CompilerPipelineFullIntegrationTests::PublicReExportsMakeTransitiveModulesVisibleToTheRootModule
- [x] CompilerPipelineFullIntegrationTests::LiteralTypingAndBodyCheckingProduceTypedArtifacts
- [x] CompilerPipelineFullIntegrationTests::EnumDeclarationsFlowIntoSyntaxAndTypeModels

### DiagnosticRegressionTests  (3/3)
- [x] DiagnosticRegressionTests::MissingMembersProduceTypeDiagnostics
- [x] DiagnosticRegressionTests::RawSliceConstructionRejectsMutableViewStrengthening
- [x] DiagnosticRegressionTests::CallingAFieldThatShadowsASameNamedMethodNamesTheShadow

### FunctionSemanticsTests  (9/9)
- [x] FunctionSemanticsTests::PlainFnsReportEffectiveKindsWhenTheyCanBeStrengthened
- [x] FunctionSemanticsTests::PlainFnsWithWillexitLoopsCanStillStrengthenToFiniteKinds
- [x] FunctionSemanticsTests::FunctionKindObligationsAllowMatchingFunctionPointerCalls
- [x] FunctionSemanticsTests::FunctionKindObligationsAllowMatchingClosureCalls
- [x] FunctionSemanticsTests::NonCapturingLawLambdasGetSeparateSemanticSummaries
- [x] FunctionSemanticsTests::LawsCanCallPlainFnsThatInferAsLaws
- [x] FunctionSemanticsTests::FiniteFunctionsCanCallPlainFnsThatInferAsFinite
- [x] FunctionSemanticsTests::StaticMemberFunctionsTypeCheckAndPreserveFunctionKinds
- [x] FunctionSemanticsTests::LawBodiesAllowImplicitDropThatOnlyMutatesDroppedValue

### GenericsFeatureTests  (7/7)
- [x] GenericsFeatureTests::ComptimeGenericValueParameterCanBindFixedArrayLength
- [x] GenericsFeatureTests::ExplicitComptimeGenericNamedTypeArgumentRecordsInstantiation
- [x] GenericsFeatureTests::ValueOnlyComptimeGenericNamedTypeArgumentRecordsInstantiation
- [x] GenericsFeatureTests::SymbolicComptimeGenericValueArgumentForwardsBySourceName
- [x] GenericsFeatureTests::ComptimeGenericStructPreservesSymbolicFixedArrayLength
- [x] GenericsFeatureTests::NestedGenericInstantiationTypeChecks
- [x] GenericsFeatureTests::ExplicitGenericFunctionCallWithOneRuntimeArgumentParsesAsGenericCall

### IfWhilePatternDiagnosticsTests  (3/3)
- [x] IfWhilePatternDiagnosticsTests::IfPatternBindsCaptureIntoThenBranch
- [x] IfWhilePatternDiagnosticsTests::WhilePatternBindsCaptureIntoLoopBody
- [x] IfWhilePatternDiagnosticsTests::PlainBooleanIfAndWhileAcceptBoolConditions

### IntegerLiteralTypingRegressionTests  (5/5)
- [x] IntegerLiteralTypingRegressionTests::AdditiveLiteralAdoptsRangedUnsignedOperandType
- [x] IntegerLiteralTypingRegressionTests::MultiplicativeLiteralAdoptsRangedUnsignedOperandType
- [x] IntegerLiteralTypingRegressionTests::BitwiseLiteralAdoptsRangedUnsignedOperandType
- [x] IntegerLiteralTypingRegressionTests::LiteralAdoptsSignedRangedOperandType
- [x] IntegerLiteralTypingRegressionTests::LiteralAdoptionStripsQualifiersFromTheAnchorOperand

### SemanticValidationTests  (3/3)
- [x] SemanticValidationTests::BaseListAcceptsConformingTraitImplementation
- [x] SemanticValidationTests::GenericConstraintAcceptsConformingTypeArgument
- [x] SemanticValidationTests::BaseListDoesNotRequireDefaultTraitMethods

### SwitchExhaustivenessDiagnosticsTests  (9/9)
- [x] SwitchExhaustivenessDiagnosticsTests::ExhaustiveEnumSwitchWithAllVariantsReturningTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::EnumSwitchWithDefaultArmTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::ExhaustiveBoolSwitchTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::ExhaustiveRangedIntegerSwitchTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::ExhaustiveRangedIntegerSwitchWithRangePatternTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::WideIntegerSwitchWithDefaultArmTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::IfElseWhereBothBranchesReturnTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::InfiniteLoopWithoutBreakAsFinalStatementTypeChecks
- [x] SwitchExhaustivenessDiagnosticsTests::VoidFunctionWithoutReturnTypeChecks

### ThreadSafetyLawFeatureTests  (6/6)
- [x] ThreadSafetyLawFeatureTests::LawFactsDeriveStructurallyAndApplyUnsafeReferenceDefaultsAndOverrides
- [x] ThreadSafetyLawFeatureTests::ConditionalGrantsPropagateThroughGenericInstantiation
- [x] ThreadSafetyLawFeatureTests::SystemThreadingAtomicsReceiveIntrinsicLawGrant
- [x] ThreadSafetyLawFeatureTests::FunctionLawPredicatesAreEnforcedAtCallSites
- [x] ThreadSafetyLawFeatureTests::GenericFunctionLawPredicatesMustBePropagatedThroughOpenCalls
- [x] ThreadSafetyLawFeatureTests::ThreadEntryReachabilityAllowsAtomicMutableStatics

### TraitsAndDoctrinesFeatureTests  (2/2)
- [x] TraitsAndDoctrinesFeatureTests::DynTraitVtableTypeIsNameableAsReadonlyRawPointer
- [x] TraitsAndDoctrinesFeatureTests::UnsafeDynBoxFromPartsConstructsOwnedDynTraitObject

### TryPropagationDiagnosticsTests  (4/4)
- [x] TryPropagationDiagnosticsTests::TrySameErrorTypePropagationTypeChecks
- [x] TryPropagationDiagnosticsTests::TryCrossFamilyPropagationUsesFromFunnel
- [x] TryPropagationDiagnosticsTests::TryUnitFailurePropagationTypeChecks
- [x] TryPropagationDiagnosticsTests::TryAsAssignmentRightSideAndExpressionStatementAreBoundaries

### TypeCheckingTests  (94/94)
- [x] TypeCheckingTests::IntegerExponentiationTypeChecks
- [x] TypeCheckingTests::FunctionItemsPromoteToExplicitFunctionPointersAndIndirectCallsTypeCheck
- [x] TypeCheckingTests::ExplicitFfiFunctionPointerAbiPromotesMatchingForeignFunctionItems
- [x] TypeCheckingTests::StructLayoutAttributesProduceConcreteFieldLayoutFacts
- [x] TypeCheckingTests::SystemCPrimitiveAliasesResolveForTargetDataModel
- [x] TypeCheckingTests::SystemCAliasesParticipateInCStructLayout
- [x] TypeCheckingTests::SystemCCharSignednessIsCompileTimeTargetFact
- [x] TypeCheckingTests::FunctionPointerOverlapContractsAllowAliasingIndirectCalls
- [x] TypeCheckingTests::FunctionPointerSameTargetsAcceptOverlapFunctionsAndRequireSameArguments
- [x] TypeCheckingTests::ClosureTypesResolveInFunctionSignatures
- [x] TypeCheckingTests::FunctionItemPromotionPreservesFunctionKindFacts
- [x] TypeCheckingTests::FunctionItemsPromoteFromEachDeclaredFunctionKind
- [x] TypeCheckingTests::FunctionItemsPromoteInReturnAndArgumentTargetPositions
- [x] TypeCheckingTests::OverloadedFunctionItemPromotionsPreserveDistinctAddressTakenFacts
- [x] TypeCheckingTests::NonCapturingLambdasTypeCheckAsExplicitFunctionPointers
- [x] TypeCheckingTests::LambdasTypeCheckAsExplicitClosureTargets
- [x] TypeCheckingTests::FunctionItemsPromoteToExplicitClosureTargets
- [x] TypeCheckingTests::ClosureCallsTypeCheckAndRecordArgumentFacts
- [x] TypeCheckingTests::LambdaCaptureModeFactsArePreservedInTypeCheckModel
- [x] TypeCheckingTests::UnsafeBlocksPermitRawPointerLocalOperations
- [x] TypeCheckingTests::TypeRelativeIntegerRangeEndpointsResolveAgainstContainingIntegerType
- [x] TypeCheckingTests::ImportedModulePublicMembersResolveByFinalName
- [x] TypeCheckingTests::ScalarConstDeclarationsInferSmallestExactNumericTypes
- [x] TypeCheckingTests::CompileTimeUnaryIntegerConstantsReinferExactResultStorage
- [x] TypeCheckingTests::DictionaryAllowsCompilerProvenKeyTypes
- [x] TypeCheckingTests::DictionaryAllowsExplicitStaticHashEqualsKeyContract
- [x] TypeCheckingTests::HashSetAllowsExplicitStaticHashEqualsKeyContract
- [x] TypeCheckingTests::ConstantArithmeticIntegerRangeEndpointsResolveAtCompileTime
- [x] TypeCheckingTests::UnsignedIntegerRangeLowerBoundMayUseZeroInsteadOfMin
- [x] TypeCheckingTests::ExplicitArithmeticOperatorsTypeCheckWithoutPlaceholderDiagnostics
- [x] TypeCheckingTests::StrictFpModifierTypeChecksNowThatLoweringExists
- [x] TypeCheckingTests::FloatingPointArithmeticChainsTypeCheckAcrossMixedNumericOperands
- [x] TypeCheckingTests::ExplicitConversionsPointerOperatorsAndSliceViewsTypeCheck
- [x] TypeCheckingTests::TextEscapeLiteralsPreferUtf8BackedAsciiUnlessExplicitlyConverted
- [x] TypeCheckingTests::RawAndMultilineTextLiteralsTypeCheckAsTextLiterals
- [x] TypeCheckingTests::ConstGlobalAggregateProjectionsCanBindToFrozenParameters
- [x] TypeCheckingTests::ExplicitLiteralTextConversionsTypeCheck
- [x] TypeCheckingTests::CompileTimeTextConcatenationTypeChecksAsTextLiteral
- [x] TypeCheckingTests::CompileTimeInterpolatedTextTypeChecksAsTextLiteral
- [x] TypeCheckingTests::FixedCapacityRuntimeInterpolatedTextTypeChecksWithKnownFormatter
- [x] TypeCheckingTests::FixedCapacityTextConcatenationTypeChecksForAsciiAndUnicodeBuffers
- [x] TypeCheckingTests::EmptyTextSlicesTypeCheckAsSameTextKind
- [x] TypeCheckingTests::FixedArrayLengthsAcceptConstantArithmeticExpressions
- [x] TypeCheckingTests::FixedArrayInitializersCanOmitTrailingElements
- [x] TypeCheckingTests::ScalarizableNamedAggregatesAreOrderedComparable
- [x] TypeCheckingTests::ScalarizableEnumsAreOrderedComparable
- [x] TypeCheckingTests::ExplicitNonAsciiLiteralToAsciiConversionTypeChecks
- [x] TypeCheckingTests::FrozenReachableViewsTypeCheckAsReadonlyAliases
- [x] TypeCheckingTests::ConstParameterProvenanceTypeChecksAsReadonlyAliases
- [x] TypeCheckingTests::ConstParametersCanBeForwardedToConstParameters
- [x] TypeCheckingTests::ConstRawPointerProvenanceFlowsThroughImmutableLocal
- [x] TypeCheckingTests::GenericConstRawPointerCallsInferFromConstProvenanceView
- [x] TypeCheckingTests::ConstRawSliceProvenanceFlowsThroughImmutableLocal
- [x] TypeCheckingTests::AggregateSwitchPatternsTypeCheckOnScalarFields
- [x] TypeCheckingTests::AggregatePropertySwitchPatternsTypeCheckOnNamedFields
- [x] TypeCheckingTests::ListSwitchPatternsTypeCheckOnFixedArrays
- [x] TypeCheckingTests::ListSwitchPatternsTypeCheckOnSlices
- [x] TypeCheckingTests::NamedAggregateWholeValueSwitchCapturesTypeCheck
- [x] TypeCheckingTests::NestedAggregateWholeValueSwitchCapturesTypeCheck
- [x] TypeCheckingTests::EnumWholeValueSwitchCapturesTypeCheck
- [x] TypeCheckingTests::GuardedSwitchLabelsDoNotContributeToReachabilityCoverage
- [x] TypeCheckingTests::NestedAggregateSwitchPatternsTypeCheckOnScalarLeaves
- [x] TypeCheckingTests::EnumSwitchPatternsTypeCheckOnCasePayloadCaptures
- [x] TypeCheckingTests::GenericEnumInstantiationTypeChecks
- [x] TypeCheckingTests::GenericRecordInstantiationTypeChecks
- [x] TypeCheckingTests::GenericRecordPrimaryConstructorInstantiationTypeChecks
- [x] TypeCheckingTests::GenericTypeUsesRecordConcreteInstantiationTriggers
- [x] TypeCheckingTests::NestedGenericTypesInsideContainersMonomorphizeAndRecordTriggers
- [x] TypeCheckingTests::GenericFunctionBodiesCanUseTheirTypeParametersInLocalTypes
- [x] TypeCheckingTests::GenericMethodBodiesCanUseTheirTypeParametersInLocalTypes
- [x] TypeCheckingTests::GenericFunctionCallsRecordConcreteInstantiationTriggers
- [x] TypeCheckingTests::GenericMethodCallsRecordConcreteInstantiationTriggers
- [x] TypeCheckingTests::GenericMethodsOnGenericTypesRecordConcreteInstantiationTriggers
- [x] TypeCheckingTests::GenericNestedMemberOutCallsInferStorageType
- [x] TypeCheckingTests::RepeatedGenericFunctionCallsReuseOneCachedInstantiationTrigger
- [x] TypeCheckingTests::RepeatedGenericTypeUsesReuseOneCachedInstantiationTrigger
- [x] TypeCheckingTests::ConcreteOverloadsBeatMatchingGenericInstantiationTriggers
- [x] TypeCheckingTests::TypeAliasesResolveToTheirUnderlyingTypes
- [x] TypeCheckingTests::GenericTypeAliasesSubstituteIntoTheirUnderlyingTypes
- [x] TypeCheckingTests::GenericTypeAliasesSubstituteIntoClosureSignatures
- [x] TypeCheckingTests::GenericEnumAliasesCanQualifyVariants
- [x] TypeCheckingTests::GenericEnumVariantFieldTypeIsSubstituted
- [x] TypeCheckingTests::TopLevelOverloadGroupsRegisterDistinctFunctionsAndResolveCalls
- [x] TypeCheckingTests::NumericOverloadResolutionPrefersExactAndNarrowSafeMatches
- [x] TypeCheckingTests::MethodOverloadGroupsRegisterDistinctFunctionsAndResolveCalls
- [x] TypeCheckingTests::TargetTypedObjectCreationResolvesFromDestinationType
- [x] TypeCheckingTests::TargetTypedObjectCreationResolvesAllocatorTakingConstructorOverload
- [x] TypeCheckingTests::DynamicStorageCreationCapacityAndInitIndexTypeCheck
- [x] TypeCheckingTests::DynamicStorageTryReserveReturnsBool
- [x] TypeCheckingTests::DynamicStorageTryReserveCapacityReturnsBool
- [x] TypeCheckingTests::DynamicStorageMoveLastReturnsElementType
- [x] TypeCheckingTests::DynamicStorageMoveAtReturnsElementType
- [x] TypeCheckingTests::DynamicStorageSpareRangeCanBindInitSliceView
- [x] TypeCheckingTests::TextLiteralsHaveConstProvenanceForConstParameters

### TypeTypingExpressionFamilyTests  (6/6)
- [x] TypeTypingExpressionFamilyTests::AssignmentExpressionsTypeCheck
- [x] TypeTypingExpressionFamilyTests::UnaryAndExponentExpressionsTypeCheck
- [x] TypeTypingExpressionFamilyTests::MultiplicativeAdditiveAndShiftExpressionsTypeCheck
- [x] TypeTypingExpressionFamilyTests::BitwiseComparisonLogicalAndConditionalExpressionsTypeCheck
- [x] TypeTypingExpressionFamilyTests::ComparisonChainsTypeCheck
- [x] TypeTypingExpressionFamilyTests::PostfixCallsIndexesMembersAndObjectCreationTypeCheck

## Ownership & borrow validation  (243/243)

### BorrowLivenessValidationTests  (5/5)
- [x] BorrowLivenessValidationTests::MoveConflictIsReportedWhenBorrowRemainsLive
- [x] BorrowLivenessValidationTests::MoveAfterLastUseIsAllowed
- [x] BorrowLivenessValidationTests::OverwriteConflictIsReportedWhenBorrowRemainsLive
- [x] BorrowLivenessValidationTests::BranchLastUseAllowsMoveAtMerge
- [x] BorrowLivenessValidationTests::BranchLiveBorrowBlocksMoveAtMerge

### CompilerPipelineInstantiationOwnershipTests  (3/3)
- [x] CompilerPipelineInstantiationOwnershipTests::RootGenericInstantiationsStayOwnedByTheRootModule
- [x] CompilerPipelineInstantiationOwnershipTests::SourceBackedImportedGenericInstantiationsStayOwnedByTheDefiningModule
- [x] CompilerPipelineInstantiationOwnershipTests::ManifestBackedGenericInstantiationsFallBackToTheRootConsumerModule

### ConstructorFieldReadRegressionTests  (7/7)
- [x] ConstructorFieldReadRegressionTests::ReadingDynamicFieldBeforeAssignmentIsRejected
- [x] ConstructorFieldReadRegressionTests::IndexingIntoUnassignedDynamicFieldIsRejected
- [x] ConstructorFieldReadRegressionTests::ReadingAnotherUnassignedOwningFieldIsRejected
- [x] ConstructorFieldReadRegressionTests::ReadingDynamicFieldAfterAssignmentIsAllowed
- [x] ConstructorFieldReadRegressionTests::WritingFixedArrayElementsIsNotAFieldRead
- [x] ConstructorFieldReadRegressionTests::ReadingScalarFieldBeforeAssignmentIsAllowed
- [x] ConstructorFieldReadRegressionTests::FieldAssignedOnAnyEarlierPathIsNotFlagged

### CopyableDoctrineTests  (6/6)
- [x] CopyableDoctrineTests::TagEnumAndStructOfTagEnumCopyOutOfPlacesAndKeepTheSourceUsable
- [x] CopyableDoctrineTests::EnumsWithOwningPayloadsStayMoveOnlyAtIndexedPlaces
- [x] CopyableDoctrineTests::ComptimeEvaluationCopiesCopyableValuesConsistently
- [x] CopyableDoctrineTests::StructsWithDestructorsStayMoveOnly
- [x] CopyableDoctrineTests::FixedArrayOfCopyableElementsIsCopyable
- [x] CopyableDoctrineTests::SliceAndFunctionPointerFieldsAreCopyable

### DiagnosticRegressionTests  (101/101)
- [x] DiagnosticRegressionTests::StaticOwnedValuesCannotBeMovedOut
- [x] DiagnosticRegressionTests::ConstGlobalsExplainWhyReachableStateIsNotFrozen
- [x] DiagnosticRegressionTests::ConstGlobalsExplainWhenInitializersCannotLowerAsStaticData
- [x] DiagnosticRegressionTests::RuntimeDisjointConditionsCompileWithoutLoweringDiagnostic
- [x] DiagnosticRegressionTests::RuntimeDisjointScalarOperandsFailBeforeMirLowering
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectObviousOverlappingArguments
- [x] DiagnosticRegressionTests::DefaultNonOverlapParametersRejectObviousOverlappingArguments
- [x] DiagnosticRegressionTests::OverlapContractAllowsIntentionalOverlappingArguments
- [x] DiagnosticRegressionTests::SameContractRejectsDistinctRegions
- [x] DiagnosticRegressionTests::SameContractAcceptsSameRegion
- [x] DiagnosticRegressionTests::DefaultNonOverlapRejectsImmutableRawPointerLocalAliases
- [x] DiagnosticRegressionTests::DefaultNonOverlapRejectsMutableRawPointerAssignmentAliases
- [x] DiagnosticRegressionTests::DefaultNonOverlapRejectsMutableRawPointerInitializerAliases
- [x] DiagnosticRegressionTests::DefaultNonOverlapRejectsIndirectFunctionPointerRawPointerAliases
- [x] DiagnosticRegressionTests::FunctionPointerWhereDisjointIsRejectedAsRedundant
- [x] DiagnosticRegressionTests::FunctionPointerMemoryContractsRejectNonMemoryBackedOperands
- [x] DiagnosticRegressionTests::FunctionPointerMemoryContractsRejectUnknownSyntheticOperands
- [x] DiagnosticRegressionTests::DefaultNonOverlapAcceptsMutableRawPointerInitializersFromDistinctRoots
- [x] DiagnosticRegressionTests::SameContractAcceptsMutableRawPointerInitializerAlias
- [x] DiagnosticRegressionTests::DefaultNonOverlapRejectsSimpleRawPointerCastAliases
- [x] DiagnosticRegressionTests::DefaultNonOverlapRejectsIntegerLaunderedRawPointerAliasesWithoutProof
- [x] DiagnosticRegressionTests::DefaultNonOverlapInvalidatesMutablePointerAliasAfterBranchAssignment
- [x] DiagnosticRegressionTests::BranchLocalPointerAliasInvalidationDoesNotPolluteSiblingBranch
- [x] DiagnosticRegressionTests::DisjointParameterPrefixesRejectNonMemoryBackedTypes
- [x] DiagnosticRegressionTests::WholeParameterDisjointPrefixIsRejectedWithFixItDiagnostic
- [x] DiagnosticRegressionTests::WholeParameterWhereDisjointIsRejectedWithFixItDiagnostic
- [x] DiagnosticRegressionTests::FfiWholeParameterDisjointStillOptsIntoExternalNoAliasContract
- [x] DiagnosticRegressionTests::WhereDisjointContractsRejectNonMemoryBackedParameters
- [x] DiagnosticRegressionTests::OverlapContractsRejectRepeatedOperands
- [x] DiagnosticRegressionTests::SameContractsRejectUnknownOperands
- [x] DiagnosticRegressionTests::OverlapContractsRejectNonMemoryBackedOperands
- [x] DiagnosticRegressionTests::UnsafeFunctionDisjointParameterCallsForwardCallerDefaultParameterFacts
- [x] DiagnosticRegressionTests::UnsafeFunctionDisjointParameterCallsRejectHiddenRootsWithoutAssumption
- [x] DiagnosticRegressionTests::UnsafeAssumeDisjointAllowsProgrammerProvenRawPointerArguments
- [x] DiagnosticRegressionTests::BareAssumeDisjointIsAllowedInsideUnsafeFunction
- [x] DiagnosticRegressionTests::BareAssumeDisjointRequiresUnsafeContext
- [x] DiagnosticRegressionTests::UnsafeBlockAloneDoesNotSatisfyUnknownDisjointCallContract
- [x] DiagnosticRegressionTests::UnsafeAssumeDisjointRejectsHiddenRoots
- [x] DiagnosticRegressionTests::UnsafeAssumeDisjointRejectsObviousSameRoot
- [x] DiagnosticRegressionTests::DisjointParameterFactsAllowForwardingToDisjointCallee
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowDistinctMutableBorrowParameters
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowDistinctOutParameterRoots
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectRepeatedOutParameterRoots
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowImmutableSliceViewsFromDistinctLocalArrays
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectSliceViewAndBackingArrayOverlap
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowNonOverlappingTextSliceRanges
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowNonOverlappingDynamicTextSliceRanges
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectOverlappingTextSliceRanges
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectOverlappingDynamicTextSliceRanges
- [x] DiagnosticRegressionTests::DisjointParameterCallsAcceptDefaultNonOverlapReadonlyBorrowParameters
- [x] DiagnosticRegressionTests::DisjointMethodCallsValidateReceiverContracts
- [x] DiagnosticRegressionTests::DisjointMethodCallsAcceptDefaultNonOverlapReceiverContracts
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowDistinctAddressedLocalStorage
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectAncestorProjectionArguments
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowDistinctProjectionArguments
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectUnprovenIndexedArguments
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowDistinctConstantIndexedArguments
- [x] DiagnosticRegressionTests::DisjointParameterCallsAllowNonOverlappingIndexRangeArguments
- [x] DiagnosticRegressionTests::DisjointParameterCallsRejectOverlappingIndexRangeArguments
- [x] DiagnosticRegressionTests::RuntimeDisjointTrueBranchSatisfiesDisjointCallContract
- [x] DiagnosticRegressionTests::RuntimeDisjointTrueBranchUsesTextSliceLocalBackingRoots
- [x] DiagnosticRegressionTests::RuntimeDisjointTrueBranchUsesRawSliceLocalBackingRoots
- [x] DiagnosticRegressionTests::RuntimeDisjointTrueBranchCoversDescendantRegions
- [x] DiagnosticRegressionTests::RuntimeDisjointFalseBranchDoesNotSatisfyDisjointCallContract
- [x] DiagnosticRegressionTests::IndependentScalarOnlyLoopContractsCompile
- [x] DiagnosticRegressionTests::IndependentSliceLoopsCompileWithDisjointParameterFacts
- [x] DiagnosticRegressionTests::IndependentSliceLoopsCompileWithConditionalMemoryBodies
- [x] DiagnosticRegressionTests::IndependentSliceLoopsCompileWithMemberProjectedMemoryAccesses
- [x] DiagnosticRegressionTests::IndependentBoundedRawPointerLoopsCompileWithRegionContracts
- [x] DiagnosticRegressionTests::OverlapCapableSubregionDisjointCallsAllowSameRootWhenRangesAreProvenDisjoint
- [x] DiagnosticRegressionTests::OverlapCapableSubregionDisjointCallsRejectSameRootWhenRangesOverlap
- [x] DiagnosticRegressionTests::IndependentBoundedRawPointerLoopsCompileWithRuntimeRegionFacts
- [x] DiagnosticRegressionTests::IndependentBoundedRawPointerLoopsRejectUnprovenInductionBounds
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsRejectNullWhenCountIsPositive
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsAllowNullWhenCountIsZero
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsAllowFixedArrayElementWhenStorageCoversCount
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsAllowForwardedBoundedPointerWhenCountMatches
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsAllowImmutablePointerLocalWithProvenance
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsAllowForwardedSubregionsWhenStorageCoversCount
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsAllowSliceDerivedRawRegionsWhenStorageCoversCount
- [x] DiagnosticRegressionTests::BoundedRawPointerCallsRejectFixedArrayElementWhenStorageIsTooShort
- [x] DiagnosticRegressionTests::RawSliceConstructionInsideUnsafeFunctionCompiles
- [x] DiagnosticRegressionTests::UnsafeRawSliceConstructionCompiles
- [x] DiagnosticRegressionTests::BoundedRawPointerParametersRejectPossiblyNegativeCounts
- [x] DiagnosticRegressionTests::RuntimeRawPointerRegionChecksRejectPossiblyNegativeBounds
- [x] DiagnosticRegressionTests::RawSliceConstructionRejectsPossiblyNegativeCounts
- [x] DiagnosticRegressionTests::RawSliceConstructionRejectsNullWhenCountIsPositive
- [x] DiagnosticRegressionTests::RawSliceConstructionAllowsNullWhenCountIsZero
- [x] DiagnosticRegressionTests::RawSliceConstructionRejectsHiddenPointerRoots
- [x] DiagnosticRegressionTests::IndependentSliceLoopsAllowScalarLawCallsAfterValidatedMemoryReads
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectUnprovenWrittenReadRoots
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectNonInductionIndexes
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectAssignmentsToInductionVariable
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectCallsWithUnprovenMemoryEffects
- [x] DiagnosticRegressionTests::IndependentLoopContractsRejectMemoryBackedLocalDeclarations
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectMemoryBackedLocalDeclarations
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectNestedLoops
- [x] DiagnosticRegressionTests::IndependentSliceLoopsRejectEarlyExits
- [x] DiagnosticRegressionTests::IndependentLoopContractsRejectMemoryTouchingBodies
- [x] DiagnosticRegressionTests::BorrowedGenericParameterLiteralArgumentHintsByValueCopyableFix
- [x] DiagnosticRegressionTests::BorrowedConcreteParameterLiteralArgumentDoesNotHintGenericFix

### EqualityGuardFlowFactTests  (5/5)
- [x] EqualityGuardFlowFactTests::InequalityEarlyReturnGuardProvesConstantIndexOnSurvivingPath
- [x] EqualityGuardFlowFactTests::EqualityTrueBranchProvesConstantIndices
- [x] EqualityGuardFlowFactTests::NonEmptyInequalityProvesIndexZero
- [x] EqualityGuardFlowFactTests::EqualityGuardDoesNotProveOutOfBoundsIndex
- [x] EqualityGuardFlowFactTests::EqualityFactDiesOnMutatingUse

### FunctionSemanticsTests  (9/9)
- [x] FunctionSemanticsTests::SemanticValidationSummariesCaptureParameterGuaranteesAndReturnCaptures
- [x] FunctionSemanticsTests::SemanticValidationSummariesDeriveConcreteLayoutFactsForPaddedAggregates
- [x] FunctionSemanticsTests::DefaultNonOverlapParameterContractsFlowIntoSemanticNoAliasFacts
- [x] FunctionSemanticsTests::DefaultNonOverlapAndOverlapContractsFlowIntoSemanticNoAliasFacts
- [x] FunctionSemanticsTests::ConstParameterQualifierFlowsIntoSemanticReadonlyFacts
- [x] FunctionSemanticsTests::TransitiveCallEffectsFlowIntoSemanticSummaries
- [x] FunctionSemanticsTests::SemanticValidationSummariesDistinguishArgumentAndOtherMemory
- [x] FunctionSemanticsTests::SystemMemoryAllocatorDeclarationSummariesIncludeAllocatorState
- [x] FunctionSemanticsTests::DynamicStorageOperationsOnBorrowedParametersWriteArgumentMemory

### InitializedReadFlowFactTests  (5/5)
- [x] InitializedReadFlowFactTests::GuardThenReadProvesDirectAndProjectedReadsInFreeFunctions
- [x] InitializedReadFlowFactTests::UnguardedFieldProjectionsThroughDynamicSlotsAreNowChecked
- [x] InitializedReadFlowFactTests::WritingToTheIndexInvalidatesTheFact
- [x] InitializedReadFlowFactTests::NonStrictComparisonsAndNonTerminatingGuardsProveNothing
- [x] InitializedReadFlowFactTests::MutBorrowingTheStorageOwnerInvalidatesTheFact

### OwnershipBranchJoinRegressionTests  (7/7)
- [x] OwnershipBranchJoinRegressionTests::EarlyReturnMoveInThenBranchDoesNotPoisonTheFallThroughJoin
- [x] OwnershipBranchJoinRegressionTests::EarlyReturnMoveThenFieldReassignmentAndAggregateReturnValidates
- [x] OwnershipBranchJoinRegressionTests::SwitchArmEarlyReturnMoveDoesNotPoisonTheFallThroughJoin
- [x] OwnershipBranchJoinRegressionTests::ConditionalMoveWithoutReturnStillErrorsOnLaterUse
- [x] OwnershipBranchJoinRegressionTests::MoveBeforeBreakStaysVisibleAfterTheLoop
- [x] OwnershipBranchJoinRegressionTests::ElseBranchMoveSurvivesWhenThenBranchReturns
- [x] OwnershipBranchJoinRegressionTests::SwitchFallThroughArmMoveStillErrors

### OwnershipFieldReadAfterMoveRegressionTests  (6/6)
- [x] OwnershipFieldReadAfterMoveRegressionTests::DynamicLengthReadOnDefinitelyMovedRootErrors
- [x] OwnershipFieldReadAfterMoveRegressionTests::DynamicCapacityReadOnDefinitelyMovedRootErrors
- [x] OwnershipFieldReadAfterMoveRegressionTests::DynamicLengthReadOnMaybeMovedRootErrors
- [x] OwnershipFieldReadAfterMoveRegressionTests::DynamicMemberCallOnMovedReceiverErrors
- [x] OwnershipFieldReadAfterMoveRegressionTests::ScalarFieldReadOnMovedRootErrors
- [x] OwnershipFieldReadAfterMoveRegressionTests::ConstructorReadsAssignedDynamicFieldHeaderLegally

### OwnershipRoadmapRegressionTests  (48/48)
- [x] OwnershipRoadmapRegressionTests::MovedOwnedLocalCanBeReinitializedBeforeLaterRead
- [x] OwnershipRoadmapRegressionTests::OwnershipSummaryExposesTypedRootEventsForOptimization
- [x] OwnershipRoadmapRegressionTests::OwnershipSummaryExposesPartialFieldMovesForOptimization
- [x] OwnershipRoadmapRegressionTests::BranchReinitializationKeepsOwnedLocalAvailable
- [x] OwnershipRoadmapRegressionTests::LoopReinitializationKeepsOwnedLocalAvailable
- [x] OwnershipRoadmapRegressionTests::BranchMergeRequiresReinitializationOnEveryPath
- [x] OwnershipRoadmapRegressionTests::EnumWithOnlyCopyPayloadsDoesNotRecordImplicitDropAtScopeExit
- [x] OwnershipRoadmapRegressionTests::EnumWithOnlyCopyPayloadsMayRemainUninitializedAtScopeExit
- [x] OwnershipRoadmapRegressionTests::TupleEnumConstructorCallsRemainOwnedAcrossScopeExit
- [x] OwnershipRoadmapRegressionTests::ConditionalEnumConstructorsOnlyDropOwnedCases
- [x] OwnershipRoadmapRegressionTests::EnumInitializedOnOnlyOnePathWithOwnedPayloadIsRejectedAtScopeExit
- [x] OwnershipRoadmapRegressionTests::OwnedEnumPayloadCaptureLeavesNoDropWhenOnlyUnitCaseRemains
- [x] OwnershipRoadmapRegressionTests::OwnedEnumPayloadCaptureCanBeReinitializedBeforeScopeExit
- [x] OwnershipRoadmapRegressionTests::OwnedEnumPayloadCaptureCannotMoveOutOfFieldPlace
- [x] OwnershipRoadmapRegressionTests::ReassigningOwnedEnumDropsOnlyThePreviousOwnedCase
- [x] OwnershipRoadmapRegressionTests::SwitchingOnEnumParameterNarrowsActiveCaseForDropAnalysis
- [x] OwnershipRoadmapRegressionTests::UninitializedOwnedLocalIsNotDroppedAtScopeExit
- [x] OwnershipRoadmapRegressionTests::ReadingUninitializedOwnedLocalProducesInitializationDiagnostic
- [x] OwnershipRoadmapRegressionTests::FieldAssignmentsCanFullyInitializeAnAggregateLocal
- [x] OwnershipRoadmapRegressionTests::PartiallyInitializedAggregateCannotBeConsumedAsAWholeValue
- [x] OwnershipRoadmapRegressionTests::PartiallyInitializedAggregateIsRejectedAtScopeExit
- [x] OwnershipRoadmapRegressionTests::WholeAggregateUseAfterFieldMoveReportsPartialMove
- [x] OwnershipRoadmapRegressionTests::ReinitializingMovedFieldRestoresWholeAggregateAvailability
- [x] OwnershipRoadmapRegressionTests::BranchMergesPartialFieldMoveStateAcrossPaths
- [x] OwnershipRoadmapRegressionTests::BranchReinitializationAfterFieldMoveKeepsFieldAvailable
- [x] OwnershipRoadmapRegressionTests::BranchesMergeDefiniteAggregateFieldInitialization
- [x] OwnershipRoadmapRegressionTests::BranchesReportFieldAvailabilityWhenOnlySomePathsInitializeIt
- [x] OwnershipRoadmapRegressionTests::ReturningBorrowFromUnknownSourceReportsLifetimeDiagnostic
- [x] OwnershipRoadmapRegressionTests::StoredBorrowClosureFieldsRejectTemporaryEnvironments
- [x] OwnershipRoadmapRegressionTests::MoveCapturesConsumeSourceBindingAtClosureCreation
- [x] OwnershipRoadmapRegressionTests::OutAndInitClosureCapturesMustBeAssignedOnEveryReturnPath
- [x] OwnershipRoadmapRegressionTests::OnceClosureCallsConsumeTheClosureValue
- [x] OwnershipRoadmapRegressionTests::ReturningBorrowFromBranchSpecificCallsStillReportsLifetimeDiagnostic
- [x] OwnershipRoadmapRegressionTests::AssigningBorrowFromUnknownSourceReportsDestinationLifetime
- [x] OwnershipRoadmapRegressionTests::AssigningBorrowFromInnerScopeToOuterScopeReportsEscapeDiagnostic
- [x] OwnershipRoadmapRegressionTests::ReturningBorrowFromInnerScopeReportsEscapeDiagnostic
- [x] OwnershipRoadmapRegressionTests::DynamicInitAssignmentsTrackDensePrefix
- [x] OwnershipRoadmapRegressionTests::DynamicInitAssignmentRejectsDensePrefixHole
- [x] OwnershipRoadmapRegressionTests::DynamicAppendByLengthIsAcceptedForUnknownPrefix
- [x] OwnershipRoadmapRegressionTests::DynamicInitSliceAssignmentsTrackSequentialSlots
- [x] OwnershipRoadmapRegressionTests::DynamicInitSliceIndependentInductionLoopTracksRuntimeSlots
- [x] OwnershipRoadmapRegressionTests::DynamicInitSliceIndependentInductionLoopRejectsRepeatedSlotProof
- [x] OwnershipRoadmapRegressionTests::DynamicInitSliceRejectsOutOfOrderSlotInitialization
- [x] OwnershipRoadmapRegressionTests::DynamicNonTailMoveRequiresSparseSlotProof
- [x] OwnershipRoadmapRegressionTests::UnsafeDynamicSparseSlotProofAllowsReadInsideProofBoundary
- [x] OwnershipRoadmapRegressionTests::UnsafeDynamicSparseInitProofAllowsUseInsideProofBoundaryOnly
- [x] OwnershipRoadmapRegressionTests::UnsafeDynamicSparseInitProofDoesNotLeakIntoSafeCode
- [x] OwnershipRoadmapRegressionTests::UnsafeDynamicSparseProofAllowsNonTailMoveInsideProofBoundary

### OwnershipValidationTests  (15/15)
- [x] OwnershipValidationTests::HeapOwnedValuesAreDroppedAtScopeExit
- [x] OwnershipValidationTests::MovingOwnedLocalMakesLaterUseInvalid
- [x] OwnershipValidationTests::MoveDiagnosticsAreNotDuplicatedForTheSameUse
- [x] OwnershipValidationTests::ValueReceiverMethodCallsMoveTheReceiver
- [x] OwnershipValidationTests::CopyValuesRemainUsableAfterAssignment
- [x] OwnershipValidationTests::ImmutableTextViewsRemainUsableAfterByValueCalls
- [x] OwnershipValidationTests::ReassigningOwnedLocalDropsPreviousValue
- [x] OwnershipValidationTests::ConditionalMoveMakesLaterUseInvalid
- [x] OwnershipValidationTests::ReturningOwnedLocalMovesItOutInsteadOfDroppingIt
- [x] OwnershipValidationTests::MovingOutOfATopLevelFieldIsAllowed
- [x] OwnershipValidationTests::MovingOutOfANestedFieldIsRejected
- [x] OwnershipValidationTests::DoctrineCallsParticipateInOwnershipFlow
- [x] OwnershipValidationTests::ExplicitGenericDoctrineCallsParticipateInOwnershipFlow
- [x] OwnershipValidationTests::RuntimeTextConcatenationConsumesOwnedTextResult
- [x] OwnershipValidationTests::DisjointMutableBorrowCallsParticipateInOwnershipFlow

### SemanticValidationTests  (10/10)
- [x] SemanticValidationTests::BorrowReturnTypesAreRejected
- [x] SemanticValidationTests::GlobalNonEscapingBorrowsAreRejected
- [x] SemanticValidationTests::AggregateFieldsRejectNonEscapingBorrowClasses
- [x] SemanticValidationTests::AggregateFieldsAllowExplicitStoredClosureBorrows
- [x] SemanticValidationTests::RegisterLocalsCannotBePassedToBorrowParameters
- [x] SemanticValidationTests::RegisterFixedArraysCannotFormSliceViews
- [x] SemanticValidationTests::LawsCanForwardRetborrowThroughLawWrappers
- [x] SemanticValidationTests::RetborrowsCannotBeForwardedToRetborrowParameters
- [x] SemanticValidationTests::SafeBorrowsCannotCrossFfiBoundaries
- [x] SemanticValidationTests::RetborrowsCanBeUsedLocallyAndReturned

### TraversalLoopFeatureTests  (2/2)
- [x] TraversalLoopFeatureTests::ForInRejectsNonBorrowElementBindings
- [x] TraversalLoopFeatureTests::ForInRejectsMutableBorrowFromImmutableStorage

### TypeTypingDiagnosticsTests  (14/14)
- [x] TypeTypingDiagnosticsTests::ImmutableLocalCannotBeReassigned
- [x] TypeTypingDiagnosticsTests::ImmutableLocalFieldsCannotBeMutated
- [x] TypeTypingDiagnosticsTests::FrozenMemberProjectionsCannotBeMutated
- [x] TypeTypingDiagnosticsTests::FrozenSliceProjectionsRemainReadonly
- [x] TypeTypingDiagnosticsTests::FrozenDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers
- [x] TypeTypingDiagnosticsTests::FrozenDerivedReadonlyPointersCannotBeLaunderedThroughIntegers
- [x] TypeTypingDiagnosticsTests::FrozenReachableRawPointerFieldsCannotLeakMutableAliases
- [x] TypeTypingDiagnosticsTests::ConstRawPointerParametersCannotMutatePointees
- [x] TypeTypingDiagnosticsTests::ConstParameterReachableRawPointerFieldsCannotLeakMutableAliases
- [x] TypeTypingDiagnosticsTests::ConstParameterCallsRequireConstProvenance
- [x] TypeTypingDiagnosticsTests::FrozenParameterCallsDoNotSatisfyConstProvenance
- [x] TypeTypingDiagnosticsTests::FrozenParameterProjectionsDoNotSatisfyConstProvenance
- [x] TypeTypingDiagnosticsTests::FrozenRawPointerFieldsDoNotSatisfyConstProvenance
- [x] TypeTypingDiagnosticsTests::RawSlicesFromFrozenPointersDoNotSatisfyConstProvenance

## MIR (mid-level IR) lowering  (126/126)

### CompilerCliTests  (2/2)
- [x] CompilerCliTests::EmitMirModePrintsMirModule
- [x] CompilerCliTests::EmitMirModeSupportsOutputPath

### CompilerPipelineEnumLayoutTests  (2/2)
- [x] CompilerPipelineEnumLayoutTests::EnumLayoutsUseSmallestSoundTagWidths
- [x] CompilerPipelineEnumLayoutTests::EnumPayloadLayoutsUseTargetSoundAlignmentForNonStandardAndWideIntegers

### CompilerPipelineFullIntegrationTests  (7/7)
- [x] CompilerPipelineFullIntegrationTests::PipelinePreservesMirSwitchBreakShapeAndNormalizesOptimizedSsaControlFlow
- [x] CompilerPipelineFullIntegrationTests::PipelineCarriesSourceLocationsThroughMirAndSsaArtifacts
- [x] CompilerPipelineFullIntegrationTests::ImportedAsmDeclarationsFlowThroughHirMirAndSsaAsExplicitBypassFunctions
- [x] CompilerPipelineFullIntegrationTests::ImportedModulePrivateLawHelpersFlowIntoHirMirAndSsa
- [x] CompilerPipelineFullIntegrationTests::ImportedSourceModulesFlowIntoHirMirAndSsaArtifacts
- [x] CompilerPipelineFullIntegrationTests::ImportedSourceModulesWithPrivateHelpersAndStringLiteralsLowerIntoMirAndSsa
- [x] CompilerPipelineFullIntegrationTests::OverloadedMethodsResolveThroughSemanticValidationAndMirLowering

### CompilerPipelineLowerAbiTests  (1/1)
- [x] CompilerPipelineLowerAbiTests::MonomorphizedGenericFunctionsReceiveExplicitAbiLowering

### CompilerPipelineLowerHirTests  (2/2)
- [x] CompilerPipelineLowerHirTests::NestedGenericCallsMaterializeTransitiveSpecializationsIntoHighLevelIr
- [x] CompilerPipelineLowerHirTests::LowerHirMaterializesSourceBackedMonomorphizedGenericFunctions

### CompilerPipelineLowerMirTests  (3/3)
- [x] CompilerPipelineLowerMirTests::ExplicitConstructorBodiesLowerIntoObjectCreation
- [x] CompilerPipelineLowerMirTests::LowerMirSubstitutesConcreteTypesInsideMaterializedGenericBodies
- [x] CompilerPipelineLowerMirTests::LowerMirRewritesGenericCallsToMaterializedSpecializationSymbols

### GenericUseSiteInstantiationRegressionTests  (1/1)
- [x] GenericUseSiteInstantiationRegressionTests::NestedGenericTypeLayoutsAreDiscoveredFromSourceUseSites

### LoweringContractFactKeyRegressionTests  (2/2)
- [x] LoweringContractFactKeyRegressionTests::MemberCallsDoNotCollideWithImportedConstructorCallsAtTheSameCoordinates
- [x] LoweringContractFactKeyRegressionTests::ImportedGenericMemberCallLowersAcrossModulesWithoutInvariantViolation

### MidLevelIrDynamicFixedArrayIndexingTests  (1/1)
- [x] MidLevelIrDynamicFixedArrayIndexingTests::DynamicIndexingOnFixedArrayTemporaryLowersWithoutUnsupportedFallback

### MidLevelIrLoweringTests.CompileTimeEvaluator  (5/5)
- [x] MidLevelIrLoweringTests.CompileTimeEvaluator::PureConstantArithmeticReturnsFoldedMirConstant
- [x] MidLevelIrLoweringTests.CompileTimeEvaluator::ConstantIntegerExponentComparisonOperandFoldsBeforeMirWidthSelection
- [x] MidLevelIrLoweringTests.CompileTimeEvaluator::ConstantLawCallsFoldToMirConstants
- [x] MidLevelIrLoweringTests.CompileTimeEvaluator::BackendOpaqueLawCallsDoNotFoldToMirConstants
- [x] MidLevelIrLoweringTests.CompileTimeEvaluator::FixedArrayArithmeticIndexStillLowersToAggregateIndexOperations

### MidLevelIrLoweringTests.Core  (32/32)
- [x] MidLevelIrLoweringTests.Core::PayloadEnumConstructorCallsDoNotProduceIndirectCallProbeDiagnostics
- [x] MidLevelIrLoweringTests.Core::IfStatementsLowerToBranchingBlocks
- [x] MidLevelIrLoweringTests.Core::WhileLoopsLowerToBackedgeControlFlow
- [x] MidLevelIrLoweringTests.Core::ForLoopsProduceConditionIteratorAndExitBlocks
- [x] MidLevelIrLoweringTests.Core::LabeledBreakAndContinueLowerThroughNestedLoops
- [x] MidLevelIrLoweringTests.Core::EnumConstructorsLowerToDirectTagFieldInserts
- [x] MidLevelIrLoweringTests.Core::ComparisonChainsLowerToShortCircuitBlocksAndReuseSharedOperands
- [x] MidLevelIrLoweringTests.Core::ShortCircuitOrLowersToMultipleBlocksAndDirectCodegen
- [x] MidLevelIrLoweringTests.Core::ConditionalExpressionLowersToJoinableBlocks
- [x] MidLevelIrLoweringTests.Core::VoidConditionalCallStatementsLowerToJoinableBlocks
- [x] MidLevelIrLoweringTests.Core::VoidFunctionPointerCallsLowerAsStatementOnlyOperations
- [x] MidLevelIrLoweringTests.Core::LocalDeclarationsAndAssignmentsLowerToMirStatements
- [x] MidLevelIrLoweringTests.Core::SizeofAndAlignofLowerToConcreteIntegerConstants
- [x] MidLevelIrLoweringTests.Core::HugeCompileTimeIntegerConversionMaterializesConcreteMirConstant
- [x] MidLevelIrLoweringTests.Core::ConstantNarrowingConversionMaterializesWrappedMirConstant
- [x] MidLevelIrLoweringTests.Core::BitwiseXorExpressionLowersToMirBinaryOperation
- [x] MidLevelIrLoweringTests.Core::BitwiseXorAssignmentLowersToMirBinaryOperation
- [x] MidLevelIrLoweringTests.Core::BitwiseAndShiftChainsRespectPrecedenceAndAssociativity
- [x] MidLevelIrLoweringTests.Core::WrappingAndSaturatingArithmeticLowerToDistinctMirOperators
- [x] MidLevelIrLoweringTests.Core::ExponentExpressionLowersToMirBinaryOperation
- [x] MidLevelIrLoweringTests.Core::IntegerExponentExpressionLowersToMirBinaryOperation
- [x] MidLevelIrLoweringTests.Core::FloatingPointArithmeticChainsLowerToFloatMirBinaryOperations
- [x] MidLevelIrLoweringTests.Core::CharacterLiteralsLowerToMirStringConstants
- [x] MidLevelIrLoweringTests.Core::ExplicitAsciiLiteralToUnicodeConversionLowersToUnicodeStringConstant
- [x] MidLevelIrLoweringTests.Core::ObjectCreationAndFieldAccessLowerToAggregateOperations
- [x] MidLevelIrLoweringTests.Core::PrimaryRecordConstructorArgumentsLowerInEvaluationOrder
- [x] MidLevelIrLoweringTests.Core::ConstructorInitializerCombinationAppliesInitializerAfterConstructorFields
- [x] MidLevelIrLoweringTests.Core::NestedObjectAndArrayInitializersLowerRecursivelyInSourceOrder
- [x] MidLevelIrLoweringTests.Core::MemberCallsEvaluateReceiverBeforeExplicitArguments
- [x] MidLevelIrLoweringTests.Core::TargetTypedObjectCreationLowersWithDestinationType
- [x] MidLevelIrLoweringTests.Core::RegisterObjectCreationKeepsValueStyleLocalLowering
- [x] MidLevelIrLoweringTests.Core::HeapObjectCreationMarksLocalAsAddressableStorage

### MidLevelIrLoweringTests.LoweringInvariant  (1/1)
- [x] MidLevelIrLoweringTests.LoweringInvariant::AcceptedBoundOperationsDoNotProduceNullMirArtifacts

### MidLevelIrLoweringTests.PlaceLowerer  (26/26)
- [x] MidLevelIrLoweringTests.PlaceLowerer::FieldAssignmentLowersToAggregateUpdate
- [x] MidLevelIrLoweringTests.PlaceLowerer::FieldCompoundAssignmentLowersToAggregateReadModifyWrite
- [x] MidLevelIrLoweringTests.PlaceLowerer::FixedArrayInitializerAndConstantIndexLowerToAggregateIndexOperations
- [x] MidLevelIrLoweringTests.PlaceLowerer::PartialFixedArrayInitializersInsideObjectInitializersLowerWithZeroFilledTails
- [x] MidLevelIrLoweringTests.PlaceLowerer::FixedArrayElementAssignmentLowersToAggregateIndexUpdate
- [x] MidLevelIrLoweringTests.PlaceLowerer::LargeFixedArrayConstantIndexReadsAndWritesUseAddressBasedMemoryAccess
- [x] MidLevelIrLoweringTests.PlaceLowerer::LargeAggregateFieldAndConstantIndexReadsAndWritesUseAddressBasedMemoryAccess
- [x] MidLevelIrLoweringTests.PlaceLowerer::LocalFixedArrayCanLowerToSliceAndDynamicSliceRead
- [x] MidLevelIrLoweringTests.PlaceLowerer::InitSliceElementAssignmentCarriesInitializationWriteKindInMir
- [x] MidLevelIrLoweringTests.PlaceLowerer::OutParameterAssignmentCarriesInitializationWriteKindInMir
- [x] MidLevelIrLoweringTests.PlaceLowerer::TextSlicesLowerToViewProducingMir
- [x] MidLevelIrLoweringTests.PlaceLowerer::EmptyTextSlicesLowerToIdentityTextOperands
- [x] MidLevelIrLoweringTests.PlaceLowerer::SingleElementTextIndexingLowersToUnitLengthTextViews
- [x] MidLevelIrLoweringTests.PlaceLowerer::DynamicFixedArrayIndexMutationUsesAddressBasedMemoryAccess
- [x] MidLevelIrLoweringTests.PlaceLowerer::FixedArrayParameterDynamicIndexUsesAddressBasedMemoryAccess
- [x] MidLevelIrLoweringTests.PlaceLowerer::NestedLvalueChainsWithDynamicIndexCompoundAssignmentsUseAddressBasedMemoryAccess
- [x] MidLevelIrLoweringTests.PlaceLowerer::SliceMutationUsesAddressBasedMemoryAccess
- [x] MidLevelIrLoweringTests.PlaceLowerer::MixedCallMemberAndIndexPostfixChainsLowerToMir
- [x] MidLevelIrLoweringTests.PlaceLowerer::ExplicitPointerOperatorsAndConversionsLowerToMir
- [x] MidLevelIrLoweringTests.PlaceLowerer::FieldAndGlobalAddressExpressionsLowerToMirAddresses
- [x] MidLevelIrLoweringTests.PlaceLowerer::IndexedFieldAddressBehindRawPointerLowersToParameterBackedMirAddresses
- [x] MidLevelIrLoweringTests.PlaceLowerer::ImmutableGlobalAddressesLowerToReadonlyMirAddresses
- [x] MidLevelIrLoweringTests.PlaceLowerer::ConstGlobalAddressesLowerToFrozenMirAddresses
- [x] MidLevelIrLoweringTests.PlaceLowerer::ConstParameterAddressesLowerToFrozenMirAddresses
- [x] MidLevelIrLoweringTests.PlaceLowerer::ConstProvenanceLocalsAreMarkedInMir
- [x] MidLevelIrLoweringTests.PlaceLowerer::FrozenSliceAddressesLowerToReadonlyMirAddresses

### MidLevelIrLoweringTests.RuntimeDropLowerer  (15/15)
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::DestructorBlocksLowerBeforeStorageDeadAtScopeExit
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::WholeLocalDestructorDropsInPlaceWithoutCopyingToDropTemporary
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::EarlyReturnBranchDropDoesNotSuppressFallthroughScopeDrop
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::ReassigningADestructibleLocalLowersTheOldDropBeforeOverwrite
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::DynamicStorageDropsInitializedElementsBeforeFreeingBackingStorage
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::EnumPayloadCaptureDropsMovedPayloadOnlyOnce
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::LargeEnumPayloadCaptureDropsMovedPayloadOnlyOnce
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::LargeEnumPayloadCaptureEarlyReturnKeepsDropCleanup
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::LargeEnumPayloadReassignmentDropsOldAndReplacementOnce
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::PrimaryConstructorArgumentsMoveIntoReturnedOwnerDropState
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::ExplicitConstructorArgumentsMoveIntoReturnedOwnerDropState
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::FixedArrayElementsCascadeRuntimeDrops
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::HeapBackedFixedArrayElementsDropBeforeStorageDead
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::ImportedTypeDestructorsResolveHelpersInTheirDefiningModule
- [x] MidLevelIrLoweringTests.RuntimeDropLowerer::EnumPayloadDropsLowerThroughActiveTagDispatch

### MidLevelIrLoweringTests.SwitchPatternLowerer  (23/23)
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::LiteralSwitchLowersToMirSwitchTerminator
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::SwitchSectionsCanAssignAndBreakToSharedExit
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::GuardedSwitchLowersToBranchBasedCfg
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::GuardedDiscardSwitchLowersToBranchBasedCfg
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::MultiLabelSectionWithGuardedDiscardLowersInOrder
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::MultiLabelSectionsNormalizeIntoSectionDecisionTrees
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::CaptureSwitchPatternLowersToMatchLocalAndBody
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::AggregateSwitchPatternBindsScalarFieldsAfterPatternSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::AggregatePropertySwitchPatternBindsNamedFieldsAfterPatternSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::ListSwitchPatternBindsFixedArrayElementsAfterPatternSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::AggregateWholeValueSwitchPatternBindsMatchLocalAfterPatternSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::NestedAggregateWholeValueSwitchPatternBindsMatchLocalAfterPatternSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::EnumWholeValueSwitchPatternBindsMatchLocalAfterTagSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::NestedAggregateSwitchPatternBindsScalarLeavesAfterPatternSelection
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::EnumSwitchPatternsLowerToTagTestsAndActivePayloadExtractions
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::EnumSwitchExpressionCallIsLoweredOnceInMir
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::TextLiteralSwitchLowersToBranchBasedComparisonTree
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::LargeTextLiteralSwitchLowersThroughLengthPartitioning
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::UnicodeTextLiteralSwitchLowersSuccessfully
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::FloatLiteralSwitchLowersToBranchBasedCfg
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::RawPointerNullSwitchLowersToBranchBasedCfg
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::AggregateSwitchPatternsCanMatchAndCaptureTextLeaves
- [x] MidLevelIrLoweringTests.SwitchPatternLowerer::EnumSwitchPatternsCanMatchAndCaptureTextLeaves

### RawSingleLineLiteralRegressionTests  (1/1)
- [x] RawSingleLineLiteralRegressionTests::RawSingleLineLiteralsLowerAsComparisonOperands

### TraitsAndDoctrinesFeatureTests  (2/2)
- [x] TraitsAndDoctrinesFeatureTests::DynTraitFatPointerCarriesTypedVtablePointerInMir
- [x] TraitsAndDoctrinesFeatureTests::UnsafeDynTraitFromPartsCanRoundTripContextAndVtable

## SSA lowering, validation & optimization  (390/390)

### CompilerCliTests  (1/1)
- [x] CompilerCliTests::EmitSsaModePrintsSsaModule

### CompilerPipelineFullIntegrationTests  (1/1)
- [x] CompilerPipelineFullIntegrationTests::PipelineFoldsPureConstantsInMirAndOptimizedSsa

### CompilerPipelineOptimizeSsaTests  (144/144)
- [x] CompilerPipelineOptimizeSsaTests::CallableAddressTakenFactsArePrunedAfterDirectCallDevirtualization
- [x] CompilerPipelineOptimizeSsaTests::NonCapturingLambdaAddressTakenFactsArePrunedAfterDirectCallDevirtualization
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaPreservesSuccessorPhiIncomingFromSplitContinuationBlocks
- [x] CompilerPipelineOptimizeSsaTests::OptimizeSsaPreservesMaterializedGenericCallSymbols
- [x] CompilerPipelineOptimizeSsaTests::OptimizeSsaDevirtualizesIdenticalFunctionPointerPhiTargets
- [x] CompilerPipelineOptimizeSsaTests::OptimizeSsaKeepsMixedFunctionPointerPhiTargetsIndirect
- [x] CompilerPipelineOptimizeSsaTests::CleanupSsaRemovesSourceLevelIntegerAlgebraicIdentities
- [x] CompilerPipelineOptimizeSsaTests::CleanupSsaRemovesIntegerAlgebraicAbsorbingAndSameOperandIdentities
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsRepeatedUnknownAddsToMultiply
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsLongRepeatedAddRunToSingleMultiply
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaPreservesTrailingConstantsAfterRepeatedAddRun
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsMultipleContiguousRepeatedTerms
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaCombinesSafeConstantCoefficientTerms
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotCombineUnsafeConstantCoefficientTerms
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaRecognizesSafeLeftShiftCoefficients
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotTreatUnsafeLeftShiftAsCoefficient
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsRepeatedUnaryNegatedTerms
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaCombinesSameSignConstantTail
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDropsAdjacentZeroCoefficientTerms
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsSafeRepeatedSubtractionRuns
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotFoldUnsafeRepeatedSubtractionRuns
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotReassociateSeparatedOrdinaryTerms
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsWrappingAddRunsWithWrappingMultiply
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsWrappingSubtractRunsWithWrappingMultiply
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotRewriteSaturatingAddChains
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotRewriteSaturatingSubtractChains
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsRepeatedMultiplicationRunToExponent
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaGroupsIndependentRepeatedProductFactors
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaKeepsSeparatedProductFactorsSourceShaped
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaFoldsWrappingProductRunsToExponent
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaDoesNotRewriteSaturatingProductChains
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaRenderShowsFoldedLinearAndProductShapes
- [x] CompilerPipelineOptimizeSsaTests::CleanupSsaForwardsAggregateFieldThroughMatchingBranchPhi
- [x] CompilerPipelineOptimizeSsaTests::OptimizeSsaRemovesStaticRangeModuloAndDivisionIdentities
- [x] CompilerPipelineOptimizeSsaTests::CleanupSsaRemovesSameOperandDivisionAndModuloWhenSourceRangeExcludesZero
- [x] CompilerPipelineOptimizeSsaTests::CleanupSsaRemovesSameOperandIntegerComparisons
- [x] CompilerPipelineOptimizeSsaTests::ShapeBranchesSimplifiesBooleanReturnDiamondToCondition
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesSmallDirectCallsAndRerunsConstantPropagation
- [x] CompilerPipelineOptimizeSsaTests::AggregateConstructionSsaStoresFullConstructorsDirectlyIntoNonEscapedLocalFields
- [x] CompilerPipelineOptimizeSsaTests::AggregateConstructionSsaKeepsWholeStoreForRawEscapedLocal
- [x] CompilerPipelineOptimizeSsaTests::OwnershipTrafficSsaElidesDeadAggregateMoveTrafficForNonEscapedRoots
- [x] CompilerPipelineOptimizeSsaTests::OwnershipTrafficSsaKeepsMoveInvalidationForRawEscapedRoots
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaPreservesOwnershipSummariesOnRewrittenFunctions
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaElidesReserveWhenCapacityFactsProveNoop
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaFoldsTryReserveWhenCapacityFactsProveSuccess
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaKeepsTryReserveWhenCapacityMayBeInsufficient
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaKeepsReserveAfterOpaqueDynamicOwnerCall
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaPreservesCapacityFactsAcrossFullInitSliceHelperCall
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaDoesNotCommitLengthAfterUnrecognizedInitSliceCall
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaPreservesCapacityFactsAcrossReadOnlyLawCall
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaElidesFreeWhenBackingAllocationProvenAbsent
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaKeepsFreeWhenBackingAllocationMayExist
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaMarksMoveLastNonEmptyWhenPrefixFactsProveLength
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaMarksMoveAtInBoundsWhenIndexAndPrefixFactsProveIt
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaKeepsMoveAtBoundsCheckWhenIndexMayReachLength
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaDoesNotPromoteSparseUnsafeReadsToDensePrefixFacts
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaKeepsDistinctFieldOwnerFactsSeparate
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaPreservesEmptyPrefixAfterClearLoop
- [x] CompilerPipelineOptimizeSsaTests::DynamicAppendLoopSsaCommitsLengthOnceAfterLoop
- [x] CompilerPipelineOptimizeSsaTests::DynamicAppendLoopSsaKeepsPerIterationCommitWhenValueReadsChangingLength
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaPreservesConstProvenanceOnSynthesizedArgumentSlots
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesSmallModulePrivateDirectCallsWithoutExplicitInline
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaOptimizesThroughSourceBuiltDependencyBoundary
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesSmallMonomorphizedGenericHelpers
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaOptimizesSmallGenericAbstractionLikeHandWrittenCode
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaKeepsExplicitNoInlineMonomorphizedGenericHelpers
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaKeepsPublicOrdinaryNonWrapperDirectCallsWithoutExplicitInline
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesSmallPublicOrdinaryCallsWithConstantArguments
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaKeepsPublicOrdinaryConstantArgumentCallsWhenBodyHasDirectCalls
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesPublicWrapperDirectCallsWithoutExplicitInline
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesSmallPublicLawDirectCallsWithoutExplicitInline
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaInlinesDirectCallForwarderChains
- [x] CompilerPipelineOptimizeSsaTests::InlineSsaKeepsExplicitNoInlineDirectCalls
- [x] CompilerPipelineOptimizeSsaTests::ConstGraphCallCseReusesRepeatedFiniteLawReadsFromConstGraphs
- [x] CompilerPipelineOptimizeSsaTests::ConstGraphCallCseKeepsRepeatedReadonlyPointerLocalReadsWithoutPermanentConst
- [x] CompilerPipelineOptimizeSsaTests::ConstGraphCallCseKeepsRepeatedNonFiniteLawReads
- [x] CompilerPipelineOptimizeSsaTests::ConstGraphCallCseKeepsRepeatedFfiReads
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureIntegerRangesAndProvenComparisons
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsEmitVerboseOptimizationTraceSummary
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsJoinIntegerRangesAtPhis
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsPropagateJoinedRangesThroughDependentInstructions
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsPropagateBitwiseAndShiftRanges
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsUseKnownBitsToProveMaskedSingletons
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsUseConstantMasksToProveSignedValuesNonNegative
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsUseKnownBitsToProveShiftedLowBitIsZero
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsUseKnownBitsToProveEqualityFalse
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsPropagateDivisionAndModuloRanges
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesKnownBitsForMaskedComparisons
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesKnownBitsForImpossibleEquality
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesModuloRangeFacts
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesKnownBitsForShiftedMaskedComparisons
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureBranchTargetEntryRanges
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureBlockExitFactsForBranchScopedRanges
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureBranchTargetEntryNullability
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCapturePointerEqualityTargetEntryNullability
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCapturePointerInequalityFalseTargetEntryNullability
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCapturePointerAlignmentForAddressValues
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureFixedArraySliceLengths
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsJoinSliceLengthRangesAtPhis
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureTextSliceLengthRanges
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureDynamicStorageSliceLengthRanges
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureTextLiteralLengthCallRanges
- [x] CompilerPipelineOptimizeSsaTests::ConstantPropagationFoldsTextLiteralLengthCalls
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationPrecomputesPathFactsForLiteral
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationPrecomputesLiteralPathProjections
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationKeepsRuntimePathInputsOnStdlibPath
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationRetargetsLiteralPathWritesToConstVariants
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationRetargetsConstPathWritesToConstVariants
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationKeepsRuntimePathWritesOnSnapshotCapableVariants
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationRetargetsLiteralTextAppendsToConstDisjointVariants
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationRetargetsConstTextHelpers
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationRetargetsLiteralTextMemberAppends
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationKeepsRuntimeTextAppendsOnSnapshotCapableVariants
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationRetargetsLiteralTextFactoryHelpers
- [x] CompilerPipelineOptimizeSsaTests::ConstStdlibHelperSpecializationUsesWindowsPathSeparatorsForWindowsTargets
- [x] CompilerPipelineOptimizeSsaTests::ValueFactsCaptureExplicitWrappingAndSaturatingArithmeticRanges
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningRemovesProvenBranchAndStalePhiIncoming
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesBranchTargetEntryRanges
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesBranchTargetNullability
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesPointerEqualityNullability
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesBitwiseRangeFacts
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesExplicitArithmeticRangeFacts
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningKeepsWrappingArithmeticThatMayWrap
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningUsesTextLiteralLengthFacts
- [x] CompilerPipelineOptimizeSsaTests::AsciiToUnicodeLiteralSpecializationRewritesSmallLiteralCallsBeforeAbiLowering
- [x] CompilerPipelineOptimizeSsaTests::AsciiToUnicodeLiteralSpecializationLowersLargeLiteralToSsaCopy
- [x] CompilerPipelineOptimizeSsaTests::AsciiToUnicodeLiteralSpecializationKeepsNonAsciiLiteralOnBuiltinPath
- [x] CompilerPipelineOptimizeSsaTests::ConstantTextFormatSpecializationRewritesLargeIntegerAsciiFormatCallsBeforeAbiLowering
- [x] CompilerPipelineOptimizeSsaTests::ConstantTextFormatSpecializationRewritesLargeIntegerUnicodeFormatCallsBeforeAbiLowering
- [x] CompilerPipelineOptimizeSsaTests::ConstantTextFormatSpecializationKeepsRuntimeIntegerFormatCallsOnStdLibPath
- [x] CompilerPipelineOptimizeSsaTests::ConstantTextFormatSpecializationKeepsReadonlyDestinationIntegerFormatCalls
- [x] CompilerPipelineOptimizeSsaTests::ConstantTextFormatSpecializationNormalizesOutOfRangeNarrowedIntegerConstants
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenSwitchPruningRemovesCasesOutsideKnownInputRange
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenSwitchPruningRewritesSingleReachableCaseToBranch
- [x] CompilerPipelineOptimizeSsaTests::FactDrivenBranchPruningKeepsReusedForLoopVariableRangesScoped
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationRemovesDeadForwardedStackScalarStores
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationKeepsAddressTakenStackScalarStoresConservative
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationForwardsStackFieldLoadsFromSource
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationForwardsNestedStackFieldLoadsFromSource
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationPreservesStackFieldFactsAcrossPureScalarCallsFromSource
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationForwardsStackFieldFactsAcrossSinglePredecessorBlocksFromSource
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationForwardsFixedArrayElementFactsFromSource
- [x] CompilerPipelineOptimizeSsaTests::ScalarReplacementRemovesDeadStackFieldStoresFromSource
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationUsesFunctionEffectsForPureCallGlobalFacts

### SsaCrossBlockLoadForwardingRegressionTests  (1/1)
- [x] SsaCrossBlockLoadForwardingRegressionTests::InlinedLawReturnValueSurvivesFieldLoadForwardingAcrossBlocks

### SsaIrValidationTests  (95/95)
- [x] SsaIrValidationTests::UndefinedSsaValueReferenceFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedSsaConversionFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsizedIntegerConversionFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::CompileTimeIntegerConstantFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::IntegerConstantOutsideStorageFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::IntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedFloatConversionTargetFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DirectCallAbiMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DirectCallPromotedUnknownLocalFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DirectCallIndirectAddressAndPromotedStorageShapesAreAccepted
- [x] SsaIrValidationTests::IndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::IndirectCallLargeByvalAddressShapeIsAccepted
- [x] SsaIrValidationTests::FfiTextReturnFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::MirOnlyTempLocalStorageFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::NonHeapDeallocationFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::LocalUseWithoutAllocationFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionMissingAbiFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DuplicateSsaValueDefinitionFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::PhiIncomingTypeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::PhiIncomingFromNonPredecessorFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::PhiMissingIncomingFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::PhiDuplicateIncomingFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::PhiWithoutIncomingFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAbiReturnMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAbiRetborrowPointerReturnIsAccepted
- [x] SsaIrValidationTests::FunctionAbiParameterCountMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAbiParameterTypeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAbiSretShapeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAbiSretShapeAccepted
- [x] SsaIrValidationTests::AddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedSsaInstructionFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedSsaRValueFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedSsaValueFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedSsaTerminatorFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DynamicStorageElementLayoutFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DynamicStorageCapacityWidthFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DynamicStorageReserveAddressShapeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::DynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ElementAddressBasePointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ElementAddressResultPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedUnaryShapeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsizedIntegerUnaryFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedFloatUnaryFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::BinaryOperandShapeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::WrappingFloatOperatorFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ComparisonResultShapeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmission
- [x] SsaIrValidationTests::NamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmission
- [x] SsaIrValidationTests::ExtractFieldResultMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ExtractFieldNameIndexMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ExtractIndexOutOfRangeIsUnrepresentable
- [x] SsaIrValidationTests::InsertIndexValueMismatchIsUnrepresentable
- [x] SsaIrValidationTests::SliceViewIndexExtractionIsAccepted
- [x] SsaIrValidationTests::SliceViewNoOpRetypeSupportsImmutableComponentExtraction
- [x] SsaIrValidationTests::IndexOperationFamilyMismatchIsUnrepresentable
- [x] SsaIrValidationTests::TextViewIndexExtractionIsAccepted
- [x] SsaIrValidationTests::TextViewFieldExtractionIsAccepted
- [x] SsaIrValidationTests::SelectArmShapeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsupportedSwitchConditionFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnsizedIntegerSwitchConditionFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SwitchCaseShapeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SliceCreationShapeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::IndirectLoadAddressShapeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::IndirectStoreAcceptsQualifiedPointeeShape
- [x] SsaIrValidationTests::CopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::CopyMemorySourcePointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::CopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::CopyMemoryAllowsFixedArrayElementPointers
- [x] SsaIrValidationTests::ScopedNoAliasProofCarrierAcceptsMatchingParameterRoots
- [x] SsaIrValidationTests::ScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmission
- [x] SsaIrValidationTests::ScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::StringConstantNonTextTypeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::TextDataAddressPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::GlobalAddressPointeeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::UnknownGlobalLoadFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::KnownGlobalLoadTypeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::KnownGlobalAddressTypeMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::StoreToImmutableGlobalFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::StoreGlobalValueMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::StoreMutableKnownGlobalPassesSsaValidation
- [x] SsaIrValidationTests::FunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAddressMissingAbiFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::FunctionAddressSignatureMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidation
- [x] SsaIrValidationTests::SystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::SystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmission
- [x] SsaIrValidationTests::ValidSystemBuiltinSignaturesPassSsaValidation

### SsaLoweringTests  (37/37)
- [x] SsaLoweringTests::BranchJoinProducesPhiForMergedLocal
- [x] SsaLoweringTests::CommutativeRepeatedExpressionsShareAValueNumber
- [x] SsaLoweringTests::OptimizedSsaRemovesTrivialPhiNodesAndRewritesReturns
- [x] SsaLoweringTests::EmptyTrampolineBlocksAreCollapsed
- [x] SsaLoweringTests::VoidCallStatementsLowerToStatementOnlySsaInstructions
- [x] SsaLoweringTests::LoopHeaderProducesPhiForBackedgeValue
- [x] SsaLoweringTests::AggregateBranchJoinProducesByValuePhi
- [x] SsaLoweringTests::UnreachableJoinBlocksArePrunedFromSsa
- [x] SsaLoweringTests::AggregateFieldOperationsLowerToSsaExtractAndInsert
- [x] SsaLoweringTests::RegisterObjectCreationRemainsScalarizedInSsa
- [x] SsaLoweringTests::RegisterScalarInitializerRemainsDirectSsaValue
- [x] SsaLoweringTests::HeapObjectCreationUsesStorageBackedSsaLocalWithoutStackLifetimeMarkers
- [x] SsaLoweringTests::HeapFieldInitializationUsesAddressStoresWithoutAggregateLoads
- [x] SsaLoweringTests::HeapFixedArrayElementInitializationUsesAddressStores
- [x] SsaLoweringTests::AddressableAggregateAssignmentLowersToMemoryCopy
- [x] SsaLoweringTests::AggregateByValueCallInvalidatesMovedAddressableSource
- [x] SsaLoweringTests::AddressableAggregateInitializerDoesNotMaterializeAggregateTempLocals
- [x] SsaLoweringTests::AddressableAggregateConditionalDoesNotMaterializeAggregateTempLocals
- [x] SsaLoweringTests::NonAddressableFixedArrayDynamicIndexAllocatesCompilerGeneratedScratchLocalInSsa
- [x] SsaLoweringTests::FixedArrayIndexOperationsLowerToSsaExtractAndInsert
- [x] SsaLoweringTests::SliceLoweringUsesLocalSlotsAndSliceLoads
- [x] SsaLoweringTests::TextSlicesLowerToSsaViewOperations
- [x] SsaLoweringTests::ExplicitAsciiLiteralToUnicodeConversionLowersToUnicodeConstantInSsa
- [x] SsaLoweringTests::DynamicFixedArrayIndexMutationUsesIndirectAddressOps
- [x] SsaLoweringTests::SliceMutationUsesIndirectAddressOps
- [x] SsaLoweringTests::ExplicitPointerOperatorsAndConversionsLowerToSsa
- [x] SsaLoweringTests::FieldAndGlobalAddressExpressionsLowerToSsaAddresses
- [x] SsaLoweringTests::IndexedFieldAddressBehindRawPointerLowersToParameterBackedSsaAddresses
- [x] SsaLoweringTests::ImmutableGlobalAddressesLowerToReadonlySsaAddresses
- [x] SsaLoweringTests::FrozenSliceAddressesLowerToReadonlySsaAddresses
- [x] SsaLoweringTests::RuntimeDisjointTrueBranchCarriesScopedNoAliasFactsIntoSsaMemoryOperations
- [x] SsaLoweringTests::UnsafeAssumeDisjointCarriesUnsafeProofCarrierKindIntoSsaMemoryOperations
- [x] SsaLoweringTests::IndependentForLoopsPreserveLoopContractsInSsa
- [x] SsaLoweringTests::WillexitWhileLoopsPreserveLoopBehaviorOnBackedgesInSsa
- [x] SsaLoweringTests::IndependentSliceLoopsCarryAccessGroupsInSsa
- [x] SsaLoweringTests::InitAndOutAssignmentsCarryInitializationWriteKindInSsa
- [x] SsaLoweringTests::OrdinaryMutableSliceAssignmentKeepsReplacementWriteKindInSsa

### SsaOptimizationTests  (111/111)
- [x] SsaOptimizationTests::CleanupRemovesTrivialCopyInstructions
- [x] SsaOptimizationTests::CleanupPrunesPhiIncomingsForRemovedCfgEdges
- [x] SsaOptimizationTests::CleanupRemovesUnusedPureTemporaries
- [x] SsaOptimizationTests::CleanupRemovesIntegerAlgebraicIdentities
- [x] SsaOptimizationTests::CleanupRemovesIntegerModuloByOne
- [x] SsaOptimizationTests::CleanupRemovesSameOperandDivisionAndModuloWhenRangeExcludesZero
- [x] SsaOptimizationTests::CleanupRemovesSameOperandIntegerComparisons
- [x] SsaOptimizationTests::CleanupRemovesModuloAndDivisionWhenStaticRangeIsBelowPositiveDivisor
- [x] SsaOptimizationTests::CleanupForwardsAggregateFieldThroughPhiWhenIncomingFieldsMatch
- [x] SsaOptimizationTests::CleanupKeepsAggregateFieldExtractThroughPhiWhenIncomingFieldsDiffer
- [x] SsaOptimizationTests::CleanupForwardsAggregateIndexThroughPhiWhenIncomingElementsMatch
- [x] SsaOptimizationTests::CleanupForwardsAggregateFieldThroughSelectWhenSelectedFieldsMatch
- [x] SsaOptimizationTests::CleanupKeepsAggregateFieldExtractThroughSelectWhenSelectedFieldsDiffer
- [x] SsaOptimizationTests::CleanupForwardsAggregateIndexThroughSelectWhenSelectedElementsMatch
- [x] SsaOptimizationTests::CleanupRemovesIdentityPhiNodes
- [x] SsaOptimizationTests::CleanupReusesIdenticalMaterializedConstantConversions
- [x] SsaOptimizationTests::CleanupCanonicalizesEquivalentCommutativeExpressions
- [x] SsaOptimizationTests::CleanupRemovesRedundantSameTypeConversions
- [x] SsaOptimizationTests::CleanupCoalescesEquivalentPhiNodes
- [x] SsaOptimizationTests::CleanupSimplifiesBranchWithIdenticalTargets
- [x] SsaOptimizationTests::CleanupSimplifiesDefaultOnlySwitchToGoto
- [x] SsaOptimizationTests::CleanupRemovesUnusedLocalStorageScaffolding
- [x] SsaOptimizationTests::CleanupRemovesEmptyJumpOnlyBlocks
- [x] SsaOptimizationTests::CleanupMergesLinearBlocksWithSinglePredecessor
- [x] SsaOptimizationTests::CleanupSimplifiesSingleCaseSwitchToBranch
- [x] SsaOptimizationTests::CleanupKeepsDuplicateEdgeBranchesWhenSuccessorPhiNeedsBothIncomingValues
- [x] SsaOptimizationTests::CleanupNormalizesExhaustiveBoolSwitchToBranch
- [x] SsaOptimizationTests::CleanupDropsSwitchCasesThatAlreadyMatchDefaultTarget
- [x] SsaOptimizationTests::OptimizedSsaSimplifiesSingleCaseSwitchBeforeLlvmEmission
- [x] SsaOptimizationTests::DevirtualizerTurnsKnownDynTraitCallIntoDirectCallBeforeInlining
- [x] SsaOptimizationTests::OptimizedSsaNormalizesSmallSparseSwitchToCompareChainBeforeLlvmEmission
- [x] SsaOptimizationTests::OptimizedSsaKeepsDenseFourWaySwitchForLlvmLowering
- [x] SsaOptimizationTests::CleanupCanonicalizesEarlyReturnDiamonds
- [x] SsaOptimizationTests::OptimizedSsaCanonicalizesReturnPhiJoinsBeforeLlvmEmission
- [x] SsaOptimizationTests::CleanupRemovesLoopInvariantSelfReferentialPhiNodes
- [x] SsaOptimizationTests::OptimizedSsaRemovesLoopInvariantHeaderPhisBeforeLlvmEmission
- [x] SsaOptimizationTests::OptimizedSsaCanonicalizesBooleanCompareBranches
- [x] SsaOptimizationTests::OptimizedSsaFoldsConstantArithmetic
- [x] SsaOptimizationTests::OptimizedSsaFoldsConstantAggregateInitializerAccesses
- [x] SsaOptimizationTests::OptimizedSsaRemovesDeadAddressableLocalStorageBeforeLlvmEmission
- [x] SsaOptimizationTests::OptimizedSsaReusesRepeatedSliceLoadsWithinABlock
- [x] SsaOptimizationTests::OptimizedSsaDoesNotReuseLoadsAcrossStores
- [x] SsaOptimizationTests::FactDrivenBranchPruningThreadsProvenEdgesThroughBranchOnlyBlocks
- [x] SsaOptimizationTests::ValueFactsJoinLoopPhiInputsToAStableRange
- [x] SsaOptimizationTests::ValueFactsIgnoreUnreachablePhiInputs
- [x] SsaOptimizationTests::ValueFactsCaptureTextLiteralPayloadFacts
- [x] SsaOptimizationTests::ValueFactsPreserveOnlyIdenticalTextLiteralPayloadsThroughPhi
- [x] SsaOptimizationTests::ValueFactsDeriveTextSliceLiteralPayloads
- [x] SsaOptimizationTests::ValueFactsKeepDynamicTextSlicePayloadUnknown
- [x] SsaOptimizationTests::ValueFactsPublishBoundedRawPointerParameterRegions
- [x] SsaOptimizationTests::ValueFactsKeepZeroAllowedBoundedRawPointersNullable
- [x] SsaOptimizationTests::ValueFactsTrackDynamicStorageAllocationFieldsAndDataPointer
- [x] SsaOptimizationTests::ValueFactsTrackDynamicStorageTryReserveSuccessAndFailureEdges
- [x] SsaOptimizationTests::ValueFactsTrackDynamicStorageMoveLastLengthCommit
- [x] SsaOptimizationTests::ValueFactsTrackDynamicStorageLengthFieldStoreAsPrefixCommit
- [x] SsaOptimizationTests::ValueFactsTrackDynamicLengthReadDerivedCommitThroughOwnerAddress
- [x] SsaOptimizationTests::ValueFactsDemoteDynamicStorageAfterEscapedDataPointerCall
- [x] SsaOptimizationTests::DynamicStorageOptimizerKeepsReserveAfterEscapedDataPointerCall
- [x] SsaOptimizationTests::DynamicStorageOptimizerKeepsReserveAfterEscapedDataSliceCall
- [x] SsaOptimizationTests::ValueFactsTrackDynamicStorageZeroInitializerAsNoBackingAllocation
- [x] SsaOptimizationTests::ValueFactsKeepTextLocalPayloadUnknownWhenAddressTaken
- [x] SsaOptimizationTests::ValueFactsClampNonWrappingArithmeticToContinuingTypeRange
- [x] SsaOptimizationTests::ValueFactsDoNotCreateInvalidRangesWhenNonWrappingArithmeticAlwaysOverflows
- [x] SsaOptimizationTests::ValueFactsKeepWrappingArithmeticConservativeWhenRangeMayWrap
- [x] SsaOptimizationTests::ValueFactsClampSaturatingArithmeticInsteadOfUsingWrappingSemantics
- [x] SsaOptimizationTests::ValueFactsClampIntegerConversionsToTargetRange
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStoredStackScalarLoadsWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsAddressEscapedLocalLoads
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStoredStackFieldLoadsWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStoredStackFixedArrayElementLoadsWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackFixedArrayElementLoadsAcrossDynamicElementStores
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackFieldLoadsAcrossUnknownIndirectStores
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsNestedStackFieldLoadsWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsSiblingNestedStackFieldPathsSeparate
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationRemovesOverwrittenStackFieldStoresWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackFieldStoresAcrossUnknownIndirectStores
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStackFieldLoadsAcrossPureScalarCalls
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackFieldLoadsAcrossImpureCalls
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationRemovesOverwrittenStackFieldStoresAcrossReadonlyScalarCalls
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackFieldStoresAcrossReadonlyLocalMemoryCalls
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStackFieldLoadsAcrossSinglePredecessorBlocks
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackFieldLoadsAtJoinBlocks
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationRemovesOverwrittenStackScalarStoresWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStackScalarsAcrossSinglePredecessorBlocks
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsStackScalarLoadsAtJoinBlocks
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsRepeatedGlobalScalarLoadsWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsStoredGlobalScalarLoadsWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsGlobalScalarLoadsAcrossCallBarriers
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsGlobalScalarLoadsAcrossUnknownIndirectStores
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsGlobalScalarsAcrossSinglePredecessorBlocks
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsGlobalScalarLoadsAtJoinBlocks
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationRemovesOverwrittenGlobalScalarStoresWithinBlock
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsGlobalScalarStoresAcrossCallBarriers
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationForwardsGlobalScalarLoadsAcrossPureCalls
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationRemovesOverwrittenGlobalScalarStoresAcrossPureCalls
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsGlobalScalarStoresAcrossPureArgumentMemoryReadsWhenGlobalAddressIsPassed
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationRemovesOverwrittenGlobalScalarStoresAcrossPureArgumentMemoryReadsWithScalarArguments
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsPendingStackFieldStoresAcrossReadonlyReadsOfOtherLocals
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsPendingSiblingFieldStoresAcrossReadonlyFieldReads
- [x] SsaOptimizationTests::AliasAwareMemoryOptimizationKeepsPendingSiblingFieldStoresAcrossExactFieldCopies
- [x] SsaOptimizationTests::ScalarReplacementRemovesDeadStackFieldStores
- [x] SsaOptimizationTests::ScalarReplacementKeepsStackFieldStoresObservedByLaterLoad
- [x] SsaOptimizationTests::ScalarReplacementKeepsStackFieldStoresAcrossUnknownIndirectLoads
- [x] SsaOptimizationTests::ScalarReplacementKeepsStackFieldStoresAcrossUnknownCalls
- [x] SsaOptimizationTests::ScalarReplacementKeepsStackFieldStoresAfterAggregateAddressEscapes
- [x] SsaOptimizationTests::ScalarReplacementRemovesDeadExactScalarFieldCopies
- [x] SsaOptimizationTests::ScalarReplacementKeepsExactScalarFieldCopiesObservedByLaterLoad
- [x] SsaOptimizationTests::ScalarReplacementRemovesDeadAggregateCopiesToStackLocals
- [x] SsaOptimizationTests::ScalarReplacementKeepsAggregateCopiesObservedByLaterFieldLoad
- [x] SsaOptimizationTests::ScalarReplacementKeepsAggregateCopiesAfterDestinationAddressEscapes
- [x] SsaOptimizationTests::ScalarReplacementKeepsDeadAggregateMoveCopiesConservative

## Compiler pipeline passes  (33/33)

### CompilerCliTests  (2/2)
- [x] CompilerCliTests::VerboseLogVerbosityRequiresExplicitInfoLogLevelForPipelineLifecycleEvents
- [x] CompilerCliTests::ExplicitInfoLogLevelPrintsPipelineLifecycleEvents

### CompilerPipelineFullIntegrationTests  (1/1)
- [x] CompilerPipelineFullIntegrationTests::MinimalModuleRunsThroughTheFullPipeline

### CompilerPipelineLoadModulesTests  (1/1)
- [x] CompilerPipelineLoadModulesTests::LoadModulesReusesSourceParsesDiscoveredByModuleGraph

### CompilerPipelineMonomorphizationPlanTests  (16/16)
- [x] CompilerPipelineMonomorphizationPlanTests::RootGenericInstantiationsGetFullySpelledMonomorphizationSymbols
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnForwarderGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnMemberCallForwarderGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnFieldAccessWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnIndexAccessWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnConversionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnAddressOfWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnBinaryOperatorWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSingleReturnComparisonWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootTerminalIfSelectionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootTerminalSwitchSelectionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootObjectConstructionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootEnumConstructionWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::RootSimpleLocalUpdateWrapperGenericInstantiationsUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::SourceBackedImportedGenericInstantiationsUseDefiningModuleMonomorphizationSymbols
- [x] CompilerPipelineMonomorphizationPlanTests::ColdGenericInstantiationsPreferCodeSizeReductionInThePlan

### CompilerPipelineOptimizeSsaTests  (1/1)
- [x] CompilerPipelineOptimizeSsaTests::AliasAwareMemoryOptimizationRunsBeforeAbiLowering

### CompilerPipelineSpecializationCodegenStrategyTests  (5/5)
- [x] CompilerPipelineSpecializationCodegenStrategyTests::RootGenericInstantiationsChooseOwnedBodyCodegenStrategy
- [x] CompilerPipelineSpecializationCodegenStrategyTests::SourceBackedImportedLawGenericsChooseLawCloneAwareCodegenStrategy
- [x] CompilerPipelineSpecializationCodegenStrategyTests::ColdImportedLawGenericsUseAbiFallbackOnlyCodegenStrategy
- [x] CompilerPipelineSpecializationCodegenStrategyTests::DeclarationOnlyImportedGenericInstantiationsChooseAbiFallbackOnlyCodegenStrategy
- [x] CompilerPipelineSpecializationCodegenStrategyTests::ManifestBackedImportedGenericsWithoutPublishedAbiFactsDoNotClaimAbiFallbackInCodegenStrategy

### CompilerPipelineSpecializationPlanTests  (4/4)
- [x] CompilerPipelineSpecializationPlanTests::RootGenericInstantiationsPreferOwnedConcreteBodiesInSpecializationPlan
- [x] CompilerPipelineSpecializationPlanTests::SourceBackedImportedLawGenericsPreferCallerCloneBeforeOwnedBodyInSpecializationPlan
- [x] CompilerPipelineSpecializationPlanTests::SourceBackedImportedTopLevelLawGenericsPreferCallerCloneBeforeOwnedBodyInSpecializationPlan
- [x] CompilerPipelineSpecializationPlanTests::ColdImportedLawGenericsPreferAbiFallbackOverCallerCloneInSpecializationPlan

### CompilerPipelineSyntaxModelTests  (1/1)
- [x] CompilerPipelineSyntaxModelTests::BackendOpaqueCallableAttributeForcesNoInlineEffect

### LoweringContractValidationTests  (1/1)
- [x] LoweringContractValidationTests::ValidateLoweringContractRunsBeforeHirAndAcceptsTypedOperationFacts

### WideEnumTagRegressionTests  (1/1)
- [x] WideEnumTagRegressionTests::EnumsWithMoreThan128VariantsCompileThroughTheWholePipeline

## Runtime / native execution  (83/83)

### CompilerCliTests  (7/7)
- [x] CompilerCliTests::DefaultModeInfersExecutableFromExportedMain
- [x] CompilerCliTests::DefaultModeInfersLibraryWhenRootHasNoExportedMain
- [x] CompilerCliTests::EmitObjectModeWritesObjectFile
- [x] CompilerCliTests::CompileOnlyAliasWritesObjectFile
- [x] CompilerCliTests::EmitExecutableModeBuildsImportedAggregateDependencies
- [x] CompilerCliTests::EmitExecutableModeLinksManifestBackedLibrariesWithoutSource
- [x] CompilerCliTests::EmitExecutableModeLinksManifestBackedOverloadedLibrariesWithoutSource

### DynTraitObjectRuntimeTests  (2/2)
- [x] DynTraitObjectRuntimeTests::BorrowDynTraitObjectDispatchesPolymorphicallyAtRuntime
- [x] DynTraitObjectRuntimeTests::OwnedHeapDynTraitObjectAllocatesDispatchesAndDrops

### DynamicFixedArrayElementRuntimeTests  (1/1)
- [x] DynamicFixedArrayElementRuntimeTests::DynamicOfFixedArrayWritesReadsAndSlicesPreserveRows

### FixedArrayOrderedComparisonRuntimeTests  (1/1)
- [x] FixedArrayOrderedComparisonRuntimeTests::FixedArrayOrderedComparisonsAreLexicographicAtRuntime

### FloatConstantEmissionRuntimeTests  (1/1)
- [x] FloatConstantEmissionRuntimeTests::IntegralAndScientificFloatConstantsBuildAndRunThroughLlvmBackend

### GenericUseSiteInstantiationIntegrationTests  (1/1)
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedTypedInterfaceModifiersWithoutCompilerFactsStillSpecializeAndRun

### IfWhilePatternRuntimeTests  (4/4)
- [x] IfWhilePatternRuntimeTests::IfPatternBindsOnMatchAndTakesElseOnMismatch
- [x] IfWhilePatternRuntimeTests::WhilePatternDrainsUntilNoMatch
- [x] IfWhilePatternRuntimeTests::IfPatternSupportsResultNestedStructAndLiteralPatterns
- [x] IfWhilePatternRuntimeTests::IfAndWhilePatternsDropOwnedCapturesExactlyOnce

### IntegerExponentRuntimeTests  (1/1)
- [x] IntegerExponentRuntimeTests::IntegerExponentiationRunsAtRuntime

### LargeOwnedAggregateRuntimeTests  (2/2)
- [x] LargeOwnedAggregateRuntimeTests::LargeEnumPayloadMovesPreserveSingleOwnerDropSemantics
- [x] LargeOwnedAggregateRuntimeTests::MutableGlobalReloadsAfterDestructorBackedCall

### MidLevelIrDynamicFixedArrayIndexingRuntimeTests  (1/1)
- [x] MidLevelIrDynamicFixedArrayIndexingRuntimeTests::DynamicIndexingOnFixedArrayTemporaryCompilesAndRuns

### MidLevelIrRuntimeTests  (38/38)
- [x] MidLevelIrRuntimeTests::NonAsciiTextLiteralsPreferAsciiOverloadsAtRuntime
- [x] MidLevelIrRuntimeTests::CompileTimeTextConcatenationMatchesSingleLiteralAtRuntime
- [x] MidLevelIrRuntimeTests::FixedCapacityTextConcatenationCopiesIntoStackStorageAtRuntime
- [x] MidLevelIrRuntimeTests::TextConcatenationRejectsNegativeDestinationCapacityAtRuntime
- [x] MidLevelIrRuntimeTests::FixedCapacityInterpolatedTextFormatsIntoStackStorageAtRuntime
- [x] MidLevelIrRuntimeTests::FixedCapacityUnicodeInterpolatedTextFormatsIntoStackStorageAtRuntime
- [x] MidLevelIrRuntimeTests::CompileTimeInterpolatedTextMatchesSingleLiteralAtRuntime
- [x] MidLevelIrRuntimeTests::StrictFpFunctionsCompileAndRunAtRuntime
- [x] MidLevelIrRuntimeTests::FloatingPointArithmeticChainsCompileAndRunAtRuntime
- [x] MidLevelIrRuntimeTests::HeapStorageUsesAllocatorLoweringAtRuntime
- [x] MidLevelIrRuntimeTests::SystemMemoryReallocatePreservesAllocatorProvenanceAtRuntime
- [x] MidLevelIrRuntimeTests::FixedArrayParameterDynamicIndexReadsAtRuntime
- [x] MidLevelIrRuntimeTests::PartialFixedArrayInitializersInsideObjectInitializersZeroFillTrailingElementsAtRuntime
- [x] MidLevelIrRuntimeTests::SingleElementAsciiTextIndexReadsAtRuntime
- [x] MidLevelIrRuntimeTests::EmptyTextSlicesPreserveFullViewAtRuntime
- [x] MidLevelIrRuntimeTests::RecordEqualityAndInequalityCompareScalarFieldsAtRuntime
- [x] MidLevelIrRuntimeTests::RecordOrderedComparisonsAreLexicographicAtRuntime
- [x] MidLevelIrRuntimeTests::FixedArrayEqualityAndInequalityCompareScalarElementsAtRuntime
- [x] MidLevelIrRuntimeTests::VoidConditionalMemberCallStatementsExecuteAtRuntime
- [x] MidLevelIrRuntimeTests::EnumEqualityAndInequalityCompareTagAndPayloadAtRuntime
- [x] MidLevelIrRuntimeTests::EnumOrderedComparisonsAreLexicographicAtRuntime
- [x] MidLevelIrRuntimeTests::LargerScalarizableAggregatesCompareAtRuntime
- [x] MidLevelIrRuntimeTests::TextAndAggregateTextEqualityCompareContentsAtRuntime
- [x] MidLevelIrRuntimeTests::SliceAndAggregateSliceEqualityCompareViewIdentityAtRuntime
- [x] MidLevelIrRuntimeTests::ReassigningEnumDropsOnlyThePreviousActivePayloadAtRuntime
- [x] MidLevelIrRuntimeTests::NestedAggregateDropsCascadeThroughStructFieldsAndEnumPayloadsAtRuntime
- [x] MidLevelIrRuntimeTests::SwitchPatternCaptureDropsMovedEnumPayloadAtRuntime
- [x] MidLevelIrRuntimeTests::MutableBorrowReceiverCallsObserveSharedStateAtRuntime
- [x] MidLevelIrRuntimeTests::NestedMutableBorrowReceiverCallsMutateStoredFieldAtRuntime
- [x] MidLevelIrRuntimeTests::StoreBorrowFieldsMutateOriginalOwnerAtRuntime
- [x] MidLevelIrRuntimeTests::OutArgumentsWriteBackToCallerLocalsAtRuntime
- [x] MidLevelIrRuntimeTests::SwitchExpressionCallOnEnumIsEvaluatedOnceAtRuntime
- [x] MidLevelIrRuntimeTests::AggregateAndEnumSwitchPatternsMatchTextLeavesAtRuntime
- [x] MidLevelIrRuntimeTests::RawPointerIndexedFieldAddressesObserveSharedStateAtRuntime
- [x] MidLevelIrRuntimeTests::RawPointerIndexedElementsObserveSharedStateAtRuntime
- [x] MidLevelIrRuntimeTests::BorrowReceiverIndexedFieldAddressesObserveSharedStateAtRuntime
- [x] MidLevelIrRuntimeTests::LargeAggregateByValueCallsAndReturnsWorkAtRuntime
- [x] MidLevelIrRuntimeTests::LabeledBreakAndContinueTargetOuterLoopsAtRuntime

### MultiFileIntegrationTests  (4/4)
- [x] MultiFileIntegrationTests::ManifestBackedLibrariesCanBeConsumedWithoutSourceFiles
- [x] MultiFileIntegrationTests::ManifestBackedPublicGlobalsLinkAcrossPackageBoundaries
- [x] MultiFileIntegrationTests::SystemTextSourceModuleSupportsRuntimeAsciiUnicodeConversionHelpers
- [x] MultiFileIntegrationTests::SystemTextSourceModuleSupportsRuntimeUtf16ConversionHelpers

### PackageImageCliToolingTests  (2/2)
- [x] PackageImageCliToolingTests::EmitExecutableLinksImportedPackageNativeSources
- [x] PackageImageCliToolingTests::EmitExecutableUsesPackageNativePkgConfigMetadata

### ProjectCliTests  (11/11)
- [x] ProjectCliTests::RunPrefersRepoStdlibDistPackageBeforeRepoSource
- [x] ProjectCliTests::RunUsesSolutionDefaultRunTarget
- [x] ProjectCliTests::TestRunsCurrentTestProjectFromManifest
- [x] ProjectCliTests::TestGeneratesFactRunnerFromMetadataAndAppliesFilter
- [x] ProjectCliTests::TestGeneratedFactRunnerAppliesPlatformGatesFromTargetTriple
- [x] ProjectCliTests::TestGeneratedFactRunnerAppliesSerialCollections
- [x] ProjectCliTests::TestGeneratedFactRunnerExpandsInlineDataTheories
- [x] ProjectCliTests::TestGeneratedFactRunnerExpandsMemberDataTheories
- [x] ProjectCliTests::TestGeneratedFactRunnerReportsFailingFactThroughExitCode
- [x] ProjectCliTests::TestReturnsFailureWhenTestExecutableFails
- [x] ProjectCliTests::TestRunsSolutionDefaultTestTargetAndPathDependencies

### StructLayoutInteropRuntimeTests  (1/1)
- [x] StructLayoutInteropRuntimeTests::CStructLayoutAttributesMatchNativeCFixturesAtRuntime

### TextOrderedComparisonRuntimeTests  (1/1)
- [x] TextOrderedComparisonRuntimeTests::TextOrderedComparisonsAreLexicographicAtRuntime

### TryPropagationRuntimeTests  (4/4)
- [x] TryPropagationRuntimeTests::TrySameErrorTypePropagatesAndUnwrapsAtRuntime
- [x] TryPropagationRuntimeTests::TryFromConversionAndUnitFailurePropagateOnBothPathsAtRuntime
- [x] TryPropagationRuntimeTests::TryStdlibIOResultPropagatesIntoUserEnumThroughFunnelAtRuntime
- [x] TryPropagationRuntimeTests::TryDropsOwnedPayloadsExactlyOnceThroughFromConversionAtRuntime

### UnsignedIntegerRuntimeTests  (1/1)
- [x] UnsignedIntegerRuntimeTests::UnsignedSmallMediumAndWideIntegerOperationsRunCorrectly

## Package image (typed compilation behavior)  (281/281)

### CompilerCliTests  (1/1)
- [x] CompilerCliTests::EmitLibraryModeBuildsStaticLibraryAndManifest

### CompilerPipelineEnumLayoutTests  (2/2)
- [x] CompilerPipelineEnumLayoutTests::ManifestBackedModulesPreservePublishedLayoutFactsFromCompilerFactSections
- [x] CompilerPipelineEnumLayoutTests::PackageImagesPreserveGenericResultAndStatusEnumLayouts

### CompilerPipelineFullIntegrationTests  (68/68)
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedAsmLibrariesResolveWithoutSourceFilesAndStayAbiDeclarations
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedAsmLibrariesRejectMismatchedTargetArchitectures
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedStrictFpFunctionsPreserveModifierAndEffects
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedDoctrineLibrariesResolveWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedDoctrineMethodsResolveFromPackageImageFactsWhenBridgeSignatureSourceIsCorrupted
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedTraitLibrariesResolveWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedTraitMethodsResolveFromPackageImageFactsWhenBridgeSignatureSourceIsCorrupted
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedTraitAndDoctrineOptimizationRulesStayAbiBounded
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedEnumsPreserveVariantShapesWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedThreadSafetyLawAttributesFeedComptimeStructuralFactsWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedMethodThreadSafetyLawPredicatesFeedComptimeStructuralFactsWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedGenericEnumsCanBeInstantiatedWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedOverloadedFunctionsResolveWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedGenericFunctionsPreserveTypeParametersWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesStructuredTypedInterfaceSections
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesExplicitSourceSurfaceSections
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesExplicitCompilerSections
- [x] CompilerPipelineFullIntegrationTests::PackageImageSourceBridgeKeepsSupportedGenericTemplateBodiesDeclarationOnlyWhenTypedInterfaceIsPresent
- [x] CompilerPipelineFullIntegrationTests::StructuredPackageImageDocumentsKeepTypedLoopControlGenericDeclarationsWhenBodyTextIsCorrupted
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedModulesCanBeReconstructedFromStructuredTypedInterfaceSections
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesStructuredFunctionEffectFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesStructuredAbiFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesStructuredLayoutFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesStructuredSemanticBorrowFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesStructuredSemanticCallFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesOnlyApiVisibleGenericTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesGenericTemplateBodySections
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesTypedTemplateBodiesForMethodsOnGenericTypes
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesGenericTemplateSemanticSummaries
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesGenericTemplateSemanticCallFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesGenericTemplateEffectiveKindsInSemanticSummaries
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesGroupedLocalDeclarationTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesDiscardedExpressionStatementTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesUninitializedLocalDeclarationTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesObjectInitializerLocalDeclarationTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesEmptyBlockAndOpenEndedLoopTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesNestedInitializerObjectCreationTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesAssignmentExpressionTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesRawPointerDereferenceTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesProjectedRawPointerDereferenceTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesAddressOfTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesPowerTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPublishesComparisonChainTypedTemplateBodies
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateObjectCreationFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTargetTypedDefaultObjectCreationFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateObjectInitializerMemberFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateLocalDeclarationFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesFirstTypedGenericTemplateBodySubset
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedSwitchTemplateBodySubset
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateConversionFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateEnumConstructorFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateEnumCallFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateEnumValueFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateEnumPatternFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateEnumPatternMemberFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateAggregatePatternFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateDirectCallFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateFieldAccessFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesTypedGenericTemplateMemberCallFacts
- [x] CompilerPipelineFullIntegrationTests::PackageManifestPreservesRecordPrimaryConstructorParameters
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesDeferredGenericInstantiationPatterns
- [x] CompilerPipelineFullIntegrationTests::PackageManifestIncludesDeferredGenericTypeInstantiationPatterns
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedGenericMethodsPreserveTypeParametersWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedTypeAliasesRoundTripWithoutSourceFiles
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedTypeAliasesResolveFromPackageImageFactsWhenBridgeAliasSourceIsCorrupted
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedAliasDoctrineAndTraitImportsResolveFromPackageImageFactsWhenImportedParseTreeIsEmpty
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedInternalTypeAliasesRemainHiddenFromConsumers
- [x] CompilerPipelineFullIntegrationTests::ManifestBackedTypeDestructorsRoundTripWithoutSourceFiles

### CompilerPipelineFunctionEffectsTests  (1/1)
- [x] CompilerPipelineFunctionEffectsTests::ManifestBackedModulesPreserveImportedFunctionEffectsFromCompilerFactSections

### CompilerPipelineIntegrationTests  (1/1)
- [x] CompilerPipelineIntegrationTests::ManifestBackedLibrariesResolveWithoutSourceFiles

### CompilerPipelineLoadModulesTests  (8/8)
- [x] CompilerPipelineLoadModulesTests::FileSystemResolverDoesNotLetNestedStarkBuildManifestsShadowExplicitSourceRoots
- [x] CompilerPipelineLoadModulesTests::ManifestBackedModulesPreservePublishedSemanticFactsFromCompilerFactSections
- [x] CompilerPipelineLoadModulesTests::ManifestBackedModulesPreservePublishedSemanticCallFactsFromCompilerFactSections
- [x] CompilerPipelineLoadModulesTests::ManifestBackedModulesPreservePublishedOwnershipFactsFromCompilerFactSections
- [x] CompilerPipelineLoadModulesTests::ManifestBackedModulesPreservePublishedGenericTemplateSemanticCallFacts
- [x] CompilerPipelineLoadModulesTests::PackageImageDocumentResolversLoadStructuredImportsWithoutAnySourceText
- [x] CompilerPipelineLoadModulesTests::PackageImageDocumentResolversLoadNonReExportImportsWithoutAnySourceText
- [x] CompilerPipelineLoadModulesTests::ManifestBackedModulesPreserveOptimizationReadyGenericTemplateFacts

### CompilerPipelineLowerAbiTests  (2/2)
- [x] CompilerPipelineLowerAbiTests::ManifestBackedModulesPreservePublishedAbiFactsFromCompilerFactSections
- [x] CompilerPipelineLowerAbiTests::ManifestBackedMethodsPreservePublishedAbiFactsFromCompilerFactSections

### CompilerPipelineLowerHirTests  (3/3)
- [x] CompilerPipelineLowerHirTests::ManifestBackedGenericFunctionsMaterializeConcreteBodiesFromPackageImageTemplates
- [x] CompilerPipelineLowerHirTests::ManifestBackedEnumWholeCaptureGenericBodiesPreserveTypedTemplateFactsIntoHighLevelIr
- [x] CompilerPipelineLowerHirTests::LowerMirUsesPublishedTemplateObjectCreationFactsForImportedGenericBodies

### CompilerPipelineLowerMirTests  (80/80)
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedTemplateBodiesLowerFromPackageImageFactsWhenImportedDeclarationSyntaxIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedTemplateBodiesLowerFromPackageImageFactsWhenImportedParseTreeIsEmpty
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedGroupedLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedUninitializedLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedDiscardedExpressionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedConversionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedAssignmentTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedIfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedTerminalIfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedWhileTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedPatternConditionTemplateBodiesPublishAndImportForGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedForTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedLoopControlTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedGenericMethodBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedTemplateBodiesForMethodsOnGenericTypesLowerWithoutBridgeBodyText
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedComparisonChainTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedRawPointerDereferenceTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedProjectedRawPointerDereferenceTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedAddressOfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedPowerTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedAssignmentExpressionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedObjectInitializerLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedEmptyBlockAndOpenEndedLoopTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesCanConstructImportedPrimaryConstructorTypes
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedLocalDeclarationTypesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericOwnershipFactsAreSubstitutedForImportedTypedTemplateBodies
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedConstTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreserveConstProvenanceLocalsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedMultiLocalTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedConditionalTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedBinaryTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedShortCircuitTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedComparisonConditionsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericImportsCanLoadFromExplicitCompilerSectionsWhenLegacyFieldsAreMissing
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericImportsPreferExplicitCompilerSectionsOverConflictingLegacyFields
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedFieldAccessTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedChainedMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedDirectCallReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedObjectCreationReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedGroupedConditionalReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedVoidDirectCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedVoidMemberCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedConditionalCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedFieldAndIndexAssignmentTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedCompoundFieldAndIndexAssignmentTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedIndexAccessTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedFullViewTextSliceTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPublishAndLowerRetborrowTypedTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedTextSliceTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedSingleElementTextIndexTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedChainedFieldIndexTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedVoidReturnTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedObjectCreationTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedTypedNestedInitializerObjectCreationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedEnumCallTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedTryPropagationTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedEnumValueTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedLiteralTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreferTypedEnumConstructorTemplateBodiesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedConversionTargetsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedEnumConstructorFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedEnumCallFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedEnumValueFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedEnumPatternFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedEnumPatternMemberFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedAggregatePatternFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedNestedAndLiteralSwitchPatternFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedEnumWholeCaptureSwitchPatternFactsWithoutBridgeBodyText
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedLiteralAndGuardedSwitchFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesFoldComptimeStructuralFactsInMir
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedObjectInitializerMembersWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedObjectCreationTypesWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedDirectCallTargetsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedFieldAccessFactsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesUsePublishedMemberCallTargetsWhenBridgeSourceIsCorrupted
- [x] CompilerPipelineLowerMirTests::ManifestBackedGenericBodiesPreserveTransitiveImportedModuleSurfaceAcrossPackageImages
- [x] CompilerPipelineLowerMirTests::ManifestBackedConcreteGenericAliasesMaterializeObjectInitializersAndGroupedConditionalsInMir

### CompilerPipelineMonomorphizationPlanTests  (9/9)
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedGenericInstantiationsUseRootOwnedMonomorphizationSymbols
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedColdGenericInstantiationsPreservePackageImagePlanningFacts
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedTerminalSelectionGenericBodiesUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedGenericPlanningUsesPublishedTemplateSummaryWhenImportedBridgeBodyIsCorrupted
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedSingleReturnForwarderGenericBodiesUseOptimizationSummaryForPlanning
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedLargeAggregateGenericInstantiationsPreferCodeSizeReductionFromPublishedLayoutFacts
- [x] CompilerPipelineMonomorphizationPlanTests::RepeatedManifestBackedNestedGenericInstantiationsStayRootOwnedAndDeduplicatedInMonomorphizationPlan
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedNestedGenericPlanningUsesPublishedDeferredTriggers
- [x] CompilerPipelineMonomorphizationPlanTests::ManifestBackedNestedGenericTypePlanningUsesPublishedDeferredTypeTriggers

### CompilerPipelineOptimizeSsaTests  (7/7)
- [x] CompilerPipelineOptimizeSsaTests::ArithmeticFoldSsaOptimizesImportedGenericTypedTemplateAfterSourceBodyRemoval
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaPreservesCapacityFactsAcrossImportedReadOnlyLawCall
- [x] CompilerPipelineOptimizeSsaTests::DynamicStorageSsaPreservesFactsAcrossImportedGenericInlineHelper
- [x] CompilerPipelineOptimizeSsaTests::ConstLookupTableOptimizationFoldsPackageConstFixedArrayIndexFromTypedInitializer
- [x] CompilerPipelineOptimizeSsaTests::ConstLookupTableOptimizationFoldsPackageConstLookupHelperFromTypedInitializer
- [x] CompilerPipelineOptimizeSsaTests::ConstLookupTableOptimizationKeepsConstLookupHelperWithRuntimeIndexAsLoad
- [x] CompilerPipelineOptimizeSsaTests::ConstLookupTableOptimizationKeepsStaticFixedArrayIndexAsGlobalLoad

### CompilerPipelineSemanticValidateTests  (1/1)
- [x] CompilerPipelineSemanticValidateTests::ManifestBackedSemanticValidationUsesPublishedBorrowFactsFromCompilerFactSections

### CompilerPipelineSpecializationPlanTests  (5/5)
- [x] CompilerPipelineSpecializationPlanTests::ManifestBackedImportedLawGenericsPreferCallerCloneWhenTemplateSemanticsSurviveWithoutFunctionSemantics
- [x] CompilerPipelineSpecializationPlanTests::ManifestBackedImportedPlainFnGenericsThatStrengthenToLawPreferCallerCloneWhenTemplateSemanticsSurviveWithoutFunctionSemantics
- [x] CompilerPipelineSpecializationPlanTests::ManifestBackedRecursiveImportedLawGenericsDoNotPreferCallerCloneWhenTemplateSemanticsSurviveWithoutFunctionSemantics
- [x] CompilerPipelineSpecializationPlanTests::DeclarationOnlyImportedGenericInstantiationsFallBackToAbiOnlySpecializationPlan
- [x] CompilerPipelineSpecializationPlanTests::ManifestBackedImportedGenericsWithoutPublishedAbiFactsPreferOwnedBodyOnlyInSpecializationPlan

### CompilerPipelineTypeCheckTests  (9/9)
- [x] CompilerPipelineTypeCheckTests::ManifestBackedGenericEnumsRecordTypeInstantiationTriggersWithoutSourceFiles
- [x] CompilerPipelineTypeCheckTests::ManifestBackedGenericFunctionsRecordInstantiationTriggersWithoutSourceFiles
- [x] CompilerPipelineTypeCheckTests::ManifestBackedStaticMemberFunctionsPreserveStaticAndFunctionKindContracts
- [x] CompilerPipelineTypeCheckTests::ManifestBackedMemberFunctionsPreserveVisibility
- [x] CompilerPipelineTypeCheckTests::ManifestBackedGlobalsResolveFromPackageImageFactsWhenBridgeGlobalSourceIsCorrupted
- [x] CompilerPipelineTypeCheckTests::ManifestBackedNamedTypeShapeResolvesFromPackageImageFactsWhenBridgeTypeSourceIsCorrupted
- [x] CompilerPipelineTypeCheckTests::ManifestBackedRecordPrimaryConstructorsResolveFromPackageImageFactsWhenBridgeTypeSourceIsCorrupted
- [x] CompilerPipelineTypeCheckTests::ManifestBackedExplicitStructConstructorsResolveFromPackageImageFactsWithoutBridgeDeclarations
- [x] CompilerPipelineTypeCheckTests::ManifestBackedExplicitRecordConstructorsResolveFromPackageImageFactsWithoutBridgeDeclarations

### CopyableDoctrineTests  (1/1)
- [x] CopyableDoctrineTests::DestructorFlagRoundTripsThroughPackageImageFacts

### GenericUseSiteInstantiationIntegrationTests  (7/7)
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedNestedGenericTypePlanningDiscoversNestedLayoutsFromImportedUseSites
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedGenericMethodsAndNestedGenericTypesLowerThroughImportedTemplateBodies
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedGenericMethodsLoadDirectlyFromStructuredPackageImageFactsEvenWhenBodyTextIsCorrupted
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedLoopControlGenericMethodsLoadDirectlyFromStructuredPackageImageFactsEvenWhenBodyTextIsCorrupted
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedBridgeGenericBodiesLoadWithoutSourceSurfaceFunctionEntriesWhenTypedInterfaceCarriesOverloadKeys
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedRecursiveGenericPlanningFallsBackToPublishedCallSummariesWithoutDeferredFunctionTriggers
- [x] GenericUseSiteInstantiationIntegrationTests::ManifestBackedGenericTypePlanningFallsBackToPublishedTemplateFactsWithoutDeferredTypeTriggers

### GenericUseSiteInstantiationRegressionTests  (4/4)
- [x] GenericUseSiteInstantiationRegressionTests::ManifestBackedGenericMethodsAndNestedGenericTypesMaterializeFromPublishedTemplateBodies
- [x] GenericUseSiteInstantiationRegressionTests::ManifestBackedRecursiveGenericPlanningFallsBackToPublishedCallSummariesWhenDeferredFunctionTriggersAreMissing
- [x] GenericUseSiteInstantiationRegressionTests::ManifestBackedGenericTypePlanningFallsBackToPublishedTemplateFactsWhenDeferredTypeTriggersAreMissing
- [x] GenericUseSiteInstantiationRegressionTests::ManifestBackedDictionaryKeyConstraintRejectsUnprovenKeyTypes

### PackageImageArchitectureTests  (28/28)
- [x] PackageImageArchitectureTests::PackageImagePreservesAssociatedTypesAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesMethodStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesVisibilityStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesEnumErrorFunnelsAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesFieldAndPayloadTypePredicatesAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImageConsumerLowersImportedGenericForTraversalTypedBody
- [x] PackageImageArchitectureTests::PackageImagePreservesLabeledLoopControlInImportedGenericTypedBody
- [x] PackageImageArchitectureTests::PackageImagePreservesFunctionPointerStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesDynTraitStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesFieldAndEnumPayloadQualifierFactsAcrossTypedInterfaceSourceBridgeAndFacts
- [x] PackageImageArchitectureTests::PackageImagePreservesComptimeGenericDeclarationsAndSymbolicTemplateCalls
- [x] PackageImageArchitectureTests::PackageImageConsumerLowersImportedComptimeGenericTemplateTypedBody
- [x] PackageImageArchitectureTests::PackageImageConsumerLowersImportedOrdinaryComptimeTemplateTypedBody
- [x] PackageImageArchitectureTests::PackageImageConsumerLowersImportedComptimeGenericArithmeticTemplateTypedBody
- [x] PackageImageArchitectureTests::PackageImageConsumerFoldsImportedComptimeTemplateCallWithStatementBody
- [x] PackageImageArchitectureTests::PackageImageConsumerFoldsImportedComptimeTemplateCallWithMemberCalls
- [x] PackageImageArchitectureTests::PackageImageConsumerFoldsImportedComptimeTemplateCallWithObjectInitializers
- [x] PackageImageArchitectureTests::PackageImageConsumerFoldsImportedComptimeTemplateCallWithTextConstants
- [x] PackageImageArchitectureTests::PackageImageConsumerFoldsImportedComptimeTemplateCallWithPatterns
- [x] PackageImageArchitectureTests::PackageImageBackedDefaultNonOverlapCallsRejectOverlappingArguments
- [x] PackageImageArchitectureTests::PackageImageBackedWhereOverlapCallsAllowOverlappingArguments
- [x] PackageImageArchitectureTests::PackageImageBackedSubregionDisjointContractsRejectOverlappingImportedCalls
- [x] PackageImageArchitectureTests::PackageImageBackedRetborrowDynamicIndexTemplatesReturnElementAddresses
- [x] PackageImageArchitectureTests::PackageImageGenericTemplatesPublishAllBoundOperationFamilies
- [x] PackageImageArchitectureTests::PackageImageGenericTemplatesPreservePropertyAndListSwitchPatterns
- [x] PackageImageArchitectureTests::PackageImageConsumerLowersImportedGenericTypedBodyAfterSourceAndBodyTextAreRemoved
- [x] PackageImageArchitectureTests::PackageImagePreservesTraitConformanceMetadata
- [x] PackageImageArchitectureTests::PackageImagePreservesSystemCTypedInterfaceSurface

### PackageImageCallableValueTests  (10/10)
- [x] PackageImageCallableValueTests::PackageImagePreservesFunctionPointerTypesAndUnsafeFunctionFacts
- [x] PackageImageCallableValueTests::PackageImagePreservesDynTraitVtablePointerTypes
- [x] PackageImageCallableValueTests::PackageImagePreservesTargetSelectedPlatformAbiInTypedTemplateStructuralFacts
- [x] PackageImageCallableValueTests::PackageImagePreservesTargetSelectedPlatformAbiInTypedConstructorParameters
- [x] PackageImageCallableValueTests::PackageImagePreservesClosureTypes
- [x] PackageImageCallableValueTests::PackageImageBackedExplicitConstructorWithAliasCallableParameterLowersWithoutSource
- [x] PackageImageCallableValueTests::PackageImageBackedCallableAliasPreservesFiniteLawFunctionPointerKind
- [x] PackageImageCallableValueTests::PackageImageBackedQualifiedFunctionItemsPromoteToOrdinaryFunctionPointers
- [x] PackageImageCallableValueTests::PackageImageBackedFunctionItemsPromoteFromEachDeclaredFunctionKind
- [x] PackageImageCallableValueTests::PackageImageBackedOverloadedFunctionItemsPreserveDistinctAddressTakenFacts

### PackageImageCliToolingTests  (5/5)
- [x] PackageImageCliToolingTests::EmitPackageModeWritesPackageImageWithRequestedLibraryFileName
- [x] PackageImageCliToolingTests::EmitPackageModeWritesNativeDependencyMetadata
- [x] PackageImageCliToolingTests::EmitPackageModeStoresAbsoluteNativeSourcePathRelativeToPackageImage
- [x] PackageImageCliToolingTests::InspectPackageModePrintsReadableSummaryForValidPackageImage
- [x] PackageImageCliToolingTests::InspectPackageModeReportsValidationDiagnosticsForMalformedContent

### PackageImageGenericMemberSmokeTests  (1/1)
- [x] PackageImageGenericMemberSmokeTests::GenericInstanceMemberCallBindsThroughPackageImage

### PackageImageOptimizationSummaryWrapperIntegrationTests  (5/5)
- [x] PackageImageOptimizationSummaryWrapperIntegrationTests::ManifestBackedAggregateConstructionWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics
- [x] PackageImageOptimizationSummaryWrapperIntegrationTests::ManifestBackedLocalUpdateWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics
- [x] PackageImageOptimizationSummaryWrapperIntegrationTests::ManifestBackedTerminalSelectionWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics
- [x] PackageImageOptimizationSummaryWrapperIntegrationTests::ManifestBackedBinaryAndComparisonWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics
- [x] PackageImageOptimizationSummaryWrapperIntegrationTests::ManifestBackedConversionWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics

### PackageImageTryPropagationIntegrationTests  (4/4)
- [x] PackageImageTryPropagationIntegrationTests::PackageImageRoleAnnotatedEnumsPropagateWithTryAtRuntime
- [x] PackageImageTryPropagationIntegrationTests::PackageImageTypedOnlyManifestCarriesPropagationRolesAndFunnels
- [x] PackageImageTryPropagationIntegrationTests::PackageImageExportedGenericTemplateWithTrySpecializesDownstream
- [x] PackageImageTryPropagationIntegrationTests::PackageImagePublishesTryPropagationFactsForExportedTemplates

### PackageImageTypedArrayInitializerIntegrationTests  (1/1)
- [x] PackageImageTypedArrayInitializerIntegrationTests::ManifestBackedTypedArrayInitializerBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedArrayInitializerTests  (1/1)
- [x] PackageImageTypedArrayInitializerTests::ManifestBackedTypedArrayInitializerBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization

### PackageImageTypedAssignmentExpressionIntegrationTests  (1/1)
- [x] PackageImageTypedAssignmentExpressionIntegrationTests::ManifestBackedTypedAssignmentExpressionsCompileAndRunWithoutSyntheticSource

### PackageImageTypedComparisonChainIntegrationTests  (1/1)
- [x] PackageImageTypedComparisonChainIntegrationTests::ManifestBackedTypedComparisonChainBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedDiscardedExpressionIntegrationTests  (1/1)
- [x] PackageImageTypedDiscardedExpressionIntegrationTests::ManifestBackedTypedDiscardedExpressionBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedGroupedLocalDeclarationIntegrationTests  (1/1)
- [x] PackageImageTypedGroupedLocalDeclarationIntegrationTests::ManifestBackedTypedGroupedLocalDeclarationsCompileAndRunWithoutSyntheticSource

### PackageImageTypedNestedObjectCreationIntegrationTests  (1/1)
- [x] PackageImageTypedNestedObjectCreationIntegrationTests::ManifestBackedTypedNestedObjectCreationBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedObjectInitializerLocalDeclarationIntegrationTests  (1/1)
- [x] PackageImageTypedObjectInitializerLocalDeclarationIntegrationTests::ManifestBackedTypedObjectInitializerLocalDeclarationsCompileAndRunWithoutSyntheticSource

### PackageImageTypedPowerIntegrationTests  (1/1)
- [x] PackageImageTypedPowerIntegrationTests::ManifestBackedTypedPowerBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedRawPointerDereferenceIntegrationTests  (3/3)
- [x] PackageImageTypedRawPointerDereferenceIntegrationTests::ManifestBackedTypedRawPointerDereferenceBodiesCompileAndRunWithoutSyntheticSource
- [x] PackageImageTypedRawPointerDereferenceIntegrationTests::ManifestBackedTypedProjectedRawPointerDereferenceBodiesCompileAndRunWithoutSyntheticSource
- [x] PackageImageTypedRawPointerDereferenceIntegrationTests::ManifestBackedTypedAddressOfBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedSwitchPatternIntegrationTests  (4/4)
- [x] PackageImageTypedSwitchPatternIntegrationTests::ManifestBackedTypedNestedAndLiteralSwitchPatternsCompileAndRunWithoutSyntheticSource
- [x] PackageImageTypedSwitchPatternIntegrationTests::ManifestBackedTypedRangeSwitchPatternsCompileAndRunWithoutSyntheticSource
- [x] PackageImageTypedSwitchPatternIntegrationTests::ManifestBackedTypedAggregateWholeCapturePatternsCompileAndRunWithoutSyntheticSource
- [x] PackageImageTypedSwitchPatternIntegrationTests::ManifestBackedTypedEnumWholeCapturePatternsCompileAndRunWithoutSyntheticSource

### PackageImageTypedTerminalIfIntegrationTests  (1/1)
- [x] PackageImageTypedTerminalIfIntegrationTests::ManifestBackedTypedTerminalIfBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedTextFullViewIntegrationTests  (1/1)
- [x] PackageImageTypedTextFullViewIntegrationTests::ManifestBackedTypedFullViewTextBodiesCompileAndRunWithoutSyntheticSource

### PackageImageTypedUninitializedLocalDeclarationIntegrationTests  (1/1)
- [x] PackageImageTypedUninitializedLocalDeclarationIntegrationTests::ManifestBackedTypedUninitializedLocalDeclarationsCompileAndRunWithoutSyntheticSource

## CLI / project driver behavior  (31/31)

### CompilerCliTests  (18/18)
- [x] CompilerCliTests::CheckModeReportsSuccess
- [x] CompilerCliTests::PackageImageOutputRejectsExecutableEmission
- [x] CompilerCliTests::CheckModeRejectsPositiveSignedRangesByDefault
- [x] CompilerCliTests::StrictIntegerRangeFlagRejectsPositiveSignedRanges
- [x] CompilerCliTests::JsonDiagnosticFormatEmitsStableMachineReadableDocument
- [x] CompilerCliTests::TextDiagnosticsRenderSingleLineSourceSnippets
- [x] CompilerCliTests::TextDiagnosticsExpandTabsBeforeRenderingCarets
- [x] CompilerCliTests::TextDiagnosticsRenderMultilineSpansAcrossSourceLines
- [x] CompilerCliTests::TextDiagnosticsGroupCrossCodeNotesUnderTheirPrimaryDiagnostic
- [x] CompilerCliTests::LogLevelFilterCanSuppressInformationalPassLogs
- [x] CompilerCliTests::SuccessfulChecksPrintWarningsAndTextSummary
- [x] CompilerCliTests::TextDiagnosticsRenderSourceSnippetsForInfoNotesToo
- [x] CompilerCliTests::TextDiagnosticsDoNotRepeatTheSameOwnershipMoveError
- [x] CompilerCliTests::EmitMirModeReportsTypeErrorsInsteadOfLoweringWarningsForVoidCallsUsedAsValues
- [x] CompilerCliTests::EmitLlvmModeFailsWithTypeDiagnosticForVoidCallsUsedAsValues
- [x] CompilerCliTests::CheckModeResolvesSourceImportsFromConfiguredSearchPath
- [x] CompilerCliTests::CheckModeCanUseStarkPathAndCanDisableIt
- [x] CompilerCliTests::EmitExecutableModeReportsFriendlyMissingNativeLibraryDiagnostic

### MultiFileIntegrationTests  (3/3)
- [x] MultiFileIntegrationTests::SiblingModulesResolveThroughTheSourceSearchPath
- [x] MultiFileIntegrationTests::ExportedReExportsMakeTransitiveModulesAvailableToConsumingApps
- [x] MultiFileIntegrationTests::ModuleQualifiedEnumCasesResolveThroughImportedEnumTypes

### ProjectCliTests  (10/10)
- [x] ProjectCliTests::BuildBuildsCurrentProjectFromManifest
- [x] ProjectCliTests::BuildUsesStageLocalStdlibSearchDirectory
- [x] ProjectCliTests::BuildUsesRepoStdlibSourceTreeDiscovery
- [x] ProjectCliTests::BuildUsesInstalledBundledStdlibPackageWhenNoRepoStdlibExists
- [x] ProjectCliTests::BuildReportsStdlibDiscoveryPathsForMissingSystemImport
- [x] ProjectCliTests::ProjectBuildDoesNotUseStarkPathAsHiddenStdlibDiscovery
- [x] ProjectCliTests::BuildRejectsUnavailableCompilerStage
- [x] ProjectCliTests::BuildBuildsSolutionDefaultTargetAndPathDependencies
- [x] ProjectCliTests::TestRejectsFilterWhenNoGeneratedFactsExist
- [x] ProjectCliTests::TestRejectsExplicitMainWhenFactRunnerIsGenerated

## Example & benchmark sources  (21/21)

### ExampleSourceTests  (1/1)
- [x] ExampleSourceTests::RepresentativeExampleSourcesEmitLlvmWithoutFallbackLogs

### ExamplesCompileRunTests  (19/19)
- [x] ExamplesCompileRunTests::HelloExampleCompilesAndRunsWithStdlibPackage
- [x] ExamplesCompileRunTests::MultiModuleExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::BasicSyntaxExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::TypeSystemExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::ModulesExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::BorrowingExamplesCompileAndRun
- [x] ExamplesCompileRunTests::FfiExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::StandardLibraryExampleCompilesAndRunsWithStdlibPackage
- [x] ExamplesCompileRunTests::BuildYourOwnGitExamplesInitializeWriteCommitUpdateRefListInspectAndReportStatusWithStdlibPackage
- [x] ExamplesCompileRunTests::NeuralNetworkExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::SimpleDatabaseExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::BitTorrentTrackerResponseExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::BitTorrentHandshakeExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::BreakoutCoreExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::RaylibStarkModulesCheckWithoutNativeExecution
- [x] ExamplesCompileRunTests::DataModelExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::ArithmeticExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::ControlFlowExampleCompilesAndRuns
- [x] ExamplesCompileRunTests::StaticLibraryExampleBuildsAndRunsFromPackage

### StandardLibrarySourceTests  (1/1)
- [x] StandardLibrarySourceTests::StandardLibraryRootEmitsLlvmWithoutFallbackLogs

## Other portable compiler-behavior tests  (16/16)

### CompilerPipelineFullIntegrationTests  (16/16)
- [x] CompilerPipelineFullIntegrationTests::FunctionKindsAndModifiersDeriveExpectedEffectProfiles
- [x] CompilerPipelineFullIntegrationTests::AsmDeclarationsFlowIntoConservativeEffectsAndAbiLowering
- [x] CompilerPipelineFullIntegrationTests::LargeAggregateAbiUsesIndirectByValueParametersAndSRetReturns
- [x] CompilerPipelineFullIntegrationTests::PlainFnsRefineToStrongerEffectProfilesFromSemanticAnalysis
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldModulePrivateLawHelpersInferAlwaysInline
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldLawInliningRespectsExplicitHintsAndSkipsRecursiveHelpers
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldImportedModulePrivateLawHelpersInferAlwaysInline
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldRootLawCallersCanInlineImportedNonExportLawChains
- [x] CompilerPipelineFullIntegrationTests::ImportedLawEntrypointsWithMixedLawAndNonLawCallersStayInlineHintGlobally
- [x] CompilerPipelineFullIntegrationTests::ImportedModulesResolveThroughTheConfiguredResolver
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldOptimizationModelCapturesTraitAndDoctrineRules
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldOptimizationModelCapturesImportedTopLevelLawRules
- [x] CompilerPipelineFullIntegrationTests::ClosedWorldOptimizationModelKeepsOpaqueImportedLawsAtAbiBoundary
- [x] CompilerPipelineFullIntegrationTests::DoctrineMethodsUseModuleQualifiedSymbolsWhenEmittingLibraries
- [x] CompilerPipelineFullIntegrationTests::DeclaredFunctionSyntaxCollectorMatchesPublishedOverloadKeysFromTypedInterfaceSyntaxModels
- [x] CompilerPipelineFullIntegrationTests::PublishedOverloadKeysDriveResolvedNamesForPackageDeclarations

---

## Excluded — not ported (122)

_Tests that enforce a specific architecture or are tightly coupled to C# host internals. Listed for completeness; revisit if a self-hosted analog appears._

### BenchmarkRegressionScriptTests
- BenchmarkRegressionScriptTests::AddBenchmarkCRatiosUsesAverageRuntimeByBenchmark — host shell-script benchmark harness
- BenchmarkRegressionScriptTests::CheckBenchmarkRegressionsPassesWithinConfiguredBaselineThreshold — host shell-script benchmark harness
- BenchmarkRegressionScriptTests::CheckBenchmarkRegressionsFailsWhenBaselineRuntimeRegressesPastThreshold — host shell-script benchmark harness
- BenchmarkRegressionScriptTests::CheckBenchmarkRegressionsFailsWhenStarkToRustRatioExceedsThreshold — host shell-script benchmark harness

### BenchmarkSourceTests
- BenchmarkSourceTests::NetworkTcpBenchmarksUseImportedInlineSocketClonesThroughLocalHelpers — asserts Linux syscall numbers and iovec layout
- BenchmarkSourceTests::WindowsAllocatorBenchmarksUseHeapReAllocFastPath — Windows MSVC HeapReAlloc-specific IR
- BenchmarkSourceTests::StandardLibraryOptimizationBenchmarkGatesHaveExpectedSourceMatrix — audits benchmark file matrix on disk
- BenchmarkSourceTests::BenchmarkHarnessReportsCanonicalStarkOnly — host benchmark shell-script harness

### CompilerCliTests
- CompilerCliTests::HelpOutputGroupsOptionsByWorkflow — host cli help text
- CompilerCliTests::DefaultExecutableOutputPathMatchesInputNameAndAddsExeForWindowsTargets — host path-derivation helper, target-locked
- CompilerCliTests::DefaultExecutableOutputPathUsesModuleNameForStandardInput — host path-derivation helper
- CompilerCliTests::EmitLibraryCanRoutePackageImageAwayFromStaticLibrary — host manifest path layout
- CompilerCliTests::EmitObjectModeForwardsTargetCpuAndFeaturesToClang — arch target-cpu/feature clang args
- CompilerCliTests::EmitObjectModeForwardsRelocationModelAndCodeModelToClang — arch reloc/code-model clang args
- CompilerCliTests::EmitObjectModeKeepsLlvmPassesForImportedInlineBodyClones — host clang arg plumbing
- CompilerCliTests::EmitLibraryModeRebuildReplacesStaleArchiveMembers — host archive member inspection
- CompilerCliTests::EmitLibraryModeCompilesArchiveObjectsWithSectionGranularity — host clang arg plumbing
- CompilerCliTests::EmitLibraryModeSuppressesRanlibEmptyObjectWarningsFromSuccessfulArchive — host ranlib warning plumbing
- CompilerCliTests::EmitExecutableModeEnablesThinLtoForOptimizedBuildsWhenLldIsAvailable — host clang LTO arg plumbing
- CompilerCliTests::EmitExecutableModeForwardsMacOSSdkRootToClangForDarwinTargets — host macOS SDK clang plumbing
- CompilerCliTests::EmitExecutableModeAllowsSystemTextDependencyThinLto — host clang LTO arg plumbing
- CompilerCliTests::EmitExecutableModeAllowsSystemCollectionsDependencyThinLto — host clang LTO arg plumbing
- CompilerCliTests::EmitExecutableModeReportsMixedThinLtoDependencyPolicy — host clang LTO arg plumbing
- CompilerCliTests::EmitExecutableModeLinksManifestBackedAsmLibrariesWithoutSource — arch x86_64 asm syscall
- CompilerCliTests::EmitExecutableModeSupportsCustomLinkerLinkArgsAndSavedTemps — host linker arg plumbing
- CompilerCliTests::EmitExecutableModeForwardsRelocationModelToLinker — host linker arg plumbing
- CompilerCliTests::LinkOnlyAliasSupportsCustomLinkerLinkArgsAndSavedTemps — host linker arg plumbing
- CompilerCliTests::EmitLibraryModeSupportsCustomArchiverTool — host archiver arg plumbing

### CompilerPipelineFullIntegrationTests
- CompilerPipelineFullIntegrationTests::PipelineDoesNotPrintInformationalLogsToConsoleErrorByDefault — host console stderr StringWriter plumbing
- CompilerPipelineFullIntegrationTests::CrashedPassesProduceStructuredErrorLogs — asserts .NET exception type in logs
- CompilerPipelineFullIntegrationTests::PackageImageSourceBridgeFallsBackToExplicitSourceSurfaceSectionsWhenTypedInterfaceIsMissing — host manifest section fallback, no Stark source
- CompilerPipelineFullIntegrationTests::PackageImageSourceBridgePrefersExplicitSourceSurfaceOverLegacyFlatSurfaceFields — host manifest section precedence structure
- CompilerPipelineFullIntegrationTests::PackageImageSourceBridgePrefersExplicitCompilerSectionsOverLegacyFlatFields — host manifest section precedence structure
- CompilerPipelineFullIntegrationTests::PackageImageSourceBridgeOmitsPrivateSourceImportsWhenTypedInterfaceIsEnough — host manifest section bridge, no Stark source
- CompilerPipelineFullIntegrationTests::PackageImageFactsPreferExplicitCompilerSectionsOverLegacyFlatFields — host manifest section precedence structure
- CompilerPipelineFullIntegrationTests::PackageImageSyntaxModelCarriesPublishedOverloadKeysFromSourceSurface — hand-built host manifest, no Stark source
- CompilerPipelineFullIntegrationTests::PackageImageSyntaxModelCarriesPublishedOverloadKeysFromLegacyFlatSurfaceFieldsWhenExplicitSourceSurfaceIsMissing — host manifest legacy-field fallback structure
- CompilerPipelineFullIntegrationTests::PackageImageSyntaxModelCarriesPublishedOverloadKeysFromTypedInterfaceWhenSourceSurfaceFunctionEntriesAreMissing — host manifest section fallback structure
- CompilerPipelineFullIntegrationTests::PackageImageSyntaxModelCarriesFunctionModifiersFromTypedInterfaceWhenCompilerFactsAreMissing — host manifest section fallback structure

### ExamplesCompileRunTests
- ExamplesCompileRunTests::BreakoutRaylibBuildsThroughPackageOwnedNativeMetadataWithoutGraphicalExecution — host unix linker flag plumbing

### HostCompilerTestRunnerTests
- HostCompilerTestRunnerTests::HostTestInspectReturnsStructuredArtifactsDiagnosticsLogsAndExecutions — host test-runner protocol/JSON tooling
- HostCompilerTestRunnerTests::HostTestInspectAcceptsBatchRequestsAndSourcePaths — host test-runner protocol/JSON tooling
- HostCompilerTestRunnerTests::HostTestInspectExportsArtifactsAndDiagnosticsWithoutInliningArtifactText — host test-runner protocol/JSON tooling
- HostCompilerTestRunnerTests::HostTestServerProcessesMultipleJsonLinesAndShutdown — host test-server protocol/JSON tooling

### IntegerArithmeticFoldNativeCodegenTests
- IntegerArithmeticFoldNativeCodegenTests::RepeatedAddFoldLetsX64BackendSelectLea — x86_64 lea instruction-selection disassembly

### LlvmIrEmissionTests
- LlvmIrEmissionTests::PointerAndViewGlobalsEmitTargetAwareAlignmentOnX86_64 — x86_64-specific alignment
- LlvmIrEmissionTests::I386GlobalsUseTargetAwareScalarAndViewAlignment — i386-specific alignment
- LlvmIrEmissionTests::SystemMathHardwareBuiltinsEmitInlineAsmForX86_64 — x86 register inline asm
- LlvmIrEmissionTests::SystemMathHardwareBuiltinsEmitInlineAsmForAArch64 — aarch64 register inline asm
- LlvmIrEmissionTests::ExplicitFfiAbiModifiersEmitLlvmCallingConventions — win64 ABI calling convention
- LlvmIrEmissionTests::PlatformSelectedFfiAbiModifiersResolvePerTarget — x86_64 sysv ABI convention
- LlvmIrEmissionTests::SystemCPrimitiveAliasesLowerToTargetLlvmTypes — target-triple-locked C type sizes
- LlvmIrEmissionTests::ExplicitFfiFunctionPointerAbiEmitsIndirectCallConvention — win64 ABI calling convention
- LlvmIrEmissionTests::UnsafeFfiFunctionItemsPromoteToUnsafeFfiFunctionPointerArguments — win64 ABI calling convention
- LlvmIrEmissionTests::RuntimeAllocatorUsesWindowsHeapApisForWindowsTargets — Windows-target HeapAlloc APIs
- LlvmIrEmissionTests::RuntimeAllocatorUsesMmap2ForThirtyTwoBitLinuxTargets — 32-bit syscall numbers/int 0x80
- LlvmIrEmissionTests::SystemMemoryAllocatorBoundsChecksRuntimeSizesForThirtyTwoBitTargets — 32-bit target bitwidth-locked
- LlvmIrEmissionTests::I386ViewTypedLocalAddressesUseTargetAwareAlignment — i386-specific alignment
- LlvmIrEmissionTests::FfiCStructLayoutArgumentsUseX64SysVCAbiCarriers — x86_64 SysV ABI struct carriers
- LlvmIrEmissionTests::RootAsmFunctionsEmitInlineAsmBodiesForTheSyscallSubset — x86_64 register/syscall asm
- LlvmIrEmissionTests::RootAsmFunctionsEmitFloatingPointRegisterBindings — x86_64 xmm register asm

### MidLevelIrArtifactValidationTests
- MidLevelIrArtifactValidationTests::ValueCallRValuesRejectVoidResultTypes — host C# IR constructor validation, no Stark source
- MidLevelIrArtifactValidationTests::SsaValueCallRValuesRejectVoidResultTypes — host C# IR constructor validation, no Stark source
- MidLevelIrArtifactValidationTests::IndexRValuesRejectMismatchedOperationFamilies — host C# IR constructor validation, no Stark source
- MidLevelIrArtifactValidationTests::IndexRValuesRejectMismatchedResultAndValueTypes — host C# IR constructor validation, no Stark source
- MidLevelIrArtifactValidationTests::ViewIndexRValuesRejectMismatchedComponentTypes — host C# IR constructor validation, no Stark source
- MidLevelIrArtifactValidationTests::ObjectConstructionOperandsRequireResolvedConstructorFacts — host C# IR constructor validation, no Stark source
- MidLevelIrArtifactValidationTests::EnumConstructionOperandsRequireVariantPayloadFacts — host C# IR constructor validation, no Stark source

### PackageImageArchitectureTests
- PackageImageArchitectureTests::PackageImagePreservesBackendOpaqueModuleBoundary — host manifest JSON + loader, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesThreadSafetyLawSurfaceAcrossTypedInterfaceSourceBridgeAndFacts — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesFineGrainedBackendOpaqueBoundaries — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::NonOpaqueSourceDependencyCanParticipateInLto — host CompilerCli LTO decision
- PackageImageArchitectureTests::SystemCollectionsSourceUsesDefaultBackendOptimizationWithoutModuleNameGate — host CompilerCli LTO decision
- PackageImageArchitectureTests::PackageImagePreservesIndependentLoopContractsInTypedTemplateBodies — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesFfiVarargsFacts — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesConstDefaultNonOverlapAndExplicitRelationQualifiers — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesUnsignedIntegerFacts — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesStructLayoutMetadataAndConcreteFieldOffsets — package-image binary byte layout
- PackageImageArchitectureTests::PackageImageBuilderPublishesTypedInterfaceImportsAsStructuredDependencySurface — host manifest dependency-surface structure
- PackageImageArchitectureTests::PackageImageBuilderPublishesInternalDependencyImportsNeededByImportedBodies — host manifest dependency-surface structure
- PackageImageArchitectureTests::PackageImageBuilderPublishesLinkageMetadataForModuleObjectSelection — host link-metadata structure
- PackageImageArchitectureTests::PackageImagePreservesConstNumericStorageWithoutReconstructingScalarRanges — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesNamedAggregateConstantInitializers — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::PackageImagePreservesEnumConstantInitializers — host manifest/loader object-graph, no consumer compile
- PackageImageArchitectureTests::StructuredPackageImageSourceIgnoresCorruptedBodyTextWhenTypedBodyFactsExist — host loader source reconstruction
- PackageImageArchitectureTests::PackageImageLoaderPrefersTypedInterfaceImportsOverExplicitSourceSurfaceImports — host loader import precedence
- PackageImageArchitectureTests::PackageImageLoaderFallsBackToLegacyFlatImportsWhenTypedInterfaceImportsAreMissing — host loader import precedence
- PackageImageArchitectureTests::PackageImageLoaderLegacyFlatImportsDoNotHideLegacyReExports — host loader import precedence

### PackageImageCliToolingTests
- PackageImageCliToolingTests::EmitExecutableCopiesWindowsPackageRuntimeDllsBesideOutput — Windows-specific DLL staging, arch-coupled

### PackageImageLoaderDiagnosticsTests
- PackageImageLoaderDiagnosticsTests::TryParseManifestJsonReportsMalformedJson — host manifest JSON parse structure
- PackageImageLoaderDiagnosticsTests::ValidateManifestReportsDuplicateModuleAndMissingRootModuleEntry — host manifest structural invariants
- PackageImageLoaderDiagnosticsTests::ValidateManifestReportsMissingRichSectionsForLegacyOnlyModule — host manifest structural invariants
- PackageImageLoaderDiagnosticsTests::ValidateManifestAcceptsRichReExportOnlyModuleWithEmptyFacts — host manifest structural invariants

### ProjectCliTests
- ProjectCliTests::BuildHelpUsesProjectCommandDriver — host cli help text
- ProjectCliTests::TestHelpUsesProjectCommandDriver — host cli help text
- ProjectCliTests::CleanHelpUsesProjectCommandDriver — host cli help text
- ProjectCliTests::CleanDeletesSelectedStageByDefault — host filesystem dir cleanup, no Stark
- ProjectCliTests::CleanDeletesTargetAndProfileScopes — host filesystem dir cleanup, no Stark
- ProjectCliTests::CleanDeletesDiagnosticsAndArtifactsScopes — host filesystem dir cleanup, no Stark

### SsaEmitterCoverageMatrixTests
- SsaEmitterCoverageMatrixTests::EveryConcreteSsaNodeHasEmitterCoverageOrValidationRejection — host-source audit via reflection
- SsaEmitterCoverageMatrixTests::PositiveLlvmCoverageMatrixKeepsBackendAssumptionTestsVisible — host-source audit via reflection

### StarkTestRunnerGeneratorTests
- StarkTestRunnerGeneratorTests::PlatformGatesSkipNonMatchingFactsInGeneratedRunner — host test-runner generator
- StarkTestRunnerGeneratorTests::PlatformGatesSkipNonMatchingTheoryRowsInGeneratedRunner — host test-runner generator
- StarkTestRunnerGeneratorTests::PlatformGatesAcceptExactTargetTriplesAndArchitectureAliases — host test-runner generator
- StarkTestRunnerGeneratorTests::PlatformGatesReportMalformedSelectors — host test-runner generator
- StarkTestRunnerGeneratorTests::FiltersCanSelectPlatformSkippedFacts — host test-runner generator
- StarkTestRunnerGeneratorTests::CollectionsGroupFactsByFirstCollectionOccurrence — host test-runner generator
- StarkTestRunnerGeneratorTests::TheoriesExpandInlineDataRowsInGeneratedRunner — host test-runner generator
- StarkTestRunnerGeneratorTests::FiltersCanSelectTheoryRowsByGeneratedDisplayName — host test-runner generator
- StarkTestRunnerGeneratorTests::TheoriesExpandMemberDataRowsInGeneratedRunner — host test-runner generator
- StarkTestRunnerGeneratorTests::FiltersCanSelectMemberDataRowsByGeneratedDisplayName — host test-runner generator
- StarkTestRunnerGeneratorTests::CollectionsReportMalformedAndConflictingAttributes — host test-runner generator
- StarkTestRunnerGeneratorTests::CollectionsUnionAcrossModuleTypeAndMemberAndAcceptVariadicNames — host test-runner generator
- StarkTestRunnerGeneratorTests::TheoriesReportMalformedInlineData — host test-runner generator
- StarkTestRunnerGeneratorTests::TheoriesReportMalformedMemberData — host test-runner generator
- StarkTestRunnerGeneratorTests::DuplicateExpandedTheoryRowsReportGeneratedEntryCollision — host test-runner generator
- StarkTestRunnerGeneratorTests::CollectionAttributesRequireFactOnCallables — host test-runner generator

### TypeTypingDiagnosticsTests
- TypeTypingDiagnosticsTests::FfiAbiModifiersRejectUnsupportedTargetsDuringCompilation — target-specific ABI (stdcall) support check
- TypeTypingDiagnosticsTests::AsmFunctionsRejectUnsupportedParameterAndReturnTypes — x86_64 asm registers, target-locked
- TypeTypingDiagnosticsTests::AsmFunctionsAcceptFloatingPointParametersAndReturns — x86_64 asm registers, target-locked
- TypeTypingDiagnosticsTests::AsmFunctionsAcceptAArch64FloatingPointRegisters — aarch64 asm registers, target-locked
- TypeTypingDiagnosticsTests::AsmFunctionsRejectRegisterClassesThatDoNotMatchValueKinds — x86_64 asm register classes, target-locked

