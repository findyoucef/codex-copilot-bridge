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
        Assert.False(root.TryGetProperty("usage", out _));
    }

    [Fact]
    public async Task JsonResponseWriter_AggregatesProviderUsage()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var request = new ResponsesApiRequest { Model = "gpt-5.5", Stream = false };
        var bridge = new FakeBridge(
            new UsageOutput(100, 20, 5, 10, 3),
            new UsageOutput(50, 8, 2, 7, 1),
            new FinalTextOutput("done"));

        await JsonResponseWriter.WriteResponseAsync(context, request, bridge, CancellationToken.None);

        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        var usage = document.RootElement.GetProperty("usage");

        Assert.Equal(150, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(28, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64());
        Assert.Equal(7, usage.GetProperty("input_tokens_details").GetProperty("cache_write_tokens").GetInt64());
        Assert.Equal(17, usage.GetProperty("output_tokens").GetInt64());
        Assert.Equal(4, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt64());
        Assert.Equal(167, usage.GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public async Task SseResponseWriter_IncludesUsageOnCompletedEvent()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var request = new ResponsesApiRequest { Model = "gpt-5.5", Stream = true };
        var bridge = new FakeBridge(
            new TextDeltaOutput("done"),
            new UsageOutput(125, 25, 4, 9, 2));

        await SseResponseWriter.WriteResponseAsync(context, request, bridge, CancellationToken.None);

        body.Position = 0;
        using var reader = new StreamReader(body);
        var events = ReadSseDataEvents(await reader.ReadToEndAsync());
        var usage = events
            .Single(EventType("response.completed"))
            .GetProperty("response")
            .GetProperty("usage");

        Assert.Equal(125, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(25, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64());
        Assert.Equal(4, usage.GetProperty("input_tokens_details").GetProperty("cache_write_tokens").GetInt64());
        Assert.Equal(9, usage.GetProperty("output_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt64());
        Assert.Equal(134, usage.GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public void UsageOutput_RequiresProviderInputAndOutputCounts()
    {
        Assert.Null(UsageOutput.FromProvider(null, 1, 2, 3, 4));
        Assert.Null(UsageOutput.FromProvider(1, 2, 3, null, 4));

        var usage = UsageOutput.FromProvider(10, -1, null, 5, -2);

        Assert.NotNull(usage);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
        Assert.Equal(0, usage.CacheWriteInputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(0, usage.ReasoningOutputTokens);
    }

    [Fact]
    public void ResponsesApiRequest_DeserializesCodexConversationMetadata()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.6-sol",
              "prompt_cache_key": "cache-1",
              "client_metadata": {
                "thread_id": "thread-1",
                "session_id": "session-1",
                "x-codex-window-id": "window-1"
              }
            }
            """,
            ProtocolJson.SerializerOptions);

        Assert.NotNull(request);
        Assert.Equal("cache-1", request.PromptCacheKey);
        Assert.Equal("thread-1", request.ClientMetadata!["thread_id"]);
        Assert.Equal("session-1", request.ClientMetadata["session_id"]);
        Assert.Equal("window-1", request.ClientMetadata["x-codex-window-id"]);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("max")]
    public void ResponsesApiRequest_ReadsDynamicReasoningEffort(string effort)
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            $$"""
            {
              "reasoning": {
                "effort": "{{effort}}"
              }
            }
            """,
            ProtocolJson.SerializerOptions);

        Assert.NotNull(request);
        Assert.Equal(effort, request.GetReasoningEffort());
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
        Assert.Equal(10_000_000, model.GetProperty("truncation_policy").GetProperty("limit").GetInt32());
        Assert.Equal(272_000, model.GetProperty("context_window").GetInt32());
    }

    [Fact]
    public void ModelCatalog_UsesConfiguredCostLimits()
    {
        var json = JsonSerializer.Serialize(
            CodexModelCatalog.CreateDefault("gpt-5", contextWindow: 32_000, truncationLimitBytes: 120_000),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var model = document.RootElement.GetProperty("models")[0];

        Assert.Equal(120_000, model.GetProperty("truncation_policy").GetProperty("limit").GetInt32());
        Assert.Equal(32_000, model.GetProperty("context_window").GetInt32());
    }

    [Fact]
    public void ModelCatalog_DefaultDoesNotGuessModelReasoningLevels()
    {
        var json = JsonSerializer.Serialize(
            CodexModelCatalog.CreateDefault("gpt-5.6-sol"),
            ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var reasoningLevels = document.RootElement
            .GetProperty("models")[0]
            .GetProperty("supported_reasoning_levels")
            .EnumerateArray()
            .Select(level => level.GetProperty("effort").GetString()!)
            .ToArray();

        Assert.Equal(["medium"], reasoningLevels);
    }

    [Fact]
    public void ModelCatalog_IncludesEveryDiscoveredModel()
    {
        var catalog = CodexModelCatalog.Create(
            [
                new CodexModelDescriptor("model-a", "Model A", 100_000, ["low", "high"], "high"),
                new CodexModelDescriptor("model-b", "Model B", 200_000, ["medium"], "medium")
            ]);
        var json = JsonSerializer.Serialize(catalog, ProtocolJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var models = document.RootElement.GetProperty("models");

        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal("model-a", models[0].GetProperty("slug").GetString());
        Assert.Equal("Model A", models[0].GetProperty("display_name").GetString());
        Assert.Equal(100_000, models[0].GetProperty("context_window").GetInt32());
        Assert.Equal("high", models[0].GetProperty("default_reasoning_level").GetString());
        Assert.Equal("model-b", models[1].GetProperty("slug").GetString());
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
    public void PromptRenderer_RenderDelta_SkipsAssistantAndToolHistory()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [
                {
                  "type": "message",
                  "role": "user",
                  "content": [
                    { "type": "input_text", "text": "first user turn" }
                  ]
                },
                {
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    { "type": "output_text", "text": "previous assistant answer" }
                  ]
                },
                {
                  "type": "function_call",
                  "call_id": "call_1",
                  "name": "exec_command",
                  "arguments": "{\"cmd\":\"pwd\"}"
                },
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "tool output"
                },
                {
                  "type": "message",
                  "role": "user",
                  "content": [
                    { "type": "input_text", "text": "second user turn" }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var prompt = CodexPromptRenderer.RenderDelta(request!, startIndex: 1);

        Assert.Contains("Conversation update:", prompt);
        Assert.Contains("user: second user turn", prompt);
        Assert.DoesNotContain("previous assistant answer", prompt);
        Assert.DoesNotContain("tool output", prompt);
        Assert.DoesNotContain("function_call", prompt);
    }

    [Fact]
    public void PromptRenderer_CountCommonInputPrefix_UsesExactInputItems()
    {
        var firstRequest = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [
                {
                  "type": "message",
                  "role": "user",
                  "content": [
                    { "type": "input_text", "text": "first user turn" }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);
        var secondRequest = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [
                {
                  "type": "message",
                  "role": "user",
                  "content": [
                    { "type": "input_text", "text": "first user turn" }
                  ]
                },
                {
                  "type": "message",
                  "role": "user",
                  "content": [
                    { "type": "input_text", "text": "second user turn" }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var fingerprints = CodexPromptRenderer.CreateInputFingerprints(firstRequest!);
        var commonPrefix = CodexPromptRenderer.CountCommonInputPrefix(fingerprints, secondRequest!);

        Assert.Equal(1, commonPrefix);
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
    public void ToolOutputReader_TruncatesLargeTextOutput()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": "abcdef"
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var output = ToolOutputReader.FindTextOutput(request!, "call_1", maxToolOutputChars: 3);

        Assert.NotNull(output);
        Assert.StartsWith("abc", output);
        Assert.Contains("tool output truncated", output);
        Assert.Contains("original length 6 chars", output);
        Assert.DoesNotContain("def", output);
    }

    [Fact]
    public void ToolOutputReader_OmitsNonTextToolOutputPayloads()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.5",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": [
                    {
                      "type": "input_image",
                      "image_url": "data:image/png;base64,expensive-image-data"
                    },
                    {
                      "type": "output_text",
                      "text": "visible text"
                    }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var output = ToolOutputReader.FindTextOutput(request!, "call_1");

        Assert.NotNull(output);
        Assert.Contains("non-text tool output omitted", output);
        Assert.Contains("visible text", output);
        Assert.DoesNotContain("expensive-image-data", output);
        Assert.DoesNotContain("image_url", output);
    }

    [Fact]
    public void PromptRenderer_OmitsNonTextToolOutputPayloads()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5.5",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "output": [
                    {
                      "type": "input_image",
                      "image_url": "data:image/png;base64,expensive-image-data"
                    },
                    {
                      "type": "output_text",
                      "text": "visible text"
                    }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var prompt = CodexPromptRenderer.Render(request!);

        Assert.Contains("non-text tool output omitted", prompt);
        Assert.Contains("visible text", prompt);
        Assert.DoesNotContain("expensive-image-data", prompt);
        Assert.DoesNotContain("image_url", prompt);
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
    public void CodexToolReader_ExcludesToolsAndCompactsParameterSchemas()
    {
        var request = JsonSerializer.Deserialize<ResponsesApiRequest>(
            """
            {
              "model": "gpt-5",
              "input": [],
              "tools": [
                {
                  "type": "function",
                  "name": "view_image",
                  "description": "View an image.",
                  "parameters": {
                    "type": "object",
                    "properties": {}
                  }
                },
                {
                  "type": "function",
                  "name": "exec_command",
                  "description": "Run a command.",
                  "parameters": {
                    "type": "object",
                    "description": "Root schema description.",
                    "properties": {
                      "command": {
                        "type": "string",
                        "title": "Command",
                        "description": "Command description."
                      },
                      "description": {
                        "type": "string",
                        "description": "A real argument named description."
                      }
                    }
                  }
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var tools = CodexToolReader.ReadToolDeclarations(
            request!,
            new CodexToolReaderOptions
            {
                CompactSchemas = true,
                ExcludedToolNames = new HashSet<string>(["view_image"], StringComparer.Ordinal)
            });

        var tool = Assert.Single(tools);
        Assert.Equal("exec_command", tool.CopilotName);
        Assert.False(tool.Parameters.TryGetProperty("description", out _));

        var properties = tool.Parameters.GetProperty("properties");
        Assert.True(properties.TryGetProperty("description", out _));

        var command = properties.GetProperty("command");
        Assert.False(command.TryGetProperty("title", out _));
        Assert.False(command.TryGetProperty("description", out _));
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
                    },
                    {
                      "type": "function",
                      "name": "list_orders",
                      "parameters": {
                        "type": "object",
                        "properties": {}
                      }
                    }
                  ]
                }
              ]
            }
            """,
            ProtocolJson.SerializerOptions);

        var tools = CodexToolReader.ReadToolDeclarations(request!);

        Assert.Collection(
            tools,
            tool =>
            {
                Assert.Equal("mcp__demo__lookup_order", tool.CopilotName);
                Assert.Equal("lookup_order", tool.CodexName);
                Assert.Equal("mcp__demo__", tool.CodexNamespace);
                Assert.Equal("Look up an order.", tool.Description);
            },
            tool =>
            {
                Assert.Equal("mcp__demo__list_orders", tool.CopilotName);
                Assert.Equal("Demo tools.", tool.Description);
            });
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
