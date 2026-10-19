namespace RoomSplit.Api.Entities;

public class ExpenseShare
{
    public int ExpenseId { get; set; }
    public int MemberId { get; set; }
    public decimal ShareAmount { get; set; }

    public Expense Expense { get; set; } = null!;
    public Member Member { get; set; } = null!;
}
