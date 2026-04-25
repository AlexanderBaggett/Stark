using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemNetStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceNetFoundationTypesAndCompactLayoutsTypeCheck()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibNetFoundation.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Net
                module Demo

                fn bool StatusOk(System.Net.NetStatus status) {
                    switch (status) {
                        case System.Net.NetStatus.Ok:
                            return true;
                        case System.Net.NetStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[-2147483648 2147483647] ReadResult(System.Net.NetResult<i32[-2147483648 2147483647]> result) {
                    switch (result) {
                        case System.Net.NetResult<i32[-2147483648 2147483647]>.Ok(var value):
                            return value;
                        case System.Net.NetResult<i32[-2147483648 2147483647]>.Err(var error):
                            return 0;
                    }
                }

                fn u8[0 255] FirstOctet(System.Net.IPv4Endpoint endpoint) {
                    return endpoint.Address.A;
                }

                fn u16[0 65535] EndpointPort(System.Net.IPv4Endpoint endpoint) {
                    return endpoint.Port;
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack System.Net.IPv4Address address = new System.Net.IPv4Address() {
                        A = 127,
                        B = 0,
                        C = 0,
                        D = 1
                    };
                    stack System.Net.IPv4Endpoint endpoint = new System.Net.IPv4Endpoint() {
                        Address = address,
                        Port = 8080
                    };
                    stack System.Net.NetResult<i32[-2147483648 2147483647]> result =
                        System.Net.NetResult<i32[-2147483648 2147483647]>.Ok(7);

                    if (!StatusOk(System.Net.NetStatus.Ok)) {
                        return 1;
                    }

                    if (FirstOctet(endpoint) != 127) {
                        return 2;
                    }

                    if (EndpointPort(endpoint) != 8080) {
                        return 3;
                    }

                    return ReadResult(result);
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "enum-layout"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(enumLayoutModel);
        AssertNetLayouts(enumLayoutModel.Layouts);
    }

    [Fact]
    public async Task PackagedStdLibNetFoundationTypesWorkWithoutSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-net-foundation-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg.json");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-pkg", "--package-library-file", libraryFileName, "-o", manifestPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted package image:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                module App

                fn bool StatusOk(System.Net.NetStatus status) {
                    switch (status) {
                        case System.Net.NetStatus.Ok:
                            return true;
                        case System.Net.NetStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[-2147483648 2147483647] ReadResult(System.Net.NetResult<i32[-2147483648 2147483647]> result) {
                    switch (result) {
                        case System.Net.NetResult<i32[-2147483648 2147483647]>.Ok(var value):
                            return value;
                        case System.Net.NetResult<i32[-2147483648 2147483647]>.Err(var error):
                            return 0;
                    }
                }

                fn u16[0 65535] EndpointPort(System.Net.IPv4Endpoint endpoint) {
                    return endpoint.Port;
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack System.Net.IPv4Endpoint endpoint = new System.Net.IPv4Endpoint() {
                        Address = new System.Net.IPv4Address() {
                            A = 127,
                            B = 0,
                            C = 0,
                            D = 1
                        },
                        Port = 8080
                    };
                    stack System.Net.NetResult<i32[-2147483648 2147483647]> result =
                        System.Net.NetResult<i32[-2147483648 2147483647]>.Ok(11);

                    if (!StatusOk(System.Net.NetStatus.Ok)) {
                        return 1;
                    }

                    if (EndpointPort(endpoint) != 8080) {
                        return 2;
                    }

                    return ReadResult(result);
                }
                """;
            await File.WriteAllTextAsync(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(packageDirectory),
                    StopAfterPassId: "enum-layout"));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);
            AssertNetLayouts(enumLayoutModel.Layouts);
        }
        finally
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

    private static void AssertNetLayouts(IReadOnlyDictionary<string, EnumLayoutSymbol> layouts)
    {
        var networkError = layouts["System.Net.NetworkError"];
        AssertCompactTag(networkError, bitWidth: 8, maxTagValue: 8);
        Assert.Equal(["$tag", "$Unknown_0"], networkError.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("i32", networkError.OrderedFields[1].Type.DisplayName);

        var netStatus = layouts["System.Net.NetStatus"];
        AssertCompactTag(netStatus, bitWidth: 8, maxTagValue: 1);
        Assert.Equal(["$tag", "$Err_0"], netStatus.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("System.Net.NetworkError", netStatus.OrderedFields[1].Type.DisplayName);

        var netResult = Assert.Single(
            layouts,
            static layout => layout.Key.StartsWith("System.Net.NetResult<", StringComparison.Ordinal)).Value;
        AssertCompactTag(netResult, bitWidth: 8, maxTagValue: 1);
        Assert.Equal(["$tag", "$Ok_0", "$Err_0"], netResult.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("i32", netResult.OrderedFields[1].Type.DisplayName);
        Assert.Equal("System.Net.NetworkError", netResult.OrderedFields[2].Type.DisplayName);
    }

    private static void AssertCompactTag(EnumLayoutSymbol layout, int bitWidth, int maxTagValue)
    {
        Assert.Equal("$tag", layout.TagField.Name);
        Assert.Equal(StarkTypeKind.Integer, layout.TagField.Type.Kind);
        Assert.Equal(bitWidth, layout.TagField.Type.BitWidth);
        Assert.Equal(System.Numerics.BigInteger.Zero, layout.TagField.Type.RangeMin);
        Assert.Equal(new System.Numerics.BigInteger(maxTagValue), layout.TagField.Type.RangeMax);
    }
}
