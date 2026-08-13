package protocol

import (
	"encoding/base64"
	"strings"
	"testing"
	"time"

	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"
)

func TestAuthTranscriptShape(t *testing.T) {
	challenge := &agentv1.SessionChallenge{
		ControllerId:       "ctl",
		SessionId:          "sess",
		ControllerNonce:    []byte("controller nonce 123"),
		SelectedTapVersion: &agentv1.ProtocolVersion{Major: 1, Minor: 1},
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
	if !strings.Contains(text, "binding=tap-grpc\nbinding_version=1\n") {
		t.Fatalf("TAP 1.1 transcript did not bind the binding version: %q", text)
	}
	if !strings.HasSuffix(text, "issued_at=2026-07-17T12:00:00Z\n") {
		t.Fatalf("unexpected issued_at: %q", text)
	}
}

func TestTap10AuthTranscriptPreservesLegacyShape(t *testing.T) {
	challenge := &agentv1.SessionChallenge{
		ControllerId:       "ctl",
		SessionId:          "sess",
		ControllerNonce:    []byte("controller nonce 123"),
		SelectedTapVersion: LegacyVersion(),
		SelectedBinding:    &agentv1.ProtocolBinding{Profile: BindingProfile, Version: BindingVersion},
		IssuedAt:           timestamppb.New(time.Date(2026, 7, 17, 12, 0, 0, 0, time.UTC)),
	}
	got, err := AuthTranscript(challenge, "agt", "key", []byte("agent nonce 123456"))
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(got), "binding_version=") {
		t.Fatalf("TAP 1.0 transcript changed shape: %q", got)
	}
}

func TestProtocolVersionAndBindingSupport(t *testing.T) {
	header := Header("test")
	if header.GetVersion().GetMajor() != 1 || header.GetVersion().GetMinor() != 1 {
		t.Fatalf("current header version = %d.%d, want 1.1", header.GetVersion().GetMajor(), header.GetVersion().GetMinor())
	}

	versions := SupportedVersions()
	if len(versions) != 2 || versions[0].GetMinor() != 1 || versions[1].GetMinor() != 0 {
		t.Fatalf("supported versions = %v, want [1.1, 1.0]", versions)
	}
	if !IsSupportedVersion(&agentv1.ProtocolVersion{Major: 1, Minor: 0}) || IsSupportedVersion(&agentv1.ProtocolVersion{Major: 2, Minor: 0}) {
		t.Fatal("semantic protocol compatibility check returned the wrong result")
	}
	selected := SelectHighestCommon([]*agentv1.ProtocolVersion{{Major: 1, Minor: 0}, {Major: 1, Minor: 1}})
	if selected == nil || selected.GetMinor() != 1 {
		t.Fatalf("selected version = %v, want 1.1", selected)
	}
	if !IsSupportedBinding(&agentv1.ProtocolBinding{Profile: BindingProfile, Version: BindingVersion}) || IsSupportedBinding(&agentv1.ProtocolBinding{Profile: BindingProfile, Version: "2"}) {
		t.Fatal("binding compatibility check returned the wrong result")
	}
	if SupportsScheduleV1(&agentv1.ProtocolVersion{Major: 1, Minor: 0}) || !SupportsScheduleV1(&agentv1.ProtocolVersion{Major: 1, Minor: 1}) {
		t.Fatal("schedule.v1 TAP version gate returned the wrong result")
	}
	if IsFrameHeaderValid(&agentv1.FrameHeader{Protocol: "not-tap", Version: CurrentVersion()}, CurrentVersion()) {
		t.Fatal("frame header accepted the wrong protocol identifier")
	}
}

func TestScheduleFrameRoundTrip(t *testing.T) {
	want := &agentv1.ControllerFrame{Body: &agentv1.ControllerFrame_ScheduleUpsert{ScheduleUpsert: &agentv1.ScheduleUpsert{
		Identity:       &agentv1.ScheduleIdentity{ScheduleId: "sched-1", Revision: 7, SourceDesktopInstanceId: "desktop-1"},
		Spec:           &agentv1.ScheduleSpec{ScheduleId: "sched-1", Enabled: true, TriggerType: agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_ONE_SHOT, TriggerAtUtc: timestamppb.Now(), PayloadRunPlanSnapshot: &agentv1.RunPlan{SchemaVersion: 1}},
		IdempotencyKey: "operation-7",
	}}}
	data, err := proto.Marshal(want)
	if err != nil {
		t.Fatal(err)
	}
	var got agentv1.ControllerFrame
	if err := proto.Unmarshal(data, &got); err != nil {
		t.Fatal(err)
	}
	if got.GetScheduleUpsert().GetIdentity().GetRevision() != 7 || got.GetScheduleUpsert().GetSpec().GetScheduleId() != "sched-1" {
		t.Fatalf("unexpected round trip: %v", &got)
	}
}
