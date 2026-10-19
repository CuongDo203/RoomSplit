using System.ComponentModel.DataAnnotations;

namespace RoomSplit.Api.Dtos;

public record RoomResponse(
    int Id,
    string Name,
    string? Address,
    DateTime CreatedAt,
    int ActiveMemberCount);

public record CreateRoomRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [StringLength(255)] string? Address);

public record UpdateRoomRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [StringLength(255)] string? Address);
