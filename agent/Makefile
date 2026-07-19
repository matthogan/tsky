BINARY := tsky-agent
VERSION ?= dev
COMMIT ?= $(shell git rev-parse --short HEAD 2>/dev/null || echo none)
DATE ?= $(shell date -u +%Y-%m-%dT%H:%M:%SZ)
PROTO_DIR := ../protocol
PROTO_FILES := $(wildcard $(PROTO_DIR)/tsky/agent/v1/*.proto)
LDFLAGS := -X main.version=$(VERSION) -X main.commit=$(COMMIT) -X main.date=$(DATE)

.PHONY: all proto build test vet fmt tidy clean release-snapshot

all: proto tidy fmt vet test build

proto:
	protoc -I $(PROTO_DIR) \
		--go_out=. --go_opt=module=github.com/matthogan/tsky/agent \
		--go-grpc_out=. --go-grpc_opt=module=github.com/matthogan/tsky/agent \
		$(PROTO_FILES)

build:
	go build -trimpath -ldflags "$(LDFLAGS)" -o bin/$(BINARY) ./cmd/tsky-agent

test:
	go test ./...

vet:
	go vet ./...

fmt:
	gofmt -w $$(find . -name '*.go' -not -path './vendor/*')

tidy:
	go mod tidy

clean:
	rm -rf bin dist

release-snapshot:
	goreleaser release --snapshot --clean
