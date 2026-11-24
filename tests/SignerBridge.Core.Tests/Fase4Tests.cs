using SignerBridge.Core.Models;
using SignerBridge.Providers.ReFirmaPdf.Monitoring;
using Xunit;

namespace SignerBridge.Core.Tests;

public sealed class Fase4Tests
{
    [Fact]
    public void RefirmaLogWatcher_Reads_Only_Appended_Lines_From_Offset()
    {
        string logFile = Path.Combine(Path.GetTempPath(), $"refirma-test-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(logFile, "24-11-2026 15:00:00 WARN  old - DniePeruSignPinDialog cancelled\r\n");

            var watcher = new RefirmaLogWatcher(logFile);
            Assert.Empty(watcher.ReadNewLines()); // Old lines ignored

            File.AppendAllText(logFile, "24-11-2026 16:00:00 INFO  new - Validación en la TSL OK!\r\n");
            var firstBatch = watcher.ReadNewLines();
            Assert.Single(firstBatch);
            Assert.False(RefirmaLogWatcher.InspectLines(firstBatch).HasTerminalFinding);

            File.AppendAllText(logFile, "24-11-2026 16:00:05 WARN  new - DniePeruSignPinDialog cancelled\r\n");
            var secondBatch = watcher.ReadNewLines();
            var finding = RefirmaLogWatcher.InspectLines(secondBatch);
            Assert.True(finding.HasTerminalFinding);
            Assert.True(finding.Cancelled);
            Assert.Equal(SignErrorCode.USER_CANCELLED_PIN, finding.ErrorCode);
        }
        finally
        {
            if (File.Exists(logFile)) File.Delete(logFile);
        }
    }

    [Theory]
    [InlineData("24-11-2026 16:00:00 ERROR - PIN incorrecto. Quedan 2 intentos", SignErrorCode.INVALID_PIN_OR_LOCKED)]
    [InlineData("24-11-2026 16:00:00 ERROR - CKR_PIN_LOCKED", SignErrorCode.INVALID_PIN_OR_LOCKED)]
    [InlineData("24-11-2026 16:00:00 ERROR - Error validando TSL", SignErrorCode.TSL_VALIDATION_ERROR)]
    public void RefirmaLogWatcher_Classifies_Error_Lines(string line, SignErrorCode expectedCode)
    {
        var finding = RefirmaLogWatcher.InspectLines([line]);
        Assert.True(finding.HasTerminalFinding);
        Assert.False(finding.Cancelled);
        Assert.Equal(expectedCode, finding.ErrorCode);
    }

    [Fact]
    public void PinAndResultMonitor_Verifies_NonEmpty_Output_Pdfs()
    {
        string outDir = Path.Combine(Path.GetTempPath(), $"sb-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            Assert.False(PinAndResultMonitor.AreAllOutputPdfsReady(outDir, 1));

            string pdf = Path.Combine(outDir, "signed.pdf");
            File.WriteAllBytes(pdf, []);
            Assert.False(PinAndResultMonitor.AreAllOutputPdfsReady(outDir, 1)); // 0-byte rejected

            File.WriteAllBytes(pdf, "%PDF-1.4\n%%EOF\n"u8.ToArray());
            Assert.True(PinAndResultMonitor.AreAllOutputPdfsReady(outDir, 1));
            Assert.False(PinAndResultMonitor.AreAllOutputPdfsReady(outDir, 2));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
}
