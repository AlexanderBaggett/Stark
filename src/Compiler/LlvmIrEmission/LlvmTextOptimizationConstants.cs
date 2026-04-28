namespace Stark.Compiler.LlvmIrEmission;

internal static class LlvmTextOptimizationConstants
{
    // Keep small literals as scalar stores; larger literals use readonly UTF-32 data
    // plus memcpy to avoid large emitted store sequences. This is a code-size first
    // threshold. April 28, 2026 focused smokes were within ~5% of same-run C across
    // tiny, medium, and large literal cases; retune with c_avg_ratio plus binary_bytes.
    public const int AsciiToUnicodeLiteralMemcpyThresholdCodeUnits = 32;
}
