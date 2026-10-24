using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using RoomSplit.Api.Common;
using RoomSplit.Api.Dtos;

namespace RoomSplit.Api.Services.Members;

public class MemberCache(
    IDistributedCache cache,
    IOptions<CacheOptions> options,
    TimeProvider timeProvider,
    ILogger<MemberCache> logger) : IMemberCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // While Redis is down every call waits for the client timeouts (seconds), which is slower than
    // having no cache at all. After a failure, skip the cache for this long before trying again.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly CacheOptions _options = options.Value;
    private long _skipUntilTicks;

    private bool IsActive => _options.Enabled && timeProvider.GetUtcNow().UtcTicks >= Interlocked.Read(ref _skipUntilTicks);

    // The Redis InstanceName ("roomsplit:") is prepended by the cache, giving roomsplit:members:room:{id}.
    public static string RoomKey(int roomId) => $"members:room:{roomId}";

    public async Task<List<MemberResponse>?> GetByRoomAsync(int roomId, CancellationToken ct)
    {
        if (!IsActive)
        {
            return null;
        }

        try
        {
            var bytes = await cache.GetAsync(RoomKey(roomId), ct);
            return bytes is null ? null : JsonSerializer.Deserialize<List<MemberResponse>>(bytes, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreachable cache or an entry written by an older MemberResponse shape: fall back to the database.
            OnFailure(ex, "Reading", roomId);
            return null;
        }
    }

    public async Task SetByRoomAsync(int roomId, List<MemberResponse> members, CancellationToken ct)
    {
        if (!IsActive)
        {
            return;
        }

        try
        {
            await cache.SetAsync(
                RoomKey(roomId),
                JsonSerializer.SerializeToUtf8Bytes(members, JsonOptions),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.MembersTtlSeconds)
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnFailure(ex, "Writing", roomId);
        }
    }

    public async Task InvalidateRoomAsync(int roomId)
    {
        if (!IsActive)
        {
            return;
        }

        try
        {
            // No CancellationToken on purpose: the change is already saved, so a client disconnecting now
            // must not leave a stale list behind.
            await cache.RemoveAsync(RoomKey(roomId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The entry still expires after MembersTtlSeconds.
            OnFailure(ex, "Invalidating", roomId);
        }
    }

    private void OnFailure(Exception ex, string operation, int roomId)
    {
        Interlocked.Exchange(ref _skipUntilTicks, (timeProvider.GetUtcNow() + RetryAfter).UtcTicks);
        logger.LogWarning(ex, "{Operation} cached members of room {RoomId} failed; skipping the cache for {RetryAfterSeconds}s",
            operation, roomId, RetryAfter.TotalSeconds);
    }
}
