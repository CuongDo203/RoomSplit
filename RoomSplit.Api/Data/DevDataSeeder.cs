using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Entities;

namespace RoomSplit.Api.Data;

/// <summary>
/// Sample data for Development only, run by EF Core after Migrate()/EnsureCreated()
/// (UseSeeding/UseAsyncSeeding). Skips if any room exists, so restarts don't duplicate it.
/// </summary>
public static class DevDataSeeder
{
    public static void Seed(DbContext context, DateOnly today)
    {
        var db = (AppDbContext)context;
        if (db.Rooms.Any())
        {
            return;
        }

        db.Rooms.Add(BuildSampleRoom(today));
        db.SaveChanges();
    }

    public static async Task SeedAsync(DbContext context, DateOnly today, CancellationToken ct)
    {
        var db = (AppDbContext)context;
        if (await db.Rooms.AnyAsync(ct))
        {
            return;
        }

        db.Rooms.Add(BuildSampleRoom(today));
        await db.SaveChangesAsync(ct);
    }

    // Category ids come from the HasData seed in AppDbContext.
    private const int Electricity = 1, Water = 2, Internet = 3, Food = 4;

    private static Room BuildSampleRoom(DateOnly today)
    {
        var joinedAt = today.AddMonths(-3);
        var room = new Room { Name = "Phòng 101", Address = "12 Nguyễn Trãi, Thanh Xuân, Hà Nội" };

        var an = new Member { Room = room, FullName = "Nguyễn Văn An", Phone = "0901000001", JoinedAt = joinedAt };
        var binh = new Member { Room = room, FullName = "Trần Thị Bình", Phone = "0901000002", JoinedAt = joinedAt };
        var chi = new Member { Room = room, FullName = "Lê Minh Chi", Phone = "0901000003", JoinedAt = joinedAt };
        Member[] everyone = [an, binh, chi];

        // Balances before the settlement: An +236,667, Binh −113,334, Chi −123,333.
        // After Binh pays An 100,000: An +136,667, Binh −13,334, Chi −123,333.
        Equal(room, Electricity, an, 300_000, today.AddDays(-20), "Tiền điện tháng trước", everyone);
        Equal(room, Water, binh, 100_000, today.AddDays(-18), "Tiền nước tháng trước", everyone);
        Equal(room, Internet, an, 240_000, today.AddDays(-15), "Internet tháng này", everyone);
        Custom(room, Food, chi, today.AddDays(-5), "Đi chợ cuối tuần", (an, 90_000), (chi, 60_000));

        room.Settlements.Add(new Settlement
        {
            Room = room,
            Payer = binh,
            Payee = an,
            Amount = 100_000,
            SettledAt = today.AddDays(-2),
            Note = "Trả tiền điện",
        });

        return room;
    }

    // EQUAL split: the remainder goes to the payer's share (business rule 3).
    private static void Equal(Room room, int categoryId, Member payer, decimal amount, DateOnly date,
        string description, Member[] members)
    {
        var baseShare = Math.Floor(amount / members.Length);
        var remainder = amount - baseShare * members.Length;

        var expense = NewExpense(room, categoryId, payer, amount, date, description, SplitType.Equal);
        foreach (var member in members)
        {
            var share = member == payer ? baseShare + remainder : baseShare;
            expense.Shares.Add(new ExpenseShare { Member = member, ShareAmount = share });
        }
    }

    private static void Custom(Room room, int categoryId, Member payer, DateOnly date, string description,
        params (Member Member, decimal Amount)[] shares)
    {
        var expense = NewExpense(room, categoryId, payer, shares.Sum(s => s.Amount), date, description,
            SplitType.Custom);
        foreach (var (member, amount) in shares)
        {
            expense.Shares.Add(new ExpenseShare { Member = member, ShareAmount = amount });
        }
    }

    private static Expense NewExpense(Room room, int categoryId, Member payer, decimal amount, DateOnly date,
        string description, SplitType splitType)
    {
        var expense = new Expense
        {
            Room = room,
            CategoryId = categoryId,
            PaidByMember = payer,
            Amount = amount,
            ExpenseDate = date,
            Description = description,
            SplitType = splitType,
        };
        room.Expenses.Add(expense);
        return expense;
    }
}
