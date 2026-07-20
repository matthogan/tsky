using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Tsky.Agent.V1;

namespace tsky.Tests;

public sealed class RemoteControlPlaneServiceDigestTests
{
    [Fact]
    public void ComputePlanDigest_MatchesKnownFixture()
    {
        var plan = new RunPlan
        {
            SchemaVersion = 1,
            Source = new RunSource
            {
                Kind = "task",
                SourceId = "task:Diagnostics/SSL Probe yoctobusiness.com",
                Revision = 1
            },
            Policy = new RunPolicy
            {
                DisconnectPolicy = DisconnectPolicy.ContinueAndReport,
                MaximumDuration = Duration.FromTimeSpan(TimeSpan.FromSeconds(300)),
                AllowShellExecutors = true,
                MaximumOutputBytesPerStream = 8192
            }
        };

        var parameters = new Struct();
        parameters.Fields["executable"] = Value.ForString("cmd.exe");
        parameters.Fields["arguments"] = new Value
        {
            ListValue = new ListValue
            {
                Values =
                {
                    Value.ForString("/c"),
                    Value.ForString("echo hi")
                }
            }
        };
        parameters.Fields["workingDirectory"] = Value.ForString("C:/code");

        var environment = new Struct();
        environment.Fields["JAVA_HOME"] = Value.ForString("C:/Java/jdk-21");
        environment.Fields["PATH"] = Value.ForString("C:/Java/jdk-21/bin");
        parameters.Fields["environment"] = Value.ForStruct(environment);

        var step = new RunStep
        {
            StepId = "step_fixture",
            SourceTaskId = "task:Diagnostics/SSL Probe yoctobusiness.com",
            DisplayName = "SSL Probe yoctobusiness.com",
            Timeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(120)),
            ContinueOnError = false,
            Command = new ExecutorCommand
            {
                ExecutorId = "process.v1",
                ExecutorVersion = "1",
                Parameters = parameters
            }
        };

        step.Inputs["inputUrl"] = new PortValue { StringValue = "https://yoctobusiness.com" };
        step.DeclaredOutputs.Add(new DeclaredOutput
        {
            OutputId = "report",
            Name = "report",
            Kind = "port",
            MediaType = "text/plain",
            Required = false
        });

        plan.Steps.Add(step);

        var digest = RemoteControlPlaneService.ComputePlanDigest(plan);
        var digestHex = Convert.ToHexString(digest).ToLowerInvariant();

        const string expectedDigestHex = "7c6c5f4dc2748fb7a55d7d62ad44f149df03a5ffed2ea1f6a8c50dd298c10ba2";
        Assert.Equal(expectedDigestHex, digestHex);
    }
}
