using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;

namespace RoomSplit.Api.Services.Members;

public interface IMemberService
{
    Task<List<MemberResponse>> GetByRoomAsync(int roomId, MemberStatus? status);
    Task<MemberResponse> GetByIdAsync(int id);
    Task<MemberResponse> CreateAsync(int roomId, CreateMemberRequest request);
    Task UpdateAsync(int id, UpdateMemberRequest request);
    Task<MemberResponse> LeaveAsync(int id, LeaveRoomRequest request);
    Task DeleteAsync(int id);
}
