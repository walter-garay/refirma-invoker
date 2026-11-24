using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SignerBridge.Core.Models;

namespace SignerBridge.Core.Storage;

public interface IArchiveService
{
    IReadOnlyList<string> ExtractPdfZipSafely(Stream zipStream, string targetDir);
    Task<string> SavePdfStreamAsync(Stream pdfStream, string originalFileName, string targetDir, CancellationToken ct = default);
    Task<string> BuildResultZipAsync(string jobId, string providerId, string outputDir, string zipDestinationPath, CancellationToken ct = default);
}

public sealed class ArchiveService : IArchiveService
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    public static bool HasPdfMagicHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[PdfMagic.Length];
        int read = stream.Read(header);
        return read == PdfMagic.Length && header.SequenceEqual(PdfMagic);
    }

    public static bool IsValidPdfFile(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        using var fs = File.OpenRead(filePath);
        return HasPdfMagicHeader(fs);
    }

    public IReadOnlyList<string> ExtractPdfZipSafely(Stream zipStream, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        string fullTargetRoot = Path.GetFullPath(targetDir);
        if (!fullTargetRoot.EndsWith(Path.DirectorySeparatorChar))
            fullTargetRoot += Path.DirectorySeparatorChar;

        var extractedPdfs = new List<string>();
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // Reject ZipSlip traversal or rooted paths before normalizing
            string normalizedEntry = entry.FullName.Replace('\\', '/');
            if (normalizedEntry.StartsWith('/') ||
                normalizedEntry.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(entry.FullName))
            {
                throw new InvalidOperationException($"ZipSlip path traversal detected: {entry.FullName}");
            }

            if (!entry.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                continue;

            string safeFileName = Path.GetFileName(entry.Name);
            string destPath = Path.GetFullPath(Path.Combine(fullTargetRoot, safeFileName));
            if (!destPath.StartsWith(fullTargetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ZipSlip path traversal detected: {entry.FullName}");

            if (File.Exists(destPath))
            {
                string stem = Path.GetFileNameWithoutExtension(safeFileName);
                string ext = Path.GetExtension(safeFileName);
                destPath = Path.Combine(fullTargetRoot, $"{stem}_{Guid.NewGuid():N[..6]}{ext}");
            }

            using (var entryStream = entry.Open())
            using (var outStream = File.Create(destPath))
            {
                entryStream.CopyTo(outStream);
            }

            if (!IsValidPdfFile(destPath))
            {
                File.Delete(destPath);
                throw new InvalidDataException($"El archivo '{entry.FullName}' no tiene cabecera PDF válida (%PDF-).");
            }

            extractedPdfs.Add(destPath);
        }

        return extractedPdfs;
    }

    public async Task<string> SavePdfStreamAsync(Stream pdfStream, string originalFileName, string targetDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        string safeName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidDataException("Nombre de archivo PDF inválido.");

        string dest = Path.Combine(targetDir, safeName);
        await using (var fs = File.Create(dest))
            await pdfStream.CopyToAsync(fs, ct);

        if (!IsValidPdfFile(dest))
        {
            File.Delete(dest);
            throw new InvalidDataException($"El archivo '{safeName}' no es un PDF válido (%PDF-).");
        }

        return dest;
    }

    public async Task<string> BuildResultZipAsync(
        string jobId,
        string providerId,
        string outputDir,
        string zipDestinationPath,
        CancellationToken ct = default)
    {
        var pdfFiles = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*.pdf", SearchOption.TopDirectoryOnly)
            : [];

        var entries = new List<SignedFileEntry>(pdfFiles.Length);

        if (File.Exists(zipDestinationPath))
            File.Delete(zipDestinationPath);

        using (var zipFs = File.Create(zipDestinationPath))
        using (var archive = new ZipArchive(zipFs, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var filePath in pdfFiles)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(filePath);
                var fi = new FileInfo(filePath);
                string sha256 = await ComputeSha256Async(filePath, ct);

                entries.Add(new SignedFileEntry(fileName, fi.Length, sha256));
                archive.CreateEntryFromFile(filePath, fileName, CompressionLevel.Fastest);
            }

            var manifest = new SignResultManifest(
                jobId,
                providerId,
                DateTimeOffset.UtcNow,
                entries.Count,
                entries);

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using var ms = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(ms, manifest, new JsonSerializerOptions { WriteIndented = true }, ct);
        }

        return zipDestinationPath;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
