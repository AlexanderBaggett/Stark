using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Stark.Compiler;

internal static class ProjectCliDriver
{
    private const string ProjectManifestFileName = "Stark.toml";
    private const string SolutionManifestFileName = "Stark.solution.toml";
    private const string LocalUserConfigFileName = "Stark.user.toml";
    private const string BuildLockFileName = ".stark-build.lock";

    public static bool IsProjectCommand(string argument)
    {
        return TryParseCommand(argument, out _);
    }

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0 || !TryParseCommand(args[0], out var command))
            {
                return 1;
            }

            var options = ProjectCommandOptions.Parse(command, args[1..], stderr);
            if (options is null)
            {
                return 1;
            }

            if (options.ShowHelp)
            {
                await WriteHelpAsync(command, stdout);
                return 0;
            }

            if (!TryDiscoverManifest(Environment.CurrentDirectory, out var discovery))
            {
                await stderr.WriteLineAsync(
                    $"No {ProjectManifestFileName} or {SolutionManifestFileName} was found in this directory or its parents.");
                return 1;
            }

            var userConfig = LoadUserConfig(Environment.CurrentDirectory);

            return command switch
            {
                ProjectCommand.Build => await ExecuteBuildAsync(discovery!, options, userConfig, stdout, stderr),
                ProjectCommand.Run => await ExecuteRunAsync(discovery!, options, userConfig, stdout, stderr),
                ProjectCommand.Test => await ExecuteTestAsync(discovery!, options, userConfig, stdout, stderr),
                ProjectCommand.Clean => await ExecuteCleanAsync(discovery!, options, userConfig, stdout, stderr),
                _ => 1
            };
        }
        catch (InvalidOperationException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ExecuteBuildAsync(
        DiscoveredManifest discovery,
        ProjectCommandOptions options,
        UserConfig userConfig,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (discovery.Project is not null)
        {
            if (!TryCreateBuildSession(
                    options,
                    discovery.RootDirectory,
                    EmptyProfiles,
                    userConfig,
                    stdout,
                    stderr,
                    out var projectSession))
            {
                return 1;
            }

            var project = LoadProjectManifest(discovery.Project);
            try
            {
                var buildResult = await BuildProjectAsync(project, projectSession);
                return buildResult.Success ? 0 : 1;
            }
            finally
            {
                ReleaseBuildLocks(projectSession);
            }
        }

        var solution = LoadSolutionManifest(discovery.Solution!);
        if (!TryCreateBuildSession(
                options,
                discovery.RootDirectory,
                solution.Profiles,
                userConfig,
                stdout,
                stderr,
                out var session))
        {
            return 1;
        }

        var targets = ResolveBuildTargets(solution, options.TargetName, session.ManifestCache, stderr);
        if (targets is null)
        {
            return 1;
        }

        try
        {
            foreach (var target in targets)
            {
                var buildResult = await BuildProjectAsync(target, session);
                if (!buildResult.Success)
                {
                    return 1;
                }
            }

            return 0;
        }
        finally
        {
            ReleaseBuildLocks(session);
        }
    }

    private static async Task<int> ExecuteRunAsync(
        DiscoveredManifest discovery,
        ProjectCommandOptions options,
        UserConfig userConfig,
        TextWriter stdout,
        TextWriter stderr)
    {
        ProjectManifest project;
        IReadOnlyDictionary<BuildProfile, ProfileManifest> defaultProfiles = EmptyProfiles;
        if (discovery.Project is not null)
        {
            project = LoadProjectManifest(discovery.Project);
        }
        else
        {
            var solution = LoadSolutionManifest(discovery.Solution!);
            defaultProfiles = solution.Profiles;
            var manifestCache = new ManifestCache();
            var resolvedProject = ResolveRunTarget(solution, options.TargetName, manifestCache, stderr);
            if (resolvedProject is null)
            {
                return 1;
            }

            project = resolvedProject;
        }

        if (project.Kind != ProjectKind.Executable)
        {
            await stderr.WriteLineAsync($"Project '{project.Name}' is not runnable because it is a library.");
            return 1;
        }

        if (!TryCreateBuildSession(
                options,
                discovery.RootDirectory,
                defaultProfiles,
                userConfig,
                stdout,
                stderr,
                out var session))
        {
            return 1;
        }

        try
        {
            var buildResult = await BuildProjectAsync(project, session);
            if (!buildResult.Success)
            {
                return 1;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = buildResult.OutputPath,
                WorkingDirectory = Path.GetDirectoryName(buildResult.OutputPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                await stderr.WriteLineAsync($"Could not start '{buildResult.OutputPath}'.");
                return 1;
            }

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            ReleaseBuildLocks(session);
        }
    }

    private static async Task<int> ExecuteTestAsync(
        DiscoveredManifest discovery,
        ProjectCommandOptions options,
        UserConfig userConfig,
        TextWriter stdout,
        TextWriter stderr)
    {
        ProjectManifest[] projects;
        IReadOnlyDictionary<BuildProfile, ProfileManifest> defaultProfiles = EmptyProfiles;
        if (discovery.Project is not null)
        {
            var project = LoadProjectManifest(discovery.Project);
            if (project.Kind != ProjectKind.Test)
            {
                await stderr.WriteLineAsync($"Project '{project.Name}' is not a test project.");
                return 1;
            }

            projects = [project];
        }
        else
        {
            var solution = LoadSolutionManifest(discovery.Solution!);
            defaultProfiles = solution.Profiles;
            var targets = ResolveTestTargets(solution, options.TargetName, new ManifestCache(), stderr);
            if (targets is null)
            {
                return 1;
            }

            projects = targets;
        }

        if (!TryCreateBuildSession(
                options,
                discovery.RootDirectory,
                defaultProfiles,
                userConfig,
                stdout,
                stderr,
                out var session))
        {
            return 1;
        }

        try
        {
            var failed = false;
            foreach (var project in projects)
            {
                if (project.Kind != ProjectKind.Test)
                {
                    await stderr.WriteLineAsync($"Project '{project.Name}' is not a test project.");
                    failed = true;
                    continue;
                }

                var buildResult = await BuildProjectAsync(project, session);
                if (!buildResult.Success)
                {
                    return 1;
                }

                var exitCode = await RunTestExecutableAsync(buildResult, session.TestCollections, session.ListTestCollections, stdout, stderr);
                if (exitCode != 0)
                {
                    failed = true;
                }
            }

            return failed ? 1 : 0;
        }
        finally
        {
            ReleaseBuildLocks(session);
        }
    }

    private static async Task<int> ExecuteCleanAsync(
        DiscoveredManifest discovery,
        ProjectCommandOptions options,
        UserConfig userConfig,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (!TryResolveCleanScope(options.TargetName, stderr, out var scope))
        {
            return 1;
        }

        if (!TryCreateCleanSession(options, discovery.RootDirectory, scope, userConfig, stdout, stderr, out var session))
        {
            return 1;
        }

        var path = GetCleanPath(session, scope);
        if (!IsPathInsideBuildDirectory(path, GetBuildDirectory(session.BuildRootDirectory)))
        {
            await stderr.WriteLineAsync($"Refusing to clean path outside the Stark build directory: {path}");
            return 1;
        }

        if (!Directory.Exists(path))
        {
            await stdout.WriteLineAsync($"Nothing to clean: {path}");
            return 0;
        }

        Directory.Delete(path, recursive: true);
        await stdout.WriteLineAsync($"Deleted {path}");
        return 0;
    }

    private static async Task WriteHelpAsync(ProjectCommand command, TextWriter stdout)
    {
        switch (command)
        {
            case ProjectCommand.Build:
                await stdout.WriteLineAsync("Usage: stark build [target] [--dev|--release] [--target <triple>] [--stage stage0] [--toolchain-dir <dir>] [--package-image-json]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Build the current Stark project or solution.");
                await stdout.WriteLineAsync("- In a project directory, `stark build` builds that project.");
                await stdout.WriteLineAsync("- In a solution directory, `stark build` builds the default solution targets or all members.");
                await stdout.WriteLineAsync("- `target` may be a solution alias, member path, or project name.");
                await stdout.WriteLineAsync("- Outputs are routed under `build/<profile>/<target-triple>/<stage>/`.");
                await stdout.WriteLineAsync("- `--package-image-json` writes explicit package inspection views under `artifacts/pkg/`.");
                return;
            case ProjectCommand.Run:
                await stdout.WriteLineAsync("Usage: stark run [target] [--dev|--release] [--target <triple>] [--stage stage0] [--toolchain-dir <dir>]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Build and run the current Stark executable project or solution run target.");
                return;
            case ProjectCommand.Test:
                await stdout.WriteLineAsync("Usage: stark test [target] [--dev|--release] [--target <triple>] [--stage stage0] [--toolchain-dir <dir>]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Build and run Stark test projects.");
                await stdout.WriteLineAsync("- In a test project directory, `stark test` runs that project.");
                await stdout.WriteLineAsync("- In a solution directory, `stark test` runs the default test set or every test project.");
                await stdout.WriteLineAsync("- `--filter <text>` may be repeated; matching is ordinal substring over generated test names.");
                await stdout.WriteLineAsync("- `--collection <name[,name...]>` may be repeated; runs only facts tagged with the named [Collection]s (union).");
                await stdout.WriteLineAsync("- `--list-collections` prints the project's collection names without running facts.");
                return;
            case ProjectCommand.Clean:
                await stdout.WriteLineAsync("Usage: stark clean [stage|target|profile|diagnostics|artifacts] [--dev|--release] [--target <triple>] [--stage stage0] [--toolchain-dir <dir>]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Clean the formal `build/<profile>/<target-triple>/<stage>/` tree.");
                await stdout.WriteLineAsync("- Default scope is `stage`.");
                await stdout.WriteLineAsync("- `target`, `stage`, `diagnostics`, and `artifacts` use `--target <triple>` or the detected default target.");
                await stdout.WriteLineAsync("- `profile` deletes `build/<profile>/` and does not require target discovery.");
                return;
        }
    }

    private static async Task<int> RunTestExecutableAsync(
        BuildResult buildResult,
        IReadOnlyList<string> testCollections,
        bool listTestCollections,
        TextWriter stdout,
        TextWriter stderr)
    {
        await stdout.WriteLineAsync($"Running test project '{buildResult.Project.Name}'...");

        var startInfo = new ProcessStartInfo
        {
            FileName = buildResult.OutputPath,
            WorkingDirectory = buildResult.Project.DirectoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // The generated runner treats every argument as a collection name,
        // plus the literal --list-collections discovery request.
        if (listTestCollections)
        {
            startInfo.ArgumentList.Add("--list-collections");
        }

        foreach (var collectionName in testCollections)
        {
            startInfo.ArgumentList.Add(collectionName);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            await stderr.WriteLineAsync($"Could not start '{buildResult.OutputPath}'.");
            return 1;
        }

        var testStdoutTask = process.StandardOutput.ReadToEndAsync();
        var testStderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var testStdout = await testStdoutTask;
        var testStderr = await testStderrTask;

        if (!string.IsNullOrEmpty(testStdout))
        {
            await stdout.WriteAsync(testStdout);
        }

        if (!string.IsNullOrEmpty(testStderr))
        {
            await stderr.WriteAsync(testStderr);
        }

        if (process.ExitCode == 0)
        {
            await stdout.WriteLineAsync($"Passed test project '{buildResult.Project.Name}'.");
        }
        else
        {
            await stderr.WriteLineAsync($"Failed test project '{buildResult.Project.Name}' with exit code {process.ExitCode}.");
        }

        return process.ExitCode;
    }

    private static async Task<BuildResult> BuildProjectAsync(ProjectManifest project, BuildSession session)
    {
        if (session.BuildResults.TryGetValue(project.ManifestPath, out var cached))
        {
            return cached;
        }

        var dependencyResults = new List<BuildResult>();
        foreach (var dependency in project.Dependencies.Values)
        {
            var dependencyDirectory = ResolveProjectPath(project.DirectoryPath, dependency.Path);
            var dependencyManifestPath = Path.Combine(dependencyDirectory, ProjectManifestFileName);
            if (!File.Exists(dependencyManifestPath))
            {
                await session.Stderr.WriteLineAsync(
                    $"Project '{project.Name}' depends on '{dependency.Name}', but '{dependencyManifestPath}' was not found.");
                return RememberFailure(project, session);
            }

            var dependencyProject = LoadProjectManifest(dependencyManifestPath, session.ManifestCache);
            var dependencyResult = await BuildProjectAsync(dependencyProject, session);
            if (!dependencyResult.Success)
            {
                return RememberFailure(project, session);
            }

            dependencyResults.Add(dependencyResult);
        }

        var outputDirectory = GetOutputDirectory(project, session);
        session.BuildLocks.Add(await AcquireBuildDirectoryLockAsync(outputDirectory));

        // PAINPOINTS #5: derive a deterministic stamp over every input that can change
        // this project's output (its own sources, the manifest, bundled-library
        // search-path inputs, each dependency's stamp, the build configuration, and
        // the compiler binary itself). If the stamp matches the one recorded beside a
        // present output, the build is up to date and we skip recompilation; otherwise
        // we DELETE the stale outputs before rebuilding so a leftover
        // `.starkpkg`/executable can never shadow fresh source — removing the need for
        // a manual `rm -rf build` to get trustworthy pass/fail counts.
        var inputStamp = ComputeProjectInputStamp(project, session, dependencyResults);
        var stampPath = Path.Combine(outputDirectory, BuildStampFileName);
        var earlyOutputPath = GetOutputPath(project, outputDirectory);
        var earlyPackageDirectory = project.Kind == ProjectKind.Library
            ? GetPackageDirectory(project, session)
            : null;
        if (IsBuildUpToDate(stampPath, inputStamp, earlyOutputPath, project, session))
        {
            if (!await EmitPackageImageJsonInspectionIfRequestedAsync(project, session))
            {
                return RememberFailure(project, session);
            }

            var upToDate = new BuildResult(
                true,
                project,
                outputDirectory,
                earlyOutputPath,
                earlyPackageDirectory,
                inputStamp);
            session.BuildResults[project.ManifestPath] = upToDate;
            return upToDate;
        }

        CleanProjectStaleOutputs(project, session, outputDirectory, earlyOutputPath);

        var rootSourcePath = Path.GetFullPath(Path.Combine(project.DirectoryPath, project.RootFile));
        var rootInputPath = rootSourcePath;
        var generatedTestRunner = false;
        if (project.Kind == ProjectKind.Test)
        {
            var runnerResult = await GenerateTestRunnerIfNeededAsync(project, outputDirectory, session);
            if (!runnerResult.Success)
            {
                return RememberFailure(project, session);
            }

            if (runnerResult.GeneratedRunner)
            {
                rootInputPath = runnerResult.GeneratedPath!;
                generatedTestRunner = true;
            }
        }

        var outputPath = GetOutputPath(project, outputDirectory);
        var compileArgs = new List<string>
        {
            rootInputPath,
            project.Kind == ProjectKind.Library ? "--emit-lib" : "--emit-exe",
            "--no-stark-path",
            "-o",
            outputPath
        };

        compileArgs.Add("--target");
        compileArgs.Add(session.TargetTriple);
        compileArgs.Add("--package-profile");
        compileArgs.Add(session.Profile == BuildProfile.Release ? "release" : "dev");
        if (!string.IsNullOrWhiteSpace(session.ToolchainDirectory))
        {
            compileArgs.Add("--toolchain-dir");
            compileArgs.Add(session.ToolchainDirectory);
        }

        var intermediateDirectory = GetIntermediateDirectory(project, session);
        Directory.CreateDirectory(intermediateDirectory);
        compileArgs.Add("--save-temps");
        compileArgs.Add(intermediateDirectory);

        string? packageSearchDirectory = null;
        if (project.Kind == ProjectKind.Library)
        {
            packageSearchDirectory = GetPackageDirectory(project, session);
            Directory.CreateDirectory(packageSearchDirectory);
            compileArgs.Add("--package-image-output");
            compileArgs.Add(GetPackageImagePath(project, session));
        }

        if (generatedTestRunner)
        {
            compileArgs.Add("-I");
            compileArgs.Add(Path.GetDirectoryName(rootSourcePath) ?? project.DirectoryPath);
        }

        foreach (var searchDirectory in session.BuildResults.Values
                     .Where(result => result.Success && result.PackageSearchDirectory is not null)
                     .Select(result => result.PackageSearchDirectory!)
                     .Distinct(StringComparer.Ordinal))
        {
            compileArgs.Add("-I");
            compileArgs.Add(searchDirectory);
        }

        var bundledLibrarySearchPaths = GetBundledLibrarySearchPaths(session);
        foreach (var bundledSearchDirectory in bundledLibrarySearchPaths
                     .Where(static path => path.IncludeInCompilerSearch)
                     .Select(static path => path.Path)
                     .Distinct(StringComparer.Ordinal))
        {
            compileArgs.Add("-I");
            compileArgs.Add(bundledSearchDirectory);
        }

        var nativeArgsResult = BuildNativeArgs(
            project,
            bundledLibrarySearchPaths,
            session.ManifestCache,
            session.UserConfig,
            session.Stderr);
        if (!nativeArgsResult.Success)
        {
            return RememberFailure(project, session);
        }

        compileArgs.AddRange(nativeArgsResult.Arguments);

        var compilerStderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            compileArgs.ToArray(),
            new StringReader(string.Empty),
            session.Stdout,
            compilerStderr);

        var compilerStderrText = compilerStderr.ToString();
        if (!string.IsNullOrEmpty(compilerStderrText))
        {
            await session.Stderr.WriteAsync(compilerStderrText);
        }

        if (exitCode != 0)
        {
            if (TryGetBundledLibraryDiscoveryFailureRoot(compilerStderrText, out var failedBundledRoot))
            {
                await WriteBundledLibraryDiscoveryFailureAsync(
                    session,
                    failedBundledRoot,
                    bundledLibrarySearchPaths.Where(path => path.Root == failedBundledRoot).ToArray());
            }

            return RememberFailure(project, session);
        }

        if (!await EmitPackageImageJsonInspectionIfRequestedAsync(project, session))
        {
            return RememberFailure(project, session);
        }

        // PAINPOINTS #5: record the input stamp only after a clean build so the next
        // run can skip recompilation when nothing changed; a failed build leaves no
        // stamp, forcing a retry.
        try
        {
            await File.WriteAllTextAsync(stampPath, inputStamp);
        }
        catch (IOException)
        {
            // A missing stamp only costs an extra rebuild next run; never fail the build over it.
        }
        catch (UnauthorizedAccessException)
        {
        }

        var success = new BuildResult(
            true,
            project,
            outputDirectory,
            outputPath,
            packageSearchDirectory,
            inputStamp);
        session.BuildResults[project.ManifestPath] = success;
        return success;
    }

    private static async Task<bool> EmitPackageImageJsonInspectionIfRequestedAsync(ProjectManifest project, BuildSession session)
    {
        if (!session.EmitPackageImageJsonInspection || project.Kind != ProjectKind.Library)
        {
            return true;
        }

        var packageImagePath = GetPackageImagePath(project, session);
        if (!PackageImageLoader.TryLoadManifest(packageImagePath, out var manifest, out var loadDiagnostics))
        {
            await session.Stderr.WriteLineAsync($"Could not render package image JSON inspection view for '{packageImagePath}'.");
            await WriteProjectDiagnosticsAsync(session.Stderr, loadDiagnostics);
            return false;
        }

        var validationDiagnostics = PackageImageLoader.ValidateManifest(manifest, packageImagePath);
        if (validationDiagnostics.Count > 0)
        {
            await WriteProjectDiagnosticsAsync(session.Stderr, validationDiagnostics);
            if (validationDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                return false;
            }
        }

        var jsonPath = GetPackageImageJsonInspectionPath(project, session);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath) ?? GetProjectInspectionPackageDirectory(project, session));
        await File.WriteAllTextAsync(jsonPath, manifest.ToJson());
        await session.Stdout.WriteLineAsync($"Emitted package image JSON: {jsonPath}");
        return true;
    }

    private static async Task WriteProjectDiagnosticsAsync(TextWriter writer, IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            await writer.WriteLineAsync(diagnostic.ToString());
        }
    }

    private static async Task<FileStream> AcquireBuildDirectoryLockAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var lockPath = Path.Combine(outputDirectory, BuildLockFileName);

        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    private static void ReleaseBuildLocks(BuildSession session)
    {
        foreach (var buildLock in session.BuildLocks)
        {
            buildLock.Dispose();
        }

        session.BuildLocks.Clear();
    }

    private static async Task<TestRunnerBuildResult> GenerateTestRunnerIfNeededAsync(
        ProjectManifest project,
        string outputDirectory,
        BuildSession session)
    {
        var rootSourcePath = Path.GetFullPath(Path.Combine(project.DirectoryPath, project.RootFile));
        if (!File.Exists(rootSourcePath))
        {
            await session.Stderr.WriteLineAsync(
                $"Project '{project.Name}' test root '{rootSourcePath}' was not found.");
            return TestRunnerBuildResult.Fail();
        }

        var sourceText = await File.ReadAllTextAsync(rootSourcePath);
        var generation = StarkTestRunnerGenerator.Generate(sourceText, session.TestFilters, session.TargetTriple);
        if (!generation.Success)
        {
            foreach (var diagnostic in generation.Diagnostics)
            {
                await session.Stderr.WriteLineAsync(
                    $"{rootSourcePath}({diagnostic.Line},{diagnostic.Column}): test runner: {diagnostic.Message}");
            }

            return TestRunnerBuildResult.Fail();
        }

        await WarnUnreachableTestFactsAsync(project, rootSourcePath, session);

        if (!generation.GeneratedRunner)
        {
            return TestRunnerBuildResult.NotGenerated();
        }

        var generatedDirectory = Path.Combine(outputDirectory, "generated");
        Directory.CreateDirectory(generatedDirectory);
        var generatedPath = Path.Combine(generatedDirectory, $"{project.OutputName}.generated.stark");
        await File.WriteAllTextAsync(generatedPath, generation.SourceText);
        return TestRunnerBuildResult.Generated(generatedPath);
    }

    private static readonly Regex ModuleDeclarationPattern =
        new(@"^\s*module\s+([A-Za-z_][\w.]*)",
            RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ImportDeclarationPattern =
        new(@"^\s*(?:export\s+)?import\s+([A-Za-z_][\w.]*)\b",
            RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TestFactAttributePattern =
        new(@"^\s*\[\s*(Fact|Theory)\b",
            RegexOptions.Multiline | RegexOptions.Compiled);

    // PAINPOINTS #6: the generated test runner collects [Fact]/[Theory] facts ONLY from
    // the [test] root compilation unit, and an imported module file must be named after
    // its module to resolve. So warn (non-fatally, to stderr) when a project source file
    // (a) declares [Fact]/[Theory] but is not the root — those facts silently never run —
    // or (b) is not the root and its file name does not match its `module` name — that
    // import will not resolve. Build outputs and the generated runner are skipped.
    private static async Task WarnUnreachableTestFactsAsync(
        ProjectManifest project,
        string rootSourcePath,
        BuildSession session)
    {
        string rootFull;
        IEnumerable<string> files;
        try
        {
            rootFull = Path.GetFullPath(rootSourcePath);
            files = Directory.EnumerateFiles(project.DirectoryPath, "*.stark", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var path in files)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (IsIgnoredProjectSourcePath(full))
            {
                continue;
            }

            if (string.Equals(full, rootFull, StringComparison.Ordinal))
            {
                continue;
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(path);
            }
            catch
            {
                continue;
            }

            // Blank out string/char literals and comments before scanning. Ported
            // compiler/CLI/testing tests embed whole Stark programs inside raw"""..."""
            // literals, and those programs routinely contain `[Fact]`/`[Theory]`/`module`
            // lines; matching them would misreport every such file as having unreachable
            // facts. After blanking, only genuine top-level declarations remain.
            var scan = BlankLiteralsAndComments(text);

            var moduleMatch = ModuleDeclarationPattern.Match(scan);
            if (moduleMatch.Success)
            {
                var moduleName = moduleMatch.Groups[1].Value;
                var lastSegment = moduleName.Contains('.')
                    ? moduleName[(moduleName.LastIndexOf('.') + 1)..]
                    : moduleName;
                var fileBase = Path.GetFileNameWithoutExtension(path);
                if (!string.Equals(lastSegment, fileBase, StringComparison.Ordinal))
                {
                    await session.Stderr.WriteLineAsync(
                        $"{path}: warning: test-project file name '{fileBase}.stark' does not match its module name '{moduleName}'; it will not resolve when imported as '{moduleName}'.");
                }
            }

            if (TestFactAttributePattern.IsMatch(scan))
            {
                await session.Stderr.WriteLineAsync(
                    $"{path}: warning: this file declares [Fact]/[Theory] tests but is not the [test] root ('{project.RootFile}'); the test runner only collects facts from the root, so these tests will not run. Move them into the root (or add a [Fact] in the root that calls them).");
            }
        }
    }

    // Replace every string literal, character literal, and comment with blank space
    // (newlines preserved so line-anchored scans still align), so tokens that appear
    // INSIDE embedded source programs or comments — most importantly the `[Fact]`,
    // `[Theory]`, and `module` lines inside raw"""..."""` test fixtures — are not
    // mistaken for real declarations by the reachability scan above. Mirrors the
    // StringLiteral / CharacterLiteral / LINE_COMMENT / BLOCK_COMMENT lexer rules in
    // Stark.g4: raw"""...""", raw"...", "..." (with \-escapes), '...' (with \-escapes),
    // // to end-of-line, and non-nesting /* ... */.
    private static string BlankLiteralsAndComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        int n = text.Length;

        void Blank(char ch) => sb.Append(ch == '\n' ? '\n' : (ch == '\r' ? '\r' : ' '));

        while (i < n)
        {
            char c = text[i];

            // Line comment: // ... to end of line.
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') { Blank(text[i]); i++; }
                continue;
            }

            // Block comment: /* ... */ (does not nest).
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                Blank(' '); Blank(' '); i += 2;
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/')) { Blank(text[i]); i++; }
                if (i < n) { Blank(' '); Blank(' '); i += 2; }
                continue;
            }

            // Raw string: raw"""...""" (multi-line) or raw"..." (single line, no escapes).
            // `raw` must stand alone as a keyword, i.e. not be the tail of a longer identifier.
            if (c == 'r'
                && i + 3 < n && text[i + 1] == 'a' && text[i + 2] == 'w' && text[i + 3] == '"'
                && (i == 0 || !IsIdentifierPart(text[i - 1])))
            {
                if (i + 5 < n && text[i + 4] == '"' && text[i + 5] == '"')
                {
                    for (int k = 0; k < 6; k++) { Blank(text[i]); i++; } // raw"""
                    while (i + 2 < n && !(text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"')) { Blank(text[i]); i++; }
                    int q = 0;
                    while (i < n && q < 3) { Blank(text[i]); i++; q++; } // closing """ (or EOF)
                }
                else
                {
                    for (int k = 0; k < 4; k++) { Blank(text[i]); i++; } // raw"
                    while (i < n && text[i] != '"' && text[i] != '\r' && text[i] != '\n') { Blank(text[i]); i++; }
                    if (i < n && text[i] == '"') { Blank(text[i]); i++; }
                }
                continue;
            }

            // Normal string "..." with \-escapes (single line).
            if (c == '"')
            {
                Blank(c); i++;
                while (i < n && text[i] != '"' && text[i] != '\r' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < n) { Blank(text[i]); i++; }
                    if (i < n) { Blank(text[i]); i++; }
                }
                if (i < n && text[i] == '"') { Blank(text[i]); i++; }
                continue;
            }

            // Character literal '...' with \-escapes (single line).
            if (c == '\'')
            {
                Blank(c); i++;
                while (i < n && text[i] != '\'' && text[i] != '\r' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < n) { Blank(text[i]); i++; }
                    if (i < n) { Blank(text[i]); i++; }
                }
                if (i < n && text[i] == '\'') { Blank(text[i]); i++; }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static BuildResult RememberFailure(ProjectManifest project, BuildSession session)
    {
        var failure = new BuildResult(false, project, string.Empty, string.Empty, null);
        session.BuildResults[project.ManifestPath] = failure;
        return failure;
    }

    // PAINPOINTS #5: the file written beside a project's outputs recording the input
    // stamp of the build that produced them.
    private const string BuildStampFileName = ".stark-build-stamp";

    // PAINPOINTS #5: a build is up to date when the recorded stamp matches the freshly
    // computed one AND the output it described is still present (for libraries, the
    // package image too). Any mismatch or missing artifact forces a clean rebuild.
    private static bool IsBuildUpToDate(
        string stampPath,
        string inputStamp,
        string outputPath,
        ProjectManifest project,
        BuildSession session)
    {
        if (!File.Exists(stampPath) || !File.Exists(outputPath))
        {
            return false;
        }

        string recorded;
        try
        {
            recorded = File.ReadAllText(stampPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!string.Equals(recorded, inputStamp, StringComparison.Ordinal))
        {
            return false;
        }

        if (project.Kind == ProjectKind.Library
            && !File.Exists(GetPackageImagePath(project, session)))
        {
            return false;
        }

        return true;
    }

    // PAINPOINTS #5: before rebuilding a changed project, remove the artifacts a prior
    // build produced (executable/library, the generated test runner, intermediate
    // temps, and any emitted package image) so a stale `.starkpkg` can never be
    // rediscovered and shadow the fresh source.
    private static void CleanProjectStaleOutputs(
        ProjectManifest project,
        BuildSession session,
        string outputDirectory,
        string outputPath)
    {
        TryDeleteFile(outputPath);
        TryDeleteFile(Path.Combine(outputDirectory, BuildStampFileName));
        TryDeleteDirectory(Path.Combine(outputDirectory, "generated"));
        TryDeleteDirectory(GetIntermediateDirectory(project, session));
        if (project.Kind == ProjectKind.Library)
        {
            TryDeleteDirectory(GetPackageDirectory(project, session));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // PAINPOINTS #5: a stable hash over every input that can change this project's
    // output — its sources and manifest, bundled-library search-path inputs, each
    // dependency's own stamp (so transitive source changes propagate), the build
    // configuration and test filters, and the compiler binary itself (so a rebuilt
    // host compiler invalidates everything). File identity uses (path, mtime, size),
    // which is cheap and flips whenever an editor rewrites a file.
    private static string ComputeProjectInputStamp(
        ProjectManifest project,
        BuildSession session,
        IReadOnlyList<BuildResult> dependencyResults)
    {
        var builder = new StringBuilder();
        builder.Append("v1\n");
        builder.Append("profile=").Append(session.Profile).Append('\n');
        builder.Append("triple=").Append(session.TargetTriple).Append('\n');
        builder.Append("stage=").Append(session.StageName).Append('\n');
        builder.Append("kind=").Append(project.Kind).Append('\n');
        builder.Append("output=").Append(project.OutputName).Append('\n');
        if (project.Kind == ProjectKind.Test)
        {
            builder.Append("filters=").Append(string.Join("", session.TestFilters)).Append('\n');
        }

        try
        {
            var compilerPath = typeof(ProjectCliDriver).Assembly.Location;
            if (!string.IsNullOrEmpty(compilerPath) && File.Exists(compilerPath))
            {
                builder.Append("compiler=").Append(compilerPath)
                    .Append('|').Append(File.GetLastWriteTimeUtc(compilerPath).Ticks).Append('\n');
            }
        }
        catch
        {
        }

        AppendFileStamp(builder, "manifest", project.ManifestPath);

        foreach (var dependency in dependencyResults)
        {
            builder.Append("dep=").Append(dependency.Project.ManifestPath)
                .Append('|').Append(dependency.InputStamp).Append('\n');
        }

        AppendDirectoryStarkFileStamps(builder, "src", project.DirectoryPath);

        var stampedBundledLibraryPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bundledDirectory in GetBundledLibrarySearchPaths(session)
                     .Where(static path => path.IncludeInCompilerSearch))
        {
            if (stampedBundledLibraryPaths.Add(bundledDirectory.Path))
            {
                AppendDirectoryBundledLibraryFileStamps(builder, bundledDirectory.Root.DirectoryName, bundledDirectory.Path);
            }
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void AppendDirectoryStarkFileStamps(StringBuilder builder, string label, string directory)
    {
        AppendDirectoryFileStamps(builder, label, directory, "*.stark");
    }

    private static void AppendDirectoryBundledLibraryFileStamps(StringBuilder builder, string label, string directory)
    {
        AppendDirectoryFileStamps(builder, label, directory, "*.stark", "*.starkpkg", "*.starkpkg.json");
    }

    private static void AppendDirectoryFileStamps(StringBuilder builder, string label, string directory, params string[] patterns)
    {
        string root;
        IEnumerable<string> files;
        try
        {
            root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
            {
                return;
            }

            files = patterns.SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories));
        }
        catch
        {
            return;
        }

        foreach (var path in files.OrderBy(static p => p, StringComparer.Ordinal))
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.Contains("/build/")
                || normalized.Contains("/generated/")
                || normalized.Contains("/.stark/"))
            {
                continue;
            }

            AppendFileStamp(builder, label, path);
        }
    }

    private static void AppendFileStamp(StringBuilder builder, string label, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                builder.Append(label).Append('=').Append(Path.GetFullPath(path))
                    .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                    .Append('|').Append(info.Length).Append('\n');
            }
        }
        catch
        {
        }
    }

    private static NativeArgumentResult BuildNativeArgs(
        ProjectManifest project,
        IReadOnlyList<BundledLibrarySearchPath> bundledLibrarySearchPaths,
        ManifestCache manifestCache,
        UserConfig userConfig,
        TextWriter stderr)
    {
        var arguments = new List<string>();
        if (!TryAppendNativeArgs(project.Name, project.DirectoryPath, project.Native, userConfig, stderr, arguments, out var failure))
        {
            return failure;
        }

        foreach (var bundledProject in ResolveImportedBundledSourceNativeProjects(project, bundledLibrarySearchPaths, manifestCache))
        {
            if (!TryAppendNativeArgs(
                    bundledProject.Name,
                    bundledProject.DirectoryPath,
                    bundledProject.Native,
                    userConfig,
                    stderr,
                    arguments,
                    out failure))
            {
                return failure;
            }
        }

        return NativeArgumentResult.FromArguments(arguments);
    }

    private static bool TryAppendNativeArgs(
        string projectName,
        string baseDirectory,
        NativeDependencyManifest? native,
        UserConfig userConfig,
        TextWriter stderr,
        List<string> arguments,
        out NativeArgumentResult failure)
    {
        failure = default!;
        if (native is null)
        {
            return true;
        }

        foreach (var source in native.Sources)
        {
            arguments.Add("--native-source");
            arguments.Add(Path.GetFullPath(Path.Combine(baseDirectory, source)));
        }

        if (native.PkgConfigPackages.Count != 0
            && ArePkgConfigPackagesAvailable(native.PkgConfigPackages))
        {
            foreach (var package in native.PkgConfigPackages)
            {
                arguments.Add("--native-pkg-config");
                arguments.Add(package);
            }

            return true;
        }

        var fallback = native.GetFallbackForCurrentPlatform();
        if (fallback is null)
        {
            if (native.PkgConfigPackages.Count == 0)
            {
                return true;
            }

            failure = NativeArgumentResult.Fail(
                stderr,
                $"Project '{projectName}' needs native package metadata that is available neither through pkg-config nor a platform fallback.");
            return false;
        }

        foreach (var includeDirectory in fallback.IncludeDirectories)
        {
            if (!TryResolveNativePath(includeDirectory, userConfig, baseDirectory, out var resolved, out var missingKey))
            {
                failure = NativeArgumentResult.Fail(
                    stderr,
                    $"Project '{projectName}' needs native path '{missingKey}' to build on this machine.",
                    "Add it under [native.paths] in Stark.user.toml or ~/.config/stark/config.toml.");
                return false;
            }

            arguments.Add("--native-include-dir");
            arguments.Add(resolved);
        }

        foreach (var libraryDirectory in fallback.LibraryDirectories)
        {
            if (!TryResolveNativePath(libraryDirectory, userConfig, baseDirectory, out var resolved, out var missingKey))
            {
                failure = NativeArgumentResult.Fail(
                    stderr,
                    $"Project '{projectName}' needs native path '{missingKey}' to build on this machine.",
                    "Add it under [native.paths] in Stark.user.toml or ~/.config/stark/config.toml.");
                return false;
            }

            arguments.Add("--native-library-dir");
            arguments.Add(resolved);
        }

        foreach (var library in fallback.Libraries)
        {
            arguments.Add("--native-library");
            arguments.Add(library);
        }

        return true;
    }

    private static IReadOnlyList<ProjectManifest> ResolveImportedBundledSourceNativeProjects(
        ProjectManifest project,
        IReadOnlyList<BundledLibrarySearchPath> bundledLibrarySearchPaths,
        ManifestCache manifestCache)
    {
        var importedModules = CollectProjectImportedModules(project);
        if (importedModules.Count == 0)
        {
            return [];
        }

        var packagedModulesByRoot = BuildBundledPackageModuleIndex(bundledLibrarySearchPaths);
        var nativeProjects = new List<ProjectManifest>();
        var seenManifests = new HashSet<string>(StringComparer.Ordinal);

        foreach (var searchPath in bundledLibrarySearchPaths)
        {
            if (!TryResolveBundledSourceManifestPath(searchPath, out var manifestPath)
                || !seenManifests.Add(manifestPath))
            {
                continue;
            }

            var bundledProject = LoadProjectManifest(manifestPath, manifestCache);
            if (bundledProject.Native is null
                || !TryReadProjectRootModuleName(bundledProject, out var rootModule)
                || !ImportsModuleOrChild(importedModules, rootModule))
            {
                continue;
            }

            if (packagedModulesByRoot.TryGetValue(searchPath.Root, out var packagedModules)
                && PackageImageCoversModuleOrChild(packagedModules, rootModule))
            {
                continue;
            }

            nativeProjects.Add(bundledProject);
        }

        return nativeProjects;
    }

    private static Dictionary<BundledLibraryRoot, HashSet<string>> BuildBundledPackageModuleIndex(
        IReadOnlyList<BundledLibrarySearchPath> bundledLibrarySearchPaths)
    {
        var modulesByRoot = new Dictionary<BundledLibraryRoot, HashSet<string>>();

        foreach (var searchPath in bundledLibrarySearchPaths)
        {
            if (!searchPath.IncludeInCompilerSearch
                || !string.Equals(searchPath.State, "package images", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var packageImagePath in EnumeratePackageImageFiles(searchPath.Path))
            {
                if (!PackageImageLoader.TryLoadManifest(packageImagePath, out var manifest))
                {
                    continue;
                }

                if (!modulesByRoot.TryGetValue(searchPath.Root, out var modules))
                {
                    modules = new HashSet<string>(StringComparer.Ordinal);
                    modulesByRoot.Add(searchPath.Root, modules);
                }

                if (!string.IsNullOrWhiteSpace(manifest.RootModule))
                {
                    modules.Add(manifest.RootModule.Trim());
                }

                foreach (var module in manifest.Modules)
                {
                    if (!string.IsNullOrWhiteSpace(module.ModuleName))
                    {
                        modules.Add(module.ModuleName.Trim());
                    }
                }
            }
        }

        return modulesByRoot;
    }

    private static IEnumerable<string> EnumeratePackageImageFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> packageImages;
        IEnumerable<string> jsonPackageImages;
        try
        {
            packageImages = Directory.EnumerateFiles(directory, "*.starkpkg", SearchOption.AllDirectories)
                .Where(static path => PackageImageBinaryFormat.HasBinaryFileName(path))
                .ToArray();
            jsonPackageImages = Directory.EnumerateFiles(directory, "*.starkpkg.json", SearchOption.AllDirectories)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var path in packageImages.Concat(jsonPackageImages).OrderBy(static path => path, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    private static bool TryResolveBundledSourceManifestPath(BundledLibrarySearchPath searchPath, out string manifestPath)
    {
        manifestPath = string.Empty;
        if (!searchPath.IncludeInCompilerSearch
            || !string.Equals(searchPath.State, "source tree", StringComparison.Ordinal))
        {
            return false;
        }

        var rootDirectory = Directory.GetParent(searchPath.Path)?.FullName;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        manifestPath = Path.GetFullPath(Path.Combine(rootDirectory, ProjectManifestFileName));
        return File.Exists(manifestPath);
    }

    private static bool TryReadProjectRootModuleName(ProjectManifest project, out string moduleName)
    {
        moduleName = string.Empty;
        var rootPath = Path.GetFullPath(Path.Combine(project.DirectoryPath, project.RootFile));
        if (!File.Exists(rootPath))
        {
            return false;
        }

        try
        {
            var scan = BlankLiteralsAndComments(File.ReadAllText(rootPath));
            var moduleMatch = ModuleDeclarationPattern.Match(scan);
            if (!moduleMatch.Success)
            {
                return false;
            }

            moduleName = moduleMatch.Groups[1].Value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<string> CollectProjectImportedModules(ProjectManifest project)
    {
        var modules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in EnumerateProjectSourceFiles(project.DirectoryPath))
        {
            try
            {
                var scan = BlankLiteralsAndComments(File.ReadAllText(path));
                foreach (Match match in ImportDeclarationPattern.Matches(scan))
                {
                    modules.Add(match.Groups[1].Value);
                }
            }
            catch
            {
            }
        }

        return modules;
    }

    private static IEnumerable<string> EnumerateProjectSourceFiles(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.stark", SearchOption.AllDirectories)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var path in files.OrderBy(static path => path, StringComparer.Ordinal))
        {
            if (!IsIgnoredProjectSourcePath(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsIgnoredProjectSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/build/")
            || normalized.Contains("/generated/")
            || normalized.Contains("/.stark/");
    }

    private static bool ImportsModuleOrChild(IReadOnlySet<string> importedModules, string moduleName)
    {
        foreach (var importedModule in importedModules)
        {
            if (string.Equals(importedModule, moduleName, StringComparison.Ordinal)
                || importedModule.StartsWith($"{moduleName}.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PackageImageCoversModuleOrChild(IReadOnlySet<string> packageModules, string moduleName)
    {
        foreach (var packageModule in packageModules)
        {
            if (string.Equals(packageModule, moduleName, StringComparison.Ordinal)
                || packageModule.StartsWith($"{moduleName}.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveNativePath(
        string value,
        UserConfig userConfig,
        string projectDirectory,
        out string resolvedPath,
        out string missingKey)
    {
        missingKey = string.Empty;
        string? unresolvedKey = null;
        var resolved = Regex.Replace(
            value,
            @"\$\{([^}]+)\}",
            match =>
            {
                var key = match.Groups[1].Value;
                if (userConfig.NativePaths.TryGetValue(key, out var replacement))
                {
                    return replacement;
                }

                unresolvedKey ??= key;
                return match.Value;
            });

        missingKey = unresolvedKey ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(missingKey))
        {
            resolvedPath = string.Empty;
            return false;
        }

        resolvedPath = Path.GetFullPath(Path.IsPathRooted(resolved) ? resolved : Path.Combine(projectDirectory, resolved));
        return true;
    }

    private static bool ArePkgConfigPackagesAvailable(IReadOnlyList<string> packageNames)
    {
        if (packageNames.Count == 0)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pkg-config",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--exists");
            foreach (var packageName in packageNames)
            {
                startInfo.ArgumentList.Add(packageName);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProjectManifest[]? ResolveBuildTargets(
        SolutionManifest solution,
        string? targetName,
        ManifestCache manifestCache,
        TextWriter stderr)
    {
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            var target = ResolveSolutionProject(solution, targetName, manifestCache, stderr);
            return target is null ? null : [target];
        }

        var defaultTargets = solution.DefaultBuildTargets.Count != 0
            ? solution.DefaultBuildTargets
            : solution.Members;

        var projects = new List<ProjectManifest>();
        foreach (var target in defaultTargets)
        {
            var project = ResolveSolutionProject(solution, target, manifestCache, stderr);
            if (project is null)
            {
                return null;
            }

            projects.Add(project);
        }

        return projects.ToArray();
    }

    private static ProjectManifest? ResolveRunTarget(
        SolutionManifest solution,
        string? targetName,
        ManifestCache manifestCache,
        TextWriter stderr)
    {
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            return ResolveSolutionProject(solution, targetName, manifestCache, stderr);
        }

        if (string.IsNullOrWhiteSpace(solution.DefaultRunTarget))
        {
            stderr.WriteLine("This solution does not declare a default run target. Pass a target name to `stark run`.");
            return null;
        }

        return ResolveSolutionProject(solution, solution.DefaultRunTarget, manifestCache, stderr);
    }

    private static ProjectManifest[]? ResolveTestTargets(
        SolutionManifest solution,
        string? targetName,
        ManifestCache manifestCache,
        TextWriter stderr)
    {
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            var target = ResolveSolutionProject(solution, targetName, manifestCache, stderr);
            return target is null ? null : [target];
        }

        var defaultTargets = solution.DefaultTestTargets;
        if (defaultTargets.Count != 0)
        {
            var projects = new List<ProjectManifest>();
            foreach (var target in defaultTargets)
            {
                var project = ResolveSolutionProject(solution, target, manifestCache, stderr);
                if (project is null)
                {
                    return null;
                }

                projects.Add(project);
            }

            return projects.ToArray();
        }

        var testProjects = new List<ProjectManifest>();
        foreach (var member in solution.Members)
        {
            var project = LoadProjectManifest(
                Path.Combine(ResolveProjectPath(solution.DirectoryPath, member), ProjectManifestFileName),
                manifestCache);
            if (project.Kind == ProjectKind.Test)
            {
                testProjects.Add(project);
            }
        }

        if (testProjects.Count == 0)
        {
            stderr.WriteLine("This solution does not contain any test projects. Pass a test target name to `stark test`.");
            return null;
        }

        return testProjects.ToArray();
    }

    private static ProjectManifest? ResolveSolutionProject(
        SolutionManifest solution,
        string targetName,
        ManifestCache manifestCache,
        TextWriter stderr)
    {
        var normalizedTarget = solution.Aliases.TryGetValue(targetName, out var aliasTarget)
            ? aliasTarget
            : targetName;

        foreach (var member in solution.Members)
        {
            if (string.Equals(member, normalizedTarget, StringComparison.Ordinal))
            {
                return LoadProjectManifest(
                    Path.Combine(ResolveProjectPath(solution.DirectoryPath, member), ProjectManifestFileName),
                    manifestCache);
            }
        }

        foreach (var member in solution.Members)
        {
            var project = LoadProjectManifest(
                Path.Combine(ResolveProjectPath(solution.DirectoryPath, member), ProjectManifestFileName),
                manifestCache);
            if (string.Equals(project.Name, normalizedTarget, StringComparison.Ordinal))
            {
                return project;
            }
        }

        stderr.WriteLine($"Solution target '{targetName}' was not found.");
        return null;
    }

    private static string ResolveProjectPath(string baseDirectory, string path)
    {
        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private static bool TryCreateBuildSession(
        ProjectCommandOptions options,
        string buildRootDirectory,
        IReadOnlyDictionary<BuildProfile, ProfileManifest> defaultProfiles,
        UserConfig userConfig,
        TextWriter stdout,
        TextWriter stderr,
        out BuildSession session)
    {
        session = default!;

        if (!string.Equals(options.StageName, "stage0", StringComparison.Ordinal))
        {
            stderr.WriteLine(
                $"Compiler stage '{options.StageName}' is not available yet. The current host project driver can build only stage0 artifacts.");
            return false;
        }

        var toolchainResolutionOptions = new NativeToolchainResolutionOptions(
            CliToolchainDirectory: options.ToolchainDirectory,
            UserConfigToolchainDirectory: userConfig.ToolchainDirectory);
        var targetToolchain = NativeToolchain.Resolve(toolchainResolutionOptions);
        var forwardedToolchainDirectory = ResolveForwardedToolchainDirectory(options, userConfig);

        var targetTriple = options.TargetTriple;
        if (string.IsNullOrWhiteSpace(targetTriple)
            && NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo, targetToolchain))
        {
            targetTriple = detectedTargetInfo.Triple;
        }

        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            stderr.WriteLine("Could not resolve a target triple. Pass --target <triple>.");
            return false;
        }

        session = new BuildSession(
            Profile: options.Profile,
            BuildRootDirectory: buildRootDirectory,
            TargetTriple: targetTriple.Trim(),
            StageName: options.StageName,
            ToolchainDirectory: forwardedToolchainDirectory,
            EmitPackageImageJsonInspection: options.EmitPackageImageJsonInspection,
            UserConfig: userConfig,
            DefaultProfiles: defaultProfiles,
            TestFilters: options.TestFilters,
            TestCollections: options.TestCollections,
            ListTestCollections: options.ListTestCollections,
            Stdout: stdout,
            Stderr: stderr);
        return true;
    }

    private static string? ResolveForwardedToolchainDirectory(ProjectCommandOptions options, UserConfig userConfig)
    {
        if (!string.IsNullOrWhiteSpace(options.ToolchainDirectory))
        {
            return options.ToolchainDirectory;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STARK_TOOLCHAIN_DIR")))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(userConfig.ToolchainDirectory) ? null : userConfig.ToolchainDirectory;
    }

    private static bool TryCreateCleanSession(
        ProjectCommandOptions options,
        string buildRootDirectory,
        CleanScope scope,
        UserConfig userConfig,
        TextWriter stdout,
        TextWriter stderr,
        out CleanSession session)
    {
        session = default!;

        string? targetTriple = null;
        if (scope.RequiresTargetTriple())
        {
            targetTriple = options.TargetTriple;
            var targetToolchain = NativeToolchain.Resolve(new NativeToolchainResolutionOptions(
                CliToolchainDirectory: options.ToolchainDirectory,
                UserConfigToolchainDirectory: userConfig.ToolchainDirectory));
            if (string.IsNullOrWhiteSpace(targetTriple)
                && NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo, targetToolchain))
            {
                targetTriple = detectedTargetInfo.Triple;
            }

            if (string.IsNullOrWhiteSpace(targetTriple))
            {
                stderr.WriteLine("Could not resolve a target triple. Pass --target <triple>.");
                return false;
            }

            targetTriple = targetTriple.Trim();
        }

        session = new CleanSession(
            Profile: options.Profile,
            BuildRootDirectory: buildRootDirectory,
            TargetTriple: targetTriple,
            StageName: options.StageName,
            Stdout: stdout,
            Stderr: stderr);
        return true;
    }

    private static string GetOutputDirectory(ProjectManifest project, BuildSession session)
    {
        return Path.Combine(
            GetStageRootDirectory(session),
            project.Kind == ProjectKind.Test ? "tests" : "bin",
            GetProjectArtifactDirectoryName(project, session));
    }

    private static string GetIntermediateDirectory(ProjectManifest project, BuildSession session)
    {
        return Path.Combine(
            GetStageRootDirectory(session),
            "obj",
            GetProjectArtifactDirectoryName(project, session));
    }

    private static string GetPackageDirectory(ProjectManifest project, BuildSession session)
    {
        return Path.Combine(
            GetStageRootDirectory(session),
            "pkg",
            GetProjectArtifactDirectoryName(project, session));
    }

    private static string GetPackageImagePath(ProjectManifest project, BuildSession session)
    {
        return Path.Combine(GetPackageDirectory(project, session), GetPackageImageFileName(project));
    }

    private static string GetProjectInspectionPackageDirectory(ProjectManifest project, BuildSession session)
    {
        return Path.Combine(
            GetStageRootDirectory(session),
            "artifacts",
            "pkg",
            GetProjectArtifactDirectoryName(project, session));
    }

    private static string GetPackageImageJsonInspectionPath(ProjectManifest project, BuildSession session)
    {
        return Path.Combine(
            GetProjectInspectionPackageDirectory(project, session),
            PackageImageBinaryFormat.JsonSidecarPath(GetPackageImageFileName(project)));
    }

    private static string GetPackageImageFileName(ProjectManifest project)
    {
        return $"lib{project.OutputName}{PackageImageBinaryFormat.FileExtension}";
    }

    private static string GetStageBundledLibraryDirectory(BuildSession session, BundledLibraryRoot root)
    {
        return Path.Combine(GetStageRootDirectory(session), root.DirectoryName);
    }

    private static IReadOnlyList<BundledLibrarySearchPath> GetBundledLibrarySearchPaths(BuildSession session)
    {
        var paths = new List<BundledLibrarySearchPath>();
        foreach (var root in BundledLibraryRoots)
        {
            AddBundledLibrarySearchPaths(session, root, paths);
        }

        return paths;
    }

    private static void AddBundledLibrarySearchPaths(
        BuildSession session,
        BundledLibraryRoot root,
        List<BundledLibrarySearchPath> paths)
    {
        var stageDirectory = GetStageBundledLibraryDirectory(session, root);
        paths.Add(new BundledLibrarySearchPath(
            root,
            "stage/build-local",
            Path.GetFullPath(stageDirectory),
            IncludeInCompilerSearch: true,
            Directory.Exists(stageDirectory) ? "present" : "missing"));
        AddDevelopmentBundledLibrarySearchPaths(session.BuildRootDirectory, root, paths);
        AddInstalledBundledLibrarySearchPaths(AppContext.BaseDirectory, root, paths);
    }

    private static void AddDevelopmentBundledLibrarySearchPaths(
        string buildRootDirectory,
        BundledLibraryRoot root,
        List<BundledLibrarySearchPath> paths)
    {
        foreach (var rootDirectory in GetDevelopmentBundledLibraryRootCandidates(buildRootDirectory, root))
        {
            if (IsDevelopmentBundledLibraryDirectory(rootDirectory))
            {
                AddBundledLibrarySearchDirectories(rootDirectory, root, "repo development", paths);
                return;
            }

            paths.Add(new BundledLibrarySearchPath(
                root,
                "repo development",
                Path.GetFullPath(rootDirectory),
                IncludeInCompilerSearch: false,
                Directory.Exists(rootDirectory) ? $"not a {root.DirectoryName} project" : "missing"));
        }
    }

    private static bool IsDevelopmentBundledLibraryDirectory(string path)
    {
        return File.Exists(Path.Combine(path, ProjectManifestFileName))
            && (Directory.Exists(Path.Combine(path, "dist"))
                || Directory.Exists(Path.Combine(path, "src")));
    }

    private static IEnumerable<string> GetDevelopmentBundledLibraryRootCandidates(
        string buildRootDirectory,
        BundledLibraryRoot root)
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        var directory = new DirectoryInfo(Path.GetFullPath(buildRootDirectory));
        while (directory is not null)
        {
            if (string.Equals(directory.Name, root.DirectoryName, StringComparison.OrdinalIgnoreCase)
                && TryYieldDirectory(directory.FullName, yielded, out var self))
            {
                yield return self;
            }

            var childRootDirectory = Path.Combine(directory.FullName, root.DirectoryName);
            if (TryYieldDirectory(childRootDirectory, yielded, out var child))
            {
                yield return child;
            }

            directory = directory.Parent;
        }
    }

    private static void AddInstalledBundledLibrarySearchPaths(
        string compilerBaseDirectory,
        BundledLibraryRoot root,
        List<BundledLibrarySearchPath> paths)
    {
        foreach (var rootDirectory in GetInstalledBundledLibraryRootCandidates(compilerBaseDirectory, root))
        {
            AddBundledLibrarySearchDirectories(rootDirectory, root, "installed bundle", paths);
        }
    }

    private static IEnumerable<string> GetInstalledBundledLibraryRootCandidates(
        string compilerBaseDirectory,
        BundledLibraryRoot root)
    {
        var baseDirectory = Path.GetFullPath(compilerBaseDirectory);
        yield return Path.Combine(baseDirectory, root.DirectoryName);

        var parentDirectory = Directory.GetParent(baseDirectory);
        if (parentDirectory is not null)
        {
            yield return Path.Combine(parentDirectory.FullName, root.DirectoryName);
        }
    }

    private static void AddBundledLibrarySearchDirectories(
        string rootDirectory,
        BundledLibraryRoot root,
        string tier,
        List<BundledLibrarySearchPath> paths)
    {
        if (!Directory.Exists(rootDirectory))
        {
            paths.Add(new BundledLibrarySearchPath(
                root,
                tier,
                Path.GetFullPath(rootDirectory),
                IncludeInCompilerSearch: false,
                "missing"));
            return;
        }

        var includedAny = false;
        var distDirectory = Path.Combine(rootDirectory, "dist");
        if (ContainsPackageImages(distDirectory))
        {
            AddDistinctSearchPath(paths, root, tier, distDirectory, "package images");
            includedAny = true;
        }
        else if (Directory.Exists(distDirectory))
        {
            paths.Add(new BundledLibrarySearchPath(
                root,
                tier,
                Path.GetFullPath(distDirectory),
                IncludeInCompilerSearch: false,
                "no package images"));
        }

        if (ContainsPackageImages(rootDirectory, SearchOption.TopDirectoryOnly))
        {
            AddDistinctSearchPath(paths, root, tier, rootDirectory, "package images");
            includedAny = true;
        }

        var sourceDirectory = Path.Combine(rootDirectory, "src");
        if (Directory.Exists(sourceDirectory))
        {
            AddDistinctSearchPath(paths, root, tier, sourceDirectory, "source tree");
            includedAny = true;
        }

        if (!includedAny && !Directory.Exists(distDirectory) && !Directory.Exists(sourceDirectory))
        {
            paths.Add(new BundledLibrarySearchPath(
                root,
                tier,
                Path.GetFullPath(rootDirectory),
                IncludeInCompilerSearch: false,
                "no dist package images or src tree"));
        }
    }

    private static bool ContainsPackageImages(string directory, SearchOption searchOption = SearchOption.AllDirectories)
    {
        return Directory.Exists(directory)
            && (Directory.EnumerateFiles(directory, "*.starkpkg", searchOption).Any(static path => PackageImageBinaryFormat.HasBinaryFileName(path))
                || Directory.EnumerateFiles(directory, "*.starkpkg.json", searchOption).Any());
    }

    private static void AddDistinctSearchPath(
        List<BundledLibrarySearchPath> paths,
        BundledLibraryRoot root,
        string tier,
        string directory,
        string state)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!paths.Any(path => string.Equals(path.Path, fullPath, StringComparison.Ordinal)
                               && path.IncludeInCompilerSearch))
        {
            paths.Add(new BundledLibrarySearchPath(root, tier, fullPath, IncludeInCompilerSearch: true, state));
        }
    }

    private static bool TryYieldDirectory(string directory, HashSet<string> yielded, out string fullPath)
    {
        fullPath = Path.GetFullPath(directory);
        return yielded.Add(fullPath);
    }

    private static bool TryGetBundledLibraryDiscoveryFailureRoot(
        string compilerStderr,
        out BundledLibraryRoot root)
    {
        foreach (var candidate in BundledLibraryRoots)
        {
            if (compilerStderr.Contains($"Unable to resolve imported module '{candidate.ImportRoot}.", StringComparison.Ordinal)
                || compilerStderr.Contains($"Unable to resolve imported module \"{candidate.ImportRoot}.", StringComparison.Ordinal))
            {
                root = candidate;
                return true;
            }
        }

        root = default!;
        return false;
    }

    private static async Task WriteBundledLibraryDiscoveryFailureAsync(
        BuildSession session,
        BundledLibraryRoot root,
        IReadOnlyList<BundledLibrarySearchPath> paths)
    {
        await session.Stderr.WriteLineAsync($"Stark {root.DiagnosticName} discovery failed while resolving a {root.ImportRoot}.* import.");
        await session.Stderr.WriteLineAsync(
            $"Active {root.DiagnosticName} context: profile={(session.Profile == BuildProfile.Release ? "release" : "dev")}, target={session.TargetTriple}, stage={session.StageName}");
        await session.Stderr.WriteLineAsync($"Searched {root.DiagnosticName} paths:");

        foreach (var path in paths)
        {
            var marker = path.IncludeInCompilerSearch ? "included" : "checked";
            await session.Stderr.WriteLineAsync($"  - {path.Tier}: {path.Path} ({marker}, {path.State})");
        }
    }

    private static string GetStageRootDirectory(BuildSession session)
    {
        return Path.Combine(
            GetBuildDirectory(session.BuildRootDirectory),
            session.Profile == BuildProfile.Release ? "release" : "dev",
            NormalizeBuildPathSegment(session.TargetTriple),
            session.StageName);
    }

    private static string GetCleanPath(CleanSession session, CleanScope scope)
    {
        var profilePath = Path.Combine(
            GetBuildDirectory(session.BuildRootDirectory),
            session.Profile == BuildProfile.Release ? "release" : "dev");

        if (scope == CleanScope.Profile)
        {
            return profilePath;
        }

        var targetPath = Path.Combine(profilePath, NormalizeBuildPathSegment(session.TargetTriple!));
        if (scope == CleanScope.Target)
        {
            return targetPath;
        }

        var stagePath = Path.Combine(targetPath, session.StageName);
        return scope switch
        {
            CleanScope.Stage => stagePath,
            CleanScope.Diagnostics => Path.Combine(stagePath, "diagnostics"),
            CleanScope.Artifacts => Path.Combine(stagePath, "artifacts"),
            _ => throw new InvalidOperationException($"Unhandled clean scope '{scope}'.")
        };
    }

    private static string GetBuildDirectory(string buildRootDirectory)
    {
        return Path.Combine(buildRootDirectory, "build");
    }

    private static bool IsPathInsideBuildDirectory(string path, string buildDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullBuildDirectory = Path.GetFullPath(buildDirectory);
        var relativePath = Path.GetRelativePath(fullBuildDirectory, fullPath);
        return !string.IsNullOrWhiteSpace(relativePath)
            && relativePath != "."
            && !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private static string GetProjectArtifactDirectoryName(ProjectManifest project, BuildSession session)
    {
        var relativeDirectory = Path.GetRelativePath(session.BuildRootDirectory, project.DirectoryPath);
        return string.Equals(relativeDirectory, ".", StringComparison.Ordinal)
            ? NormalizeBuildPathSegment(project.Name)
            : NormalizeBuildPathSegment(relativeDirectory);
    }

    private static string NormalizeBuildPathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '+')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static string GetOutputPath(ProjectManifest project, string outputDirectory)
    {
        return project.Kind switch
        {
            ProjectKind.Library => Path.Combine(
                outputDirectory,
                $"{(OperatingSystem.IsWindows() ? string.Empty : "lib")}{project.OutputName}{(OperatingSystem.IsWindows() ? ".lib" : ".a")}"),
            _ => Path.Combine(
                outputDirectory,
                $"{project.OutputName}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}")
        };
    }

    private static bool TryDiscoverManifest(string startDirectory, out DiscoveredManifest? discovery)
    {
        var current = Path.GetFullPath(startDirectory);
        while (true)
        {
            var solutionPath = Path.Combine(current, SolutionManifestFileName);
            if (File.Exists(solutionPath))
            {
                discovery = new DiscoveredManifest(
                    RootDirectory: current,
                    Project: null,
                    Solution: solutionPath);
                return true;
            }

            var projectPath = Path.Combine(current, ProjectManifestFileName);
            if (File.Exists(projectPath))
            {
                discovery = new DiscoveredManifest(
                    RootDirectory: current,
                    Project: projectPath,
                    Solution: null);
                return true;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        discovery = null;
        return false;
    }

    private static UserConfig LoadUserConfig(string startDirectory)
    {
        var nativePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        string? toolchainDirectory = null;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var globalConfigPath = Path.Combine(userProfile, ".config", "stark", "config.toml");
            if (File.Exists(globalConfigPath))
            {
                MergeUserConfig(globalConfigPath, nativePaths, ref toolchainDirectory);
            }
        }

        var directories = new List<string>();
        var current = Path.GetFullPath(startDirectory);
        while (true)
        {
            directories.Add(current);
            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        directories.Reverse();
        foreach (var directory in directories)
        {
            var localConfigPath = Path.Combine(directory, LocalUserConfigFileName);
            if (File.Exists(localConfigPath))
            {
                MergeUserConfig(localConfigPath, nativePaths, ref toolchainDirectory);
            }
        }

        return new UserConfig(nativePaths, toolchainDirectory);
    }

    private static void MergeUserConfig(
        string configPath,
        Dictionary<string, string> nativePaths,
        ref string? toolchainDirectory)
    {
        var document = SimpleToml.ParseFile(configPath);
        if (SimpleToml.TryGetTable(document, ["toolchain"], out var toolchainTable)
            && toolchainTable.TryGetValue("dir", out var configuredToolchainDirectory)
            && configuredToolchainDirectory is string toolchainDirectoryValue
            && !string.IsNullOrWhiteSpace(toolchainDirectoryValue))
        {
            toolchainDirectory = ResolveUserConfigPath(configPath, toolchainDirectoryValue);
        }

        if (!SimpleToml.TryGetTable(document, ["native", "paths"], out var nativePathTable))
        {
            return;
        }

        foreach (var (key, value) in nativePathTable)
        {
            if (value is string textValue && !string.IsNullOrWhiteSpace(textValue))
            {
                nativePaths[$"native.paths.{key}"] = textValue;
            }
        }
    }

    private static string ResolveUserConfigPath(string configPath, string configuredPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        return Path.GetFullPath(
            Path.IsPathRooted(expandedPath)
                ? expandedPath
                : Path.Combine(Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory, expandedPath));
    }

    private static ProjectManifest LoadProjectManifest(string manifestPath, ManifestCache? cache = null)
    {
        cache ??= new ManifestCache();
        if (cache.Projects.TryGetValue(manifestPath, out var cached))
        {
            return cached;
        }

        var document = SimpleToml.ParseFile(manifestPath);
        var directoryPath = Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory;
        var projectTable = SimpleToml.GetRequiredTable(document, ["project"], manifestPath);
        var name = SimpleToml.GetRequiredString(projectTable, "name", manifestPath);
        var kindText = SimpleToml.GetRequiredString(projectTable, "kind", manifestPath);
        var kind = kindText switch
        {
            "library" => ProjectKind.Library,
            "executable" => ProjectKind.Executable,
            "test" => ProjectKind.Test,
            _ => throw new InvalidOperationException(
                $"Project manifest '{manifestPath}' has unsupported kind '{kindText}'.")
        };

        var targetTable = SimpleToml.GetRequiredTable(
            document,
            [kind switch
            {
                ProjectKind.Library => "library",
                ProjectKind.Executable => "executable",
                ProjectKind.Test => "test",
                _ => "executable"
            }],
            manifestPath);
        var rootFile = SimpleToml.GetRequiredString(targetTable, "root", manifestPath);
        var outputName = SimpleToml.GetOptionalString(targetTable, "output") ?? name;

        var dependencies = new Dictionary<string, PathDependencySpec>(StringComparer.Ordinal);
        if (SimpleToml.TryGetTable(document, ["dependencies"], out var dependencyTable))
        {
            foreach (var (dependencyName, value) in dependencyTable)
            {
                if (value is not Dictionary<string, object?> inlineTable
                    || !SimpleToml.TryGetString(inlineTable, "path", out var pathValue))
                {
                    throw new InvalidOperationException(
                        $"Dependency '{dependencyName}' in '{manifestPath}' must use an inline table with a string path.");
                }

                dependencies[dependencyName] = new PathDependencySpec(dependencyName, pathValue);
            }
        }

        NativeDependencyManifest? native = null;
        if (SimpleToml.TryGetTable(document, ["native"], out var nativeTable))
        {
            var sources = SimpleToml.GetOptionalStringArray(nativeTable, "sources");
            var pkgConfigPackages = SimpleToml.GetOptionalStringArray(nativeTable, "pkg-config");

            var fallbackByPlatform = new Dictionary<string, NativeFallbackManifest>(StringComparer.Ordinal);
            if (SimpleToml.TryGetTable(document, ["native", "fallback"], out var fallbackTable))
            {
                foreach (var (platformName, value) in fallbackTable)
                {
                    if (value is not Dictionary<string, object?> platformTable)
                    {
                        continue;
                    }

                    fallbackByPlatform[platformName] = new NativeFallbackManifest(
                        IncludeDirectories: SimpleToml.GetOptionalStringArray(platformTable, "include-dirs"),
                        LibraryDirectories: SimpleToml.GetOptionalStringArray(platformTable, "library-dirs"),
                        Libraries: SimpleToml.GetOptionalStringArray(platformTable, "libraries"));
                }
            }

            native = new NativeDependencyManifest(sources, pkgConfigPackages, fallbackByPlatform);
        }

        var profiles = LoadProfiles(document);
        var project = new ProjectManifest(
            ManifestPath: manifestPath,
            DirectoryPath: directoryPath,
            Name: name,
            Kind: kind,
            RootFile: rootFile,
            OutputName: outputName,
            Dependencies: dependencies,
            Native: native,
            Profiles: profiles);
        cache.Projects[manifestPath] = project;
        return project;
    }

    private static SolutionManifest LoadSolutionManifest(string manifestPath)
    {
        var document = SimpleToml.ParseFile(manifestPath);
        var directoryPath = Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory;
        var solutionTable = SimpleToml.GetRequiredTable(document, ["solution"], manifestPath);
        var name = SimpleToml.GetRequiredString(solutionTable, "name", manifestPath);
        var members = SimpleToml.GetRequiredStringArray(solutionTable, "members", manifestPath);

        List<string> defaultBuildTargets = [];
        string? defaultRunTarget = null;
        List<string> defaultTestTargets = [];
        if (SimpleToml.TryGetTable(document, ["defaults"], out var defaultsTable))
        {
            defaultBuildTargets = SimpleToml.GetOptionalStringArray(defaultsTable, "build");
            defaultRunTarget = SimpleToml.GetOptionalString(defaultsTable, "run");
            defaultTestTargets = SimpleToml.GetOptionalStringArray(defaultsTable, "test");
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (SimpleToml.TryGetTable(document, ["aliases"], out var aliasesTable))
        {
            foreach (var (alias, value) in aliasesTable)
            {
                if (value is string textValue)
                {
                    aliases[alias] = textValue;
                }
            }
        }

        return new SolutionManifest(
            ManifestPath: manifestPath,
            DirectoryPath: directoryPath,
            Name: name,
            Members: members,
            DefaultBuildTargets: defaultBuildTargets,
            DefaultRunTarget: defaultRunTarget,
            DefaultTestTargets: defaultTestTargets,
            Aliases: aliases,
            Profiles: LoadProfiles(document));
    }

    private static Dictionary<BuildProfile, ProfileManifest> LoadProfiles(Dictionary<string, object?> document)
    {
        var profiles = new Dictionary<BuildProfile, ProfileManifest>();
        if (!SimpleToml.TryGetTable(document, ["profiles"], out var profileTable))
        {
            return profiles;
        }

        foreach (var (profileName, value) in profileTable)
        {
            if (value is not Dictionary<string, object?> table)
            {
                continue;
            }

            var buildProfile = profileName switch
            {
                "dev" => BuildProfile.Dev,
                "release" => BuildProfile.Release,
                _ => (BuildProfile?)null
            };

            if (buildProfile is null)
            {
                continue;
            }

            profiles[buildProfile.Value] = new ProfileManifest();
        }

        return profiles;
    }

    private static bool TryParseCommand(string argument, out ProjectCommand command)
    {
        command = argument switch
        {
            "build" => ProjectCommand.Build,
            "run" => ProjectCommand.Run,
            "test" => ProjectCommand.Test,
            "clean" => ProjectCommand.Clean,
            _ => ProjectCommand.None
        };

        return command != ProjectCommand.None;
    }

    private static bool TryResolveCleanScope(string? scopeName, TextWriter stderr, out CleanScope scope)
    {
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            scope = CleanScope.Stage;
            return true;
        }

        scope = scopeName.Trim() switch
        {
            "stage" => CleanScope.Stage,
            "target" => CleanScope.Target,
            "profile" => CleanScope.Profile,
            "diagnostics" => CleanScope.Diagnostics,
            "artifacts" => CleanScope.Artifacts,
            _ => CleanScope.None
        };

        if (scope != CleanScope.None)
        {
            return true;
        }

        stderr.WriteLine("Unknown clean scope. Expected stage, target, profile, diagnostics, or artifacts.");
        return false;
    }

    private static bool RequiresTargetTriple(this CleanScope scope)
    {
        return scope is CleanScope.Target
            or CleanScope.Stage
            or CleanScope.Diagnostics
            or CleanScope.Artifacts;
    }

    private sealed record DiscoveredManifest(
        string RootDirectory,
        string? Project,
        string? Solution);

    private sealed record UserConfig(IReadOnlyDictionary<string, string> NativePaths, string? ToolchainDirectory);

    private static readonly IReadOnlyDictionary<BuildProfile, ProfileManifest> EmptyProfiles =
        new Dictionary<BuildProfile, ProfileManifest>();

    private static readonly BundledLibraryRoot[] BundledLibraryRoots =
    [
        new("System", "stdlib", "stdlib"),
        new("Vendor", "vendor", "vendor library")
    ];

    private sealed class ManifestCache
    {
        public Dictionary<string, ProjectManifest> Projects { get; } = new(StringComparer.Ordinal);
    }

    private sealed record BuildSession(
        BuildProfile Profile,
        string BuildRootDirectory,
        string TargetTriple,
        string StageName,
        string? ToolchainDirectory,
        bool EmitPackageImageJsonInspection,
        UserConfig UserConfig,
        IReadOnlyDictionary<BuildProfile, ProfileManifest> DefaultProfiles,
        IReadOnlyList<string> TestFilters,
        IReadOnlyList<string> TestCollections,
        bool ListTestCollections,
        TextWriter Stdout,
        TextWriter Stderr)
    {
        public ManifestCache ManifestCache { get; } = new();
        public Dictionary<string, BuildResult> BuildResults { get; } = new(StringComparer.Ordinal);
        public List<FileStream> BuildLocks { get; } = [];
    }

    private sealed record CleanSession(
        BuildProfile Profile,
        string BuildRootDirectory,
        string? TargetTriple,
        string StageName,
        TextWriter Stdout,
        TextWriter Stderr);

    private sealed record BuildResult(
        bool Success,
        ProjectManifest Project,
        string OutputDirectory,
        string OutputPath,
        string? PackageSearchDirectory,
        string InputStamp = "");

    private sealed record BundledLibraryRoot(
        string ImportRoot,
        string DirectoryName,
        string DiagnosticName);

    private sealed record BundledLibrarySearchPath(
        BundledLibraryRoot Root,
        string Tier,
        string Path,
        bool IncludeInCompilerSearch,
        string State);

    private sealed record ProjectManifest(
        string ManifestPath,
        string DirectoryPath,
        string Name,
        ProjectKind Kind,
        string RootFile,
        string OutputName,
        IReadOnlyDictionary<string, PathDependencySpec> Dependencies,
        NativeDependencyManifest? Native,
        IReadOnlyDictionary<BuildProfile, ProfileManifest> Profiles);

    private sealed record PathDependencySpec(
        string Name,
        string Path);

    private sealed record SolutionManifest(
        string ManifestPath,
        string DirectoryPath,
        string Name,
        IReadOnlyList<string> Members,
        IReadOnlyList<string> DefaultBuildTargets,
        string? DefaultRunTarget,
        IReadOnlyList<string> DefaultTestTargets,
        IReadOnlyDictionary<string, string> Aliases,
        IReadOnlyDictionary<BuildProfile, ProfileManifest> Profiles);

    private sealed record ProfileManifest;

    private sealed record NativeDependencyManifest(
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> PkgConfigPackages,
        IReadOnlyDictionary<string, NativeFallbackManifest> FallbackByPlatform)
    {
        public NativeFallbackManifest? GetFallbackForCurrentPlatform()
        {
            if (OperatingSystem.IsWindows()
                && FallbackByPlatform.TryGetValue("windows", out var windowsFallback))
            {
                return windowsFallback;
            }

            if (OperatingSystem.IsLinux()
                && FallbackByPlatform.TryGetValue("linux", out var linuxFallback))
            {
                return linuxFallback;
            }

            if (OperatingSystem.IsMacOS()
                && FallbackByPlatform.TryGetValue("macos", out var macFallback))
            {
                return macFallback;
            }

            return null;
        }
    }

    private sealed record NativeFallbackManifest(
        IReadOnlyList<string> IncludeDirectories,
        IReadOnlyList<string> LibraryDirectories,
        IReadOnlyList<string> Libraries);

    private sealed record NativeArgumentResult(
        bool Success,
        IReadOnlyList<string> Arguments)
    {
        public static NativeArgumentResult FromArguments(IReadOnlyList<string> arguments) => new(true, arguments);

        public static NativeArgumentResult Fail(TextWriter stderr, string message, string? detail = null)
        {
            stderr.WriteLine(message);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                stderr.WriteLine(detail);
            }

            return new NativeArgumentResult(false, []);
        }
    }

    private sealed record ProjectCommandOptions(
        BuildProfile Profile,
        string? TargetName,
        string? TargetTriple,
        string StageName,
        string? ToolchainDirectory,
        bool EmitPackageImageJsonInspection,
        bool ShowHelp,
        IReadOnlyList<string> TestFilters,
        IReadOnlyList<string> TestCollections,
        bool ListTestCollections)
    {
        public static ProjectCommandOptions? Parse(ProjectCommand command, string[] args, TextWriter stderr)
        {
            var profile = BuildProfile.Dev;
            string? targetName = null;
            string? targetTriple = null;
            string? toolchainDirectory = null;
            var stageName = "stage0";
            var emitPackageImageJsonInspection = false;
            var showHelp = false;
            var testFilters = new List<string>();
            var testCollections = new List<string>();
            var listTestCollections = false;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "--dev":
                        profile = BuildProfile.Dev;
                        break;
                    case "--release":
                        profile = BuildProfile.Release;
                        break;
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    case "--target":
                        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                        {
                            stderr.WriteLine("--target requires a non-empty target triple.");
                            return null;
                        }

                        targetTriple = args[++index].Trim();
                        break;
                    case "--stage":
                        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                        {
                            stderr.WriteLine("--stage requires one of: stage0, stage1, stage2.");
                            return null;
                        }

                        if (!TryParseStageName(args[++index], stderr, out stageName))
                        {
                            return null;
                        }

                        break;
                    case "--toolchain-dir":
                        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                        {
                            stderr.WriteLine("--toolchain-dir requires a non-empty directory path.");
                            return null;
                        }

                        if (!TryNormalizeToolchainDirectory(args[++index], stderr, out toolchainDirectory))
                        {
                            return null;
                        }

                        break;
                    case "--package-image-json":
                        if (command != ProjectCommand.Build)
                        {
                            stderr.WriteLine("--package-image-json is only valid for `stark build`.");
                            return null;
                        }

                        emitPackageImageJsonInspection = true;
                        break;
                    case "--filter":
                        if (command != ProjectCommand.Test)
                        {
                            stderr.WriteLine("--filter is only valid for `stark test`.");
                            return null;
                        }

                        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                        {
                            stderr.WriteLine("--filter requires a non-empty test name fragment.");
                            return null;
                        }

                        testFilters.Add(args[++index]);
                        break;
                    case "--collection":
                        if (command != ProjectCommand.Test)
                        {
                            stderr.WriteLine("--collection is only valid for `stark test`.");
                            return null;
                        }

                        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                        {
                            stderr.WriteLine("--collection requires one or more collection names (comma-separated values are split).");
                            return null;
                        }

                        foreach (var collectionName in args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!testCollections.Contains(collectionName, StringComparer.Ordinal))
                            {
                                testCollections.Add(collectionName);
                            }
                        }

                        break;
                    case "--list-collections":
                        if (command != ProjectCommand.Test)
                        {
                            stderr.WriteLine("--list-collections is only valid for `stark test`.");
                            return null;
                        }

                        listTestCollections = true;
                        break;
                    default:
                        if (argument.StartsWith("--target=", StringComparison.Ordinal))
                        {
                            var value = argument["--target=".Length..].Trim();
                            if (string.IsNullOrWhiteSpace(value))
                            {
                                stderr.WriteLine("--target requires a non-empty target triple.");
                                return null;
                            }

                            targetTriple = value;
                            break;
                        }

                        if (argument.StartsWith("--stage=", StringComparison.Ordinal))
                        {
                            if (!TryParseStageName(argument["--stage=".Length..], stderr, out stageName))
                            {
                                return null;
                            }

                            break;
                        }

                        if (argument.StartsWith("--toolchain-dir=", StringComparison.Ordinal))
                        {
                            if (!TryNormalizeToolchainDirectory(argument["--toolchain-dir=".Length..], stderr, out toolchainDirectory))
                            {
                                return null;
                            }

                            break;
                        }

                        if (argument.StartsWith("--filter=", StringComparison.Ordinal))
                        {
                            if (command != ProjectCommand.Test)
                            {
                                stderr.WriteLine("--filter is only valid for `stark test`.");
                                return null;
                            }

                            var filter = argument["--filter=".Length..];
                            if (string.IsNullOrWhiteSpace(filter))
                            {
                                stderr.WriteLine("--filter requires a non-empty test name fragment.");
                                return null;
                            }

                            testFilters.Add(filter);
                            break;
                        }

                        if (argument.StartsWith("-", StringComparison.Ordinal))
                        {
                            stderr.WriteLine($"Unknown project command option '{argument}'.");
                            return null;
                        }

                        if (targetName is not null)
                        {
                            stderr.WriteLine("Project commands accept at most one target name.");
                            return null;
                        }

                        targetName = argument;
                        break;
                }
            }

            return new ProjectCommandOptions(
                profile,
                targetName,
                targetTriple,
                stageName,
                toolchainDirectory,
                emitPackageImageJsonInspection,
                showHelp,
                testFilters,
                testCollections,
                listTestCollections);
        }

        private static bool TryNormalizeToolchainDirectory(string value, TextWriter stderr, out string? toolchainDirectory)
        {
            toolchainDirectory = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                stderr.WriteLine("--toolchain-dir requires a non-empty directory path.");
                return false;
            }

            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
            if (!Directory.Exists(fullPath))
            {
                stderr.WriteLine($"Toolchain directory '{fullPath}' was not found.");
                return false;
            }

            toolchainDirectory = fullPath;
            return true;
        }

        private static bool TryParseStageName(string value, TextWriter stderr, out string stageName)
        {
            stageName = value.Trim();
            if (stageName is "stage0" or "stage1" or "stage2")
            {
                return true;
            }

            stderr.WriteLine("--stage requires one of: stage0, stage1, stage2.");
            return false;
        }
    }

    private sealed record TestRunnerBuildResult(
        bool Success,
        bool GeneratedRunner,
        string? GeneratedPath)
    {
        public static TestRunnerBuildResult NotGenerated() => new(true, false, null);
        public static TestRunnerBuildResult Generated(string path) => new(true, true, path);
        public static TestRunnerBuildResult Fail() => new(false, false, null);
    }

    private enum ProjectCommand
    {
        None,
        Build,
        Run,
        Test,
        Clean
    }

    private enum CleanScope
    {
        None,
        Profile,
        Target,
        Stage,
        Diagnostics,
        Artifacts
    }

    private enum ProjectKind
    {
        Library,
        Executable,
        Test
    }

    private enum BuildProfile
    {
        Dev,
        Release
    }

    private static class SimpleToml
    {
        public static Dictionary<string, object?> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path), path);
        }

        public static Dictionary<string, object?> Parse(string text, string sourceName)
        {
            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            Dictionary<string, object?> currentTable = root;
            string? pendingLine = null;
            var pendingLineNumber = 0;

            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var rawLine = StripComments(lines[lineIndex]).Trim();
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                if (pendingLine is not null)
                {
                    pendingLine += "\n" + rawLine;
                    if (!NeedsContinuation(pendingLine))
                    {
                        ParseAssignment(pendingLine, currentTable, sourceName, pendingLineNumber);
                        pendingLine = null;
                    }

                    continue;
                }

                if (rawLine.StartsWith("[", StringComparison.Ordinal)
                    && rawLine.EndsWith("]", StringComparison.Ordinal))
                {
                    currentTable = GetOrCreateTable(root, SplitDottedPath(rawLine[1..^1].Trim()));
                    continue;
                }

                if (NeedsContinuation(rawLine))
                {
                    pendingLine = rawLine;
                    pendingLineNumber = lineIndex + 1;
                    continue;
                }

                ParseAssignment(rawLine, currentTable, sourceName, lineIndex + 1);
            }

            if (pendingLine is not null)
            {
                throw new InvalidOperationException(
                    $"TOML document '{sourceName}' has an unterminated value starting on line {pendingLineNumber}.");
            }

            return root;
        }

        public static Dictionary<string, object?> GetRequiredTable(
            Dictionary<string, object?> root,
            string[] path,
            string sourceName)
        {
            if (!TryGetTable(root, path, out var table))
            {
                throw new InvalidOperationException(
                    $"Manifest '{sourceName}' is missing required table [{string.Join(".", path)}].");
            }

            return table;
        }

        public static bool TryGetTable(
            Dictionary<string, object?> root,
            string[] path,
            out Dictionary<string, object?> table)
        {
            table = root;
            foreach (var segment in path)
            {
                if (!table.TryGetValue(segment, out var value)
                    || value is not Dictionary<string, object?> next)
                {
                    table = null!;
                    return false;
                }

                table = next;
            }

            return true;
        }

        public static string GetRequiredString(
            Dictionary<string, object?> table,
            string key,
            string sourceName)
        {
            if (!TryGetString(table, key, out var value))
            {
                throw new InvalidOperationException(
                    $"Manifest '{sourceName}' is missing required string '{key}'.");
            }

            return value;
        }

        public static bool TryGetString(
            Dictionary<string, object?> table,
            string key,
            out string value)
        {
            if (table.TryGetValue(key, out var rawValue) && rawValue is string text)
            {
                value = text;
                return true;
            }

            value = string.Empty;
            return false;
        }

        public static string? GetOptionalString(Dictionary<string, object?> table, string key)
        {
            return TryGetString(table, key, out var value) ? value : null;
        }

        public static List<string> GetRequiredStringArray(
            Dictionary<string, object?> table,
            string key,
            string sourceName)
        {
            if (!table.TryGetValue(key, out var rawValue)
                || rawValue is not List<object?> values)
            {
                throw new InvalidOperationException(
                    $"Manifest '{sourceName}' is missing required string array '{key}'.");
            }

            return values.Select(value => value as string ?? string.Empty).ToList();
        }

        public static List<string> GetOptionalStringArray(Dictionary<string, object?> table, string key)
        {
            if (!table.TryGetValue(key, out var rawValue)
                || rawValue is not List<object?> values)
            {
                return [];
            }

            return values
                .OfType<string>()
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        public static int? GetOptionalInt32(Dictionary<string, object?> table, string key)
        {
            return table.TryGetValue(key, out var rawValue) && rawValue is int intValue
                ? intValue
                : null;
        }

        private static void ParseAssignment(
            string line,
            Dictionary<string, object?> table,
            string sourceName,
            int lineNumber)
        {
            var equalsIndex = FindUnquoted(line, '=');
            if (equalsIndex <= 0)
            {
                throw new InvalidOperationException(
                    $"TOML document '{sourceName}' has malformed assignment syntax on line {lineNumber}.");
            }

            var key = line[..equalsIndex].Trim();
            var valueText = line[(equalsIndex + 1)..].Trim();
            table[key] = ParseValue(valueText, sourceName, lineNumber);
        }

        private static object? ParseValue(string text, string sourceName, int lineNumber)
        {
            if (text.StartsWith('"') && text.EndsWith('"'))
            {
                return ParseString(text);
            }

            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
            {
                return ParseArray(text, sourceName, lineNumber);
            }

            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
            {
                return ParseInlineTable(text, sourceName, lineNumber);
            }

            if (bool.TryParse(text, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(text, out var intValue))
            {
                return intValue;
            }

            throw new InvalidOperationException(
                $"TOML document '{sourceName}' has unsupported value '{text}' on line {lineNumber}.");
        }

        private static string ParseString(string text)
        {
            var builder = new StringBuilder();
            var escaped = false;
            for (var index = 1; index < text.Length - 1; index++)
            {
                var ch = text[index];
                if (escaped)
                {
                    builder.Append(ch switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => ch
                    });
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        private static List<object?> ParseArray(string text, string sourceName, int lineNumber)
        {
            var inner = text[1..^1];
            var items = SplitTopLevel(inner, ',');
            var values = new List<object?>();
            foreach (var item in items)
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                values.Add(ParseValue(trimmed, sourceName, lineNumber));
            }

            return values;
        }

        private static Dictionary<string, object?> ParseInlineTable(string text, string sourceName, int lineNumber)
        {
            var table = new Dictionary<string, object?>(StringComparer.Ordinal);
            var inner = text[1..^1];
            foreach (var item in SplitTopLevel(inner, ','))
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                ParseAssignment(trimmed, table, sourceName, lineNumber);
            }

            return table;
        }

        private static bool NeedsContinuation(string text)
        {
            var squareDepth = 0;
            var curlyDepth = 0;
            var quote = '\0';
            var escaped = false;

            foreach (var ch in text)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (ch is '\'' or '"')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '[')
                {
                    squareDepth++;
                    continue;
                }

                if (ch == ']')
                {
                    squareDepth--;
                    continue;
                }

                if (ch == '{')
                {
                    curlyDepth++;
                    continue;
                }

                if (ch == '}')
                {
                    curlyDepth--;
                }
            }

            return squareDepth > 0 || curlyDepth > 0;
        }

        private static Dictionary<string, object?> GetOrCreateTable(
            Dictionary<string, object?> root,
            string[] path)
        {
            var current = root;
            foreach (var segment in path)
            {
                if (!current.TryGetValue(segment, out var existing)
                    || existing is not Dictionary<string, object?> next)
                {
                    next = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current[segment] = next;
                }

                current = next;
            }

            return current;
        }

        private static string[] SplitDottedPath(string path)
        {
            return path
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static int FindUnquoted(string text, char target)
        {
            var quote = '\0';
            var escaped = false;
            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (ch is '\'' or '"')
                {
                    quote = ch;
                    continue;
                }

                if (ch == target)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string StripComments(string line)
        {
            var quote = '\0';
            var escaped = false;
            for (var index = 0; index < line.Length; index++)
            {
                var ch = line[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (ch is '\'' or '"')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '#')
                {
                    return line[..index];
                }
            }

            return line;
        }

        private static List<string> SplitTopLevel(string text, char separator)
        {
            var items = new List<string>();
            var current = new StringBuilder();
            var quote = '\0';
            var escaped = false;
            var squareDepth = 0;
            var curlyDepth = 0;

            foreach (var ch in text)
            {
                if (escaped)
                {
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    current.Append(ch);
                    escaped = true;
                    continue;
                }

                if (quote != '\0')
                {
                    current.Append(ch);
                    if (ch == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (ch is '\'' or '"')
                {
                    current.Append(ch);
                    quote = ch;
                    continue;
                }

                if (ch == '[')
                {
                    squareDepth++;
                    current.Append(ch);
                    continue;
                }

                if (ch == ']')
                {
                    squareDepth--;
                    current.Append(ch);
                    continue;
                }

                if (ch == '{')
                {
                    curlyDepth++;
                    current.Append(ch);
                    continue;
                }

                if (ch == '}')
                {
                    curlyDepth--;
                    current.Append(ch);
                    continue;
                }

                if (ch == separator && squareDepth == 0 && curlyDepth == 0)
                {
                    items.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            items.Add(current.ToString());
            return items;
        }
    }
}
