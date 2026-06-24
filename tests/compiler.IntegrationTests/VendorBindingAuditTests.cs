using Stark.Compiler;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace compiler.IntegrationTests;

public sealed partial class VendorBindingAuditTests
{
    private const string CheckedInRaylibTargetTriple = "x86_64-pc-linux-gnu";

    private static readonly VendorBinding[] Bindings =
    [
        new(
            "STB.Image",
            "vendor/src/Vendor/STB/Image.stark",
            "vendor/build-stb-image-package.sh",
            "vendor/dist/libVendorSTBImage.starkpkg",
            "examples/stb-image/StbImageResize.stark",
            ["StbImageImplementation.c"],
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
            "SDL3",
            "vendor/src/Vendor/SDL3.stark",
            "vendor/build-sdl3-package.sh",
            "vendor/dist/libVendorSDL3.starkpkg",
            "examples/sdl3/Sdl3WindowAudio.stark",
            ["Sdl3Binding.c"],
            ["sdl3"]),
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
            $"vendor/dist/{CheckedInRaylibTargetTriple}/libVendorRaylib.starkpkg",
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

    [Fact]
    public void RaylibRelatedHeaderModulesKeepUnsafeAtInternalEdge()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibRootText = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib.stark"));
        var raymathText = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raymath.stark"));
        var rlglText = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Rlgl.stark"));

        Assert.Contains("import Vendor.Raymath", raylibRootText, StringComparison.Ordinal);
        Assert.Contains("import Vendor.Rlgl", raylibRootText, StringComparison.Ordinal);
        Assert.Contains("module Vendor.Raymath", raymathText, StringComparison.Ordinal);
        Assert.Contains("module Vendor.Rlgl", rlglText, StringComparison.Ordinal);

        foreach (var (name, text) in new[] { ("Raymath", raymathText), ("Rlgl", rlglText) })
        {
            Assert.DoesNotContain("System.C", text, StringComparison.Ordinal);
            Assert.DoesNotContain("public unsafe", text, StringComparison.Ordinal);

            var publicRawLines = text.Split('\n')
                .Select((line, index) => (Line: line.TrimStart(), Number: index + 1))
                .Where(entry => PublicRawPointerRegex().IsMatch(entry.Line))
                .Select(entry => $"{name}:{entry.Number}: {entry.Line}")
                .ToArray();

            Assert.Empty(publicRawLines);
        }

        Assert.DoesNotContain("public fn", rlglText.AsSpan(
            rlglText.IndexOf("rlLoadExtensions", StringComparison.Ordinal),
            rlglText.IndexOf("rlGetVersion", StringComparison.Ordinal) - rlglText.IndexOf("rlLoadExtensions", StringComparison.Ordinal)).ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RaymathCoversRaylib60PublicHelperSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raymathText = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raymath.stark"));
        var publicFunctions = ExtractPublicFunctionNames(raymathText);
        var expectedFunctions = new[]
        {
            "Clamp", "Lerp", "Normalize", "Remap", "Wrap", "FloatEquals",
            "Vector2Zero", "Vector2One", "Vector2Add", "Vector2AddValue", "Vector2Subtract", "Vector2SubtractValue",
            "Vector2Length", "Vector2LengthSqr", "Vector2DotProduct", "Vector2CrossProduct", "Vector2Distance",
            "Vector2DistanceSqr", "Vector2Angle", "Vector2LineAngle", "Vector2Scale", "Vector2Multiply",
            "Vector2Negate", "Vector2Divide", "Vector2Normalize", "Vector2Transform", "Vector2Lerp",
            "Vector2Reflect", "Vector2Min", "Vector2Max", "Vector2Rotate", "Vector2MoveTowards",
            "Vector2Invert", "Vector2Clamp", "Vector2ClampValue", "Vector2Equals", "Vector2Refract",
            "Vector3Zero", "Vector3One", "Vector3Add", "Vector3AddValue", "Vector3Subtract", "Vector3SubtractValue",
            "Vector3Scale", "Vector3Multiply", "Vector3CrossProduct", "Vector3Perpendicular", "Vector3Length",
            "Vector3LengthSqr", "Vector3DotProduct", "Vector3Distance", "Vector3DistanceSqr", "Vector3Angle",
            "Vector3Negate", "Vector3Divide", "Vector3Normalize", "Vector3Project", "Vector3Reject",
            "Vector3OrthoNormalize", "Vector3Transform", "Vector3RotateByQuaternion", "Vector3RotateByAxisAngle",
            "Vector3MoveTowards", "Vector3Lerp", "Vector3CubicHermite", "Vector3Reflect", "Vector3Min",
            "Vector3Max", "Vector3Barycenter", "Vector3Unproject", "Vector3ToFloatV", "Vector3Invert",
            "Vector3Clamp", "Vector3ClampValue", "Vector3Equals", "Vector3Refract",
            "Vector4Zero", "Vector4One", "Vector4Add", "Vector4AddValue", "Vector4Subtract", "Vector4SubtractValue",
            "Vector4Length", "Vector4LengthSqr", "Vector4DotProduct", "Vector4Distance", "Vector4DistanceSqr",
            "Vector4Scale", "Vector4Multiply", "Vector4Negate", "Vector4Divide", "Vector4Normalize",
            "Vector4Min", "Vector4Max", "Vector4Lerp", "Vector4MoveTowards", "Vector4Invert", "Vector4Equals",
            "MatrixDeterminant", "MatrixTrace", "MatrixTranspose", "MatrixInvert", "MatrixIdentity", "MatrixAdd",
            "MatrixSubtract", "MatrixMultiply", "MatrixMultiplyValue", "MatrixTranslate", "MatrixRotate",
            "MatrixRotateX", "MatrixRotateY", "MatrixRotateZ", "MatrixRotateXYZ", "MatrixRotateZYX",
            "MatrixScale", "MatrixFrustum", "MatrixPerspective", "MatrixOrtho", "MatrixLookAt", "MatrixToFloatV",
            "QuaternionAdd", "QuaternionAddValue", "QuaternionSubtract", "QuaternionSubtractValue", "QuaternionIdentity",
            "QuaternionLength", "QuaternionNormalize", "QuaternionInvert", "QuaternionMultiply", "QuaternionScale",
            "QuaternionDivide", "QuaternionLerp", "QuaternionNlerp", "QuaternionSlerp", "QuaternionCubicHermiteSpline",
            "QuaternionFromVector3ToVector3", "QuaternionFromMatrix", "QuaternionToMatrix", "QuaternionFromAxisAngle",
            "QuaternionToAxisAngle", "QuaternionFromEuler", "QuaternionToEuler", "QuaternionTransform",
            "QuaternionEquals", "MatrixCompose", "MatrixDecompose"
        };

        Assert.Equal(146, expectedFunctions.Length);
        Assert.Empty(expectedFunctions.Except(publicFunctions, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void RlglCoversRaylib60NativeEntryPointsWithSafePublicWrappers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var rlglText = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Rlgl.stark"));
        var linkNames = ExtractRlglLinkNames(rlglText);
        var publicFunctions = ExtractPublicFunctionNames(rlglText);
        var expectedNativeFunctions = new[]
        {
            "rlMatrixMode", "rlPushMatrix", "rlPopMatrix", "rlLoadIdentity", "rlTranslatef", "rlRotatef", "rlScalef",
            "rlMultMatrixf", "rlFrustum", "rlOrtho", "rlViewport", "rlSetClipPlanes", "rlGetCullDistanceNear",
            "rlGetCullDistanceFar", "rlBegin", "rlEnd", "rlVertex2i", "rlVertex2f", "rlVertex3f", "rlTexCoord2f",
            "rlNormal3f", "rlColor4ub", "rlColor3f", "rlColor4f", "rlEnableVertexArray", "rlDisableVertexArray",
            "rlEnableVertexBuffer", "rlDisableVertexBuffer", "rlEnableVertexBufferElement", "rlDisableVertexBufferElement",
            "rlEnableVertexAttribute", "rlDisableVertexAttribute", "rlEnableStatePointer", "rlDisableStatePointer",
            "rlActiveTextureSlot", "rlEnableTexture", "rlDisableTexture", "rlEnableTextureCubemap",
            "rlDisableTextureCubemap", "rlTextureParameters", "rlCubemapParameters", "rlEnableShader", "rlDisableShader",
            "rlEnableFramebuffer", "rlDisableFramebuffer", "rlGetActiveFramebuffer", "rlActiveDrawBuffers",
            "rlBlitFramebuffer", "rlBindFramebuffer", "rlEnableColorBlend", "rlDisableColorBlend", "rlEnableDepthTest",
            "rlDisableDepthTest", "rlEnableDepthMask", "rlDisableDepthMask", "rlEnableBackfaceCulling",
            "rlDisableBackfaceCulling", "rlColorMask", "rlSetCullFace", "rlEnableScissorTest", "rlDisableScissorTest",
            "rlScissor", "rlEnablePointMode", "rlDisablePointMode", "rlSetPointSize", "rlGetPointSize",
            "rlEnableWireMode", "rlDisableWireMode", "rlSetLineWidth", "rlGetLineWidth", "rlEnableSmoothLines",
            "rlDisableSmoothLines", "rlEnableStereoRender", "rlDisableStereoRender", "rlIsStereoRenderEnabled",
            "rlClearColor", "rlClearScreenBuffers", "rlCheckErrors", "rlSetBlendMode", "rlSetBlendFactors",
            "rlSetBlendFactorsSeparate", "rlglInit", "rlglClose", "rlLoadExtensions", "rlGetProcAddress", "rlGetVersion",
            "rlSetFramebufferWidth", "rlGetFramebufferWidth", "rlSetFramebufferHeight", "rlGetFramebufferHeight",
            "rlGetTextureIdDefault", "rlGetShaderIdDefault", "rlGetShaderLocsDefault", "rlLoadRenderBatch",
            "rlUnloadRenderBatch", "rlDrawRenderBatch", "rlSetRenderBatchActive", "rlDrawRenderBatchActive",
            "rlCheckRenderBatchLimit", "rlSetTexture", "rlLoadVertexArray", "rlLoadVertexBuffer",
            "rlLoadVertexBufferElement", "rlUpdateVertexBuffer", "rlUpdateVertexBufferElements", "rlUnloadVertexArray",
            "rlUnloadVertexBuffer", "rlSetVertexAttribute", "rlSetVertexAttributeDivisor", "rlSetVertexAttributeDefault",
            "rlDrawVertexArray", "rlDrawVertexArrayElements", "rlDrawVertexArrayInstanced",
            "rlDrawVertexArrayElementsInstanced", "rlLoadTexture", "rlLoadTextureDepth", "rlLoadTextureCubemap",
            "rlUpdateTexture", "rlGetGlTextureFormats", "rlGetPixelFormatName", "rlUnloadTexture",
            "rlGenTextureMipmaps", "rlReadTexturePixels", "rlReadScreenPixels", "rlLoadFramebuffer",
            "rlFramebufferAttach", "rlFramebufferComplete", "rlUnloadFramebuffer", "rlCopyFramebuffer",
            "rlResizeFramebuffer", "rlLoadShader", "rlLoadShaderProgram", "rlLoadShaderProgramEx",
            "rlLoadShaderProgramCompute", "rlUnloadShader", "rlUnloadShaderProgram", "rlGetLocationUniform",
            "rlGetLocationAttrib", "rlSetUniform", "rlSetUniformMatrix", "rlSetUniformMatrices", "rlSetUniformSampler",
            "rlSetShader", "rlComputeShaderDispatch", "rlLoadShaderBuffer", "rlUnloadShaderBuffer",
            "rlUpdateShaderBuffer", "rlBindShaderBuffer", "rlReadShaderBuffer", "rlCopyShaderBuffer",
            "rlGetShaderBufferSize", "rlBindImageTexture", "rlGetMatrixModelview", "rlGetMatrixProjection",
            "rlGetMatrixTransform", "rlGetMatrixProjectionStereo", "rlGetMatrixViewOffsetStereo",
            "rlSetMatrixProjection", "rlSetMatrixModelview", "rlSetMatrixProjectionStereo",
            "rlSetMatrixViewOffsetStereo", "rlLoadDrawCube", "rlLoadDrawQuad"
        };

        Assert.Equal(163, expectedNativeFunctions.Length);
        Assert.Empty(expectedNativeFunctions.Except(linkNames, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));

        var intentionallyInternalOnly = new[] { "rlLoadExtensions", "rlGetProcAddress" };
        var expectedSafePublicWrappers = expectedNativeFunctions.Except(intentionallyInternalOnly, StringComparer.Ordinal);
        Assert.Empty(expectedSafePublicWrappers.Except(publicFunctions, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.DoesNotContain("public fn", rlglText.AsSpan(
            rlglText.IndexOf("rlLoadExtensions", StringComparison.Ordinal),
            rlglText.IndexOf("rlGetVersion", StringComparison.Ordinal) - rlglText.IndexOf("rlLoadExtensions", StringComparison.Ordinal)).ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibBundledHeadersMatchRecordedVersionAndInventory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var nativeRaylibRoot = Path.Combine(repositoryRoot, "vendor", "dist", CheckedInRaylibTargetTriple, "native", "raylib");
        var raylibHeader = File.ReadAllText(Path.Combine(nativeRaylibRoot, "raylib.h"));
        var raymathHeader = File.ReadAllText(Path.Combine(nativeRaylibRoot, "raymath.h"));
        var rlglHeader = File.ReadAllText(Path.Combine(nativeRaylibRoot, "rlgl.h"));
        var bindingText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib"), "*.stark")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        bindingText += Environment.NewLine + File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib.stark"));

        Assert.Contains("#define RAYLIB_VERSION  \"6.0\"", raylibHeader, StringComparison.Ordinal);
        Assert.Contains("public const ascii RAYLIB_VERSION = \"6.0\";", bindingText, StringComparison.Ordinal);

        var coreFunctions = ExtractRlapiFunctionNames(raylibHeader);
        var raymathFunctions = ExtractRaymathFunctionNames(raymathHeader);
        var rlglFunctions = ExtractRlapiFunctionNames(rlglHeader)
            .Where(static name => name.StartsWith("rl", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var coreStructs = ExtractTypedefStructNames(raylibHeader);
        var enumValues = ExtractEnumValueNames(raylibHeader);
        var callbacks = ExtractCallbackTypedefNames(raylibHeader);
        var linkNames = LinkNameRegex().Matches(bindingText).Select(static match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var directFfiNames = DirectFfiNameRegex().Matches(bindingText).Select(static match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var boundNativeNames = new HashSet<string>(linkNames, StringComparer.Ordinal);
        boundNativeNames.UnionWith(directFfiNames);

        Assert.Equal(599, coreFunctions.Count);
        Assert.Equal(35, coreStructs.Count);
        Assert.Equal(303, enumValues.Count);
        Assert.Equal(6, callbacks.Count);
        Assert.Equal(146, raymathFunctions.Count);
        Assert.Equal(163, rlglFunctions.Count);

        Assert.Empty(coreFunctions.Except(boundNativeNames, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Empty(coreStructs.Except(ExtractPublicStructNames(bindingText), StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Empty(enumValues.Except(ExtractPublicConstNames(bindingText), StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Empty(callbacks.Except(ExtractPublicAliasNames(bindingText), StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void RaylibSixAllocatingTextFunctionsUseOwnedWrappersAndStaleModelPointSymbolsAreGone()
    {
        var repositoryRoot = FindRepositoryRoot();
        var textBinding = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib", "Text.stark"));
        var modelBinding = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib", "Models.stark"));

        foreach (var name in new[] { "TextReplace", "TextInsert", "TextReplaceBetween" })
        {
            Assert.Contains($@"[LinkName(""{name}"")]", textBinding, StringComparison.Ordinal);
            Assert.Contains($"internal unsafe ffi fn rawmutptr<i8[min max]> stark_raylib_{name}", textBinding, StringComparison.Ordinal);
            Assert.Contains($"public fn RaylibTextResult {name}", textBinding, StringComparison.Ordinal);
        }

        Assert.Contains("public struct RaylibOwnedText", textBinding, StringComparison.Ordinal);
        Assert.Contains("mut drop", textBinding, StringComparison.Ordinal);
        Assert.Contains("stark_raylib_MemFree(self.Data)", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("stark_raylib_Text_MemFree", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("TextReplaceAlloc", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("TextInsertAlloc", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("TextReplaceBetweenAlloc", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("public fn raw", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe ffi fn rawmutptr<i8[min max]> TextReplace", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe ffi fn rawmutptr<i8[min max]> TextInsert", textBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe ffi fn rawmutptr<i8[min max]> TextReplaceBetween", textBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void DrawModelPoints(Model model, Vector3 position, f32 scale, Color tint)", modelBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void DrawModelPointsEx(Model model, Vector3 position, Vector3 rotationAxis, f32 rotationAngle, Vector3 scale, Color tint)", modelBinding, StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibCallbacksUseTypedNativeFunctionPointersWhereAbiIsExpressible()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibRoot = Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib");
        var typesBinding = File.ReadAllText(Path.Combine(raylibRoot, "Types.stark"));
        var coreBinding = File.ReadAllText(Path.Combine(raylibRoot, "Core.stark"));
        var audioBinding = File.ReadAllText(Path.Combine(raylibRoot, "Audio.stark"));

        Assert.Contains("public alias LoadFileDataCallback = fnptr<unsafe ffi(c) fn rawmutptr<u8[0 max]>(ascii, rawmutptr<i32[min max]>)>;", typesBinding, StringComparison.Ordinal);
        Assert.Contains("public alias SaveFileDataCallback = fnptr<unsafe ffi(c) fn bool(ascii, rawmutptr<i8[min max]>, i32[min max])>;", typesBinding, StringComparison.Ordinal);
        Assert.Contains("public alias LoadFileTextCallback = fnptr<unsafe ffi(c) fn rawmutptr<i8[min max]>(ascii)>;", typesBinding, StringComparison.Ordinal);
        Assert.Contains("public alias SaveFileTextCallback = fnptr<unsafe ffi(c) fn bool(ascii, ascii)>;", typesBinding, StringComparison.Ordinal);
        Assert.Contains("public alias AudioCallback = fnptr<unsafe ffi(c) fn void(rawmutptr<i8[min max]>, u32[0 max])>;", typesBinding, StringComparison.Ordinal);
        Assert.Contains("Raylib's trace-log callback takes a C va_list", typesBinding, StringComparison.Ordinal);
        Assert.Contains("public alias TraceLogCallback = rawptr<i8[min max]>;", typesBinding, StringComparison.Ordinal);

        foreach (var name in new[]
        {
            "SetLoadFileDataCallback",
            "SetSaveFileDataCallback",
            "SetLoadFileTextCallback",
            "SetSaveFileTextCallback"
        })
        {
            Assert.Contains($@"[LinkName(""{name}"")]", coreBinding, StringComparison.Ordinal);
            Assert.Contains($"public fn void {name}(", coreBinding, StringComparison.Ordinal);
            Assert.Contains($"public fn void Clear{name["Set".Length..]}()", coreBinding, StringComparison.Ordinal);
            Assert.DoesNotContain($"public unsafe ffi fn void {name}", coreBinding, StringComparison.Ordinal);
        }

        foreach (var name in new[]
        {
            "SetAudioStreamCallback",
            "AttachAudioStreamProcessor",
            "DetachAudioStreamProcessor",
            "AttachAudioMixedProcessor",
            "DetachAudioMixedProcessor"
        })
        {
            Assert.Contains($@"[LinkName(""{name}"")]", audioBinding, StringComparison.Ordinal);
            Assert.Contains($"public fn void {name}(", audioBinding, StringComparison.Ordinal);
            Assert.DoesNotContain($"public unsafe fn void {name}", audioBinding, StringComparison.Ordinal);
            Assert.DoesNotContain($"public unsafe ffi fn void {name}", audioBinding, StringComparison.Ordinal);
        }

        Assert.Contains("public fn void ClearAudioStreamCallback(AudioStream stream)", audioBinding, StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibEnumFamiliesUseZeroCostTypedCarriersAndOverloads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibRoot = Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib");
        var typesBinding = File.ReadAllText(Path.Combine(raylibRoot, "Types.stark"));
        var coreBinding = File.ReadAllText(Path.Combine(raylibRoot, "Core.stark"));
        var texturesBinding = File.ReadAllText(Path.Combine(raylibRoot, "Textures.stark"));
        var textBinding = File.ReadAllText(Path.Combine(raylibRoot, "Text.stark"));
        var modelsBinding = File.ReadAllText(Path.Combine(raylibRoot, "Models.stark"));

        foreach (var carrier in new[]
        {
            "ConfigFlags",
            "TraceLogLevel",
            "KeyboardKey",
            "MouseButton",
            "MouseCursor",
            "GamepadButton",
            "GamepadAxis",
            "MaterialMapIndex",
            "ShaderLocationIndex",
            "ShaderUniformDataType",
            "ShaderAttributeDataType",
            "PixelFormat",
            "TextureFilter",
            "TextureWrap",
            "CubemapLayout",
            "FontType",
            "BlendMode",
            "Gesture",
            "CameraMode",
            "CameraProjection",
            "NPatchLayout"
        })
        {
            Assert.Contains($"public struct {carrier}", typesBinding, StringComparison.Ordinal);
            Assert.Contains($"public finite law {carrier} {carrier}FromNative", typesBinding, StringComparison.Ordinal);
            Assert.Contains($"{carrier}Native({carrier} value)", typesBinding, StringComparison.Ordinal);
        }

        foreach (var valueFactory in new[]
        {
            "ConfigFlagVsyncHint",
            "TraceLogWarning",
            "KeyA",
            "MouseButtonLeft",
            "MouseCursorArrow",
            "GamepadButtonLeftFaceUp",
            "GamepadAxisLeftX",
            "MaterialMapAlbedo",
            "ShaderLocationMatrixMvp",
            "ShaderUniformVec4",
            "ShaderAttributeVec3",
            "PixelFormatUncompressedR8g8b8a8",
            "TextureFilterBilinear",
            "TextureWrapRepeat",
            "CubemapLayoutAutoDetect",
            "FontTypeSdf",
            "BlendAlpha",
            "GestureTap",
            "CameraModeFree",
            "CameraProjectionPerspective",
            "NPatchLayoutNinePatch"
        })
        {
            Assert.Matches($@"public finite law \w+ {valueFactory}\(\)", typesBinding);
        }

        Assert.Contains("public fn bool IsKeyDown(KeyboardKey key)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void SetExitKey(KeyboardKey key)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn bool IsMouseButtonPressed(MouseButton button)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void SetMouseCursor(MouseCursor cursor)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn bool IsGamepadButtonPressed(i32[min max] gamepad, GamepadButton button)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn f32 GetGamepadAxisMovement(i32[min max] gamepad, GamepadAxis axis)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void SetConfigFlags(ConfigFlags flags)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void BeginBlendMode(BlendMode mode)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn Camera UpdateCamera(Camera camera, CameraMode mode)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void SetShaderValueMatrix(Shader shader, ShaderLocationIndex locIndex, Matrix mat)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus SetShaderValue(Shader shader, ShaderLocationIndex locIndex, borrow i8[min max][] value, ShaderUniformDataType uniformType)", coreBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus SetShaderValueV(Shader shader, ShaderLocationIndex locIndex, borrow i8[min max][] value, ShaderUniformDataType uniformType, i32[min max] count)", coreBinding, StringComparison.Ordinal);

        Assert.Contains("public fn Image ImageFormat(Image image, PixelFormat newFormat)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn TextureCubemap LoadTextureCubemap(Image image, CubemapLayout layout)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void SetTextureFilter(Texture2D texture, TextureFilter filter)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn void SetTextureWrap(Texture2D texture, TextureWrap wrap)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn Color GetPixelColor(borrow i8[min max][] source, PixelFormat format)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus SetPixelColor(borrow mut i8[min max][] destination, Color color, PixelFormat format)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn i32[min max] GetPixelDataSize(i32[min max] width, i32[min max] height, PixelFormat format)", texturesBinding, StringComparison.Ordinal);

        Assert.Contains("public unsafe ffi fn rawmutptr<GlyphInfo> LoadFontData", textBinding, StringComparison.Ordinal);
        Assert.Contains("public fn Material SetMaterialTexture(Material material, MaterialMapIndex mapType, Texture2D texture)", modelsBinding, StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibLegacyRawSurfaceIsLimitedToDocumentedAdvancedEdges()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibRoot = Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Core.stark:public unsafe ffi fn rawmutptr<i8[min max]> GetWindowHandle();",
            "Core.stark:public unsafe ffi varargs fn void TraceLog(i32[min max] logLevel, ascii text);",
            "Core.stark:public unsafe ffi fn void SetTraceLogCallback(TraceLogCallback callback);",
            "Text.stark:public unsafe ffi fn rawmutptr<GlyphInfo> LoadFontData(rawptr<u8[0 max]> fileData, i32[min max] dataSize, i32[min max] fontSize, rawmutptr<i32[min max]> codepoints, i32[min max] codepointCount, i32[min max] type);",
            "Text.stark:public unsafe fn Image GenImageFontAtlas(rawptr<GlyphInfo> glyphs, rawmutptr<i8[min max]> glyphRecs, i32[min max] glyphCount, i32[min max] fontSize, i32[min max] padding, i32[min max] packMethod) {",
            "Text.stark:public unsafe ffi fn void UnloadFontData(rawmutptr<GlyphInfo> glyphs, i32[min max] glyphCount);",
            "Text.stark:public unsafe ffi fn rawmutptr<rawmutptr<i8[min max]>> LoadTextLines(ascii text, rawmutptr<i32[min max]> count);",
            "Text.stark:public unsafe ffi fn void UnloadTextLines(rawmutptr<rawmutptr<i8[min max]>> text, i32[min max] lineCount);",
            "Text.stark:public ffi varargs unsafe fn rawptr<i8[min max]> TextFormat(ascii text);",
            "Text.stark:public unsafe ffi fn rawptr<i8[min max]> TextJoin(rawptr<rawptr<i8[min max]>> textList, i32[min max] count, ascii delimiter);",
            "Text.stark:public unsafe ffi fn rawptr<rawptr<i8[min max]>> TextSplit(ascii text, i8[min max] delimiter, rawmutptr<i32[min max]> count);",
            "Text.stark:public unsafe ffi fn void TextAppend(rawmutptr<i8[min max]> text, ascii append, rawmutptr<i32[min max]> position);",
            "Types.stark:public alias ModelAnimPose = rawmutptr<Transform>;",
            "Types.stark:public alias TraceLogCallback = rawptr<i8[min max]>;",
            "Types.stark:public alias LoadFileDataCallback = fnptr<unsafe ffi(c) fn rawmutptr<u8[0 max]>(ascii, rawmutptr<i32[min max]>)>;",
            "Types.stark:public alias SaveFileDataCallback = fnptr<unsafe ffi(c) fn bool(ascii, rawmutptr<i8[min max]>, i32[min max])>;",
            "Types.stark:public alias LoadFileTextCallback = fnptr<unsafe ffi(c) fn rawmutptr<i8[min max]>(ascii)>;",
            "Types.stark:public alias AudioCallback = fnptr<unsafe ffi(c) fn void(rawmutptr<i8[min max]>, u32[0 max])>;"
        };

        var actual = Directory.EnumerateFiles(raylibRoot, "*.stark")
            .SelectMany(path => File.ReadLines(path).Select(line => (FileName: Path.GetFileName(path), Line: line.TrimStart())))
            .Where(static entry => entry.Line.StartsWith("public unsafe", StringComparison.Ordinal)
                || entry.Line.StartsWith("public ffi varargs unsafe", StringComparison.Ordinal)
                || PublicRawPointerRegex().IsMatch(entry.Line))
            .Select(static entry => $"{entry.FileName}:{entry.Line}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(actual.Except(allowed, StringComparer.Ordinal).OrderBy(static line => line, StringComparer.Ordinal));
        Assert.Empty(allowed.Except(actual, StringComparer.Ordinal).OrderBy(static line => line, StringComparer.Ordinal));

        var coreBinding = File.ReadAllText(Path.Combine(raylibRoot, "Core.stark"));
        var texturesBinding = File.ReadAllText(Path.Combine(raylibRoot, "Textures.stark"));
        var textBinding = File.ReadAllText(Path.Combine(raylibRoot, "Text.stark"));
        var modelsBinding = File.ReadAllText(Path.Combine(raylibRoot, "Models.stark"));
        var audioBinding = File.ReadAllText(Path.Combine(raylibRoot, "Audio.stark"));
        var ownersBinding = File.ReadAllText(Path.Combine(raylibRoot, "Owners.stark"));

        foreach (var safeSignature in new[]
        {
            "public fn RaylibBytesResult LoadFileData(ascii fileName)",
            "public fn bool SaveFileData(ascii fileName, borrow u8[0 max][] data)",
            "public fn System.Memory.MemoryResult<System.Text.OwnedAscii> GetFileName(ascii filePath)",
            "public fn RaylibBytesResult CompressData(borrow u8[0 max][] data)",
            "public fn bool ComputeSHA256(borrow u8[0 max][] data, out u32[0 max][8] hash)"
        })
        {
            Assert.Contains(safeSignature, coreBinding, StringComparison.Ordinal);
        }

        Assert.Contains("public fn Image LoadImageFromMemory(ascii fileType, borrow u8[0 max][] fileData)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibColorsResult LoadImageColors(Image image)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus UpdateTexture(Texture2D texture, borrow i8[min max][] pixels)", texturesBinding, StringComparison.Ordinal);
        Assert.Contains("public fn Wave LoadWaveFromMemory(ascii fileType, borrow u8[0 max][] fileData)", audioBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibWaveSamplesResult LoadWaveSamples(Wave wave)", audioBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibTextResult LoadUTF8(borrow i32[min max][] codepoints)", textBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibCodepointsResult LoadCodepoints(ascii text)", textBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus DrawTextCodepoints(Font font, borrow i32[min max][] codepoints", textBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus UpdateMeshBuffer(Mesh mesh, i32[min max] index, borrow i8[min max][] data", modelsBinding, StringComparison.Ordinal);
        Assert.Contains("public fn RaylibStatus DrawMeshInstanced(Mesh mesh, Material material, borrow Matrix[] transforms)", modelsBinding, StringComparison.Ordinal);
        Assert.Contains("public struct OwnedMaterials", ownersBinding, StringComparison.Ordinal);
        Assert.Contains("public fn OwnedMaterials LoadOwnedMaterials(ascii fileName)", ownersBinding, StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibResourceOwnersCoverUnloadFamiliesWithoutPublicRawSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibRoot = Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib");
        var rootBinding = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib.stark"));
        var ownersBinding = File.ReadAllText(Path.Combine(raylibRoot, "Owners.stark"));

        Assert.Contains("export import Vendor.Raylib.Owners", rootBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe", ownersBinding, StringComparison.Ordinal);
        Assert.Empty(PublicRawPointerRegex().Matches(ownersBinding).Select(static match => match.Value).ToArray());

        foreach (var owner in new[]
        {
            "OwnedImage",
            "OwnedTexture2D",
            "OwnedRenderTexture2D",
            "OwnedFont",
            "OwnedShader",
            "OwnedMesh",
            "OwnedMaterial",
            "OwnedMaterials",
            "OwnedModel",
            "OwnedModelAnimations",
            "OwnedWave",
            "OwnedSound",
            "OwnedSoundAlias",
            "OwnedMusic",
            "OwnedAudioStream",
            "OwnedVrStereoConfig",
            "OwnedDirectoryFiles",
            "OwnedDroppedFiles",
            "OwnedAutomationEventList"
        })
        {
            Assert.Contains($"public struct {owner}", ownersBinding, StringComparison.Ordinal);
            Assert.Contains("public inline finite law", ownersBinding.AsSpan(
                ownersBinding.IndexOf($"public struct {owner}", StringComparison.Ordinal)).ToString(),
                StringComparison.Ordinal);
            Assert.Contains("public fn void Close(mut borrow", ownersBinding.AsSpan(
                ownersBinding.IndexOf($"public struct {owner}", StringComparison.Ordinal)).ToString(),
                StringComparison.Ordinal);
            Assert.Contains("mut drop", ownersBinding.AsSpan(
                ownersBinding.IndexOf($"public struct {owner}", StringComparison.Ordinal)).ToString(),
                StringComparison.Ordinal);
        }

        foreach (var factory in new[]
        {
            "OwnImage",
            "LoadOwnedImage",
            "OwnTexture2D",
            "LoadOwnedTexture",
            "LoadOwnedTextureFromImage",
            "OwnRenderTexture2D",
            "LoadOwnedRenderTexture",
            "OwnFont",
            "LoadOwnedFont",
            "LoadOwnedFontFromImage",
            "OwnShader",
            "LoadOwnedShader",
            "LoadOwnedShaderFromMemory",
            "OwnMesh",
            "OwnMaterial",
            "LoadOwnedMaterialDefault",
            "LoadOwnedMaterials",
            "OwnModel",
            "LoadOwnedModel",
            "LoadOwnedModelFromMesh",
            "LoadOwnedModelAnimations",
            "OwnWave",
            "LoadOwnedWave",
            "OwnSound",
            "LoadOwnedSound",
            "LoadOwnedSoundFromWave",
            "OwnSoundAlias",
            "LoadOwnedSoundAlias",
            "OwnMusic",
            "LoadOwnedMusicStream",
            "OwnAudioStream",
            "LoadOwnedAudioStream",
            "OwnVrStereoConfig",
            "LoadOwnedVrStereoConfig",
            "OwnDirectoryFiles",
            "LoadOwnedDirectoryFiles",
            "LoadOwnedDirectoryFilesEx",
            "OwnDroppedFiles",
            "LoadOwnedDroppedFiles",
            "OwnAutomationEventList",
            "LoadOwnedAutomationEventList"
        })
        {
            Assert.Matches($@"public fn\s+[A-Za-z0-9_<>\[\]\s\*\- ]+\s+{Regex.Escape(factory)}\(", ownersBinding);
        }

        foreach (var unload in new[]
        {
            "UnloadImage",
            "UnloadTexture",
            "UnloadRenderTexture",
            "UnloadFont",
            "UnloadShader",
            "UnloadMesh",
            "UnloadMaterial",
            "UnloadModel",
            "UnloadModelAnimations",
            "UnloadWave",
            "UnloadSound",
            "UnloadSoundAlias",
            "UnloadMusicStream",
            "UnloadAudioStream",
            "UnloadVrStereoConfig",
            "UnloadDirectoryFiles",
            "UnloadDroppedFiles",
            "UnloadAutomationEventList"
        })
        {
            Assert.Contains($"{unload}(", ownersBinding, StringComparison.Ordinal);
        }

        Assert.Contains("public fn bool TryGet(borrow OwnedModelAnimations self", ownersBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe fn OwnedModelAnimations", ownersBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("DataPointer", ownersBinding, StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibPackageBuildIsTargetScopedAndCarriesBundledNativePayload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "build-raylib-package.sh"));
        var targetPackageRoot = Path.Combine(repositoryRoot, "vendor", "dist", CheckedInRaylibTargetTriple);
        var targetNativeRoot = Path.Combine(targetPackageRoot, "native", "raylib");

        Assert.Contains("target_dist=\"${vendor_dist}/${target_triple}\"", buildScript, StringComparison.Ordinal);
        Assert.Contains("packaged_raylib_dir=\"${target_dist}/native/raylib\"", buildScript, StringComparison.Ordinal);
        Assert.Contains("-o \"${target_dist}/libVendorRaylib.a\"", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-o \"${vendor_dist}/libVendorRaylib.a\"", buildScript, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(targetPackageRoot, "libVendorRaylib.starkpkg")));
        Assert.True(File.Exists(Path.Combine(targetPackageRoot, "libVendorRaylib.a")));
        Assert.True(File.Exists(Path.Combine(targetNativeRoot, "libraylib.a")));
        Assert.True(File.Exists(Path.Combine(targetNativeRoot, "raylib.h")));
        Assert.True(File.Exists(Path.Combine(targetNativeRoot, "raymath.h")));
        Assert.True(File.Exists(Path.Combine(targetNativeRoot, "rlgl.h")));
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

    private static HashSet<string> ExtractPublicFunctionNames(string sourceText)
    {
        return PublicFunctionNameRegex().Matches(sourceText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractRlglLinkNames(string sourceText)
    {
        return RlglLinkNameRegex().Matches(sourceText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractRlapiFunctionNames(string headerText)
    {
        return RlapiFunctionRegex().Matches(headerText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractRaymathFunctionNames(string headerText)
    {
        return RaymathFunctionRegex().Matches(headerText)
            .Select(static match => match.Groups[1].Value)
            .Where(static name => char.IsUpper(name[0]))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractTypedefStructNames(string headerText)
    {
        return TypedefStructRegex().Matches(headerText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractEnumValueNames(string headerText)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in TypedefEnumBlockRegex().Matches(headerText))
        {
            foreach (Match value in EnumValueRegex().Matches(block.Groups[1].Value))
            {
                values.Add(value.Groups[1].Value);
            }
        }

        return values;
    }

    private static HashSet<string> ExtractCallbackTypedefNames(string headerText)
    {
        return CallbackTypedefRegex().Matches(headerText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractPublicStructNames(string sourceText)
    {
        return PublicStructRegex().Matches(sourceText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractPublicConstNames(string sourceText)
    {
        return PublicConstRegex().Matches(sourceText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractPublicAliasNames(string sourceText)
    {
        return PublicAliasRegex().Matches(sourceText)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\s*public\s+(?:inline\s+)?(?:finite\s+law\s+|finite\s+|law\s+|fn\s+)?[^{;\r\n]*?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PublicFunctionNameRegex();

    [GeneratedRegex(@"\[LinkName\(""((?:rl|rlgl)[A-Za-z0-9_]*)""\)\]\s*\r?\n\s*internal\s+unsafe\s+ffi\s+fn", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RlglLinkNameRegex();

    [GeneratedRegex(@"^\s*RLAPI\s+[^;\r\n]*?[*\s]+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RlapiFunctionRegex();

    [GeneratedRegex(@"^\s*(?:RMAPI|RAYMATHAPI|RAYMATH_INLINE|static\s+inline)\s+[^;\r\n]*?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RaymathFunctionRegex();

    [GeneratedRegex(@"typedef\s+struct\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex TypedefStructRegex();

    [GeneratedRegex(@"typedef\s+enum(?:\s+[A-Za-z_][A-Za-z0-9_]*)?\s*\{(?<body>.*?)\}\s*[A-Za-z_][A-Za-z0-9_]*\s*;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TypedefEnumBlockRegex();

    [GeneratedRegex(@"^\s*([A-Z][A-Z0-9_]+)\s*(?:=|,|//)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EnumValueRegex();

    [GeneratedRegex(@"typedef\s+[^;\r\n]*\(\s*\*\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallbackTypedefRegex();

    [GeneratedRegex(@"\[LinkName\(""([^""]+)""\)\]", RegexOptions.CultureInvariant)]
    private static partial Regex LinkNameRegex();

    [GeneratedRegex(@"^\s*(?:public|internal)\s+(?=[^;\r\n]*\bffi\b)(?=[^;\r\n]*\bfn\b)[^;\r\n]*?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DirectFfiNameRegex();

    [GeneratedRegex(@"^\s*public\s+struct\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PublicStructRegex();

    [GeneratedRegex(@"^\s*public\s+const\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PublicConstRegex();

    [GeneratedRegex(@"^\s*public\s+alias\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PublicAliasRegex();

    private sealed record VendorBinding(
        string Name,
        string RootSourceRelativePath,
        string BuildScriptRelativePath,
        string PackageRelativePath,
        string ExampleRelativePath,
        IReadOnlyList<string> NativeSources,
        IReadOnlyList<string> RequiredPkgConfigPackages);
}
