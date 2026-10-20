using Microsoft.AspNetCore.Mvc;
using RoomSplit.Api.Common;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Entities;
using RoomSplit.Api.Services.Members;

namespace RoomSplit.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Base)]
public class MembersController(IMemberService memberService) : ControllerBase
{
    [HttpGet("rooms/{roomId:int}/members")]
    public async Task<ActionResult<List<MemberResponse>>> GetByRoom(int roomId, [FromQuery] MemberStatus? status, CancellationToken ct) =>
        await memberService.GetByRoomAsync(roomId, status, ct);

    [HttpGet("members/{id:int}")]
    public async Task<ActionResult<MemberResponse>> GetById(int id, CancellationToken ct) =>
        await memberService.GetByIdAsync(id, ct);

    [HttpPost("rooms/{roomId:int}/members")]
    public async Task<ActionResult<MemberResponse>> Create(int roomId, CreateMemberRequest request, CancellationToken ct)
    {
        var member = await memberService.CreateAsync(roomId, request, ct);
        return CreatedAtAction(nameof(GetById), new { id = member.Id }, member);
    }

    [HttpPut("members/{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateMemberRequest request, CancellationToken ct)
    {
        await memberService.UpdateAsync(id, request, ct);
        return NoContent();
    }

    [HttpPost("members/{id:int}/leave")]
    public async Task<ActionResult<MemberResponse>> Leave(int id, LeaveRoomRequest request, CancellationToken ct) =>
        await memberService.LeaveAsync(id, request, ct);

    [HttpDelete("members/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await memberService.DeleteAsync(id, ct);
        return NoContent();
    }
}
