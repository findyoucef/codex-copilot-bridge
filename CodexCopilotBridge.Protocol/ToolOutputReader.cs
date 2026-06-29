using System.Text.Json;

namespace CodexCopilotBridge.Protocol;

public static class ToolOutputReader
{
    public static string? FindFirstOutputCallId(ResponsesApiRequest request)
    {
        foreach (var item in request.Input)
        {
            if (!IsToolOutput(item))
            {
                continue;
            }

            if (item.TryGetProperty("call_id", out var callIdElement))
            {
                return callIdElement.GetString();
            }
        }

        return null;
    }

    public static string? FindTextOutput(ResponsesApiRequest request, string callId)
    {
        foreach (var item in request.Input)
        {
            if (!IsToolOutput(item))
            {
                continue;
            }

            if (!item.TryGetProperty("call_id", out var callIdElement) ||
                callIdElement.GetString() != callId)
            {
                continue;
            }

            if (!item.TryGetProperty("output", out var output))
            {
                return string.Empty;
            }

            return OutputToText(output);
        }

        return null;
    }

    private static bool IsToolOutput(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var typeElement))
        {
            return false;
        }

        return typeElement.GetString() is "function_call_output" or "custom_tool_call_output";
    }

    private static string OutputToText(JsonElement output) =>
        output.ValueKind switch
        {
            JsonValueKind.String => output.GetString() ?? string.Empty,
            JsonValueKind.Array => RenderOutputArray(output),
            _ => JsonSerializer.Serialize(output, ProtocolJson.SerializerOptions)
        };

    private static string RenderOutputArray(JsonElement output)
    {
        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textElement))
            {
                parts.Add(textElement.GetString() ?? string.Empty);
            }
            else
            {
                parts.Add(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
