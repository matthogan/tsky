# Tsky Agent Protocol versions

The current semantic protocol is **TAP 1.1**. Implementations advertise TAP 1.1 and TAP 1.0, then select the highest exact common version during `SessionHello` / `SessionChallenge` negotiation.

Because `SessionHello` is sent before selection, its envelope uses the TAP 1.0 compatibility baseline. Every frame after `SessionChallenge` uses the selected semantic version.

The TAP 1.1 authentication transcript includes both `binding` and `binding_version`. TAP 1.0 omits the binding-version line to retain its original proof format.

Version numbers are independent:

- The technical specification revision describes a document and is not sent on the wire.
- TAP `major.minor` describes message semantics and state-machine behavior.
- A named binding version describes how TAP frames are carried. The required binding remains `tap-grpc` version 1.
- Extension versions describe optional features. `schedule.v1` version 1 requires a negotiated TAP 1.1 session.

Additive semantic changes increment the TAP minor version while retaining older versions when compatibility is implemented. Breaking semantic changes increment the TAP major version and require a new protobuf package namespace. A representation-only change increments the affected binding version and does not automatically increment TAP Core.
