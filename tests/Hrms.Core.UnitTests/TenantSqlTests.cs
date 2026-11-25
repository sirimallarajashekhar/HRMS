using FluentAssertions;
using Hrms.Persistence.Sql;

namespace Hrms.Core.UnitTests;

public class TenantSqlTests
{
    [Theory]
    [InlineData("SELECT id FROM widget WHERE tenant_id = @TenantId")]
    [InlineData("select * from widget w where w.tenant_id=@TenantId and w.code=@Code")]
    [InlineData("SELECT * FROM widget w WHERE @TenantId = w.tenant_id")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = @TenantId;")]
    [InlineData("SELECT * FROM widget w JOIN other o ON o.tenant_id = w.tenant_id AND w.tenant_id = @TenantId")]
    [InlineData("SELECT * FROM `widget` WHERE `tenant_id` = @TenantId")]
    [InlineData("SELECT * FROM `widget` w WHERE `w`.`tenant_id` = @TenantId")]
    [InlineData("UPDATE widget SET name = @Name WHERE tenant_id = @TenantId AND id = @Id")]
    [InlineData("DELETE FROM widget WHERE tenant_id = @TenantId AND id = @Id")]
    [InlineData("INSERT INTO widget (id, tenant_id, code) VALUES (@Id, @TenantId, @Code)")]
    [InlineData("INSERT INTO widget (id, tenant_id, code) VALUES (@Id, @TenantId, @Code) ON DUPLICATE KEY UPDATE code = @Code")]
    [InlineData("WITH x AS (SELECT * FROM widget WHERE tenant_id = @TenantId) SELECT * FROM x")]
    [InlineData("SELECT 1 FROM widget WHERE tenant_id = @TenantId -- trailing comment")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = @TenantId AND name = 'a;b'")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = @TenantId AND name = 'it''s -- not a comment'")]
    public void Validate_StatementFilteredByTenant_IsAccepted(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().NotThrow();

    [Theory]
    [InlineData("SELECT * FROM widget")]
    [InlineData("SELECT * FROM widget WHERE code = @Code")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = 'abc'")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = @OtherTenantId")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = @TenantIdX")]
    [InlineData("SELECT * FROM widget WHERE my_tenant_id = @TenantId")]
    [InlineData("SELECT * FROM widget WHERE tenant_id <> @TenantId")]
    [InlineData("SELECT * FROM widget WHERE tenant_id != @TenantId")]
    [InlineData("SELECT * FROM widget WHERE tenant_id >= @TenantId")]
    [InlineData("SELECT * FROM widget -- WHERE tenant_id = @TenantId")]
    [InlineData("SELECT * FROM widget # WHERE tenant_id = @TenantId")]
    [InlineData("SELECT * FROM widget /* WHERE tenant_id = @TenantId */")]
    [InlineData("SELECT * FROM widget WHERE name = 'tenant_id = @TenantId'")]
    [InlineData("SELECT * FROM widget WHERE name = \"tenant_id = @TenantId\"")]
    [InlineData("UPDATE widget SET name = @Name WHERE id = @Id")]
    [InlineData("DELETE FROM widget WHERE id = @Id")]
    public void Validate_StatementWithoutARealTenantPredicate_IsRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*Unsafe tenant SQL*");

    [Theory]
    [InlineData("SELECT 1 FROM widget WHERE tenant_id = @TenantId; DELETE FROM widget")]
    [InlineData("SELECT 1 FROM widget WHERE tenant_id = @TenantId; SELECT 2 FROM widget WHERE tenant_id = @TenantId")]
    public void Validate_MoreThanOneStatement_IsRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*one statement*");

    [Theory]
    [InlineData("DROP TABLE widget")]
    [InlineData("TRUNCATE TABLE widget")]
    [InlineData("ALTER TABLE widget ADD x INT")]
    [InlineData("CREATE TABLE x (tenant_id INT)")]
    [InlineData("CALL do_things(@TenantId)")]
    [InlineData("SET @x = 1")]
    public void Validate_NonDataStatements_AreRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*Only SELECT*");

    [Theory]
    [InlineData("UPDATE widget SET tenant_id = @TenantId WHERE tenant_id = @TenantId")]
    [InlineData("UPDATE widget SET name = @Name, tenant_id = @Other WHERE tenant_id = @TenantId")]
    [InlineData("UPDATE widget w SET w.`tenant_id` = @Other WHERE w.tenant_id = @TenantId")]
    public void Validate_UpdateThatReassignsTheTenant_IsRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*immutable*");

    [Theory]
    [InlineData("INSERT INTO widget (id, code) VALUES (@Id, @Code)")]
    [InlineData("INSERT INTO widget (id, tenant_id) VALUES (@Id, @Other)")]
    [InlineData("INSERT INTO widget (id, code) VALUES (@Id, 'tenant_id @TenantId')")]
    public void Validate_InsertThatDoesNotSetTheTenantFromTheContext_IsRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*INSERT must set tenant_id*");

    [Fact]
    public void Validate_InsertThatRewritesTheTenantOnDuplicateKey_IsRejected() =>
        FluentActions.Invoking(() => TenantSql.Validate(
                "INSERT INTO widget (id, tenant_id) VALUES (@Id, @TenantId) ON DUPLICATE KEY UPDATE tenant_id = @Other"))
            .Should().Throw<ArgumentException>().WithMessage("*immutable*");

    [Theory]
    [InlineData("SELECT /*! 1 */ FROM widget WHERE tenant_id = @TenantId")]
    [InlineData("SELECT /*+ INDEX(widget x) */ * FROM widget WHERE tenant_id = @TenantId")]
    public void Validate_ExecutableOrHintComments_AreRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*comments*");

    [Theory]
    [InlineData("SELECT * FROM widget WHERE name = 'abc AND tenant_id = @TenantId")]
    [InlineData("SELECT * FROM widget WHERE tenant_id = @TenantId /* never closed")]
    public void Validate_UnterminatedLiteralOrComment_IsRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>().WithMessage("*Unterminated*");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyStatement_IsRejected(string sql) =>
        FluentActions.Invoking(() => TenantSql.Validate(sql)).Should().Throw<ArgumentException>();

    [Fact]
    public void Validate_DashDashWithoutASpace_IsNotACommentInMySql() =>
        // In MySQL "--1" is arithmetic, not a comment, so a predicate after it is still code and still counts.
        FluentActions.Invoking(() => TenantSql.Validate("SELECT 1--1 FROM widget WHERE tenant_id = @TenantId")).Should().NotThrow();
}
