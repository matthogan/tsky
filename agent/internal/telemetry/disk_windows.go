//go:build windows

package telemetry

func diskFree(string) uint64 {
	return 0
}
