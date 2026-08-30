using System.Text;
using System.Text.Json;

namespace CodexCopilotBridge.Protocol;

public static class CodexPromptRenderer
{
    public static string Render(
        ResponsesApiRequest request,
        bool includeToolNotice = true,
        int maxToolOutputChars = 0)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            builder.AppendLine("Instructions:");
            builder.AppendLine(request.Instructions.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Conversation:");

        foreach (var item in request.Input)
        {
            AppendInputItem(builder, item, maxToolOutputChars);
        }

        if (includeToolNotice && request.Tools is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine($"Codex supplied {request.Tools.Count} tool declaration(s). Tool bridging is not enabled in this build.");
        }

        return builder.ToString().Trim();
    }

    public static string RenderDelta(
        ResponsesApiRequest request,
        int startIndex,
        int maxToolOutputChars = 0)
    {
        if (startIndex <= 0)
        {
            return Render(request, includeToolNotice: false, maxToolOutputChars);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Conversation update:");
        var appended = false;

        for (var index = startIndex; index < request.Input.Count; index++)
        {
            var item = request.Input[index];
            if (IsAssistantMessage(item) || IsToolItem(item))
            {
                continue;
            }

            AppendInputItem(builder, item, maxToolOutputChars);
            appended = true;
        }

        return appended ? builder.ToString().Trim() : string.Empty;
    }

    public static IReadOnlyList<string> CreateInputFingerprints(ResponsesApiRequest request) =>
        request.Input
            .Select(item => item.GetRawText())
            .ToArray();

    public static int CountCommonInputPrefix(
        IReadOnlyList<string> previousInputFingerprints,
        ResponsesApiRequest request)
    {
        var count = Math.Min(previousInputFingerprints.Count, request.Input.Count);
        for (var index = 0; index < count; index++)
        {
            if (!string.Equals(previousInputFingerprints[index], request.Input[index].GetRawText(), StringComparison.Ordinal))
            {
                return index;
            }
        }

        return count;
    }

    private static void AppendInputItem(StringBuilder builder, JsonElement item, int maxToolOutputChars)
    {
        if (!item.TryGetProperty("type", out var typeElement))
        {
            builder.AppendLine(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
            return;
        }

        var type = typeElement.GetString();
        switch (type)
        {
            case "message":
                AppendMessage(builder, item);
                break;
            case "function_call_output":
            case "custom_tool_call_output":
                AppendToolOutput(builder, item, type, maxToolOutputChars);
                break;
            case "function_call":
            case "custom_tool_call":
                AppendToolCall(builder, item, type);
                break;
            default:
                builder.AppendLine(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
                break;
        }
    }

    private static void AppendMessage(StringBuilder builder, JsonElement item)
    {
        var role = item.TryGetProperty("role", out var roleElement)
            ? roleElement.GetString() ?? "unknown"
            : "unknown";

        builder.Append(role);
        builder.Append(": ");

        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            builder.AppendLine(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
            return;
        }

        var parts = new List<string>();
        foreach (var contentItem in content.EnumerateArray())
        {
            if (contentItem.TryGetProperty("text", out var textElement))
            {
                parts.Add(textElement.GetString() ?? string.Empty);
            }
        }

        builder.AppendLine(string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part))));
    }

    private static void AppendToolOutput(StringBuilder builder, JsonElement item, string type, int maxToolOutputChars)
    {
        var callId = item.TryGetProperty("call_id", out var callIdElement)
            ? callIdElement.GetString()
            : null;

        builder.Append(type);
        if (!string.IsNullOrWhiteSpace(callId))
        {
            builder.Append(" ");
            builder.Append(callId);
        }

        builder.Append(": ");

        if (item.TryGetProperty("output", out var output))
        {
            builder.AppendLine(ToolOutputText.Render(output, maxToolOutputChars));
        }
        else
        {
            builder.AppendLine(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
        }
    }

    private static void AppendToolCall(StringBuilder builder, JsonElement item, string type)
    {
        var name = item.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        builder.Append(type);
        if (!string.IsNullOrWhiteSpace(name))
        {
            builder.Append(" ");
            builder.Append(name);
        }

        builder.Append(": ");
        builder.AppendLine(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
    }

    private static bool IsAssistantMessage(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var typeElement) ||
            typeElement.GetString() != "message" ||
            !item.TryGetProperty("role", out var roleElement))
        {
            return false;
        }

        return roleElement.GetString() == "assistant";
    }

    private static bool IsToolItem(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var typeElement))
        {
            return false;
        }

        return typeElement.GetString() is
            "function_call" or
            "custom_tool_call" or
            "function_call_output" or
            "custom_tool_call_output";
    }
}
