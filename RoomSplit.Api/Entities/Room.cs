namespace RoomSplit.Api.Entities;

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<Member> Members { get; set; } = new List<Member>();
    public ICollection<Expense> Expenses { get; set; } = new List<Expense>();
    public ICollection<Settlement> Settlements { get; set; } = new List<Settlement>();
}
