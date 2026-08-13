using Tsky.Agent.V1;
using Google.Protobuf.WellKnownTypes;

namespace tsky.Tests;

public sealed class TapProtocolVersioningTests
{
    [Fact]
    public void SupportedVersions_AreCurrentThenLegacy()
    {
        var versions = TapProtocolVersioning.SupportedVersions();

        Assert.Collection(
            versions,
            version => Assert.Equal((1U, 1U), (version.Major, version.Minor)),
            version => Assert.Equal((1U, 0U), (version.Major, version.Minor)));
        Assert.Equal((1U, 1U), (TapProtocolVersioning.Current().Major, TapProtocolVersioning.Current().Minor));
    }

    [Fact]
    public void BuildChallenge_SelectsHighestCommonVersion()
    {
        using var fixture = new GatewayFixture();
        var hello = fixture.Hello(
            new ProtocolVersion { Major = 1, Minor = 0 },
            new ProtocolVersion { Major = 1, Minor = 1 });

        var challenge = fixture.Runtime.BuildChallenge(hello, out var error);

        Assert.Null(error);
        Assert.NotNull(challenge);
        Assert.Equal((1U, 1U), (challenge.SelectedTapVersion.Major, challenge.SelectedTapVersion.Minor));
        Assert.Equal("tap-grpc", challenge.SelectedBinding.Profile);
        Assert.Equal("1", challenge.SelectedBinding.Version);
    }

    [Fact]
    public void BuildChallenge_FallsBackToTap10()
    {
        using var fixture = new GatewayFixture();

        var challenge = fixture.Runtime.BuildChallenge(
            fixture.Hello(new ProtocolVersion { Major = 1, Minor = 0 }),
            out var error);

        Assert.Null(error);
        Assert.NotNull(challenge);
        Assert.Equal((1U, 0U), (challenge.SelectedTapVersion.Major, challenge.SelectedTapVersion.Minor));
    }

    [Fact]
    public void BuildChallenge_RejectsRequiredExtensionThatSelectedVersionCannotSupport()
    {
        using var fixture = new GatewayFixture();
        var hello = fixture.Hello(new ProtocolVersion { Major = 1, Minor = 0 });
        hello.Extensions.Add(new ExtensionSupport { Identifier = "schedule.v1", Version = "1", Required = true });

        var challenge = fixture.Runtime.BuildChallenge(hello, out var error);

        Assert.Null(challenge);
        Assert.Equal("incompatible_extension", error?.Code);
    }

    [Fact]
    public void BuildChallenge_RejectsUnsupportedSemanticOrBindingVersion()
    {
        using var semanticFixture = new GatewayFixture();
        var semanticChallenge = semanticFixture.Runtime.BuildChallenge(
            semanticFixture.Hello(new ProtocolVersion { Major = 2, Minor = 0 }),
            out var semanticError);

        Assert.Null(semanticChallenge);
        Assert.Equal("incompatible_protocol_version", semanticError?.Code);

        var recoveryHello = semanticFixture.Hello(new ProtocolVersion { Major = 1, Minor = 1 });
        var recoveryChallenge = Assert.IsType<SessionChallenge>(semanticFixture.Runtime.BuildChallenge(recoveryHello, out var recoveryError));
        Assert.Null(recoveryError);
        semanticFixture.Runtime.MarkSessionAccepted(recoveryHello, recoveryChallenge);
        Assert.Equal(AgentDefinitionState.Online, Assert.Single(semanticFixture.RegistryService.Load().Agents).State);

        using var bindingFixture = new GatewayFixture();
        var bindingHello = bindingFixture.Hello(new ProtocolVersion { Major = 1, Minor = 1 });
        bindingHello.SupportedBindings.Clear();
        bindingHello.SupportedBindings.Add(new ProtocolBinding { Profile = "tap-grpc", Version = "2" });
        var bindingChallenge = bindingFixture.Runtime.BuildChallenge(bindingHello, out var bindingError);

        Assert.Null(bindingChallenge);
        Assert.Equal("incompatible_binding", bindingError?.Code);
    }

    [Fact]
    public void CapabilityReport_CannotActivateUnnegotiatedScheduleExtension()
    {
        using var fixture = new GatewayFixture();
        var hello = fixture.Hello(new ProtocolVersion { Major = 1, Minor = 1 });
        var challenge = Assert.IsType<SessionChallenge>(fixture.Runtime.BuildChallenge(hello, out _));
        fixture.Runtime.MarkSessionAccepted(hello, challenge);
        var report = new CapabilityReport();
        report.Features.Add("schedule.v1");
        report.Extensions.Add(new ExtensionSupport { Identifier = "schedule.v1", Version = "1" });

        fixture.Runtime.ApplyCapabilityReport(hello.AgentId, report);

        var agent = Assert.Single(fixture.RegistryService.Load().Agents);
        Assert.DoesNotContain("schedule.v1", agent.Features, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("schedule.v1", agent.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityReport_DoesNotEraseNegotiatedScheduleExtension()
    {
        using var fixture = new GatewayFixture();
        var hello = fixture.Hello(new ProtocolVersion { Major = 1, Minor = 1 });
        hello.Extensions.Add(new ExtensionSupport { Identifier = "schedule.v1", Version = "1" });
        var challenge = Assert.IsType<SessionChallenge>(fixture.Runtime.BuildChallenge(hello, out _));
        fixture.Runtime.MarkSessionAccepted(hello, challenge);

        fixture.Runtime.ApplyCapabilityReport(hello.AgentId, new CapabilityReport());

        var agent = Assert.Single(fixture.RegistryService.Load().Agents);
        Assert.Contains("schedule.v1", agent.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildChallenge_RejectsShortNonceAndProofValidationRejectsExpiredOrMismatchedSecurityProfile()
    {
        using var fixture = new GatewayFixture();
        var shortNonceHello = fixture.Hello(new ProtocolVersion { Major = 1, Minor = 1 });
        shortNonceHello.AgentNonce = Google.Protobuf.ByteString.CopyFrom(new byte[15]);
        Assert.Null(fixture.Runtime.BuildChallenge(shortNonceHello, out var nonceError));
        Assert.Equal("invalid_nonce", nonceError?.Code);

        var hello = fixture.Hello(new ProtocolVersion { Major = 1, Minor = 1 });
        var challenge = Assert.IsType<SessionChallenge>(fixture.Runtime.BuildChallenge(hello, out _));
        var proof = new SessionProof
        {
            SessionId = challenge.SessionId,
            AgentId = hello.AgentId,
            KeyId = hello.KeyId,
            SecurityProfile = challenge.SecurityProfile
        };
        challenge.ExpiresAt = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-1));
        Assert.False(fixture.Runtime.VerifyProof(challenge, hello, proof, out var expiryError));
        Assert.Equal("challenge_expired", expiryError?.Code);

        challenge.ExpiresAt = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(30));
        proof.SecurityProfile = "unsupported-profile";
        Assert.False(fixture.Runtime.VerifyProof(challenge, hello, proof, out var profileError));
        Assert.Equal("security_profile_mismatch", profileError?.Code);
    }

    private sealed class GatewayFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tsky-tap-version-tests", Guid.NewGuid().ToString("N"));

        public GatewayFixture()
        {
            RegistryService = new AgentRegistryService(Path.Combine(_root, "registry.json"));
            var registry = RegistryService.Load();
            registry.Agents.Add(new AgentDefinition
            {
                AgentId = "agt-version-test",
                DisplayName = "Version test",
                KeyId = "key-version-test",
                State = AgentDefinitionState.Offline
            });
            RegistryService.Save(registry);
            Runtime = new AgentGatewayRuntime(
                RegistryService,
                "ctl-version-test",
                "https://localhost:7341",
                "https://localhost:7341",
                null,
                null);
        }

        public AgentGatewayRuntime Runtime { get; }

        public AgentRegistryService RegistryService { get; }

        public SessionHello Hello(params ProtocolVersion[] versions)
        {
            var hello = new SessionHello
            {
                AgentId = "agt-version-test",
                KeyId = "key-version-test",
                AgentNonce = Google.Protobuf.ByteString.CopyFrom(new byte[16])
            };
            hello.SupportedTapVersions.Add(versions);
            hello.SupportedBindings.Add(new ProtocolBinding { Profile = "tap-grpc", Version = "1" });
            return hello;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
