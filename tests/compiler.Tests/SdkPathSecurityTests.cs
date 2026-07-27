using Stark.Compiler;

namespace compiler.Tests;

public sealed class SdkPathSecurityTests
{
    [Fact]
    public void ManifestPathValidationRejectsChildSymlinkEscape()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symbolic links can require an elevated token on
            // Windows. The production check also covers reparse points there.
            return;
        }

        var sdkRoot = Directory.CreateTempSubdirectory("stark-sdk-path-root-");
        var outsideRoot = Directory.CreateTempSubdirectory("stark-sdk-path-outside-");
        var linkedDirectory = Path.Combine(sdkRoot.FullName, "vendor");
        try
        {
            File.WriteAllText(Path.Combine(outsideRoot.FullName, "package.starkpkg"), "outside");
            Directory.CreateSymbolicLink(linkedDirectory, outsideRoot.FullName);
            var diagnostics = new List<SdkDiagnostic>();

            var valid = SdkManifestPathValidator.TryValidate(
                sdkRoot.FullName,
                "vendor/package.starkpkg",
                "package image",
                diagnostics,
                Path.Combine(sdkRoot.FullName, "sdk.json"));

            Assert.False(valid);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("STK7414", diagnostic.Code);
            Assert.Contains("symbolic link or reparse point", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (File.Exists(linkedDirectory) || Directory.Exists(linkedDirectory))
                {
                    Directory.Delete(linkedDirectory);
                }
            }
            catch
            {
                // Best-effort cleanup for platforms with different link
                // deletion semantics; the temporary parent cleanup follows.
            }

            sdkRoot.Delete(recursive: true);
            outsideRoot.Delete(recursive: true);
        }
    }
}
