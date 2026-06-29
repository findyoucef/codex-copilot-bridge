# Codex Copilot Bridge

Local bridge for experimenting with GitHub Copilot as a Codex CLI inference provider.

## Run

```bash
dotnet run --project CodexCopilotBridge/CodexCopilotBridge.csproj --urls http://127.0.0.1:17776
```

The default mode is `CopilotBridge`: Codex sends its tool declarations to the bridge, Copilot can request those declaration-only tools, and Codex remains the executor for shell, filesystem, MCP, and other tool calls.

## Health

```bash
curl http://127.0.0.1:17776/healthz
```

The health response includes `pending_tool_calls`, `active_copilot_sessions`, `oldest_pending_tool_call_age_seconds`, `pending_tool_call_ttl_seconds`, and `request_timeout_seconds`.

## Codex Provider Config

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

## Smoke Test

```bash
codex exec --ignore-user-config --skip-git-repo-check \
  -c 'model_providers.github-copilot-bridge={ name="GitHub Copilot Bridge", base_url="http://127.0.0.1:17776/v1", wire_api="responses", requires_openai_auth=false, supports_websockets=false }' \
  -c 'model_provider="github-copilot-bridge"' \
  -m gpt-5.5 \
  --json 'Use the shell_command tool to run: printf "codex-copilot-full-loop-ok". Then reply with exactly the command output and no other text.'
```

Expected result: Codex executes the command, then the final assistant message is `codex-copilot-full-loop-ok`. Codex may warn that it has no built-in model metadata for `gpt-5.5`; the bridge call still completes.

## Development Modes

Set `Bridge:Mode` to `Echo` to test the Codex provider contract without spending Copilot quota or depending on auth state.

Set `Bridge:Mode` to `CopilotSdk` for text-only Copilot streaming without tool bridging.

The installed Copilot runtime on this machine accepted `claude-sonnet-4.5` and rejected `gpt-5` as unavailable during verification.

```bash
dotnet test
```

Echo-mode smoke run:

```bash
Bridge__Mode=Echo dotnet run --project CodexCopilotBridge/CodexCopilotBridge.csproj --urls http://127.0.0.1:17776
```

Tool-loop probe:

```bash
Bridge__Mode=ToolProbe dotnet run --project CodexCopilotBridge/CodexCopilotBridge.csproj --urls http://127.0.0.1:17776
```

Then point Codex at the bridge. The probe backend emits a `shell_command` function call, waits for Codex to execute it, and summarizes the returned `function_call_output`.

Copilot external-tool probe:

```bash
Bridge__Mode=CopilotToolProbe dotnet run --project CodexCopilotBridge/CodexCopilotBridge.csproj --urls http://127.0.0.1:17776
```

This probe registers a declaration-only `bridge_echo` tool with Copilot, responds to the SDK's `external_tool.requested` event through the pending-tool RPC, and returns Copilot's final answer.

Codex smoke command:

```bash
codex exec --ignore-user-config --skip-git-repo-check \
  -c 'model_providers.github-copilot-bridge={ name="GitHub Copilot Bridge", base_url="http://127.0.0.1:17776/v1", wire_api="responses", requires_openai_auth=false, supports_websockets=false }' \
  -c 'model_provider="github-copilot-bridge"' \
  -m gpt-5.5 \
  --json 'Run the Copilot external tool probe.'
```

Codex may warn that it has no built-in model metadata for `gpt-5.5`; the probe still verifies Copilot external-tool request/response handling.

## Lifecycle Settings

```json
{
  "Bridge": {
    "RequestTimeout": "00:02:00",
    "PendingToolCallTtl": "00:10:00",
    "PendingToolCallCleanupInterval": "00:01:00"
  }
}
```

`RequestTimeout` caps each active `/v1/responses` bridge operation. `PendingToolCallTtl` caps how long a Copilot session can wait for Codex to return tool output. The cleanup service disposes expired sessions on `PendingToolCallCleanupInterval`.
