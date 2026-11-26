using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hrms.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApproverScopeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_user_role_scope_tenant_scope",
                table: "user_role_scope",
                columns: new[] { "tenant_id", "scope_type", "scope_value" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_role_scope_tenant_scope",
                table: "user_role_scope");
        }
    }
}
