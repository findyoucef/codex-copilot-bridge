using System.Text.Json;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexCopilotBridge.Tests;

public sealed class PendingToolCallStoreTests
{
    [Fact]
    public void TryTakeOutputs_ReturnsAndRemovesMatchingPendingCall()
    {
        var store = CreateStore();
        var session = new FakeSession();
        var pending = CreatePendingToolCall("call_1", session);
        Assert.True(store.TryAdd(pending));
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.5",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "tool output"
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var found = store.TryTakeOutputs(request!, out var outputs);

        Assert.True(found);
        var output = Assert.Single(outputs);
        Assert.Same(pending, output.PendingCall);
        Assert.Equal("tool output", output.Output);
        Assert.Equal(0, store.Snapshot().PendingToolCallCount);
    }

    [Fact]
    public void TryFindOutputs_ReturnsMatchingPendingCallWithoutRemovingIt()
    {
        var store = CreateStore();
        var session = new FakeSession();
        var pending = CreatePendingToolCall("call_1", session);
        Assert.True(store.TryAdd(pending));
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.5",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "tool output"
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var found = store.TryFindOutputs(request!, out var outputs);

        Assert.True(found);
        var output = Assert.Single(outputs);
        Assert.Same(pending, output.PendingCall);
        Assert.Equal("tool output", output.Output);
        Assert.Equal(1, store.Snapshot().PendingToolCallCount);
    }

    [Fact]
    public void TryFindOutputs_IgnoresHistoricalPendingOutputFollowedByUserInput()
    {
        var store = CreateStore();
        var session = new FakeSession();
        Assert.True(store.TryAdd(CreatePendingToolCall("call_1", session)));
        var request = Request(
            """
            {
              "type": "function_call_output",
              "call_id": "call_1",
              "output": "tool output"
            }
            """,
            """
            {
              "type": "message",
              "role": "user",
              "content": [{ "type": "input_text", "text": "next turn" }]
            }
            """);

        var found = store.TryFindOutputs(request, out var outputs);

        Assert.False(found);
        Assert.Empty(outputs);
        Assert.Equal(1, store.Snapshot().PendingToolCallCount);
    }

    [Fact]
    public void ContainsTrailingRetiredOutput_MatchesRemovedCallWithoutSuppressingLaterUserInput()
    {
        var store = CreateStore();
        var session = new FakeSession();
        var pending = CreatePendingToolCall("call_1", session);
        Assert.True(store.TryAdd(pending));
        Assert.True(store.TryRemove(pending));
        var duplicate = Request(
            """
            {
              "type": "function_call_output",
              "call_id": "call_1",
              "output": "tool output"
            }
            """);
        var laterTurn = Request(
            """
            {
              "type": "function_call_output",
              "call_id": "call_1",
              "output": "tool output"
            }
            """,
            """
            {
              "type": "message",
              "role": "user",
              "content": [{ "type": "input_text", "text": "next turn" }]
            }
            """);

        Assert.True(store.ContainsTrailingRetiredOutput(duplicate));
        Assert.False(store.ContainsTrailingRetiredOutput(laterTurn));
    }

    [Fact]
    public void TryTakeOutputs_ReturnsAllMatchingPendingCallsForOneSession()
    {
        var store = CreateStore();
        var firstSession = new FakeSession();
        var secondSession = new FakeSession();
        var firstPending = CreatePendingToolCall("call_1", firstSession);
        var secondPending = CreatePendingToolCall("call_2", firstSession);
        var otherSessionPending = CreatePendingToolCall("call_3", secondSession);
        Assert.True(store.TryAdd(firstPending));
        Assert.True(store.TryAdd(secondPending));
        Assert.True(store.TryAdd(otherSessionPending));
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.5",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "first output"
                },
                {
                  "type": "function_call_output",
                  "call_id": "call_2",
                  "output": "second output"
                },
                {
                  "type": "function_call_output",
                  "call_id": "call_3",
                  "output": "other session output"
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var found = store.TryTakeOutputs(request!, out var outputs);

        Assert.True(found);
        Assert.Equal(2, outputs.Count);
        Assert.Contains(outputs, output => ReferenceEquals(output.PendingCall, firstPending) && output.Output == "first output");
        Assert.Contains(outputs, output => ReferenceEquals(output.PendingCall, secondPending) && output.Output == "second output");
        Assert.Equal(1, store.Snapshot().PendingToolCallCount);
    }

    [Fact]
    public void TryTakeOutputs_IgnoresHistoricalOutputsWhenNoPendingCallMatches()
    {
        var store = CreateStore();
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.5",
              "input": [
                {
                  "type": "function_call",
                  "call_id": "call_1",
                  "name": "shell_command",
                  "arguments": "{\"command\":\"pwd\"}"
                },
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "historical output"
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var found = store.TryTakeOutputs(request!, out var outputs);

        Assert.False(found);
        Assert.Empty(outputs);
    }

    [Fact]
    public void RemoveForSession_RemovesOnlyMatchingSessionCalls()
    {
        var store = CreateStore();
        var firstSession = new FakeSession();
        var secondSession = new FakeSession();
        Assert.True(store.TryAdd(CreatePendingToolCall("call_1", firstSession)));
        Assert.True(store.TryAdd(CreatePendingToolCall("call_2", firstSession)));
        Assert.True(store.TryAdd(CreatePendingToolCall("call_3", secondSession)));

        var removed = store.RemoveForSession(firstSession);
        var snapshot = store.Snapshot();

        Assert.Equal(2, removed);
        Assert.Equal(1, snapshot.PendingToolCallCount);
        Assert.Equal(1, snapshot.ActiveSessionCount);
    }

    [Fact]
    public async Task ExpireAsync_RemovesExpiredCallsAndDisposesSessionOnce()
    {
        var store = CreateStore();
        var session = new FakeSession();
        Assert.True(store.TryAdd(CreatePendingToolCall("call_1", session)));
        Assert.True(store.TryAdd(CreatePendingToolCall("call_2", session)));

        var expired = await store.ExpireAsync(
            DateTimeOffset.UtcNow.AddMinutes(11),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);

        Assert.Equal(2, expired);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, store.Snapshot().PendingToolCallCount);
    }

    [Fact]
    public async Task ExpireAsync_RemovesFreshSiblingCallsWithRetiredSession()
    {
        var store = CreateStore();
        var session = new FakeSession();
        var oldCall = CreatePendingToolCall(
            "call_old",
            session,
            DateTimeOffset.UtcNow.AddMinutes(-20));
        var freshCall = CreatePendingToolCall(
            "call_fresh",
            session);
        Assert.True(store.TryAdd(oldCall));
        Assert.True(store.TryAdd(freshCall));
        oldCall.Touch(DateTimeOffset.UtcNow.AddMinutes(-20));

        var expired = await store.ExpireAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);

        Assert.Equal(2, expired);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, store.Snapshot().PendingToolCallCount);
    }

    [Fact]
    public void Snapshot_ReturnsPendingAndActiveSessionCounts()
    {
        var store = CreateStore();
        var firstSession = new FakeSession();
        var secondSession = new FakeSession();
        Assert.True(store.TryAdd(CreatePendingToolCall("call_1", firstSession)));
        Assert.True(store.TryAdd(CreatePendingToolCall("call_2", firstSession)));
        Assert.True(store.TryAdd(CreatePendingToolCall("call_3", secondSession)));

        var snapshot = store.Snapshot(DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.Equal(3, snapshot.PendingToolCallCount);
        Assert.Equal(2, snapshot.ActiveSessionCount);
        Assert.NotNull(snapshot.OldestPendingCallAgeSeconds);
        Assert.True(snapshot.OldestPendingCallAgeSeconds >= 0);
    }

    private static PendingToolCallStore CreateStore() =>
        new(NullLogger<PendingToolCallStore>.Instance);

    private static ResponsesApiRequest Request(params string[] inputItems) =>
        JsonSerializer.Deserialize<ResponsesApiRequest>(
            $$"""
            {
              "model": "gpt-5.5",
              "input": [{{string.Join(",", inputItems)}}]
            }
            """,
            ProtocolJson.SerializerOptions)!;

    private static PendingToolCall CreatePendingToolCall(
        string callId,
        FakeSession session,
        DateTimeOffset? createdAt = null) =>
        new(
            callId,
            $"request_{callId}",
            session,
            new CodexToolDeclaration(
                "shell_command",
                "shell_command",
                null,
                "Run a command.",
                EmptyParametersSchema()),
            createdAt ?? DateTimeOffset.UtcNow);

    private static JsonElement EmptyParametersSchema() =>
        JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """,
            ProtocolJson.SerializerOptions);

    private sealed class FakeSession : IPendingBridgeSession
    {
        public string Model { get; } = "test-model";

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
