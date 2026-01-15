using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Domain.Entities;

public class SagaInstance<TData> where TData : class
{
    public Guid Id { get; private set; }
    public SagaState State { get; private set; }
    public int CurrentStepIndex { get; private set; }
    public TData Data { get; private set; }
    
    // Error log to track what went wrong
    public List<string> ErrorLog { get; private set; } = new();

    // EXPOSED: The Coordinator needs access to the list to pick steps by index
    public List<ISagaStep<TData>> Steps { get; private set; }

    // Constructor for creating a new Saga
    public SagaInstance(Guid id, TData data, List<ISagaStep<TData>> steps)
    {
        Id = id;
        Data = data;
        Steps = steps;
        State = SagaState.Created;
        CurrentStepIndex = 0;
    }

    public void Start()
    {
        State = SagaState.Running;
    }

    // --- State Transition Methods (Used by Coordinator) ---

    public void MoveToNextStep()
    {
        CurrentStepIndex++;
        // Check if we ran out of steps -> Saga is fully completed
        if (CurrentStepIndex >= Steps.Count)
        {
            State = SagaState.Completed;
        }
    }

    public void MoveToPreviousStep()
    {
        CurrentStepIndex--;
        // Check if we rolled back past the first step -> Saga is fully compensated
        if (CurrentStepIndex < 0)
        {
            State = SagaState.Compensated; 
            CurrentStepIndex = 0; // Clamp to 0 just in case
        }
    }

    public void Fail(string error)
    {
        ErrorLog.Add(error);
        // Immediately switch to Compensating mode to start rollback loop
        State = SagaState.Compensating;
    }

    // --- Rehydration Method (Used by Repository) ---
    // This allows the Repository to restore the state from the DB entity
    public void LoadState(SagaState state, int currentStepIndex, List<string>? errorLog)
    {
        State = state;
        CurrentStepIndex = currentStepIndex;
        ErrorLog = errorLog ?? new List<string>();
    }

    // Constructor for EF Core (needs to be protected/private)
    protected SagaInstance()
    {
        Data = default!;
        Steps = default!;
    }
}