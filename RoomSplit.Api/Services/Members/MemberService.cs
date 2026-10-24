using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Common;
using RoomSplit.Api.Data;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;
using RoomSplit.Api.Exceptions;

namespace RoomSplit.Api.Services.Members;

public class MemberService(AppDbContext db, IAppClock clock, IMemberCache memberCache) : IMemberService
{
    // The whole member list of a room is cached under one key (a room has only a few members),
    // so the status filter runs in memory and one invalidation covers every filter.
    public async Task<List<MemberResponse>> GetByRoomAsync(int roomId, MemberStatus? status, CancellationToken ct)
    {
        var members = await memberCache.GetByRoomAsync(roomId, ct);
        if (members is null)
        {
            await EnsureRoomExistsAsync(roomId, ct);

            members = await db.Members
                .Where(m => m.RoomId == roomId)
                .OrderBy(m => m.Id)
                .Select(m => ToResponse(m))
                .ToListAsync(ct);

            await memberCache.SetByRoomAsync(roomId, members, ct);
        }

        return status is null ? members : members.Where(m => m.Status == status).ToList();
    }

    public async Task<MemberResponse> GetByIdAsync(int id, CancellationToken ct) =>
        ToResponse(await FindMemberAsync(id, ct));

    public async Task<MemberResponse> CreateAsync(int roomId, CreateMemberRequest request, CancellationToken ct)
    {
        await EnsureRoomExistsAsync(roomId, ct);

        var member = new Member
        {
            RoomId = roomId,
            FullName = request.FullName.Trim(),
            Phone = request.Phone?.Trim(),
            JoinedAt = request.JoinedAt ?? clock.Today,
            Status = MemberStatus.Active
        };

        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
        await memberCache.InvalidateRoomAsync(roomId);

        return ToResponse(member);
    }

    public async Task UpdateAsync(int id, UpdateMemberRequest request, CancellationToken ct)
    {
        var member = await FindMemberAsync(id, ct);

        member.FullName = request.FullName.Trim();
        member.Phone = request.Phone?.Trim();
        await db.SaveChangesAsync(ct);
        await memberCache.InvalidateRoomAsync(member.RoomId);
    }

    // A member who leaves keeps their history; they just can't join new expenses.
    public async Task<MemberResponse> LeaveAsync(int id, LeaveRoomRequest request, CancellationToken ct)
    {
        var member = await FindMemberAsync(id, ct);

        if (member.Status == MemberStatus.Left)
        {
            throw new ConflictException("Member already left", $"Member {id} left the room on {member.LeftAt:yyyy-MM-dd}.");
        }

        var leftAt = request.LeftAt ?? clock.Today;
        if (member.JoinedAt is not null && leftAt < member.JoinedAt)
        {
            throw new BusinessRuleException("Invalid leave date", "LeftAt cannot be earlier than JoinedAt.");
        }

        member.LeftAt = leftAt;
        member.Status = MemberStatus.Left;
        await db.SaveChangesAsync(ct);
        await memberCache.InvalidateRoomAsync(member.RoomId);

        return ToResponse(member);
    }

    // Hard delete is only for members added by mistake; anyone with history should leave instead.
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var member = await FindMemberAsync(id, ct);

        var hasHistory = await db.Expenses.AnyAsync(e => e.PaidBy == id, ct)
                         || await db.ExpenseShares.AnyAsync(s => s.MemberId == id, ct)
                         || await db.Settlements.AnyAsync(s => s.PayerId == id || s.PayeeId == id, ct);
        if (hasHistory)
        {
            throw new ConflictException(
                "Member has history",
                "Cannot delete a member with expenses or settlements. Use POST /api/v1/members/{id}/leave instead.");
        }

        db.Members.Remove(member);
        await db.SaveChangesAsync(ct);
        await memberCache.InvalidateRoomAsync(member.RoomId);
    }

    private async Task<Member> FindMemberAsync(int id, CancellationToken ct) =>
        await db.Members.FindAsync([id], ct) ?? throw new NotFoundException($"Member {id} not found.");

    private async Task EnsureRoomExistsAsync(int roomId, CancellationToken ct)
    {
        if (!await db.Rooms.AnyAsync(r => r.Id == roomId, ct))
        {
            throw new NotFoundException($"Room {roomId} not found.");
        }
    }

    private static MemberResponse ToResponse(Member m) =>
        new(m.Id, m.RoomId, m.FullName, m.Phone, m.JoinedAt, m.LeftAt, m.Status);
}
