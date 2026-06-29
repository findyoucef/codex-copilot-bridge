using System.Text.Json;
using CodexCopilotBridge.Protocol;

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
}
