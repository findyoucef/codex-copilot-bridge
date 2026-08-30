using CodexCopilotBridge;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = ProtocolJson.SerializerOptions.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = ProtocolJson.SerializerOptions.DefaultIgnoreCondition;
});
builder.Services.AddSingleton<ConversationSessionStore>();
builder.Services.AddSingleton<PendingToolCallStore>();
builder.Services.AddHostedService<PendingToolCallCleanupService>();
builder.Services.AddSingleton<EchoInferenceBridge>();
builder.Services.AddSingleton<CopilotSdkInferenceBridge>();
builder.Services.AddSingleton<CopilotToolBridgeInferenceBridge>();
builder.Services.AddSingleton<ToolProbeInferenceBridge>();
builder.Services.AddSingleton<CopilotToolProbeInferenceBridge>();
builder.Services.AddSingleton<IInferenceBridge>(services =>
{
    var options = services.GetRequiredService<IOptions<BridgeOptions>>().Value;
    return options.Mode switch
    {
        BridgeMode.CopilotSdk => services.GetRequiredService<CopilotSdkInferenceBridge>(),
        BridgeMode.CopilotBridge => services.GetRequiredService<CopilotToolBridgeInferenceBridge>(),
        BridgeMode.ToolProbe => services.GetRequiredService<ToolProbeInferenceBridge>(),
        BridgeMode.CopilotToolProbe => services.GetRequiredService<CopilotToolProbeInferenceBridge>(),
        _ => services.GetRequiredService<EchoInferenceBridge>()
    };
});

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/healthz"));

app.MapGet("/healthz", (
    IOptions<BridgeOptions> options,
    PendingToolCallStore pendingToolCalls,
    ConversationSessionStore conversationSessions) =>
{
    var bridgeOptions = options.Value;
    var pendingSnapshot = pendingToolCalls.Snapshot();
    var conversationSnapshot = conversationSessions.Snapshot();
    return Results.Json(
        new
        {
            Status = "ok",
            Bridge = "codex-copilot-bridge",
            Mode = bridgeOptions.Mode.ToString(),
            DefaultModel = bridgeOptions.DefaultModel,
            PendingToolCalls = pendingSnapshot.PendingToolCallCount,
            ActiveCopilotSessions = pendingSnapshot.ActiveSessionCount,
            ReusableCopilotSessions = conversationSnapshot.ActiveSessionCount,
            OldestPendingToolCallAgeSeconds = pendingSnapshot.OldestPendingCallAgeSeconds ?? 0,
            OldestReusableSessionAgeSeconds = conversationSnapshot.OldestSessionAgeSeconds ?? 0,
            OldestReusableIdleSessionAgeSeconds = conversationSnapshot.OldestIdleSessionAgeSeconds ?? 0,
            PendingToolCallTtlSeconds = Positive(bridgeOptions.PendingToolCallTtl, TimeSpan.FromHours(1)).TotalSeconds,
            ConversationSessionTtlSeconds = Positive(bridgeOptions.ConversationSessionTtl, BridgeOptions.DefaultConversationSessionTtl).TotalSeconds,
            RequestTimeoutSeconds = Positive(bridgeOptions.RequestTimeout, TimeSpan.FromHours(1)).TotalSeconds,
            ToolRequestBatchQuietPeriodMilliseconds = Positive(bridgeOptions.ToolRequestBatchQuietPeriod, TimeSpan.FromMilliseconds(500)).TotalMilliseconds,
            ResponseOutputChannelCapacity = PositiveInt(bridgeOptions.ResponseOutputChannelCapacity, 512),
            CodexContextWindow = PositiveInt(bridgeOptions.CodexContextWindow, BridgeOptions.DefaultCodexContextWindow),
            CodexTruncationLimitBytes = PositiveInt(bridgeOptions.CodexTruncationLimitBytes, BridgeOptions.DefaultCodexTruncationLimitBytes),
            MaxToolOutputChars = PositiveInt(bridgeOptions.MaxToolOutputChars, BridgeOptions.DefaultMaxToolOutputChars),
            CompactToolSchemas = bridgeOptions.CompactToolSchemas,
            EnableConversationSessionReuse = bridgeOptions.EnableConversationSessionReuse,
            ExcludedToolNames = EffectiveExcludedToolNames(bridgeOptions.ExcludedToolNames),
            ExcludedToolNamePrefixes = bridgeOptions.ExcludedToolNamePrefixes
        },
        ProtocolJson.SerializerOptions);
});

app.MapGet("/v1/models", async (
    CopilotToolBridgeInferenceBridge bridge,
    CancellationToken cancellationToken) =>
    Results.Json(
        await bridge.ListModelsAsync(cancellationToken),
        ProtocolJson.SerializerOptions));

app.MapPost("/v1/responses", async (
    HttpContext context,
    ResponsesApiRequest request,
    IInferenceBridge bridge,
    CancellationToken cancellationToken) =>
{
    if (request.Stream)
    {
        await SseResponseWriter.WriteResponseAsync(context, request, bridge, cancellationToken);
        return;
    }

    await JsonResponseWriter.WriteResponseAsync(context, request, bridge, cancellationToken);
});

app.Run();

static TimeSpan Positive(TimeSpan value, TimeSpan fallback) =>
    value > TimeSpan.Zero ? value : fallback;

static int PositiveInt(int value, int fallback) =>
    value > 0 ? value : fallback;

static IReadOnlyList<string> EffectiveExcludedToolNames(string[]? excludedToolNames) =>
    excludedToolNames is { Length: > 0 }
        ? excludedToolNames
        : BridgeOptions.DefaultExcludedToolNames;
