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
        var textItemCompleted = false;
        var emittedText = false;
        var emittedToolCall = false;
        UsageOutput? usage = null;
        var nextOutputIndex = 0;
        int? textOutputIndex = null;

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
                        var deltaOutputIndex = await EnsureTextItemStartedAsync();

                        text.Append(delta.Delta);
                        emittedText = true;
                        await WriteEventAsync(
                            context,
                            "response.output_text.delta",
                            ResponsesEvents.OutputTextDelta(responseId, delta.Delta, deltaOutputIndex),
                            cancellationToken);
                        break;
                    case FinalTextOutput final:
                        if (!string.IsNullOrEmpty(final.Text) || text.Length == 0)
                        {
                            finalText = final.Text;
                        }

                        emittedText = emittedText || !string.IsNullOrEmpty(final.Text);
                        break;
                    case UsageOutput currentUsage:
                        usage = usage is null ? currentUsage : usage.Add(currentUsage);
                        break;
                    case FunctionCallRequestOutput functionCall:
                        await CompleteTextItemIfNeededAsync();
                        emittedToolCall = true;
                        var functionCallOutputIndex = nextOutputIndex++;
                        await WriteEventAsync(
                            context,
                            "response.output_item.done",
                            ResponsesEvents.FunctionCallDone(
                                functionCall.CallId,
                                functionCall.Name,
                                functionCall.Arguments,
                                functionCall.Namespace,
                                functionCallOutputIndex),
                            cancellationToken);
                        break;
                }
            }

            await CompleteTextItemIfNeededAsync();

            await WriteEventAsync(
                context,
                "response.completed",
                ResponsesEvents.Completed(responseId, endTurn: !emittedToolCall, usage: usage?.ToResponseUsage()),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteEventAsync(context, "response.failed", ResponsesEvents.Failed(responseId, ex.Message), CancellationToken.None);
        }

        async Task<int> EnsureTextItemStartedAsync()
        {
            textOutputIndex ??= nextOutputIndex++;
            var outputIndex = textOutputIndex.Value;

            if (!textItemStarted)
            {
                await WriteEventAsync(context, "response.output_item.added", ResponsesEvents.OutputMessageAdded(responseId, outputIndex), cancellationToken);
                textItemStarted = true;
            }

            if (!contentPartStarted)
            {
                await WriteEventAsync(context, "response.content_part.added", ResponsesEvents.ContentPartAdded(responseId, outputIndex), cancellationToken);
                contentPartStarted = true;
            }

            return outputIndex;
        }

        async Task CompleteTextItemIfNeededAsync()
        {
            if (!emittedText || textItemCompleted)
            {
                return;
            }

            var completedText = finalText ?? text.ToString();
            var completedTextOutputIndex = await EnsureTextItemStartedAsync();

            await WriteEventAsync(context, "response.output_text.done", ResponsesEvents.OutputTextDone(responseId, completedText, completedTextOutputIndex), cancellationToken);
            await WriteEventAsync(context, "response.content_part.done", ResponsesEvents.ContentPartDone(responseId, completedText, completedTextOutputIndex), cancellationToken);
            await WriteEventAsync(context, "response.output_item.done", ResponsesEvents.OutputMessageDone(responseId, completedText, completedTextOutputIndex), cancellationToken);
            textItemCompleted = true;
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
