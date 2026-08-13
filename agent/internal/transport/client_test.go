package transport

import (
	"testing"

	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
)

func TestDisconnectDropsQueuedFramesAndDisconnectedSendDoesNotRequeue(t *testing.T) {
	client := &Client{outbound: make(chan *agentv1.AgentFrame, 4)}
	client.connected.Store(true)
	if err := client.Send(&agentv1.AgentFrame{}); err != nil {
		t.Fatal(err)
	}
	if len(client.outbound) != 1 {
		t.Fatalf("queued frames = %d, want 1", len(client.outbound))
	}

	client.disconnect()

	if client.connected.Load() || len(client.outbound) != 0 {
		t.Fatalf("disconnect left connected=%v queued=%d", client.connected.Load(), len(client.outbound))
	}
	if err := client.Send(&agentv1.AgentFrame{}); err != nil {
		t.Fatal(err)
	}
	if len(client.outbound) != 0 {
		t.Fatalf("disconnected send queued %d frames", len(client.outbound))
	}
}
