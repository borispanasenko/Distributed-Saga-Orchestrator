using Microsoft.Extensions.Logging;
using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Application.Engine;

public sealed class SagaCoordinator
{
    private readonly ISagaRepository _repository;
    private readonly ILogger<SagaCoordinator> _logger;

    public SagaCoordinator(ISagaRepository repository, ILogger<SagaCoordinator> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task ProcessAsync<TData>(SagaInstance<TData> saga, CancellationToken ct)
        where TData : class
    {
        _logger.LogInformation("Processing Saga {SagaId}. State={State}, StepIndex={Index}",
            saga.Id, saga.State, saga.CurrentStepIndex);

        // Terminal state guard (idempotency)
        if (saga.IsTerminal)
        {
            _logger.LogInformation("Saga {SagaId} already finished ({State}). Skipping.", saga.Id, saga.State);
            return;
        }

        // Mark as in progress once (optional but clean)
        if (saga.State == SagaState.Created)
        {
            saga.MarkAsRunning();
            await _repository.SaveAsync(saga, ct);
        }

        while (!saga.IsTerminal)
        {
            ct.ThrowIfCancellationRequested();

            var step = saga.GetCurrentStep();

            // If no steps left, mark completed cleanly (avoid index++ past end)
            if (step == null)
            {
                _logger.LogInformation("Saga {SagaId} has no remaining steps. Marking Completed.", saga.Id);
                // "Complete without overshooting"
                saga.LoadState(SagaState.Completed, saga.CurrentStepIndex, saga.ErrorLog);
                await _repository.SaveAsync(saga, ct);
                return;
            }

            _logger.LogInformation("Executing step {StepName} (Index={Index}) for Saga {SagaId}",
                step.Name, saga.CurrentStepIndex, saga.Id);

            try
            {
                await step.ExecuteAsync(saga.Data, ct);

                saga.Advance();
                await _repository.SaveAsync(saga, ct);

                _logger.LogInformation("Step {StepName} succeeded. Checkpoint saved.", step.Name);
            }
            catch (RetryLaterException)
            {
                // Normal transient flow control
                _logger.LogInformation("Step {StepName} requested RetryLater. Stopping saga execution.", step.Name);
                await _repository.SaveAsync(saga, ct); // keep cursor unchanged
                throw;
            }
            catch (LostLeaseException ex)
            {
                // Transient: lease expired mid-flight, idempotency must make retry safe
                _logger.LogWarning(ex, "Lost lease during step {StepName}. Will retry.", step.Name);
                await _repository.SaveAsync(saga, ct);
                throw;
            }
            catch (Exception ex)
            {
                // Permanent failure -> mark saga failed, and STOP (do NOT retry endlessly)
                _logger.LogError(ex, "Step {StepName} failed permanently. Marking saga as Failed.", step.Name);

                saga.Fail($"{DateTime.UtcNow:o} | {step.Name} | {ex.GetType().Name}: {ex.Message}");
                await _repository.SaveAsync(saga, ct);

                // Important: return, so OutboxProcessor can mark message processed.
                return;
            }
        }

        _logger.LogInformation("Saga {SagaId} finished. Final State={State}", saga.Id, saga.State);
    }
}