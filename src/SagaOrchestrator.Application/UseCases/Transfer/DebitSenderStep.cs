using Microsoft.Extensions.Logging;
using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Enums;
using SagaOrchestrator.Domain.ValueObjects;
using SagaOrchestrator.Ledger.Contracts;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class DebitSenderStep : ISagaStep<TransferSagaData>
{
    private readonly ISagaRepository _repository;
    private readonly ILedgerService _ledgerService;
    private readonly ILogger<DebitSenderStep> _logger;

    // "Employee badge" / worker identity.
    // Unique per instance. Used to prove ownership when completing the lease.
    private readonly string _ownerId = Guid.NewGuid().ToString();

    public DebitSenderStep(
        ISagaRepository repository,
        ILedgerService ledgerService,
        ILogger<DebitSenderStep> logger)
    {
        _repository = repository;
        _ledgerService = ledgerService;
        _logger = logger;
    }

    public string Name => "DebitSender";

    public async Task ExecuteAsync(TransferSagaData d, CancellationToken ct)
    {
        // KEY #1: Technical step lock (lease).
        // Prevents two workers from executing the step concurrently.
        var stepKey = $"Debit_Step_Lock_{d.SagaId}";

        // TTL should exceed worst-case execution time with buffer.
        // In real world, measure and tune this (plus heartbeats/renewals if needed).
        var leaseDuration = TimeSpan.FromMinutes(2);

        _logger.LogInformation("[DebitSender] Checking lease for key: {Key}", stepKey);

        // Try to acquire the lease using our owner badge.
        var claim = await _repository.TryClaimKeyAsync(stepKey, _ownerId, leaseDuration, ct);

        switch (claim)
        {
            case IdempotencyResult.AlreadyConsumed:
                _logger.LogInformation("[DebitSender] Step already completed earlier. Skipping.");
                return;

            case IdempotencyResult.LockedByOther:
                _logger.LogInformation("[DebitSender] Locked by another worker. Retry later.");
                throw new RetryLaterException($"Lease conflict for {stepKey}");

            case IdempotencyResult.Acquired:
                _logger.LogInformation("[DebitSender] Lease acquired. Proceeding with ledger debit...");
                break;

            default:
                // Defensive: if enum grows in the future.
                throw new InvalidOperationException($"Unexpected IdempotencyResult: {claim}");
        }

        try
        {
            // KEY #2: Financial idempotency key (ledger key).
            // Protects from double charging even if step lease expires/crashes/retries.
            var ledgerKey = $"Debit_{d.SagaId}";

            _logger.LogInformation("[DebitSender] Executing REAL Ledger Debit for {Amount}...", d.Amount);

            var accountId = d.FromUserId;

            // Call ledger (must be idempotent by ledgerKey).
            var result = await _ledgerService.TryDebitAsync(accountId, d.Amount, ledgerKey, ct);

            switch (result)
            {
                case LedgerOperationResult.Success:
                case LedgerOperationResult.IdempotentSuccess:
                    _logger.LogInformation("[DebitSender] Ledger debit success.");
                    break;

                case LedgerOperationResult.Conflict:
                    // DB race / optimistic concurrency in ledger -> retry later.
                    _logger.LogInformation("[DebitSender] Ledger conflict. Retry later.");
                    throw new RetryLaterException("Ledger conflict / race condition");

                case LedgerOperationResult.Rejected:
                    // Business failure: retries won't help (e.g., insufficient funds).
                    _logger.LogInformation("[DebitSender] Ledger rejected debit (insufficient funds).");
                    throw new InvalidOperationException("Ledger rejected debit: insufficient funds.");

                default:
                    throw new InvalidOperationException($"Unexpected LedgerOperationResult: {result}");
            }

            // Seal the step idempotency in repository (close the "door"),
            // presenting our owner badge. If repo enforces owner, this prevents
            // completing someone else's lease.
            await _repository.CompleteKeyAsync(stepKey, _ownerId, ct);

            _logger.LogInformation("[DebitSender] Step finalized in Repository.");
        }
        catch
        {
            // Do NOT release lease manually — let it expire.
            // Retrying is handled by the outbox processor / saga retry policy.
            _logger.LogError("[DebitSender] Failed/interrupted. Will retry later or fail saga.");
            throw;
        }
    }

    public async Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        _logger.LogWarning("⏪ [DebitSender] COMPENSATING: Refunding {Amount}...", d.Amount);

        // Use the same key as the original debit.
        // Compensation must also be idempotent (refund/tombstone semantics).
        var originalKey = $"Debit_{d.SagaId}";

        var accountId = d.FromUserId;

        // Perform "smart" compensation in ledger (refund or tombstone depending on design).
        var result = await _ledgerService.TryCompensateDebitAsync(accountId, d.Amount, originalKey, ct);

        switch (result)
        {
            case LedgerOperationResult.Success:
            case LedgerOperationResult.IdempotentSuccess:
                _logger.LogInformation("✅ [DebitSender] Compensation registered in Ledger.");
                return;

            case LedgerOperationResult.Conflict:
                // Ledger race -> retry later.
                _logger.LogInformation("[DebitSender] Compensation conflict. Retry later.");
                throw new RetryLaterException("Ledger compensation conflict");

            case LedgerOperationResult.Rejected:
                // Usually should not happen for compensation; treat as non-retryable unless your domain says otherwise.
                throw new InvalidOperationException("Ledger rejected compensation.");

            default:
                throw new InvalidOperationException($"Unexpected LedgerOperationResult: {result}");
        }
    }
}
