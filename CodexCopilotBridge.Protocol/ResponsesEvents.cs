namespace CodexCopilotBridge.Protocol;

public static class ResponsesEvents
{
    private static string MessageId(string responseId) => $"msg_{responseId}";

    public static object Created(string responseId, string model) =>
        new
        {
            Type = "response.created",
            Response = new
            {
                Id = responseId,
                Model = model,
                Status = "in_progress"
            }
        };

    public static object ContentPartAdded(string responseId, int outputIndex = 0, int contentIndex = 0) =>
        new
        {
            Type = "response.content_part.added",
            ItemId = MessageId(responseId),
            OutputIndex = outputIndex,
            ContentIndex = contentIndex,
            Part = new
            {
                Type = "output_text",
                Text = string.Empty
            }
        };

    public static object OutputTextDelta(string responseId, string delta, int outputIndex = 0, int contentIndex = 0) =>
        new
        {
            Type = "response.output_text.delta",
            ItemId = MessageId(responseId),
            OutputIndex = outputIndex,
            ContentIndex = contentIndex,
            Delta = delta
        };

    public static object OutputTextDone(string responseId, string text, int outputIndex = 0, int contentIndex = 0) =>
        new
        {
            Type = "response.output_text.done",
            ItemId = MessageId(responseId),
            OutputIndex = outputIndex,
            ContentIndex = contentIndex,
            Text = text
        };

    public static object ContentPartDone(string responseId, string text, int outputIndex = 0, int contentIndex = 0) =>
        new
        {
            Type = "response.content_part.done",
            ItemId = MessageId(responseId),
            OutputIndex = outputIndex,
            ContentIndex = contentIndex,
            Part = new
            {
                Type = "output_text",
                Text = text
            }
        };

    public static object OutputMessageAdded(string responseId, int outputIndex = 0) =>
        new
        {
            Type = "response.output_item.added",
            OutputIndex = outputIndex,
            Item = new
            {
                Id = MessageId(responseId),
                Type = "message",
                Status = "in_progress",
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

    public static object OutputMessageDone(string responseId, string text, int outputIndex = 0) =>
        new
        {
            Type = "response.output_item.done",
            OutputIndex = outputIndex,
            Item = new
            {
                Id = MessageId(responseId),
                Type = "message",
                Status = "completed",
                Role = "assistant",
                Content = new[]
                {
                    new
                    {
                        Type = "output_text",
                        Text = text,
                        Annotations = Array.Empty<object>()
                    }
                }
            }
        };

    public static object FunctionCallDone(string callId, string name, string arguments, string? @namespace = null, int outputIndex = 0)
    {
        var item = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["call_id"] = callId,
            ["name"] = name,
            ["arguments"] = arguments,
            ["status"] = "completed"
        };

        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            item["namespace"] = @namespace;
        }

        return new
        {
            Type = "response.output_item.done",
            OutputIndex = outputIndex,
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
                Status = "completed",
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
