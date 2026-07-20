using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tsky.Agent.V1;

namespace tsky;

public sealed class AgentGatewayRuntimeEvent
{
    public required string AgentId { get; init; }

    public RunAccepted? RunAccepted { get; init; }

    public RunRejected? RunRejected { get; init; }

    public RunEvent? RunEvent { get; init; }

    public RunOutput? RunOutput { get; init; }

    public RunCompleted? RunCompleted { get; init; }

    public ReconciliationResponse? ReconciliationResponse { get; init; }

    public ArtifactUploadCompleted? ArtifactUploadCompleted { get; init; }

    public ArtifactUploadFailed? ArtifactUploadFailed { get; init; }
}

public sealed class AgentGatewayHost : IAsyncDisposable
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(35);
    private readonly AgentRegistryService _registryService;
    private readonly Action<AgentGatewayRuntimeEvent>? _onAgentEvent;
    private AgentGatewaySettings _settings;
    private AgentGatewayRuntime? _runtime;
    private WebApplication? _app;

    public const int DefaultPort = 7341;

    public AgentGatewayHost(AgentRegistryService registryService, AgentGatewaySettings settings, Action<AgentGatewayRuntimeEvent>? onAgentEvent = null)
    {
        _registryService = registryService;
        _settings = settings;
        _onAgentEvent = onAgentEvent;
    }

    public bool IsRunning => _app is not null;

    public void ApplySettings(AgentGatewaySettings settings)
    {
        _settings = settings;
    }

    public bool TryDispatchRunOffer(string agentId, RunOffer offer, out string reason)
    {
        if (_runtime is null)
        {
            reason = "Gateway runtime is not available.";
            return false;
        }

        return _runtime.TryDispatchRunOffer(agentId, offer, out reason);
    }

    public bool TrySendCancel(string agentId, CancelRun cancelRun, out string reason)
    {
        if (_runtime is null)
        {
            reason = "Gateway runtime is not available.";
            return false;
        }

        return _runtime.TrySendCancel(agentId, cancelRun, out reason);
    }

    public int RequestReconciliationFromAll()
    {
        return _runtime?.RequestReconciliationFromAll() ?? 0;
    }

    public int RequestDrainingFromAll(bool draining)
    {
        return _runtime?.RequestDrainingFromAll(draining) ?? 0;
    }

    public void MarkKnownAgentsOfflineIfStale()
    {
        if (_runtime is not null)
        {
            _runtime.MarkKnownAgentsOfflineIfStale();
            return;
        }

        var registry = _registryService.Load();
        var changed = false;
        var now = DateTime.UtcNow;

        foreach (var agent in registry.Agents)
        {
            if (agent.State is AgentDefinitionState.Revoked or AgentDefinitionState.Disabled or AgentDefinitionState.Incompatible)
            {
                continue;
            }

            if (agent.LastSeenAtUtc is null)
            {
                continue;
            }

            if (now - agent.LastSeenAtUtc.Value <= OfflineAfter)
            {
                continue;
            }

            if (agent.State != AgentDefinitionState.Offline)
            {
                agent.State = AgentDefinitionState.Offline;
                agent.SessionId = null;
                agent.LastStateReasonCode = "heartbeat_timeout";
                agent.LastStateReasonMessage = "Heartbeat timeout exceeded offline threshold.";
                agent.LastSeenSummary = "Heartbeat timeout";
                changed = true;
            }
        }

        if (changed)
        {
            _registryService.Save(registry);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var controllerId = _registryService.GetControllerId();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AgentGatewayHost).Assembly.FullName
        });

        var settings = _settings;
        var bindHost = string.IsNullOrWhiteSpace(settings.BindHost) ? "localhost" : settings.BindHost.Trim();
        var port = settings.Port <= 0 ? DefaultPort : settings.Port;
        var restPort = port;
        var primaryScheme = settings.EnableTlsListener ? "https" : "http";
        var primaryEndpoint = $"{primaryScheme}://{bindHost}:{port}";
        string? fallbackEndpoint = null;

        builder.WebHost.UseKestrel(options =>
        {
            if (settings.EnableTlsListener)
            {
                if (string.IsNullOrWhiteSpace(settings.CertificatePath) || !File.Exists(settings.CertificatePath))
                {
                    throw new InvalidOperationException("TLS listener is enabled, but the configured PFX certificate path is missing or invalid.");
                }

                var certificate = new X509Certificate2(settings.CertificatePath, settings.CertificatePassword);
                ConfigureListener(options, bindHost, port, HttpProtocols.Http1AndHttp2, certificate);

                if (settings.IsDevLoopbackFallbackEnabled && port != DefaultPort)
                {
                    ConfigureListener(options, bindHost, DefaultPort, HttpProtocols.Http1AndHttp2);
                    fallbackEndpoint = $"http://{bindHost}:{DefaultPort}";
                }
            }
            else
            {
                // Cleartext gRPC requires a dedicated HTTP/2 endpoint.
                ConfigureListener(options, bindHost, port, HttpProtocols.Http2);

                restPort = port < 65535 ? port + 1 : DefaultPort;
                if (restPort == port || restPort <= 0 || restPort > 65535)
                {
                    restPort = DefaultPort == port ? 7342 : DefaultPort;
                }

                // Keep enrolment and diagnostics endpoints available to HTTP/1 clients.
                ConfigureListener(options, bindHost, restPort, HttpProtocols.Http1);

                fallbackEndpoint = $"http://{bindHost}:{restPort}";
            }
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_registryService);

        var enrolmentBaseEndpoint = settings.EnableTlsListener ? primaryEndpoint : $"http://{bindHost}:{restPort}";
        var runtime = new AgentGatewayRuntime(_registryService, controllerId, primaryEndpoint, enrolmentBaseEndpoint, fallbackEndpoint, _onAgentEvent);
        _runtime = runtime;
        builder.Services.AddSingleton(runtime);

        var app = builder.Build();

        app.MapGrpcService<AgentControlGrpcService>();

        app.MapPost("/api/agent/v1/enrol", async (HttpContext context, AgentGatewayRuntime runtime, CancellationToken ct) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var payload = await reader.ReadToEndAsync(ct);

            EnrolmentRequest request;
            try
            {
                request = JsonParser.Default.Parse<EnrolmentRequest>(payload);
            }
            catch (Exception ex)
            {
                var invalidResponse = new EnrolmentResponse
                {
                    Error = new TapError
                    {
                        Code = "invalid_request",
                        Message = $"Invalid enrolment payload: {ex.Message}",
                        Retryable = false
                    }
                };

                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonFormatter.Default.Format(invalidResponse), ct);
                return;
            }

            var response = runtime.CompleteEnrolment(request);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonFormatter.Default.Format(response), ct);
        });

        app.MapGet("/api/agent/v1/health", () => Results.Json(new
        {
            status = "ok",
            service = "tsky-agent-gateway",
            protocol = "tap",
            version = "1.0"
        }));

        app.MapGet("/.well-known/tsky-controller.json", (AgentGatewayRuntime runtime) => Results.Json(runtime.GetControllerDescriptor()));

        await app.StartAsync(cancellationToken);
        _app = app;
    }

    private static void ConfigureListener(KestrelServerOptions options, string bindHost, int port, HttpProtocols protocols, X509Certificate2? certificate = null)
    {
        var host = string.IsNullOrWhiteSpace(bindHost) ? "localhost" : bindHost.Trim();

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(port, listen =>
            {
                listen.Protocols = protocols;
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
            });

            return;
        }

        if (host == "*" || host == "+" || host == "0.0.0.0")
        {
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = protocols;
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
            });

            return;
        }

        if (IPAddress.TryParse(host, out var ipAddress))
        {
            options.Listen(ipAddress, port, listen =>
            {
                listen.Protocols = protocols;
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
            });

            return;
        }

        var resolved = Dns.GetHostAddresses(host)
            .Distinct()
            .ToArray();

        if (resolved.Length == 0)
        {
            throw new InvalidOperationException($"BindHost '{host}' did not resolve to any IP addresses.");
        }

        foreach (var address in resolved)
        {
            options.Listen(address, port, listen =>
            {
                listen.Protocols = protocols;
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
            });
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        var app = _app;
        _app = null;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Fast-stop path: caller requested bounded shutdown time.
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

internal sealed class AgentGatewayRuntime
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(35);

    private readonly AgentRegistryService _registryService;
    private readonly string _controllerId;
    private readonly string _primaryEndpoint;
    private readonly string _enrolmentBaseEndpoint;
    private readonly string? _fallbackEndpoint;
    private readonly Action<AgentGatewayRuntimeEvent>? _onAgentEvent;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, AgentSessionConnection> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public AgentGatewayRuntime(
        AgentRegistryService registryService,
        string controllerId,
        string primaryEndpoint,
        string enrolmentBaseEndpoint,
        string? fallbackEndpoint,
        Action<AgentGatewayRuntimeEvent>? onAgentEvent)
    {
        _registryService = registryService;
        _controllerId = controllerId;
        _primaryEndpoint = primaryEndpoint;
        _enrolmentBaseEndpoint = enrolmentBaseEndpoint;
        _fallbackEndpoint = fallbackEndpoint;
        _onAgentEvent = onAgentEvent;
    }

    public object GetControllerDescriptor()
    {
        return new
        {
            controllerId = _controllerId,
            displayName = Environment.MachineName + " Tsky",
            tapVersions = new[] { "1.0" },
            bindings = new[]
            {
                new
                {
                    profile = "tap-grpc",
                    version = "1",
                    endpoint = _primaryEndpoint
                }
            },
            fallbackEndpoint = _fallbackEndpoint,
            enrolmentEndpoint = _enrolmentBaseEndpoint + "/api/agent/v1/enrol"
        };
    }

    public EnrolmentResponse CompleteEnrolment(EnrolmentRequest request)
    {
        var enrolRequest = new AgentEnrolmentRequest
        {
            PendingAgentId = request.PendingAgentId,
            Token = request.EnrolmentToken,
            ControllerId = request.ControllerId,
            PublicKeyJwkJson = request.PublicKeyJwk is null ? null : JsonFormatter.Default.Format(request.PublicKeyJwk),
            Hostname = request.Device?.Hostname,
            OperatingSystem = request.Device?.OperatingSystem,
            Architecture = request.Device?.Architecture,
            ImplementationName = request.Device?.ImplementationName,
            ImplementationVersion = request.Device?.ImplementationVersion
        };

        AgentEnrolmentResult result;
        lock (_sync)
        {
            var registry = _registryService.Load();
            result = _registryService.CompleteEnrolment(registry, enrolRequest);
        }

        if (!result.Succeeded)
        {
            return new EnrolmentResponse
            {
                Error = new TapError
                {
                    Code = result.ErrorCode ?? "enrolment_failed",
                    Message = result.ErrorMessage ?? "Enrolment failed.",
                    Retryable = false
                }
            };
        }

        var response = new EnrolmentResponse
        {
            AgentId = result.AgentId ?? string.Empty,
            KeyId = result.KeyId ?? string.Empty,
            ControllerId = result.ControllerId ?? string.Empty,
            EnrolledAt = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        response.SupportedTapVersions.Add(new ProtocolVersion { Major = 1, Minor = 0 });

        foreach (var binding in result.SupportedBindings)
        {
            response.SupportedBindings.Add(new ProtocolBinding
            {
                Profile = binding.Profile,
                Version = binding.Version,
                Endpoint = binding.Endpoint,
                Optional = binding.Optional
            });
        }

        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = result.AgentId ?? string.Empty
        });
        return response;
    }

    public SessionChallenge? BuildChallenge(SessionHello hello, out TapError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(hello.AgentId) || string.IsNullOrWhiteSpace(hello.KeyId))
        {
            error = NewError("invalid_hello", "SessionHello must include agent_id and key_id.");
            return null;
        }

        var registry = _registryService.Load();
        var agent = registry.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentId, hello.AgentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            error = NewError("unknown_agent", "Agent identity is not registered.");
            return null;
        }

        if (agent.State == AgentDefinitionState.Revoked)
        {
            error = NewError("agent_revoked", "Agent identity is revoked.");
            return null;
        }

        if (agent.State == AgentDefinitionState.Disabled)
        {
            error = NewError("agent_disabled", "Agent is disabled.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(agent.KeyId) || !string.Equals(agent.KeyId, hello.KeyId, StringComparison.Ordinal))
        {
            error = NewError("unknown_key", "Key identity is not registered for this agent.");
            return null;
        }

        var selectedBinding = hello.SupportedBindings.FirstOrDefault(binding =>
            string.Equals(binding.Profile, "tap-grpc", StringComparison.OrdinalIgnoreCase));
        if (selectedBinding is null)
        {
            lock (_sync)
            {
                var latestRegistry = _registryService.Load();
                var live = latestRegistry.Agents.FirstOrDefault(candidate =>
                    string.Equals(candidate.AgentId, hello.AgentId, StringComparison.OrdinalIgnoreCase));
                if (live is not null && live.State is not AgentDefinitionState.Disabled and not AgentDefinitionState.Revoked)
                {
                    live.State = AgentDefinitionState.Incompatible;
                    live.LastStateReasonCode = "incompatible_binding";
                    live.LastStateReasonMessage = "No compatible TAP binding (tap-grpc).";
                    var proposedProfiles = hello.SupportedBindings.Count == 0
                        ? "none"
                        : string.Join(", ",
                            hello.SupportedBindings.Select(binding => $"{binding.Profile}:{binding.Version}"));
                    live.ProtocolMismatchDetail = $"Expected tap-grpc:1; agent proposed [{proposedProfiles}]";
                    live.LastSeenSummary = "No compatible TAP binding (tap-grpc)";
                    _registryService.Save(latestRegistry);
                }
            }

            error = NewError("incompatible_binding", "No compatible TAP binding was proposed.");
            return null;
        }

        var now = DateTime.UtcNow;
        return new SessionChallenge
        {
            ControllerId = _controllerId,
            SessionId = "ses_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant(),
            ControllerNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            SelectedTapVersion = new ProtocolVersion { Major = 1, Minor = 0 },
            SelectedBinding = new ProtocolBinding
            {
                Profile = "tap-grpc",
                Version = "1",
                Endpoint = selectedBinding.Endpoint
            },
            IssuedAt = Timestamp.FromDateTime(now),
            ExpiresAt = Timestamp.FromDateTime(now.AddSeconds(30)),
            SecurityProfile = "tap-es256-p1363-v1"
        };
    }

    public bool VerifyProof(SessionChallenge challenge, SessionHello hello, SessionProof proof, out TapError? error)
    {
        error = null;

        if (!string.Equals(challenge.SessionId, proof.SessionId, StringComparison.Ordinal)
            || !string.Equals(hello.AgentId, proof.AgentId, StringComparison.Ordinal)
            || !string.Equals(hello.KeyId, proof.KeyId, StringComparison.Ordinal))
        {
            error = NewError("auth_mismatch", "Session proof identifiers do not match the challenge.");
            return false;
        }

        var registry = _registryService.Load();
        var agent = registry.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentId, hello.AgentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null || string.IsNullOrWhiteSpace(agent.PublicKeyJwkJson))
        {
            error = NewError("missing_public_key", "No enrolled public key is available for this agent.");
            return false;
        }

        var transcript = BuildAuthTranscript(challenge, hello.AgentId, hello.KeyId, hello.AgentNonce.ToByteArray());

        try
        {
            using var ecdsa = BuildVerifier(agent.PublicKeyJwkJson);
            var verified = ecdsa.VerifyData(
                transcript,
                proof.Signature.ToByteArray(),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            if (!verified)
            {
                error = NewError("invalid_signature", "Session proof signature verification failed.");
                return false;
            }
        }
        catch (Exception ex)
        {
            error = NewError("invalid_public_key", ex.Message);
            return false;
        }

        return true;
    }

    public void MarkSessionAccepted(SessionHello hello, SessionChallenge challenge)
    {
        lock (_sync)
        {
            var registry = _registryService.Load();
            var agent = registry.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.AgentId, hello.AgentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                return;
            }

            agent.SessionId = challenge.SessionId;
            agent.LastSeenAtUtc = DateTime.UtcNow;
            agent.Hostname = NormalizeOrExisting(hello.ImplementationName, agent.Hostname);
            agent.ImplementationName = NormalizeOrExisting(hello.ImplementationName, agent.ImplementationName);
            agent.ImplementationVersion = NormalizeOrExisting(hello.ImplementationVersion, agent.ImplementationVersion);
            agent.LastSeenSummary = "Authenticated session";
            agent.LastStateReasonCode = null;
            agent.LastStateReasonMessage = null;
            agent.ProtocolMismatchDetail = null;
            if (agent.State is not AgentDefinitionState.Revoked and not AgentDefinitionState.Disabled and not AgentDefinitionState.Incompatible)
            {
                agent.State = AgentDefinitionState.Online;
            }

            _registryService.Save(registry);
        }

        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = hello.AgentId
        });
    }

    public void ApplySnapshot(string agentId, AgentSnapshot snapshot)
    {
        lock (_sync)
        {
            var registry = _registryService.Load();
            var agent = registry.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                return;
            }

            agent.LastSeenAtUtc = DateTime.UtcNow;
            agent.Hostname = NormalizeOrExisting(snapshot.Hostname, agent.Hostname);
            agent.OperatingSystem = NormalizeOrExisting(snapshot.OperatingSystem, agent.OperatingSystem);
            agent.Architecture = NormalizeOrExisting(snapshot.Architecture, agent.Architecture);
            agent.ImplementationName = NormalizeOrExisting(snapshot.ImplementationName, agent.ImplementationName);
            agent.ImplementationVersion = NormalizeOrExisting(snapshot.ImplementationVersion, agent.ImplementationVersion);
            agent.ActiveRuns = (int)(snapshot.Capacity?.ActiveRuns ?? 0);
            agent.MaximumConcurrentRuns = Math.Max(agent.MaximumConcurrentRuns, (int)(snapshot.Capacity?.MaximumRuns ?? 0));
            agent.CpuPercent = snapshot.Capacity?.CpuPercent ?? 0;
            agent.MemoryUsedBytes = (long)(snapshot.Capacity?.MemoryUsedBytes ?? 0);
            agent.MemoryTotalBytes = (long)(snapshot.Capacity?.MemoryTotalBytes ?? 0);
            agent.RuntimeDiskFreeBytes = (long)(snapshot.Capacity?.RuntimeDiskFreeBytes ?? 0);
            if (agent.State is AgentDefinitionState.Revoked or AgentDefinitionState.Disabled)
            {
                agent.LastSeenSummary = agent.State == AgentDefinitionState.Revoked ? "Revoked" : "Disabled";
            }
            else
            {
                agent.State = MapAvailability(snapshot.Availability, agent.ActiveRuns, snapshot.Capacity?.MaximumRuns ?? 0U);
                agent.LastSeenSummary = BuildLiveSummary(agent);
            }

            _registryService.Save(registry);
        }

        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId
        });
    }

    public void ApplyHeartbeat(string agentId, AgentHeartbeat heartbeat)
    {
        lock (_sync)
        {
            var registry = _registryService.Load();
            var agent = registry.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                return;
            }

            agent.LastSeenAtUtc = DateTime.UtcNow;
            agent.ActiveRuns = (int)(heartbeat.Capacity?.ActiveRuns ?? 0);
            agent.MaximumConcurrentRuns = Math.Max(agent.MaximumConcurrentRuns, (int)(heartbeat.Capacity?.MaximumRuns ?? 0));
            agent.CpuPercent = heartbeat.Capacity?.CpuPercent ?? 0;
            agent.MemoryUsedBytes = (long)(heartbeat.Capacity?.MemoryUsedBytes ?? 0);
            agent.MemoryTotalBytes = (long)(heartbeat.Capacity?.MemoryTotalBytes ?? 0);
            agent.RuntimeDiskFreeBytes = (long)(heartbeat.Capacity?.RuntimeDiskFreeBytes ?? 0);
            if (agent.State is AgentDefinitionState.Revoked or AgentDefinitionState.Disabled)
            {
                agent.IsDraining = false;
                agent.LastSeenSummary = agent.State == AgentDefinitionState.Revoked ? "Revoked" : "Disabled";
            }
            else
            {
                agent.IsDraining = heartbeat.Draining;
                agent.State = heartbeat.Draining
                    ? AgentDefinitionState.Draining
                    : (agent.ActiveRuns > 0 ? AgentDefinitionState.Busy : AgentDefinitionState.Online);
                agent.LastStateReasonCode = heartbeat.Draining ? "draining" : null;
                agent.LastStateReasonMessage = heartbeat.Draining
                    ? "Agent requested draining mode."
                    : null;
                agent.LastSeenSummary = BuildLiveSummary(agent);
            }

            _registryService.Save(registry);
        }

        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId
        });
    }

    public void ApplyRunEvent(string agentId, RunEvent runEvent)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            RunEvent = runEvent
        });
    }

    public void ApplyRunAccepted(string agentId, RunAccepted runAccepted)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            RunAccepted = runAccepted
        });
    }

    public void ApplyRunRejected(string agentId, RunRejected runRejected)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            RunRejected = runRejected
        });
    }

    public void ApplyRunOutput(string agentId, RunOutput runOutput)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            RunOutput = runOutput
        });
    }

    public void ApplyRunCompleted(string agentId, RunCompleted runCompleted)
    {
        lock (_sync)
        {
            var registry = _registryService.Load();
            var agent = registry.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                return;
            }

            if (runCompleted.TerminalState == RunAttemptState.Succeeded)
            {
                agent.SuccessfulRuns = Math.Max(0, agent.SuccessfulRuns) + 1;
            }
            else
            {
                agent.FailedRuns = Math.Max(0, agent.FailedRuns) + 1;
            }

            _registryService.Save(registry);
        }

        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            RunCompleted = runCompleted
        });
    }

    public void ApplyReconciliationResponse(string agentId, ReconciliationResponse reconciliationResponse)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            ReconciliationResponse = reconciliationResponse
        });
    }

    public void ApplyArtifactUploadCompleted(string agentId, ArtifactUploadCompleted artifactUploadCompleted)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            ArtifactUploadCompleted = artifactUploadCompleted
        });
    }

    public void ApplyArtifactUploadFailed(string agentId, ArtifactUploadFailed artifactUploadFailed)
    {
        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId,
            ArtifactUploadFailed = artifactUploadFailed
        });
    }

    public void MarkDisconnected(string agentId, string sessionId)
    {
        lock (_sync)
        {
            var registry = _registryService.Load();
            var agent = registry.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(sessionId) && !string.Equals(agent.SessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            agent.SessionId = null;
            if (agent.State is not AgentDefinitionState.Revoked and not AgentDefinitionState.Disabled and not AgentDefinitionState.Incompatible)
            {
                agent.State = AgentDefinitionState.Offline;
                agent.LastStateReasonCode = "disconnected";
                agent.LastStateReasonMessage = "Agent stream disconnected.";
            }

            var lastSeen = agent.LastSeenAtUtc;
            var age = lastSeen is null ? "unknown" : Math.Max(0, (int)(DateTime.UtcNow - lastSeen.Value).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            agent.LastSeenSummary = $"Disconnected (last heartbeat {age}s ago)";
            _registryService.Save(registry);
        }

        _onAgentEvent?.Invoke(new AgentGatewayRuntimeEvent
        {
            AgentId = agentId
        });
    }

    public void MarkKnownAgentsOfflineIfStale()
    {
        lock (_sync)
        {
            var registry = _registryService.Load();
            var changed = false;
            var now = DateTime.UtcNow;
            foreach (var agent in registry.Agents)
            {
                if (agent.State is AgentDefinitionState.Revoked or AgentDefinitionState.Disabled or AgentDefinitionState.Incompatible)
                {
                    continue;
                }

                if (agent.LastSeenAtUtc is null)
                {
                    continue;
                }

                if (now - agent.LastSeenAtUtc.Value <= OfflineAfter)
                {
                    continue;
                }

                if (agent.State != AgentDefinitionState.Offline)
                {
                    agent.State = AgentDefinitionState.Offline;
                    agent.SessionId = null;
                    agent.LastStateReasonCode = "heartbeat_timeout";
                    agent.LastStateReasonMessage = "Heartbeat timeout exceeded offline threshold.";
                    agent.LastSeenSummary = "Heartbeat timeout";
                    changed = true;
                }
            }

            if (changed)
            {
                _registryService.Save(registry);
            }
        }
    }

    public void AttachSession(string agentId, string sessionId, IServerStreamWriter<ControllerFrame> responseStream)
    {
        var connection = new AgentSessionConnection(agentId, sessionId, responseStream);
        _sessions.AddOrUpdate(agentId, connection, (_, existing) =>
        {
            existing.Dispose();
            return connection;
        });
    }

    public void DetachSession(string agentId, string sessionId)
    {
        if (_sessions.TryGetValue(agentId, out var existing)
            && string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
            && _sessions.TryRemove(agentId, out var removed))
        {
            removed.Dispose();
        }
    }

    public bool TryDispatchRunOffer(string agentId, RunOffer offer, out string reason)
    {
        var frame = new ControllerFrame
        {
            Header = CreateControlHeader(offer.RunId),
            RunOffer = offer
        };
        return TrySend(agentId, frame, out reason);
    }

    public bool TrySendCancel(string agentId, CancelRun cancelRun, out string reason)
    {
        var frame = new ControllerFrame
        {
            Header = CreateControlHeader(cancelRun.RunId),
            CancelRun = cancelRun
        };
        return TrySend(agentId, frame, out reason);
    }

    public int RequestReconciliationFromAll()
    {
        var count = 0;
        foreach (var pair in _sessions)
        {
            var request = new ReconciliationRequest
            {
                ReconciliationId = "rcn_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                IncludeRecentTerminalAttempts = true
            };
            var frame = new ControllerFrame
            {
                Header = CreateControlHeader(request.ReconciliationId),
                ReconciliationRequest = request
            };

            if (TrySend(pair.Key, frame, out _))
            {
                count++;
            }
        }

        return count;
    }

    public int RequestDrainingFromAll(bool draining)
    {
        var count = 0;
        foreach (var pair in _sessions)
        {
            var correlationId = "drn_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var request = new DrainRequest
            {
                Draining = draining,
                Reason = draining ? "Controller listener shutting down." : "Controller listener resumed."
            };

            var frame = new ControllerFrame
            {
                Header = CreateControlHeader(correlationId),
                DrainRequest = request
            };

            if (TrySend(pair.Key, frame, out _))
            {
                count++;
            }
        }

        return count;
    }

    private bool TrySend(string agentId, ControllerFrame frame, out string reason)
    {
        if (!_sessions.TryGetValue(agentId, out var connection))
        {
            reason = "Agent does not have an active session.";
            return false;
        }

        const int sendTimeoutMilliseconds = 15000;

        if (TrySendOnConnection(connection, frame, sendTimeoutMilliseconds, out reason))
        {
            return true;
        }

        // If the connection changed while sending failed, retry once on the latest session.
        if (_sessions.TryGetValue(agentId, out var latestConnection)
            && !ReferenceEquals(latestConnection, connection)
            && TrySendOnConnection(latestConnection, frame, sendTimeoutMilliseconds, out reason))
        {
            return true;
        }

        var isTimeout = reason.StartsWith("Timed out sending frame to agent", StringComparison.OrdinalIgnoreCase);
        if (!isTimeout
            && _sessions.TryGetValue(agentId, out var current)
            && ReferenceEquals(current, connection)
            && _sessions.TryRemove(agentId, out var removed))
        {
            removed.Dispose();
        }

        return false;
    }

    private static bool TrySendOnConnection(AgentSessionConnection connection, ControllerFrame frame, int sendTimeoutMilliseconds, out string reason)
    {
        try
        {
            connection.SendAsync(frame, sendTimeoutMilliseconds).GetAwaiter().GetResult();
            reason = string.Empty;
            return true;
        }
        catch (TimeoutException ex)
        {
            reason = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static FrameHeader CreateControlHeader(string correlationId)
    {
        return new FrameHeader
        {
            Protocol = "tap",
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            MessageId = "ctl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant(),
            SentAt = Timestamp.FromDateTime(DateTime.UtcNow),
            CorrelationId = correlationId
        };
    }

    private sealed class AgentSessionConnection : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private bool _disposed;
        private bool _faulted;

        public AgentSessionConnection(string agentId, string sessionId, IServerStreamWriter<ControllerFrame> responseStream)
        {
            AgentId = agentId;
            SessionId = sessionId;
            ResponseStream = responseStream;
        }

        public string AgentId { get; }

        public string SessionId { get; }

        public IServerStreamWriter<ControllerFrame> ResponseStream { get; }

        public async Task SendAsync(ControllerFrame frame, int timeoutMilliseconds)
        {
            if (_disposed || _faulted)
            {
                throw new ObjectDisposedException(nameof(AgentSessionConnection));
            }

            var gateEntered = await _writeGate.WaitAsync(timeoutMilliseconds).ConfigureAwait(false);
            if (!gateEntered)
            {
                _faulted = true;
                throw new TimeoutException($"Timed out sending frame to agent after {timeoutMilliseconds}ms.");
            }

            try
            {
                if (_disposed || _faulted)
                {
                    throw new ObjectDisposedException(nameof(AgentSessionConnection));
                }

                var writeTask = ResponseStream.WriteAsync(frame);
                var completed = await Task.WhenAny(writeTask, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) == writeTask;
                if (!completed)
                {
                    _faulted = true;
                    _ = writeTask.ContinueWith(t =>
                    {
                        _ = t.Exception;
                    }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    throw new TimeoutException($"Timed out sending frame to agent after {timeoutMilliseconds}ms.");
                }

                await writeTask.ConfigureAwait(false);
            }
            catch
            {
                _faulted = true;
                throw;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    private static AgentDefinitionState MapAvailability(AgentAvailability availability, int activeRuns, uint maximumRuns)
    {
        return availability switch
        {
            AgentAvailability.Busy => AgentDefinitionState.Busy,
            AgentAvailability.Draining => AgentDefinitionState.Draining,
            AgentAvailability.Degraded => AgentDefinitionState.Degraded,
            _ => activeRuns > 0 || maximumRuns == 0 ? AgentDefinitionState.Busy : AgentDefinitionState.Online
        };
    }

    private static string BuildLiveSummary(AgentDefinition agent)
    {
        var memoryText = agent.MemoryTotalBytes > 0
            ? $"{Math.Round((double)agent.MemoryUsedBytes / Math.Max(1, agent.MemoryTotalBytes) * 100, 0):0}% mem"
            : "mem n/a";
        return $"{agent.ActiveRuns}/{agent.MaximumConcurrentRuns} runs, {agent.CpuPercent:0.#}% cpu, {memoryText}";
    }

    private static string? NormalizeOrExisting(string? incoming, string? existing)
    {
        return string.IsNullOrWhiteSpace(incoming) ? existing : incoming.Trim();
    }

    private static TapError NewError(string code, string message)
    {
        return new TapError
        {
            Code = code,
            Message = message,
            Retryable = false
        };
    }

    private static ECDsa BuildVerifier(string jwkJson)
    {
        using var document = JsonDocument.Parse(jwkJson);
        var root = document.RootElement;
        var xText = root.GetProperty("x").GetString();
        var yText = root.GetProperty("y").GetString();

        if (string.IsNullOrWhiteSpace(xText) || string.IsNullOrWhiteSpace(yText))
        {
            throw new InvalidOperationException("Public key JWK is missing x or y coordinates.");
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q =
            {
                X = Base64UrlDecode(xText),
                Y = Base64UrlDecode(yText)
            }
        };

        return ECDsa.Create(parameters);
    }

    private static byte[] BuildAuthTranscript(SessionChallenge challenge, string agentId, string keyId, byte[] agentNonce)
    {
        var issuedAt = challenge.IssuedAt.ToDateTime().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var transcript = new StringBuilder();
        transcript.Append("TSKY-TAP-AUTH-V1\n");
        transcript.Append("controller_id=").Append(challenge.ControllerId).Append('\n');
        transcript.Append("agent_id=").Append(agentId).Append('\n');
        transcript.Append("key_id=").Append(keyId).Append('\n');
        transcript.Append("session_id=").Append(challenge.SessionId).Append('\n');
        transcript.Append("agent_nonce=").Append(Base64UrlEncode(agentNonce)).Append('\n');
        transcript.Append("controller_nonce=").Append(Base64UrlEncode(challenge.ControllerNonce.ToByteArray())).Append('\n');
        transcript.Append("tap_version=").Append(challenge.SelectedTapVersion.Major).Append('.').Append(challenge.SelectedTapVersion.Minor).Append('\n');
        transcript.Append("binding=").Append(challenge.SelectedBinding.Profile).Append('\n');
        transcript.Append("issued_at=").Append(issuedAt).Append('\n');
        return Encoding.UTF8.GetBytes(transcript.ToString());
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
    }
}

internal sealed class AgentControlGrpcService : AgentControl.AgentControlBase
{
    private readonly AgentGatewayRuntime _runtime;

    public AgentControlGrpcService(AgentGatewayRuntime runtime)
    {
        _runtime = runtime;
    }

    public override async Task Connect(
        IAsyncStreamReader<AgentFrame> requestStream,
        IServerStreamWriter<ControllerFrame> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken))
        {
            return;
        }

        var helloFrame = requestStream.Current;
        var hello = helloFrame.SessionHello;
        if (hello is null)
        {
            await responseStream.WriteAsync(new ControllerFrame
            {
                Header = NewHeader("hello-required"),
                SessionRejected = new SessionRejected
                {
                    Error = new TapError
                    {
                        Code = "invalid_first_message",
                        Message = "First frame must be SessionHello.",
                        Retryable = false
                    }
                }
            });
            return;
        }

        var challenge = _runtime.BuildChallenge(hello, out var challengeError);
        if (challenge is null)
        {
            await responseStream.WriteAsync(new ControllerFrame
            {
                Header = NewHeader(hello.AgentId),
                SessionRejected = new SessionRejected { Error = challengeError ?? new TapError { Code = "auth_failed", Message = "Authentication failed." } }
            });
            return;
        }

        await responseStream.WriteAsync(new ControllerFrame
        {
            Header = NewHeader(hello.AgentId),
            SessionChallenge = challenge
        });

        if (!await requestStream.MoveNext(context.CancellationToken))
        {
            _runtime.MarkDisconnected(hello.AgentId, challenge.SessionId);
            return;
        }

        var proof = requestStream.Current.SessionProof;
        if (proof is null)
        {
            await responseStream.WriteAsync(new ControllerFrame
            {
                Header = NewHeader(challenge.SessionId),
                SessionRejected = new SessionRejected
                {
                    Error = new TapError
                    {
                        Code = "proof_required",
                        Message = "Second frame must be SessionProof.",
                        Retryable = false
                    }
                }
            });
            return;
        }

        if (!_runtime.VerifyProof(challenge, hello, proof, out var proofError))
        {
            await responseStream.WriteAsync(new ControllerFrame
            {
                Header = NewHeader(challenge.SessionId),
                SessionRejected = new SessionRejected { Error = proofError ?? new TapError { Code = "auth_failed", Message = "Authentication failed." } }
            });
            return;
        }

        _runtime.MarkSessionAccepted(hello, challenge);

        await responseStream.WriteAsync(new ControllerFrame
        {
            Header = NewHeader(challenge.SessionId),
            SessionAccepted = new SessionAccepted
            {
                SessionId = challenge.SessionId,
                SelectedTapVersion = new ProtocolVersion { Major = 1, Minor = 0 },
                SelectedBinding = new ProtocolBinding { Profile = "tap-grpc", Version = "1", Endpoint = challenge.SelectedBinding.Endpoint },
                Policy = new SessionPolicy
                {
                    MaximumControlMessageBytes = 1024 * 1024,
                    MaximumOutputChunkBytes = 1024 * 1024,
                    HeartbeatIntervalSeconds = 10,
                    SuspectAfterSeconds = 20,
                    OfflineAfterSeconds = 35,
                    MaximumConcurrentRuns = 16
                }
            }
        });

        _runtime.AttachSession(hello.AgentId, challenge.SessionId, responseStream);

        try
        {
            await foreach (var frame in requestStream.ReadAllAsync(context.CancellationToken))
            {
                if (frame.AgentSnapshot is not null)
                {
                    _runtime.ApplySnapshot(hello.AgentId, frame.AgentSnapshot);
                    continue;
                }

                if (frame.AgentHeartbeat is not null)
                {
                    _runtime.ApplyHeartbeat(hello.AgentId, frame.AgentHeartbeat);
                    continue;
                }

                if (frame.Pong is not null)
                {
                    continue;
                }

                if (frame.RunCompleted is not null)
                {
                    _runtime.ApplyRunCompleted(hello.AgentId, frame.RunCompleted);
                    continue;
                }

                if (frame.RunAccepted is not null)
                {
                    _runtime.ApplyRunAccepted(hello.AgentId, frame.RunAccepted);
                    continue;
                }

                if (frame.RunRejected is not null)
                {
                    _runtime.ApplyRunRejected(hello.AgentId, frame.RunRejected);
                    continue;
                }

                if (frame.RunEvent is not null)
                {
                    _runtime.ApplyRunEvent(hello.AgentId, frame.RunEvent);
                    continue;
                }

                if (frame.RunOutput is not null)
                {
                    _runtime.ApplyRunOutput(hello.AgentId, frame.RunOutput);
                    continue;
                }

                if (frame.ReconciliationResponse is not null)
                {
                    _runtime.ApplyReconciliationResponse(hello.AgentId, frame.ReconciliationResponse);
                    continue;
                }

                if (frame.ArtifactUploadCompleted is not null)
                {
                    _runtime.ApplyArtifactUploadCompleted(hello.AgentId, frame.ArtifactUploadCompleted);
                    continue;
                }

                if (frame.ArtifactUploadFailed is not null)
                {
                    _runtime.ApplyArtifactUploadFailed(hello.AgentId, frame.ArtifactUploadFailed);
                    continue;
                }

                if (frame.CapabilityReport is not null)
                {
                    continue;
                }
            }
        }
        finally
        {
            _runtime.DetachSession(hello.AgentId, challenge.SessionId);
            _runtime.MarkDisconnected(hello.AgentId, challenge.SessionId);
        }
    }

    private static FrameHeader NewHeader(string correlationId)
    {
        return new FrameHeader
        {
            Protocol = "tap",
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            MessageId = "msg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            SentAt = Timestamp.FromDateTime(DateTime.UtcNow),
            CorrelationId = correlationId
        };
    }
}
