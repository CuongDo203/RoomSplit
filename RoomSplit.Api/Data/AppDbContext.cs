using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Entities;

namespace RoomSplit.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseShare> ExpenseShares => Set<ExpenseShare>();
    public DbSet<Settlement> Settlements => Set<Settlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Room>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(100).IsRequired();
            e.Property(r => r.Address).HasMaxLength(255);
            e.Property(r => r.CreatedAt)
                .HasColumnType("datetime(6)")
                .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
        });

        modelBuilder.Entity<Member>(e =>
        {
            e.Property(m => m.FullName).HasMaxLength(100).IsRequired();
            e.Property(m => m.Phone).HasMaxLength(15);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);

            e.HasOne(m => m.Room)
                .WithMany(r => r.Members)
                .HasForeignKey(m => m.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(50).IsRequired();
            e.HasIndex(c => c.Name).IsUnique();

            e.HasData(
                new Category { Id = 1, Name = "Electricity" },
                new Category { Id = 2, Name = "Water" },
                new Category { Id = 3, Name = "Internet" },
                new Category { Id = 4, Name = "Food" },
                new Category { Id = 5, Name = "Household" },
                new Category { Id = 6, Name = "Other" });
        });

        modelBuilder.Entity<Expense>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("ck_expenses_amount_positive", "amount > 0"));

            e.Property(x => x.Amount).HasPrecision(12, 0);
            e.Property(x => x.Description).HasMaxLength(255);
            e.Property(x => x.SplitType).HasConversion<string>().HasMaxLength(20);

            // Listing/filtering a room's expenses by date range.
            e.HasIndex(x => new { x.RoomId, x.ExpenseDate });

            e.HasOne(x => x.Room)
                .WithMany(r => r.Expenses)
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Category)
                .WithMany(c => c.Expenses)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.PaidByMember)
                .WithMany(m => m.PaidExpenses)
                .HasForeignKey(x => x.PaidBy)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExpenseShare>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("ck_expense_shares_share_amount_non_negative", "share_amount >= 0"));

            e.HasKey(s => new { s.ExpenseId, s.MemberId });
            e.Property(s => s.ShareAmount).HasPrecision(12, 0);

            e.HasOne(s => s.Expense)
                .WithMany(x => x.Shares)
                .HasForeignKey(s => s.ExpenseId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.Member)
                .WithMany(m => m.ExpenseShares)
                .HasForeignKey(s => s.MemberId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Settlement>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_settlements_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_settlements_payer_not_payee", "payer_id <> payee_id");
            });

            e.Property(s => s.Amount).HasPrecision(12, 0);
            e.Property(s => s.Note).HasMaxLength(255);

            e.HasIndex(s => new { s.RoomId, s.SettledAt });

            // room_id is stored (not derived via members) so settlements stay attributable
            // to a room once a member can belong to several rooms.
            e.HasOne(s => s.Room)
                .WithMany(r => r.Settlements)
                .HasForeignKey(s => s.RoomId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(s => s.Payer)
                .WithMany(m => m.SettlementsPaid)
                .HasForeignKey(s => s.PayerId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(s => s.Payee)
                .WithMany(m => m.SettlementsReceived)
                .HasForeignKey(s => s.PayeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
