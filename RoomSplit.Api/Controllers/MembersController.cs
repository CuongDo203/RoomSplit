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
    public async Task<ActionResult<List<MemberResponse>>> GetByRoom(int roomId, [FromQuery] MemberStatus? status) =>
        await memberService.GetByRoomAsync(roomId, status);

    [HttpGet("members/{id:int}")]
    public async Task<ActionResult<MemberResponse>> GetById(int id) =>
        await memberService.GetByIdAsync(id);

    [HttpPost("rooms/{roomId:int}/members")]
    public async Task<ActionResult<MemberResponse>> Create(int roomId, CreateMemberRequest request)
    {
        var member = await memberService.CreateAsync(roomId, request);
        return CreatedAtAction(nameof(GetById), new { id = member.Id }, member);
    }

    [HttpPut("members/{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateMemberRequest request)
    {
        await memberService.UpdateAsync(id, request);
        return NoContent();
    }

    [HttpPost("members/{id:int}/leave")]
    public async Task<ActionResult<MemberResponse>> Leave(int id, LeaveRoomRequest request) =>
        await memberService.LeaveAsync(id, request);

    [HttpDelete("members/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await memberService.DeleteAsync(id);
        return NoContent();
    }
}
