using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

/// <summary>
/// Service for Cosmos DB data access operations.
/// </summary>
public sealed class CosmosDbService : IDisposable
{
    private readonly CosmosClient _client;
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        _logger = logger;

        // Create Cosmos client with recommended settings
        var clientOptions = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };

        _client = new CosmosClient(options.Endpoint, options.Key, clientOptions);
        var database = _client.GetDatabase(options.DatabaseName);

        // Container partition keys:
        // - Technicians: /department
        // - PartsInventory: /category
        // - WorkOrders: /status
        _techniciansContainer = database.GetContainer("Technicians");
        _partsContainer = database.GetContainer("PartsInventory");
        _workOrdersContainer = database.GetContainer("WorkOrders");

        _logger.LogInformation("CosmosDbService initialized for database '{Database}'", options.DatabaseName);
    }

    /// <summary>
    /// Gets available technicians that have at least one of the required skills.
    /// </summary>
    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken ct = default)
    {
        if (requiredSkills.Count == 0)
        {
            _logger.LogWarning("No required skills specified, returning empty list");
            return [];
        }

        try
        {
            // Query technicians who are available and have at least one matching skill
            // Using ARRAY_CONTAINS to check if technician has any required skill
            var skillConditions = string.Join(" OR ",
                requiredSkills.Select((_, i) => $"ARRAY_CONTAINS(c.skills, @skill{i})"));

            var queryText = $@"
                SELECT * FROM c 
                WHERE c.available = true 
                AND ({skillConditions})";

            var queryDef = new QueryDefinition(queryText);
            for (int i = 0; i < requiredSkills.Count; i++)
            {
                queryDef = queryDef.WithParameter($"@skill{i}", requiredSkills[i]);
            }

            var technicians = new List<Technician>();

            // Cross-partition query (department is partition key)
            using var iterator = _techniciansContainer.GetItemQueryIterator<Technician>(queryDef);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                technicians.AddRange(response);
            }

            _logger.LogInformation(
                "Found {Count} available technicians matching skills: {Skills}",
                technicians.Count,
                string.Join(", ", requiredSkills.Take(3)));

            return technicians;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error querying technicians: {StatusCode}", ex.StatusCode);
            throw;
        }
    }

    /// <summary>
    /// Gets parts by their part numbers.
    /// </summary>
    public async Task<List<Part>> GetPartsByNumbersAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
        {
            _logger.LogDebug("No part numbers requested, returning empty list");
            return [];
        }

        try
        {
            // Query parts by part numbers using IN clause
            var queryText = @"
                SELECT * FROM c 
                WHERE ARRAY_CONTAINS(@partNumbers, c.partNumber)";

            var queryDef = new QueryDefinition(queryText)
                .WithParameter("@partNumbers", partNumbers);

            var parts = new List<Part>();

            // Cross-partition query (category is partition key)
            using var iterator = _partsContainer.GetItemQueryIterator<Part>(queryDef);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                parts.AddRange(response);
            }

            _logger.LogInformation(
                "Fetched {Count} parts for {Requested} requested part numbers",
                parts.Count,
                partNumbers.Count);

            // Log any missing parts
            var foundNumbers = parts.Select(p => p.PartNumber).ToHashSet();
            var missing = partNumbers.Where(pn => !foundNumbers.Contains(pn)).ToList();
            if (missing.Count > 0)
            {
                _logger.LogWarning("Parts not found in inventory: {MissingParts}", string.Join(", ", missing));
            }

            return parts;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error fetching parts: {StatusCode}", ex.StatusCode);
            throw;
        }
    }

    /// <summary>
    /// Creates a new work order in Cosmos DB.
    /// </summary>
    public async Task<string> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        try
        {
            // Ensure ID is set (Cosmos DB requires it)
            // ??= means "assign if null" (like Python's: x = x or default_value)
            workOrder.Id ??= Guid.NewGuid().ToString();
            workOrder.Status ??= "new";
            workOrder.CreatedDate = DateTime.UtcNow;

            // Partition key is /status
            var response = await _workOrdersContainer.CreateItemAsync(
                workOrder,
                new PartitionKey(workOrder.Status),
                cancellationToken: ct);

            _logger.LogInformation(
                "Created work order {WorkOrderNumber} (id={Id}, status={Status}, RU={RU})",
                workOrder.WorkOrderNumber,
                workOrder.Id,
                workOrder.Status,
                response.RequestCharge);

            return workOrder.Id;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex,
                "Cosmos DB error creating work order {WorkOrderNumber}: {StatusCode}",
                workOrder.WorkOrderNumber,
                ex.StatusCode);
            throw;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
