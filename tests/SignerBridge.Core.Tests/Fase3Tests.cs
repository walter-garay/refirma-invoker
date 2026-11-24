using SignerBridge.Core.Models;
using SignerBridge.Providers.ReFirmaPdf.Jab;
using Xunit;

namespace SignerBridge.Core.Tests;

public sealed class Fase3Tests
{
    private const string SampleCertLabel =
        "VALDIVIA ALVARADO Beatriz Rocio FIR 09428893 hard (SN:362E9833BE2C815573F13729B7FEE49346DA3662)";

    [Theory]
    [InlineData("FULL_NAME", "VALDIVIA ALVARADO Beatriz Rocio", true)]
    [InlineData("FULL_NAME", "PEREZ GOMEZ Juan", false)]
    [InlineData("FIR", "09428893", true)]
    [InlineData("FIR", "FIR 09428893", true)]
    [InlineData("FIR", "99999999", false)]
    [InlineData("SN", "362E9833BE2C815573F13729B7FEE49346DA3662", true)]
    [InlineData("SN", "36:2E:98:33:BE:2C", true)]
    [InlineData("AUTO", "Beatriz Rocio", true)]
    [InlineData("AUTO", "", true)]
    public void MatchesCertificate_Handles_All_Modes(string matchBy, string query, bool expected)
    {
        var selector = new CertificateSelector { MatchBy = matchBy, Query = query };
        Assert.Equal(expected, JabSession.MatchesCertificate(SampleCertLabel, selector));
    }
}
