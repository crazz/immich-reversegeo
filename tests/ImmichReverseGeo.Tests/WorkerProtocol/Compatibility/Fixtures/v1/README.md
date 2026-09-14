# Worker protocol v1 fixtures

These fixtures are raw UTF-8 bytes with no BOM. Canonical files contain one compact JSON object with no transport delimiter; framing tests add an LF or CRLF independently.

They record the `immich-reversegeo.worker` worker-to-controller protocol, version 1. Canonical and original-v1 fixtures are backward-compatibility evidence: keep them append-only and readable while v1 is supported. Same-v1 additive fixtures are forward-compatibility evidence and remain separate from canonical files.

Do not blanket-regenerate fixtures or overwrite canonical bytes after serializer changes. Correct an approved mistaken fixture only with a reviewed protocol decision documented here; otherwise restore v1 output or add a new versioned fixture directory.

Scope proof: these fixtures and their sibling compatibility tests call only `WorkerProtocolCodec`, `WorkerProtocolV1`, payload/event records, and `WorkerProtocolEventStreamValidator` from the Core project reference. They contain worker-to-controller envelopes only; no request/command, host, runtime, transport, stream, or executable evidence belongs here.
