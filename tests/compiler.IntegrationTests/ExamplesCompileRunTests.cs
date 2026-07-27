using Stark.Compiler;
using System.Text.RegularExpressions;

namespace compiler.IntegrationTests;

public sealed class ExamplesCompileRunTests
{
    [Fact]
    public async Task HelloExampleCompilesAndRunsWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-hello-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        Directory.CreateDirectory(stdlibPackageDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var helloSource = Path.Combine(repositoryRoot, "examples", "hello.stark");
        var helloOutput = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "hello.exe" : "hello");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var compileResult = await CompileExecutableAsync(helloSource, helloOutput, stdlibPackageDirectory);

            Assert.True(File.Exists(helloOutput));
            Assert.Contains("Emitted executable:", compileResult.Stdout);

            var processResult = await RunNativeExecutableAsync(helloOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("Hello, World!\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task MultiModuleExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-multi-");
        var appOutput = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "multi-module", "App.stark"),
                appOutput);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(appOutput);

            Assert.Equal(7, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BasicSyntaxExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-basic-syntax-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "basic-syntax.exe" : "basic-syntax");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "basic-syntax", "BasicSyntax.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TypeSystemExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-type-system-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "type-system.exe" : "type-system");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "type-system", "TypeSystem.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ModulesExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-modules-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "modules.exe" : "modules");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "modules", "App.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BorrowingExamplesCompileAndRun()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-borrowing-");
        try
        {
            foreach (var exampleName in new[] { "Borrowing", "OwnershipMoves", "BorrowKinds", "OutParameters" })
            {
                var outputPath = Path.Combine(
                    tempDirectory.FullName,
                    OperatingSystem.IsWindows() ? $"{exampleName}.exe" : exampleName);

                var result = await CompileExecutableAsync(
                    Path.Combine(repositoryRoot, "examples", "borrowing", $"{exampleName}.stark"),
                    outputPath);

                Assert.Contains("Emitted executable:", result.Stdout);

                var processResult = await RunNativeExecutableAsync(outputPath);

                Assert.Equal(0, processResult.ExitCode);
                Assert.Equal(string.Empty, processResult.StandardOutput);
                Assert.Equal(string.Empty, processResult.StandardError);
            }
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task FfiExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-ffi-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "ffi.exe" : "ffi");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "ffi", "Ffi.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(7, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task StandardLibraryExampleCompilesAndRunsWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-stdlib-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        Directory.CreateDirectory(stdlibPackageDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var sourcePath = Path.Combine(repositoryRoot, "examples", "standard-library", "StandardLibrary.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "standard-library.exe" : "standard-library");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var result = await CompileExecutableAsync(sourcePath, outputPath, stdlibPackageDirectory);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("Standard library ready\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildYourOwnGitExamplesInitializeWriteCommitUpdateRefListInspectAndReportStatusWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-git-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        var workingDirectory = Path.Combine(tempDirectory.FullName, "work");
        Directory.CreateDirectory(stdlibPackageDirectory);
        Directory.CreateDirectory(workingDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var initSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Init.stark");
        var commitSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Commit.stark");
        var refSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Ref.stark");
        var objectsSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Objects.stark");
        var inspectSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Inspect.stark");
        var statusSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Status.stark");
        var initOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-init.exe" : "git-init");
        var commitOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-commit.exe" : "git-commit");
        var refOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-ref.exe" : "git-ref");
        var objectsOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-objects.exe" : "git-objects");
        var inspectOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-inspect.exe" : "git-inspect");
        var statusOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-status.exe" : "git-status");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var initResult = await CompileExecutableAsync(initSourcePath, initOutputPath, stdlibPackageDirectory);
            var commitResult = await CompileExecutableAsync(commitSourcePath, commitOutputPath, stdlibPackageDirectory);
            var refResult = await CompileExecutableAsync(refSourcePath, refOutputPath, stdlibPackageDirectory);
            var objectsResult = await CompileExecutableAsync(objectsSourcePath, objectsOutputPath, stdlibPackageDirectory);
            var inspectResult = await CompileExecutableAsync(inspectSourcePath, inspectOutputPath, stdlibPackageDirectory);
            var statusResult = await CompileExecutableAsync(statusSourcePath, statusOutputPath, stdlibPackageDirectory);

            Assert.Contains("Emitted executable:", initResult.Stdout);
            Assert.Contains("Emitted executable:", commitResult.Stdout);
            Assert.Contains("Emitted executable:", refResult.Stdout);
            Assert.Contains("Emitted executable:", objectsResult.Stdout);
            Assert.Contains("Emitted executable:", inspectResult.Stdout);
            Assert.Contains("Emitted executable:", statusResult.Stdout);

            var initProcessResult = await RunNativeExecutableAsync(initOutputPath, workingDirectory);

            Assert.Equal(0, initProcessResult.ExitCode);
            Assert.Equal("Initialized starkgit-demo/.starkgit\n", initProcessResult.StandardOutput);
            Assert.Equal(string.Empty, initProcessResult.StandardError);
            Assert.True(Directory.Exists(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "objects")));
            Assert.True(Directory.Exists(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "refs", "heads")));
            Assert.Equal(
                "ref: refs/heads/main\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "HEAD")));

            var commitProcessResult = await RunNativeExecutableAsync(commitOutputPath, workingDirectory);

            Assert.Equal(0, commitProcessResult.ExitCode);
            Assert.Equal("Wrote demo commit object\n", commitProcessResult.StandardOutput);
            Assert.Equal(string.Empty, commitProcessResult.StandardError);
            Assert.Equal(
                "tree empty\nauthor Stark Example\n\ninitial commit\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "objects", "demo-commit")));

            var refProcessResult = await RunNativeExecutableAsync(refOutputPath, workingDirectory);

            Assert.Equal(0, refProcessResult.ExitCode);
            Assert.Equal("Updated main ref\n", refProcessResult.StandardOutput);
            Assert.Equal(string.Empty, refProcessResult.StandardError);
            Assert.Equal(
                "demo-commit\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "refs", "heads", "main")));

            var objectsProcessResult = await RunNativeExecutableAsync(objectsOutputPath, workingDirectory);

            Assert.Equal(0, objectsProcessResult.ExitCode);
            Assert.Equal("Object demo-commit\n", objectsProcessResult.StandardOutput);
            Assert.Equal(string.Empty, objectsProcessResult.StandardError);

            var inspectProcessResult = await RunNativeExecutableAsync(inspectOutputPath, workingDirectory);

            Assert.Equal(0, inspectProcessResult.ExitCode);
            Assert.Equal("Repository metadata present\n", inspectProcessResult.StandardOutput);
            Assert.Equal(string.Empty, inspectProcessResult.StandardError);

            var statusProcessResult = await RunNativeExecutableAsync(statusOutputPath, workingDirectory);

            Assert.Equal(0, statusProcessResult.ExitCode);
            Assert.Equal("Repository status clean\n", statusProcessResult.StandardOutput);
            Assert.Equal(string.Empty, statusProcessResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task NeuralNetworkExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-neural-network-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "neural-network.exe" : "neural-network");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "neural-network", "Inference.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task SimpleDatabaseExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-simple-database-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "simple-database.exe" : "simple-database");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "simple-database", "MemoryTable.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BitTorrentTrackerResponseExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-bit-torrent-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "bit-torrent.exe" : "bit-torrent");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "bit-torrent", "TrackerResponse.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BitTorrentHandshakeExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-bit-torrent-handshake-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "bit-torrent-handshake.exe" : "bit-torrent-handshake");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "bit-torrent", "Handshake.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BreakoutCoreExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-breakout-core-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "breakout-core.exe" : "breakout-core");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "breakout", "BreakoutCore.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task RaylibStarkModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibImportDirectory = Path.Combine(repositoryRoot, "examples", "raylib");
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");

        Assert.False(File.Exists(Path.Combine(raylibImportDirectory, "RaylibNative.c")));
        Assert.DoesNotContain(
            "RaylibNative.c",
            await File.ReadAllTextAsync(Path.Combine(raylibImportDirectory, "Stark.toml")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--native-source",
            await File.ReadAllTextAsync(Path.Combine(raylibImportDirectory, "Raylib.package.args")),
            StringComparison.Ordinal);

        var exampleTypesSource = await File.ReadAllTextAsync(Path.Combine(raylibImportDirectory, "Raylib", "Types.stark"));
        Assert.Contains("public const RAYLIB_VERSION_MAJOR = 6;", exampleTypesSource, StringComparison.Ordinal);
        Assert.Contains("""public const ascii RAYLIB_VERSION = "6.0";""", exampleTypesSource, StringComparison.Ordinal);

        var vendorTypesSource = await File.ReadAllTextAsync(Path.Combine(vendorImportDirectory, "Vendor", "Raylib", "Types.stark"));
        Assert.Contains("public struct ModelSkeleton", vendorTypesSource, StringComparison.Ordinal);
        Assert.Contains("rawmutptr<ModelAnimPose> KeyframePoses;", vendorTypesSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SHADER_LOC_BONE_MATRICES", vendorTypesSource, StringComparison.Ordinal);

        await CheckSourceAsync(
            Path.Combine(raylibImportDirectory, "Raylib.stark"),
            raylibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(vendorImportDirectory, "Vendor", "Raylib.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "breakout", "BreakoutRaylib.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(raylibImportDirectory, "VendorRaylibSafeApis.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        var packageDirectory = Directory.CreateTempSubdirectory("stark-raylib-package-check-");
        try
        {
            var packageImagePath = Path.Combine(packageDirectory.FullName, "libVendorRaylib.starkpkg");
            var packageLibraryFileName = OperatingSystem.IsWindows()
                ? "VendorRaylib.lib"
                : "libVendorRaylib.a";
            var packageStdout = new StringWriter();
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(vendorImportDirectory, "Vendor", "Raylib.stark"),
                    "--emit-pkg",
                    "--package-library-file", packageLibraryFileName,
                    "--package-profile", "release",
                    "--sdk-root", repositoryRoot,
                    "--no-stark-path",
                    "-I", vendorImportDirectory,
                    "-o", packageImagePath
                ],
                new StringReader(string.Empty),
                packageStdout,
                packageStderr);
            Assert.True(
                packageExitCode == 0,
                packageStdout + Environment.NewLine + packageStderr);
            await File.WriteAllBytesAsync(
                Path.Combine(packageDirectory.FullName, packageLibraryFileName),
                []);

            await CheckSourceAsync(
                Path.Combine(raylibImportDirectory, "VendorRaylibSafeApis.stark"),
                packageDirectory.FullName);
        }
        finally
        {
            Cleanup(packageDirectory);
        }
    }

    [Fact]
    public async Task VendorRaylibSafeWrappersCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateDirectory(Path.Combine(repositoryRoot, "artifacts", "tmp", $"stark-vendor-raylib-safe-{Guid.NewGuid():N}"));
        var sourcePath = Path.Combine(tempDirectory.FullName, "VendorRaylibSafeWrappers.stark");

        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.Raylib
                import Vendor.Raymath
                import System.Memory
                import System.Text
                module VendorRaylibSafeWrappers

                fn i32[min max] Probe()
                {
                    stack mut Image image = DefaultImage();
                    image = ImageFormat(image, PixelFormatUncompressedR8g8b8a8());
                    image = ImageAlphaPremultiply(image);
                    image = ImageDrawPixel(image, 1, 1, RED());

                    stack f32[1] kernel =
                    {
                        1.0f
                    };
                    stack mut Image filtered = DefaultImage();
                    if (ImageKernelConvolution(image, kernel, filtered) != RaylibStatus.Ok)
                    {
                        return 1;
                    }

                    image = filtered;

                    stack Vector2[3] points =
                    {
                        Vec2(0.0f, 0.0f),
                        Vec2(1.0f, 1.0f),
                        Vec2(2.0f, 0.0f)
                    };

                    if (DrawLineStrip(points, BLACK()) != RaylibStatus.Ok)
                    {
                        return 2;
                    }

                    stack CollisionPointResult collision = CheckCollisionLines(
                        Vec2(0.0f, 0.0f),
                        Vec2(1.0f, 1.0f),
                        Vec2(0.0f, 1.0f),
                        Vec2(1.0f, 0.0f));
                    if (collision.Hit)
                    {
                        image = ImageDrawPixelV(image, collision.Point, WHITE());
                    }

                    stack mut Image drawn = DefaultImage();
                    if (ImageDrawTriangleFan(image, points, WHITE(), drawn) != RaylibStatus.Ok)
                    {
                        return 3;
                    }

                    stack mut Camera camera = DefaultCamera3D();
                    camera = UpdateCamera(camera, CameraModeFree());
                    camera = UpdateCameraPro(camera, Vec3(0.0f, 0.0f, 0.0f), Vec3(0.0f, 0.0f, 0.0f), 0.0f);

                    stack mut Wave wave = DefaultWave();
                    wave = WaveFormat(wave, 44100, 16, 2);

                    stack mut Texture2D texture = DefaultTexture();
                    texture = GenTextureMipmaps(texture);
                    SetTextureFilter(texture, TextureFilterBilinear());
                    SetTextureWrap(texture, TextureWrapRepeat());

                    stack mut Mesh mesh = DefaultMesh();
                    mesh = GenMeshTangents(mesh);

                    stack mut Material material = DefaultMaterial();
                    material = SetMaterialTexture(material, MaterialMapAlbedo(), texture);

                    stack u8[0 max][4] bytes =
                    {
                        1, 2, 3, 4
                    };
                    stack mut i8[min max][4] pixelBytes =
                    {
                        0, 0, 0, 0
                    };
                    stack i32[min max][2] codepoints =
                    {
                        65, 66
                    };
                    stack Matrix[1] transforms =
                    {
                        MatrixIdentity()
                    };
                    stack mut u32[0 max][8] sha256 =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0
                    };

                    stack RaylibBytesResult loadedBytes = LoadFileData("missing.bin");
                    stack bool savedBytes = SaveFileData("missing.bin", bytes);
                    stack RaylibBytesResult compressed = CompressData(bytes);
                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> encoded = EncodeDataBase64(bytes);
                    if (!ComputeSHA256(bytes, sha256))
                    {
                        return 7;
                    }

                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> fileName = GetFileName("assets/player.png");
                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> upper = TextToUpper("raylib");
                    stack RaylibTextResult utf8 = LoadUTF8(codepoints);
                    stack RaylibCodepointsResult decoded = LoadCodepoints("AB");
                    if (SetPixelColor(pixelBytes, RED(), PixelFormatUncompressedR8g8b8a8()) != RaylibStatus.Ok)
                    {
                        return 8;
                    }

                    stack Color pixel = GetPixelColor(pixelBytes, PixelFormatUncompressedR8g8b8a8());
                    if (UpdateTexture(texture, pixelBytes) != RaylibStatus.Ok)
                    {
                        return 9;
                    }

                    if (UpdateSound(DefaultSound(), pixelBytes, -1) != RaylibStatus.InvalidArgument)
                    {
                        return 10;
                    }

                    if (DrawMeshInstanced(mesh, material, transforms) != RaylibStatus.Ok)
                    {
                        return 11;
                    }

                    stack mut OwnedImage ownedImage = OwnImage(DefaultImage());
                    if (!ownedImage.IsEmpty())
                    {
                        return 4;
                    }

                    stack Image releasedImage = ownedImage.Release();
                    if (releasedImage.Data != null)
                    {
                        return 5;
                    }

                    stack mut OwnedTexture2D ownedTexture = OwnTexture2D(DefaultTexture());
                    SetTextureFilter(ownedTexture.Value(), TextureFilterBilinear());
                    ownedTexture.Close();

                    stack mut OwnedMaterial ownedMaterial = OwnMaterial(DefaultMaterial());
                    ownedMaterial.Close();

                    stack mut OwnedModelAnimations animations = LoadOwnedModelAnimations("missing.iqm");
                    stack mut ModelAnimation animation = DefaultModelAnimation();
                    if (animations.TryGet(0, animation))
                    {
                        return 6;
                    }
                    animations.Close();

                    return 0;
                }
                """);

            await CheckSourceAsync(sourcePath, vendorImportDirectory, stdlibImportDirectory);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorRaylibInternalFfiDeclarationsUseLinkNameWithoutNativeShims()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorRaylibDirectory = Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib");
        var nativeShimPath = Path.Combine(repositoryRoot, "vendor", "RaylibAbiShims.c");
        Assert.False(File.Exists(nativeShimPath));

        var starkShimDeclarations = new SortedSet<string>(StringComparer.Ordinal);
        var linkNamedDeclarations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sourcePath in Directory.EnumerateFiles(vendorRaylibDirectory, "*.stark"))
        {
            var pendingLinkName = false;
            foreach (var line in await File.ReadAllLinesAsync(sourcePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[LinkName(", StringComparison.Ordinal))
                {
                    pendingLinkName = true;
                    continue;
                }

                var match = Regex.Match(
                    line,
                    @"\binternal\s+unsafe\s+ffi(?:\([^)]*\))?\s+(?:varargs\s+)?fn\s+[^;{]*?\b(stark_raylib_[A-Za-z_][A-Za-z0-9_]*)\s*\(",
                    RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    if (pendingLinkName)
                    {
                        linkNamedDeclarations.Add(match.Groups[1].Value);
                    }
                    else
                    {
                        starkShimDeclarations.Add(match.Groups[1].Value);
                    }

                    pendingLinkName = false;
                    continue;
                }

                if (trimmed.Length != 0 && !trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    pendingLinkName = false;
                }
            }
        }

        Assert.NotEmpty(linkNamedDeclarations);
        Assert.Empty(starkShimDeclarations);

        foreach (var directAggregateBinding in new[]
                 {
                     "stark_raylib_ClearBackground",
                     "stark_raylib_DrawText",
                     "stark_raylib_LoadImage",
                     "stark_raylib_LoadImageFromTexture",
                     "stark_raylib_UnloadImage",
                     "stark_raylib_Fade",
                     "stark_raylib_LoadShader",
                     "stark_raylib_DrawLineV",
                     "stark_raylib_DrawCircleV",
                     "stark_raylib_DrawRectangleRec",
                     "stark_raylib_DrawRectanglePro",
                     "stark_raylib_CheckCollisionCircleRec",
                     "stark_raylib_DrawTexturePro",
                     "stark_raylib_DrawTextEx",
                     "stark_raylib_MeasureTextEx",
                     "stark_raylib_GetMonitorPosition",
                     "stark_raylib_DrawTextureRec",
                     "stark_raylib_GetSplinePointLinear",
                     "stark_raylib_GetScreenToWorldRay",
                     "stark_raylib_GetCollisionRec",
                     "stark_raylib_ColorNormalize",
                     "stark_raylib_DrawModelEx",
                     "stark_raylib_GenMeshHeightmap",
                     "stark_raylib_GetRayCollisionTriangle",
                     "stark_raylib_GetFileModTime",
                     "stark_raylib_DrawLineDashed",
                     "stark_raylib_DrawEllipseV",
                     "stark_raylib_UpdateModelAnimationEx",
                     "stark_raylib_FileCopy",
                     "stark_raylib_FileMove"
                 })
        {
            Assert.Contains(directAggregateBinding, linkNamedDeclarations);
        }

        Assert.DoesNotContain("stark_raylib_UpdateModelAnimationBones", linkNamedDeclarations);
        Assert.DoesNotContain("stark_raylib_UnloadModelAnimation", linkNamedDeclarations);
    }

    [Fact]
    public async Task BreakoutRaylibBuildsThroughPackageOwnedNativeMetadataWithoutGraphicalExecution()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-breakout-raylib-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var nativeIncludeDirectory = Path.Combine(packageDirectory, "native-include");
        var nativeLibraryDirectory = Path.Combine(packageDirectory, "native-libs");
        var tempsDirectory = Path.Combine(tempDirectory.FullName, "temps");
        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(nativeIncludeDirectory);
        Directory.CreateDirectory(nativeLibraryDirectory);

        var fakeNativeSource = Path.Combine(packageDirectory, "RaylibNativeSmoke.c");
        var vendorRaylibLibrary = Path.Combine(packageDirectory, "libVendorRaylib.a");
        var breakoutOutput = Path.Combine(tempDirectory.FullName, "breakout-raylib");
        var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);

        try
        {
            await File.WriteAllTextAsync(
                fakeNativeSource,
                """
                int stark_raylib_native_metadata_anchor(void) {
                    return 0;
                }
                """);

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(vendorImportDirectory, "Vendor", "Raylib.stark"),
                    "--emit-lib",
                    "-I", vendorImportDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", vendorRaylibLibrary,
                    "--native-source", fakeNativeSource,
                    "--native-include-dir", nativeIncludeDirectory,
                    "--native-library-dir", nativeLibraryDirectory,
                    "--native-library", "raylib",
                    "--native-library", "GL",
                    "--native-library", "m",
                    "--native-library", "pthread",
                    "--native-library", "dl",
                    "--native-library", "rt",
                    "--native-library", "X11",
                    "--native-library", "Xrandr",
                    "--native-library", "Xi",
                    "--native-library", "Xcursor",
                    "--native-library", "Xinerama",
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.True(File.Exists(vendorRaylibLibrary));

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "breakout", "BreakoutRaylib.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", breakoutOutput,
                    "--linker", linkerPath,
                    "--save-temps", tempsDirectory,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(breakoutOutput));

            var linkerLog = await File.ReadAllTextAsync(linkerLogPath);
            Assert.Contains(Path.GetFullPath(vendorRaylibLibrary), linkerLog, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(nativeLibraryDirectory), linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lraylib", linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lGL", linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lXinerama", linkerLog, StringComparison.Ordinal);
            Assert.Contains("native_0_RaylibNativeSmoke", linkerLog, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorSTBImageModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var stbImageSource = Path.Combine(vendorImportDirectory, "Vendor", "STB", "Image.stark");
        var stbNativeSource = Path.Combine(repositoryRoot, "vendor", "StbImageImplementation.c");
        var stbVersionFile = Path.Combine(repositoryRoot, "vendor", "native", "stb", "VERSION.md");

        Assert.True(File.Exists(stbNativeSource));
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "vendor", "native", "stb", "stb_image.h")));
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "vendor", "native", "stb", "stb_image_write.h")));
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "vendor", "native", "stb", "stb_image_resize2.h")));
        Assert.Contains(
            "31c1ad37456438565541f4919958214b6e762fb4",
            await File.ReadAllTextAsync(stbVersionFile),
            StringComparison.Ordinal);

        var nativeSourceText = await File.ReadAllTextAsync(stbNativeSource);
        Assert.Contains("STB_IMAGE_IMPLEMENTATION", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("STB_IMAGE_WRITE_IMPLEMENTATION", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("STB_IMAGE_RESIZE_IMPLEMENTATION", nativeSourceText, StringComparison.Ordinal);

        var sourceText = await File.ReadAllTextAsync(stbImageSource);
        Assert.Contains("""[LinkName("stbi_load_from_memory")]""", sourceText, StringComparison.Ordinal);
        Assert.Contains("""[LinkName("stbi_write_png")]""", sourceText, StringComparison.Ordinal);
        Assert.Contains("""[LinkName("stbir_resize_uint8_linear")]""", sourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Image", sourceText, StringComparison.Ordinal);
        Assert.Contains("dynamic u8[0 max] PixelBytes", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn ImageResult LoadFromMemory", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn StbImageStatus ResizeLinearInto", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe", sourceText, StringComparison.Ordinal);

        await CheckSourceAsync(
            stbImageSource,
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "stb-image", "StbImageResize.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task VendorSTBImageBuildsAndRunsThroughPackageOwnedNativeMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-vendor-stb-image-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var vendorSTBImageSource = Path.Combine(vendorImportDirectory, "Vendor", "STB", "Image.stark");
        var stbNativeSource = Path.Combine(repositoryRoot, "vendor", "StbImageImplementation.c");
        var stbIncludeDirectory = Path.Combine(repositoryRoot, "vendor", "native", "stb");
        var vendorSTBImageLibrary = Path.Combine(
            packageDirectory,
            OperatingSystem.IsWindows() ? "VendorSTBImage.lib" : "libVendorSTBImage.a");
        var vendorSTBImagePackage = Path.ChangeExtension(vendorSTBImageLibrary, ".starkpkg");
        var stbImageOutput = Path.Combine(
            tempDirectory.FullName,
            OperatingSystem.IsWindows() ? "stb-image-resize.exe" : "stb-image-resize");

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    vendorSTBImageSource,
                    "--emit-lib",
                    "-I", vendorImportDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", vendorSTBImageLibrary,
                    "--native-source", stbNativeSource,
                    "--native-include-dir", stbIncludeDirectory,
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(vendorSTBImageLibrary));
            Assert.True(File.Exists(vendorSTBImagePackage));

            Assert.True(PackageImageLoader.TryLoadManifest(vendorSTBImagePackage, out var manifest));
            Assert.Equal("Vendor.STB.Image", manifest.RootModule);
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal("StbImageImplementation.c", Path.GetFileName(Assert.Single(manifest.NativeDependencies!.Sources!)));
            Assert.EndsWith(
                Path.Combine("native", "stb"),
                Assert.Single(manifest.NativeDependencies.IncludeDirectories!),
                StringComparison.Ordinal);
            Assert.True(manifest.NativeDependencies.PkgConfigPackages is null
                || manifest.NativeDependencies.PkgConfigPackages.Count == 0);

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [vendorSTBImagePackage, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.True(inspectExitCode == 0, inspectStderr.ToString());
            Assert.Matches(
                "native dependencies: sources=1, includes=1, .*pkg-config=0",
                inspectStdout.ToString());

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "stb-image", "StbImageResize.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", stbImageOutput,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(stbImageOutput));

            var processResult = await RunNativeExecutableAsync(stbImageOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("STB image ok\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorMiniaudioModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var miniaudioSource = Path.Combine(vendorImportDirectory, "Vendor", "Miniaudio.stark");
        var miniaudioNativeSource = Path.Combine(repositoryRoot, "vendor", "MiniaudioImplementation.c");
        var miniaudioVersionFile = Path.Combine(repositoryRoot, "vendor", "native", "miniaudio", "VERSION.md");
        var miniaudioHeader = Path.Combine(repositoryRoot, "vendor", "native", "miniaudio", "miniaudio.h");

        Assert.True(File.Exists(miniaudioNativeSource));
        Assert.True(File.Exists(miniaudioHeader));
        Assert.Contains(
            "0.11.25",
            await File.ReadAllTextAsync(miniaudioVersionFile),
            StringComparison.Ordinal);
        Assert.Contains(
            "9634bedb5b5a2ca38c1ee7108a9358a4e233f14d",
            await File.ReadAllTextAsync(miniaudioVersionFile),
            StringComparison.Ordinal);

        var nativeSourceText = await File.ReadAllTextAsync(miniaudioNativeSource);
        Assert.Contains("MINIAUDIO_IMPLEMENTATION", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("MA_NO_ENGINE", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_ma_decoder_create_memory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_ma_playback_create_f32", nativeSourceText, StringComparison.Ordinal);

        var sourceText = await File.ReadAllTextAsync(miniaudioSource);
        Assert.Contains("public struct Decoder", sourceText, StringComparison.Ordinal);
        Assert.Contains("public struct PlaybackDevice", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn DecoderResult OpenDecoderFromMemory", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn ReadFramesResult ReadF32Frames", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn PlaybackDeviceResult CreatePlaybackDeviceF32", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe", sourceText, StringComparison.Ordinal);

        await CheckSourceAsync(
            miniaudioSource,
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "miniaudio", "MiniaudioDecode.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task VendorMiniaudioBuildsAndRunsThroughPackageOwnedNativeMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-vendor-miniaudio-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var vendorMiniaudioSource = Path.Combine(vendorImportDirectory, "Vendor", "Miniaudio.stark");
        var miniaudioNativeSource = Path.Combine(repositoryRoot, "vendor", "MiniaudioImplementation.c");
        var miniaudioIncludeDirectory = Path.Combine(repositoryRoot, "vendor", "native", "miniaudio");
        var vendorMiniaudioLibrary = Path.Combine(
            packageDirectory,
            OperatingSystem.IsWindows() ? "VendorMiniaudio.lib" : "libVendorMiniaudio.a");
        var vendorMiniaudioPackage = Path.ChangeExtension(vendorMiniaudioLibrary, ".starkpkg");
        var miniaudioOutput = Path.Combine(
            tempDirectory.FullName,
            OperatingSystem.IsWindows() ? "miniaudio-decode.exe" : "miniaudio-decode");

        try
        {
            var emitArgs = new List<string>
            {
                vendorMiniaudioSource,
                "--emit-lib",
                "-I", vendorImportDirectory,
                "-I", stdlibImportDirectory,
                "-o", vendorMiniaudioLibrary,
                "--native-source", miniaudioNativeSource,
                "--native-include-dir", miniaudioIncludeDirectory,
            };

            if (OperatingSystem.IsLinux())
            {
                emitArgs.AddRange(["--native-library", "pthread", "--native-library", "m", "--native-library", "dl"]);
            }
            else if (OperatingSystem.IsMacOS())
            {
                emitArgs.AddRange([
                    "--native-link-arg", "-framework",
                    "--native-link-arg", "CoreAudio",
                    "--native-link-arg", "-framework",
                    "--native-link-arg", "AudioToolbox",
                    "--native-link-arg", "-framework",
                    "--native-link-arg", "CoreFoundation",
                ]);
            }

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                emitArgs.ToArray(),
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(vendorMiniaudioLibrary));
            Assert.True(File.Exists(vendorMiniaudioPackage));

            Assert.True(PackageImageLoader.TryLoadManifest(vendorMiniaudioPackage, out var manifest));
            Assert.Equal("Vendor.Miniaudio", manifest.RootModule);
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal(
                "MiniaudioImplementation.c",
                Path.GetFileName(Assert.Single(manifest.NativeDependencies!.Sources!)));
            Assert.EndsWith(
                Path.Combine("native", "miniaudio"),
                Assert.Single(manifest.NativeDependencies.IncludeDirectories!),
                StringComparison.Ordinal);
            Assert.True(manifest.NativeDependencies.PkgConfigPackages is null
                || manifest.NativeDependencies.PkgConfigPackages.Count == 0);
            if (OperatingSystem.IsLinux())
            {
                Assert.Contains("pthread", manifest.NativeDependencies.Libraries!);
                Assert.Contains("m", manifest.NativeDependencies.Libraries!);
                Assert.Contains("dl", manifest.NativeDependencies.Libraries!);
            }

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [vendorMiniaudioPackage, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.True(inspectExitCode == 0, inspectStderr.ToString());
            Assert.Contains("native dependencies: sources=1, includes=1", inspectStdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("pkg-config=0", inspectStdout.ToString(), StringComparison.Ordinal);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "miniaudio", "MiniaudioDecode.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", miniaudioOutput,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(miniaudioOutput));

            var processResult = await RunNativeExecutableAsync(miniaudioOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("Miniaudio decode ok\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorCgltfModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var cgltfSource = Path.Combine(vendorImportDirectory, "Vendor", "Cgltf.stark");
        var cgltfNativeSource = Path.Combine(repositoryRoot, "vendor", "CgltfImplementation.c");
        var cgltfVersionFile = Path.Combine(repositoryRoot, "vendor", "native", "cgltf", "VERSION.md");
        var cgltfHeader = Path.Combine(repositoryRoot, "vendor", "native", "cgltf", "cgltf.h");

        Assert.True(File.Exists(cgltfNativeSource));
        Assert.True(File.Exists(cgltfHeader));
        Assert.Contains(
            "v1.15",
            await File.ReadAllTextAsync(cgltfVersionFile),
            StringComparison.Ordinal);
        Assert.Contains(
            "360db1a95480fe102ae9c69b27c5d101167ff5ba",
            await File.ReadAllTextAsync(cgltfVersionFile),
            StringComparison.Ordinal);

        var nativeSourceText = await File.ReadAllTextAsync(cgltfNativeSource);
        Assert.Contains("CGLTF_IMPLEMENTATION", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_cgltf_parse_memory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_cgltf_parse_file", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_cgltf_copy_name", nativeSourceText, StringComparison.Ordinal);

        var sourceText = await File.ReadAllTextAsync(cgltfSource);
        Assert.Contains("public struct Document", sourceText, StringComparison.Ordinal);
        Assert.Contains("mut drop", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn DocumentResult ParseFromMemory", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn DocumentResult ParseFromFile", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn PrimitiveInfoResult GetPrimitiveInfo", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn NameCopyResult CopyMeshNameBytes", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe", sourceText, StringComparison.Ordinal);

        await CheckSourceAsync(
            cgltfSource,
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "cgltf", "CgltfAssetSummary.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task VendorCgltfBuildsAndRunsThroughPackageOwnedNativeMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-vendor-cgltf-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var vendorCgltfSource = Path.Combine(vendorImportDirectory, "Vendor", "Cgltf.stark");
        var cgltfNativeSource = Path.Combine(repositoryRoot, "vendor", "CgltfImplementation.c");
        var cgltfIncludeDirectory = Path.Combine(repositoryRoot, "vendor", "native", "cgltf");
        var tinyGltfAsset = Path.Combine(repositoryRoot, "examples", "cgltf", "assets", "tiny-triangle.gltf");
        var vendorCgltfLibrary = Path.Combine(
            packageDirectory,
            OperatingSystem.IsWindows() ? "VendorCgltf.lib" : "libVendorCgltf.a");
        var vendorCgltfPackage = Path.ChangeExtension(vendorCgltfLibrary, ".starkpkg");
        var cgltfOutput = Path.Combine(
            tempDirectory.FullName,
            OperatingSystem.IsWindows() ? "cgltf-asset-summary.exe" : "cgltf-asset-summary");

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    vendorCgltfSource,
                    "--emit-lib",
                    "-I", vendorImportDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", vendorCgltfLibrary,
                    "--native-source", cgltfNativeSource,
                    "--native-include-dir", cgltfIncludeDirectory,
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(vendorCgltfLibrary));
            Assert.True(File.Exists(vendorCgltfPackage));

            Assert.True(PackageImageLoader.TryLoadManifest(vendorCgltfPackage, out var manifest));
            Assert.Equal("Vendor.Cgltf", manifest.RootModule);
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal(
                "CgltfImplementation.c",
                Path.GetFileName(Assert.Single(manifest.NativeDependencies!.Sources!)));
            Assert.EndsWith(
                Path.Combine("native", "cgltf"),
                Assert.Single(manifest.NativeDependencies.IncludeDirectories!),
                StringComparison.Ordinal);
            Assert.True(manifest.NativeDependencies.PkgConfigPackages is null
                || manifest.NativeDependencies.PkgConfigPackages.Count == 0);

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [vendorCgltfPackage, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.True(inspectExitCode == 0, inspectStderr.ToString());
            Assert.Matches(
                "native dependencies: sources=1, includes=1, .*pkg-config=0",
                inspectStdout.ToString());

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "cgltf", "CgltfAssetSummary.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", cgltfOutput,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(cgltfOutput));

            var processResult = await RunNativeExecutableAsync(
                cgltfOutput,
                environment: new Dictionary<string, string>
                {
                    ["STARK_CGLTF_ASSET_PATH"] = tinyGltfAsset
                });

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("cgltf ok\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorGLFWModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var glfwSource = Path.Combine(vendorImportDirectory, "Vendor", "GLFW.stark");
        var glfwNativeSource = Path.Combine(repositoryRoot, "vendor", "GlfwEventBridge.c");

        Assert.True(File.Exists(glfwNativeSource));
        var nativeSourceText = await File.ReadAllTextAsync(glfwNativeSource);
        Assert.Contains("#include <GLFW/glfw3.h>", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_glfw_install_event_bridge", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("glfwSetKeyCallback", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_glfw_poll_event", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("STARK_GLFW_EVENT_CAPACITY = 256", nativeSourceText, StringComparison.Ordinal);

        var sourceText = await File.ReadAllTextAsync(glfwSource);
        Assert.Contains("public struct Library", sourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Window", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn LibraryResult Initialize", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn WindowResult CreateWindow", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn GlfwStatus EnableEventBridge", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn EventPollResult PollEvent", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn InputAction GetKey", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe", sourceText, StringComparison.Ordinal);

        await CheckSourceAsync(
            glfwSource,
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "glfw", "GlfwHiddenWindow.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "tests-stark", "vendor.GLFW", "GlfwTests.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task VendorGLFWBuildsAndRunsThroughPackageOwnedNativeMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !await PkgConfigPackageExistsAsync("glfw3"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-vendor-glfw-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var vendorGLFWSource = Path.Combine(vendorImportDirectory, "Vendor", "GLFW.stark");
        var glfwNativeSource = Path.Combine(repositoryRoot, "vendor", "GlfwEventBridge.c");
        var vendorGLFWLibrary = Path.Combine(
            packageDirectory,
            OperatingSystem.IsWindows() ? "VendorGLFW.lib" : "libVendorGLFW.a");
        var vendorGLFWPackage = Path.ChangeExtension(vendorGLFWLibrary, ".starkpkg");
        var glfwOutput = Path.Combine(
            tempDirectory.FullName,
            OperatingSystem.IsWindows() ? "glfw-hidden-window.exe" : "glfw-hidden-window");

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    vendorGLFWSource,
                    "--emit-lib",
                    "-I", vendorImportDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", vendorGLFWLibrary,
                    "--native-source", glfwNativeSource,
                    "--native-pkg-config", "glfw3",
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(vendorGLFWLibrary));
            Assert.True(File.Exists(vendorGLFWPackage));

            Assert.True(PackageImageLoader.TryLoadManifest(vendorGLFWPackage, out var manifest));
            Assert.Equal("Vendor.GLFW", manifest.RootModule);
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal("glfw3", Assert.Single(manifest.NativeDependencies!.PkgConfigPackages!));
            Assert.Equal(
                "GlfwEventBridge.c",
                Path.GetFileName(Assert.Single(manifest.NativeDependencies.Sources!)));

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [vendorGLFWPackage, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.True(inspectExitCode == 0, inspectStderr.ToString());
            Assert.Matches(
                "native dependencies: sources=1, .*pkg-config=1",
                inspectStdout.ToString());

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "glfw", "GlfwHiddenWindow.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", glfwOutput,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(glfwOutput));

            var processResult = await RunNativeExecutableAsync(glfwOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.True(
                processResult.StandardOutput == "GLFW hidden window ok\n"
                    || processResult.StandardOutput == "GLFW unavailable\n",
                processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorSDL3ModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var sdl3Source = Path.Combine(vendorImportDirectory, "Vendor", "SDL3.stark");
        var sdl3NativeSource = Path.Combine(repositoryRoot, "vendor", "Sdl3Binding.c");

        Assert.True(File.Exists(sdl3NativeSource));
        var nativeSourceText = await File.ReadAllTextAsync(sdl3NativeSource);
        Assert.Contains("#include <SDL3/SDL.h>", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sdl3_translate_event", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("SDL_PollEvent", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("SDL_OpenAudioDeviceStream", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("SDL_SetRenderDrawColor", nativeSourceText, StringComparison.Ordinal);

        var sourceText = await File.ReadAllTextAsync(sdl3Source);
        Assert.Contains("public struct Library", sourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Window", sourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Renderer", sourceText, StringComparison.Ordinal);
        Assert.Contains("public struct AudioStream", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn LibraryResult Initialize", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn WindowResult CreateWindow", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn RendererResult CreateDefaultRenderer", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn EventPollResult PollEvent", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn AudioStreamResult OpenDefaultPlaybackStream", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe", sourceText, StringComparison.Ordinal);

        await CheckSourceAsync(
            sdl3Source,
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "sdl3", "Sdl3WindowAudio.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "tests-stark", "vendor.SDL3", "Sdl3Tests.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task VendorSDL3BuildsAndRunsThroughPackageOwnedNativeMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !await PkgConfigPackageExistsAsync("sdl3"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-vendor-sdl3-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var vendorSDL3Source = Path.Combine(vendorImportDirectory, "Vendor", "SDL3.stark");
        var sdl3NativeSource = Path.Combine(repositoryRoot, "vendor", "Sdl3Binding.c");
        var vendorSDL3Library = Path.Combine(
            packageDirectory,
            OperatingSystem.IsWindows() ? "VendorSDL3.lib" : "libVendorSDL3.a");
        var vendorSDL3Package = Path.ChangeExtension(vendorSDL3Library, ".starkpkg");
        var sdl3Output = Path.Combine(
            tempDirectory.FullName,
            OperatingSystem.IsWindows() ? "sdl3-window-audio.exe" : "sdl3-window-audio");

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    vendorSDL3Source,
                    "--emit-lib",
                    "-I", vendorImportDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", vendorSDL3Library,
                    "--native-source", sdl3NativeSource,
                    "--native-pkg-config", "sdl3",
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(vendorSDL3Library));
            Assert.True(File.Exists(vendorSDL3Package));

            Assert.True(PackageImageLoader.TryLoadManifest(vendorSDL3Package, out var manifest));
            Assert.Equal("Vendor.SDL3", manifest.RootModule);
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal("sdl3", Assert.Single(manifest.NativeDependencies!.PkgConfigPackages!));
            Assert.Equal(
                "Sdl3Binding.c",
                Path.GetFileName(Assert.Single(manifest.NativeDependencies.Sources!)));

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [vendorSDL3Package, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.True(inspectExitCode == 0, inspectStderr.ToString());
            Assert.Matches(
                "native dependencies: sources=1, .*pkg-config=1",
                inspectStdout.ToString());

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "sdl3", "Sdl3WindowAudio.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", sdl3Output,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(sdl3Output));

            var processResult = await RunNativeExecutableAsync(
                sdl3Output,
                environment: new Dictionary<string, string>
                {
                    ["SDL_VIDEODRIVER"] = "dummy",
                    ["SDL_AUDIODRIVER"] = "dummy",
                    ["SDL_RENDER_DRIVER"] = "software"
                });

            Assert.Equal(0, processResult.ExitCode);
            Assert.True(
                processResult.StandardOutput == "SDL3 window/audio ok\n"
                    || processResult.StandardOutput == "SDL3 unavailable\n",
                processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task VendorSQLiteModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var sqliteNativeSource = Path.Combine(repositoryRoot, "vendor", "SQLiteTextBinding.c");
        var sqliteCoreSource = Path.Combine(vendorImportDirectory, "Vendor", "SQLite", "Core.stark");
        var sqliteTypesSource = Path.Combine(vendorImportDirectory, "Vendor", "SQLite", "Types.stark");
        var sqliteRawSource = Path.Combine(vendorImportDirectory, "Vendor", "SQLite", "Raw.stark");

        Assert.True(File.Exists(sqliteNativeSource));
        var nativeSourceText = await File.ReadAllTextAsync(sqliteNativeSource);
        Assert.Contains("SQLITE_TRANSIENT", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_text_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_text16_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_text64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_blob_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_blob64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_v2_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_v2_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_text_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_text_v2_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_blob_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_carray_bind_blob_v2_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_mutex_held_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_mutex_notheld_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_mutex_held", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_mutex_notheld", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_win32_set_directory_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_win32_set_directory8_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_win32_set_directory16_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_win32_set_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_win32_set_directory8", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_win32_set_directory16", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_version_variable", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_temp_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_data_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_set_temp_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_set_data_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_text_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_text16_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_text64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_blob_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_blob64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_function_argument", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_normalized_sql_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_v2_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_normalized_sql", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_i64", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_int", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_double", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_text", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_stmt_scanstatus_reset", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_snapshot_available", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_snapshot_get", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_snapshot_open", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_snapshot_free", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_snapshot_cmp", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_snapshot_recover", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_normalized_sql", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_stmt_scanstatus_v2", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_snapshot_get", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_carray_bind", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_carray_bind_v2", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_mutex_held", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_mutex_notheld", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_win32_set_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_win32_set_directory8", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_temp_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_data_directory", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("sqlite3_win32_set_directory16", nativeSourceText, StringComparison.Ordinal);

        var coreSourceText = await File.ReadAllTextAsync(sqliteCoreSource);
        var rawSourceText = await File.ReadAllTextAsync(sqliteRawSource);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> stark_sqlite_version_variable", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> stark_sqlite_temp_directory", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> stark_sqlite_data_directory", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_set_temp_directory", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_set_data_directory", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_text_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_text16_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_text64_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_blob_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_v2_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_v2_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_text_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_text_v2_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_blob_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_carray_bind_blob_v2_transient", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_mutex_held_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_mutex_notheld_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_mutex_held", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_mutex_notheld", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_win32_set_directory_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_win32_set_directory8_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_win32_set_directory16_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_win32_set_directory", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_win32_set_directory8", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_win32_set_directory16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_compileoption_used", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_keyword_name", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_complete16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_open16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_prepare16_v3", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_bind_zeroblob", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_prepare_v3", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_bind_int", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_bind_parameter_count", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_bind_parameter_name", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_data_count", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_void> sqlite3_column_blob", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_void> sqlite3_column_text16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_column_decltype", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_column_origin_name", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_column_int", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_sql", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_char> sqlite3_expanded_sql", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_normalized_sql_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_v2_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_normalized_sql", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_i64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_int", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_double", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_text", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_stmt_scanstatus_reset", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_snapshot_available", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_snapshot_get", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_snapshot_open", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_snapshot_cmp", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int stark_sqlite_snapshot_recover", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_transfer_bindings", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_stmt_status", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3Native> sqlite3_db_handle", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_blob_open", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_blob_read", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_blob_write", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3BackupNative> sqlite3_backup_init", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_backup_step", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_malloc(", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_malloc64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_realloc(", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_realloc64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn u64[0 max] sqlite3_msize", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<u8[0 max]> sqlite3_serialize", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_deserialize", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_wal_autocheckpoint", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_wal_checkpoint_v2", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn i64[min max] sqlite3_memory_used", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_release_memory", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_soft_heap_limit", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_randomness", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_stricmp", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_table_column_metadata", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_initialize", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult LibraryVersionConstant", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteTextResult TempDirectory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteTextResult DataDirectory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetTempDirectory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearTempDirectory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetDataDirectory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearDataDirectory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_shutdown", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_os_init", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_os_end", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_global_recover", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_thread_cleanup", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool Win32DirectoryNativeAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool Win32DirectoryUtf8Available", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool Win32DirectoryUtf16Available", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetWin32DirectoryNative", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ClearWin32DirectoryNative", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetWin32DirectoryUtf8", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ClearWin32DirectoryUtf8", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetWin32DirectoryUtf16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetWin32DirectoryUtf16Unicode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ClearWin32DirectoryUtf16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_enable_shared_cache", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_void> sqlite3_errmsg16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_busy_timeout", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_enable_load_extension", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_setlk_timeout", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_get_table", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_create_filename", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_filename_database", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3ValueNative> stark_sqlite_function_argument", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_collation", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_collation_v2", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_collation16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_collation_needed", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_collation_needed16", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_function_v2", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_window_function", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_aggregate_context", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_aggregate_count", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_user_data", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3Native> sqlite3_context_db_handle", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3ValueNative> sqlite3_column_value", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_bind_value", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<u8[0 max]> sqlite3_value_text", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_value_frombind", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3ValueNative> sqlite3_value_dup", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_result_error", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_result_int64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_result_zeroblob64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_errcode", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_extended_result_codes", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_limit", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_db_name", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_txn_state", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3StatementNative> sqlite3_next_stmt", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_status64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_db_status(", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_db_status64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3MutexNative> sqlite3_db_mutex", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3MutexNative> sqlite3_mutex_alloc", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_mutex_enter", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_mutex_try", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_mutex_leave", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3StringNative> sqlite3_str_new", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_char> sqlite3_str_finish", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_str_append(", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_str_appendall", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_str_appendchar", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_str_reset", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_str_truncate", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_str_errcode", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_str_length", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_char> sqlite3_str_value", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_changes", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn i64[min max] sqlite3_total_changes64", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatementResult PrepareWithFlags", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatementResult PrepareLegacy", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatementResult PrepareUtf16AsciiWithFlags", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteDatabaseResult OpenDefault", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult CloseStrict", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTableResult GetTable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult TableColumnName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult TableCell", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult TableCellIsNull", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteFilenameResult CreateFilename", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteFilenameResult CreateFilenameWithRawParameters", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FilenameDatabase", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FilenameJournal", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FilenameWal", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult CompileOptionUsed", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult KeywordName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult IsCompleteSqlUtf16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool CArrayBindAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool CArrayBindV2Available", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayInt32", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayInt32V2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayInt64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayInt64V2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayDouble", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayDoubleV2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayText", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayTextV2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayBlob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindCArrayBlobV2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindText64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindText16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindZeroBlob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult ColumnBlobCopy", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBytesResult ColumnBlobBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ColumnBlobView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBytesResult ColumnTextBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ColumnTextBytesView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBytesResult ColumnText16Bytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ColumnText16BytesView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteUtf16Result ColumnText16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult ColumnOriginName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteColumnMetadataResult TableColumnMetadata", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBlobResult OpenBlob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult ReadBlob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult WriteBlob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBackupResult OpenBackup", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBackupStepResult StepBackup", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBytesResult SerializeDatabase", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult DeserializeDatabaseFromSerialized", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteWalCheckpointResult WalCheckpointWithMode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ValueBlobView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBytesResult ValueTextBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ValueTextBytesView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBytesResult ValueText16Bytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ValueText16BytesView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ValueText16LeBytesView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteByteViewResult ValueText16BeBytesView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool SnapshotAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteSnapshotResult GetSnapshot", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult OpenSnapshot", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult RecoverSnapshots", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult CompareSnapshots", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult ExpandedSql", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool NormalizedSqlAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool StatementScanStatusAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool StatementScanStatusV2Available", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult NormalizedSql", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result StatementScanStatusI64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult StatementScanStatusInt", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteDoubleResult StatementScanStatusDouble", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult StatementScanStatusText", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ResetStatementScanStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result StatementScanLoopCount", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result StatementScanVisitCount", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result StatementScanCycleCount", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteDoubleResult StatementScanEstimatedRows", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult StatementScanName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult StatementScanExplain", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult StatementScanSelectId", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult StatementScanParentId", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn i32[min max] StatementStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatus LastErrorCode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult Initialize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult Shutdown", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult InitializeOs", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ShutdownOs", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult GlobalRecoverDeprecated", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn void ThreadCleanupDeprecated", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetSharedCacheEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteUtf16Result ErrorMessage16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetBusyTimeout", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn System.C.c_int sqlite3_config", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn void sqlite3_log", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_memory_alarm", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn rawmutptr<System.C.c_char> sqlite3_mprintf", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_char> sqlite3_vmprintf", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn rawmutptr<System.C.c_char> sqlite3_snprintf", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_char> sqlite3_vsnprintf", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn void sqlite3_str_appendf", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_str_vappendf", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn System.C.c_int sqlite3_test_control", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureSingleThreadMode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureMultiThreadMode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureSerializedMode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetConfigLog<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearConfigLog", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetMemoryAlarmDeprecated", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearMemoryAlarmDeprecated", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult LogMessage", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FormatSqlTextLiteral", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FormatSqlTextLiteralFixed", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn i32[min max] TestControlIsInitialized", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn i32[min max] TestControlByteOrder", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn System.C.c_int sqlite3_db_config", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_load_extension", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_overload_function", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_file_control", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3FileNative> sqlite3_database_file_object", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<SQLite3VfsNative> sqlite3_vfs_find", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vfs_register", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vfs_unregister", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_module(", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_create_module_v2", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_drop_modules", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_declare_vtab", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi varargs fn System.C.c_int sqlite3_vtab_config", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_on_conflict", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_nochange", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_vtab_collation", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_distinct", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_in(", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_rhs_value", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_in_first", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_vtab_in_next", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult SetDatabaseConfigFlag", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetMainDatabaseName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ConfigureLookaside", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ConfigureOwnedLookaside", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult DisableLookaside", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult SetFloatingPointDigits", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult CurrentFloatingPointDigits", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult SetLoadExtensionApiOnly", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetLoadExtensionEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult LoadExtension", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult LoadExtensionWithEntryPoint", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult OverloadFunction", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult DisableLoadExtensions", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult FileControlRaw", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult FileControlLockState", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult FileControlDataVersion", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteFileObjectResult FileControlFileObject", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteFileObjectResult FileControlJournalObject", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult FileControlSetSizeHint", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult FileControlSetChunkSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result FileControlCurrentSizeLimit", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result FileControlSetSizeLimit", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult FileControlPersistentWal", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult FileControlSetPersistentWal", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult FileControlPowerSafeOverwrite", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult FileControlSetPowerSafeOverwrite", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FileControlVfsName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FileControlTempFilename", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result FileControlCurrentMmapSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteI64Result FileControlSetMmapSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult FileControlHasMoved", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteVfsResult FileControlVfs", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult FileControlBeginAtomicWrite", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult FileControlCommitAtomicWrite", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult FileControlRollbackAtomicWrite", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult FileControlSetLockTimeout", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult FileControlExternalReader", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult FileControlResetCache", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteFileObjectResult DatabaseFileObjectFromVfsFilename", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteVfsResult DefaultVfs", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteVfsResult FindVfs", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterVfsView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult UnregisterVfsView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteVirtualTableModuleResult VirtualTableModuleFromRaw", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterVirtualTableModuleViewNoDestructor<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterVirtualTableModuleView<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult UnregisterVirtualTableModule", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult DropAllVirtualTableModules", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult DeclareVirtualTable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult DeclareVirtualTableForCallback", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureVirtualTableConstraintSupport", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureVirtualTableDirectOnly", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureVirtualTableInnocuous", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureVirtualTableUsesAllSchemas", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteIntResult VirtualTableConflictPolicy", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult VirtualTableNoChange", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteTextResult VirtualTableConstraintCollation", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteIntResult VirtualTableDistinctMode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult VirtualTableInCanProcessAllAtOnce", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult SetVirtualTableInAllAtOnce", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteValueResult VirtualTableRightHandSideValue", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteValueStepResult VirtualTableInFirst", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteValueStepResult VirtualTableInNext", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetLockTimeout", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult CurrentLimit", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult DatabaseStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult DatabaseStatus32", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult GlobalStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult GlobalStatus32", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteMutexResult AllocateMutex", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteMutexViewResult DatabaseMutex", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult EnterMutex", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult TryEnterMutex", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult LeaveMutex", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult EnterMutexView", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool MutexHeldAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn bool MutexNotHeldAvailable", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult IsMutexHeld", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult IsMutexNotHeld", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult IsMutexViewHeld", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult IsMutexViewNotHeld", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStringBuilderResult CreateStringBuilder", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStringBuilderResult CreateStringBuilderForDatabase", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult StringBuilderAppend", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult StringBuilderAppendPrefix", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult StringBuilderAppendSqlTextLiteral", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult StringBuilderAppendRepeatedAsciiByte", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult StringBuilderValue", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult FinishStringBuilder", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn i64[min max] MemoryUsed", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn void SetSoftHeapLimitDeprecated", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBytesResult AllocateBytes(", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBytesResult AllocateBytes64(", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ResizeBytes(", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult ResizeBytes64(", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn u64[0 max] AllocatedByteSize(", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult Randomness", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult GlobMatches", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterScalarFunction", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterCollation", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterCollationWithUserDataNoDestructor<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterCollationWithUserData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterCollationUtf16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterCollationUtf16AsciiWithUserData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearCollation", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearCollationUtf16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetCollationNeeded", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetCollationNeededWithUserData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetCollationNeededUtf16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetCollationNeededUtf16WithUserData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearCollationNeeded", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearCollationNeededUtf16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterAggregateFunction", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterAggregateFunctionWithUserDataNoDestructor<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterAggregateFunctionWithUserData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterWindowFunction", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterWindowFunctionWithUserDataNoDestructor<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterWindowFunctionWithUserData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn rawmutptr<SQLite3ValueNative> FunctionArgument", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn rawmutptr<System.C.c_void> AggregateContext", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn rawmutptr<System.C.c_void> ExistingAggregateContext", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn i32[min max] AggregateCountDeprecated", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_set_errmsg", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetErrorMessage", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult SetDefaultErrorMessage", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_busy_handler", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_set_authorizer", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_trace_v2", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_commit_hook", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_update_hook", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_preupdate_hook", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetBusyHandler<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetAuthorizer<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetTraceHandler<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetProgressHandler<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetCommitHook<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetRollbackHook<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetAutovacuumPages<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetUpdateHook<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetWalHook<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetUnlockNotify<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetPreupdateHook<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLitePreupdateValueResult PreupdateOldValue", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLitePreupdateValueResult PreupdateNewValue", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_get_clientdata", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_set_clientdata", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_auto_extension", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_cancel_auto_extension", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn void sqlite3_reset_auto_extension", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterAutoExtension", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn bool CancelAutoExtension", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn void ResetAutoExtensions", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("rawptr<i8[min max]> destroy", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("(rawptr<i8[min max]>)destroy", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe ffi(c) fn System.C.c_int sqlite3_bind_pointer", rawSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLitePointerTypeResult CreatePointerType", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteClientDataKeyResult CreateClientDataKey", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteMutablePointerResult ClientData", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetClientDataNoDestructor", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetClientData<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("storeborrow mut T data", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearClientDataNoDestructor", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult BindPointerNoDestructor", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult BindPointer<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn void SetFunctionAuxDataWithDestructor", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteTextResult ValueText", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteIntResult ValueBlobLength", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult ValueFromBind", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLitePointerResult ValuePointerWithType", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultText", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultPointer", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultPointerWithDestructor", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultValue", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("System.C.FromAscii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("System.C.ToAscii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetMemoryMethods", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ReadMemoryMethods", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetMutexMethods", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ReadMutexMethods", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetPcacheMethods2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ReadPcacheMethods2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetSmallMallocHint", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetMemoryStatisticsEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureDefaultLookaside", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult DisableDefaultLookaside", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureDefaultPageCacheHeap", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureDefaultPageCacheMemory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearDefaultPageCache", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ConfigureHeapMemory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearHeapMemory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetUriHandlingEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetCoveringIndexScanEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetDefaultMmapSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetWin32HeapSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteIntResult PageCacheHeaderSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetPmaSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetStatementJournalSpill", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetSorterReferenceSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetMemoryDatabaseMaxSize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult RowIdInViewEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult SetRowIdInViewEnabled", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult SetSqlLog<T>", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ClearSqlLog", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLITE_CONFIG_MALLOC", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLITE_CONFIG_GETMALLOC", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLITE_CONFIG_MUTEX", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLITE_CONFIG_GETMUTEX", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLITE_CONFIG_PCACHE2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLITE_CONFIG_GETPCACHE2", coreSourceText, StringComparison.Ordinal);

        var typesSourceText = await File.ReadAllTextAsync(sqliteTypesSource);
        Assert.Contains("public struct SQLite3ContextNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3ValueNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteExtensionApiRoutine = fnptr<unsafe ffi(c) fn void()>;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLite3ApiRoutinesNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3ApiRoutinesNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLiteExtensionApiRoutine AggregateContext;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLiteExtensionApiRoutine CreateWindowFunction;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLiteExtensionApiRoutine DbStatus64;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLiteExtensionApiRoutine CArrayBindV2;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteExtensionDatabase = rawmutptr<i8[min max]>;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteExtensionErrorMessagePointer = rawmutptr<System.C.c_char>;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteExtensionApi = rawptr<SQLite3ApiRoutinesNative>;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteLoadExtensionEntry = fnptr<unsafe ffi(c) fn System.C.c_int(", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("rawmutptr<SQLiteExtensionErrorMessagePointer>", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteAutoExtensionCallback = SQLiteLoadExtensionEntry;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3PcacheNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteScalarCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteAggregateStepCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteAggregateFinalCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteWindowValueCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteWindowInverseCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLitePointerDestructor = fnptr<unsafe ffi(c) fn void(rawmutptr<i8[min max]>)>", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteClientDataDestructor = fnptr<unsafe ffi(c) fn void(rawmutptr<i8[min max]>)>", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteLogCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteSqlLogCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteMemoryAlarmCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteCollationCompareCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteCollationNeededCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteCollationNeeded16Callback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteAutoExtensionCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteBusyCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteAuthorizerCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteTraceV2Callback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteProgressCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteCommitCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteRollbackCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteAutovacuumPagesCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteUpdateCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteWalCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteUnlockNotifyCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLitePreupdateCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteMemoryMallocCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteMutexAllocCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteIoCloseCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteIoSharedMemoryMapCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteVfsOpenCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteVfsDynamicLibrarySymbolCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteVfsSetSystemCallCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLitePcacheFetchCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLite3VfsNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLiteMemoryMethods", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLiteMutexMethods", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLiteIoMethods", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLitePcachePage", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("[StructLayout(C)]\npublic struct SQLitePcacheMethods2", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLiteVfsNextSystemCallCallback NextSystemCall;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLiteIoFetchCallback Fetch;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("SQLitePcacheRekeyCallback Rekey;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Database", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Statement", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Blob", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Backup", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal struct SQLite3SnapshotNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteSnapshot", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_free_table", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void stark_sqlite_snapshot_free", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_free_filename", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_mutex_free", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_str_free", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteTable", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteFilename", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteOwnedBytes", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteByteView", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteCArrayBlob", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal struct SQLiteCArrayBlobInput", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteOwnedValue", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteMutex", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteMutexView", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteFileObjectView", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteVfsView", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3ModuleNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3VirtualTableNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3VirtualTableCursorNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3IndexInfoNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteVirtualTableModuleDestructor", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteVirtualTableModuleView", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteStringBuilder", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLitePointerType", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteClientDataKey", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3Native> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3StatementNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3BlobNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3BackupNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3MutexNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3FileNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3VfsNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawptr<SQLite3ModuleNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3StringNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteStatementExplainMode", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBlobResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBackupResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteTableResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteFilenameResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBytesResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteByteViewResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteOwnedValueResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteMutexResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteMutexViewResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteFileObjectResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteVfsResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteVirtualTableModuleResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteStringBuilderResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLitePointerTypeResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteClientDataKeyResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLitePointerResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteMutablePointerResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteWalCheckpointResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteSnapshotResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteUtf16Result", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBoolResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteI64Result", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteDoubleResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteValueResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteValueStepResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteColumnMetadata", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteIntResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteStatusSnapshot", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_CHECKPOINT_NOOP", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_SERIALIZE_NOCOPY", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_DESERIALIZE_FREEONCLOSE", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_LIMIT_VARIABLE_NUMBER", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_TXN_NONE", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_STATUS_MEMORY_USED", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_DBSTATUS_SCHEMA_USED", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_UTF8", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_DETERMINISTIC", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_DIRECTONLY", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_INNOCUOUS", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_OPEN_WAL", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_SCANSTAT_NLOOP", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_PREPARE_PERSISTENT", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public const SQLITE_STMTSTATUS_VM_STEP", typesSourceText, StringComparison.Ordinal);

        foreach (var (name, value) in new (string Name, int Value)[]
        {
            ("SQLITE_ERROR_MISSING_COLLSEQ", 257),
            ("SQLITE_ERROR_RETRY", 513),
            ("SQLITE_ERROR_SNAPSHOT", 769),
            ("SQLITE_ERROR_RESERVESIZE", 1025),
            ("SQLITE_ERROR_KEY", 1281),
            ("SQLITE_ERROR_UNABLE", 1537),
            ("SQLITE_IOERR_READ", 266),
            ("SQLITE_IOERR_SHORT_READ", 522),
            ("SQLITE_IOERR_WRITE", 778),
            ("SQLITE_IOERR_FSYNC", 1034),
            ("SQLITE_IOERR_DIR_FSYNC", 1290),
            ("SQLITE_IOERR_TRUNCATE", 1546),
            ("SQLITE_IOERR_FSTAT", 1802),
            ("SQLITE_IOERR_UNLOCK", 2058),
            ("SQLITE_IOERR_RDLOCK", 2314),
            ("SQLITE_IOERR_DELETE", 2570),
            ("SQLITE_IOERR_BLOCKED", 2826),
            ("SQLITE_IOERR_NOMEM", 3082),
            ("SQLITE_IOERR_ACCESS", 3338),
            ("SQLITE_IOERR_CHECKRESERVEDLOCK", 3594),
            ("SQLITE_IOERR_LOCK", 3850),
            ("SQLITE_IOERR_CLOSE", 4106),
            ("SQLITE_IOERR_DIR_CLOSE", 4362),
            ("SQLITE_IOERR_SHMOPEN", 4618),
            ("SQLITE_IOERR_SHMSIZE", 4874),
            ("SQLITE_IOERR_SHMLOCK", 5130),
            ("SQLITE_IOERR_SHMMAP", 5386),
            ("SQLITE_IOERR_SEEK", 5642),
            ("SQLITE_IOERR_DELETE_NOENT", 5898),
            ("SQLITE_IOERR_MMAP", 6154),
            ("SQLITE_IOERR_GETTEMPPATH", 6410),
            ("SQLITE_IOERR_CONVPATH", 6666),
            ("SQLITE_IOERR_VNODE", 6922),
            ("SQLITE_IOERR_AUTH", 7178),
            ("SQLITE_IOERR_BEGIN_ATOMIC", 7434),
            ("SQLITE_IOERR_COMMIT_ATOMIC", 7690),
            ("SQLITE_IOERR_ROLLBACK_ATOMIC", 7946),
            ("SQLITE_IOERR_DATA", 8202),
            ("SQLITE_IOERR_CORRUPTFS", 8458),
            ("SQLITE_IOERR_IN_PAGE", 8714),
            ("SQLITE_IOERR_BADKEY", 8970),
            ("SQLITE_IOERR_CODEC", 9226),
            ("SQLITE_LOCKED_SHAREDCACHE", 262),
            ("SQLITE_LOCKED_VTAB", 518),
            ("SQLITE_BUSY_RECOVERY", 261),
            ("SQLITE_BUSY_SNAPSHOT", 517),
            ("SQLITE_BUSY_TIMEOUT", 773),
            ("SQLITE_CANTOPEN_NOTEMPDIR", 270),
            ("SQLITE_CANTOPEN_ISDIR", 526),
            ("SQLITE_CANTOPEN_FULLPATH", 782),
            ("SQLITE_CANTOPEN_CONVPATH", 1038),
            ("SQLITE_CANTOPEN_DIRTYWAL", 1294),
            ("SQLITE_CANTOPEN_SYMLINK", 1550),
            ("SQLITE_CORRUPT_VTAB", 267),
            ("SQLITE_CORRUPT_SEQUENCE", 523),
            ("SQLITE_CORRUPT_INDEX", 779),
            ("SQLITE_READONLY_RECOVERY", 264),
            ("SQLITE_READONLY_CANTLOCK", 520),
            ("SQLITE_READONLY_ROLLBACK", 776),
            ("SQLITE_READONLY_DBMOVED", 1032),
            ("SQLITE_READONLY_CANTINIT", 1288),
            ("SQLITE_READONLY_DIRECTORY", 1544),
            ("SQLITE_ABORT_ROLLBACK", 516),
            ("SQLITE_CONSTRAINT_CHECK", 275),
            ("SQLITE_CONSTRAINT_COMMITHOOK", 531),
            ("SQLITE_CONSTRAINT_FOREIGNKEY", 787),
            ("SQLITE_CONSTRAINT_FUNCTION", 1043),
            ("SQLITE_CONSTRAINT_NOTNULL", 1299),
            ("SQLITE_CONSTRAINT_PRIMARYKEY", 1555),
            ("SQLITE_CONSTRAINT_TRIGGER", 1811),
            ("SQLITE_CONSTRAINT_UNIQUE", 2067),
            ("SQLITE_CONSTRAINT_VTAB", 2323),
            ("SQLITE_CONSTRAINT_ROWID", 2579),
            ("SQLITE_CONSTRAINT_PINNED", 2835),
            ("SQLITE_CONSTRAINT_DATATYPE", 3091),
            ("SQLITE_NOTICE_RECOVER_WAL", 283),
            ("SQLITE_NOTICE_RECOVER_ROLLBACK", 539),
            ("SQLITE_NOTICE_RBU", 795),
            ("SQLITE_WARNING_AUTOINDEX", 284),
            ("SQLITE_AUTH_USER", 279),
            ("SQLITE_OK_LOAD_PERMANENTLY", 256),
            ("SQLITE_OK_SYMLINK", 512),
            ("SQLITE_ACCESS_EXISTS", 0),
            ("SQLITE_ACCESS_READWRITE", 1),
            ("SQLITE_ACCESS_READ", 2),
            ("SQLITE_LOCK_NONE", 0),
            ("SQLITE_LOCK_SHARED", 1),
            ("SQLITE_LOCK_RESERVED", 2),
            ("SQLITE_LOCK_PENDING", 3),
            ("SQLITE_LOCK_EXCLUSIVE", 4),
            ("SQLITE_DENY", 1),
            ("SQLITE_IGNORE", 2),
            ("SQLITE_CREATE_INDEX", 1),
            ("SQLITE_CREATE_TABLE", 2),
            ("SQLITE_CREATE_TEMP_INDEX", 3),
            ("SQLITE_CREATE_TEMP_TABLE", 4),
            ("SQLITE_CREATE_TEMP_TRIGGER", 5),
            ("SQLITE_CREATE_TEMP_VIEW", 6),
            ("SQLITE_CREATE_TRIGGER", 7),
            ("SQLITE_CREATE_VIEW", 8),
            ("SQLITE_DELETE", 9),
            ("SQLITE_DROP_INDEX", 10),
            ("SQLITE_DROP_TABLE", 11),
            ("SQLITE_DROP_TEMP_INDEX", 12),
            ("SQLITE_DROP_TEMP_TABLE", 13),
            ("SQLITE_DROP_TEMP_TRIGGER", 14),
            ("SQLITE_DROP_TEMP_VIEW", 15),
            ("SQLITE_DROP_TRIGGER", 16),
            ("SQLITE_DROP_VIEW", 17),
            ("SQLITE_INSERT", 18),
            ("SQLITE_PRAGMA", 19),
            ("SQLITE_READ", 20),
            ("SQLITE_SELECT", 21),
            ("SQLITE_TRANSACTION", 22),
            ("SQLITE_UPDATE", 23),
            ("SQLITE_ATTACH", 24),
            ("SQLITE_DETACH", 25),
            ("SQLITE_ALTER_TABLE", 26),
            ("SQLITE_REINDEX", 27),
            ("SQLITE_ANALYZE", 28),
            ("SQLITE_CREATE_VTABLE", 29),
            ("SQLITE_DROP_VTABLE", 30),
            ("SQLITE_FUNCTION", 31),
            ("SQLITE_SAVEPOINT", 32),
            ("SQLITE_COPY", 0),
            ("SQLITE_RECURSIVE", 33),
            ("SQLITE_ROLLBACK", 1),
            ("SQLITE_FAIL", 3),
            ("SQLITE_REPLACE", 5),
            ("SQLITE_TRACE_STMT", 1),
            ("SQLITE_TRACE_PROFILE", 2),
            ("SQLITE_TRACE_ROW", 4),
            ("SQLITE_TRACE_CLOSE", 8),
            ("SQLITE_CONFIG_SINGLETHREAD", 1),
            ("SQLITE_CONFIG_MULTITHREAD", 2),
            ("SQLITE_CONFIG_SERIALIZED", 3),
            ("SQLITE_CONFIG_MALLOC", 4),
            ("SQLITE_CONFIG_GETMALLOC", 5),
            ("SQLITE_CONFIG_SCRATCH", 6),
            ("SQLITE_CONFIG_PAGECACHE", 7),
            ("SQLITE_CONFIG_HEAP", 8),
            ("SQLITE_CONFIG_MEMSTATUS", 9),
            ("SQLITE_CONFIG_MUTEX", 10),
            ("SQLITE_CONFIG_GETMUTEX", 11),
            ("SQLITE_CONFIG_LOOKASIDE", 13),
            ("SQLITE_CONFIG_PCACHE", 14),
            ("SQLITE_CONFIG_GETPCACHE", 15),
            ("SQLITE_CONFIG_LOG", 16),
            ("SQLITE_CONFIG_URI", 17),
            ("SQLITE_CONFIG_PCACHE2", 18),
            ("SQLITE_CONFIG_GETPCACHE2", 19),
            ("SQLITE_CONFIG_COVERING_INDEX_SCAN", 20),
            ("SQLITE_CONFIG_SQLLOG", 21),
            ("SQLITE_CONFIG_MMAP_SIZE", 22),
            ("SQLITE_CONFIG_WIN32_HEAPSIZE", 23),
            ("SQLITE_CONFIG_PCACHE_HDRSZ", 24),
            ("SQLITE_CONFIG_PMASZ", 25),
            ("SQLITE_CONFIG_STMTJRNL_SPILL", 26),
            ("SQLITE_CONFIG_SMALL_MALLOC", 27),
            ("SQLITE_CONFIG_SORTERREF_SIZE", 28),
            ("SQLITE_CONFIG_MEMDB_MAXSIZE", 29),
            ("SQLITE_CONFIG_ROWID_IN_VIEW", 30),
            ("SQLITE_DBCONFIG_MAINDBNAME", 1000),
            ("SQLITE_DBCONFIG_LOOKASIDE", 1001),
            ("SQLITE_DBCONFIG_ENABLE_FKEY", 1002),
            ("SQLITE_DBCONFIG_ENABLE_TRIGGER", 1003),
            ("SQLITE_DBCONFIG_ENABLE_FTS3_TOKENIZER", 1004),
            ("SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION", 1005),
            ("SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE", 1006),
            ("SQLITE_DBCONFIG_ENABLE_QPSG", 1007),
            ("SQLITE_DBCONFIG_TRIGGER_EQP", 1008),
            ("SQLITE_DBCONFIG_RESET_DATABASE", 1009),
            ("SQLITE_DBCONFIG_DEFENSIVE", 1010),
            ("SQLITE_DBCONFIG_WRITABLE_SCHEMA", 1011),
            ("SQLITE_DBCONFIG_LEGACY_ALTER_TABLE", 1012),
            ("SQLITE_DBCONFIG_DQS_DML", 1013),
            ("SQLITE_DBCONFIG_DQS_DDL", 1014),
            ("SQLITE_DBCONFIG_ENABLE_VIEW", 1015),
            ("SQLITE_DBCONFIG_LEGACY_FILE_FORMAT", 1016),
            ("SQLITE_DBCONFIG_TRUSTED_SCHEMA", 1017),
            ("SQLITE_DBCONFIG_STMT_SCANSTATUS", 1018),
            ("SQLITE_DBCONFIG_REVERSE_SCANORDER", 1019),
            ("SQLITE_DBCONFIG_ENABLE_ATTACH_CREATE", 1020),
            ("SQLITE_DBCONFIG_ENABLE_ATTACH_WRITE", 1021),
            ("SQLITE_DBCONFIG_ENABLE_COMMENTS", 1022),
            ("SQLITE_DBCONFIG_FP_DIGITS", 1023),
            ("SQLITE_DBCONFIG_MAX", 1023),
            ("SQLITE_FCNTL_LOCKSTATE", 1),
            ("SQLITE_FCNTL_GET_LOCKPROXYFILE", 2),
            ("SQLITE_FCNTL_SET_LOCKPROXYFILE", 3),
            ("SQLITE_FCNTL_LAST_ERRNO", 4),
            ("SQLITE_FCNTL_SIZE_HINT", 5),
            ("SQLITE_FCNTL_CHUNK_SIZE", 6),
            ("SQLITE_FCNTL_FILE_POINTER", 7),
            ("SQLITE_FCNTL_SYNC_OMITTED", 8),
            ("SQLITE_FCNTL_WIN32_AV_RETRY", 9),
            ("SQLITE_FCNTL_PERSIST_WAL", 10),
            ("SQLITE_FCNTL_OVERWRITE", 11),
            ("SQLITE_FCNTL_VFSNAME", 12),
            ("SQLITE_FCNTL_POWERSAFE_OVERWRITE", 13),
            ("SQLITE_FCNTL_PRAGMA", 14),
            ("SQLITE_FCNTL_BUSYHANDLER", 15),
            ("SQLITE_FCNTL_TEMPFILENAME", 16),
            ("SQLITE_FCNTL_MMAP_SIZE", 18),
            ("SQLITE_FCNTL_TRACE", 19),
            ("SQLITE_FCNTL_HAS_MOVED", 20),
            ("SQLITE_FCNTL_SYNC", 21),
            ("SQLITE_FCNTL_COMMIT_PHASETWO", 22),
            ("SQLITE_FCNTL_WIN32_SET_HANDLE", 23),
            ("SQLITE_FCNTL_WAL_BLOCK", 24),
            ("SQLITE_FCNTL_ZIPVFS", 25),
            ("SQLITE_FCNTL_RBU", 26),
            ("SQLITE_FCNTL_VFS_POINTER", 27),
            ("SQLITE_FCNTL_JOURNAL_POINTER", 28),
            ("SQLITE_FCNTL_WIN32_GET_HANDLE", 29),
            ("SQLITE_FCNTL_PDB", 30),
            ("SQLITE_FCNTL_BEGIN_ATOMIC_WRITE", 31),
            ("SQLITE_FCNTL_COMMIT_ATOMIC_WRITE", 32),
            ("SQLITE_FCNTL_ROLLBACK_ATOMIC_WRITE", 33),
            ("SQLITE_FCNTL_LOCK_TIMEOUT", 34),
            ("SQLITE_FCNTL_DATA_VERSION", 35),
            ("SQLITE_FCNTL_SIZE_LIMIT", 36),
            ("SQLITE_FCNTL_CKPT_DONE", 37),
            ("SQLITE_FCNTL_RESERVE_BYTES", 38),
            ("SQLITE_FCNTL_CKPT_START", 39),
            ("SQLITE_FCNTL_EXTERNAL_READER", 40),
            ("SQLITE_FCNTL_CKSM_FILE", 41),
            ("SQLITE_FCNTL_RESET_CACHE", 42),
            ("SQLITE_FCNTL_NULL_IO", 43),
            ("SQLITE_FCNTL_BLOCK_ON_CONNECT", 44),
            ("SQLITE_FCNTL_FILESTAT", 45),
            ("SQLITE_SYNC_NORMAL", 2),
            ("SQLITE_SYNC_FULL", 3),
            ("SQLITE_SYNC_DATAONLY", 16),
            ("SQLITE_SHM_UNLOCK", 1),
            ("SQLITE_SHM_LOCK", 2),
            ("SQLITE_SHM_SHARED", 4),
            ("SQLITE_SHM_EXCLUSIVE", 8),
            ("SQLITE_SHM_NLOCK", 8),
            ("SQLITE_IOCAP_ATOMIC", 1),
            ("SQLITE_IOCAP_ATOMIC512", 2),
            ("SQLITE_IOCAP_ATOMIC1K", 4),
            ("SQLITE_IOCAP_ATOMIC2K", 8),
            ("SQLITE_IOCAP_ATOMIC4K", 16),
            ("SQLITE_IOCAP_ATOMIC8K", 32),
            ("SQLITE_IOCAP_ATOMIC16K", 64),
            ("SQLITE_IOCAP_ATOMIC32K", 128),
            ("SQLITE_IOCAP_ATOMIC64K", 256),
            ("SQLITE_IOCAP_SAFE_APPEND", 512),
            ("SQLITE_IOCAP_SEQUENTIAL", 1024),
            ("SQLITE_IOCAP_UNDELETABLE_WHEN_OPEN", 2048),
            ("SQLITE_IOCAP_POWERSAFE_OVERWRITE", 4096),
            ("SQLITE_IOCAP_IMMUTABLE", 8192),
            ("SQLITE_IOCAP_BATCH_ATOMIC", 16384),
            ("SQLITE_IOCAP_SUBPAGE_READ", 32768),
            ("SQLITE_INDEX_SCAN_UNIQUE", 1),
            ("SQLITE_INDEX_SCAN_HEX", 2),
            ("SQLITE_INDEX_CONSTRAINT_EQ", 2),
            ("SQLITE_INDEX_CONSTRAINT_GT", 4),
            ("SQLITE_INDEX_CONSTRAINT_LE", 8),
            ("SQLITE_INDEX_CONSTRAINT_LT", 16),
            ("SQLITE_INDEX_CONSTRAINT_GE", 32),
            ("SQLITE_INDEX_CONSTRAINT_MATCH", 64),
            ("SQLITE_INDEX_CONSTRAINT_LIKE", 65),
            ("SQLITE_INDEX_CONSTRAINT_GLOB", 66),
            ("SQLITE_INDEX_CONSTRAINT_REGEXP", 67),
            ("SQLITE_INDEX_CONSTRAINT_NE", 68),
            ("SQLITE_INDEX_CONSTRAINT_ISNOT", 69),
            ("SQLITE_INDEX_CONSTRAINT_ISNOTNULL", 70),
            ("SQLITE_INDEX_CONSTRAINT_ISNULL", 71),
            ("SQLITE_INDEX_CONSTRAINT_IS", 72),
            ("SQLITE_INDEX_CONSTRAINT_LIMIT", 73),
            ("SQLITE_INDEX_CONSTRAINT_OFFSET", 74),
            ("SQLITE_INDEX_CONSTRAINT_FUNCTION", 150),
            ("SQLITE_MUTEX_FAST", 0),
            ("SQLITE_MUTEX_RECURSIVE", 1),
            ("SQLITE_MUTEX_STATIC_MAIN", 2),
            ("SQLITE_MUTEX_STATIC_MEM", 3),
            ("SQLITE_MUTEX_STATIC_MEM2", 4),
            ("SQLITE_MUTEX_STATIC_OPEN", 4),
            ("SQLITE_MUTEX_STATIC_PRNG", 5),
            ("SQLITE_MUTEX_STATIC_LRU", 6),
            ("SQLITE_MUTEX_STATIC_LRU2", 7),
            ("SQLITE_MUTEX_STATIC_PMEM", 7),
            ("SQLITE_MUTEX_STATIC_APP1", 8),
            ("SQLITE_MUTEX_STATIC_APP2", 9),
            ("SQLITE_MUTEX_STATIC_APP3", 10),
            ("SQLITE_MUTEX_STATIC_VFS1", 11),
            ("SQLITE_MUTEX_STATIC_VFS2", 12),
            ("SQLITE_MUTEX_STATIC_VFS3", 13),
            ("SQLITE_VERSION_NUMBER", 3053002),
            ("SQLITE_SETLK_BLOCK_ON_CONNECT", 1),
            ("SQLITE_ANY", 5),
            ("SQLITE_STATIC", 0),
            ("SQLITE_WIN32_DATA_DIRECTORY_TYPE", 1),
            ("SQLITE_WIN32_TEMP_DIRECTORY_TYPE", 2),
            ("SQLITE_TESTCTRL_FIRST", 5),
            ("SQLITE_TESTCTRL_PRNG_SAVE", 5),
            ("SQLITE_TESTCTRL_PRNG_RESTORE", 6),
            ("SQLITE_TESTCTRL_PRNG_RESET", 7),
            ("SQLITE_TESTCTRL_FK_NO_ACTION", 7),
            ("SQLITE_TESTCTRL_BITVEC_TEST", 8),
            ("SQLITE_TESTCTRL_FAULT_INSTALL", 9),
            ("SQLITE_TESTCTRL_BENIGN_MALLOC_HOOKS", 10),
            ("SQLITE_TESTCTRL_PENDING_BYTE", 11),
            ("SQLITE_TESTCTRL_ASSERT", 12),
            ("SQLITE_TESTCTRL_ALWAYS", 13),
            ("SQLITE_TESTCTRL_RESERVE", 14),
            ("SQLITE_TESTCTRL_JSON_SELFCHECK", 14),
            ("SQLITE_TESTCTRL_OPTIMIZATIONS", 15),
            ("SQLITE_TESTCTRL_ISKEYWORD", 16),
            ("SQLITE_TESTCTRL_GETOPT", 16),
            ("SQLITE_TESTCTRL_SCRATCHMALLOC", 17),
            ("SQLITE_TESTCTRL_INTERNAL_FUNCTIONS", 17),
            ("SQLITE_TESTCTRL_LOCALTIME_FAULT", 18),
            ("SQLITE_TESTCTRL_EXPLAIN_STMT", 19),
            ("SQLITE_TESTCTRL_ONCE_RESET_THRESHOLD", 19),
            ("SQLITE_TESTCTRL_NEVER_CORRUPT", 20),
            ("SQLITE_TESTCTRL_VDBE_COVERAGE", 21),
            ("SQLITE_TESTCTRL_BYTEORDER", 22),
            ("SQLITE_TESTCTRL_ISINIT", 23),
            ("SQLITE_TESTCTRL_SORTER_MMAP", 24),
            ("SQLITE_TESTCTRL_IMPOSTER", 25),
            ("SQLITE_TESTCTRL_PARSER_COVERAGE", 26),
            ("SQLITE_TESTCTRL_RESULT_INTREAL", 27),
            ("SQLITE_TESTCTRL_PRNG_SEED", 28),
            ("SQLITE_TESTCTRL_EXTRA_SCHEMA_CHECKS", 29),
            ("SQLITE_TESTCTRL_SEEK_COUNT", 30),
            ("SQLITE_TESTCTRL_TRACEFLAGS", 31),
            ("SQLITE_TESTCTRL_TUNE", 32),
            ("SQLITE_TESTCTRL_LOGEST", 33),
            ("SQLITE_TESTCTRL_USELONGDOUBLE", 34),
            ("SQLITE_TESTCTRL_ATOF", 34),
            ("SQLITE_TESTCTRL_LAST", 34),
            ("SQLITE_VTAB_CONSTRAINT_SUPPORT", 1),
            ("SQLITE_VTAB_INNOCUOUS", 2),
            ("SQLITE_VTAB_DIRECTONLY", 3),
            ("SQLITE_VTAB_USES_ALL_SCHEMAS", 4),
            ("SQLITE_CARRAY_INT32", 0),
            ("SQLITE_CARRAY_INT64", 1),
            ("SQLITE_CARRAY_DOUBLE", 2),
            ("SQLITE_CARRAY_TEXT", 3),
            ("SQLITE_CARRAY_BLOB", 4),
        })
        {
            Assert.Contains($"public const {name} = {value};", typesSourceText, StringComparison.Ordinal);
        }

        foreach (var (name, value) in new (string Name, string Value)[]
        {
            ("SQLITE_VERSION", "3.53.2"),
            ("SQLITE_SOURCE_ID", "2026-06-03 19:12:13 d6e03d8c777cfa2d35e3b60d8ec3e0187f3e9f99d8e2ee9cac695fd6fcdfalt1"),
            ("SQLITE_SCM_BRANCH", "branch-3.53"),
            ("SQLITE_SCM_TAGS", "release version-3.53.2"),
            ("SQLITE_SCM_DATETIME", "2026-06-03T19:12:13.350Z"),
        })
        {
            Assert.Contains($"public const ascii {name} = \"{value}\";", typesSourceText, StringComparison.Ordinal);
        }

        await CheckSourceAsync(
            Path.Combine(vendorImportDirectory, "Vendor", "SQLite.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteInMemoryQueries.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "sqlite", "TaskReport.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteCallbacks.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteBinaryData.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteSnapshots.stark"),
            vendorImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task VendorSQLiteBuildsAndRunsThroughPackageOwnedNativeMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows()
            || !await PkgConfigPackageExistsAsync("sqlite3"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var vendorImportDirectory = Path.Combine(repositoryRoot, "vendor", "src");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-vendor-sqlite-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var vendorSQLiteSource = Path.Combine(vendorImportDirectory, "Vendor", "SQLite.stark");
        var sqliteNativeSource = Path.Combine(repositoryRoot, "vendor", "SQLiteTextBinding.c");
        var vendorSQLiteLibrary = Path.Combine(packageDirectory, "libVendorSQLite.a");
        var vendorSQLitePackage = Path.ChangeExtension(vendorSQLiteLibrary, ".starkpkg");
        var sqliteQueriesOutput = Path.Combine(tempDirectory.FullName, "sqlite-in-memory-queries");

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    vendorSQLiteSource,
                    "--emit-lib",
                    "-I", vendorImportDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", vendorSQLiteLibrary,
                    "--native-source", sqliteNativeSource,
                    "--native-pkg-config", "sqlite3",
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(vendorSQLiteLibrary));
            Assert.True(File.Exists(vendorSQLitePackage));

            Assert.True(PackageImageLoader.TryLoadManifest(vendorSQLitePackage, out var manifest));
            Assert.Equal("Vendor.SQLite", manifest.RootModule);
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal("sqlite3", Assert.Single(manifest.NativeDependencies!.PkgConfigPackages!));
            Assert.Equal("SQLiteTextBinding.c", Path.GetFileName(Assert.Single(manifest.NativeDependencies.Sources!)));

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [vendorSQLitePackage, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.True(inspectExitCode == 0, inspectStderr.ToString());
            Assert.Matches(
                "native dependencies: sources=1, .*pkg-config=1",
                inspectStdout.ToString());

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteInMemoryQueries.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", sqliteQueriesOutput,
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(sqliteQueriesOutput));

            var processResult = await RunNativeExecutableAsync(sqliteQueriesOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);

            var reportOutput = Path.Combine(tempDirectory.FullName, "sqlite-task-report");
            var reportCompileStdout = new StringWriter();
            var reportCompileStderr = new StringWriter();
            var reportCompileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "sqlite", "TaskReport.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", reportOutput,
                ],
                new StringReader(string.Empty),
                reportCompileStdout,
                reportCompileStderr);

            Assert.True(reportCompileExitCode == 0, reportCompileStderr.ToString());
            Assert.Contains("Emitted executable:", reportCompileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(reportOutput));

            var reportResult = await RunNativeExecutableAsync(reportOutput);

            Assert.Equal(0, reportResult.ExitCode);
            Assert.Equal(
                "SQLite task report: 3 tasks, 2 complete, priority sum 10\nTop pending task:\ndocument-usage\n",
                reportResult.StandardOutput);
            Assert.Equal(string.Empty, reportResult.StandardError);

            var callbacksOutput = Path.Combine(tempDirectory.FullName, "sqlite-callbacks");
            var callbacksCompileStdout = new StringWriter();
            var callbacksCompileStderr = new StringWriter();
            var callbacksCompileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteCallbacks.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", callbacksOutput,
                ],
                new StringReader(string.Empty),
                callbacksCompileStdout,
                callbacksCompileStderr);

            Assert.True(callbacksCompileExitCode == 0, callbacksCompileStderr.ToString());
            Assert.Contains("Emitted executable:", callbacksCompileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(callbacksOutput));

            var callbacksResult = await RunNativeExecutableAsync(callbacksOutput);

            Assert.Equal(0, callbacksResult.ExitCode);
            Assert.Equal(
                "SQLite callback example:\ntop row = gamma, boosted score = 13\n",
                callbacksResult.StandardOutput);
            Assert.Equal(string.Empty, callbacksResult.StandardError);

            var binaryDataOutput = Path.Combine(tempDirectory.FullName, "sqlite-binary-data");
            var binaryDataCompileStdout = new StringWriter();
            var binaryDataCompileStderr = new StringWriter();
            var binaryDataCompileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteBinaryData.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", binaryDataOutput,
                ],
                new StringReader(string.Empty),
                binaryDataCompileStdout,
                binaryDataCompileStderr);

            Assert.True(binaryDataCompileExitCode == 0, binaryDataCompileStderr.ToString());
            Assert.Contains("Emitted executable:", binaryDataCompileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(binaryDataOutput));

            var binaryDataResult = await RunNativeExecutableAsync(binaryDataOutput);

            Assert.Equal(0, binaryDataResult.ExitCode);
            Assert.Equal(
                "SQLite binary data example:\nstored 4 blob bytes and 5 UTF-8 text bytes\n",
                binaryDataResult.StandardOutput);
            Assert.Equal(string.Empty, binaryDataResult.StandardError);

            var snapshotsOutput = Path.Combine(tempDirectory.FullName, "sqlite-snapshots");
            var snapshotsCompileStdout = new StringWriter();
            var snapshotsCompileStderr = new StringWriter();
            var snapshotsCompileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "sqlite", "SQLiteSnapshots.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", snapshotsOutput,
                ],
                new StringReader(string.Empty),
                snapshotsCompileStdout,
                snapshotsCompileStderr);

            Assert.True(snapshotsCompileExitCode == 0, snapshotsCompileStderr.ToString());
            Assert.Contains("Emitted executable:", snapshotsCompileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(snapshotsOutput));

            var snapshotsResult = await RunNativeExecutableAsync(snapshotsOutput);

            Assert.Equal(0, snapshotsResult.ExitCode);
            Assert.True(
                snapshotsResult.StandardOutput == "SQLite snapshot example:\nsnapshot extension unavailable in this SQLite build\n"
                    || snapshotsResult.StandardOutput == "SQLite snapshot example:\nsnapshot extension available in this SQLite build\n",
                snapshotsResult.StandardOutput);
            Assert.Equal(string.Empty, snapshotsResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task DataModelExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-data-model-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "data-model.exe" : "data-model");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "data-model", "DataModel.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(15, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ArithmeticExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-arithmetic-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "arithmetic.exe" : "arithmetic");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "arithmetic", "Arithmetic.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ControlFlowExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-control-flow-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "control-flow.exe" : "control-flow");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "control-flow", "ControlFlow.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task StaticLibraryExampleBuildsAndRunsFromPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-static-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var facadeSource = Path.Combine(repositoryRoot, "examples", "static-library", "Facade.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var appSource = Path.Combine(appDirectory, "App.stark");
        var appOutput = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await CompileLibraryAsync(facadeSource, libraryPath);

            await File.WriteAllTextAsync(
                appSource,
                """
                import Facade
                module App

                export fn i32[min max] main()
                {
                    return Facade.Quadruple(5);
                }
                """);

            var compileResult = await CompileExecutableAsync(appSource, appOutput, packageDirectory);
            Assert.Contains("Emitted executable:", compileResult.Stdout);

            var processResult = await RunNativeExecutableAsync(appOutput);

            Assert.Equal(20, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static async Task CompileLibraryAsync(string sourcePath, string libraryPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [sourcePath, "--emit-lib", "-o", libraryPath],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(libraryPath));
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task<(string Stdout, string Stderr)> CompileExecutableAsync(
        string sourcePath,
        string outputPath,
        string? libraryDirectory = null)
    {
        var args = new List<string> { sourcePath, "--emit-exe", "-o", outputPath };
        if (libraryDirectory is not null)
        {
            args.Add("-I");
            args.Add(libraryDirectory);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            args.ToArray(),
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        AssertCompilerLogsEmitted(stderr.ToString());
        return (stdout.ToString(), stderr.ToString());
    }

    private static async Task CheckSourceAsync(string sourcePath, params string[] importDirectories)
    {
        var args = new List<string>
        {
            sourcePath,
            "--check",
            "--sdk-root", FindRepositoryRoot(),
            "--no-stark-path"
        };
        foreach (var importDirectory in importDirectories)
        {
            args.Add("-I");
            args.Add(importDirectory);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            args.ToArray(),
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.True(
            exitCode == 0,
            $"Source check failed for '{sourcePath}'."
            + Environment.NewLine
            + stdout
            + Environment.NewLine
            + stderr);
        Assert.Contains("Check succeeded.", stdout.ToString());
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task BuildStdlibPackageAsync(string sourcePath, string libraryPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [sourcePath, "--emit-lib", "-o", libraryPath],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(libraryPath));
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunNativeExecutableAsync(
        string executablePath,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = System.Diagnostics.Process.Start(startInfo);

        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, standardOutput, standardError);
    }
private static async Task<bool> PkgConfigPackageExistsAsync(string packageName)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pkg-config",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--exists");
            startInfo.ArgumentList.Add(packageName);

            using var process = System.Diagnostics.Process.Start(startInfo);
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

    private static async Task<string> CreateUnixCaptureLinkerAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "capture-linker.sh");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > "{{logPath}}"
            out=""
            prev=""
            for arg in "$@"; do
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            : > "$out"
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for example tests.");
    }

    private static void Cleanup(DirectoryInfo tempDirectory)
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

    private static void AssertCompilerLogsEmitted(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.True(
                line.StartsWith("Pass '", StringComparison.Ordinal)
                && line.Contains(" took ", StringComparison.Ordinal)
                && line.Contains("[warn pipeline stage=", StringComparison.Ordinal)
                && line.EndsWith(" outcome=continued]", StringComparison.Ordinal),
                $"Unexpected compiler log: {line}");
        }
    }
}
