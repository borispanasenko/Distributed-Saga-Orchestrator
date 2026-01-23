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

        // Continue compensation after restart
        if (saga.State == SagaState.Compensating || saga.State == SagaState.Failed)
        {
            _logger.LogWarning("Saga {SagaId} is in {State} state. Resuming compensation...", saga.Id, saga.State);
            await RunCompensationAsync(saga, ct);
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
                // 1. Log the permanent failure of the step
                _logger.LogError(ex, "Step {StepName} failed permanently. Initiating compensation...", step.Name);

                // 2. Update saga state to "Compensating" and record the failure reason
                saga.Fail($"{DateTime.UtcNow:o} | {step.Name} | {ex.GetType().Name}: {ex.Message}");
                saga.MarkCompensating();

                // Persist intermediate state to ensure compensation progress is not lost
                await _repository.SaveAsync(saga, ct);

                await RunCompensationAsync(saga, ct);
                return;
            }
        }

        _logger.LogInformation("Saga {SagaId} finished. Final State={State}", saga.Id, saga.State);
    }

    private async Task RunCompensationAsync<TData>(SagaInstance<TData> saga, CancellationToken ct)
        where TData : class
    {
        var compensationFailed = false;

        // 3. Retrieve executed steps in reverse order using the encapsulated API
        foreach (var (index, stepToCompensate) in saga.GetExecutedStepsReverse())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation(
                    "Compensating step {StepName} (Index={Index})...",
                    stepToCompensate.Name,
                    index
                );

                // Execute compensation logic for the step
                await stepToCompensate.CompensateAsync(saga.Data, ct);

                _logger.LogInformation(
                    "Step {StepName} compensated successfully.",
                    stepToCompensate.Name
                );
            }
            catch (RetryLaterException)
            {
                // Compensation cannot proceed at this time.
                // Persist current state and rethrow to allow a retry at a later point.
                _logger.LogWarning(
                    "RetryLater requested during compensation of step {StepName}. Compensation will be retried.",
                    stepToCompensate.Name
                );
                await _repository.SaveAsync(saga, ct);
                throw;
            }
            catch (LostLeaseException lle)
            {
                // Lost lease during compensation.
                // Persist state and rethrow to allow retry by the caller.
                _logger.LogWarning(
                    lle,
                    "Lease was lost during compensation of step {StepName}. Retrying is required.",
                    stepToCompensate.Name
                );
                await _repository.SaveAsync(saga, ct);
                throw;
            }
            catch (Exception compEx)
            {
                // An unexpected error occurred during compensation.
                // Mark the failure and continue compensating remaining steps.
                compensationFailed = true;
                _logger.LogCritical(
                    compEx,
                    "Failed to compensate step {StepName}. Manual intervention is required.",
                    stepToCompensate.Name
                );
                saga.ErrorLog.Add(
                    $"COMPENSATION FAILED: {stepToCompensate.Name} | {compEx.GetType().Name}: {compEx.Message}"
                );
            }
        }

        // 4. Finalize saga status after compensation attempt
        if (compensationFailed)
        {
            // One or more steps failed to compensate successfully
            saga.MarkFatal(
                $"{DateTime.UtcNow:o} | Compensation completed with errors. Manual review is required."
            );
        }
        else
        {
            // All steps were compensated successfully
            saga.MarkCompensated();
        }

        await _repository.SaveAsync(saga, ct);
    }
}