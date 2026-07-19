package runtimefs

import (
	"bufio"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/encoding/protojson"
)

const SchemaVersion = 1

type Store struct {
	root string
	mu   sync.Mutex
}

type Acceptance struct {
	SchemaVersion    int       `json:"schemaVersion"`
	OfferID          string    `json:"offerId"`
	RunID            string    `json:"runId"`
	AttemptID        string    `json:"attemptId"`
	PlanDigestSHA256 string    `json:"planDigestSha256"`
	AcceptedAt       time.Time `json:"acceptedAt"`
}

type State struct {
	SchemaVersion       int                     `json:"schemaVersion"`
	RunID               string                  `json:"runId"`
	AttemptID           string                  `json:"attemptId"`
	PlanDigestSHA256    string                  `json:"planDigestSha256"`
	State               agentv1.RunAttemptState `json:"state"`
	HighestEventSeq     uint64                  `json:"highestEventSequence"`
	StdoutSequence      uint64                  `json:"stdoutSequence"`
	StderrSequence      uint64                  `json:"stderrSequence"`
	ExitCode            int32                   `json:"exitCode,omitempty"`
	HasExitCode         bool                    `json:"hasExitCode,omitempty"`
	ErrorCode           string                  `json:"errorCode,omitempty"`
	ErrorMessage        string                  `json:"errorMessage,omitempty"`
	ProcessKnownRunning bool                    `json:"processIsKnownRunning"`
	UpdatedAt           time.Time               `json:"updatedAt"`
}

type StoredEvent struct {
	SchemaVersion int       `json:"schemaVersion"`
	RecordedAt    time.Time `json:"recordedAt"`
	Event         string    `json:"event"`
	Data          any       `json:"data"`
}

func New(root string) *Store {
	return &Store{root: root}
}

func (s *Store) Root() string {
	return s.root
}

func (s *Store) Init() error {
	for _, dir := range []string{s.root, filepath.Join(s.root, "runs")} {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			return err
		}
	}
	return nil
}

func (s *Store) Accept(offer *agentv1.RunOffer) (*Acceptance, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.Init(); err != nil {
		return nil, false, err
	}
	dir := s.AttemptDir(offer.RunId, offer.AttemptId)
	if existing, err := s.ReadAcceptance(offer.RunId, offer.AttemptId); err == nil {
		if existing.PlanDigestSHA256 != hex.EncodeToString(offer.PlanDigestSha256) {
			return existing, true, fmt.Errorf("run attempt already exists with a different plan digest")
		}
		return existing, true, nil
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, false, err
	}
	if err := os.MkdirAll(filepath.Join(dir, "work"), 0o700); err != nil {
		return nil, false, err
	}
	if err := os.MkdirAll(filepath.Join(dir, "artifacts"), 0o700); err != nil {
		return nil, false, err
	}
	acceptance := &Acceptance{
		SchemaVersion:    SchemaVersion,
		OfferID:          offer.OfferId,
		RunID:            offer.RunId,
		AttemptID:        offer.AttemptId,
		PlanDigestSHA256: hex.EncodeToString(offer.PlanDigestSha256),
		AcceptedAt:       time.Now().UTC(),
	}
	planData, err := protojson.MarshalOptions{Multiline: true, Indent: "  "}.Marshal(offer.Plan)
	if err != nil {
		return nil, false, err
	}
	if err := atomicWrite(filepath.Join(dir, "plan.json"), planData, 0o600); err != nil {
		return nil, false, err
	}
	if err := writeJSON(filepath.Join(dir, "acceptance.json"), acceptance); err != nil {
		return nil, false, err
	}
	state := State{
		SchemaVersion:    SchemaVersion,
		RunID:            offer.RunId,
		AttemptID:        offer.AttemptId,
		PlanDigestSHA256: acceptance.PlanDigestSHA256,
		State:            agentv1.RunAttemptState_RUN_ATTEMPT_STATE_ACCEPTED,
		UpdatedAt:        time.Now().UTC(),
	}
	if err := writeJSON(filepath.Join(dir, "state.json"), state); err != nil {
		return nil, false, err
	}
	if err := appendJSONLine(filepath.Join(dir, "events.ndjson"), StoredEvent{
		SchemaVersion: SchemaVersion,
		RecordedAt:    time.Now().UTC(),
		Event:         "accepted",
		Data:          acceptance,
	}); err != nil {
		return nil, false, err
	}
	return acceptance, false, nil
}

func (s *Store) ReadAcceptance(runID, attemptID string) (*Acceptance, error) {
	var out Acceptance
	if err := readJSON(filepath.Join(s.AttemptDir(runID, attemptID), "acceptance.json"), &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (s *Store) ReadState(runID, attemptID string) (*State, error) {
	var out State
	if err := readJSON(filepath.Join(s.AttemptDir(runID, attemptID), "state.json"), &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (s *Store) WriteState(state State) error {
	state.SchemaVersion = SchemaVersion
	state.UpdatedAt = time.Now().UTC()
	return writeJSON(filepath.Join(s.AttemptDir(state.RunID, state.AttemptID), "state.json"), state)
}

func (s *Store) AppendRunEvent(event *agentv1.RunEvent) error {
	return appendJSONLine(filepath.Join(s.AttemptDir(event.RunId, event.AttemptId), "events.ndjson"), StoredEvent{
		SchemaVersion: SchemaVersion,
		RecordedAt:    time.Now().UTC(),
		Event:         "run_event",
		Data:          event,
	})
}

func (s *Store) AppendOutput(output *agentv1.RunOutput) error {
	name := "stdout.log"
	if output.Stream == agentv1.OutputStream_OUTPUT_STREAM_STDERR {
		name = "stderr.log"
	}
	if err := appendBytes(filepath.Join(s.AttemptDir(output.RunId, output.AttemptId), name), output.Data); err != nil {
		return err
	}
	return appendJSONLine(filepath.Join(s.AttemptDir(output.RunId, output.AttemptId), "events.ndjson"), StoredEvent{
		SchemaVersion: SchemaVersion,
		RecordedAt:    time.Now().UTC(),
		Event:         "run_output",
		Data: map[string]any{
			"stepId": output.StepId,
			"stream": output.Stream.String(),
			"seq":    output.OutputSequence,
			"bytes":  len(output.Data),
		},
	})
}

func (s *Store) Attempts(includeTerminal bool) ([]State, error) {
	root := filepath.Join(s.root, "runs")
	entries, err := os.ReadDir(root)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var states []State
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		statePath := filepath.Join(root, entry.Name(), "state.json")
		var state State
		if err := readJSON(statePath, &state); err != nil {
			continue
		}
		if includeTerminal || !terminal(state.State) {
			states = append(states, state)
		}
	}
	return states, nil
}

func (s *Store) AttemptDir(runID, attemptID string) string {
	return filepath.Join(s.root, "runs", sanitize(runID)+"_"+sanitize(attemptID))
}

func (s *Store) WorkDir(runID, attemptID string) string {
	return filepath.Join(s.AttemptDir(runID, attemptID), "work")
}

func terminal(state agentv1.RunAttemptState) bool {
	switch state {
	case agentv1.RunAttemptState_RUN_ATTEMPT_STATE_REJECTED,
		agentv1.RunAttemptState_RUN_ATTEMPT_STATE_CANCELLED,
		agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED,
		agentv1.RunAttemptState_RUN_ATTEMPT_STATE_FAILED,
		agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT,
		agentv1.RunAttemptState_RUN_ATTEMPT_STATE_INTERRUPTED:
		return true
	default:
		return false
	}
}

func sanitize(id string) string {
	id = strings.TrimSpace(id)
	id = strings.Map(func(r rune) rune {
		if r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '_' || r == '-' || r == '.' {
			return r
		}
		return '_'
	}, id)
	if id == "" || id == "." || id == ".." {
		return "unknown"
	}
	return id
}

func writeJSON(path string, value any) error {
	data, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	data = append(data, '\n')
	return atomicWrite(path, data, 0o600)
}

func readJSON(path string, value any) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	if len(data) > 16*1024*1024 {
		return fmt.Errorf("%s is larger than the runtime store safety limit", path)
	}
	return json.Unmarshal(data, value)
}

func atomicWrite(path string, data []byte, perm os.FileMode) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, perm); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func appendJSONLine(path string, value any) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	w := bufio.NewWriter(f)
	if err := json.NewEncoder(w).Encode(value); err != nil {
		return err
	}
	if err := w.Flush(); err != nil {
		return err
	}
	return f.Sync()
}

func appendBytes(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err := f.Write(data); err != nil {
		return err
	}
	return f.Sync()
}
