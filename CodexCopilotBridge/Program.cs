using CodexCopilotBridge;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.AddSingleton<EchoInferenceBridge>();
builder.Services.AddSingleton<CopilotSdkInferenceBridge>();
builder.Services.AddSingleton<ToolProbeInferenceBridge>();
builder.Services.AddSingleton<CopilotToolProbeInferenceBridge>();
builder.Services.AddSingleton<IInferenceBridge>(services =>
{
    var options = services.GetRequiredService<IOptions<BridgeOptions>>().Value;
    return options.Mode switch
    {
        BridgeMode.CopilotSdk => services.GetRequiredService<CopilotSdkInferenceBridge>(),
        BridgeMode.ToolProbe => services.GetRequiredService<ToolProbeInferenceBridge>(),
        BridgeMode.CopilotToolProbe => services.GetRequiredService<CopilotToolProbeInferenceBridge>(),
        _ => services.GetRequiredService<EchoInferenceBridge>()
    };
});

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/healthz"));

app.MapGet("/healthz", (IOptions<BridgeOptions> options) => Results.Json(
    new
    {
        Status = "ok",
        Bridge = "codex-copilot-bridge",
        Mode = options.Value.Mode.ToString(),
        DefaultModel = options.Value.DefaultModel
    },
    ProtocolJson.SerializerOptions));

app.MapGet("/v1/models", (IOptions<BridgeOptions> options) =>
    Results.Json(CodexModelCatalog.CreateDefault(options.Value.DefaultModel), ProtocolJson.SerializerOptions));

app.MapPost("/v1/responses", async (
    HttpContext context,
    ResponsesApiRequest request,
    IInferenceBridge bridge,
    CancellationToken cancellationToken) =>
{
    await SseResponseWriter.WriteResponseAsync(context, request, bridge, cancellationToken);
});

app.Run();
