# Codex Copilot Bridge

Local bridge for experimenting with GitHub Copilot as a Codex CLI inference provider.

## Run

```bash
dotnet run --project CodexCopilotBridge/CodexCopilotBridge.csproj --urls http://127.0.0.1:17776
```

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

## Development Mode

The host uses the GitHub Copilot SDK backend by default. Set `Bridge:Mode` to `Echo` to test the Codex provider contract without spending Copilot quota or depending on auth state.

The installed Copilot runtime on this machine accepted `claude-sonnet-4.5` and rejected `gpt-5` as unavailable during verification.

```bash
dotnet test
```

Echo-mode smoke run:

```bash
Bridge__Mode=Echo dotnet run --project CodexCopilotBridge/CodexCopilotBridge.csproj --urls http://127.0.0.1:17776
```
