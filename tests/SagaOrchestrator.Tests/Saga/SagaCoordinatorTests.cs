using Microsoft.Extensions.Logging.Abstractions;
using SagaOrchestrator.Application.Engine;
using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Tests.Saga;

public class SagaCoordinatorTests
{
    [Fact]
    public async Task ProcessAsync_WhenAllStepsSucceed_ShouldCompleteSaga()
    {
        var repository = new FakeSagaRepository();
        var coordinator = CreateCoordinator(repository);

        var firstStep = new TestSagaStep<TransferSagaData>("FirstStep");
        var secondStep = new TestSagaStep<TransferSagaData>("SecondStep");

        var saga = CreateSaga(firstStep, secondStep);

        await coordinator.ProcessAsync(saga, CancellationToken.None);

        Assert.Equal(SagaState.Completed, saga.State);
        Assert.True(saga.IsTerminal);
        Assert.Equal(2, saga.CurrentStepIndex);

        Assert.Equal(1, firstStep.ExecuteCount);
        Assert.Equal(1, secondStep.ExecuteCount);

        Assert.Equal(0, firstStep.CompensateCount);
        Assert.Equal(0, secondStep.CompensateCount);

        Assert.Equal(3, repository.SaveCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenSecondStepFails_ShouldCompensateExecutedSteps()
    {
        var repository = new FakeSagaRepository();
        var coordinator = CreateCoordinator(repository);

        var firstStep = new TestSagaStep<TransferSagaData>("FirstStep");

        var secondStep = new TestSagaStep<TransferSagaData>(
            "SecondStep",
            execute: (_, _) => throw new InvalidOperationException("Permanent failure"));

        var saga = CreateSaga(firstStep, secondStep);

        await coordinator.ProcessAsync(saga, CancellationToken.None);

        Assert.Equal(SagaState.Compensated, saga.State);
        Assert.True(saga.IsTerminal);

        Assert.Equal(1, firstStep.ExecuteCount);
        Assert.Equal(1, secondStep.ExecuteCount);

        Assert.Equal(1, firstStep.CompensateCount);
        Assert.Equal(0, secondStep.CompensateCount);

        Assert.Contains(saga.ErrorLog, error =>
            error.Contains("SecondStep") &&
            error.Contains("Permanent failure"));

        Assert.Equal(4, repository.SaveCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenStepRequestsRetryLater_ShouldKeepSagaRunningAndRethrow()
    {
        var repository = new FakeSagaRepository();
        var coordinator = CreateCoordinator(repository);

        var retryStep = new TestSagaStep<TransferSagaData>(
            "RetryStep",
            execute: (_, _) => throw new RetryLaterException("Try again later"));

        var saga = CreateSaga(retryStep);

        await Assert.ThrowsAsync<RetryLaterException>(() =>
            coordinator.ProcessAsync(saga, CancellationToken.None));

        Assert.Equal(SagaState.Running, saga.State);
        Assert.False(saga.IsTerminal);
        Assert.Equal(0, saga.CurrentStepIndex);

        Assert.Equal(1, retryStep.ExecuteCount);
        Assert.Equal(0, retryStep.CompensateCount);

        Assert.Equal(2, repository.SaveCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenSagaIsAlreadyTerminal_ShouldSkipProcessing()
    {
        var repository = new FakeSagaRepository();
        var coordinator = CreateCoordinator(repository);

        var step = new TestSagaStep<TransferSagaData>("SkippedStep");
        var saga = CreateSaga(step);

        saga.LoadState(SagaState.Completed, 1, []);

        await coordinator.ProcessAsync(saga, CancellationToken.None);

        Assert.Equal(SagaState.Completed, saga.State);
        Assert.True(saga.IsTerminal);

        Assert.Equal(0, step.ExecuteCount);
        Assert.Equal(0, step.CompensateCount);

        Assert.Equal(0, repository.SaveCount);
    }

    private static SagaCoordinator CreateCoordinator(FakeSagaRepository repository)
    {
        return new SagaCoordinator(
            repository,
            NullLogger<SagaCoordinator>.Instance);
    }

    private static SagaInstance<TransferSagaData> CreateSaga(
        params ISagaStep<TransferSagaData>[] steps)
    {
        var sagaId = Guid.NewGuid();

        var data = new TransferSagaData
        {
            SagaId = sagaId,
            FromUserId = Guid.NewGuid(),
            ToUserId = Guid.NewGuid(),
            Amount = 100m
        };

        return new SagaInstance<TransferSagaData>(
            sagaId,
            data,
            steps.ToList());
    }
}