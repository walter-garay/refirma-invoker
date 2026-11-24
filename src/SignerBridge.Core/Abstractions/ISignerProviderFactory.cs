namespace SignerBridge.Core.Abstractions;

public interface ISignerProviderFactory
{
    IReadOnlyCollection<ISignerProvider> GetAll();
    bool TryGetProvider(string providerId, out ISignerProvider provider);
    ISignerProvider GetRequiredProvider(string providerId);
}

public sealed class SignerProviderFactory : ISignerProviderFactory
{
    private readonly IReadOnlyDictionary<string, ISignerProvider> _providers;

    public SignerProviderFactory(IEnumerable<ISignerProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ISignerProvider> GetAll() => _providers.Values.ToArray();

    public bool TryGetProvider(string providerId, out ISignerProvider provider)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            provider = null!;
            return false;
        }
        return _providers.TryGetValue(providerId.Trim(), out provider!);
    }

    public ISignerProvider GetRequiredProvider(string providerId)
    {
        if (!TryGetProvider(providerId, out var provider))
            throw new KeyNotFoundException($"Proveedor de firma no registrado: '{providerId}'.");
        return provider;
    }
}
