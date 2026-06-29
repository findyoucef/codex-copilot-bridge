using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexCopilotBridge.Protocol;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
