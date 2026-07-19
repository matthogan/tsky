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

func Header(correlationID string) *agentv1.FrameHeader {
	return &agentv1.FrameHeader{
		Protocol:      "tap",
		Version:       &agentv1.ProtocolVersion{Major: 1, Minor: 0},
		MessageId:     RandomID("msg"),
		SentAt:        timestamppb.Now(),
		CorrelationId: correlationID,
	}
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
	text := fmt.Sprintf("TSKY-TAP-AUTH-V1\ncontroller_id=%s\nagent_id=%s\nkey_id=%s\nsession_id=%s\nagent_nonce=%s\ncontroller_nonce=%s\ntap_version=%d.%d\nbinding=%s\nissued_at=%s\n",
		challenge.ControllerId,
		agentID,
		keyID,
		challenge.SessionId,
		base64.RawURLEncoding.EncodeToString(agentNonce),
		base64.RawURLEncoding.EncodeToString(challenge.ControllerNonce),
		challenge.SelectedTapVersion.Major,
		challenge.SelectedTapVersion.Minor,
		challenge.SelectedBinding.Profile,
		issued,
	)
	return []byte(text), nil
}
