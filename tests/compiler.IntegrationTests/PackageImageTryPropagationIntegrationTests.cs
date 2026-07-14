using Stark.Compiler;

namespace compiler.IntegrationTests;

/// <summary>
/// End-to-end coverage for EP15 (docs/Self-host-Prep/11-error-propagation.md): `try`
/// error propagation across the package-image boundary. The `[Ok]`/`[Err]` variant roles
/// and `from` funnels declared in a producing package must survive `--emit-lib` package
/// publication and load back so that downstream packages can `try` imported enums
/// (including cross-family funnel conversion), and exported generic templates whose
/// bodies contain `try` must republish their propagation facts so downstream
/// specialization lowers them without re-type-checking.
/// </summary>
public sealed class PackageImageTryPropagationIntegrationTests
{
    private const string OutcomesPackageSource =
        """
        module Outcomes

        public enum FetchError
        {
            Timeout,
            Refused,
        }

        public enum AppError
        {
            Fetch from FetchError,
            Invalid,
        }

        public enum FetchOutcome<T>
        {
            [Ok] Got(T),
            [Err] Failed(FetchError),
        }

        public enum AppResult<T>
        {
            [Ok] Ok(T),
            [Err] Err(AppError),
        }

        public fn FetchOutcome<i32[min max]> Fetch(i32[min max] x)
        {
            if (x < 0)
            {
                return FetchOutcome<i32[min max]>.Failed(FetchError.Timeout);
            }

            return FetchOutcome<i32[min max]>.Got(x + 1);
        }
        """;

    private const string OutcomesConsumerSource =
        """
        import Outcomes
        module Demo

        fn Outcomes.AppResult<i32[min max]> Load(i32[min max] x)
        {
            // Cross-family propagation through the package boundary: the operand is the
            // imported FetchOutcome (fails with FetchError), the enclosing return type is
            // the imported AppResult (fails with AppError), connected by AppError's
            // `Fetch from FetchError` funnel.
            stack i32[min max] n = try Outcomes.Fetch(x);
            return Outcomes.AppResult<i32[min max]>.Ok(n + 50);
        }

        export fn i32[min max] main()
        {
            stack mut i32[min max] okPart = 0;
            switch (Load(10))
            {
                case Outcomes.AppResult<i32[min max]>.Ok(var v): okPart = v;
                case Outcomes.AppResult<i32[min max]>.Err(var e): okPart = 0;
            }

            stack mut i32[min max] errPart = 0;
            switch (Load(-1))
            {
                case Outcomes.AppResult<i32[min max]>.Ok(var v): errPart = 0;
                case Outcomes.AppResult<i32[min max]>.Err(var e):
                    switch (e)
                    {
                        case Outcomes.AppError.Fetch(var inner): errPart = 5;
                        case Outcomes.AppError.Invalid: errPart = 1;
                    }
            }

            return okPart + errPart;
        }
        """;

    [Fact]
    public async Task PackageImageRoleAnnotatedEnumsPropagateWithTryAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Load(10): Fetch = Got(11), returns Ok(61). Load(-1): Failed(Timeout) funnels into
        // AppError.Fetch = 5. Total 61+5 = 66.
        var exitCode = await EmitPackageThenCompileAndRunConsumerAsync(
            "Outcomes",
            OutcomesPackageSource,
            OutcomesConsumerSource,
            stripToTypedOnlyManifest: false);

        Assert.Equal(66, exitCode);
    }

    [Fact]
    public async Task PackageImageTypedOnlyManifestCarriesPropagationRolesAndFunnels()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Same scenario, but the package image is stripped to its structured typed
        // sections (no source-surface types, no legacy flat sections) before the consumer
        // compiles. The [Ok]/[Err] roles and the `from` funnel must come from the
        // typed-interface/compiler-facts sections alone.
        var exitCode = await EmitPackageThenCompileAndRunConsumerAsync(
            "Outcomes",
            OutcomesPackageSource,
            OutcomesConsumerSource,
            stripToTypedOnlyManifest: true);

        Assert.Equal(66, exitCode);
    }

    [Fact]
    public async Task PackageImageExportedGenericTemplateWithTrySpecializesDownstream()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // ValidateTwice's body contains two `try` expressions. The package image must
        // republish their propagation facts (operand/enclosing roles per `try`, ordinal-keyed)
        // so the consumer can specialize ValidateTwice<i32[min max]> without re-type-checking
        // the imported template body.
        // ValidateTwice(40, true, true) = Loaded(40) -> 40; ValidateTwice(40, true, false)
        // diverts at the second try -> Failed(Empty) -> 2. Total 42.
        var exitCode = await EmitPackageThenCompileAndRunConsumerAsync(
            "Loader",
            """
            module Loader

            public enum LoadError
            {
                Empty,
                TooBig,
            }

            public enum LoadResult<T>
            {
                [Ok] Loaded(T),
                [Err] Failed(LoadError),
            }

            public fn LoadResult<T> Validate<T>(T value, bool ok)
            {
                if (!ok)
                {
                    return LoadResult<T>.Failed(LoadError.Empty);
                }

                return LoadResult<T>.Loaded(value);
            }

            public fn LoadResult<T> ValidateTwice<T>(T value, bool first, bool second)
            {
                stack T a = try Validate(value, first);
                stack T b = try Validate(a, second);
                return LoadResult<T>.Loaded(b);
            }
            """,
            """
            import Loader
            module Demo

            export fn i32[min max] main()
            {
                stack i32[min max] value = 40;

                stack mut i32[min max] okPart = 0;
                switch (Loader.ValidateTwice(value, true, true))
                {
                    case Loader.LoadResult<i32[min max]>.Loaded(var v): okPart = v;
                    case Loader.LoadResult<i32[min max]>.Failed(var e): okPart = 0;
                }

                stack mut i32[min max] errPart = 0;
                switch (Loader.ValidateTwice(value, true, false))
                {
                    case Loader.LoadResult<i32[min max]>.Loaded(var v): errPart = 0;
                    case Loader.LoadResult<i32[min max]>.Failed(var e): errPart = 2;
                }

                return okPart + errPart;
            }
            """,
            stripToTypedOnlyManifest: false);

        Assert.Equal(42, exitCode);
    }

    [Fact]
    public async Task PackageImagePublishesTryPropagationFactsForExportedTemplates()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-try-facts-");
        var packageSourcePath = Path.Combine(tempDirectory.FullName, "Loader.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libLoader.starkpkg");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Loader.lib" : "libLoader.a");

        try
        {
            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module Loader

                public enum LoadError { Empty }

                public enum LoadResult<T>
                {
                    [Ok] Loaded(T),
                    [Err] Failed(LoadError),
                }

                public fn LoadResult<T> Validate<T>(T value, bool ok)
                {
                    if (!ok)
                    {
                        return LoadResult<T>.Failed(LoadError.Empty);
                    }

                    return LoadResult<T>.Loaded(value);
                }

                public fn LoadResult<T> ValidateOnce<T>(T value, bool ok)
                {
                    stack T checked = try Validate(value, ok);
                    return LoadResult<T>.Loaded(checked);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [packageSourcePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(manifestPath));

            Assert.True(PackageImageLoader.TryLoadManifest(manifestPath, out var manifest));

            var loaderModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Loader");

            // The role attributes survive in the typed interface variants.
            var typedInterface = loaderModule.EffectiveTypedInterface;
            Assert.NotNull(typedInterface);
            var loadResultType = Assert.Single(typedInterface!.Types, static type => type.Name == "LoadResult");
            Assert.NotNull(loadResultType.Variants);
            Assert.Equal("ok", Assert.Single(loadResultType.Variants!, static variant => variant.Name == "Loaded").Role);
            Assert.Equal("err", Assert.Single(loadResultType.Variants!, static variant => variant.Name == "Failed").Role);

            // The template whose body contains `try` republishes its propagation facts.
            var templates = loaderModule.EffectiveGenericTemplates;
            Assert.NotNull(templates);
            var validateOnce = Assert.Single(templates!.Functions, static template => template.QualifiedResolvedName == "Loader.ValidateOnce");
            Assert.NotNull(validateOnce.TryPropagations);
            var tryFact = Assert.Single(validateOnce.TryPropagations!);
            Assert.Equal(0, tryFact.Ordinal);
            Assert.Equal("Loaded", tryFact.OperandOkVariantName);
            Assert.Equal("Failed", tryFact.OperandErrVariantName);
            Assert.Equal("Failed", tryFact.EnclosingErrVariantName);

            // A template without `try` publishes no try facts.
            var validate = Assert.Single(templates.Functions, static template => template.QualifiedResolvedName == "Loader.Validate");
            Assert.Null(validate.TryPropagations);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static async Task<int> EmitPackageThenCompileAndRunConsumerAsync(
        string packageModuleName,
        string packageSource,
        string consumerSource,
        bool stripToTypedOnlyManifest)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-try-propagation-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "pkg");
        Directory.CreateDirectory(packageDirectory);
        var packageSourcePath = Path.Combine(packageDirectory, $"{packageModuleName}.stark");
        var manifestPath = Path.Combine(packageDirectory, $"lib{packageModuleName}.starkpkg");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? $"{packageModuleName}.lib" : $"lib{packageModuleName}.a");
        var consumerSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(packageSourcePath, packageSource);

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [packageSourcePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Contains("Emitted static library:", emitStdout.ToString());
            Assert.Equal(string.Empty, emitStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            if (stripToTypedOnlyManifest)
            {
                Assert.True(PackageImageLoader.TryLoadManifest(manifestPath, out var manifest));

                var packageModule = WithEffectiveLegacyCompilerSectionCopies(
                    Assert.Single(manifest.Modules, module => module.ModuleName == packageModuleName));
                var typedOnlyManifest = manifest with
                {
                    Modules = manifest.Modules
                        .Select(module => module.ModuleName == packageModuleName
                            ? module with
                            {
                                Functions = [],
                                Types = [],
                                Globals = [],
                                TypeAliases = [],
                                TypedInterface = packageModule.TypedInterface,
                                CompilerFacts = packageModule.CompilerFacts,
                                GenericTemplates = packageModule.GenericTemplates,
                                CompilerSections = new StarkPackageCompilerSectionsManifest(
                                    TypedInterface: packageModule.TypedInterface,
                                    CompilerFacts: packageModule.CompilerFacts,
                                    GenericTemplates: packageModule.GenericTemplates),
                                SourceSurface = new StarkPackageSourceSurfaceSection(
                                    Imports: packageModule.EffectiveSourceSurface.Imports,
                                    ReExports: packageModule.EffectiveSourceSurface.ReExports,
                                    Functions: [],
                                    Types: [],
                                    Globals: [],
                                    TypeAliases: [])
                            }
                            : module)
                        .ToArray()
                };

                await File.WriteAllBytesAsync(manifestPath, PackageImageBinaryFormat.Encode(typedOnlyManifest));
            }

            // The consumer must work from the package image alone.
            File.Delete(packageSourcePath);

            await File.WriteAllTextAsync(consumerSourcePath, consumerSource);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    consumerSourcePath,
                    "--emit-exe",
                    "-o",
                    outputPath,
                    "-I",
                    packageDirectory
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(compileExitCode == 0, stderr.ToString());
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processOutput = await process!.StandardOutput.ReadToEndAsync();
            var processError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(string.Empty, processOutput);
            Assert.Equal(string.Empty, processError);
            return process.ExitCode;
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static StarkPackageModuleManifest WithEffectiveLegacyCompilerSectionCopies(StarkPackageModuleManifest module)
    {
        return module with
        {
            TypedInterface = module.EffectiveTypedInterface,
            CompilerFacts = module.EffectiveCompilerFacts,
            GenericTemplates = module.EffectiveGenericTemplates
        };
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
}
