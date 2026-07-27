using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageAbiCarrierTests
{
    [Fact]
    public void PackageImagePreservesDistinctSingleAArch64ParameterCarrier()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-aarch64-carrier-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "NativeColor.stark");
            var manifestPath = Path.Combine(
                tempDirectory.FullName,
                OperatingSystem.IsWindows() ? "NativeColor.starkpkg.json" : "libNativeColor.starkpkg.json");
            var libraryPath = Path.Combine(
                tempDirectory.FullName,
                OperatingSystem.IsWindows() ? "NativeColor.lib" : "libNativeColor.a");
            var targetInfo = new LlvmTargetInfo(
                "arm64-apple-macosx11.0.0",
                "e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32");

            File.WriteAllText(
                sourcePath,
                """
                module NativeColor

                [StructLayout(C)]
                public struct Color
                {
                    public u8[0 max] R;
                    public u8[0 max] G;
                    public u8[0 max] B;
                    public u8[0 max] A;
                }

                [LinkName("roundtrip_color")]
                public unsafe ffi(c) fn Color Roundtrip(Color value);
                """);

            var packageResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(File.ReadAllText(sourcePath), sourcePath),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    TargetInfo: targetInfo));

            Assert.True(
                packageResult.Succeeded,
                string.Join(", ", packageResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(packageResult, libraryPath);
            var module = Assert.Single(manifest.Modules, static module => module.ModuleName == "NativeColor");
            var function = Assert.Single(module.CompilerSections?.CompilerFacts?.AbiFunctions ?? []);
            var parameter = Assert.Single(function.Parameters, static parameter => parameter.SourceName == "value");

            Assert.Equal("integer", parameter.LlvmType.Kind);
            Assert.Equal(32, parameter.LlvmType.BitWidth);
            var physicalCarrier = Assert.Single(parameter.LlvmParameterTypes ?? []);
            Assert.Equal("integer", physicalCarrier.Kind);
            Assert.Equal(64, physicalCarrier.BitWidth);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import NativeColor
                    module App

                    export unsafe fn i32[min max] main()
                    {
                        stack Color input = new Color()
                        {
                            R = 230,
                            G = 41,
                            B = 55,
                            A = 255
                        };
                        stack Color output = Roundtrip(input);
                        return (i32[min max])output.A;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "App.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: targetInfo,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(
                consumerResult.Succeeded,
                string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.Contains("declare i32 @roundtrip_color(i64)", llvmModule!.Text, StringComparison.Ordinal);
            Assert.Contains("call i32 @roundtrip_color(i64", llvmModule.Text, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
