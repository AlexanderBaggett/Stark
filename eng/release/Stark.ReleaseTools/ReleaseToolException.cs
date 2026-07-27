namespace Stark.ReleaseTools;

internal sealed class ReleaseToolException : Exception
{
    public ReleaseToolException(string message)
        : base(message)
    {
    }

    public ReleaseToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
