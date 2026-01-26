using Microsoft.Extensions.Logging;
using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Enums;
using SagaOrchestrator.Domain.ValueObjects;
using SagaOrchestrator.Ledger.Contracts;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class CreditReceiverStep : ISagaStep<TransferSagaData>
{
    private readonly ISagaRepository _repository;
    private readonly ILedgerService _ledgerService;
    private readonly ILogger<CreditReceiverStep> _logger;

    // "Employee badge" / worker identity.
    // Unique per instance. Used to prove ownership when completing the lease.
    private readonly string _ownerId = Guid.NewGuid().ToString();

    public CreditReceiverStep(
        ISagaRepository repository,
        ILedgerService ledgerService,
        ILogger<CreditReceiverStep> logger)
    {
        _repository = repository;
        _ledgerService = ledgerService;
        _logger = logger;
    }

    public string Name => "CreditReceiver";

    public async Task ExecuteAsync(TransferSagaData d, CancellationToken ct)
    {
        // KEY #1: Technical step lock (lease).
        // Prevents two workers from executing the step concurrently.
        var stepKey = $"Credit_Step_Lock_{d.SagaId}";

        // TTL should exceed worst-case execution time with buffer.
        // In real world, measure and tune this (plus heartbeats/renewals if needed).
        var leaseDuration = TimeSpan.FromMinutes(2);

        _logger.LogInformation("[CreditReceiver] Checking lease for key: {Key}", stepKey);

        // Try to acquire the lease using our owner badge.
        var claim = await _repository.TryClaimKeyAsync(stepKey, _ownerId, leaseDuration, ct);

        switch (claim)
        {
            case IdempotencyResult.AlreadyConsumed:
                _logger.LogInformation("[CreditReceiver] Step already completed earlier. Skipping.");
                return;

            case IdempotencyResult.LockedByOther:
                _logger.LogInformation("[CreditReceiver] Locked by another worker. Retry later.");
                throw new RetryLaterException($"Lease conflict for {stepKey}");

            case IdempotencyResult.Acquired:
                _logger.LogInformation("[CreditReceiver] Lease acquired. Proceeding with AML + ledger credit...");
                break;

            default:
                // Defensive: if enum grows in the future.
                throw new InvalidOperationException($"Unexpected IdempotencyResult: {claim}");
        }

        try
        {
            // --- BUSINESS LOGIC (AML CHECK) ---
            // If AML fails, this is a non-retryable failure:
            // - we must NOT credit the receiver
            // - saga should fail and trigger compensation of DebitSender
            if (d.Amount > 100000)
            {
                _logger.LogWarning("⛔ [CreditReceiver] AML ALERT: Amount {Amount} exceeds auto-credit limit.", d.Amount);

                // Simulate audit / security review delay
                await Task.Delay(500, ct);

                throw new InvalidOperationException(
                    $"AML Check Failed: Amount {d.Amount} is too high for auto-processing.");
            }

            // KEY #2: Financial idempotency key (ledger key).
            // Protects from double credit even if step lease expires/crashes/retries.
            var ledgerKey = $"Credit_{d.SagaId}";

            _logger.LogInformation("[CreditReceiver] Executing REAL Ledger Credit for {Amount}...", d.Amount);

            var accountId = d.FromUserId;

            // Call ledger (must be idempotent by ledgerKey).
            var result = await _ledgerService.TryCreditAsync(accountId, d.Amount, ledgerKey, ct);

            switch (result)
            {
                case LedgerOperationResult.Success:
                case LedgerOperationResult.IdempotentSuccess:
                    _logger.LogInformation("[CreditReceiver] Ledger credit success.");
                    break;

                case LedgerOperationResult.Conflict:
                    // DB race / optimistic concurrency in ledger -> retry later.
                    _logger.LogInformation("[CreditReceiver] Ledger conflict. Retry later.");
                    throw new RetryLaterException("Ledger conflict / race condition");

                case LedgerOperationResult.Rejected:
                    // Business failure: retries won't help.
                    // Depending on your domain, this may be rare for credit, but handle explicitly.
                    _logger.LogInformation("[CreditReceiver] Ledger rejected credit.");
                    throw new InvalidOperationException("Ledger rejected credit.");

                default:
                    throw new InvalidOperationException($"Unexpected LedgerOperationResult: {result}");
            }

            // Seal the step idempotency in repository (close the "door"),
            // presenting our owner badge.
            await _repository.CompleteKeyAsync(stepKey, _ownerId, ct);

            _logger.LogInformation("[CreditReceiver] Step finalized in Repository.");
        }
        catch
        {
            // Do NOT release lease manually — let it expire.
            // Retrying is handled by the outbox processor / saga retry policy.
            _logger.LogError("[CreditReceiver] Failed/interrupted. Will retry later or fail saga.");
            throw;
        }
    }

    public Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        // CreditReceiver is the last step in the saga.
        // If the saga fails here BEFORE ledger credit succeeded, no self-compensation is needed.
        // If ledger credit succeeded but we crashed BEFORE CompleteKeyAsync,
        // retry will re-run ExecuteAsync and ledgerKey must guarantee idempotent success.
        _logger.LogInformation("[CreditReceiver] Nothing to compensate (End of Saga).");
        return Task.CompletedTask;
    }
}
