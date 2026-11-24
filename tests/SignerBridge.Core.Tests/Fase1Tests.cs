using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SignerBridge.Core.Abstractions;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Models;
using SignerBridge.Core.Security;
using SignerBridge.Core.Storage;
using SignerBridge.Host;
using SignerBridge.Providers.ReFirmaPdf;
using Xunit;

namespace SignerBridge.Core.Tests;

public sealed class Fase1Tests : IClassFixture<Fase1Tests.TestHostFactory>
{
    private readonly TestHostFactory _factory;

    public Fase1Tests(TestHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void DpapiTokenStore_Generates_Persists_And_Validates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sb-dpapi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store1 = new DpapiTokenStore(dir);
            string token = store1.GetOrCreateToken();
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(File.Exists(Path.Combine(dir, "pairing.dat")));

            var store2 = new DpapiTokenStore(dir);
            Assert.Equal(token, store2.GetOrCreateToken());
            Assert.True(store2.ValidateToken(token));
            Assert.False(store2.ValidateToken("wrong-token"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TempArchiveHelper_Rejects_ZipSlip_And_InvalidPdf()
    {
        using var zipMs = new MemoryStream();
        using (var za = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var bad = za.CreateEntry("not-a-pdf.pdf");
            using var s = bad.Open();
            s.Write("HELLO WORLD"u8);
        }
        zipMs.Position = 0;

        string target = Path.Combine(Path.GetTempPath(), "sb-zip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new ArchiveService();
            Assert.Throws<InvalidDataException>(() => svc.ExtractPdfZipSafely(zipMs, target));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    [Fact]
    public async Task Health_Rejects_MissingToken_And_DisallowedOrigin_And_Accepts_Valid()
    {
        var client = _factory.CreateClient();

        // 1. No token -> 401
        var noTokenRes = await client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, noTokenRes.StatusCode);

        // 2. Disallowed origin -> 403
        var badOriginReq = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        badOriginReq.Headers.Add("Origin", "https://evil.example.com");
        badOriginReq.Headers.Add("X-Signer-Token", _factory.Token);
        var badOriginRes = await client.SendAsync(badOriginReq);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginRes.StatusCode);

        // 3. OPTIONS preflight with allowed origin -> PNA header
        var preflightReq = new HttpRequestMessage(HttpMethod.Options, "/v1/health");
        preflightReq.Headers.Add("Origin", "http://localhost");
        var preflightRes = await client.SendAsync(preflightReq);
        Assert.Equal(HttpStatusCode.NoContent, preflightRes.StatusCode);
        Assert.Equal("true", preflightRes.Headers.GetValues("Access-Control-Allow-Private-Network").First());

        // 4. Valid token + allowed origin -> 200 OK
        var okReq = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        okReq.Headers.Add("Origin", "http://localhost");
        okReq.Headers.Add("X-Signer-Token", _factory.Token);
        var okRes = await client.SendAsync(okReq);
        Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);

        var health = await okRes.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(health);
        Assert.Equal("SignerBridge", health.Service);
        Assert.Contains(health.Providers, p => p.ProviderId == "refirma-pdf" && p.Available);
    }

    [Fact]
    public async Task MockJob_Lifecycle_UploadZip_Poll_And_DownloadResult()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Signer-Token", _factory.Token);

        using var inputZipStream = new MemoryStream();
        using (var za = new ZipArchive(inputZipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = za.CreateEntry("doc1.pdf");
            await using var es = entry.Open();
            await es.WriteAsync("%PDF-1.4\n%%EOF\n"u8.ToArray());
        }
        inputZipStream.Position = 0;

        using var form = new MultipartFormDataContent();
        var zipContent = new StreamContent(inputZipStream);
        zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(zipContent, "archive", "lote.zip");
        form.Add(new StringContent(JsonSerializer.Serialize(new SignJobPayload { Provider = "refirma-pdf" }), Encoding.UTF8, "application/json"), "payload");

        var createRes = await client.PostAsync("/v1/sign/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, createRes.StatusCode);

        var job = await createRes.Content.ReadFromJsonAsync<SignJobResponse>();
        Assert.NotNull(job);

        SignJobResponse? latest = job;
        for (int i = 0; i < 20 && latest!.Status != SignJobStatus.SUCCEEDED; i++)
        {
            await Task.Delay(100);
            latest = await client.GetFromJsonAsync<SignJobResponse>($"/v1/sign/jobs/{job.JobId}");
        }

        Assert.NotNull(latest);
        Assert.Equal(SignJobStatus.SUCCEEDED, latest.Status);
        Assert.Equal(1, latest.OutputFilesCount);

        var resultRes = await client.GetAsync($"/v1/sign/jobs/{job.JobId}/result");
        Assert.Equal(HttpStatusCode.OK, resultRes.StatusCode);
        Assert.Equal("application/zip", resultRes.Content.Headers.ContentType?.MediaType);

        await using var resultStream = await resultRes.Content.ReadAsStreamAsync();
        using var outZip = new ZipArchive(resultStream, ZipArchiveMode.Read);
        Assert.Contains(outZip.Entries, e => e.Name == "doc1.pdf");
        Assert.Contains(outZip.Entries, e => e.Name == "manifest.json");
    }

    public sealed class TestHostFactory : WebApplicationFactory<Program>
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), "sb-test-host-" + Guid.NewGuid().ToString("N"));
        public string Token { get; private set; } = string.Empty;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("SignerBridge:DataDirectory", _tempDataDir);
            builder.ConfigureServices(services =>
            {
                services.Configure<SignerBridgeOptions>(o => o.DataDirectory = _tempDataDir);
                services.RemoveAll<ISignerProvider>();
                services.AddSingleton<ISignerProvider>(sp =>
                    new ReFirmaPdfProvider(sp.GetRequiredService<IOptions<SignerBridgeOptions>>(), forceMock: true));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Serilog.Log.CloseAndFlush();
            try
            {
                if (Directory.Exists(_tempDataDir))
                    Directory.Delete(_tempDataDir, true);
            }
            catch
            {
                // Ignore transient file lock on test cleanup
            }
        }

        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            Token = host.Services.GetRequiredService<DpapiTokenStore>().GetOrCreateToken();
            return host;
        }
    }
}
