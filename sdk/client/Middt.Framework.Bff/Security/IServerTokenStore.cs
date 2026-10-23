using Middt.Framework.Model.Authentication;

namespace Middt.Framework.Bff.Security;

/// <summary>
/// Server side storage of the API tokens of a signed-in browser session (BFF pattern): the browser only holds an
/// HttpOnly cookie with the session id, tokens never reach JavaScript.
/// </summary>
public interface IServerTokenStore
{
    /// <summary>
    /// Returns the session's tokens, or <c>null</c> when the session does not exist, was revoked or timed out.
    /// <paramref name="touch"/> marks user activity and extends the idle timeout.
    /// </summary>
    Task<SaveTokenResponseModel?> GetAsync(string sessionId, bool touch, CancellationToken cancellationToken = default);

    /// <summary>Stores tokens; <see cref="SaveTokenResponseModel.expire_datetime"/> is the absolute session expiry.</summary>
    Task SetAsync(string sessionId, SaveTokenResponseModel tokens, CancellationToken cancellationToken = default);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a short-lived distributed refresh lease for <paramref name="sessionId"/> when its stored
    /// refresh token still matches <paramref name="expectedRefreshToken"/>.
    /// </summary>
    Task<bool> TryAcquireRefreshLeaseAsync(
        string sessionId,
        string expectedRefreshToken,
        string leaseOwnerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>Releases a distributed refresh lease previously acquired by <paramref name="leaseOwnerId"/>.</summary>
    Task ReleaseRefreshLeaseAsync(
        string sessionId,
        string leaseOwnerId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Atomically stores <paramref name="newTokens"/> if the session's current refresh token is still
    /// <paramref name="expectedRefreshToken"/>; otherwise returns the newer tokens already stored by another instance.
    /// </summary>
    async Task<SaveTokenResponseModel?> CompareAndSetAsync(
        string sessionId,
        string expectedRefreshToken,
        SaveTokenResponseModel newTokens,
        CancellationToken cancellationToken = default)
    {
        if (await GetAsync(sessionId, touch: false, cancellationToken) is { } current &&
            !string.Equals(current.refresh_token, expectedRefreshToken, StringComparison.Ordinal))
        {
            return current;
        }

        await SetAsync(sessionId, newTokens, cancellationToken);
        return newTokens;
    }

    /// <summary>
    /// Removes the session only if its current refresh token still matches <paramref name="expectedRefreshToken"/>,
    /// preventing a failed concurrent refresh from wiping out a session already rotated by another BFF instance.
    /// </summary>
    async Task<bool> RemoveIfMatchAsync(
        string sessionId,
        string expectedRefreshToken,
        CancellationToken cancellationToken = default)
    {
        if (await GetAsync(sessionId, touch: false, cancellationToken) is { } current &&
            !string.Equals(current.refresh_token, expectedRefreshToken, StringComparison.Ordinal))
        {
            return false;
        }

        await RemoveAsync(sessionId, cancellationToken);
        return true;
    }
}
