using TwinCheck.Agent.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
});

var agentConfig = builder.Configuration.GetSection("Agent").Get<AgentConfig>() ?? new AgentConfig();
builder.Services.AddSingleton(agentConfig);
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<ScanProcessor>();
builder.Services.AddSingleton<HealthService>();

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["https://twin-checkn-2b61c265fe70.herokuapp.com"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("X-Api-Key", "Content-Type")
            .AllowCredentials();
    });
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method)
        && context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
    {
        context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    await next();
});

app.UseCors();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" || HttpMethods.IsOptions(context.Request.Method))
    {
        await next();
        return;
    }

    var configuredKey = agentConfig.ApiKey;
    var suppliedKey = context.Request.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrWhiteSpace(configuredKey)
        || string.IsNullOrWhiteSpace(suppliedKey)
        || !string.Equals(configuredKey, suppliedKey, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "Missing or invalid X-Api-Key." });
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Ok(new
{
    ok = true,
    service = "TwinCheck Scan Agent",
    endpoints = new[]
    {
        "/api/scan/health",
        "/api/scan/config",
        "/api/scan/process"
    }
}));

app.MapGet("/api/scan/health", (HttpContext context, HealthService healthService) =>
{
    var agentUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    return Results.Ok(healthService.GetHealth(agentUrl));
});

app.MapGet("/api/scan/config", (AgentConfig config) => Results.Ok(new
{
    config.AgentName,
    config.Version,
    config.ActiveProfileId,
    Profiles = config.Profiles.Select(profile => new
    {
        profile.Id,
        profile.Name,
        profile.SourceDir,
        profile.DestinationDir,
        profile.NamingPattern,
        profile.Options
    })
}));

app.MapPost("/api/scan/process", (ProcessScanRequest request, ScanProcessor processor) =>
{
    try
    {
        return Results.Ok(processor.Process(request));
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.Run();
