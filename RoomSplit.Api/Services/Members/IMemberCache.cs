using RoomSplit.Api.Dtos;

namespace RoomSplit.Api.Services.Members;

/// <summary>
/// Cached member list of a room (all statuses). Fail-open: cache errors are logged and treated as a miss.
/// </summary>
public interface IMemberCache
{
    /// <returns>The cached list, or <c>null</c> on a miss, when caching is off, or when the cache is unreachable.</returns>
    Task<List<MemberResponse>?> GetByRoomAsync(int roomId, CancellationToken ct);

    Task SetByRoomAsync(int roomId, List<MemberResponse> members, CancellationToken ct);

    /// <summary>Call after any change to the room's members has been saved.</summary>
    Task InvalidateRoomAsync(int roomId);
}
