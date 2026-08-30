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

    public static string? FindTextOutput(ResponsesApiRequest request, string callId, int maxToolOutputChars = 0)
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

            return ToolOutputText.Render(output, maxToolOutputChars);
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
}
