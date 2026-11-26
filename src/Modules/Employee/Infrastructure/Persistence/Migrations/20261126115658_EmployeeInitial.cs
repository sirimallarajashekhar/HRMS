using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Employee.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    joining_date = table.Column<DateOnly>(type: "date", nullable: false),
                    confirmation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: true),
                    active_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_employee", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_code_rule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    prefix = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    width = table.Column<int>(type: "int", nullable: false),
                    next_number = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_employee_code_rule", x => x.id);
                    table.CheckConstraint("ck_employee_code_rule_next_number", "`next_number` >= 1");
                    table.CheckConstraint("ck_employee_code_rule_width", "`width` BETWEEN 1 AND 12");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    event_type = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    payload = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    processed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    last_error = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_employee_outbox", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_consent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    consent_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    source = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    granted_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    withdrawn_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_employee_consent", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_consent_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_device",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    device_id = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    platform = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<int>(type: "int", nullable: false),
                    registered_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    active_device_id = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `device_id`, NULL)", stored: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_employee_device", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_device_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_document",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    document_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("PK_employee_document", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_document_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    location_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    org_unit_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    designation_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_type_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    grade_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    cost_centre_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    manager_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    probation_end = table.Column<DateOnly>(type: "date", nullable: true),
                    notice_period_days = table.Column<int>(type: "int", nullable: false),
                    reason = table.Column<int>(type: "int", nullable: false),
                    event_published_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_employee_job", x => x.id);
                    table.CheckConstraint("ck_employee_job_notice_period", "`notice_period_days` BETWEEN 0 AND 365");
                    table.ForeignKey(
                        name: "FK_employee_job_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_personal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    first_name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    middle_name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    gender = table.Column<int>(type: "int", nullable: false),
                    personal_email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phone = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_employee_personal", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_personal_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_separation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    type = table.Column<int>(type: "int", nullable: false),
                    notice_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    event_published_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_employee_separation", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_separation_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_statutory_id",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    id_type = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protected_value = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_employee_statutory_id", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_statutory_id_employee_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_joining_date",
                table: "employee",
                columns: new[] { "tenant_id", "joining_date" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_last_working_day",
                table: "employee",
                columns: new[] { "tenant_id", "last_working_day" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_record_tenant_code",
                table: "employee",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_employee_code_rule_tenant_legal_entity",
                table: "employee_code_rule",
                columns: new[] { "tenant_id", "legal_entity_id", "active_flag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_consent_employee_id",
                table: "employee_consent",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_consent_tenant_employee_type",
                table: "employee_consent",
                columns: new[] { "tenant_id", "employee_id", "consent_type" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_device_employee_id",
                table: "employee_device",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_device_tenant_employee",
                table: "employee_device",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_device_tenant_device_id",
                table: "employee_device",
                columns: new[] { "tenant_id", "active_device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_document_employee_id",
                table: "employee_document",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_document_tenant_employee",
                table: "employee_document",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_document_tenant_expires_on",
                table: "employee_document",
                columns: new[] { "tenant_id", "expires_on" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_job_employee_id",
                table: "employee_job",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_job_tenant_from_published",
                table: "employee_job",
                columns: new[] { "tenant_id", "effective_from", "event_published_at" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_job_tenant_legal_entity_from",
                table: "employee_job",
                columns: new[] { "tenant_id", "legal_entity_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_job_tenant_location_from",
                table: "employee_job",
                columns: new[] { "tenant_id", "location_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_job_tenant_manager_from",
                table: "employee_job",
                columns: new[] { "tenant_id", "manager_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_job_tenant_org_unit_from",
                table: "employee_job",
                columns: new[] { "tenant_id", "org_unit_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_job_tenant_employee_from",
                table: "employee_job",
                columns: new[] { "tenant_id", "employee_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_outbox_tenant_processed_occurred",
                table: "employee_outbox",
                columns: new[] { "tenant_id", "processed_at", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_personal_employee_id",
                table: "employee_personal",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_personal_tenant_name",
                table: "employee_personal",
                columns: new[] { "tenant_id", "last_name", "first_name" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_personal_tenant_employee",
                table: "employee_personal",
                columns: new[] { "tenant_id", "employee_id", "active_flag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_separation_employee_id",
                table: "employee_separation",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_separation_tenant_last_day_published",
                table: "employee_separation",
                columns: new[] { "tenant_id", "last_working_day", "event_published_at" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_separation_tenant_employee",
                table: "employee_separation",
                columns: new[] { "tenant_id", "employee_id", "active_flag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_statutory_id_employee_id",
                table: "employee_statutory_id",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ux_employee_statutory_id_tenant_employee_type",
                table: "employee_statutory_id",
                columns: new[] { "tenant_id", "employee_id", "id_type", "active_flag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_code_rule");

            migrationBuilder.DropTable(
                name: "employee_consent");

            migrationBuilder.DropTable(
                name: "employee_device");

            migrationBuilder.DropTable(
                name: "employee_document");

            migrationBuilder.DropTable(
                name: "employee_job");

            migrationBuilder.DropTable(
                name: "employee_outbox");

            migrationBuilder.DropTable(
                name: "employee_personal");

            migrationBuilder.DropTable(
                name: "employee_separation");

            migrationBuilder.DropTable(
                name: "employee_statutory_id");

            migrationBuilder.DropTable(
                name: "employee");
        }
    }
}
