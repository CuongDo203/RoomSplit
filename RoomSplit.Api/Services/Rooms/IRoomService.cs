using RoomSplit.Api.Dtos;

namespace RoomSplit.Api.Services.Rooms;

public interface IRoomService
{
    Task<List<RoomResponse>> GetAllAsync();
    Task<RoomResponse> GetByIdAsync(int id);
    Task<RoomResponse> CreateAsync(CreateRoomRequest request);
    Task UpdateAsync(int id, UpdateRoomRequest request);
    Task DeleteAsync(int id);
}
