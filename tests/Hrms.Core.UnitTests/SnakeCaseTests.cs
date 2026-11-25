using FluentAssertions;
using Hrms.Persistence;

namespace Hrms.Core.UnitTests;

public class SnakeCaseTests
{
    [Theory]
    [InlineData("Widget", "widget")]
    [InlineData("LegalEntity", "legal_entity")]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("CreatedAt", "created_at")]
    [InlineData("IsDeleted", "is_deleted")]
    [InlineData("RowVersion", "row_version")]
    [InlineData("Id", "id")]
    [InlineData("CountryPackCode", "country_pack_code")]
    [InlineData("ISOCode", "iso_code")]
    [InlineData("Line1Address", "line1_address")]
    [InlineData("already_snake", "already_snake")]
    public void From_PascalCase_ReturnsSnakeCase(string input, string expected) =>
        SnakeCase.From(input).Should().Be(expected);

    [Fact]
    public void From_Empty_Throws() =>
        FluentActions.Invoking(() => SnakeCase.From(string.Empty)).Should().Throw<ArgumentException>();
}
