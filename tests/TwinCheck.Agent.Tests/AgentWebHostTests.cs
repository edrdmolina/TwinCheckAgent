using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TwinCheck.Agent.Core;
using TwinCheck.Agent.Hosting;

namespace TwinCheck.Agent.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentEnvironmentCollection
{
    public const string Name = "Agent environment variables";
}

/// <summary>The in-process hosting path the Windows tray app uses.</summary>
[Collection(AgentEnvironmentCollection.Name)]
public sealed class AgentWebHostTests : IDisposable
{
    private static readonly string[] Variables =
    [
        LocalAgentConfigStore.ConfigPathEnvironmentVariable,
        LocalAgentLogger.LogDirectoryEnvironmentVariable,
        ScanOperationStore.StateDirectoryEnvironmentVariable,
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), "twincheck-host-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> original = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public AgentWebHostTests()
    {
        Environment.SetEnvironmentVariable(LocalAgentConfigStore.ConfigPathEnvironmentVariable, Path.Combine(root, "agent-config.json"));
        Environment.SetEnvironmentVariable(LocalAgentLogger.LogDirectoryEnvironmentVariable, Path.Combine(root, "logs"));
        Environment.SetEnvironmentVariable(ScanOperationStore.StateDirectoryEnvironmentVariable, root);
    }

    [Fact]
    public async Task ServesHttpsWithSuppliedCertificateAndRequiresApiKey()
    {
        LocalAgentConfigStore.CreateIfMissing();
        var apiKey = LocalAgentConfigStore.LoadOrDefault(new AgentConfig()).ApiKey;
        using var certificate = CreateLocalhostCertificate();
        var port = FreePort();

        await using var app = AgentWebHost.Create([], new AgentHostOptions
        {
            Certificate = certificate,
            Port = port,
            ContentRootPath = root,
        });
        await app.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, served, _, _) => served?.Thumbprint == certificate.Thumbprint
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{port}") };

        using var index = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        using var unauthorized = await client.GetAsync("/api/scan/health");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scan/health");
        request.Headers.Add("X-Api-Key", apiKey);
        using var health = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal(AgentVersion.Current, body.RootElement.GetProperty("version").GetString());

        await app.StopAsync();
    }

    public void Dispose()
    {
        foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static X509Certificate2 CreateLocalhostCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
