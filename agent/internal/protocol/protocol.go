package protocol

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"time"

	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"
)

const (
	TAPMajor        uint32 = 1
	TAPCurrentMinor uint32 = 1
	TAPLegacyMinor  uint32 = 0
	BindingProfile         = "tap-grpc"
	BindingVersion         = "1"
)

func CurrentVersion() *agentv1.ProtocolVersion {
	return &agentv1.ProtocolVersion{Major: TAPMajor, Minor: TAPCurrentMinor}
}

func LegacyVersion() *agentv1.ProtocolVersion {
	return &agentv1.ProtocolVersion{Major: TAPMajor, Minor: TAPLegacyMinor}
}

func SupportedVersions() []*agentv1.ProtocolVersion {
	return []*agentv1.ProtocolVersion{
		CurrentVersion(),
		LegacyVersion(),
	}
}

func IsSupportedVersion(version *agentv1.ProtocolVersion) bool {
	if version == nil || version.Major != TAPMajor {
		return false
	}
	return version.Minor == TAPCurrentMinor || version.Minor == TAPLegacyMinor
}

func SelectHighestCommon(proposed []*agentv1.ProtocolVersion) *agentv1.ProtocolVersion {
	for _, supported := range SupportedVersions() {
		for _, candidate := range proposed {
			if VersionsEqual(supported, candidate) {
				return supported
			}
		}
	}
	return nil
}

func IsSupportedBinding(binding *agentv1.ProtocolBinding) bool {
	return binding != nil && binding.Profile == BindingProfile && binding.Version == BindingVersion
}

func VersionsEqual(left, right *agentv1.ProtocolVersion) bool {
	return left != nil && right != nil && left.Major == right.Major && left.Minor == right.Minor
}

func BindingsEqual(left, right *agentv1.ProtocolBinding) bool {
	return left != nil && right != nil && left.Profile == right.Profile && left.Version == right.Version
}

func IsFrameHeaderValid(header *agentv1.FrameHeader, selectedVersion *agentv1.ProtocolVersion) bool {
	return header != nil && header.Protocol == "tap" && VersionsEqual(header.Version, selectedVersion)
}

func IsTap11OrLater(version *agentv1.ProtocolVersion) bool {
	return version != nil && version.Major == TAPMajor && version.Minor >= TAPCurrentMinor
}

func SupportsScheduleV1(version *agentv1.ProtocolVersion) bool { return IsTap11OrLater(version) }

func Header(correlationID string) *agentv1.FrameHeader {
	return HeaderForVersion(correlationID, CurrentVersion())
}

func HeaderForVersion(correlationID string, version *agentv1.ProtocolVersion) *agentv1.FrameHeader {
	if version == nil {
		version = CurrentVersion()
	}
	return &agentv1.FrameHeader{
		Protocol:      "tap",
		Version:       &agentv1.ProtocolVersion{Major: version.GetMajor(), Minor: version.GetMinor()},
		MessageId:     RandomID("msg"),
		SentAt:        timestamppb.Now(),
		CorrelationId: correlationID,
	}
}

func SetFrameVersion(frame *agentv1.AgentFrame, version *agentv1.ProtocolVersion) {
	if frame == nil || version == nil {
		return
	}
	if frame.Header == nil {
		frame.Header = HeaderForVersion("", version)
		return
	}
	frame.Header.Version = &agentv1.ProtocolVersion{Major: version.Major, Minor: version.Minor}
}

func RandomID(prefix string) string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	return prefix + "_" + hex.EncodeToString(b[:])
}

func RandomNonce() ([]byte, error) {
	nonce := make([]byte, 32)
	_, err := rand.Read(nonce)
	return nonce, err
}

func DigestPlan(plan *agentv1.RunPlan) ([]byte, error) {
	data, err := proto.MarshalOptions{Deterministic: true}.Marshal(plan)
	if err != nil {
		return nil, err
	}
	sum := sha256.Sum256(data)
	return sum[:], nil
}

func AuthTranscript(challenge *agentv1.SessionChallenge, agentID, keyID string, agentNonce []byte) ([]byte, error) {
	if challenge == nil || challenge.SelectedTapVersion == nil || challenge.SelectedBinding == nil {
		return nil, fmt.Errorf("challenge is incomplete")
	}
	issued := challenge.IssuedAt.AsTime().UTC().Format(time.RFC3339)
	text := fmt.Sprintf("TSKY-TAP-AUTH-V1\ncontroller_id=%s\nagent_id=%s\nkey_id=%s\nsession_id=%s\nagent_nonce=%s\ncontroller_nonce=%s\ntap_version=%d.%d\nbinding=%s\n",
		challenge.ControllerId,
		agentID,
		keyID,
		challenge.SessionId,
		base64.RawURLEncoding.EncodeToString(agentNonce),
		base64.RawURLEncoding.EncodeToString(challenge.ControllerNonce),
		challenge.SelectedTapVersion.Major,
		challenge.SelectedTapVersion.Minor,
		challenge.SelectedBinding.Profile,
	)
	if IsTap11OrLater(challenge.SelectedTapVersion) {
		text += fmt.Sprintf("binding_version=%s\n", challenge.SelectedBinding.Version)
	}
	text += fmt.Sprintf("issued_at=%s\n", issued)
	return []byte(text), nil
}
