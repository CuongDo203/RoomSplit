namespace RoomSplit.Api.Entities;

public class Settlement
{
    public int Id { get; set; }
    public int PayerId { get; set; }
    public int PayeeId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly SettledAt { get; set; }
    public string? Note { get; set; }

    public Member Payer { get; set; } = null!;
    public Member Payee { get; set; } = null!;
}
