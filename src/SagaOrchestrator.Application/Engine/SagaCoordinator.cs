using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Application.Engine;

public class SagaCoordinator
{
    private readonly ISagaRepository _repository;

    public SagaCoordinator(ISagaRepository repository)
    {
        _repository = repository;
    }

    public async Task ProcessAsync<TData>(SagaInstance<TData> saga, CancellationToken ct = default) 
        where TData : class
    {
        // 1. Save initial state
        await _repository.SaveAsync(saga, ct);

        // 2. Loop while Saga is active (moving forward or rolling back)
        while (saga.State == SagaState.Running || saga.State == SagaState.Compensating)
        {
            if (saga.State == SagaState.Running)
            {
                await ExecuteNextStepAsync(saga, ct);
            }
            else if (saga.State == SagaState.Compensating)
            {
                await CompensateStepAsync(saga, ct);
            }
            
            // 3. Persist state after each step (Checkpoint)
            Console.WriteLine($"   [DB] 💾 Saving state: {saga.State}, Step: {saga.CurrentStepIndex}...");
            await _repository.SaveAsync(saga, ct);
        }
    }

    private async Task ExecuteNextStepAsync<TData>(SagaInstance<TData> saga, CancellationToken ct) 
        where TData : class
    {
        var step = saga.Steps[saga.CurrentStepIndex];
        
        try
        {
            Console.WriteLine($"[EXEC] Executing step: {step.Name}");
            
            // Execute step business logic
            await step.ExecuteAsync(saga.Data, ct);
            
            // SUCCESS: Tell saga to move forward (Encapsulation)
            saga.MoveToNextStep();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR] 💥 Step {step.Name} failed: {ex.Message}");
            
            // ERROR: Tell saga to fail and switch to compensation mode
            saga.Fail(ex.Message); 
        }
    }

    private async Task CompensateStepAsync<TData>(SagaInstance<TData> saga, CancellationToken ct) 
        where TData : class
    {
        // Boundary check: ensure we don't access an invalid index if failed on the last step
        var index = saga.CurrentStepIndex;
        if (index >= saga.Steps.Count) 
        {
            index = saga.Steps.Count - 1;
        }

        var step = saga.Steps[index];

        try
        {
            Console.WriteLine($"[ROLLBACK] ↩️ Compensating step: {step.Name}");
            
            // Execute compensation logic
            await step.CompensateAsync(saga.Data, ct);
            
            // COMPENSATION SUCCESS: Tell saga to move backward
            saga.MoveToPreviousStep();
        }
        catch (Exception ex)
        {
            // Fatal error during compensation. Manual intervention required.
            Console.WriteLine($"[FATAL] Compensation failed at {step.Name}: {ex.Message}");
            throw; 
        }
    }
}