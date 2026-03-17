using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

/// <summary>
/// Main agent that orchestrates repair planning workflow using Azure OpenAI.
/// </summary>
public sealed class RepairPlannerAgent(
    IChatClient chatClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    // System prompt for the repair planner agent
    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Your job is to generate comprehensive repair plans when faults are detected.

        Given information about a diagnosed fault, available technicians, and parts inventory,
        create a detailed work order with repair tasks.

        Output your response as valid JSON matching this schema:
        {
            "workOrderNumber": "WO-YYYY-NNN",
            "machineId": "machine-XXX",
            "faultType": "fault_type_name",
            "title": "Brief descriptive title",
            "description": "Detailed description of the repair work",
            "type": "corrective" | "preventive" | "emergency",
            "priority": "critical" | "high" | "medium" | "low",
            "status": "new",
            "assignedTo": "tech-XXX" or null,
            "estimatedDuration": 90,
            "notes": "Additional notes",
            "partsUsed": [
                { "partId": "part-xxx", "partNumber": "XXX-YYY-ZZZ", "quantity": 1 }
            ],
            "tasks": [
                {
                    "sequence": 1,
                    "title": "Task title",
                    "description": "Detailed task description",
                    "estimatedDurationMinutes": 30,
                    "requiredSkills": ["skill1", "skill2"],
                    "safetyNotes": "Safety precautions"
                }
            ]
        }

        IMPORTANT RULES:
        - estimatedDuration and estimatedDurationMinutes must be integers (e.g., 90), not strings
        - Assign the most qualified available technician based on matching skills
        - If no technicians are available, set assignedTo to null
        - Include only parts that are relevant to the fault; use empty array if none needed
        - Tasks must be ordered sequentially and be actionable
        - Set priority based on fault severity: critical→critical, high→high, medium→medium, low→low
        - Set type to "emergency" for critical severity, "corrective" for others
        - Generate a unique work order number in format WO-YYYY-NNN
        """;

    // JSON options that handle LLM quirks (numbers as strings)
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Plans and creates a work order for a diagnosed fault.
    /// </summary>
    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Planning repair for {MachineId}, fault={FaultType}, severity={Severity}",
            fault.MachineId,
            fault.FaultType,
            fault.Severity);

        // 1. Get required skills and parts from fault mapping
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredPartNumbers = faultMapping.GetRequiredParts(fault.FaultType);

        logger.LogDebug(
            "Fault '{FaultType}' requires skills: {Skills}, parts: {Parts}",
            fault.FaultType,
            string.Join(", ", requiredSkills.Take(3)),
            string.Join(", ", requiredPartNumbers));

        // 2. Query Cosmos DB for available technicians and parts
        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var parts = await cosmosDb.GetPartsByNumbersAsync(requiredPartNumbers, ct);

        logger.LogInformation(
            "Found {TechCount} available technicians and {PartCount} parts",
            technicians.Count,
            parts.Count);

        // 3. Build prompt with context
        var userPrompt = BuildPrompt(fault, technicians, parts, requiredSkills);

        // 4. Invoke the LLM via IChatClient
        logger.LogInformation("Invoking agent '{AgentName}'", AgentName);

        // Build chat messages: system instructions + user prompt
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AgentInstructions),
            new(ChatRole.User, userPrompt)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var responseText = response.Text ?? "";
        logger.LogDebug("Agent response length: {Length} chars", responseText.Length);

        // 5. Parse the response
        var workOrder = ParseWorkOrderResponse(responseText, fault);

        // 6. Apply defaults and save to Cosmos DB
        ApplyDefaults(workOrder, fault);

        var savedId = await cosmosDb.CreateWorkOrderAsync(workOrder, ct);
        workOrder.Id = savedId;

        logger.LogInformation(
            "Created work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
            workOrder.WorkOrderNumber,
            workOrder.Id,
            workOrder.Status,
            workOrder.AssignedTo ?? "unassigned");

        return workOrder;
    }

    /// <summary>
    /// Builds the prompt with fault info, technicians, and parts context.
    /// </summary>
    private static string BuildPrompt(
        DiagnosedFault fault,
        List<Technician> technicians,
        List<Part> parts,
        IReadOnlyList<string> requiredSkills)
    {
        var techniciansSummary = technicians.Count > 0
            ? string.Join("\n", technicians.Select(t =>
                $"- {t.Id}: {t.Name} ({t.Role}), skills: [{string.Join(", ", t.Skills)}], shift: {t.ShiftSchedule}"))
            : "No technicians currently available with required skills.";

        var partsSummary = parts.Count > 0
            ? string.Join("\n", parts.Select(p =>
                $"- {p.PartNumber}: {p.Name}, in stock: {p.QuantityInStock}, location: {p.Location}"))
            : "No parts required or available for this fault type.";

        return $"""
            ## Diagnosed Fault
            - Machine ID: {fault.MachineId}
            - Machine Name: {fault.MachineName}
            - Fault Type: {fault.FaultType}
            - Severity: {fault.Severity}
            - Description: {fault.Description}
            - Diagnosed At: {fault.DiagnosedAt:u}

            ## Required Skills
            {string.Join(", ", requiredSkills)}

            ## Available Technicians
            {techniciansSummary}

            ## Parts Inventory
            {partsSummary}

            Generate a complete work order with detailed repair tasks. Respond with JSON only.
            """;
    }

    /// <summary>
    /// Parses the LLM response into a WorkOrder object.
    /// </summary>
    private WorkOrder ParseWorkOrderResponse(string responseText, DiagnosedFault fault)
    {
        try
        {
            // Extract JSON from response (LLM might wrap it in markdown code blocks)
            var json = ExtractJson(responseText);

            var workOrder = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions);
            if (workOrder is null)
            {
                throw new InvalidOperationException("Deserialized work order is null");
            }

            return workOrder;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM response as JSON, creating fallback work order");

            // Create a fallback work order if parsing fails
            return CreateFallbackWorkOrder(fault, responseText);
        }
    }

    /// <summary>
    /// Extracts JSON from a response that might be wrapped in markdown code blocks.
    /// </summary>
    private static string ExtractJson(string text)
    {
        // Remove markdown code blocks if present
        var trimmed = text.Trim();

        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var endIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (endIndex > 7)
            {
                return trimmed[7..endIndex].Trim();
            }
        }

        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var endIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && endIndex > firstNewline)
            {
                return trimmed[(firstNewline + 1)..endIndex].Trim();
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Creates a fallback work order when LLM response parsing fails.
    /// </summary>
    private static WorkOrder CreateFallbackWorkOrder(DiagnosedFault fault, string llmResponse)
    {
        return new WorkOrder
        {
            WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyy}-{DateTime.UtcNow.Ticks % 1000:D3}",
            MachineId = fault.MachineId,
            FaultType = fault.FaultType,
            Title = $"Repair: {fault.FaultType.Replace("_", " ")}",
            Description = fault.Description,
            Type = fault.Severity == "critical" ? "emergency" : "corrective",
            Priority = fault.Severity,
            Status = "new",
            EstimatedDuration = 120,
            Notes = $"Auto-generated fallback. LLM response: {llmResponse[..Math.Min(500, llmResponse.Length)]}...",
            Tasks =
            [
                new RepairTask
                {
                    Sequence = 1,
                    Title = "Diagnose and repair",
                    Description = $"Investigate and repair {fault.FaultType} on {fault.MachineId}",
                    EstimatedDurationMinutes = 120,
                    RequiredSkills = ["general_maintenance"],
                    SafetyNotes = "Follow standard lockout/tagout procedures"
                }
            ]
        };
    }

    /// <summary>
    /// Applies default values to the work order.
    /// </summary>
    private static void ApplyDefaults(WorkOrder workOrder, DiagnosedFault fault)
    {
        // Ensure required fields have values
        // ??= means "assign if null" (like Python's: x = x or default_value)
        workOrder.Id ??= Guid.NewGuid().ToString();
        workOrder.MachineId ??= fault.MachineId;
        workOrder.FaultType ??= fault.FaultType;
        workOrder.Status ??= "new";
        workOrder.Priority ??= fault.Severity;
        workOrder.Type ??= fault.Severity == "critical" ? "emergency" : "corrective";
        workOrder.CreatedDate = DateTime.UtcNow;
        workOrder.PartsUsed ??= [];
        workOrder.Tasks ??= [];

        // Generate work order number if not set
        if (string.IsNullOrEmpty(workOrder.WorkOrderNumber))
        {
            workOrder.WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyy}-{DateTime.UtcNow.Ticks % 1000:D3}";
        }
    }
}
