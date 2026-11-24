using System.Security.Cryptography;
using System.Text;

namespace SignerBridge.Core.Security;

/// <summary>
/// Persists and validates the local pairing token using Windows DPAPI (CurrentUser scope).
/// </summary>
public sealed class DpapiTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SignerBridge.PairingToken.v1");
    private readonly string _tokenFilePath;
    private readonly object _gate = new();
    private string? _cachedToken;

    public DpapiTokenStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _tokenFilePath = Path.Combine(dataDirectory, "pairing.dat");
    }

    public string GetOrCreateToken()
    {
        lock (_gate)
        {
            if (_cachedToken is not null)
                return _cachedToken;

            if (File.Exists(_tokenFilePath))
            {
                try
                {
                    var encrypted = File.ReadAllBytes(_tokenFilePath);
                    var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    _cachedToken = Encoding.UTF8.GetString(plain);
                    return _cachedToken;
                }
                catch
                {
                    // Corrupted or different user profile; regenerate below.
                }
            }

            _cachedToken = GenerateAndSaveToken();
            return _cachedToken;
        }
    }

    public string RegenerateToken()
    {
        lock (_gate)
        {
            _cachedToken = GenerateAndSaveToken();
            return _cachedToken;
        }
    }

    public bool ValidateToken(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var expected = GetOrCreateToken();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var candidateBytes = Encoding.UTF8.GetBytes(candidate.Trim());

        if (expectedBytes.Length != candidateBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private string GenerateAndSaveToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var plainBytes = Encoding.UTF8.GetBytes(token);
        var encrypted = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_tokenFilePath, encrypted);
        return token;
    }
}
