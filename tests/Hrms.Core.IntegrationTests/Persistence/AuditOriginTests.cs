using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Hrms.Audit.Persistence;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Org;
using Hrms.Http.Authorization;
using Hrms.Persistence.Auditing;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Persistence;

/// <summary>What the request context reports about a caller.</summary>
public sealed class AuditOriginTests
{
    private static AuditOrigin OriginOf(DefaultHttpContext? http) =>
        new HttpActorContext(new HttpContextAccessor { HttpContext = http }).Origin;

    private static DefaultHttpContext Request(string? subject = null)
    {
        var http = new DefaultHttpContext();
        if (subject is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HrmsClaims.Subject, subject)], "test"));
        }

        return http;
    }

    [Fact]
    public void Origin_WithoutARequest_IsTheSystem() => OriginOf(null).Should().Be(AuditOrigin.System);

    [Fact]
    public void Origin_OfAnUnauthenticatedRequest_IsAnonymous() => OriginOf(Request()).ActorType.Should().Be(AuditActorType.Anonymous);

    [Fact]
    public void Origin_OfAUser_NamesNoClient()
    {
        var origin = OriginOf(Request(Guid.CreateVersion7().ToString()));

        origin.ActorType.Should().Be(AuditActorType.User);
        origin.ClientId.Should().BeNull();
    }

    [Fact]
    public void Origin_OfAnIntegration_NamesItsClient()
    {
        var origin = OriginOf(Request("payroll-integration"));

        origin.ActorType.Should().Be(AuditActorType.Client);
        origin.ClientId.Should().Be("payroll-integration");
    }

    [Fact]
    public void Origin_CarriesTheAddressAndTheCorrelationId()
    {
        var http = Request();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        http.Items[AuditRequest.CorrelationIdItem] = "corr-42";

        var origin = OriginOf(http);

        origin.IpAddress.Should().Be("203.0.113.9");
        origin.CorrelationId.Should().Be("corr-42");
    }

    [Theory]
    [InlineData("  Fixing a typo  ", "Fixing a typo")]
    [InlineData("line one\r\nline two\t!", "line oneline two!")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Origin_CleansTheChangeReason(string? header, string? expected)
    {
        var http = Request();
        if (header is not null)
        {
            http.Request.Headers[AuditRequest.ReasonHeader] = header;
        }

        OriginOf(http).Reason.Should().Be(expected);
    }
}

/// <summary>The same through the real chain: token, tenant, endpoint, module context and MySQL.</summary>
[Collection(MySqlTestGroup.Name)]
public sealed class AuditRequestApiTests(OrgApiFixture fixture) : OrgApiTestBase(fixture), IClassFixture<OrgApiFixture>
{
    private async Task<List<AuditEntry>> HistoryAsync(TenantId tenant, Guid recordId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenant);
        var options = (DbContextOptions<AuditDbContext>)AuditDbContext
            .UseAuditMySql(new DbContextOptionsBuilder<AuditDbContext>(), ConnectionString)
            .Options;
        await using var context = new AuditDbContext(options, tenantContext, new FixedActor(null), TimeProvider.System);
        return await context.Entries.AsNoTracking().Where(e => e.RecordId == recordId).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task CreatingARecordThroughTheApi_AuditsTheSignedInUserAndTheReason()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/grades")
        {
            Content = JsonContent.Create(new { code = NewCode(), name = "Audited grade", rank = 3 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeAdmin);
        request.Headers.Add(AuditRequest.ReasonHeader, "Annual restructure");

        using var response = await Host.Client.SendAsync(request);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        var id = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
        var entry = (await HistoryAsync(Acme.TenantId, id)).Should().ContainSingle().Subject;
        entry.EntityType.Should().Be("grade");
        entry.Action.Should().Be(AuditAction.Created);
        entry.ActorType.Should().Be(AuditActorType.User);
        entry.ActorId.Should().NotBeNull();
        entry.Reason.Should().Be("Annual restructure");
        (await HistoryAsync(Globex.TenantId, id)).Should().BeEmpty("history belongs to the tenant that made the change");
    }
}
