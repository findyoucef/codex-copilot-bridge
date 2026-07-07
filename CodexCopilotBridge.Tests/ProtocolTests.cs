using System.Text.Json;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;
using Microsoft.AspNetCore.Http;

namespace CodexCopilotBridge.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void OutputMessageDone_UsesCodexResponseItemShape()
    {
        var json = JsonSerializer.Serialize(
            ResponsesEvents.OutputMessageDone("resp_123", "hello"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("response.output_item.done", root.GetProperty("type").GetString());
        Assert.Equal("message", root.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("item").GetProperty("role").GetString());
        Assert.Equal("output_text", root.GetProperty("item").GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("hello", root.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void OutputMessageAdded_StartsCodexStreamingItem()
    {
        var json = JsonSerializer.Serialize(
            ResponsesEvents.OutputMessageAdded("resp_123"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("response.output_item.added", root.GetProperty("type").GetString());
        Assert.Equal("message", root.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("item").GetProperty("role").GetString());
        Assert.Equal(string.Empty, root.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void OutputTextDelta_IncludesStreamingIndexes()
    {
        var json = JsonSerializer.Serialize(
            ResponsesEvents.OutputTextDelta("resp_123", "hello"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("response.output_text.delta", root.GetProperty("type").GetString());
        Assert.Equal("msg_resp_123", root.GetProperty("item_id").GetString());
        Assert.Equal(0, root.GetProperty("output_index").GetInt32());
        Assert.Equal(0, root.GetProperty("content_index").GetInt32());
        Assert.Equal("hello", root.GetProperty("delta").GetString());
    }

    [Fact]
    public void OutputTextDone_UsesResponsesTextDoneShape()
    {
        var json = JsonSerializer.Serialize(
            ResponsesEvents.OutputTextDone("resp_123", "hello"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("response.output_text.done", root.GetProperty("type").GetString());
        Assert.Equal("msg_resp_123", root.GetProperty("item_id").GetString());
        Assert.Equal("hello", root.GetProperty("text").GetString());
    }

    [Fact]
    public void FunctionCallDone_UsesCodexFunctionCallShape()
    {
        var json = JsonSerializer.Serialize(
            ResponsesEvents.FunctionCallDone("call_1", "shell_command", "{\"command\":\"pwd\"}"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("response.output_item.done", root.GetProperty("type").GetString());
        Assert.Equal("function_call", root.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("call_1", root.GetProperty("item").GetProperty("call_id").GetString());
        Assert.Equal("shell_command", root.GetProperty("item").GetProperty("name").GetString());
        Assert.Equal("{\"command\":\"pwd\"}", root.GetProperty("item").GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task JsonResponseWriter_AggregatesDeltasForNonStreamingResponse()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var request = new ResponsesApiRequest { Model = "gpt-5.5", Stream = false };
        var bridge = new FakeBridge(
            new TextDeltaOutput("hel"),
            new TextDeltaOutput("lo"),
            new FinalTextOutput("hello"));

        await JsonResponseWriter.WriteResponseAsync(context, request, bridge, CancellationToken.None);

        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        var root = document.RootElement;

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("end_turn").GetBoolean());
        Assert.Equal("message", root.GetProperty("output")[0].GetProperty("type").GetString());
        Assert.Equal("hello", root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task SseResponseWriter_UsesDistinctOutputIndexesForMixedTextAndToolCall()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var request = new ResponsesApiRequest { Model = "gpt-5.5", Stream = true };
        var bridge = new FakeBridge(
            new TextDeltaOutput("checking"),
            new FunctionCallRequestOutput("call_1", "shell_command", "{\"command\":\"pwd\"}"));

        await SseResponseWriter.WriteResponseAsync(context, request, bridge, CancellationToken.None);

        body.Position = 0;
        using var reader = new StreamReader(body);
        var events = ReadSseDataEvents(await reader.ReadToEndAsync());

        var textDelta = events.Single(EventType("response.output_text.delta"));
        var textDone = events.Single(EventType("response.output_text.done"));
        var contentPartDone = events.Single(EventType("response.content_part.done"));
        var messageDone = events.Single(evt =>
            evt.GetProperty("type").GetString() == "response.output_item.done" &&
            evt.GetProperty("item").GetProperty("type").GetString() == "message");
        var functionCall = events.Single(evt =>
            evt.GetProperty("type").GetString() == "response.output_item.done" &&
            evt.GetProperty("item").GetProperty("type").GetString() == "function_call");

        Assert.Equal(0, textDelta.GetProperty("output_index").GetInt32());
        Assert.Equal(0, textDone.GetProperty("output_index").GetInt32());
        Assert.Equal(1, functionCall.GetProperty("output_index").GetInt32());
        Assert.True(EventIndex(events, textDone) < EventIndex(events, functionCall));
        Assert.True(EventIndex(events, contentPartDone) < EventIndex(events, functionCall));
        Assert.True(EventIndex(events, messageDone) < EventIndex(events, functionCall));
    }

    [Fact]
    public void ModelCatalog_IncludesRequiredCodexFields()
    {
        var json = JsonSerializer.Serialize(
            CodexModelCatalog.CreateDefault("gpt-5"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var model = document.RootElement.GetProperty("models")[0];

        Assert.Equal("gpt-5", model.GetProperty("slug").GetString());
        Assert.Equal("shell_command", model.GetProperty("shell_type").GetString());
        Assert.Equal("list", model.GetProperty("visibility").GetString());
        Assert.True(model.GetProperty("supported_in_api").GetBoolean());
        Assert.False(model.GetProperty("supports_parallel_tool_calls").GetBoolean());
        Assert.Equal("bytes", model.GetProperty("truncation_policy").GetProperty("mode").GetString());
    }

    [Fact]
    public void PromptRenderer_FlattensMessagesAndToolOutputs()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "instructions": "Be concise.",
              "input": [
                {
                  "type": "message",
                  "role": "user",
                  "content": [
                    { "type": "input_text", "text": "List files." }
                  ]
                },
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "README.md"
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var prompt = CodexPromptRenderer.Render(request!);

        Assert.Contains("Instructions:", prompt);
        Assert.Contains("Be concise.", prompt);
        Assert.Contains("user: List files.", prompt);
        Assert.Contains("function_call_output call_1: README.md", prompt);
    }

    [Fact]
    public void PromptRenderer_CanOmitDisabledToolNotice()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [],
              "tools": [
                {
                  "type": "function",
                  "name": "shell_command",
                  "description": "Run a command.",
                  "parameters": { "type": "object", "properties": {} }
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var prompt = CodexPromptRenderer.Render(request!, includeToolNotice: false);

        Assert.DoesNotContain("Tool bridging is not enabled", prompt);
    }

    [Fact]
    public void ToolOutputReader_FindsFunctionCallOutputText()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
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

        var output = ToolOutputReader.FindTextOutput(request!, "call_1");

        Assert.Equal("tool output", output);
    }

    [Fact]
    public void ToolOutputReader_FindsFirstOutputCallId()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
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

        var callId = ToolOutputReader.FindFirstOutputCallId(request!);

        Assert.Equal("call_1", callId);
    }

    [Fact]
    public void CodexToolReader_ReadsTopLevelFunctionTools()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [],
              "tools": [
                {
                  "type": "function",
                  "name": "shell_command",
                  "description": "Run a command.",
                  "parameters": {
                    "type": "object",
                    "properties": {
                      "command": { "type": "string" }
                    }
                  }
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var tools = CodexToolReader.ReadToolDeclarations(request!);

        var tool = Assert.Single(tools);
        Assert.Equal("shell_command", tool.CopilotName);
        Assert.Equal("shell_command", tool.CodexName);
        Assert.Null(tool.CodexNamespace);
        Assert.Equal("Run a command.", tool.Description);
        Assert.Equal("object", tool.Parameters.GetProperty("type").GetString());
    }

    [Fact]
    public void CodexToolReader_FlattensNamespaceFunctionTools()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [],
              "tools": [
                {
                  "type": "namespace",
                  "name": "mcp__demo__",
                  "description": "Demo tools.",
                  "tools": [
                    {
                      "type": "function",
                      "name": "lookup_order",
                      "description": "Look up an order.",
                      "parameters": {
                        "type": "object",
                        "properties": {
                          "order_id": { "type": "string" }
                        }
                      }
                    }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var tools = CodexToolReader.ReadToolDeclarations(request!);

        var tool = Assert.Single(tools);
        Assert.Equal("mcp__demo__lookup_order", tool.CopilotName);
        Assert.Equal("lookup_order", tool.CodexName);
        Assert.Equal("mcp__demo__", tool.CodexNamespace);
        Assert.Contains("Demo tools.", tool.Description);
        Assert.Contains("Look up an order.", tool.Description);
    }

    private sealed class FakeBridge(params BridgeOutput[] outputs) : IInferenceBridge
    {
        public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
            ResponsesApiRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var output in outputs)
            {
                await Task.Yield();
                yield return output;
            }
        }
    }

    private static Func<JsonElement, bool> EventType(string eventType) =>
        evt => evt.GetProperty("type").GetString() == eventType;

    private static int EventIndex(IReadOnlyList<JsonElement> events, JsonElement target)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (JsonElement.DeepEquals(events[index], target))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Could not find target event.");
    }

    private static IReadOnlyList<JsonElement> ReadSseDataEvents(string body)
    {
        var events = new List<JsonElement>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line["data: ".Length..]);
            events.Add(document.RootElement.Clone());
        }

        return events;
    }
}
