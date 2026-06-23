using Stark.Compiler;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace compiler.IntegrationTests;

public sealed partial class VendorBindingAuditTests
{
    private static readonly VendorBinding[] Bindings =
    [
        new(
            "LZ4",
            "vendor/src/Vendor/LZ4.stark",
            "vendor/build-lz4-package.sh",
            "vendor/dist/libVendorLZ4.starkpkg",
            "examples/lz4/LZ4RoundTrip.stark",
            [],
            ["liblz4"]),
        new(
            "Zlib",
            "vendor/src/Vendor/Zlib.stark",
            "vendor/build-zlib-package.sh",
            "vendor/dist/libVendorZlib.starkpkg",
            "examples/zlib/ZlibRoundTrip.stark",
            ["ZlibStreamBinding.c"],
            ["zlib"]),
        new(
            "Curl",
            "vendor/src/Vendor/Curl.stark",
            "vendor/build-curl-package.sh",
            "vendor/dist/libVendorCurl.starkpkg",
            "examples/curl/CurlGet.stark",
            ["CurlEasyBinding.c"],
            ["libcurl"]),
        new(
            "STB.Image",
            "vendor/src/Vendor/STB/Image.stark",
            "vendor/build-stb-image-package.sh",
            "vendor/dist/libVendorSTBImage.starkpkg",
            "examples/stb-image/StbImageResize.stark",
            ["StbImageImplementation.c"],
            []),
        new(
            "STB.Truetype",
            "vendor/src/Vendor/STB/Truetype.stark",
            "vendor/build-stb-truetype-package.sh",
            "vendor/dist/libVendorSTBTruetype.starkpkg",
            "examples/stb-truetype/StbTruetypeGlyphAtlas.stark",
            ["StbTruetypeImplementation.c"],
            []),
        new(
            "Miniaudio",
            "vendor/src/Vendor/Miniaudio.stark",
            "vendor/build-miniaudio-package.sh",
            "vendor/dist/libVendorMiniaudio.starkpkg",
            "examples/miniaudio/MiniaudioDecode.stark",
            ["MiniaudioImplementation.c"],
            []),
        new(
            "Cgltf",
            "vendor/src/Vendor/Cgltf.stark",
            "vendor/build-cgltf-package.sh",
            "vendor/dist/libVendorCgltf.starkpkg",
            "examples/cgltf/CgltfAssetSummary.stark",
            ["CgltfImplementation.c"],
            []),
        new(
            "GLFW",
            "vendor/src/Vendor/GLFW.stark",
            "vendor/build-glfw-package.sh",
            "vendor/dist/libVendorGLFW.starkpkg",
            "examples/glfw/GlfwHiddenWindow.stark",
            ["GlfwEventBridge.c"],
            ["glfw3"]),
        new(
            "KbTextShape",
            "vendor/src/Vendor/KbTextShape.stark",
            "vendor/build-kb-text-shape-package.sh",
            "vendor/dist/libVendorKbTextShape.starkpkg",
            "examples/kb-text-shape/TextShapeGlyphs.stark",
            ["KbTextShapeBinding.c"],
            ["harfbuzz", "icu-uc", "icu-i18n"]),
        new(
            "SDL3",
            "vendor/src/Vendor/SDL3.stark",
            "vendor/build-sdl3-package.sh",
            "vendor/dist/libVendorSDL3.starkpkg",
            "examples/sdl3/Sdl3WindowAudio.stark",
            ["Sdl3Binding.c"],
            ["sdl3"]),
        new(
            "Vulkan",
            "vendor/src/Vendor/Vulkan.stark",
            "vendor/build-vulkan-package.sh",
            "vendor/dist/libVendorVulkan.starkpkg",
            "examples/vulkan/VulkanInfo.stark",
            [],
            ["vulkan"]),
        new(
            "SQLite",
            "vendor/src/Vendor/SQLite.stark",
            "vendor/build-sqlite-package.sh",
            "vendor/dist/libVendorSQLite.starkpkg",
            "examples/sqlite/SQLiteInMemoryQueries.stark",
            ["SQLiteTextBinding.c"],
            ["sqlite3"]),
        new(
            "Raylib",
            "vendor/src/Vendor/Raylib.stark",
            "vendor/build-raylib-package.sh",
            "vendor/dist/libVendorRaylib.starkpkg",
            "examples/breakout/BreakoutRaylib.stark",
            [],
            ["raylib"])
    ];

    [Fact]
    public void NonLegacyVendorBindingsKeepRawAndUnsafeSurfaceInternal()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorSourceRoot = Path.Combine(repositoryRoot, "vendor", "src", "Vendor");
        var failures = new List<string>();

        foreach (var sourcePath in Directory.EnumerateFiles(vendorSourceRoot, "*.stark", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
            if (relativePath.StartsWith("vendor/src/Vendor/Raylib", StringComparison.Ordinal))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(sourcePath))
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("public unsafe", StringComparison.Ordinal)
                    || PublicRawPointerRegex().IsMatch(trimmed))
                {
                    failures.Add($"{relativePath}:{lineNumber}: {trimmed}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Non-Raylib Vendor bindings must keep raw pointers and unsafe native entry points behind safe Stark wrappers."
            + Environment.NewLine
            + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void NativeAdapterSourcesAreReferencedByBuildScriptsAndDocumented()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorRoot = Path.Combine(repositoryRoot, "vendor");
        var readmeText = File.ReadAllText(Path.Combine(vendorRoot, "README.md"));
        var buildScriptText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(vendorRoot, "build-*-package.sh")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        foreach (var nativeSource in Directory.EnumerateFiles(vendorRoot, "*.c")
                     .Select(Path.GetFileName)
                     .OrderBy(static name => name, StringComparer.Ordinal))
        {
            Assert.NotNull(nativeSource);
            Assert.Contains(nativeSource!, buildScriptText, StringComparison.Ordinal);
            Assert.Contains(nativeSource!, readmeText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildScriptsCarryPackageOwnedNativeMetadata()
    {
        var repositoryRoot = FindRepositoryRoot();

        foreach (var binding in Bindings)
        {
            var buildScriptPath = Path.Combine(repositoryRoot, binding.BuildScriptRelativePath);
            Assert.True(File.Exists(buildScriptPath), $"{binding.Name} is missing {binding.BuildScriptRelativePath}.");

            var text = File.ReadAllText(buildScriptPath);
            Assert.Contains("--emit-lib", text, StringComparison.Ordinal);
            Assert.Contains("-I \"${script_dir}/src\"", text, StringComparison.Ordinal);
            Assert.Contains("-I \"${repo_root}/stdlib/src\"", text, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileNameWithoutExtension(binding.PackageRelativePath) + ".a", text, StringComparison.Ordinal);

            foreach (var nativeSource in binding.NativeSources)
            {
                Assert.Contains("--native-source", text, StringComparison.Ordinal);
                Assert.Contains(nativeSource, text, StringComparison.Ordinal);
            }

            Assert.True(
                text.Contains("--native-source", StringComparison.Ordinal)
                || text.Contains("--native-pkg-config", StringComparison.Ordinal)
                || text.Contains("--native-library", StringComparison.Ordinal)
                || text.Contains("--native-link-arg", StringComparison.Ordinal),
                $"{binding.Name} build script does not add package-owned native metadata.");
        }
    }

    [Fact]
    public async Task BuiltPackageImagesCarryNativeMetadata()
    {
        var repositoryRoot = FindRepositoryRoot();

        foreach (var binding in Bindings)
        {
            var packagePath = Path.Combine(repositoryRoot, binding.PackageRelativePath);
            if (!File.Exists(packagePath))
            {
                Assert.False(
                    await RequiredPkgConfigPackagesExistAsync(binding.RequiredPkgConfigPackages),
                    $"{binding.Name} can resolve its native dependency but {binding.PackageRelativePath} has not been built.");
                continue;
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("native dependencies:", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                "native dependencies: sources=0, includes=0, library-dirs=0, libraries=0, pkg-config=0, link-args=0",
                stdout.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task VendorExamplesCheckThroughBuiltPackageImages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorDistRoot = Path.Combine(repositoryRoot, "vendor", "dist");
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");

        foreach (var binding in Bindings)
        {
            var packagePath = Path.Combine(repositoryRoot, binding.PackageRelativePath);
            if (!File.Exists(packagePath))
            {
                Assert.False(
                    await RequiredPkgConfigPackagesExistAsync(binding.RequiredPkgConfigPackages),
                    $"{binding.Name} can resolve its native dependency but {binding.PackageRelativePath} has not been built.");
                continue;
            }

            var sourcePath = Path.Combine(repositoryRoot, binding.ExampleRelativePath);
            Assert.True(File.Exists(sourcePath), $"{binding.Name} is missing example {binding.ExampleRelativePath}.");

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--check", "-I", vendorDistRoot, "-I", stdlibRoot],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
        }
    }

    private static async Task<bool> RequiredPkgConfigPackagesExistAsync(IReadOnlyList<string> packageNames)
    {
        if (packageNames.Count == 0)
        {
            return true;
        }

        foreach (var packageName in packageNames)
        {
            if (!await PkgConfigPackageExistsAsync(packageName))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> PkgConfigPackageExistsAsync(string packageName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pkg-config",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--exists", packageName }
            });

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for vendor binding audit tests.");
    }

    [GeneratedRegex(@"\bpublic\b.*\braw(?:mut)?ptr\s*<", RegexOptions.CultureInvariant)]
    private static partial Regex PublicRawPointerRegex();

    private sealed record VendorBinding(
        string Name,
        string RootSourceRelativePath,
        string BuildScriptRelativePath,
        string PackageRelativePath,
        string ExampleRelativePath,
        IReadOnlyList<string> NativeSources,
        IReadOnlyList<string> RequiredPkgConfigPackages);
}
