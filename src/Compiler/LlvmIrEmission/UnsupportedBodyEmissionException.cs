namespace Stark.Compiler.LlvmIrEmission;

internal sealed class UnsupportedBodyEmissionException : Exception
{
    public UnsupportedBodyEmissionException(string message)
        : base(message)
    {
    }
}
