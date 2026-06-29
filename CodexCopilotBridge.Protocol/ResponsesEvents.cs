namespace CodexCopilotBridge.Protocol;

public static class ResponsesEvents
{
    public static object Created(string responseId, string model) =>
        new
        {
            Type = "response.created",
            Response = new
            {
                Id = responseId,
                Model = model
            }
        };

    public static object OutputTextDelta(string delta) =>
        new
        {
            Type = "response.output_text.delta",
            Delta = delta
        };

    public static object OutputMessageAdded(string responseId) =>
        new
        {
            Type = "response.output_item.added",
            Item = new
            {
                Id = $"msg_{responseId}",
                Type = "message",
                Role = "assistant",
                Content = new[]
                {
                    new
                    {
                        Type = "output_text",
                        Text = string.Empty
                    }
                }
            }
        };

    public static object OutputMessageDone(string responseId, string text) =>
        new
        {
            Type = "response.output_item.done",
            Item = new
            {
                Id = $"msg_{responseId}",
                Type = "message",
                Role = "assistant",
                Content = new[]
                {
                    new
                    {
                        Type = "output_text",
                        Text = text
                    }
                }
            }
        };

    public static object FunctionCallDone(string callId, string name, string arguments, string? @namespace = null)
    {
        var item = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["call_id"] = callId,
            ["name"] = name,
            ["arguments"] = arguments
        };

        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            item["namespace"] = @namespace;
        }

        return new
        {
            Type = "response.output_item.done",
            Item = item
        };
    }

    public static object Completed(string responseId, bool endTurn = true) =>
        new
        {
            Type = "response.completed",
            Response = new
            {
                Id = responseId,
                EndTurn = endTurn
            }
        };

    public static object Failed(string responseId, string message) =>
        new
        {
            Type = "response.failed",
            Response = new
            {
                Id = responseId,
                Error = new
                {
                    Type = "bridge_error",
                    Code = "bridge_error",
                    Message = message
                }
            }
        };
}
