namespace RoomSplit.Api.Entities;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;

    public ICollection<Expense> Expenses { get; set; } = new List<Expense>();
}
