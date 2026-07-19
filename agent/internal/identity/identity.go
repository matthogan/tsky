package identity

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/pem"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"strings"

	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
)

const filePrefix = "file://"

type Manager struct {
	key *ecdsa.PrivateKey
	ref string
}

func GenerateFile(path string) (*Manager, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	block := &pem.Block{Type: "EC PRIVATE KEY", Bytes: der}
	if err := os.WriteFile(path, pem.EncodeToMemory(block), 0o600); err != nil {
		return nil, err
	}
	return &Manager{key: key, ref: filePrefix + path}, nil
}

func Load(reference string) (*Manager, error) {
	if reference == "" {
		return nil, fmt.Errorf("identity key reference is required")
	}
	path := strings.TrimPrefix(reference, filePrefix)
	if path == reference && strings.Contains(reference, "://") {
		return nil, fmt.Errorf("unsupported key reference scheme: %s", reference)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	block, _ := pem.Decode(data)
	if block == nil || block.Type != "EC PRIVATE KEY" {
		return nil, fmt.Errorf("identity key is not an EC private key")
	}
	key, err := x509.ParseECPrivateKey(block.Bytes)
	if err != nil {
		return nil, err
	}
	if key.Curve != elliptic.P256() {
		return nil, fmt.Errorf("identity key must use P-256")
	}
	return &Manager{key: key, ref: reference}, nil
}

func (m *Manager) Reference() string {
	return m.ref
}

func (m *Manager) PublicJWK() *agentv1.EcPublicKeyJwk {
	return &agentv1.EcPublicKeyJwk{
		Kty: "EC",
		Crv: "P-256",
		X:   base64.RawURLEncoding.EncodeToString(pad32(m.key.PublicKey.X)),
		Y:   base64.RawURLEncoding.EncodeToString(pad32(m.key.PublicKey.Y)),
	}
}

func (m *Manager) SignP1363(message []byte) ([]byte, error) {
	sum := sha256.Sum256(message)
	r, s, err := ecdsa.Sign(rand.Reader, m.key, sum[:])
	if err != nil {
		return nil, err
	}
	sig := make([]byte, 0, 64)
	sig = append(sig, pad32(r)...)
	sig = append(sig, pad32(s)...)
	return sig, nil
}

func pad32(v *big.Int) []byte {
	out := make([]byte, 32)
	b := v.Bytes()
	copy(out[32-len(b):], b)
	return out
}
