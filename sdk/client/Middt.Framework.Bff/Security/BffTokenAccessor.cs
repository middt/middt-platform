using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Middt.Framework.Client.Security;
using Middt.Framework.Model.Authentication;

namespace Middt.Framework.Bff.Security;

/// <summary>
/// Returns a valid API access token of the current browser session, refreshing it shortly before it expires. Refresh
/// tokens are single use (rotation + reuse detection on the API), so concurrent requests of a session share one refresh.
/// The result is memoized per request (<see cref="HttpContext.Items"/>), and the session entry read by the cookie
/// validation (<see cref="DependencyInjection.MiddtBffExtensions.ValidateServerSessionAsync"/>) is reused: a proxied request
/// reads the token store once and parses the JWT once.
/// </summary>
public sealed partial class BffTokenAccessor(IServerTokenStore store, ITokenService tokenService, TimeProvider timeProvider, ILogger<BffTokenAccessor> logger)
{
    /// <summary>Tokens closer than this to expiry are refreshed before forwarding a request.</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RefreshLeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly object SessionItem = new();
    private static readonly object AccessTokenItem = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<SaveTokenResponseModel?>>> refreshes = new(StringComparer.Ordinal);
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    /// <summary>Keeps the session entry read while validating the cookie for the rest of the request.</summary>
    public static void RememberSession(HttpContext httpContext, string sessionId, SaveTokenResponseModel tokens)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Items[SessionItem] = new RequestSession(sessionId, tokens);
    }

    /// <summary>The access token, or <c>null</c> when the user is not signed in or the session ended (incl. failed refresh).</summary>
    public async Task<string?> GetAccessTokenAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.Items.TryGetValue(AccessTokenItem, out var memoized))
        {
            return (string?)memoized;
        }

        var token = await ResolveAccessTokenAsync(httpContext, cancellationToken);
        httpContext.Items[AccessTokenItem] = token;
        return token;
    }

    private async Task<string?> ResolveAccessTokenAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.User.FindFirst(MiddtClaimTypes.SessionId)?.Value is not { } sessionId)
        {
            return null;
        }

        var tokens = httpContext.Items[SessionItem] is RequestSession session && session.SessionId == sessionId
            ? session.Tokens
            : await store.GetAsync(sessionId, touch: true, cancellationToken);
        if (tokens is not { access_token: { Length: > 0 } accessToken })
        {
            return null;
        }

        if (!ExpiresSoon(accessToken))
        {
            return accessToken;
        }

        var refresh = refreshes.GetOrAdd(sessionId, id =>
        {
            Lazy<Task<SaveTokenResponseModel?>>? lazy = null;
            lazy = new Lazy<Task<SaveTokenResponseModel?>>(async () =>
            {
                try
                {
                    return await RefreshAsync(id, tokens);
                }
                finally
                {
                    if (lazy is not null)
                    {
                        refreshes.TryRemove(new KeyValuePair<string, Lazy<Task<SaveTokenResponseModel?>>>(id, lazy));
                    }
                }
            });
            return lazy;
        });

        return (await refresh.Value.WaitAsync(cancellationToken))?.access_token;
    }

    private bool ExpiresSoon(string accessToken)
    {
        try
        {
            return new JsonWebToken(accessToken).ValidTo <= timeProvider.GetUtcNow().UtcDateTime.Add(RefreshBefore);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private async Task<SaveTokenResponseModel?> RefreshAsync(string sessionId, SaveTokenResponseModel tokens)
    {
        var expectedRefreshToken = tokens.refresh_token ?? string.Empty;

        // A concurrent request may have refreshed already (and rotated the refresh token): use the stored result.
        if (await store.GetAsync(sessionId, touch: false) is { } current && current.refresh_token != expectedRefreshToken)
        {
            return current;
        }

        var leaseOwner = $"{instanceId}:{Guid.NewGuid():N}";
        var acquired = await store.TryAcquireRefreshLeaseAsync(sessionId, expectedRefreshToken, leaseOwner, RefreshLeaseDuration);
        if (!acquired)
        {
            for (var attempt = 0; attempt < 25; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40));
                var updated = await store.GetAsync(sessionId, touch: false);
                if (updated is null)
                {
                    return null;
                }

                if (updated.refresh_token != expectedRefreshToken)
                {
                    return updated;
                }

                if (await store.TryAcquireRefreshLeaseAsync(sessionId, expectedRefreshToken, leaseOwner, RefreshLeaseDuration))
                {
                    acquired = true;
                    break;
                }
            }

            if (!acquired)
            {
                var latest = await store.GetAsync(sessionId, touch: false);
                return latest is not null && latest.refresh_token != expectedRefreshToken ? latest : null;
            }
        }

        try
        {
            var afterLease = await store.GetAsync(sessionId, touch: false);
            if (afterLease is null)
            {
                return null;
            }

            if (afterLease.refresh_token != expectedRefreshToken)
            {
                return afterLease;
            }

            var result = await tokenService.RefreshTokenAsync(
                new TokenRefreshRequestModel { token = tokens.access_token, refresh_token = expectedRefreshToken },
                CancellationToken.None);

            if (!result.IsSuccess || string.IsNullOrEmpty(result.access_token))
            {
                if (await store.GetAsync(sessionId, touch: false) is { } rotated && rotated.refresh_token != expectedRefreshToken)
                {
                    return rotated;
                }

                LogRefreshFailed(logger, sessionId[..Math.Min(8, sessionId.Length)], result.ErrorText);
                await store.RemoveIfMatchAsync(sessionId, expectedRefreshToken);
                return null;
            }

            // Keep the absolute session expiry: refreshing tokens must not extend the session.
            var refreshed = new SaveTokenResponseModel(result) { expire_datetime = tokens.expire_datetime };
            // OIDC: providers may omit the identity token on refresh; keep the previous one for the logout id_token_hint.
            if (Oidc.OidcTokens.GetIdToken(refreshed) is null && Oidc.OidcTokens.GetIdToken(tokens) is { } idToken)
            {
                Oidc.OidcTokens.SetIdToken(refreshed, idToken);
            }

            return await store.CompareAndSetAsync(sessionId, expectedRefreshToken, refreshed) ?? refreshed;
        }
        finally
        {
            await store.ReleaseRefreshLeaseAsync(sessionId, leaseOwner);
        }
    }

    private sealed record RequestSession(string SessionId, SaveTokenResponseModel Tokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh of session {SessionPrefix}… failed, signing it out: {Error}")]
    private static partial void LogRefreshFailed(ILogger logger, string sessionPrefix, string error);
}
