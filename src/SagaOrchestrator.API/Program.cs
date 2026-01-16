using Microsoft.AspNetCore.Mvc;
using SagaOrchestrator.Application.Engine;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Infrastructure;
// NOTE: Importing the namespace where Transfer logic resides
using SagaOrchestrator.Application.UseCases.Transfer; 

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// --- 1. Services Setup ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = "Host=localhost;Port=5432;Database=sagadb;Username=admin;Password=password";
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddTransient<SagaCoordinator>();

// --- 2. Build App ---
var app = builder.Build();

// --- 3. Pipeline Setup ---
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// --- 4. Endpoints ---

// POST /transfers - Starts a new saga (Fire-and-Forget)
app.MapPost("/transfers", async (
    [FromBody] TransferRequest request, 
    [FromServices] ISagaRepository repository,
    [FromServices] IServiceScopeFactory scopeFactory) =>
{
    // 1. Generate ID in advance to ensure consistency
    var newSagaId = Guid.NewGuid();
    
    // A. Create Context
    var context = new TransferContext
    {
        SagaId = newSagaId,
        FromUserId = request.FromUserId,
        ToUserId = request.ToUserId,
        Amount = request.Amount
    };

    var initialSteps = new List<ISagaStep<TransferContext>>
    {
        new DebitSenderStep(repository), 
        new CreditReceiverStep(repository)
    };

    // FIX: Use 'newSagaId' here, do not generate a new one, otherwise we lose the link!
    var saga = new SagaInstance<TransferContext>(newSagaId, context, initialSteps);

    // C. Save "Created" state synchronously (while HTTP request is alive)
    await repository.SaveAsync(saga);

    // D. Run in background (Fire-and-Forget)
    _ = Task.Run(async () =>
    {
        // Create a NEW Scope to avoid ObjectDisposedException once the HTTP request ends
        using (var scope = scopeFactory.CreateScope())
        {
            var scopedRepo = scope.ServiceProvider.GetRequiredService<ISagaRepository>();
            var coordinator = scope.ServiceProvider.GetRequiredService<SagaCoordinator>();
            
            // Re-create steps injecting the SCOPED repository
            var backgroundSteps = new List<ISagaStep<TransferContext>>
            {
                new DebitSenderStep(scopedRepo), 
                new CreditReceiverStep(scopedRepo)
            };
            
            var processingSaga = new SagaInstance<TransferContext>(saga.Id, saga.Data, backgroundSteps);
            
            processingSaga.Start();
            await coordinator.ProcessAsync(processingSaga);
        }
    });

    return Results.Accepted(value: new 
    { 
        SagaId = saga.Id, 
        Status = "Started", 
        Link = $"/transfers/{saga.Id}" 
    });
})
.WithName("CreateTransfer")
.WithOpenApi();


// GET /transfers/{id} - Check status
app.MapGet("/transfers/{id}", async (
    Guid id, 
    [FromServices] ISagaRepository repository) =>
{
    // Steps are needed for rehydration (implementation detail)
    var steps = new List<ISagaStep<TransferContext>>
    {
        new DebitSenderStep(repository),
        new CreditReceiverStep(repository)
    };

    var saga = await repository.LoadAsync(id, steps);

    if (saga is null) return Results.NotFound();

    return Results.Ok(new 
    { 
        SagaId = saga.Id, 
        State = saga.State.ToString(),
        CurrentStep = saga.CurrentStepIndex,
        Errors = saga.ErrorLog 
    });
})
.WithName("GetTransferStatus")
.WithOpenApi();

// --- 5. Run ---
app.Run();

// DTO for API Request
record TransferRequest(Guid FromUserId, Guid ToUserId, decimal Amount);