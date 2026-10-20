using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;

namespace RoomSplit.Api.Services.Members;

public interface IMemberService
{
    Task<List<MemberResponse>> GetByRoomAsync(int roomId, MemberStatus? status, CancellationToken ct);
    Task<MemberResponse> GetByIdAsync(int id, CancellationToken ct);
    Task<MemberResponse> CreateAsync(int roomId, CreateMemberRequest request, CancellationToken ct);
    Task UpdateAsync(int id, UpdateMemberRequest request, CancellationToken ct);
    Task<MemberResponse> LeaveAsync(int id, LeaveRoomRequest request, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
}
