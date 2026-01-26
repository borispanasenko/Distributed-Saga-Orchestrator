namespace SagaOrchestrator.Ledger.Contracts;

public enum LedgerOperationResult
{
    Success,
    IdempotentSuccess,
    Conflict,
    Rejected
}

public interface ILedgerService
{
    Task<LedgerOperationResult> TryDebitAsync(Guid accountId, decimal amount, string idempotencyKey, CancellationToken ct);
    Task<LedgerOperationResult> TryCreditAsync(Guid accountId, decimal amount, string idempotencyKey, CancellationToken ct);
    Task<LedgerOperationResult> TryCompensateDebitAsync(Guid accountId, decimal amount, string originalDebitIdempotencyKey, CancellationToken ct);
}