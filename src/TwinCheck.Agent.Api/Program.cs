using TwinCheck.Agent.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
});

var configuredAgentConfig = builder.Configuration.GetSection("Agent").Get<AgentConfig>() ?? new AgentConfig();
builder.Services.AddSingleton(new AgentConfigProvider(configuredAgentConfig));
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<LocalAgentLogger>();
builder.Services.AddSingleton<ScanProcessor>();
builder.Services.AddSingleton<SourceCandidateService>();
builder.Services.AddSingleton<ScanWatchService>();
builder.Services.AddSingleton<RollbackService>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<DiagnosticsService>();

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["https://twin-checkn-2b61c265fe70.herokuapp.com"];
var allowedOriginSet = allowedOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(origin => IsAllowedCorsOrigin(origin, allowedOriginSet))
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("X-Api-Key", "Content-Type");
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
    var logger = context.RequestServices.GetRequiredService<LocalAgentLogger>();
    try
    {
        await next();
        if (!HttpMethods.IsOptions(context.Request.Method))
        {
            logger.Info($"{context.Request.Method} {context.Request.Path} -> {context.Response.StatusCode}");
        }
    }
    catch (Exception exception)
    {
        logger.Error($"{context.Request.Method} {context.Request.Path} failed.", exception);
        throw;
    }
});

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" || HttpMethods.IsOptions(context.Request.Method))
    {
        await next();
        return;
    }

    var configuredKey = context.RequestServices.GetRequiredService<AgentConfigProvider>().Current.ApiKey;
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
        "/api/scan/diagnostics",
        "/api/scan/logs/recent",
        "/api/scan/config",
        "/api/scan/candidates",
        "/api/scan/watch/start",
        "/api/scan/process",
        "/api/scan/manifests",
        "/api/scan/manifest",
        "/api/scan/rollback"
    }
}));

app.MapGet("/api/scan/health", (HttpContext context, HealthService healthService) =>
{
    var agentUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    return Results.Ok(healthService.GetHealth(agentUrl));
});

app.MapGet("/api/scan/diagnostics", (HttpContext context, DiagnosticsService diagnosticsService) =>
{
    var agentUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    return Results.Ok(new { ok = true, diagnostics = diagnosticsService.GetDiagnostics(agentUrl) });
});

app.MapGet("/api/scan/logs/recent", (HttpRequest request, LocalAgentLogger logger) =>
{
    var lineValue = request.Query["lines"].ToString();
    var lines = int.TryParse(lineValue, out var parsed) ? parsed : 200;
    return Results.Ok(new
    {
        ok = true,
        logDirectory = logger.LogDirectory,
        lines = logger.ReadRecentLines(lines)
    });
});

app.MapGet("/api/scan/config", (AgentConfigProvider configProvider) =>
{
    var config = configProvider.Current;
    return Results.Ok(new
    {
        config.AgentName,
        config.Version,
        config.ActiveProfileId,
        Profiles = config.Profiles.Select(profile => new
        {
            profile.Id,
            profile.Name,
            ScannerMode = ScannerModes.NormalizeOrDefault(profile.ScannerMode),
            profile.SourceDir,
            profile.DestinationDir,
            profile.NamingPattern,
            profile.WeeklyDestination,
            profile.SettleStableSeconds,
            profile.SettleTimeoutSeconds,
            profile.SettlePollSeconds,
            profile.Options
        })
    });
});

app.MapGet("/api/scan/candidates", (HttpRequest request, SourceCandidateService candidateService) =>
{
    try
    {
        var profileId = request.Query["profileId"].ToString();
        var root = request.Query["root"].ToString();
        var orderNumber = request.Query["orderNumber"].ToString();
        var rollNumber = request.Query["rollNumber"].ToString();
        var scanKind = request.Query["scanKind"].ToString();
        var rescanNumberValue = request.Query["rescanNumber"].ToString();
        var rescanNumber = int.TryParse(rescanNumberValue, out var parsedRescanNumber) ? parsedRescanNumber : (int?)null;
        var candidates = candidateService.GetCandidates(
            string.IsNullOrWhiteSpace(profileId) ? null : profileId,
            string.IsNullOrWhiteSpace(root) ? null : root,
            string.IsNullOrWhiteSpace(orderNumber) ? null : orderNumber,
            string.IsNullOrWhiteSpace(rollNumber) ? null : rollNumber,
            string.IsNullOrWhiteSpace(scanKind) ? null : scanKind,
            rescanNumber);

        return Results.Ok(new { ok = true, candidates });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapPost("/api/scan/watch/start", (StartScanWatchRequest request, ScanWatchService watchService) =>
{
    try
    {
        var watch = watchService.Start(request);
        app.Services.GetRequiredService<LocalAgentLogger>().Info($"Started scan watch {watch.WatchId} for profile {watch.ProfileId}.");
        return Results.Ok(new { ok = true, watch });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapGet("/api/scan/watch/{watchId}", (string watchId, ScanWatchService watchService) =>
{
    try
    {
        return Results.Ok(new { ok = true, watch = watchService.Get(watchId) });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapPost("/api/scan/watch/{watchId}/cancel", (string watchId, ScanWatchService watchService) =>
{
    try
    {
        var watch = watchService.Cancel(watchId);
        app.Services.GetRequiredService<LocalAgentLogger>().Info($"Cancelled scan watch {watchId}.");
        return Results.Ok(new { ok = true, watch });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapGet("/api/scan/manifests", (AgentConfigProvider configProvider, OperationStore operationStore) =>
{
    try
    {
        return Results.Ok(new { ok = true, manifests = operationStore.ListManifests(configProvider.Current).Take(100) });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapGet("/api/scan/manifest", (HttpRequest request, AgentConfigProvider configProvider, OperationStore operationStore) =>
{
    try
    {
        var manifestPath = request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return Results.BadRequest(new { ok = false, error = "Manifest path is required." });
        }

        var safePath = FileSystemSafety.EnsureInsideAnyRoot(manifestPath, configProvider.Current.AllowedDestinationRoots, "manifest");
        return Results.Ok(new { ok = true, manifest = operationStore.ReadManifest(safePath), manifestPath = safePath });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapPost("/api/scan/rollback", (RollbackScanRequest request, RollbackService rollbackService) =>
{
    try
    {
        var result = rollbackService.Rollback(request);
        app.Services.GetRequiredService<LocalAgentLogger>().Info($"Rollback requested for manifest {request.ManifestPath}. Restored {result.RestoredFileCount} file(s).");
        return Results.Ok(result);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.MapPost("/api/scan/process", (ProcessScanRequest request, ScanProcessor processor) =>
{
    try
    {
        var result = processor.Process(request);
        app.Services.GetRequiredService<LocalAgentLogger>().Info($"Processed scan {request.OrderNumber}-{request.RollNumber} for profile {request.ProfileId}. Images: {result.ImageCount}.");
        return Results.Ok(result);
    }
    catch (MultipleSourceCandidatesException exception)
    {
        return Results.BadRequest(new
        {
            ok = false,
            code = "multiple-source-candidates",
            error = exception.Message,
            sourceDir = exception.SourceDir,
            profileId = request.ProfileId,
        });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { ok = false, error = exception.Message });
    }
});

app.Run();

static bool IsAllowedCorsOrigin(string origin, IReadOnlySet<string> allowedOrigins)
{
    if (allowedOrigins.Contains(origin))
    {
        return true;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
    {
        return false;
    }

    if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase);
}
