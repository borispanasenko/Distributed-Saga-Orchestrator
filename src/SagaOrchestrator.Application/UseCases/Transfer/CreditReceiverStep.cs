using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Enums;
using SagaOrchestrator.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class CreditReceiverStep : ISagaStep<TransferSagaData>
{
    private readonly ISagaRepository _repository;
    private readonly ILogger<CreditReceiverStep> _logger;

    public CreditReceiverStep(ISagaRepository repository, ILogger<CreditReceiverStep> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public string Name => "CreditReceiver";

    public async Task ExecuteAsync(TransferSagaData d, CancellationToken ct)
    {
        var idempotencyKey = $"Credit_{d.SagaId}";
        var ownerId = Guid.NewGuid().ToString();
        var leaseDuration = TimeSpan.FromMinutes(2);

        _logger.LogInformation("[CreditReceiver] Checking lease for key: {Key}", idempotencyKey);

        var claim = await _repository.TryClaimKeyAsync(idempotencyKey, ownerId, leaseDuration, ct);

        switch (claim)
        {
            case IdempotencyResult.Acquired:
                _logger.LogInformation("[CreditReceiver] Lease acquired. Crediting {Amount}...", d.Amount);
                try
                {
                    if (d.Amount > 100000)
                    {
                        // Audit delay simulation
                        await Task.Delay(500, ct);

                        throw new InvalidOperationException(
                            $"⛔ AML ALERT: Incoming transaction rejected! " +
                            $"Amount {d.Amount} exceeds the limit for automatic crediting for this account.");
                    }

                    await Task.Delay(2000, ct);
                    await _repository.CompleteKeyAsync(idempotencyKey, ownerId, ct);
                    _logger.LogInformation("[CreditReceiver] Credit finalized.");
                    return;
                }
                catch
                {
                    _logger.LogInformation("[CreditReceiver] Failed/interrupted. Will retry later.");
                    throw;
                }

            case IdempotencyResult.AlreadyConsumed:
                _logger.LogInformation("[CreditReceiver] Already completed earlier. Skipping.");
                return;

            case IdempotencyResult.LockedByOther:
                _logger.LogInformation("[CreditReceiver] Locked by another worker. Retry later.");
                throw new RetryLaterException($"Lease conflict for {idempotencyKey}");
        }
    }

    public Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        _logger.LogInformation("[CreditReceiver] Compensating credit {Amount}...", d.Amount);
        return Task.CompletedTask;
    }
}
