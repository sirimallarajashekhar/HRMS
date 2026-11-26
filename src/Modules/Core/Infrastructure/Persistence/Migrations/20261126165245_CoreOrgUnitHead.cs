using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoreOrgUnitHead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "org_unit_head",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    org_unit_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_unit_head", x => x.id);
                    table.ForeignKey(
                        name: "FK_org_unit_head_org_unit_org_unit_id",
                        column: x => x.org_unit_id,
                        principalTable: "org_unit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_org_unit_head_org_unit_id",
                table: "org_unit_head",
                column: "org_unit_id");

            migrationBuilder.CreateIndex(
                name: "ux_org_unit_head_tenant_unit_from",
                table: "org_unit_head",
                columns: new[] { "tenant_id", "org_unit_id", "effective_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "org_unit_head");
        }
    }
}
