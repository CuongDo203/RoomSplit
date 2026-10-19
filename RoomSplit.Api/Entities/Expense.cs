namespace RoomSplit.Api.Entities;

public class Expense
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int CategoryId { get; set; }
    public int PaidBy { get; set; }
    public decimal Amount { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public string? Description { get; set; }
    public SplitType SplitType { get; set; }

    public Room Room { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public Member PaidByMember { get; set; } = null!;
    public ICollection<ExpenseShare> Shares { get; set; } = new List<ExpenseShare>();
}
