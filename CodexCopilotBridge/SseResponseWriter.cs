using System.Text;
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
        var text = new StringBuilder();
        string? finalText = null;
        var textItemStarted = false;
        var contentPartStarted = false;
        var emittedText = false;
        var emittedToolCall = false;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["openai-model"] = request.Model;

        await WriteEventAsync(context, "response.created", ResponsesEvents.Created(responseId, request.Model), cancellationToken);

        try
        {
            await foreach (var output in bridge.CreateResponseAsync(request, cancellationToken))
            {
                switch (output)
                {
                    case TextDeltaOutput delta when !string.IsNullOrEmpty(delta.Delta):
                        if (!textItemStarted)
                        {
                            await WriteEventAsync(context, "response.output_item.added", ResponsesEvents.OutputMessageAdded(responseId), cancellationToken);
                            textItemStarted = true;
                        }

                        if (!contentPartStarted)
                        {
                            await WriteEventAsync(context, "response.content_part.added", ResponsesEvents.ContentPartAdded(responseId), cancellationToken);
                            contentPartStarted = true;
                        }

                        text.Append(delta.Delta);
                        emittedText = true;
                        await WriteEventAsync(context, "response.output_text.delta", ResponsesEvents.OutputTextDelta(responseId, delta.Delta), cancellationToken);
                        break;
                    case FinalTextOutput final:
                        if (!string.IsNullOrEmpty(final.Text) || text.Length == 0)
                        {
                            finalText = final.Text;
                        }

                        emittedText = emittedText || !string.IsNullOrEmpty(final.Text);
                        break;
                    case FunctionCallRequestOutput functionCall:
                        emittedToolCall = true;
                        await WriteEventAsync(
                            context,
                            "response.output_item.done",
                            ResponsesEvents.FunctionCallDone(
                                functionCall.CallId,
                                functionCall.Name,
                                functionCall.Arguments,
                                functionCall.Namespace),
                            cancellationToken);
                        break;
                }
            }

            if (emittedText)
            {
                var completedText = finalText ?? text.ToString();

                if (!textItemStarted)
                {
                    await WriteEventAsync(context, "response.output_item.added", ResponsesEvents.OutputMessageAdded(responseId), cancellationToken);
                    textItemStarted = true;
                }

                if (!contentPartStarted)
                {
                    await WriteEventAsync(context, "response.content_part.added", ResponsesEvents.ContentPartAdded(responseId), cancellationToken);
                }

                await WriteEventAsync(context, "response.output_text.done", ResponsesEvents.OutputTextDone(responseId, completedText), cancellationToken);
                await WriteEventAsync(context, "response.content_part.done", ResponsesEvents.ContentPartDone(responseId, completedText), cancellationToken);
                await WriteEventAsync(context, "response.output_item.done", ResponsesEvents.OutputMessageDone(responseId, completedText), cancellationToken);
            }

            await WriteEventAsync(context, "response.completed", ResponsesEvents.Completed(responseId, endTurn: !emittedToolCall), cancellationToken);
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
