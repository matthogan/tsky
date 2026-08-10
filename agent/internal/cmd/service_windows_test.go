//go:build windows

package cmd

import (
	"testing"

	"golang.org/x/sys/windows/svc/mgr"
)

func TestParseServiceStartMode(t *testing.T) {
	tests := []struct {
		name    string
		input   string
		expect  uint32
		delayed bool
		wantErr bool
	}{
		{name: "auto", input: "auto", expect: mgr.StartAutomatic, delayed: true},
		{name: "automatic", input: "automatic", expect: mgr.StartAutomatic, delayed: true},
		{name: "auto-delayed", input: "auto-delayed", expect: mgr.StartAutomatic, delayed: true},
		{name: "automatic-delayed", input: "automatic-delayed", expect: mgr.StartAutomatic, delayed: true},
		{name: "delayed", input: "delayed", expect: mgr.StartAutomatic, delayed: true},
		{name: "manual", input: "manual", expect: mgr.StartManual, delayed: false},
		{name: "demand", input: "demand", expect: mgr.StartManual, delayed: false},
		{name: "invalid", input: "later", wantErr: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, delayed, err := parseServiceStartMode(tc.input)
			if tc.wantErr {
				if err == nil {
					t.Fatalf("expected error for input %q", tc.input)
				}
				return
			}
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if got != tc.expect {
				t.Fatalf("start mode mismatch for %q: got=%v want=%v", tc.input, got, tc.expect)
			}
			if delayed != tc.delayed {
				t.Fatalf("delayed auto-start mismatch for %q: got=%v want=%v", tc.input, delayed, tc.delayed)
			}
		})
	}
}
