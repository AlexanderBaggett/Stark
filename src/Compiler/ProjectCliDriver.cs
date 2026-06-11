using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Stark.Compiler;

internal static class ProjectCliDriver
{
    private const string ProjectManifestFileName = "Stark.toml";
    private const string SolutionManifestFileName = "Stark.solution.toml";
    private const string LocalUserConfigFileName = "Stark.user.toml";

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
                ProjectCommand.Clean => await ExecuteCleanAsync(discovery!, options, stdout, stderr),
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
            var buildResult = await BuildProjectAsync(project, projectSession);
            return buildResult.Success ? 0 : 1;
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

            var exitCode = await RunTestExecutableAsync(buildResult, stdout, stderr);
            if (exitCode != 0)
            {
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    private static async Task<int> ExecuteCleanAsync(
        DiscoveredManifest discovery,
        ProjectCommandOptions options,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (!TryResolveCleanScope(options.TargetName, stderr, out var scope))
        {
            return 1;
        }

        if (!TryCreateCleanSession(options, discovery.RootDirectory, scope, stdout, stderr, out var session))
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
                await stdout.WriteLineAsync("Usage: stark build [target] [--dev|--release] [--target <triple>] [--stage stage0]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Build the current Stark project or solution.");
                await stdout.WriteLineAsync("- In a project directory, `stark build` builds that project.");
                await stdout.WriteLineAsync("- In a solution directory, `stark build` builds the default solution targets or all members.");
                await stdout.WriteLineAsync("- `target` may be a solution alias, member path, or project name.");
                await stdout.WriteLineAsync("- Outputs are routed under `.stark/build/<profile>/<target-triple>/<stage>/`.");
                return;
            case ProjectCommand.Run:
                await stdout.WriteLineAsync("Usage: stark run [target] [--dev|--release] [--target <triple>] [--stage stage0]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Build and run the current Stark executable project or solution run target.");
                return;
            case ProjectCommand.Test:
                await stdout.WriteLineAsync("Usage: stark test [target] [--dev|--release] [--target <triple>] [--stage stage0]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Build and run Stark test projects.");
                await stdout.WriteLineAsync("- In a test project directory, `stark test` runs that project.");
                await stdout.WriteLineAsync("- In a solution directory, `stark test` runs the default test set or every test project.");
                await stdout.WriteLineAsync("- `--filter <text>` may be repeated; matching is ordinal substring over generated test names.");
                return;
            case ProjectCommand.Clean:
                await stdout.WriteLineAsync("Usage: stark clean [stage|target|profile|diagnostics|artifacts] [--dev|--release] [--target <triple>] [--stage stage0]");
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync("Clean the formal `.stark/build/<profile>/<target-triple>/<stage>/` tree.");
                await stdout.WriteLineAsync("- Default scope is `stage`.");
                await stdout.WriteLineAsync("- `target`, `stage`, `diagnostics`, and `artifacts` use `--target <triple>` or the detected default target.");
                await stdout.WriteLineAsync("- `profile` deletes `.stark/build/<profile>/` and does not require target discovery.");
                return;
        }
    }

    private static async Task<int> RunTestExecutableAsync(BuildResult buildResult, TextWriter stdout, TextWriter stderr)
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
        }

        var outputDirectory = GetOutputDirectory(project, session);
        Directory.CreateDirectory(outputDirectory);

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

        var intermediateDirectory = GetIntermediateDirectory(project, session);
        Directory.CreateDirectory(intermediateDirectory);
        compileArgs.Add("--save-temps");
        compileArgs.Add(intermediateDirectory);

        var stdlibSearchPaths = GetStdlibSearchPaths(session);
        foreach (var stdlibSearchDirectory in stdlibSearchPaths
                     .Where(static path => path.IncludeInCompilerSearch)
                     .Select(static path => path.Path)
                     .Distinct(StringComparer.Ordinal))
        {
            compileArgs.Add("-I");
            compileArgs.Add(stdlibSearchDirectory);
        }

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

        compileArgs.Add(GetOptimizationArgument(project, session));

        var nativeArgsResult = BuildNativeArgs(project, session.UserConfig, session.Stderr);
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
            if (ShouldWriteStdlibDiscoveryFailure(compilerStderrText))
            {
                await WriteStdlibDiscoveryFailureAsync(session, stdlibSearchPaths);
            }

            return RememberFailure(project, session);
        }

        var success = new BuildResult(
            true,
            project,
            outputDirectory,
            outputPath,
            packageSearchDirectory);
        session.BuildResults[project.ManifestPath] = success;
        return success;
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

    private static string GetOptimizationArgument(ProjectManifest project, BuildSession session)
    {
        if (project.Profiles.TryGetValue(session.Profile, out var projectProfile)
            && projectProfile.OptimizationLevel is int projectOptimizationLevel)
        {
            return $"-O{projectOptimizationLevel}";
        }

        if (session.DefaultProfiles.TryGetValue(session.Profile, out var defaultProfile)
            && defaultProfile.OptimizationLevel is int defaultOptimizationLevel)
        {
            return $"-O{defaultOptimizationLevel}";
        }

        return session.Profile == BuildProfile.Release ? "-O3" : "-O0";
    }

    private static BuildResult RememberFailure(ProjectManifest project, BuildSession session)
    {
        var failure = new BuildResult(false, project, string.Empty, string.Empty, null);
        session.BuildResults[project.ManifestPath] = failure;
        return failure;
    }

    private static NativeArgumentResult BuildNativeArgs(ProjectManifest project, UserConfig userConfig, TextWriter stderr)
    {
        if (project.Native is null)
        {
            return NativeArgumentResult.FromArguments([]);
        }

        var arguments = new List<string>();
        foreach (var source in project.Native.Sources)
        {
            arguments.Add("--native-source");
            arguments.Add(Path.GetFullPath(Path.Combine(project.DirectoryPath, source)));
        }

        if (project.Native.PkgConfigPackages.Count != 0
            && ArePkgConfigPackagesAvailable(project.Native.PkgConfigPackages))
        {
            foreach (var package in project.Native.PkgConfigPackages)
            {
                arguments.Add("--native-pkg-config");
                arguments.Add(package);
            }

            return NativeArgumentResult.FromArguments(arguments);
        }

        var fallback = project.Native.GetFallbackForCurrentPlatform();
        if (fallback is null)
        {
            if (project.Native.PkgConfigPackages.Count == 0)
            {
                return NativeArgumentResult.FromArguments(arguments);
            }

            return NativeArgumentResult.Fail(
                stderr,
                $"Project '{project.Name}' needs native package metadata that is available neither through pkg-config nor a platform fallback.");
        }

        foreach (var includeDirectory in fallback.IncludeDirectories)
        {
            if (!TryResolveNativePath(includeDirectory, userConfig, project.DirectoryPath, out var resolved, out var missingKey))
            {
                return NativeArgumentResult.Fail(
                    stderr,
                    $"Project '{project.Name}' needs native path '{missingKey}' to build on this machine.",
                    "Add it under [native.paths] in Stark.user.toml or ~/.config/stark/config.toml.");
            }

            arguments.Add("--native-include-dir");
            arguments.Add(resolved);
        }

        foreach (var libraryDirectory in fallback.LibraryDirectories)
        {
            if (!TryResolveNativePath(libraryDirectory, userConfig, project.DirectoryPath, out var resolved, out var missingKey))
            {
                return NativeArgumentResult.Fail(
                    stderr,
                    $"Project '{project.Name}' needs native path '{missingKey}' to build on this machine.",
                    "Add it under [native.paths] in Stark.user.toml or ~/.config/stark/config.toml.");
            }

            arguments.Add("--native-library-dir");
            arguments.Add(resolved);
        }

        foreach (var library in fallback.Libraries)
        {
            arguments.Add("--native-library");
            arguments.Add(library);
        }

        return NativeArgumentResult.FromArguments(arguments);
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

        var targetTriple = options.TargetTriple;
        if (string.IsNullOrWhiteSpace(targetTriple)
            && NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo))
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
            UserConfig: userConfig,
            DefaultProfiles: defaultProfiles,
            TestFilters: options.TestFilters,
            Stdout: stdout,
            Stderr: stderr);
        return true;
    }

    private static bool TryCreateCleanSession(
        ProjectCommandOptions options,
        string buildRootDirectory,
        CleanScope scope,
        TextWriter stdout,
        TextWriter stderr,
        out CleanSession session)
    {
        session = default!;

        string? targetTriple = null;
        if (scope.RequiresTargetTriple())
        {
            targetTriple = options.TargetTriple;
            if (string.IsNullOrWhiteSpace(targetTriple)
                && NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo))
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

    private static string GetPackageImageFileName(ProjectManifest project)
    {
        return $"lib{project.OutputName}{PackageImageBinaryFormat.FileExtension}";
    }

    private static string GetStageStdlibDirectory(BuildSession session)
    {
        return Path.Combine(GetStageRootDirectory(session), "stdlib");
    }

    private static IReadOnlyList<StdlibSearchPath> GetStdlibSearchPaths(BuildSession session)
    {
        var paths = new List<StdlibSearchPath>();
        var stageStdlibDirectory = GetStageStdlibDirectory(session);
        paths.Add(new StdlibSearchPath(
            "stage/build-local",
            Path.GetFullPath(stageStdlibDirectory),
            IncludeInCompilerSearch: true,
            Directory.Exists(stageStdlibDirectory) ? "present" : "missing"));
        AddDevelopmentStdlibSearchPaths(session.BuildRootDirectory, paths);
        AddInstalledStdlibSearchPaths(AppContext.BaseDirectory, paths);
        return paths;
    }

    private static void AddDevelopmentStdlibSearchPaths(string buildRootDirectory, List<StdlibSearchPath> paths)
    {
        foreach (var stdlibDirectory in GetDevelopmentStdlibRootCandidates(buildRootDirectory))
        {
            if (IsDevelopmentStdlibDirectory(stdlibDirectory))
            {
                AddStdlibSearchDirectories(stdlibDirectory, "repo development", paths);
                return;
            }

            paths.Add(new StdlibSearchPath(
                "repo development",
                Path.GetFullPath(stdlibDirectory),
                IncludeInCompilerSearch: false,
                Directory.Exists(stdlibDirectory) ? "not a stdlib project" : "missing"));
        }
    }

    private static bool IsDevelopmentStdlibDirectory(string path)
    {
        return File.Exists(Path.Combine(path, ProjectManifestFileName))
            && (Directory.Exists(Path.Combine(path, "dist"))
                || Directory.Exists(Path.Combine(path, "src")));
    }

    private static IEnumerable<string> GetDevelopmentStdlibRootCandidates(string buildRootDirectory)
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        var directory = new DirectoryInfo(Path.GetFullPath(buildRootDirectory));
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "stdlib", StringComparison.OrdinalIgnoreCase)
                && TryYieldDirectory(directory.FullName, yielded, out var self))
            {
                yield return self;
            }

            var childStdlibDirectory = Path.Combine(directory.FullName, "stdlib");
            if (TryYieldDirectory(childStdlibDirectory, yielded, out var child))
            {
                yield return child;
            }

            directory = directory.Parent;
        }
    }

    private static void AddInstalledStdlibSearchPaths(string compilerBaseDirectory, List<StdlibSearchPath> paths)
    {
        foreach (var stdlibDirectory in GetInstalledStdlibRootCandidates(compilerBaseDirectory))
        {
            AddStdlibSearchDirectories(stdlibDirectory, "installed bundle", paths);
        }
    }

    private static IEnumerable<string> GetInstalledStdlibRootCandidates(string compilerBaseDirectory)
    {
        var baseDirectory = Path.GetFullPath(compilerBaseDirectory);
        yield return Path.Combine(baseDirectory, "stdlib");

        var parentDirectory = Directory.GetParent(baseDirectory);
        if (parentDirectory is not null)
        {
            yield return Path.Combine(parentDirectory.FullName, "stdlib");
        }
    }

    private static void AddStdlibSearchDirectories(string stdlibDirectory, string tier, List<StdlibSearchPath> paths)
    {
        if (!Directory.Exists(stdlibDirectory))
        {
            paths.Add(new StdlibSearchPath(
                tier,
                Path.GetFullPath(stdlibDirectory),
                IncludeInCompilerSearch: false,
                "missing"));
            return;
        }

        var includedAny = false;
        var distDirectory = Path.Combine(stdlibDirectory, "dist");
        if (ContainsPackageImages(distDirectory))
        {
            AddDistinctSearchPath(paths, tier, distDirectory, "package images");
            includedAny = true;
        }
        else if (Directory.Exists(distDirectory))
        {
            paths.Add(new StdlibSearchPath(
                tier,
                Path.GetFullPath(distDirectory),
                IncludeInCompilerSearch: false,
                "no package images"));
        }

        if (ContainsPackageImages(stdlibDirectory, SearchOption.TopDirectoryOnly))
        {
            AddDistinctSearchPath(paths, tier, stdlibDirectory, "package images");
            includedAny = true;
        }

        var sourceDirectory = Path.Combine(stdlibDirectory, "src");
        if (Directory.Exists(sourceDirectory))
        {
            AddDistinctSearchPath(paths, tier, sourceDirectory, "source tree");
            includedAny = true;
        }

        if (!includedAny && !Directory.Exists(distDirectory) && !Directory.Exists(sourceDirectory))
        {
            paths.Add(new StdlibSearchPath(
                tier,
                Path.GetFullPath(stdlibDirectory),
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

    private static void AddDistinctSearchPath(List<StdlibSearchPath> paths, string tier, string directory, string state)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!paths.Any(path => string.Equals(path.Path, fullPath, StringComparison.Ordinal)
                               && path.IncludeInCompilerSearch))
        {
            paths.Add(new StdlibSearchPath(tier, fullPath, IncludeInCompilerSearch: true, state));
        }
    }

    private static bool TryYieldDirectory(string directory, HashSet<string> yielded, out string fullPath)
    {
        fullPath = Path.GetFullPath(directory);
        return yielded.Add(fullPath);
    }

    private static bool ShouldWriteStdlibDiscoveryFailure(string compilerStderr)
    {
        return compilerStderr.Contains("Unable to resolve imported module 'System.", StringComparison.Ordinal)
            || compilerStderr.Contains("Unable to resolve imported module \"System.", StringComparison.Ordinal);
    }

    private static async Task WriteStdlibDiscoveryFailureAsync(BuildSession session, IReadOnlyList<StdlibSearchPath> paths)
    {
        await session.Stderr.WriteLineAsync("Stark stdlib discovery failed while resolving a System.* import.");
        await session.Stderr.WriteLineAsync(
            $"Active stdlib context: profile={(session.Profile == BuildProfile.Release ? "release" : "dev")}, target={session.TargetTriple}, stage={session.StageName}");
        await session.Stderr.WriteLineAsync("Searched stdlib paths:");

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
        return Path.Combine(buildRootDirectory, ".stark", "build");
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

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var globalConfigPath = Path.Combine(userProfile, ".config", "stark", "config.toml");
            if (File.Exists(globalConfigPath))
            {
                MergeUserConfig(globalConfigPath, nativePaths);
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
                MergeUserConfig(localConfigPath, nativePaths);
            }
        }

        return new UserConfig(nativePaths);
    }

    private static void MergeUserConfig(string configPath, Dictionary<string, string> nativePaths)
    {
        var document = SimpleToml.ParseFile(configPath);
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

            profiles[buildProfile.Value] = new ProfileManifest(SimpleToml.GetOptionalInt32(table, "opt"));
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

    private sealed record UserConfig(IReadOnlyDictionary<string, string> NativePaths);

    private static readonly IReadOnlyDictionary<BuildProfile, ProfileManifest> EmptyProfiles =
        new Dictionary<BuildProfile, ProfileManifest>();

    private sealed class ManifestCache
    {
        public Dictionary<string, ProjectManifest> Projects { get; } = new(StringComparer.Ordinal);
    }

    private sealed record BuildSession(
        BuildProfile Profile,
        string BuildRootDirectory,
        string TargetTriple,
        string StageName,
        UserConfig UserConfig,
        IReadOnlyDictionary<BuildProfile, ProfileManifest> DefaultProfiles,
        IReadOnlyList<string> TestFilters,
        TextWriter Stdout,
        TextWriter Stderr)
    {
        public ManifestCache ManifestCache { get; } = new();
        public Dictionary<string, BuildResult> BuildResults { get; } = new(StringComparer.Ordinal);
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
        string? PackageSearchDirectory);

    private sealed record StdlibSearchPath(
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

    private sealed record ProfileManifest(int? OptimizationLevel);

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
        bool ShowHelp,
        IReadOnlyList<string> TestFilters)
    {
        public static ProjectCommandOptions? Parse(ProjectCommand command, string[] args, TextWriter stderr)
        {
            var profile = BuildProfile.Dev;
            string? targetName = null;
            string? targetTriple = null;
            var stageName = "stage0";
            var showHelp = false;
            var testFilters = new List<string>();

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

            return new ProjectCommandOptions(profile, targetName, targetTriple, stageName, showHelp, testFilters);
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
