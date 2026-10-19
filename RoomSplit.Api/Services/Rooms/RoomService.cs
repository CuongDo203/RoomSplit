using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Data;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;
using RoomSplit.Api.Exceptions;

namespace RoomSplit.Api.Services.Rooms;

public class RoomService(AppDbContext db) : IRoomService
{
    public Task<List<RoomResponse>> GetAllAsync() =>
        db.Rooms
            .OrderBy(r => r.Id)
            .Select(r => new RoomResponse(
                r.Id, r.Name, r.Address, r.CreatedAt,
                r.Members.Count(m => m.Status == MemberStatus.Active)))
            .ToListAsync();

    public async Task<RoomResponse> GetByIdAsync(int id)
    {
        var room = await db.Rooms
            .Where(r => r.Id == id)
            .Select(r => new RoomResponse(
                r.Id, r.Name, r.Address, r.CreatedAt,
                r.Members.Count(m => m.Status == MemberStatus.Active)))
            .FirstOrDefaultAsync();

        return room ?? throw RoomNotFound(id);
    }

    public async Task<RoomResponse> CreateAsync(CreateRoomRequest request)
    {
        var room = new Room
        {
            Name = request.Name.Trim(),
            Address = request.Address?.Trim()
        };

        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        return new RoomResponse(room.Id, room.Name, room.Address, room.CreatedAt, 0);
    }

    public async Task UpdateAsync(int id, UpdateRoomRequest request)
    {
        var room = await db.Rooms.FindAsync(id) ?? throw RoomNotFound(id);

        room.Name = request.Name.Trim();
        room.Address = request.Address?.Trim();
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var room = await db.Rooms.FindAsync(id) ?? throw RoomNotFound(id);

        var hasHistory = await db.Members.AnyAsync(m => m.RoomId == id)
                         || await db.Expenses.AnyAsync(e => e.RoomId == id);
        if (hasHistory)
        {
            throw new ConflictException("Room is in use", "Cannot delete a room that has members or expenses.");
        }

        db.Rooms.Remove(room);
        await db.SaveChangesAsync();
    }

    private static NotFoundException RoomNotFound(int id) => new($"Room {id} not found.");
}
