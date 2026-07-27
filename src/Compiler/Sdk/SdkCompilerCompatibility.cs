namespace Stark.Compiler;

/// <summary>
/// Identifies the SDK contract implemented by this compiler. This is a stable
/// compatibility line, not the compiler or SDK release version. A future
/// self-hosted compiler may report the same line when it implements the same
/// manifest, package-image, target, and native-link contracts.
/// </summary>
internal static class SdkCompilerCompatibility
{
    public const string SupportedLine = "stark-sdk-v1";
    public const string PrintOption = "--print-sdk-compatibility";
}
