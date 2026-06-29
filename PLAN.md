# Codex Copilot Bridge Plan

## Goal

Build a local inference-provider bridge that lets stock Codex CLI use GitHub Copilot as its model backend.

The bridge should be a new project, not a fork of Codex. Codex already supports custom providers configured with a `base_url` and `wire_api = "responses"`, so the clean boundary is:

```text
Codex CLI -> local OpenAI Responses-compatible HTTP server -> GitHub Copilot SDK -> Copilot models
```

## Initial Scope

1. Implement a local HTTP server that exposes a Codex-compatible provider surface.
2. Return valid Responses API server-sent events for text-only turns.
3. Add a Copilot SDK adapter behind an internal interface.
4. Keep Codex as the only workspace tool executor.
5. Prove the provider boundary with tests before building the full tool-call loop.

## Non-Goals For The First Build

1. Forking Codex.
2. Wrapping `codex exec` as a subprocess.
3. Letting Copilot's built-in filesystem or shell tools edit the workspace.
4. Supporting every Responses API feature on day one.

## Architecture

```text
src/CodexCopilotBridge
  ASP.NET Core host
  /healthz
  /v1/models
  /v1/responses
  SSE streaming
  bridge adapter selection

src/CodexCopilotBridge.Protocol
  Codex request DTOs
  Responses SSE event DTOs
  Codex-compatible model catalog DTOs
  prompt rendering helpers

tests/CodexCopilotBridge.Tests
  serialization tests
  model catalog tests
  prompt rendering tests
```

## Codex Configuration

Add this to a Codex `config.toml` profile or pass it with `-c` overrides:

```toml
[model_providers.github-copilot-bridge]
name = "GitHub Copilot Bridge"
base_url = "http://127.0.0.1:17776/v1"
wire_api = "responses"
requires_openai_auth = false
supports_websockets = false
stream_idle_timeout_ms = 600000

model_provider = "github-copilot-bridge"
model = "claude-sonnet-4.5"
```

Development smoke command:

```bash
codex exec \
  --ignore-user-config \
  --skip-git-repo-check \
  -c 'model_providers.github-copilot-bridge={ name="GitHub Copilot Bridge", base_url="http://127.0.0.1:17776/v1", wire_api="responses", requires_openai_auth=false, supports_websockets=false }' \
  -c 'model_provider="github-copilot-bridge"' \
  -m claude-sonnet-4.5 \
  --json \
  "Say hello through the bridge."
```

## Milestones

### 1. Provider Contract MVP

- [x] Create a new .NET 10 solution.
- [x] Add protocol and test projects.
- [x] Implement `GET /healthz`.
- [x] Implement `GET /v1/models`.
- [x] Implement `POST /v1/responses` with valid Responses SSE.
- [x] Add an echo adapter for deterministic contract testing.
- [x] Run Codex against the local provider.

### 2. Text-Only Copilot SDK Adapter

- [x] Add GitHub Copilot SDK and Microsoft Agent Framework Copilot package references.
- [x] Add adapter boundary for inference backends.
- [x] Implement text-only Copilot session streaming.
- [x] Map Copilot assistant deltas to `response.output_text.delta`.
- [x] Map Copilot final assistant messages to `response.output_item.done`.
- [x] Map Copilot completion/idle events to `response.completed`.
- [x] Add cancellation and timeout handling.

### 3. Codex Tool Loop Spike

- [x] Emit a synthetic Codex `function_call` item from the bridge.
- [x] Verify Codex executes the tool and sends `function_call_output` back.
- [x] Add tests for `function_call_output` prompt rendering.

### 4. Copilot External Tool Spike

- [x] Register one declaration-only Copilot tool.
- [x] Observe `external_tool.requested`.
- [x] Respond through the Copilot SDK pending tool-call API.
- [x] Decide whether the high-level SDK is sufficient or whether direct runtime RPC is needed.

### 5. Full Tool Bridge

- [x] Translate Codex tool schemas to Copilot tool declarations.
- [x] Convert Copilot external tool requests to Codex Responses tool-call items.
- [x] Correlate pending Copilot tool calls with Codex follow-up requests.
- [x] Send Codex tool outputs back into the Copilot session.
- [x] Add session cleanup and idle expiration.

### 6. Lifecycle Hardening

- [x] Centralize pending tool calls in a store.
- [x] Add request timeout handling.
- [x] Add pending tool-call TTL cleanup.
- [x] Expose pending-call and active-session counts from `/healthz`.
- [x] Add tests for store removal, expiry, and snapshots.
- [ ] Add structured metrics export.

### 7. Packaging And Operations

- [x] Add CI restore/test/build workflow.
- [x] Add osx-arm64 self-contained publish artifact workflow.
- [x] Add local osx-arm64 publish script.
- [x] Add macOS launchd plist template.
- [x] Add macOS install/uninstall scripts.
- [ ] Add Homebrew formula.

## Safety Rules

1. Bind to loopback only by default.
2. Do not expose Copilot built-in shell/filesystem tools in bridge mode.
3. Let Codex enforce sandbox, approvals, and workspace writes.
4. Redact auth material from logs.
5. Keep Copilot session state under a bridge-specific base directory.

## Current Implementation Status

The implementation is now a runnable tool bridge. It can serve Codex-compatible `/v1/models` metadata, stream a minimal Responses-compatible text turn from an echo adapter, stream text-only answers from the GitHub Copilot SDK adapter, and run the full Codex tool loop through the `CopilotBridge` mode. `CopilotBridge` is now the default runtime mode. Local verification showed `claude-sonnet-4.5` works with the installed Copilot runtime; `gpt-5` was rejected by that runtime as unavailable.

The Codex tool-loop probe is verified: the bridge can emit a `shell_command` function call, Codex executes it, and the bridge receives the resulting `function_call_output`.

The Copilot external-tool probe is verified: the high-level GitHub Copilot SDK can register a declaration-only tool, surface `external_tool.requested`, and accept a result through `HandlePendingToolCallAsync`. This path was verified directly over `/v1/responses` and through `codex exec -m gpt-5.5`. Codex currently warns that it has no built-in metadata for `gpt-5.5`, but the bridge request still completes.

The initial full bridge loop is verified: when Copilot requests an external tool, the bridge emits a Codex `function_call` item, persists the pending Copilot request, reads Codex's follow-up `function_call_output`, submits that output back to Copilot, and continues the Copilot session to final text. The live smoke used `codex exec -m gpt-5.5`; Copilot requested Codex's `exec_command` tool, Codex executed `printf "codex-copilot-full-loop-ok"`, and Copilot returned `codex-copilot-full-loop-ok` as the final message.

Lifecycle hardening is now implemented for in-memory state: pending calls are centralized in a store, active operations have a request timeout, pending tool calls expire after a TTL, expired Copilot sessions are disposed by a hosted cleanup service, and `/healthz` reports pending-call and active-session counts. Store lifecycle behavior is covered by unit tests.

Packaging and operations now have a first pass: CI runs restore/test/build, non-PR runs publish an osx-arm64 artifact, local publishing creates both a publish directory and tarball, and macOS launchd install/uninstall scripts run the bridge as a user service.

The next production-relevant slice is distribution polish: Homebrew formula, signed/notarized macOS artifacts if this is shared outside your machine, and structured metrics export.
