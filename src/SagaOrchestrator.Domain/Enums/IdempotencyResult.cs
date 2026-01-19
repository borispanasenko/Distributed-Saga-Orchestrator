namespace SagaOrchestrator.Domain.Enums;

public enum IdempotencyResult
{
    /// <summary>
    /// Lock acquired. Caller owns the lease and may proceed.
    /// </summary>
    Acquired,

    /// <summary>
    /// Operation has already been fully completed in the past.
    /// </summary>
    AlreadyConsumed,

    /// <summary>
    /// Another worker currently holds an active lease. Caller should retry later.
    /// </summary>
    LockedByOther
}