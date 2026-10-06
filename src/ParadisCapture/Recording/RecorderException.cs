namespace ParadisCapture;

/// <summary>An error with a message that is safe and useful to show to the user as-is.</summary>
public sealed class RecorderException : Exception
{
    public RecorderException(string userMessage, Exception? inner = null) : base(userMessage, inner) { }

    public string UserMessage => Message;
}
