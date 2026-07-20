using System;
using System.Collections.Generic;

namespace tsky.Tests;

public sealed class AgentDispatchLifecycleMarkerTests
{
    [Fact]
    public void AcceptedScenario_ProducesStartThenSuccessfulEnd()
    {
        var context = new AgentDispatchLifecycleContext("Build API", "agent-1", "run-001", "attempt-001");

        var markers = new List<string>
        {
            AgentDispatchLifecycleMarkers.Start(context),
            AgentDispatchLifecycleMarkers.End(context, "Succeeded", "remote completion reported")
        };

        Assert.Equal(
            "START task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 corr=Build_API-agent-1-attempt-001",
            markers[0]);
        Assert.Equal(
            "END task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 corr=Build_API-agent-1-attempt-001 state=Succeeded reason=remote_completion_reported",
            markers[1]);
    }

    [Fact]
    public void RejectedScenario_ProducesErrorMarker()
    {
        var marker = AgentDispatchLifecycleMarkers.Error("Build API", "agent-1", "agent not eligible", "dispatch-failed");

        Assert.Equal(
            "ERROR task=Build_API agent=agent-1 corr=Build_API-agent-1-dispatch-failed reason=agent_not_eligible",
            marker);
    }

    [Fact]
    public void DisconnectScenario_ProducesRequestedAwaitingThenUnknownEnd()
    {
        var context = new AgentDispatchLifecycleContext("Build API", "agent-1", "run-001", "attempt-001");

        var markers = new List<string>
        {
            AgentDispatchLifecycleMarkers.Start(context),
            AgentDispatchLifecycleMarkers.ReconcileRequested(context, probes: 1),
            AgentDispatchLifecycleMarkers.ReconcileAwaitingReconnect(context),
            AgentDispatchLifecycleMarkers.End(context, "Unknown", "completion_not_inferred_after_disconnect")
        };

        Assert.Equal(
            "RECONCILE task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 status=requested probes=1 reason=disconnect_detected",
            markers[1]);
        Assert.Equal(
            "RECONCILE task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 status=awaiting_reconnect note=completion_not_inferred",
            markers[2]);
        Assert.Equal(
            "END task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 corr=Build_API-agent-1-attempt-001 state=Unknown reason=completion_not_inferred_after_disconnect",
            markers[3]);
    }

    [Fact]
    public void LateCompletionAfterDisconnect_ProducesReconnectAndRecoveredMarkers()
    {
        var context = new AgentDispatchLifecycleContext("Build API", "agent-1", "run-001", "attempt-001");

        var markers = new List<string>
        {
            AgentDispatchLifecycleMarkers.Start(context),
            AgentDispatchLifecycleMarkers.ReconcileRequested(context, probes: 1),
            AgentDispatchLifecycleMarkers.ReconcileAwaitingReconnect(context),
            AgentDispatchLifecycleMarkers.End(context, "Unknown", "completion_not_inferred_after_disconnect"),
            AgentDispatchLifecycleMarkers.ReconcileRequestedOnReconnect(context, probes: 1),
            AgentDispatchLifecycleMarkers.ReconcileRecovered(context, "Succeeded", "recon-007")
        };

        Assert.Equal(
            "RECONCILE task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 status=requested_on_reconnect probes=1",
            markers[4]);
        Assert.Equal(
            "RECONCILE task=Build_API agent=agent-1 run=run-001 attempt=attempt-001 status=recovered state=Succeeded reconciliationId=recon-007",
            markers[5]);
    }
}
