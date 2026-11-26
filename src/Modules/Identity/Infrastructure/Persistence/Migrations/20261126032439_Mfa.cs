using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Mfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "mfa_enabled",
                table: "user_account",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "mfa_enrolled_at",
                table: "user_account",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "mfa_last_used_step",
                table: "user_account",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mfa_secret_protected",
                table: "user_account",
                type: "varchar(400)",
                maxLength: 400,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_recovery_code",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    user_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    code_hash = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    used_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    active_flag = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`is_deleted` = 0, 1, NULL)", stored: false),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recovery_code", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_recovery_code_user_account_user_id",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_user_recovery_code_user_id",
                table: "user_recovery_code",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_recovery_code_tenant_user_hash",
                table: "user_recovery_code",
                columns: new[] { "tenant_id", "user_id", "code_hash", "active_flag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_recovery_code");

            migrationBuilder.DropColumn(
                name: "mfa_enabled",
                table: "user_account");

            migrationBuilder.DropColumn(
                name: "mfa_enrolled_at",
                table: "user_account");

            migrationBuilder.DropColumn(
                name: "mfa_last_used_step",
                table: "user_account");

            migrationBuilder.DropColumn(
                name: "mfa_secret_protected",
                table: "user_account");
        }
    }
}
