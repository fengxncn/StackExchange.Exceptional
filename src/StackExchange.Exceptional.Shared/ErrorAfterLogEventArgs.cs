namespace StackExchange.Exceptional;

/// <summary>
/// Arguments for the event handler called after an exception is logged.
/// </summary>
/// <remarks>
/// Creates an ErrorAfterLogEventArgs object to be passed to event handlers.
/// </remarks>
/// <param name="e">The error to create <see cref="ErrorAfterLogEventArgs"/> for.</param>
public class ErrorAfterLogEventArgs(Error e) : EventArgs
{
    /// <summary>
    /// The Error object in question.
    /// </summary>
    public Error Error { get; } = e;
}
