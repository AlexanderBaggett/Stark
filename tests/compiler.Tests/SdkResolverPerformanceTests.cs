using System.Diagnostics;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SdkResolverPerformanceTests
{
    [Fact]
    public void IndexedStartupAndSelectionBenchmarkDoesNotScanOrAllocatePerLookup()
    {
        const int packageCount = 256;
        const int modulesPerPackage = 16;
        const int lookupRounds = 128;
        var sdkRoot = Directory.CreateTempSubdirectory("stark-sdk-index-benchmark-");
        try
        {
            var packages = Enumerable.Range(0, packageCount)
                .Select(index => CreatePackage($"Package{index:D3}"))
                .ToArray();
            var modules = packages
                .SelectMany(package => Enumerable.Range(0, modulesPerPackage)
                    .Select(index => new SdkModuleOwnership(
                        $"Vendor.Benchmark.{package.Id}.Module{index:D2}",
                        package.Id)))
                .ToArray();
            var moduleNames = modules.Select(static module => module.ModuleName).ToArray();

            var startup = Stopwatch.StartNew();
            var index = new SdkPackageIndex(sdkRoot.FullName, packages, modules);
            var manifest = new SdkManifest(
                SchemaVersion: 1,
                Kind: SdkDistributionKind.Release,
                SdkVersion: "benchmark",
                CompilerCompatibility: SdkCompilerCompatibility.SupportedLine,
                PackageFormatVersion: (int)PackageImageBinaryFormat.CurrentFormatVersion,
                Target: CreateTarget(),
                Modules: modules,
                Packages: packages,
                DevelopmentSourceRoots: []);
            var resolution = SdkPackageModuleResolver.CreateLazy(new SdkManifestLoadResult(
                sdkRoot.FullName,
                Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName),
                manifest,
                index,
                []));
            startup.Stop();

            var resolver = Assert.IsType<SdkPackageModuleResolver>(resolution.Resolver);
            Assert.Equal(0, resolver.MaterializedPackageCount);
            Assert.True(index.TryGetPackageForModule(moduleNames[0], out _, out _));

            // Warm the lookup path before measuring allocations.
            foreach (var moduleName in moduleNames)
            {
                Assert.True(index.TryGetPackageForModule(moduleName, out _, out _));
            }

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var selection = Stopwatch.StartNew();
            var selected = 0;
            for (var round = 0; round < lookupRounds; round++)
            {
                foreach (var moduleName in moduleNames)
                {
                    if (index.TryGetPackageForModule(moduleName, out _, out _))
                    {
                        selected++;
                    }
                }
            }

            selection.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(moduleNames.Length * lookupRounds, selected);
            Assert.Equal(0, resolver.MaterializedPackageCount);
            Assert.True(startup.Elapsed < TimeSpan.FromSeconds(5), $"Indexed SDK startup took {startup.Elapsed}.");
            Assert.True(selection.Elapsed < TimeSpan.FromSeconds(5), $"Indexed SDK selection took {selection.Elapsed}.");
            Assert.InRange(allocated, 0, 4096);
        }
        finally
        {
            try
            {
                sdkRoot.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void IndexedStartupAndSelectionOutrunRetiredDirectoryScanBaseline()
    {
        const int packageCount = 256;
        const int modulesPerPackage = 16;
        const int sampleCount = 5;
        var fixtureRoot = Directory.CreateTempSubdirectory("stark-sdk-scan-comparison-");
        try
        {
            var scanRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot.FullName, "scan"));
            var warmupRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot.FullName, "warmup"));
            var packages = Enumerable.Range(0, packageCount)
                .Select(index => CreatePackage($"Package{index:D3}"))
                .ToArray();
            var modules = packages
                .SelectMany(package => Enumerable.Range(0, modulesPerPackage)
                    .Select(index => new SdkModuleOwnership(
                        $"Vendor.Benchmark.{package.Id}.Module{index:D2}",
                        package.Id)))
                .ToArray();

            foreach (var package in packages)
            {
                WriteScannedPackage(
                    scanRoot.FullName,
                    package,
                    modules
                        .Where(module => string.Equals(module.PackageId, package.Id, StringComparison.Ordinal))
                        .Select(static module => module.ModuleName)
                        .ToArray());
            }

            // Keep one-package warmup work outside the measured directory. The
            // comparison is then about indexed construction/selection versus
            // the retired recursive package discovery and decode path, not JIT.
            var warmupPackage = CreatePackage("Warmup");
            const string warmupModule = "Vendor.Benchmark.Warmup.Module";
            WriteScannedPackage(warmupRoot.FullName, warmupPackage, [warmupModule]);
            var warmupIndex = new SdkPackageIndex(
                fixtureRoot.FullName,
                [warmupPackage],
                [new SdkModuleOwnership(warmupModule, warmupPackage.Id)]);
            Assert.True(warmupIndex.TryGetPackageForModule(warmupModule, out _, out _));
            Assert.True(new FileSystemModuleResolver(warmupRoot.FullName)
                .TryResolveModule(warmupModule, out _));

            var targetModule = modules[^1].ModuleName;
            var indexedSamples = new long[sampleCount];
            var scannedSamples = new long[sampleCount];
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var indexedStart = Stopwatch.GetTimestamp();
                var index = new SdkPackageIndex(fixtureRoot.FullName, packages, modules);
                Assert.True(index.TryGetPackageForModule(targetModule, out var indexedPackage, out _));
                indexedSamples[sample] = Stopwatch.GetTimestamp() - indexedStart;
                Assert.Equal(packages[^1].Id, indexedPackage.Id);

                var scannedStart = Stopwatch.GetTimestamp();
                var scannedResolver = new FileSystemModuleResolver(scanRoot.FullName);
                Assert.True(scannedResolver.TryResolveModule(targetModule, out var scannedModule));
                scannedSamples[sample] = Stopwatch.GetTimestamp() - scannedStart;
                Assert.NotNull(scannedModule.ManifestPath);
            }

            Array.Sort(indexedSamples);
            Array.Sort(scannedSamples);
            var indexedMedian = indexedSamples[sampleCount / 2];
            var scannedMedian = scannedSamples[sampleCount / 2];

            Assert.True(
                indexedMedian * 2 < scannedMedian,
                $"Indexed SDK startup/selection median ({indexedMedian} stopwatch ticks) "
                + $"must remain at least twice as fast as the retired directory scan ({scannedMedian} ticks).");
        }
        finally
        {
            try
            {
                fixtureRoot.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static SdkPackageDescriptor CreatePackage(string id) =>
        new(
            id,
            Version: "benchmark",
            Profile: "release",
            ImagePath: $"packages/{id}.starkpkg",
            LibraryPath: $"packages/lib{id}.a",
            ApiHash: null,
            ContentHash: null,
            ImageSha256: null,
            LibrarySha256: null,
            Dependencies: [],
            Native: new SdkNativePackageDescriptor([], [], [], [], [], [], [], []));

    private static void WriteScannedPackage(
        string directory,
        SdkPackageDescriptor package,
        IReadOnlyList<string> moduleNames)
    {
        var packageDirectory = Directory.CreateDirectory(Path.Combine(directory, package.Id));
        var manifest = new
        {
            RootModule = moduleNames[0],
            LibraryFileName = $"lib{package.Id}.a",
            Modules = moduleNames.Select(moduleName => new
            {
                ModuleName = moduleName,
                ReExports = Array.Empty<string>(),
                Functions = Array.Empty<object>(),
                Types = Array.Empty<object>(),
                Globals = Array.Empty<object>()
            })
        };
        File.WriteAllText(
            Path.Combine(packageDirectory.FullName, $"lib{package.Id}.starkpkg.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static SdkTargetDescriptor CreateTarget() =>
        new(
            Id: "benchmark",
            LlvmTriple: "x86_64-unknown-linux-gnu",
            Architecture: "x86_64",
            OperatingSystem: "linux",
            Abi: "gnu",
            PointerBitWidth: 64,
            Endianness: SdkEndianness.Little,
            DataLayout: "e-p:64:64",
            BaselineCpu: "generic",
            BaselineFeatures: [],
            RelocationModel: "pic",
            CodeModel: "small",
            CDataModel: "lp64",
            MinimumOperatingSystemVersion: null);
}
