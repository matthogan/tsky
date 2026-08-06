using System.Reflection;
using Microsoft.AspNetCore.Builder;

namespace tsky.Tests;

public sealed class RemoteControlPlaneServiceTests
{
    [Fact]
    public void TryDispatchTask_WhenGatewayNotRunning_ReturnsNotRunningMessage()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var registryService = new AgentRegistryService(Path.Combine(tempRoot, "registry.json"));
            var gatewayHost = new AgentGatewayHost(registryService, new AgentGatewaySettings());
            var service = new RemoteControlPlaneService(gatewayHost, registryService);

            var receipt = service.TryDispatchTask(
                CreateTask(runner: "cmd", commandLine: "echo hi"),
                new ExecutionTargetSelection { Mode = ExecutionTargetMode.Automatic },
                resolvedInputs: new Dictionary<string, string>(),
                resolvedOutputs: new Dictionary<string, string>());

            Assert.False(receipt.Dispatched);
            Assert.Equal("Gateway is not running.", receipt.Message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void TryDispatchTask_SpecificAgentUnavailable_ReturnsUnavailableMessage()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(
                tempRoot,
                new AgentRegistry
                {
                    Agents =
                    [
                        new AgentDefinition
                        {
                            AgentId = "agt-available",
                            DisplayName = "Available",
                            State = AgentDefinitionState.Online,
                            ControllerId = "ctl-1",
                            IsDraining = false
                        }
                    ]
                });

            var receipt = service.TryDispatchTask(
                CreateTask(runner: "cmd", commandLine: "echo hi"),
                new ExecutionTargetSelection
                {
                    Mode = ExecutionTargetMode.SpecificAgent,
                    AgentId = "agt-missing"
                },
                resolvedInputs: new Dictionary<string, string>(),
                resolvedOutputs: new Dictionary<string, string>());

            Assert.False(receipt.Dispatched);
            Assert.Equal("Selected agent is unavailable or offline.", receipt.Message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void TryDispatchTask_AgentPoolWithoutPoolId_ReturnsPoolUnavailableMessage()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(
                tempRoot,
                new AgentRegistry
                {
                    Agents =
                    [
                        new AgentDefinition
                        {
                            AgentId = "agt-available",
                            DisplayName = "Available",
                            State = AgentDefinitionState.Online,
                            ControllerId = "ctl-1",
                            IsDraining = false
                        }
                    ]
                });

            var receipt = service.TryDispatchTask(
                CreateTask(runner: "cmd", commandLine: "echo hi"),
                new ExecutionTargetSelection
                {
                    Mode = ExecutionTargetMode.AgentPool,
                    PoolId = "   "
                },
                resolvedInputs: new Dictionary<string, string>(),
                resolvedOutputs: new Dictionary<string, string>());

            Assert.False(receipt.Dispatched);
            Assert.Equal("No eligible agent found in the selected pool.", receipt.Message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Theory]
    [InlineData("cmd", "echo hi")]
    [InlineData("powershell", "Write-Output 'ok'")]
    [InlineData("python", "-c \"print('ok')\"")]
    public void TryDispatchTask_ValidRunnerBuildsPlan_ThenFailsWhenRuntimeUnavailable(string runner, string commandLine)
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(
                tempRoot,
                new AgentRegistry
                {
                    Agents =
                    [
                        new AgentDefinition
                        {
                            AgentId = "agt-available",
                            DisplayName = "Available",
                            State = AgentDefinitionState.Online,
                            ControllerId = "ctl-1",
                            IsDraining = false,
                            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["pool"] = "default"
                            }
                        }
                    ]
                });

            var receipt = service.TryDispatchTask(
                CreateTask(runner: runner, commandLine: commandLine),
                new ExecutionTargetSelection
                {
                    Mode = ExecutionTargetMode.Automatic
                },
                resolvedInputs: new Dictionary<string, string>
                {
                    ["inputA"] = "value-a"
                },
                resolvedOutputs: new Dictionary<string, string>
                {
                    ["outA"] = ""
                });

            Assert.False(receipt.Dispatched);
            Assert.Equal("Gateway runtime is not available.", receipt.Message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void TryDispatchTask_UnsupportedRunner_ReturnsValidationError()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(
                tempRoot,
                new AgentRegistry
                {
                    Agents =
                    [
                        new AgentDefinition
                        {
                            AgentId = "agt-available",
                            DisplayName = "Available",
                            State = AgentDefinitionState.Online,
                            ControllerId = "ctl-1",
                            IsDraining = false
                        }
                    ]
                });

            var receipt = service.TryDispatchTask(
                CreateTask(runner: "bash", commandLine: "echo hi"),
                new ExecutionTargetSelection { Mode = ExecutionTargetMode.Automatic },
                resolvedInputs: new Dictionary<string, string>(),
                resolvedOutputs: new Dictionary<string, string>());

            Assert.False(receipt.Dispatched);
            Assert.Equal("Runner must be one of: exe, cmd, powershell, python.", receipt.Message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void TryDispatchTask_EmptyCommandLine_ReturnsValidationError()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(
                tempRoot,
                new AgentRegistry
                {
                    Agents =
                    [
                        new AgentDefinition
                        {
                            AgentId = "agt-available",
                            DisplayName = "Available",
                            State = AgentDefinitionState.Online,
                            ControllerId = "ctl-1",
                            IsDraining = false
                        }
                    ]
                });

            var receipt = service.TryDispatchTask(
                CreateTask(runner: "cmd", commandLine: "   "),
                new ExecutionTargetSelection { Mode = ExecutionTargetMode.Automatic },
                resolvedInputs: new Dictionary<string, string>(),
                resolvedOutputs: new Dictionary<string, string>());

            Assert.False(receipt.Dispatched);
            Assert.Equal("Remote dispatch requires a non-empty commandLine.", receipt.Message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void TryCancel_WhenRuntimeUnavailable_ReturnsFalseWithReason()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(tempRoot, new AgentRegistry());

            var cancelled = service.TryCancel("agt-1", "run-1", "attempt-1", out var message);

            Assert.False(cancelled);
            Assert.Equal("Gateway runtime is not available.", message);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void RequestReconciliationAll_WhenRuntimeUnavailable_ReturnsZero()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var service = CreateServiceWithRunningGatewayNoRuntime(tempRoot, new AgentRegistry());

            var requested = service.RequestReconciliationAll();

            Assert.Equal(0, requested);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static LauncherTask CreateTask(string runner, string commandLine)
    {
        return new LauncherTask
        {
            Name = "task-1",
            Group = "group-1",
            Runner = runner,
            CommandLine = commandLine,
            TimeoutSeconds = 120,
            MaxCapturedOutputChars = 4096,
            WaitForExit = true,
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENV_A"] = "value"
            }
        };
    }

    private static RemoteControlPlaneService CreateServiceWithRunningGatewayNoRuntime(string tempRoot, AgentRegistry registry)
    {
        var registryPath = Path.Combine(tempRoot, "registry.json");
        var registryService = new AgentRegistryService(registryPath);
        registryService.Save(registry);

        var gatewayHost = new AgentGatewayHost(registryService, new AgentGatewaySettings());
        ForceGatewayRunningWithoutRuntime(gatewayHost);

        return new RemoteControlPlaneService(gatewayHost, registryService);
    }

    private static void ForceGatewayRunningWithoutRuntime(AgentGatewayHost gatewayHost)
    {
        // We only need IsRunning=true to exercise dispatch planning paths; runtime stays null to avoid network runtime setup in unit tests.
        var appField = typeof(AgentGatewayHost).GetField("_app", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AgentGatewayHost._app field not found.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(RemoteControlPlaneServiceTests).Assembly.FullName
        });
        using var inertApp = builder.Build();

        appField.SetValue(gatewayHost, inertApp);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsky-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for test temp assets.
        }
    }
}
