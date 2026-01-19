namespace SagaOrchestrator.Application.Exceptions;

/// <summary>
/// Indicates the worker lost lease ownership mid-flight.
/// Usually means TTL was too short or the process stalled.
/// </summary>
public class LostLeaseException : Exception
{
    public LostLeaseException(string message) : base(message) { }
}