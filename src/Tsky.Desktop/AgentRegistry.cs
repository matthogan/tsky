using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tsky;

public enum AgentDefinitionState
{
    Pending,
    Enrolling,
    Enrolled,
    Online,
    Busy,
    Draining,
    Degraded,
    Incompatible,
    Offline,
    Revoked,
    Disabled
}

public sealed class AgentRegistry
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AgentDefinition> Agents { get; set; } = [];
}

public sealed class AgentDefinition
{
    public string AgentId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public AgentDefinitionState State { get; set; } = AgentDefinitionState.Pending;

    public string ControllerId { get; set; } = string.Empty;

    public string? ControllerUrl { get; set; }

    public string? ProtocolVersion { get; set; } = TapProtocolVersioning.CurrentText;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? EnrolledAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? PendingAgentId { get; set; }

    public string? EnrolmentTokenHash { get; set; }

    public DateTime? EnrolmentTokenExpiresAtUtc { get; set; }

    public bool RequiresApproval { get; set; }

    public string? KeyId { get; set; }

    public string? PublicKeyJwkJson { get; set; }

    public string? LastSeenSummary { get; set; }

    public string? LastStateReasonCode { get; set; }

    public string? LastStateReasonMessage { get; set; }

    public string? ProtocolMismatchDetail { get; set; }

    public DateTime? LastSeenAtUtc { get; set; }

    public string? SessionId { get; set; }

    public string? Hostname { get; set; }

    public string? OperatingSystem { get; set; }

    public string? Architecture { get; set; }

    public string? ImplementationName { get; set; }

    public string? ImplementationVersion { get; set; }

    public int ActiveRuns { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }

    public double CpuPercent { get; set; }

    public long MemoryUsedBytes { get; set; }

    public long MemoryTotalBytes { get; set; }

    public long RuntimeDiskFreeBytes { get; set; }

    public bool IsDraining { get; set; }

    public int MaximumConcurrentRuns { get; set; } = 1;

    public List<string> AllowedExecutors { get; set; } = ["process.v1"];

    public List<string> Features { get; set; } = [];

    public List<string> Extensions { get; set; } = [];

    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? AgentId : DisplayName.Trim();
}

public sealed class AgentEnrolmentSecret
{
    public required string PendingAgentId { get; init; }

    public required string Token { get; init; }

    public required DateTime ExpiresAtUtc { get; init; }
}

public sealed class AgentEnrolmentRequest
{
    public string PendingAgentId { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string ControllerId { get; set; } = string.Empty;

    public string? PublicKeyJwkJson { get; set; }

    public string? Hostname { get; set; }

    public string? OperatingSystem { get; set; }

    public string? Architecture { get; set; }

    public string? ImplementationName { get; set; }

    public string? ImplementationVersion { get; set; }
}

public sealed class AgentEnrolmentResult
{
    public bool Succeeded { get; init; }

    public string? AgentId { get; init; }

    public string? KeyId { get; init; }

    public string? ControllerId { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ProtocolVersion { get; init; }

    public List<AgentBindingInfo> SupportedBindings { get; init; } = [];
}

public sealed class AgentBindingInfo
{
    public required string Profile { get; init; }

    public required string Version { get; init; }

    public required string Endpoint { get; init; }

    public bool Optional { get; init; }
}

public sealed class AgentDefinitionSaveResult
{
    public required AgentDefinition Agent { get; init; }

    public AgentEnrolmentSecret? EnrolmentSecret { get; init; }
}

public sealed class AgentRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string RegistryPath { get; }

    public AgentRegistryService(string? registryPath = null)
    {
        RegistryPath = registryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tsky",
            "agents",
            "registry.json");
    }

    public string GetControllerId()
    {
        return GetOrCreateControllerId();
    }

    public AgentRegistry Load()
    {
        var registry = JsonFileStorage.LoadOrDefault<AgentRegistry>(RegistryPath, JsonOptions);
        registry.SchemaVersion = AgentRegistry.CurrentSchemaVersion;
        registry.Agents ??= [];
        foreach (var agent in registry.Agents)
        {
            Normalize(agent);
        }

        return registry;
    }

    public void Save(AgentRegistry registry)
    {
        registry.SchemaVersion = AgentRegistry.CurrentSchemaVersion;
        registry.Agents ??= [];
        foreach (var agent in registry.Agents)
        {
            Normalize(agent);
        }

        JsonFileStorage.Save(RegistryPath, registry, JsonOptions);
    }

    public AgentDefinitionSaveResult AddOrUpdate(AgentRegistry registry, AgentDefinition agent, bool regenerateEnrolmentToken)
    {
        Normalize(agent);
        var enrolmentSecret = regenerateEnrolmentToken ? GenerateEnrolment(agent, TimeSpan.FromHours(2)) : null;
        if (enrolmentSecret is not null && agent.State is AgentDefinitionState.Pending or AgentDefinitionState.Enrolling)
        {
            agent.State = AgentDefinitionState.Pending;
        }

        var existingIndex = registry.Agents.FindIndex(candidate =>
            string.Equals(candidate.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            registry.Agents[existingIndex] = agent;
        }
        else
        {
            registry.Agents.Add(agent);
        }

        Save(registry);
        return new AgentDefinitionSaveResult { Agent = agent, EnrolmentSecret = enrolmentSecret };
    }

    public bool Delete(AgentRegistry registry, string agentId)
    {
        var removed = registry.Agents.RemoveAll(agent =>
            string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save(registry);
        }

        return removed;
    }

    public AgentEnrolmentResult CompleteEnrolment(AgentRegistry registry, AgentEnrolmentRequest request)
    {
        var agent = registry.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.PendingAgentId, request.PendingAgentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return EnrolmentError("pending_agent_not_found", "Pending agent was not found.");
        }

        if (!string.Equals(agent.ControllerId, request.ControllerId, StringComparison.Ordinal))
        {
            return EnrolmentError("controller_mismatch", "Controller ID does not match this desktop.");
        }

        if (string.IsNullOrWhiteSpace(agent.EnrolmentTokenHash)
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(agent.EnrolmentTokenHash),
                Convert.FromHexString(HashToken(request.Token))))
        {
            return EnrolmentError("invalid_token", "Enrolment token is invalid.");
        }

        if (agent.EnrolmentTokenExpiresAtUtc is null || agent.EnrolmentTokenExpiresAtUtc <= DateTime.UtcNow)
        {
            return EnrolmentError("token_expired", "Enrolment token has expired.");
        }

        if (agent.RequiresApproval && agent.State != AgentDefinitionState.Enrolling)
        {
            agent.State = AgentDefinitionState.Enrolling;
            Save(registry);
            return EnrolmentError("approval_required", "Agent enrolment is waiting for approval.");
        }

        agent.State = AgentDefinitionState.Offline;
        agent.KeyId = "key_" + RandomToken(12).ToLowerInvariant();
        agent.PublicKeyJwkJson = string.IsNullOrWhiteSpace(request.PublicKeyJwkJson) ? null : request.PublicKeyJwkJson;
        agent.EnrolledAtUtc = DateTime.UtcNow;
        agent.LastSeenAtUtc = null;
        agent.SessionId = null;
        agent.Hostname = NormalizeOptionalValue(request.Hostname);
        agent.OperatingSystem = NormalizeOptionalValue(request.OperatingSystem);
        agent.Architecture = NormalizeOptionalValue(request.Architecture);
        agent.ImplementationName = NormalizeOptionalValue(request.ImplementationName);
        agent.ImplementationVersion = NormalizeOptionalValue(request.ImplementationVersion);
        agent.ActiveRuns = 0;
        agent.IsDraining = false;
        agent.PendingAgentId = null;
        agent.EnrolmentTokenHash = null;
        agent.EnrolmentTokenExpiresAtUtc = null;
        Save(registry);

        return new AgentEnrolmentResult
        {
            Succeeded = true,
            AgentId = agent.AgentId,
            KeyId = agent.KeyId,
            ControllerId = agent.ControllerId,
            ProtocolVersion = agent.ProtocolVersion,
            SupportedBindings =
            [
                new AgentBindingInfo
                {
                    Profile = "tap-grpc",
                    Version = "1",
                    Endpoint = string.IsNullOrWhiteSpace(agent.ControllerUrl) ? "http://localhost:7341" : agent.ControllerUrl.Trim(),
                    Optional = false
                }
            ]
        };
    }

    public AgentDefinition CreateDraft()
    {
        var id = "agt_" + RandomToken(12).ToLowerInvariant();
        return new AgentDefinition
        {
            AgentId = id,
            DisplayName = "New Agent",
            ControllerId = GetOrCreateControllerId(),
            ControllerUrl = "http://localhost:7341",
            CreatedAtUtc = DateTime.UtcNow,
            State = AgentDefinitionState.Pending
        };
    }

    public AgentEnrolmentSecret GenerateEnrolment(AgentDefinition agent, TimeSpan lifetime)
    {
        agent.PendingAgentId = "agt_pending_" + RandomToken(12).ToLowerInvariant();
        var token = "tsky_enrol_" + RandomToken(32);
        agent.EnrolmentTokenHash = HashToken(token);
        agent.EnrolmentTokenExpiresAtUtc = DateTime.UtcNow.Add(lifetime);
        return new AgentEnrolmentSecret
        {
            PendingAgentId = agent.PendingAgentId,
            Token = token,
            ExpiresAtUtc = agent.EnrolmentTokenExpiresAtUtc.Value
        };
    }

    public static string BuildEnrolmentCommand(AgentDefinition agent, AgentEnrolmentSecret secret)
    {
        var controller = string.IsNullOrWhiteSpace(agent.ControllerUrl) ? "http://localhost:7341" : agent.ControllerUrl.Trim();
        return "tsky-agent enrol " +
               $"--controller \"{controller}\" " +
               $"--pending-agent \"{secret.PendingAgentId}\" " +
               $"--token \"{secret.Token}\" " +
               $"--controller-id \"{agent.ControllerId}\"";
    }

    public static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string GetOrCreateControllerId()
    {
        var path = Path.Combine(Path.GetDirectoryName(RegistryPath) ?? Environment.CurrentDirectory, "controller.json");
        var controller = JsonFileStorage.LoadOrDefault<ControllerIdentityFile>(path, JsonOptions);
        if (!string.IsNullOrWhiteSpace(controller.ControllerId))
        {
            return controller.ControllerId;
        }

        controller.ControllerId = "ctl_" + RandomToken(12).ToLowerInvariant();
        controller.CreatedAtUtc = DateTime.UtcNow;
        JsonFileStorage.Save(path, controller, JsonOptions);
        return controller.ControllerId;
    }

    private static void Normalize(AgentDefinition agent)
    {
        if (string.IsNullOrWhiteSpace(agent.AgentId))
        {
            agent.AgentId = "agt_" + RandomToken(12).ToLowerInvariant();
        }

        agent.DisplayName = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.AgentId : agent.DisplayName.Trim();
        agent.ControllerId = string.IsNullOrWhiteSpace(agent.ControllerId) ? "ctl_local" : agent.ControllerId.Trim();
        agent.ProtocolVersion = string.IsNullOrWhiteSpace(agent.ProtocolVersion) ? TapProtocolVersioning.CurrentText : agent.ProtocolVersion.Trim();
        if (agent.State == AgentDefinitionState.Enrolled)
        {
            agent.State = AgentDefinitionState.Offline;
        }

        agent.Description = NormalizeOptionalValue(agent.Description);
        agent.ControllerUrl = string.IsNullOrWhiteSpace(agent.ControllerUrl) ? "http://localhost:7341" : agent.ControllerUrl.Trim();
        agent.PendingAgentId = NormalizeOptionalValue(agent.PendingAgentId);
        agent.KeyId = NormalizeOptionalValue(agent.KeyId);
        agent.PublicKeyJwkJson = NormalizeOptionalValue(agent.PublicKeyJwkJson);
        agent.LastSeenSummary = NormalizeOptionalValue(agent.LastSeenSummary);
        agent.LastStateReasonCode = NormalizeOptionalValue(agent.LastStateReasonCode);
        agent.LastStateReasonMessage = NormalizeOptionalValue(agent.LastStateReasonMessage);
        agent.ProtocolMismatchDetail = NormalizeOptionalValue(agent.ProtocolMismatchDetail);
        agent.SessionId = NormalizeOptionalValue(agent.SessionId);
        agent.Hostname = NormalizeOptionalValue(agent.Hostname);
        agent.OperatingSystem = NormalizeOptionalValue(agent.OperatingSystem);
        agent.Architecture = NormalizeOptionalValue(agent.Architecture);
        agent.ImplementationName = NormalizeOptionalValue(agent.ImplementationName);
        agent.ImplementationVersion = NormalizeOptionalValue(agent.ImplementationVersion);
        agent.AllowedExecutors = agent.AllowedExecutors
            .Where(executor => !string.IsNullOrWhiteSpace(executor))
            .Select(executor => executor.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("process.v1")
            .ToList();
        agent.SuccessfulRuns = Math.Max(0, agent.SuccessfulRuns);
        agent.FailedRuns = Math.Max(0, agent.FailedRuns);
        agent.MaximumConcurrentRuns = Math.Max(1, agent.MaximumConcurrentRuns);
        agent.ActiveRuns = Math.Max(0, agent.ActiveRuns);
        agent.CpuPercent = Math.Max(0, agent.CpuPercent);
        agent.MemoryUsedBytes = Math.Max(0, agent.MemoryUsedBytes);
        agent.MemoryTotalBytes = Math.Max(0, agent.MemoryTotalBytes);
        agent.RuntimeDiskFreeBytes = Math.Max(0, agent.RuntimeDiskFreeBytes);
        agent.Labels ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		agent.Features ??= [];
		agent.Extensions ??= [];
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string RandomToken(int byteCount)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static AgentEnrolmentResult EnrolmentError(string code, string message)
    {
        return new AgentEnrolmentResult
        {
            Succeeded = false,
            ErrorCode = code,
            ErrorMessage = message
        };
    }

    private sealed class ControllerIdentityFile
    {
        public string? ControllerId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}
