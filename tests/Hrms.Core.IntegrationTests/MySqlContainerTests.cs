using Microsoft.AspNetCore.Hosting;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class MySqlTestGroup : ICollectionFixture<MySqlFixture>
{
    public const string Name = "mysql";
}

[Collection(MySqlTestGroup.Name)]
public class MySqlContainerTests(MySqlFixture mysql, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Container_AcceptsConnectionsAndRunsQueries()
    {
        await using var connection = new MySqlConnection(mysql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT 1", connection);

        var result = await command.ExecuteScalarAsync();

        Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
    }

    [Fact]
    public async Task Container_UsesUtf8mb4AndUtcByDefault()
    {
        await using var connection = new MySqlConnection(mysql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT @@character_set_server, @@time_zone", connection);
        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("utf8mb4");
        reader.GetString(1).Should().Be("+00:00");
    }

    [Fact]
    public async Task Ready_WithReachableMySql_Returns200()
    {
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Mysql", mysql.ConnectionString));
        using var client = configured.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
