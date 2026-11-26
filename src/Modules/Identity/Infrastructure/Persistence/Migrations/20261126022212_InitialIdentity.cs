using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "permission",
                columns: table => new
                {
                    code = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    module = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission", x => x.code);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_system = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    requires_mfa = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    active_code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `code`, NULL)", stored: false)
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
                    table.PrimaryKey("PK_role", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "tenant_security_policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    min_password_length = table.Column<int>(type: "int", nullable: false),
                    require_uppercase = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    require_lowercase = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    require_digit = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    require_symbol = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    max_failed_attempts = table.Column<int>(type: "int", nullable: false),
                    lockout_minutes = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_tenant_security_policy", x => x.id);
                    table.CheckConstraint("ck_tenant_security_policy_attempts", "`max_failed_attempts` BETWEEN 3 AND 20");
                    table.CheckConstraint("ck_tenant_security_policy_lockout", "`lockout_minutes` BETWEEN 1 AND 1440");
                    table.CheckConstraint("ck_tenant_security_policy_min_length", "`min_password_length` BETWEEN 8 AND 128");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    display_name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    password_hash = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    security_stamp = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    failed_access_count = table.Column<int>(type: "int", nullable: false),
                    lockout_until = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    password_changed_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    must_change_password = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    employee_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    active_email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: true, computedColumnSql: "IF(`is_deleted` = 0, `email`, NULL)", stored: false)
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
                    table.PrimaryKey("PK_user_account", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "role_permission",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    role_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    permission_code = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
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
                    table.PrimaryKey("PK_role_permission", x => x.id);
                    table.ForeignKey(
                        name: "FK_role_permission_permission_permission_code",
                        column: x => x.permission_code,
                        principalTable: "permission",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permission_role_role_id",
                        column: x => x.role_id,
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_role_scope",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    user_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    role_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    scope_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    scope_value = table.Column<Guid>(type: "binary(16)", nullable: false),
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
                    table.PrimaryKey("PK_user_role_scope", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_role_scope_role_role_id",
                        column: x => x.role_id,
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_role_scope_user_account_user_id",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_permission_module",
                table: "permission",
                column: "module");

            migrationBuilder.CreateIndex(
                name: "ux_role_tenant_code",
                table: "role",
                columns: new[] { "tenant_id", "active_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permission_permission_code",
                table: "role_permission",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "IX_role_permission_role_id",
                table: "role_permission",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_role_permission_tenant_role_permission",
                table: "role_permission",
                columns: new[] { "tenant_id", "role_id", "permission_code", "active_flag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_security_policy_tenant_effective_from",
                table: "tenant_security_policy",
                columns: new[] { "tenant_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_user_account_tenant_email",
                table: "user_account",
                columns: new[] { "tenant_id", "active_email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_role_scope_role_id",
                table: "user_role_scope",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_role_scope_user_id",
                table: "user_role_scope",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_role_scope_tenant_grant",
                table: "user_role_scope",
                columns: new[] { "tenant_id", "user_id", "role_id", "scope_type", "scope_value", "active_flag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permission");

            migrationBuilder.DropTable(
                name: "tenant_security_policy");

            migrationBuilder.DropTable(
                name: "user_role_scope");

            migrationBuilder.DropTable(
                name: "permission");

            migrationBuilder.DropTable(
                name: "role");

            migrationBuilder.DropTable(
                name: "user_account");
        }
    }
}
