using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Middt.Framework.Api.Configuration.Model;
using Middt.Framework.Model.Authentication;

namespace Middt.Framework.Bff.Security;

/// <summary>
/// <see cref="IServerTokenStore"/> on <see cref="IDistributedCache"/> (in-memory by default; register Redis for
/// multiple instances). Entries are encrypted with Data Protection, so cache dumps do not expose tokens.
/// Idle and absolute timeouts are enforced here, not by the cache's sliding expiration, so that background
/// revalidation can check a session without keeping it alive.
/// </summary>
public sealed class DistributedServerTokenStore(
    IDistributedCache cache,
    IDataProtectionProvider dataProtection,
    IOptions<GeneralSettings> settings,
    TimeProvider timeProvider) : IServerTokenStore
{
    private const string Prefix = "middt:session:";
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim[] Gates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private readonly IDataProtector protector = dataProtection.CreateProtector("Middt.ServerTokenStore.v1");

    public async Task<SaveTokenResponseModel?> GetAsync(string sessionId, bool touch, CancellationToken cancellationToken = default)
    {
        var entry = await ReadValidEntryAsync(sessionId, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (touch && now - entry.LastActivity > TouchThrottle)
        {
            var gate = GetGate(sessionId);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var latest = await ReadValidEntryAsync(sessionId, cancellationToken);
                if (latest is not null && now - latest.LastActivity > TouchThrottle)
                {
                    await WriteAsync(sessionId, latest with { LastActivity = now }, cancellationToken);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return entry.Tokens;
    }

    public async Task SetAsync(string sessionId, SaveTokenResponseModel tokens, CancellationToken cancellationToken = default)
    {
        var gate = GetGate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            tokens.expire_datetime ??= now.AddMinutes(settings.Value.SessionAbsoluteTimeoutInMinute);
            var existing = await ReadValidEntryAsync(sessionId, cancellationToken);
            var nextVersion = (existing?.Version ?? 0) + 1;
            await WriteAsync(sessionId, new Entry(tokens, now, nextVersion, null, null), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TryAcquireRefreshLeaseAsync(
        string sessionId,
        string expectedRefreshToken,
        string leaseOwnerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = await ReadValidEntryAsync(sessionId, cancellationToken);
            if (entry is null || !string.Equals(entry.Tokens.refresh_token, expectedRefreshToken, StringComparison.Ordinal))
            {
                return false;
            }

            var now = timeProvider.GetUtcNow();
            if (!string.IsNullOrEmpty(entry.LeaseOwner) &&
                !string.Equals(entry.LeaseOwner, leaseOwnerId, StringComparison.Ordinal) &&
                entry.LeaseExpiresAt is { } leaseExp &&
                leaseExp > now)
            {
                return false;
            }

            var leased = entry with
            {
                LeaseOwner = leaseOwnerId,
                LeaseExpiresAt = now.Add(leaseDuration),
            };
            await WriteAsync(sessionId, leased, cancellationToken);

            var verified = await ReadValidEntryAsync(sessionId, cancellationToken);
            return verified is not null &&
                   string.Equals(verified.Tokens.refresh_token, expectedRefreshToken, StringComparison.Ordinal) &&
                   string.Equals(verified.LeaseOwner, leaseOwnerId, StringComparison.Ordinal);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseRefreshLeaseAsync(
        string sessionId,
        string leaseOwnerId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = await ReadValidEntryAsync(sessionId, cancellationToken);
            if (entry is not null && string.Equals(entry.LeaseOwner, leaseOwnerId, StringComparison.Ordinal))
            {
                await WriteAsync(sessionId, entry with { LeaseOwner = null, LeaseExpiresAt = null }, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveTokenResponseModel?> CompareAndSetAsync(
        string sessionId,
        string expectedRefreshToken,
        SaveTokenResponseModel newTokens,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = await ReadValidEntryAsync(sessionId, cancellationToken);
            if (entry is null)
            {
                return null;
            }

            if (!string.Equals(entry.Tokens.refresh_token, expectedRefreshToken, StringComparison.Ordinal))
            {
                return entry.Tokens;
            }

            var now = timeProvider.GetUtcNow();
            newTokens.expire_datetime ??= entry.Tokens.expire_datetime ?? now.AddMinutes(settings.Value.SessionAbsoluteTimeoutInMinute);
            var updated = new Entry(newTokens, now, entry.Version + 1, null, null);
            await WriteAsync(sessionId, updated, cancellationToken);
            return newTokens;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveIfMatchAsync(
        string sessionId,
        string expectedRefreshToken,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = await ReadValidEntryAsync(sessionId, cancellationToken);
            if (entry is null)
            {
                return false;
            }

            if (!string.Equals(entry.Tokens.refresh_token, expectedRefreshToken, StringComparison.Ordinal))
            {
                return false;
            }

            await RemoveAsync(sessionId, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(Prefix + sessionId, cancellationToken);

    private TimeSpan IdleTimeout => TimeSpan.FromMinutes(settings.Value.SessionTimeoutInMinute);

    private static SemaphoreSlim GetGate(string sessionId) =>
        Gates[(uint)StringComparer.Ordinal.GetHashCode(sessionId) % (uint)Gates.Length];

    private async Task<Entry?> ReadValidEntryAsync(string sessionId, CancellationToken cancellationToken)
    {
        var bytes = await cache.GetAsync(Prefix + sessionId, cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        Entry entry;
        try
        {
            entry = JsonSerializer.Deserialize<Entry>(protector.Unprotect(bytes))!;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Data Protection keys changed (e.g. not persisted) while the cache survived: treat as signed out.
            await RemoveAsync(sessionId, cancellationToken);
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (entry.Tokens.expire_datetime <= now || now - entry.LastActivity > IdleTimeout)
        {
            await RemoveAsync(sessionId, cancellationToken);
            return null;
        }

        return entry;
    }

    private Task WriteAsync(string sessionId, Entry entry, CancellationToken cancellationToken) =>
        cache.SetAsync(
            Prefix + sessionId,
            protector.Protect(JsonSerializer.SerializeToUtf8Bytes(entry)),
            new DistributedCacheEntryOptions { AbsoluteExpiration = entry.Tokens.expire_datetime },
            cancellationToken);

    private sealed record Entry(
        SaveTokenResponseModel Tokens,
        DateTimeOffset LastActivity,
        long Version = 1,
        string? LeaseOwner = null,
        DateTimeOffset? LeaseExpiresAt = null);
}
