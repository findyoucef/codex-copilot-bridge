using System.Text.Json;

namespace CodexCopilotBridge.Protocol;

internal static class ToolOutputText
{
    private const string NonTextOutputPlaceholder = "[non-text tool output omitted]";

    public static string Render(JsonElement output, int maxChars = 0) =>
        Truncate(RenderUnbounded(output), maxChars);

    private static string RenderUnbounded(JsonElement output) =>
        output.ValueKind switch
        {
            JsonValueKind.String => output.GetString() ?? string.Empty,
            JsonValueKind.Array => RenderArray(output),
            JsonValueKind.Object => RenderObject(output),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => output.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => NonTextOutputPlaceholder
        };

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        return $"{text[..maxChars]}{Environment.NewLine}[tool output truncated; original length {text.Length} chars]";
    }

    private static string RenderArray(JsonElement output)
    {
        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.Object
                ? RenderObject(item)
                : RenderUnbounded(item);

            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string RenderObject(JsonElement item)
    {
        if (item.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        return NonTextOutputPlaceholder;
    }
}
