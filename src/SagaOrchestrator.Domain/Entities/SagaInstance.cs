using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Domain.Entities;

public class SagaInstance<TData> where TData : class
{
    public Guid Id { get; }
    public TData Data { get; }
    public SagaState State { get; private set; }
    public int CurrentStepIndex { get; private set; }
    public List<string> ErrorLog { get; private set; } = new();

    private readonly List<ISagaStep<TData>> _steps;

    public SagaInstance(Guid id, TData data, List<ISagaStep<TData>> steps)
    {
        Id = id;
        Data = data;
        _steps = steps;
        State = SagaState.Created;
        CurrentStepIndex = 0;
    }

    // Terminal states: when the Saga stops processing completely.
    public bool IsTerminal => 
        State == SagaState.Completed || 
        State == SagaState.Compensated || 
        State == SagaState.FatalError;

    public void LoadState(SagaState state, int index, List<string> errors)
    {
        State = state;
        CurrentStepIndex = index;
        ErrorLog = errors ?? new List<string>();

        // Auto-fix: if index is past the end, and we are Running, mark Completed.
        var shouldAutoComplete = 
            CurrentStepIndex >= _steps.Count
            && State is not (SagaState.Compensating or SagaState.Compensated or SagaState.FatalError);
        
        if (shouldAutoComplete) 
            State = SagaState.Completed;
        
    }

    public ISagaStep<TData>? GetCurrentStep()
    {
        if (CurrentStepIndex < _steps.Count)
        {
            return _steps[CurrentStepIndex];
        }
        return null;
    }

    /// <summary>
    /// Transitions from Created to Running.
    /// </summary>
    public void MarkAsRunning()
    {
        if (State == SagaState.Created)
        {
            State = SagaState.Running;
        }
    }

    /// <summary>
    /// Moves cursor forward. Sets State to Completed if finished.
    /// </summary>
    public void Advance()
    {
        if (IsTerminal) return;

        if (CurrentStepIndex < _steps.Count)
        {
            CurrentStepIndex++;
        }

        if (CurrentStepIndex >= _steps.Count)
        {
            State = SagaState.Completed;
        }
    }

    /// <summary>
    /// Marks the Saga as having a Fatal Error (stops execution).
    /// </summary>
    public void Fail(string error)
    {
        // For now, mapping permanent errors to FatalError to stop the Outbox loop.
        // In the future, this would trigger 'Compensating' state.
        State = SagaState.Failed;
        ErrorLog.Add(error);
    }
    
    public IEnumerable<(int Index, ISagaStep<TData> Step)> GetExecutedStepsReverse()
    {
        for (var i = CurrentStepIndex - 1; i >= 0; i--)
        {
            yield return (i, _steps[i]);
        }
    }
    
    public void MarkCompensating()
    {
        State = SagaState.Compensating;
    }

    public void MarkCompensated()
    {
        State = SagaState.Compensated;
    }

    public void MarkFatal(string error)
    {
        State = SagaState.FatalError;
        ErrorLog.Add(error);
    }
}