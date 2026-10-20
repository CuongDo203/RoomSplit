using Microsoft.AspNetCore.Mvc;
using RoomSplit.Api.Common;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Services.Rooms;

namespace RoomSplit.Api.Controllers;

[ApiController]
[Route($"{ApiRoutes.Base}/rooms")]
public class RoomsController(IRoomService roomService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RoomResponse>>> GetAll(CancellationToken ct) =>
        await roomService.GetAllAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<RoomResponse>> GetById(int id, CancellationToken ct) =>
        await roomService.GetByIdAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<RoomResponse>> Create(CreateRoomRequest request, CancellationToken ct)
    {
        var room = await roomService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = room.Id }, room);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateRoomRequest request, CancellationToken ct)
    {
        await roomService.UpdateAsync(id, request, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await roomService.DeleteAsync(id, ct);
        return NoContent();
    }
}
