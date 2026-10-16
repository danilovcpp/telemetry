namespace DeviceTelemetry.Application.Abstractions.Integrations;

/// <summary>
/// Raised by <see cref="IEmailSender"/> when a message could not be delivered.
/// <see cref="IsTransient"/> tells the caller whether retrying may succeed.
/// </summary>
public sealed class EmailSendException(string message, bool isTransient, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
}
