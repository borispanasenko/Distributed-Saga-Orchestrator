namespace SagaOrchestrator.Application.Exceptions;

/// <summary>
/// Signals a transient concurrency/lease conflict.
/// This is NOT a bug; it means "retry later".
/// </summary>
public class RetryLaterException : Exception
{
    public RetryLaterException(string message) : base(message) { }
}