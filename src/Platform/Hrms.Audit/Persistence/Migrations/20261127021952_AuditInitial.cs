using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    entity_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    record_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    root_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    root_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    action = table.Column<int>(type: "int", nullable: false),
                    changes = table.Column<string>(type: "json", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    actor_type = table.Column<int>(type: "int", nullable: false),
                    actor_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    client_id = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ip_address = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    correlation_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => new { x.tenant_id, x.occurred_at, x.id });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_record",
                table: "audit_log",
                columns: new[] { "tenant_id", "entity_type", "record_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_root",
                table: "audit_log",
                columns: new[] { "tenant_id", "root_type", "root_id", "occurred_at", "id" });

            // EF cannot express partitioning. One partition per month (UTC) up to the end of 2027; p_future catches everything later, so a
            // write never fails for want of a partition while months are being added.
            migrationBuilder.Sql(
                """
                ALTER TABLE `audit_log` PARTITION BY RANGE COLUMNS(`occurred_at`) (
                    PARTITION p202611 VALUES LESS THAN ('2026-12-01'),
                    PARTITION p202612 VALUES LESS THAN ('2027-01-01'),
                    PARTITION p202701 VALUES LESS THAN ('2027-02-01'),
                    PARTITION p202702 VALUES LESS THAN ('2027-03-01'),
                    PARTITION p202703 VALUES LESS THAN ('2027-04-01'),
                    PARTITION p202704 VALUES LESS THAN ('2027-05-01'),
                    PARTITION p202705 VALUES LESS THAN ('2027-06-01'),
                    PARTITION p202706 VALUES LESS THAN ('2027-07-01'),
                    PARTITION p202707 VALUES LESS THAN ('2027-08-01'),
                    PARTITION p202708 VALUES LESS THAN ('2027-09-01'),
                    PARTITION p202709 VALUES LESS THAN ('2027-10-01'),
                    PARTITION p202710 VALUES LESS THAN ('2027-11-01'),
                    PARTITION p202711 VALUES LESS THAN ('2027-12-01'),
                    PARTITION p202712 VALUES LESS THAN ('2028-01-01'),
                    PARTITION p_future VALUES LESS THAN (MAXVALUE)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");
        }
    }
}
