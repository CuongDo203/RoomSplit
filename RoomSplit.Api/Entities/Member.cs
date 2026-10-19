namespace RoomSplit.Api.Entities;

public class Member
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public string FullName { get; set; } = null!;
    public string? Phone { get; set; }
    public DateOnly? JoinedAt { get; set; }
    public DateOnly? LeftAt { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.Active;

    public Room Room { get; set; } = null!;
    public ICollection<Expense> PaidExpenses { get; set; } = new List<Expense>();
    public ICollection<ExpenseShare> ExpenseShares { get; set; } = new List<ExpenseShare>();
    public ICollection<Settlement> SettlementsPaid { get; set; } = new List<Settlement>();
    public ICollection<Settlement> SettlementsReceived { get; set; } = new List<Settlement>();
}
