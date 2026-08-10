using System.Security.Cryptography;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class GlfwReleaseInputScriptTests
{
    private const string Recipe = "scripts/prepare-glfw-vendor-release-input.ps1";
    private const string SourceSha256 = "b5ec004b2712fd08e8861dc271428f048775200a2df719ccf575143ba749a3e9";
    private const string MacBinarySha256 = "6775085bdae60312a3002bff2e39779a83bc72a7e1c810bd806fddb00cb35fd0";
    private const string LicenseSha256 = "149704059b5d0bf551637e50042dd4de9c2cae921021f6636298911e3a5f9462";

    [Fact]
    public void CatalogPinsOfficialInputsBuildPolicyLicenseAndEnabledTargets()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));
        var package = catalog.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Single(static item => item.GetProperty("id").GetString() == "Vendor.GLFW");

        Assert.Equal("3.4", package.GetProperty("version").GetString());
        Assert.Equal("tag:3.4", package.GetProperty("sourceIdentity").GetString());
        Assert.Equal(Recipe, package.GetProperty("buildRecipe").GetString());

        var source = package.GetProperty("sourceInput");
        Assert.Equal("glfw-3.4.zip", source.GetProperty("name").GetString());
        Assert.Equal("https://github.com/glfw/glfw/releases/download/3.4/glfw-3.4.zip", source.GetProperty("url").GetString());
        Assert.Equal(SourceSha256, source.GetProperty("sha256").GetString());
        Assert.Equal(1_653_725, source.GetProperty("size").GetInt64());
        Assert.Equal("glfw-3.4", source.GetProperty("stripPrefix").GetString());

        var binary = Assert.Single(package.GetProperty("binaryInputs").EnumerateArray());
        Assert.Equal("macos-arm64", binary.GetProperty("target").GetString());
        Assert.Equal("glfw-3.4.bin.MACOS.zip", binary.GetProperty("name").GetString());
        Assert.Equal(MacBinarySha256, binary.GetProperty("sha256").GetString());
        Assert.Equal(1_351_252, binary.GetProperty("size").GetInt64());
        Assert.Equal("glfw-3.4.bin.MACOS", binary.GetProperty("archiveRoot").GetString());

        var options = package.GetProperty("sourceBuildOptions");
        Assert.Equal("static", options.GetProperty("libraryType").GetString());
        Assert.Equal("release", options.GetProperty("configuration").GetString());
        Assert.Equal("O3", options.GetProperty("optimization").GetString());
        Assert.Equal("thin", options.GetProperty("lto").GetString());
        Assert.True(options.GetProperty("deterministicArchive").GetBoolean());
        Assert.Equal("bundled-llvm", options.GetProperty("toolchain").GetString());
        Assert.Equal("x11-only", options.GetProperty("linuxWindowSystem").GetString());
        Assert.False(options.GetProperty("wayland").GetBoolean());
        Assert.Equal("compiled-into-native-archive", options.GetProperty("eventBridge").GetString());
        Assert.False(options.GetProperty("perApplicationNativeSourceCompilation").GetBoolean());
        Assert.Contains(
            "compile-time only",
            Assert.Single(options.GetProperty("linuxBuildHostPrerequisites").EnumerateArray()).GetString(),
            StringComparison.Ordinal);

        var support = package.GetProperty("targetSupport");
        Assert.Equal("required-source-build", support.GetProperty("linux-x64").GetString());
        Assert.Equal("required-source-build", support.GetProperty("windows-x64").GetString());
        Assert.Equal("required-binary", support.GetProperty("macos-arm64").GetString());

        var links = package.GetProperty("systemLinkFacts");
        Assert.Equal(["pthread", "dl", "rt", "m"], Strings(links.GetProperty("linux")));
        Assert.Equal(["gdi32", "user32", "shell32"], Strings(links.GetProperty("windows")));
        Assert.Equal(["Cocoa", "IOKit", "CoreFoundation"], Strings(links.GetProperty("macos")));
        Assert.DoesNotContain("X11", Strings(links.GetProperty("linux")), StringComparer.Ordinal);

        var evidence = Assert.Single(package.GetProperty("licenseEvidencePaths").EnumerateArray()).GetString()!;
        Assert.Equal(
            LicenseSha256,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, evidence)))));
    }

    [Fact]
    public void ContributorUsesChecksumAddressedTransactionalInputsAndOnlyBundledTools()
    {
        var script = ReadScript();

        Assert.Contains("[ValidateSet(\"linux-x64\", \"linux-arm64\", \"windows-x64\", \"windows-arm64\", \"macos-x64\", \"macos-arm64\")]", script, StringComparison.Ordinal);
        Assert.Contains("$digestCacheRoot = Join-Path $CacheRoot $sha256", script, StringComparison.Ordinal);
        Assert.Contains(".download-$([Guid]::NewGuid().ToString('N'))", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Sha256 -Path $downloadPath", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $downloadPath -Destination $archivePath", script, StringComparison.Ordinal);
        Assert.Contains("Get-ToolPath -ToolchainRoot $toolchainRoot -Name \"clang\"", script, StringComparison.Ordinal);
        Assert.Contains("Get-ToolPath -ToolchainRoot $toolchainRoot -Name \"llvm-ar\"", script, StringComparison.Ordinal);
        Assert.Contains("Get-ToolPath -ToolchainRoot $toolchainRoot -Name \"llvm-ranlib\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--target=$TargetTriple\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-O3\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-flto=thin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-D_DEFAULT_SOURCE\"", script, StringComparison.Ordinal);
        Assert.Contains("& $ArchiverPath rcsD", script, StringComparison.Ordinal);
        Assert.Contains("& $RanlibPath -D", script, StringComparison.Ordinal);
        Assert.Contains("_GLFW_X11", script, StringComparison.Ordinal);
        Assert.Contains("_GLFW_WIN32", script, StringComparison.Ordinal);
        Assert.DoesNotContain("_GLFW_WAYLAND", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Get-Command clang", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pkg-config", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PKG_CONFIG", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GLFW_INCLUDE_DIR", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GLFW_LIBRARY_DIR", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/lib", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/local", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxReleaseBuildInstallsVerifiesAndRecordsTheDeclaredX11HeaderPrerequisite()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var script = ReadScript();

        Assert.Contains("name: Install GLFW X11 source-build prerequisites", workflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ matrix.operating_system == 'linux' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("sudo apt-get install --no-install-recommends --yes xorg-dev", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/vendor-build-prerequisites/${{ matrix.asset_suffix }}", workflow, StringComparison.Ordinal);
        Assert.Contains("glfw-x11-packages.txt", workflow, StringComparison.Ordinal);

        Assert.Contains("function Assert-GlfwLinuxBuildHeaders", script, StringComparison.Ordinal);
        Assert.Contains("-fsyntax-only", script, StringComparison.Ordinal);
        Assert.Contains("target-compatible X11 development headers", script, StringComparison.Ordinal);
        foreach (var header in new[]
        {
            "X11/XKBlib.h", "X11/Xatom.h", "X11/Xcursor/Xcursor.h", "X11/Xlib.h",
            "X11/Xmd.h", "X11/Xresource.h", "X11/cursorfont.h", "X11/extensions/XInput2.h",
            "X11/extensions/Xinerama.h", "X11/extensions/Xrandr.h", "X11/extensions/shape.h", "X11/keysym.h",
        })
        {
            Assert.Contains(header, workflow, StringComparison.Ordinal);
            Assert.Contains(header, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LinuxX64AndWindowsX64SourceBuildContractsMatchThePinnedUpstreamSourceSets()
    {
        var script = ReadScript();

        foreach (var source in new[]
        {
            "context.c", "init.c", "input.c", "monitor.c", "platform.c", "vulkan.c", "window.c",
            "egl_context.c", "osmesa_context.c", "null_init.c", "null_monitor.c", "null_window.c", "null_joystick.c",
            "posix_module.c", "posix_time.c", "posix_thread.c", "x11_init.c", "x11_monitor.c", "x11_window.c",
            "xkb_unicode.c", "glx_context.c", "linux_joystick.c", "posix_poll.c",
            "win32_module.c", "win32_time.c", "win32_thread.c", "win32_init.c", "win32_joystick.c",
            "win32_monitor.c", "win32_window.c", "wgl_context.c",
        })
        {
            Assert.Contains($"\"{source}\"", script, StringComparison.Ordinal);
        }

        foreach (var excludedWaylandSource in new[] { "wl_init.c", "wl_monitor.c", "wl_window.c" })
        {
            Assert.DoesNotContain(excludedWaylandSource, script, StringComparison.Ordinal);
        }

        Assert.Contains("Source-built GLFW is unsupported for operating system", script, StringComparison.Ordinal);
        Assert.Contains("must be prepared on $operatingSystem-$architecture", script, StringComparison.Ordinal);
        Assert.Contains("$nativeLibraryFileName = if ($IsWindows) { \"glfw3.lib\" } else { \"libglfw3.a\" }", script, StringComparison.Ordinal);
        Assert.Contains("$packageLibraryFileName = if ($IsWindows) { \"VendorGLFW.lib\" } else { \"libVendorGLFW.a\" }", script, StringComparison.Ordinal);
        Assert.Contains("@(\"pthread\", \"dl\", \"rt\", \"m\")", script, StringComparison.Ordinal);
        Assert.Contains("@(\"gdi32\", \"user32\", \"shell32\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@(\"X11\",", script, StringComparison.Ordinal);
        Assert.Contains("X11 development headers", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "eng", "release", "vendor-packages.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorPrecompilesBridgeAndRunsDisplayFreePackageRuntimeSmoke()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = ReadScript();
        var smoke = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests",
            "fixtures",
            "release",
            "GlfwBundledRuntimeSmoke.stark"));

        Assert.Contains("function Add-GlfwBridgeToNativeArchive", script, StringComparison.Ordinal);
        Assert.Contains("& $ArchiverPath rD $NativeArchive $BridgeObject", script, StringComparison.Ordinal);
        Assert.Contains("eventBridgeCompiledIntoNativeArchive = $true", script, StringComparison.Ordinal);
        Assert.Contains("perApplicationNativeSourceCompilation = $false", script, StringComparison.Ordinal);
        Assert.Contains("compile the verified repository", script, StringComparison.Ordinal);
        Assert.Contains("$repositoryPackageSource,", script, StringComparison.Ordinal);
        Assert.Contains("$legacyStagedBridge", script, StringComparison.Ordinal);
        Assert.Contains("$actualSources.Count -ne 0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--native-source\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Kind \"native-source\"", script, StringComparison.Ordinal);
        Assert.Contains("GlfwBundledRuntimeSmoke.stark", script, StringComparison.Ordinal);
        Assert.Contains("Bundled GLFW runtime smoke failed", script, StringComparison.Ordinal);

        Assert.Contains("import Vendor.GLFW", smoke, StringComparison.Ordinal);
        Assert.Contains("Version version = GetVersion()", smoke, StringComparison.Ordinal);
        Assert.Contains("version.Major != 3", smoke, StringComparison.Ordinal);
        Assert.Contains("version.Minor != 4", smoke, StringComparison.Ordinal);
        Assert.Contains("ClearEvents();", smoke, StringComparison.Ordinal);
        Assert.Contains("DroppedEventCount() != 0", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("Initialize()", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWindow", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorPreservesReleaseTargetSystemIdentityAndRelocatableNativeFacts()
    {
        var script = ReadScript();

        Assert.Contains("\"--emit-lib\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-stark-path\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--package-profile\", \"release\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--target\", $TargetTriple", script, StringComparison.Ordinal);
        Assert.Contains("\"--toolchain-dir\", $toolchainRoot", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-include-dir\", $nativeRoot", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-library-dir\", $nativeLibraryRoot", script, StringComparison.Ordinal);
        Assert.Contains("native/glfw/lib", script, StringComparison.Ordinal);
        Assert.Contains("does not exactly match staged System", script, StringComparison.Ordinal);
        Assert.Contains("exact target/data-layout/release-profile facts", script, StringComparison.Ordinal);
        Assert.Contains("Generated GLFW package native metadata does not exactly preserve", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 1", script, StringComparison.Ordinal);
        Assert.Contains("targetId = $AssetSuffix", script, StringComparison.Ordinal);
        Assert.Contains("packages = @($packageEntry)", script, StringComparison.Ordinal);
        Assert.Contains("nativePayload = [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains("provenance = $provenanceDescriptor", script, StringComparison.Ordinal);
        Assert.Contains("Sort-ObjectsOrdinalByProperty", script, StringComparison.Ordinal);
        Assert.Contains("[System.StringComparer]::Ordinal.Compare", script, StringComparison.Ordinal);
        Assert.Contains("buildHostPrerequisites", script, StringComparison.Ordinal);
        Assert.Contains("compile-time only", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "eng", "release", "vendor-packages.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorRejectsUnsafeArchivesPathsAndSharedRootOwnership()
    {
        var script = ReadScript();

        Assert.Contains("Assert-SafeOutputRoot -Path $outputRoot", script, StringComparison.Ordinal);
        Assert.Contains("must be a child of repository artifacts", script, StringComparison.Ordinal);
        Assert.Contains("traverses symbolic link or reparse point", script, StringComparison.Ordinal);
        Assert.Contains("must be outside shared OutputVendorRoot", script, StringComparison.Ordinal);
        Assert.Contains("must not overlap", script, StringComparison.Ordinal);
        Assert.Contains("duplicate or case-colliding entry", script, StringComparison.Ordinal);
        Assert.Contains("contains traversal entry", script, StringComparison.Ordinal);
        Assert.Contains("contains symbolic link", script, StringComparison.Ordinal);
        Assert.Contains("Select-DarwinArm64ArchiveSlice", script, StringComparison.Ordinal);
        Assert.Contains("must contain exactly one arm64 slice", script, StringComparison.Ordinal);
        Assert.Contains("is a thin archive", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedPath -Root $outputRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $outputRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("release-input.json", script, StringComparison.Ordinal);
    }

    private static string[] Strings(JsonElement value)
        => value.EnumerateArray().Select(static item => item.GetString()!).ToArray();

    private static string ReadScript()
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Recipe));

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

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
