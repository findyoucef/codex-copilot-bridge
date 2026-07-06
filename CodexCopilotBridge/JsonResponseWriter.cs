using System.Text;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;

namespace CodexCopilotBridge;

public static class JsonResponseWriter
{
    public static async Task WriteResponseAsync(
        HttpContext context,
        ResponsesApiRequest request,
        IInferenceBridge bridge,
        CancellationToken cancellationToken)
    {
        var responseId = $"resp_{Guid.NewGuid():N}";
        var text = new StringBuilder();
        string? finalText = null;
        var output = new List<object>();
        var emittedToolCall = false;

        context.Response.ContentType = "application/json";
        context.Response.Headers["openai-model"] = request.Model;

        try
        {
            await foreach (var bridgeOutput in bridge.CreateResponseAsync(request, cancellationToken))
            {
                switch (bridgeOutput)
                {
                    case TextDeltaOutput delta when !string.IsNullOrEmpty(delta.Delta):
                        text.Append(delta.Delta);
                        break;
                    case FinalTextOutput final:
                        if (!string.IsNullOrEmpty(final.Text) || text.Length == 0)
                        {
                            finalText = final.Text;
                        }
                        break;
                    case FunctionCallRequestOutput functionCall:
                        emittedToolCall = true;
                        output.Add(FunctionCallItem(
                            functionCall.CallId,
                            functionCall.Name,
                            functionCall.Arguments,
                            functionCall.Namespace));
                        break;
                }
            }

            var completedText = finalText ?? text.ToString();
            if (!string.IsNullOrEmpty(completedText))
            {
                output.Insert(0, MessageItem(responseId, completedText));
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(
                CompletedResponse(responseId, request.Model, output, endTurn: !emittedToolCall),
                ProtocolJson.SerializerOptions,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                FailedResponse(responseId, request.Model, ex.Message),
                ProtocolJson.SerializerOptions,
                CancellationToken.None);
        }
    }

    private static object CompletedResponse(string responseId, string model, IReadOnlyList<object> output, bool endTurn) =>
        new
        {
            Id = responseId,
            Object = "response",
            Status = "completed",
            Model = model,
            Output = output,
            EndTurn = endTurn,
            Error = (object?)null
        };

    private static object FailedResponse(string responseId, string model, string message) =>
        new
        {
            Id = responseId,
            Object = "response",
            Status = "failed",
            Model = model,
            Output = Array.Empty<object>(),
            Error = new
            {
                Type = "bridge_error",
                Code = "bridge_error",
                Message = message
            }
        };

    private static object MessageItem(string responseId, string text) =>
        new
        {
            Id = $"msg_{responseId}",
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
        };

    private static object FunctionCallItem(string callId, string name, string arguments, string? @namespace)
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

        return item;
    }
}
