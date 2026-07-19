package protocol

import (
	"encoding/base64"
	"strings"
	"testing"
	"time"

	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/types/known/timestamppb"
)

func TestAuthTranscriptShape(t *testing.T) {
	challenge := &agentv1.SessionChallenge{
		ControllerId:       "ctl",
		SessionId:          "sess",
		ControllerNonce:    []byte("controller nonce 123"),
		SelectedTapVersion: &agentv1.ProtocolVersion{Major: 1, Minor: 0},
		SelectedBinding:    &agentv1.ProtocolBinding{Profile: "tap-grpc", Version: "1"},
		IssuedAt:           timestamppb.New(time.Date(2026, 7, 17, 12, 0, 0, 0, time.UTC)),
	}
	got, err := AuthTranscript(challenge, "agt", "key", []byte("agent nonce 123456"))
	if err != nil {
		t.Fatal(err)
	}
	text := string(got)
	if !strings.HasPrefix(text, "TSKY-TAP-AUTH-V1\ncontroller_id=ctl\n") {
		t.Fatalf("unexpected transcript: %q", text)
	}
	if !strings.Contains(text, "agent_nonce="+base64.RawURLEncoding.EncodeToString([]byte("agent nonce 123456"))+"\n") {
		t.Fatalf("agent nonce was not base64url encoded: %q", text)
	}
	if !strings.HasSuffix(text, "issued_at=2026-07-17T12:00:00Z\n") {
		t.Fatalf("unexpected issued_at: %q", text)
	}
}
