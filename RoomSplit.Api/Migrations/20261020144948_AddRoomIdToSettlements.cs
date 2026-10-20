using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomSplit.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomIdToSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "room_id",
                table: "settlements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_settlements_room_id_settled_at",
                table: "settlements",
                columns: new[] { "room_id", "settled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_expenses_room_id_expense_date",
                table: "expenses",
                columns: new[] { "room_id", "expense_date" });

            // MySQL needs an index on expenses.room_id for its FK at all times (error 1553),
            // so the composite index must exist before the single-column one is dropped.
            migrationBuilder.DropIndex(
                name: "ix_expenses_room_id",
                table: "expenses");

            migrationBuilder.AddForeignKey(
                name: "fk_settlements_rooms_room_id",
                table: "settlements",
                column: "room_id",
                principalTable: "rooms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_settlements_rooms_room_id",
                table: "settlements");

            migrationBuilder.DropIndex(
                name: "ix_settlements_room_id_settled_at",
                table: "settlements");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_room_id",
                table: "expenses",
                column: "room_id");

            migrationBuilder.DropIndex(
                name: "ix_expenses_room_id_expense_date",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "room_id",
                table: "settlements");
        }
    }
}
