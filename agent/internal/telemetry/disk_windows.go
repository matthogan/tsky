//go:build windows

package telemetry

import "golang.org/x/sys/windows"

func diskFree(path string) uint64 {
	if path == "" {
		return 0
	}

	pathPtr, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return 0
	}

	var freeBytesAvailable uint64
	if err := windows.GetDiskFreeSpaceEx(pathPtr, &freeBytesAvailable, nil, nil); err != nil {
		return 0
	}

	return freeBytesAvailable
}
