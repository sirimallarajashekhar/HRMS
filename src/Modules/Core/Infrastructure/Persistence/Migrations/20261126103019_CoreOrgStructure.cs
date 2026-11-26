using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoreOrgStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cost_centre",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_centre", x => x.id);
                    table.ForeignKey(
                        name: "FK_cost_centre_legal_entity_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    is_payroll_eligible = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_type", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "grade",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    rank = table.Column<int>(type: "int", nullable: false),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade", x => x.id);
                    table.CheckConstraint("ck_grade_rank", "`rank` BETWEEN 0 AND 10000");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "location",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    address_line1 = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    address_line2 = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    city = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    state = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    postal_code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    country_code = table.Column<string>(type: "char(2)", fixedLength: true, maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    time_zone_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    work_week_days = table.Column<int>(type: "int", nullable: false),
                    holiday_calendar_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location", x => x.id);
                    table.CheckConstraint("ck_location_work_week_days", "`work_week_days` BETWEEN 1 AND 127");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "org_unit_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    level = table.Column<int>(type: "int", nullable: false),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_unit_type", x => x.id);
                    table.CheckConstraint("ck_org_unit_type_level", "`level` BETWEEN 1 AND 20");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "designation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    grade_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_designation", x => x.id);
                    table.ForeignKey(
                        name: "FK_designation_grade_grade_id",
                        column: x => x.grade_id,
                        principalTable: "grade",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "geofence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    location_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    kind = table.Column<int>(type: "int", nullable: false),
                    center_latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    center_longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    radius_metres = table.Column<int>(type: "int", nullable: true),
                    polygon_geo_json = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
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
                    table.PrimaryKey("PK_geofence", x => x.id);
                    table.CheckConstraint("ck_geofence_kind", "`kind` IN (1, 2)");
                    table.CheckConstraint("ck_geofence_shape", "(`kind` = 1 AND `center_latitude` IS NOT NULL AND `center_longitude` IS NOT NULL AND `radius_metres` BETWEEN 1 AND 100000 AND `polygon_geo_json` IS NULL) OR (`kind` = 2 AND `polygon_geo_json` IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_geofence_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "org_unit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    org_unit_type_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tenant_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    updated_by = table.Column<Guid>(type: "binary(16)", nullable: true),
                    is_deleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_unit", x => x.id);
                    table.ForeignKey(
                        name: "FK_org_unit_org_unit_type_org_unit_type_id",
                        column: x => x.org_unit_type_id,
                        principalTable: "org_unit_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "org_unit_parent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    org_unit_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    parent_id = table.Column<Guid>(type: "binary(16)", nullable: true),
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
                    table.PrimaryKey("PK_org_unit_parent", x => x.id);
                    table.ForeignKey(
                        name: "FK_org_unit_parent_org_unit_org_unit_id",
                        column: x => x.org_unit_id,
                        principalTable: "org_unit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_org_unit_parent_org_unit_parent_id",
                        column: x => x.parent_id,
                        principalTable: "org_unit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_cost_centre_legal_entity_id",
                table: "cost_centre",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_cost_centre_tenant_legal_entity",
                table: "cost_centre",
                columns: new[] { "tenant_id", "legal_entity_id" });

            migrationBuilder.CreateIndex(
                name: "ux_cost_centre_tenant_code",
                table: "cost_centre",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_designation_grade_id",
                table: "designation",
                column: "grade_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_tenant_grade",
                table: "designation",
                columns: new[] { "tenant_id", "grade_id" });

            migrationBuilder.CreateIndex(
                name: "ux_designation_tenant_code",
                table: "designation",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_employee_type_tenant_code",
                table: "employee_type",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_geofence_location_id",
                table: "geofence",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_geofence_tenant_location",
                table: "geofence",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ux_grade_tenant_code",
                table: "grade",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_location_tenant_code",
                table: "location",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_org_unit_org_unit_type_id",
                table: "org_unit",
                column: "org_unit_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_org_unit_tenant_type",
                table: "org_unit",
                columns: new[] { "tenant_id", "org_unit_type_id" });

            migrationBuilder.CreateIndex(
                name: "ux_org_unit_tenant_code",
                table: "org_unit",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_org_unit_parent_org_unit_id",
                table: "org_unit_parent",
                column: "org_unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_org_unit_parent_parent_id",
                table: "org_unit_parent",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_org_unit_parent_tenant_parent_from",
                table: "org_unit_parent",
                columns: new[] { "tenant_id", "parent_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_org_unit_parent_tenant_unit_from",
                table: "org_unit_parent",
                columns: new[] { "tenant_id", "org_unit_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_org_unit_type_tenant_level",
                table: "org_unit_type",
                columns: new[] { "tenant_id", "level" });

            migrationBuilder.CreateIndex(
                name: "ux_org_unit_type_tenant_code",
                table: "org_unit_type",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cost_centre");

            migrationBuilder.DropTable(
                name: "designation");

            migrationBuilder.DropTable(
                name: "employee_type");

            migrationBuilder.DropTable(
                name: "geofence");

            migrationBuilder.DropTable(
                name: "org_unit_parent");

            migrationBuilder.DropTable(
                name: "grade");

            migrationBuilder.DropTable(
                name: "location");

            migrationBuilder.DropTable(
                name: "org_unit");

            migrationBuilder.DropTable(
                name: "org_unit_type");
        }
    }
}
