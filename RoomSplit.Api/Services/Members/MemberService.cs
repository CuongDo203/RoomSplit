using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Data;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;
using RoomSplit.Api.Exceptions;

namespace RoomSplit.Api.Services.Members;

public class MemberService(AppDbContext db) : IMemberService
{
    public async Task<List<MemberResponse>> GetByRoomAsync(int roomId, MemberStatus? status)
    {
        await EnsureRoomExistsAsync(roomId);

        var query = db.Members.Where(m => m.RoomId == roomId);
        if (status is not null)
        {
            query = query.Where(m => m.Status == status);
        }

        return await query
            .OrderBy(m => m.Id)
            .Select(m => ToResponse(m))
            .ToListAsync();
    }

    public async Task<MemberResponse> GetByIdAsync(int id) =>
        ToResponse(await FindMemberAsync(id));

    public async Task<MemberResponse> CreateAsync(int roomId, CreateMemberRequest request)
    {
        await EnsureRoomExistsAsync(roomId);

        var member = new Member
        {
            RoomId = roomId,
            FullName = request.FullName.Trim(),
            Phone = request.Phone?.Trim(),
            JoinedAt = request.JoinedAt ?? DateOnly.FromDateTime(DateTime.Today),
            Status = MemberStatus.Active
        };

        db.Members.Add(member);
        await db.SaveChangesAsync();

        return ToResponse(member);
    }

    public async Task UpdateAsync(int id, UpdateMemberRequest request)
    {
        var member = await FindMemberAsync(id);

        member.FullName = request.FullName.Trim();
        member.Phone = request.Phone?.Trim();
        await db.SaveChangesAsync();
    }

    // A member who leaves keeps their history; they just can't join new expenses.
    public async Task<MemberResponse> LeaveAsync(int id, LeaveRoomRequest request)
    {
        var member = await FindMemberAsync(id);

        if (member.Status == MemberStatus.Left)
        {
            throw new ConflictException("Member already left", $"Member {id} left the room on {member.LeftAt:yyyy-MM-dd}.");
        }

        var leftAt = request.LeftAt ?? DateOnly.FromDateTime(DateTime.Today);
        if (member.JoinedAt is not null && leftAt < member.JoinedAt)
        {
            throw new BusinessRuleException("Invalid leave date", "LeftAt cannot be earlier than JoinedAt.");
        }

        member.LeftAt = leftAt;
        member.Status = MemberStatus.Left;
        await db.SaveChangesAsync();

        return ToResponse(member);
    }

    // Hard delete is only for members added by mistake; anyone with history should leave instead.
    public async Task DeleteAsync(int id)
    {
        var member = await FindMemberAsync(id);

        var hasHistory = await db.Expenses.AnyAsync(e => e.PaidBy == id)
                         || await db.ExpenseShares.AnyAsync(s => s.MemberId == id)
                         || await db.Settlements.AnyAsync(s => s.PayerId == id || s.PayeeId == id);
        if (hasHistory)
        {
            throw new ConflictException(
                "Member has history",
                "Cannot delete a member with expenses or settlements. Use POST /api/v1/members/{id}/leave instead.");
        }

        db.Members.Remove(member);
        await db.SaveChangesAsync();
    }

    private async Task<Member> FindMemberAsync(int id) =>
        await db.Members.FindAsync(id) ?? throw new NotFoundException($"Member {id} not found.");

    private async Task EnsureRoomExistsAsync(int roomId)
    {
        if (!await db.Rooms.AnyAsync(r => r.Id == roomId))
        {
            throw new NotFoundException($"Room {roomId} not found.");
        }
    }

    private static MemberResponse ToResponse(Member m) =>
        new(m.Id, m.RoomId, m.FullName, m.Phone, m.JoinedAt, m.LeftAt, m.Status);
}
