using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

// ============================================================================
// Program Entry Point
// ============================================================================

// Set up logging
var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// await using - like Python's "async with" - ensures proper cleanup
await using var provider = services.BuildServiceProvider();
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger<Program>();

logger.LogInformation("Starting Repair Planner Agent");

// ============================================================================
// Configuration from Environment Variables
// ============================================================================

// Get Azure OpenAI configuration
// ?? means "if null, use this instead" (like Python's "or")
var aoaiEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ENDPOINT environment variable is required");

var modelDeployment = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? "gpt-4o";

// Get Cosmos DB configuration
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? throw new InvalidOperationException("COSMOS_ENDPOINT environment variable is required");

var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY")
    ?? throw new InvalidOperationException("COSMOS_KEY environment variable is required");

var cosmosDatabase = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME") ?? "FactoryOpsDB";

logger.LogInformation("Configuration loaded: AOAI={Endpoint}, Model={Model}, CosmosDB={Database}",
    aoaiEndpoint, modelDeployment, cosmosDatabase);

// ============================================================================
// Initialize Services
// ============================================================================

// Create Azure OpenAI chat client using DefaultAzureCredential
var aoaiClient = new AzureOpenAIClient(new Uri(aoaiEndpoint), new DefaultAzureCredential());
IChatClient chatClient = aoaiClient.GetChatClient(modelDeployment).AsIChatClient();

// Create Cosmos DB service
var cosmosOptions = new CosmosDbOptions
{
    Endpoint = cosmosEndpoint,
    Key = cosmosKey,
    DatabaseName = cosmosDatabase
};
var cosmosDb = new CosmosDbService(cosmosOptions, loggerFactory.CreateLogger<CosmosDbService>());

// Create fault mapping service
IFaultMappingService faultMapping = new FaultMappingService();

// Create the repair planner agent
var agent = new RepairPlannerAgent(
    chatClient,
    cosmosDb,
    faultMapping,
    loggerFactory.CreateLogger<RepairPlannerAgent>());

// ============================================================================
// Create Sample Fault and Run Workflow
// ============================================================================

// Create a sample diagnosed fault (simulating input from Fault Diagnosis Agent)
var sampleFault = new DiagnosedFault
{
    MachineId = "machine-001",
    MachineName = "Tire Curing Press A1",
    FaultType = "curing_temperature_excessive",
    Severity = "high",
    Description = "Temperature sensor readings exceed safe operating limits. " +
                  "Mold temperature reached 185°C (threshold: 175°C). " +
                  "Risk of product defects and equipment damage if not addressed.",
    DiagnosedAt = DateTime.UtcNow,
    TelemetrySnapshot = new Dictionary<string, double>
    {
        ["mold_temperature"] = 185.0,
        ["bladder_pressure"] = 14.2,
        ["cycle_time"] = 720
    }
};

logger.LogInformation("Processing fault: {FaultType} on {MachineId}",
    sampleFault.FaultType, sampleFault.MachineId);

try
{
    // Run the repair planning workflow
    var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);

    // Display the result
    logger.LogInformation(
        "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
        workOrder.WorkOrderNumber,
        workOrder.Id,
        workOrder.Status,
        workOrder.AssignedTo ?? "unassigned");

    // Pretty-print the work order JSON
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    var json = JsonSerializer.Serialize(workOrder, jsonOptions);
    Console.WriteLine("\n" + json);
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to create work order for fault {FaultType}", sampleFault.FaultType);
    throw;
}
finally
{
    // Clean up Cosmos DB client
    cosmosDb.Dispose();
}

logger.LogInformation("Repair Planner Agent completed");
