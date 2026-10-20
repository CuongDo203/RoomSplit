using RoomSplit.Api.Dtos;

namespace RoomSplit.Api.Services.Rooms;

public interface IRoomService
{
    Task<List<RoomResponse>> GetAllAsync(CancellationToken ct);
    Task<RoomResponse> GetByIdAsync(int id, CancellationToken ct);
    Task<RoomResponse> CreateAsync(CreateRoomRequest request, CancellationToken ct);
    Task UpdateAsync(int id, UpdateRoomRequest request, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
}
