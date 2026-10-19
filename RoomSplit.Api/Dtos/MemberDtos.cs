using System.ComponentModel.DataAnnotations;
using RoomSplit.Api.Entities;

namespace RoomSplit.Api.Dtos;

public record MemberResponse(
    int Id,
    int RoomId,
    string FullName,
    string? Phone,
    DateOnly? JoinedAt,
    DateOnly? LeftAt,
    MemberStatus Status);

public record CreateMemberRequest(
    [Required, StringLength(100, MinimumLength = 1)] string FullName,
    [StringLength(15)] string? Phone,
    DateOnly? JoinedAt);

public record UpdateMemberRequest(
    [Required, StringLength(100, MinimumLength = 1)] string FullName,
    [StringLength(15)] string? Phone);

public record LeaveRoomRequest(DateOnly? LeftAt);
