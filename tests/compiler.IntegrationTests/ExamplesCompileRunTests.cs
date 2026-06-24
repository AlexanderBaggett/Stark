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

        await CheckSourceAsync(
            Path.Combine(raylibImportDirectory, "VendorRaylibSafeApis.stark"),
            Path.Combine(repositoryRoot, "vendor", "dist"),
            stdlibImportDirectory);
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

        Assert.True(File.Exists(sqliteNativeSource));
        var nativeSourceText = await File.ReadAllTextAsync(sqliteNativeSource);
        Assert.Contains("SQLITE_TRANSIENT", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_text_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_text16_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_text64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_blob_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_bind_blob64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_text_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_text16_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_text64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_blob_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_result_blob64_transient", nativeSourceText, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_function_argument", nativeSourceText, StringComparison.Ordinal);

        var coreSourceText = await File.ReadAllTextAsync(sqliteCoreSource);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_text_transient", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_text16_transient", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_text64_transient", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int stark_sqlite_bind_blob_transient", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_compileoption_used", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_keyword_name", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_complete16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_open16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_prepare16_v3", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_bind_zeroblob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_prepare_v3", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_bind_int", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_bind_parameter_count", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_bind_parameter_name", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_data_count", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_void> sqlite3_column_blob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_void> sqlite3_column_text16", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_column_decltype", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_column_origin_name", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_column_int", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_sql", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<System.C.c_char> sqlite3_expanded_sql", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_transfer_bindings", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_stmt_status", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3Native> sqlite3_db_handle", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_blob_open", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_blob_read", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_blob_write", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3BackupNative> sqlite3_backup_init", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_backup_step", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_malloc64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<u8[0 max]> sqlite3_serialize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_deserialize", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_wal_autocheckpoint", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_wal_checkpoint_v2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn i64[min max] sqlite3_memory_used", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_release_memory", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_randomness", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_stricmp", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_table_column_metadata", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3ValueNative> stark_sqlite_function_argument", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_create_function_v2", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<System.C.c_void> sqlite3_user_data", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3Native> sqlite3_context_db_handle", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3ValueNative> sqlite3_column_value", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_bind_value", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<u8[0 max]> sqlite3_value_text", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_value_frombind", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3ValueNative> sqlite3_value_dup", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_result_error", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn void sqlite3_result_int64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_result_zeroblob64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_errcode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_extended_result_codes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_limit", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawptr<System.C.c_char> sqlite3_db_name", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_txn_state", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn rawmutptr<SQLite3StatementNative> sqlite3_next_stmt", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_status64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_db_status64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn System.C.c_int sqlite3_changes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi(c) fn i64[min max] sqlite3_total_changes64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatementResult PrepareWithFlags", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatementResult PrepareLegacy", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatementResult PrepareUtf16AsciiWithFlags", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteDatabaseResult OpenDefault", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult CloseStrict", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult CompileOptionUsed", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteTextResult KeywordName", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult IsCompleteSqlUtf16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindText64", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindText16Ascii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult BindZeroBlob", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult ColumnBlobCopy", coreSourceText, StringComparison.Ordinal);
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
        Assert.Contains("public fn SQLiteTextResult ExpandedSql", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn i32[min max] StatementStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatus LastErrorCode", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteIntResult CurrentLimit", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult DatabaseStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult GlobalStatus", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusSnapshotResult GlobalStatus32", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn i64[min max] MemoryUsed", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteStatusResult Randomness", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public fn SQLiteBoolResult GlobMatches", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult RegisterScalarFunction", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn rawmutptr<SQLite3ValueNative> FunctionArgument", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteTextResult ValueText", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteIntResult ValueBlobLength", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteBoolResult ValueFromBind", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultText", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultBytes", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("public unsafe fn SQLiteStatusResult ResultValue", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("System.C.FromAscii", coreSourceText, StringComparison.Ordinal);
        Assert.Contains("System.C.ToAscii", coreSourceText, StringComparison.Ordinal);

        var typesSourceText = await File.ReadAllTextAsync(sqliteTypesSource);
        Assert.Contains("public struct SQLite3ContextNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLite3ValueNative", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public alias SQLiteScalarCallback", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Database", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Statement", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Blob", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct Backup", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteOwnedBytes", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public struct SQLiteOwnedValue", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3Native> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3StatementNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3BlobNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<SQLite3BackupNative> Handle;", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteStatementExplainMode", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBlobResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBackupResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBytesResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteOwnedValueResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLitePointerResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteWalCheckpointResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteUtf16Result", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteBoolResult", typesSourceText, StringComparison.Ordinal);
        Assert.Contains("public enum SQLiteI64Result", typesSourceText, StringComparison.Ordinal);
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
        var args = new List<string> { sourcePath, "--check" };
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

        Assert.Equal(0, exitCode);
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
        Assert.Equal(string.Empty, text);
    }
}
