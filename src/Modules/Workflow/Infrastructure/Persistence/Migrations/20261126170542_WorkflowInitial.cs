using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "delegation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    delegator_user_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    delegate_user_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    workflow_key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_delegation", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_def",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    workflow_key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: true),
                    published_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    revision = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_workflow_def", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_instance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    definition_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    definition_version = table.Column<int>(type: "int", nullable: false),
                    workflow_key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    entity_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    entity_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    subject_employee_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    current_stage = table.Column<int>(type: "int", nullable: false),
                    round = table.Column<int>(type: "int", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    blocked_reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    completed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    start_fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    event_count = table.Column<int>(type: "int", nullable: false),
                    action_count = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_workflow_instance", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_instance_workflow_def_definition_id",
                        column: x => x.definition_id,
                        principalTable: "workflow_def",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_stage_def",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    definition_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    stage_order = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    approver_json = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    fallback_json = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    condition_json = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mode = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sla_hours = table.Column<int>(type: "int", nullable: true),
                    on_timeout = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    escalation_json = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
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
                    table.PrimaryKey("PK_workflow_stage_def", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_stage_def_workflow_def_definition_id",
                        column: x => x.definition_id,
                        principalTable: "workflow_def",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_action_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    instance_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    stage_order = table.Column<int>(type: "int", nullable: false),
                    round = table.Column<int>(type: "int", nullable: false),
                    action = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    actor_user_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    on_behalf_of_user_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    task_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    comment = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
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
                    table.PrimaryKey("PK_workflow_action_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_action_log_workflow_instance_instance_id",
                        column: x => x.instance_id,
                        principalTable: "workflow_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    instance_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    event_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    payload = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    processed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    last_error = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
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
                    table.PrimaryKey("PK_workflow_outbox", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_outbox_workflow_instance_instance_id",
                        column: x => x.instance_id,
                        principalTable: "workflow_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    instance_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    stage_order = table.Column<int>(type: "int", nullable: false),
                    round = table.Column<int>(type: "int", nullable: false),
                    assignee_user_id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    origin = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    due_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    overdue_handled_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    assignee_unavailable = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "binary(16)", nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_workflow_task", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_task_workflow_instance_instance_id",
                        column: x => x.instance_id,
                        principalTable: "workflow_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_delegation_tenant_delegate_ends",
                table: "delegation",
                columns: new[] { "tenant_id", "delegate_user_id", "ends_on" });

            migrationBuilder.CreateIndex(
                name: "ix_delegation_tenant_delegator_ends",
                table: "delegation",
                columns: new[] { "tenant_id", "delegator_user_id", "ends_on" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_action_log_instance_id",
                table: "workflow_action_log",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_action_log_tenant_instance_sequence",
                table: "workflow_action_log",
                columns: new[] { "tenant_id", "instance_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_def_tenant_key_status_from",
                table: "workflow_def",
                columns: new[] { "tenant_id", "workflow_key", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_def_tenant_key_version",
                table: "workflow_def",
                columns: new[] { "tenant_id", "workflow_key", "version", "active_flag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_definition_id",
                table: "workflow_instance",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instance_tenant_record",
                table: "workflow_instance",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instance_tenant_requester_status",
                table: "workflow_instance",
                columns: new[] { "tenant_id", "requester_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instance_tenant_status_created",
                table: "workflow_instance",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instance_tenant_subject",
                table: "workflow_instance",
                columns: new[] { "tenant_id", "subject_employee_id" });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_instance_tenant_key_entity",
                table: "workflow_instance",
                columns: new[] { "tenant_id", "workflow_key", "entity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_outbox_instance_id",
                table: "workflow_outbox",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_outbox_tenant_processed_next",
                table: "workflow_outbox",
                columns: new[] { "tenant_id", "processed_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_outbox_tenant_instance_sequence",
                table: "workflow_outbox",
                columns: new[] { "tenant_id", "instance_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_stage_def_definition_id",
                table: "workflow_stage_def",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_stage_def_tenant_def_order",
                table: "workflow_stage_def",
                columns: new[] { "tenant_id", "definition_id", "stage_order", "active_flag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_task_instance_id",
                table: "workflow_task",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_task_tenant_assignee_status_due",
                table: "workflow_task",
                columns: new[] { "tenant_id", "assignee_user_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_task_tenant_decided_by",
                table: "workflow_task",
                columns: new[] { "tenant_id", "decided_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_task_tenant_instance",
                table: "workflow_task",
                columns: new[] { "tenant_id", "instance_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_task_tenant_status_handled_due",
                table: "workflow_task",
                columns: new[] { "tenant_id", "status", "overdue_handled_at", "due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delegation");

            migrationBuilder.DropTable(
                name: "workflow_action_log");

            migrationBuilder.DropTable(
                name: "workflow_outbox");

            migrationBuilder.DropTable(
                name: "workflow_stage_def");

            migrationBuilder.DropTable(
                name: "workflow_task");

            migrationBuilder.DropTable(
                name: "workflow_instance");

            migrationBuilder.DropTable(
                name: "workflow_def");
        }
    }
}
