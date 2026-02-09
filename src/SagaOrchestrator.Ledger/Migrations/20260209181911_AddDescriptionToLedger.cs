using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SagaOrchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class AddDescriptionToLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Reason",
                table: "LedgerEntries",
                newName: "Description");

            migrationBuilder.RenameIndex(
                name: "IX_LedgerEntries_ReferenceId",
                table: "LedgerEntries",
                newName: "IX_LedgerEntry_ReferenceId_Unique");

            migrationBuilder.AlterColumn<string>(
                name: "ReferenceId",
                table: "LedgerEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_AccountId",
                table: "LedgerEntries",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_AccountId_CreatedAt",
                table: "LedgerEntries",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntries_Accounts_AccountId",
                table: "LedgerEntries",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntries_Accounts_AccountId",
                table: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_AccountId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_AccountId_CreatedAt",
                table: "LedgerEntries");

            migrationBuilder.RenameColumn(
                name: "Description",
                table: "LedgerEntries",
                newName: "Reason");

            migrationBuilder.RenameIndex(
                name: "IX_LedgerEntry_ReferenceId_Unique",
                table: "LedgerEntries",
                newName: "IX_LedgerEntries_ReferenceId");

            migrationBuilder.AlterColumn<string>(
                name: "ReferenceId",
                table: "LedgerEntries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
