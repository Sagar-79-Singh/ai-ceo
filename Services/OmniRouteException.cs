namespace AiCeo.Services;

/// <summary>Raised when the OmniRoute gateway cannot be reached or returns an unusable answer.</summary>
public sealed class OmniRouteException : Exception
{
    public OmniRouteException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
