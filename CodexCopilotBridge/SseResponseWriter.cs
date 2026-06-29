using System.Text.Json;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;

namespace CodexCopilotBridge;

public static class SseResponseWriter
{
    public static async Task WriteResponseAsync(
        HttpContext context,
        ResponsesApiRequest request,
        IInferenceBridge bridge,
        CancellationToken cancellationToken)
    {
        var responseId = $"resp_{Guid.NewGuid():N}";
        var finalText = string.Empty;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["openai-model"] = request.Model;

        await WriteEventAsync(context, "response.created", ResponsesEvents.Created(responseId, request.Model), cancellationToken);
        await WriteEventAsync(context, "response.output_item.added", ResponsesEvents.OutputMessageAdded(responseId), cancellationToken);

        try
        {
            await foreach (var output in bridge.CreateResponseAsync(request, cancellationToken))
            {
                switch (output)
                {
                    case TextDeltaOutput delta when !string.IsNullOrEmpty(delta.Delta):
                        finalText += delta.Delta;
                        await WriteEventAsync(context, "response.output_text.delta", ResponsesEvents.OutputTextDelta(delta.Delta), cancellationToken);
                        break;
                    case FinalTextOutput final:
                        finalText = final.Text;
                        break;
                }
            }

            await WriteEventAsync(context, "response.output_item.done", ResponsesEvents.OutputMessageDone(responseId, finalText), cancellationToken);
            await WriteEventAsync(context, "response.completed", ResponsesEvents.Completed(responseId), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteEventAsync(context, "response.failed", ResponsesEvents.Failed(responseId, ex.Message), CancellationToken.None);
        }
    }

    private static async Task WriteEventAsync(HttpContext context, string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, ProtocolJson.SerializerOptions);
        await context.Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
