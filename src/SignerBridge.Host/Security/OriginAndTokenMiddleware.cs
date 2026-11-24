using Microsoft.Extensions.Options;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Security;

namespace SignerBridge.Host.Security;

/// <summary>
/// Enforces strict Origin whitelisting, Chrome/Edge Private Network Access (PNA) preflight headers,
/// and constant-time X-Signer-Token authentication.
/// </summary>
public sealed class OriginAndTokenMiddleware
{
    public const string TokenHeaderName = "X-Signer-Token";
    private readonly RequestDelegate _next;
    private readonly DpapiTokenStore _tokenStore;
    private readonly SignerBridgeOptions _options;

    public OriginAndTokenMiddleware(
        RequestDelegate next,
        DpapiTokenStore tokenStore,
        IOptions<SignerBridgeOptions> options)
    {
        _next = next;
        _tokenStore = tokenStore;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? origin = context.Request.Headers.Origin.FirstOrDefault();

        if (!string.IsNullOrEmpty(origin))
        {
            if (!IsOriginAllowed(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "ORIGIN_NOT_ALLOWED", detail = $"Origen no autorizado: {origin}" });
                return;
            }

            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, DELETE, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = $"{TokenHeaderName}, Content-Type";
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Token can be in X-Signer-Token header or ?token= for WebSocket upgrade
        string? candidateToken = context.Request.Headers[TokenHeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(candidateToken) && context.WebSockets.IsWebSocketRequest)
        {
            candidateToken = context.Request.Query["token"].FirstOrDefault();
        }

        if (!_tokenStore.ValidateToken(candidateToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Token de pairing inválido o ausente." });
            return;
        }

        await _next(context);
    }

    private bool IsOriginAllowed(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;

        foreach (var allowed in _options.AllowedOrigins)
        {
            if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                return true;

            if (Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri) &&
                string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(originUri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase) &&
                (allowedUri.IsDefaultPort || originUri.Port == allowedUri.Port))
            {
                return true;
            }
        }

        return false;
    }
}
