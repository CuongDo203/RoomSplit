using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Data;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;
using RoomSplit.Api.Exceptions;
using RoomSplit.Api.Services.Members;

namespace RoomSplit.Api.Services.Rooms;

public class RoomService(AppDbContext db, IMemberCache memberCache) : IRoomService
{
    public Task<List<RoomResponse>> GetAllAsync(CancellationToken ct) =>
        db.Rooms
            .OrderBy(r => r.Id)
            .Select(r => new RoomResponse(
                r.Id, r.Name, r.Address, r.CreatedAt,
                r.Members.Count(m => m.Status == MemberStatus.Active)))
            .ToListAsync(ct);

    public async Task<RoomResponse> GetByIdAsync(int id, CancellationToken ct)
    {
        var room = await db.Rooms
            .Where(r => r.Id == id)
            .Select(r => new RoomResponse(
                r.Id, r.Name, r.Address, r.CreatedAt,
                r.Members.Count(m => m.Status == MemberStatus.Active)))
            .FirstOrDefaultAsync(ct);

        return room ?? throw RoomNotFound(id);
    }

    public async Task<RoomResponse> CreateAsync(CreateRoomRequest request, CancellationToken ct)
    {
        var room = new Room
        {
            Name = request.Name.Trim(),
            Address = request.Address?.Trim()
        };

        db.Rooms.Add(room);
        await db.SaveChangesAsync(ct);

        return new RoomResponse(room.Id, room.Name, room.Address, room.CreatedAt, 0);
    }

    public async Task UpdateAsync(int id, UpdateRoomRequest request, CancellationToken ct)
    {
        var room = await db.Rooms.FindAsync([id], ct) ?? throw RoomNotFound(id);

        room.Name = request.Name.Trim();
        room.Address = request.Address?.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var room = await db.Rooms.FindAsync([id], ct) ?? throw RoomNotFound(id);

        var hasHistory = await db.Members.AnyAsync(m => m.RoomId == id, ct)
                         || await db.Expenses.AnyAsync(e => e.RoomId == id, ct)
                         || await db.Settlements.AnyAsync(s => s.RoomId == id, ct);
        if (hasHistory)
        {
            throw new ConflictException("Room is in use", "Cannot delete a room that has members, expenses or settlements.");
        }

        db.Rooms.Remove(room);
        await db.SaveChangesAsync(ct);
        // An empty member list may be cached for this room; drop it so GET members returns 404 right away.
        await memberCache.InvalidateRoomAsync(id);
    }

    private static NotFoundException RoomNotFound(int id) => new($"Room {id} not found.");
}
