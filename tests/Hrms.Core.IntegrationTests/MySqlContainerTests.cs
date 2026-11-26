using Microsoft.AspNetCore.Hosting;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;
using Testcontainers.MySql;

namespace Hrms.Core.IntegrationTests;

/// <summary>One real MySQL 8.4 container shared by every test in this collection (starts in a few seconds).</summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4")
        .WithCommand("--character-set-server=utf8mb4", "--collation-server=utf8mb4_0900_ai_ci", "--default-time-zone=+00:00", "--max-connections=1000")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

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
