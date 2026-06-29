using System.Text;
using System.Text.Json;

namespace CodexCopilotBridge.Protocol;

public static class CodexPromptRenderer
{
    public static string Render(ResponsesApiRequest request)
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
            AppendInputItem(builder, item);
        }

        if (request.Tools is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine($"Codex supplied {request.Tools.Count} tool declaration(s). Tool bridging is not enabled in this build.");
        }

        return builder.ToString().Trim();
    }

    private static void AppendInputItem(StringBuilder builder, JsonElement item)
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
                AppendToolOutput(builder, item, type);
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

    private static void AppendToolOutput(StringBuilder builder, JsonElement item, string type)
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
            builder.AppendLine(OutputToText(output));
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
            if (item.TryGetProperty("text", out var text))
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
            else
            {
                parts.Add(JsonSerializer.Serialize(item, ProtocolJson.SerializerOptions));
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
