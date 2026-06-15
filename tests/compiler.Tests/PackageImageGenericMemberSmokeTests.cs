using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Fast, fully in-process smoke test that a generic with an instance member can
/// be published into a .starkpkg image and then consumed by an app that calls the
/// generic instance member with a concrete type argument.
///
/// The library is built without invoking any native toolchain (the consumer stops
/// at "lower-mir"), and the producer SOURCE is deleted before the consumer compiles,
/// so the only way the consumer can resolve and lower <c>ReadCell&lt;i32&gt;()</c> is
/// through the serialized package image. The generic body performs an instance
/// member call (<c>cell.Read()</c>), which the consumer must rebind through the
/// imported typed-template member-call facts during MIR lowering. If the F2-style
/// serialized member-call ordinal desync were reintroduced, the binding would miss
/// and STK9999 would surface here.
/// </summary>
public sealed class PackageImageGenericMemberSmokeTests
{
    [Fact]
    public void GenericInstanceMemberCallBindsThroughPackageImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-generic-member-smoke-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(
                tempDirectory.FullName,
                OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(
                tempDirectory.FullName,
                OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

            var pipeline = DefaultCompilerPipeline.Create();

            // Producer: a generic struct with an instance member, plus a generic free
            // function whose body constructs the struct and calls the instance member.
            // The member call lives inside the generic template body, so it is what the
            // consumer must rebind from serialized member-call facts.
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Cell<T>
                    {
                        public i32[min max] Value;

                        public finite law i32[min max] Read(borrow Cell<T> self)
                        {
                            return self.Value;
                        }
                    }

                    public finite law i32[min max] ReadCell<T>(i32[min max] seed)
                    {
                        stack Cell<T> cell = new Cell<T>()
                        {
                            Value = seed
                        };

                        return cell.Read();
                    }
                    """,
                    sourcePath));

            Assert.True(
                libraryResult.Succeeded,
                string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            // Prove the generic template carries the serialized instance member call.
            var readCellTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.ReadCell");
            Assert.NotNull(readCellTemplate.TypedBody);
            Assert.True(readCellTemplate.MemberCalls is { Count: >= 1 });

            File.WriteAllText(manifestPath, manifest.ToJson());

            // PROVE it loads from the .starkpkg: delete the producer source so the only
            // resolvable form of Facade is the serialized package image.
            File.Delete(sourcePath);
            Assert.False(File.Exists(sourcePath));

            // Consumer: import the package and call the generic instance-member-bearing
            // function with a concrete type argument (i32). Stop at lower-mir so the run
            // is fully in-process (no native toolchain).
            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    public finite law i32[min max] Run()
                    {
                        return Facade.ReadCell<i32[min max]>(42);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(
                consumerResult.Succeeded,
                string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.DoesNotContain(consumerResult.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");

            // The imported generic instance member call must have lowered into MIR.
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Contains(
                mir!.Functions,
                static function => function.Name.Contains("Facade_ReadCell", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
