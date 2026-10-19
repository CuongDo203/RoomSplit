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
    public async Task<ActionResult<List<RoomResponse>>> GetAll() =>
        await roomService.GetAllAsync();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<RoomResponse>> GetById(int id) =>
        await roomService.GetByIdAsync(id);

    [HttpPost]
    public async Task<ActionResult<RoomResponse>> Create(CreateRoomRequest request)
    {
        var room = await roomService.CreateAsync(request);
        return CreatedAtAction(nameof(GetById), new { id = room.Id }, room);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateRoomRequest request)
    {
        await roomService.UpdateAsync(id, request);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await roomService.DeleteAsync(id);
        return NoContent();
    }
}
