using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SignerBridge.Core.Abstractions;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Models;
using SignerBridge.Core.Storage;
using SignerBridge.Providers.ReFirmaPdf;
using Xunit;

namespace SignerBridge.Core.Tests;

public sealed class Fase2Tests
{
    [Fact]
    public void SignerProviderFactory_Resolves_CaseInsensitive_And_Throws_On_Unknown()
    {
        var opts = Options.Create(new SignerBridgeOptions());
        var refirma = new ReFirmaPdfProvider(opts, forceMock: true);
        var factory = new SignerProviderFactory([refirma]);

        Assert.Single(factory.GetAll());
        Assert.True(factory.TryGetProvider("REFIRMA-PDF", out var resolved));
        Assert.Same(refirma, resolved);

        Assert.False(factory.TryGetProvider("firma-peru", out _));
        Assert.Throws<KeyNotFoundException>(() => factory.GetRequiredProvider("firma-onpe"));
    }

    [Fact]
    public void ArchiveService_Rejects_ZipSlip_Traversal()
    {
        using var zipMs = new MemoryStream();
        using (var za = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var evil = za.CreateEntry("../../evil.pdf");
            using var s = evil.Open();
            s.Write("%PDF-1.4\n%%EOF\n"u8);
        }
        zipMs.Position = 0;

        string target = Path.Combine(Path.GetTempPath(), "sb-zipslip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new ArchiveService();
            Assert.Throws<InvalidOperationException>(() => svc.ExtractPdfZipSafely(zipMs, target));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    [Fact]
    public async Task ArchiveService_Extracts_Valid_Pdfs_And_Builds_Manifest_Zip()
    {
        using var inputZip = new MemoryStream();
        using (var za = new ZipArchive(inputZip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var pdf1 = za.CreateEntry("sub/doc-a.pdf");
            await using (var s = pdf1.Open()) await s.WriteAsync("%PDF-1.4\n%%EOF\n"u8.ToArray());

            var pdf2 = za.CreateEntry("doc-b.pdf");
            await using (var s = pdf2.Open()) await s.WriteAsync("%PDF-1.7\n%%EOF\n"u8.ToArray());

            var ignored = za.CreateEntry("readme.txt");
            await using (var s = ignored.Open()) await s.WriteAsync(Encoding.UTF8.GetBytes("ignore"));
        }
        inputZip.Position = 0;

        string root = Path.Combine(Path.GetTempPath(), "sb-arch-" + Guid.NewGuid().ToString("N"));
        string inDir = Path.Combine(root, "in");
        string outZipPath = Path.Combine(root, "out.zip");

        try
        {
            var svc = new ArchiveService();
            var extracted = svc.ExtractPdfZipSafely(inputZip, inDir);
            Assert.Equal(2, extracted.Count);

            await svc.BuildResultZipAsync("job123", "refirma-pdf", inDir, outZipPath);
            Assert.True(File.Exists(outZipPath));

            using var outZa = ZipFile.OpenRead(outZipPath);
            var manifestEntry = outZa.GetEntry("manifest.json");
            Assert.NotNull(manifestEntry);

            await using var ms = manifestEntry!.Open();
            var manifest = await JsonSerializer.DeserializeAsync<SignResultManifest>(ms);
            Assert.NotNull(manifest);
            Assert.Equal(2, manifest!.FilesCount);
            Assert.All(manifest.Files, f => Assert.Equal(64, f.Sha256.Length));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReFirmaPdfProvider_Expands_Environment_Variables_From_Config()
    {
        var opts = Options.Create(new SignerBridgeOptions
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["refirma-pdf"] = new ProviderConfig
                {
                    ExecutablePath = @"%TEMP%\custom-refirma.appref-ms",
                    JrePath = @"%TEMP%\custom-jre\bin",
                    LogFilePath = @"%TEMP%\custom.log"
                }
            }
        });

        var provider = new ReFirmaPdfProvider(opts);
        string temp = Path.GetTempPath().TrimEnd('\\');
        Assert.StartsWith(temp, provider.ResolvedExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(temp, provider.ResolvedJreBinPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(temp, provider.ResolvedLogFilePath, StringComparison.OrdinalIgnoreCase);
    }
}
