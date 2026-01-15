using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaOrchestrator.Application.Engine;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Infrastructure;
// NOTE: Business logic matches the API implementation.
using SagaOrchestrator.Application.UseCases.Transfer;

namespace SagaOrchestrator.ConsoleClient;
    
class Program
{
    static async Task Main(string[] args)
    {
        // Fix encoding for emojis
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        
        // Configuration matches the API setup
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((_, services) => // Use '_' discard to ignore the unused context parameter
            {
                var connectionString = "Host=localhost;Port=5432;Database=sagadb;Username=admin;Password=password";
                
                services.AddInfrastructure(connectionString);
                services.AddTransient<SagaCoordinator>();
            })
            .Build();

        var coordinator = host.Services.GetRequiredService<SagaCoordinator>();
        var repository = host.Services.GetRequiredService<ISagaRepository>();
        
        // Define steps (Shared logic across the system. Repository is injected into DebitSenderStep.)
        var steps = new List<ISagaStep<TransferContext>>
        {
            new DebitSenderStep(repository),
            new CreditReceiverStep()
        };

        // Note the '?' allowing nulls to handle the reset logic cleanly
        SagaInstance<TransferContext>? saga = null;

        while (true)
        {
            Console.WriteLine("\n--- SAGA ADMIN CLI ---");
            Console.WriteLine("1. Create new Saga");
            Console.WriteLine("2. Recover/Resume Saga by ID");
            Console.WriteLine("3. Exit");
            Console.Write("Choice: ");
            var choice = Console.ReadLine() ?? string.Empty;

            if (choice == "3") break;
            
            if (choice == "2")
            {
                // Recovery logic
                Console.Write("Enter Saga ID: ");
                var inputId = Console.ReadLine() ?? string.Empty;
                
                if (Guid.TryParse(inputId, out var id))
                {
                    Console.WriteLine($"[SYSTEM] Loading saga {id} from DB...");
                    saga = await repository.LoadAsync(id, steps);
                    
                    if (saga == null) Console.WriteLine("[ERROR] Saga not found.");
                    else Console.WriteLine($"[SYSTEM] Saga found! State: {saga.State}, Step: {saga.CurrentStepIndex}");
                }
                else
                {
                    Console.WriteLine("[ERROR] Invalid GUID format.");
                    continue;
                }
            }
            else if (choice == "1")
            {
                // Creation logic
            
                // Generate ID once to maintain consistency between Entity and Context
                var newSagaId = Guid.NewGuid();
            
                var context = new TransferContext
                {
                    SagaId = newSagaId,
                    FromUserId = Guid.NewGuid(),
                    ToUserId = Guid.NewGuid(),
                    Amount = 777 // Fixed amount for console testing
                };

                saga = new SagaInstance<TransferContext>(newSagaId, context, steps);
                saga.Start();
                Console.WriteLine($"[CREATED] New Saga ID: {saga.Id}");
            }

            if (saga != null)
            {
                // Start saga processing
                await coordinator.ProcessAsync(saga);
                Console.WriteLine($"[DONE] Final Status: {saga.State}");
                
                // Reset variable for the next iteration
                saga = null;
            }
        }
    }
}