namespace tsky.Tests;

public sealed class AgentRegistryServiceTests
{
    [Fact]
    public void GenerateEnrolment_StoresHashWithoutPlaintextToken()
    {
        var service = new AgentRegistryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "registry.json"));
        var agent = service.CreateDraft();

        var secret = service.GenerateEnrolment(agent, TimeSpan.FromMinutes(30));

        Assert.StartsWith("agt_pending_", secret.PendingAgentId);
        Assert.StartsWith("tsky_enrol_", secret.Token);
        Assert.Equal(secret.PendingAgentId, agent.PendingAgentId);
        Assert.NotEqual(secret.Token, agent.EnrolmentTokenHash);
        Assert.Equal(AgentRegistryService.HashToken(secret.Token), agent.EnrolmentTokenHash);
        Assert.True(agent.EnrolmentTokenExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public void AddOrUpdate_PersistsAndReloadsAgentDefinition()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new AgentRegistryService(Path.Combine(root, "registry.json"));
        var registry = service.Load();
        var agent = service.CreateDraft();
        agent.DisplayName = "Build Agent";

        service.AddOrUpdate(registry, agent, regenerateEnrolmentToken: true);

        var reloaded = service.Load();
        var savedAgent = Assert.Single(reloaded.Agents);
        Assert.Equal("Build Agent", savedAgent.DisplayName);
        Assert.Equal(["process.v1"], savedAgent.AllowedExecutors);
        Assert.NotNull(savedAgent.EnrolmentTokenHash);
    }

    [Fact]
    public void CompleteEnrolment_RegistersKeyAndInvalidatesToken()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new AgentRegistryService(Path.Combine(root, "registry.json"));
        var registry = service.Load();
        var agent = service.CreateDraft();
        var saved = service.AddOrUpdate(registry, agent, regenerateEnrolmentToken: true);

        var result = service.CompleteEnrolment(registry, new AgentEnrolmentRequest
        {
            PendingAgentId = saved.EnrolmentSecret!.PendingAgentId,
            Token = saved.EnrolmentSecret.Token,
            ControllerId = saved.Agent.ControllerId,
            PublicKeyJwkJson = """{"kty":"EC","crv":"P-256"}"""
        });

        Assert.True(result.Succeeded);
        Assert.Equal(saved.Agent.AgentId, result.AgentId);
        Assert.StartsWith("key_", result.KeyId);

        var enrolled = Assert.Single(service.Load().Agents);
        Assert.Equal(AgentDefinitionState.Offline, enrolled.State);
        Assert.Null(enrolled.PendingAgentId);
        Assert.Null(enrolled.EnrolmentTokenHash);
        Assert.Null(enrolled.EnrolmentTokenExpiresAtUtc);
        Assert.Equal("""{"kty":"EC","crv":"P-256"}""", enrolled.PublicKeyJwkJson);
    }

    [Fact]
    public void CompleteEnrolment_RejectsInvalidToken()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new AgentRegistryService(Path.Combine(root, "registry.json"));
        var registry = service.Load();
        var agent = service.CreateDraft();
        var saved = service.AddOrUpdate(registry, agent, regenerateEnrolmentToken: true);

        var result = service.CompleteEnrolment(registry, new AgentEnrolmentRequest
        {
            PendingAgentId = saved.EnrolmentSecret!.PendingAgentId,
            Token = "wrong",
            ControllerId = saved.Agent.ControllerId
        });

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_token", result.ErrorCode);
        Assert.Equal(AgentDefinitionState.Pending, Assert.Single(service.Load().Agents).State);
    }
}
