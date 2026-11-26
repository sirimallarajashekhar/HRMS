using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Employee;

[Collection(MySqlTestGroup.Name)]
public class EmployeeRecordsApiTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    // --- statutory ids -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StatutoryId_IsStoredEncrypted_ShownMasked_AndRevealedOnlyWithThePermission()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var path = $"/employees/{Id(employee)}/statutory-ids/pan";

        using var set = await PutAsync(path, new { value = "abcde 1234 f" }, Acme.HrAdmin);
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var masked = await JsonAsync(set);
        using var revealedResponse = await GetAsync($"/employees/{Id(employee)}/statutory-ids", Acme.HrSensitive);
        using var maskedList = await GetAsync($"/employees/{Id(employee)}/statutory-ids", Acme.HrAdmin);

        masked.GetProperty("idType").GetString().Should().Be("PAN");
        masked.GetProperty("value").GetString().Should().NotContain("ABCDE").And.EndWith("34F").And.HaveLength(10);
        (await JsonAsync(revealedResponse))[0].GetProperty("value").GetString().Should().Be("ABCDE1234F");
        (await JsonAsync(maskedList))[0].GetProperty("value").GetString().Should().NotContain("ABCDE");

        await using var db = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var stored = await db.StatutoryIds.AsNoTracking().SingleAsync(s => s.EmployeeId == Id(employee));
        stored.ProtectedValue.Should().StartWith("v1.").And.NotContain("ABCDE1234F");
    }

    [Fact]
    public async Task StatutoryId_CopiedToAnotherEmployee_DoesNotDecrypt()
    {
        var first = await CreateEmployeeAsync(Acme);
        var second = await CreateEmployeeAsync(Acme);
        using (await PutAsync($"/employees/{Id(first)}/statutory-ids/PAN", new { value = "ABCDE1234F" }, Acme.HrAdmin))
        {
        }

        using (await PutAsync($"/employees/{Id(second)}/statutory-ids/PAN", new { value = "ZZZZZ9999Z" }, Acme.HrAdmin))
        {
        }

        await using (var db = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId))
        {
            var cipher = await db.StatutoryIds.AsNoTracking().Where(s => s.EmployeeId == Id(first)).Select(s => s.ProtectedValue).SingleAsync();
            var target = await db.StatutoryIds.SingleAsync(s => s.EmployeeId == Id(second));
            target.Replace(cipher);
            await db.SaveChangesAsync();
        }

        using var response = await GetAsync($"/employees/{Id(second)}/statutory-ids", Acme.HrSensitive);

        (await JsonAsync(response))[0].GetProperty("value").GetString().Should().Be("(unreadable)", "the ciphertext is bound to the employee it was made for");
    }

    [Fact]
    public async Task StatutoryId_ReplacesTheValue_RejectsBadOnes_AndCanBeRemoved()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var path = $"/employees/{Id(employee)}/statutory-ids";

        using var first = await PutAsync($"{path}/PAN", new { value = "ABCDE1234F" }, Acme.HrSensitive);
        using var replaced = await PutAsync($"{path}/PAN", new { value = "FGHIJ5678K" }, Acme.HrSensitive);
        using var tooShort = await PutAsync($"{path}/PAN", new { value = "AB" }, Acme.HrAdmin);
        using var badType = await PutAsync($"{path}/not-valid", new { value = "ABCDE1234F" }, Acme.HrAdmin);
        using var list = await GetAsync(path, Acme.HrSensitive);
        using var removed = await DeleteAsync($"{path}/PAN", Acme.HrAdmin);
        using var removedAgain = await DeleteAsync($"{path}/PAN", Acme.HrAdmin);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(list)).GetArrayLength().Should().Be(1);
        (await JsonAsync(list))[0].GetProperty("value").GetString().Should().Be("FGHIJ5678K");
        ErrorCode(await JsonAsync(tooShort)).Should().Be("employee.statutory_id.value_rejected");
        ErrorCode(await JsonAsync(badType)).Should().Be("employee.statutory_id.type_invalid");
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        removedAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- documents -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Document_CanBeAddedChangedListedAndRemoved_AndKnowsWhenItExpired()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var path = $"/employees/{Id(employee)}/documents";

        using var created = await PostAsync(path, new { documentType = "visa", title = "Work visa", fileKey = "docs/visa-1.pdf", issuedOn = "2024-01-01", expiresOn = Iso(Today.AddDays(-1)) }, Acme.HrAdmin);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var document = await JsonAsync(created);
        using var updated = await PutAsync($"{path}/{Id(document)}", new { documentType = "VISA", title = "Work visa (renewed)", issuedOn = "2026-01-01", expiresOn = Iso(Today.AddDays(400)), rowVersion = Version(document) }, Acme.HrAdmin);
        using var stale = await PutAsync($"{path}/{Id(document)}", new { documentType = "VISA", title = "again", rowVersion = Version(document) }, Acme.HrAdmin);
        using var list = await GetAsync(path, Acme.HrAdmin);
        using var removed = await DeleteAsync($"{path}/{Id(document)}", Acme.HrAdmin);

        document.GetProperty("isExpired").GetBoolean().Should().BeTrue();
        document.GetProperty("documentType").GetString().Should().Be("VISA");
        (await JsonAsync(updated)).GetProperty("isExpired").GetBoolean().Should().BeFalse();
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonAsync(list)).GetArrayLength().Should().Be(1);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("", "Title", null)]
    [InlineData("ID", "", null)]
    [InlineData("ID", "Passport", "../secret")]
    public async Task Document_WithBadDetails_Is422(string type, string title, string? key)
    {
        var employee = await CreateEmployeeAsync(Acme);

        using var response = await PostAsync($"/employees/{Id(employee)}/documents", new { documentType = type, title, fileKey = key }, Acme.HrAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // --- devices -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Device_IsRegistered_Revoked_AndFreedByDeletion()
    {
        var one = await CreateEmployeeAsync(Acme);
        var two = await CreateEmployeeAsync(Acme);
        var deviceId = $"dev-{Guid.NewGuid():N}";

        using var registered = await PostAsync($"/employees/{Id(one)}/devices", new { deviceId, platform = "android", name = "Pixel" }, Acme.HrAdmin);
        registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var device = await JsonAsync(registered);
        using var taken = await PostAsync($"/employees/{Id(two)}/devices", new { deviceId, platform = "ios" }, Acme.HrAdmin);
        using var revoked = await PostAsync($"/employees/{Id(one)}/devices/{Id(device)}/revoke", null, Acme.HrAdmin);
        using var revokedAgain = await PostAsync($"/employees/{Id(one)}/devices/{Id(device)}/revoke", null, Acme.HrAdmin);
        using var deleted = await DeleteAsync($"/employees/{Id(one)}/devices/{Id(device)}", Acme.HrAdmin);
        using var reused = await PostAsync($"/employees/{Id(two)}/devices", new { deviceId, platform = "ios" }, Acme.HrAdmin);

        device.GetProperty("status").GetString().Should().Be("active");
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(taken)).Should().Be("employee.device.device_taken");
        (await JsonAsync(revoked)).GetProperty("status").GetString().Should().Be("revoked");
        revokedAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        reused.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Device_WithAnUnknownPlatform_Is422()
    {
        var employee = await CreateEmployeeAsync(Acme);

        using var response = await PostAsync($"/employees/{Id(employee)}/devices", new { deviceId = "x1", platform = "toaster" }, Acme.HrAdmin);

        ErrorCode(await JsonAsync(response)).Should().Be("employee.device.platform_invalid");
    }

    // --- consents ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Consent_IsGrantedAndWithdrawnOnce_AndTheHistoryStays()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var path = $"/employees/{Id(employee)}/consents";

        using var granted = await PostAsync(path, new { consentType = "location_tracking", version = "v2", source = "mobile-app" }, Acme.HrAdmin);
        granted.StatusCode.Should().Be(HttpStatusCode.Created, await granted.Content.ReadAsStringAsync());
        var consent = await JsonAsync(granted);
        using var withdrawn = await PostAsync($"{path}/{Id(consent)}/withdraw", null, Acme.HrAdmin);
        using var again = await PostAsync($"{path}/{Id(consent)}/withdraw", null, Acme.HrAdmin);
        using var list = await GetAsync(path, Acme.HrAdmin);

        consent.GetProperty("consentType").GetString().Should().Be("LOCATION_TRACKING");
        (await JsonAsync(withdrawn)).GetProperty("withdrawnAt").ValueKind.Should().Be(JsonValueKind.String);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonAsync(list)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Profile_ShowsTheTabsTheCallerMaySee_AndNullForTheRest()
    {
        var employee = await CreateEmployeeAsync(Acme);
        using (await PostAsync($"/employees/{Id(employee)}/consents", new { consentType = "PAYROLL", version = "1" }, Acme.HrAdmin))
        {
        }

        var full = await ProfileAsync(Id(employee), Acme.HrAdmin);
        var viewer = await ProfileAsync(Id(employee), Acme.Viewer);

        full.GetProperty("consents").GetArrayLength().Should().Be(1);
        full.GetProperty("documents").GetArrayLength().Should().Be(0, "no documents is an empty list, not hidden");
        full.GetProperty("personal").ValueKind.Should().Be(JsonValueKind.Object);
        viewer.GetProperty("summary").GetProperty("id").GetGuid().Should().Be(Id(employee));
        foreach (var tab in new[] { "personal", "statutoryIds", "documents", "devices", "consents", "separation" })
        {
            viewer.GetProperty(tab).ValueKind.Should().Be(JsonValueKind.Null, $"the viewer lacks the permission for {tab}");
        }

        viewer.GetProperty("jobHistory").GetArrayLength().Should().Be(1);
    }

    // --- code rules -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CodeRule_CanBeReadAndChanged_ButTheNumberNeverGoesBack()
    {
        var entity = await Fixture.SeedLegalEntityAsync(Acme.Tenant.TenantId, $"RULE-{Guid.NewGuid():N}"[..14], "IN", "INR", "IN");
        using var created = await PutAsync($"/employee-code-rules/{entity}", new { prefix = "r-", width = 4, nextNumber = 10 }, Acme.HrAdmin);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var rule = await JsonAsync(created);

        using var noVersion = await PutAsync($"/employee-code-rules/{entity}", new { prefix = "R2-", width = 4, nextNumber = 20 }, Acme.HrAdmin);
        using var backwards = await PutAsync($"/employee-code-rules/{entity}", new { prefix = "R-", width = 4, nextNumber = 5, rowVersion = Version(rule) }, Acme.HrAdmin);
        using var forwards = await PutAsync($"/employee-code-rules/{entity}", new { prefix = "R2-", width = 6, nextNumber = 20, rowVersion = Version(rule) }, Acme.HrAdmin);
        using var read = await GetAsync($"/employee-code-rules/{entity}", Acme.HrAdmin);
        using var unknown = await PutAsync($"/employee-code-rules/{Guid.NewGuid()}", new { prefix = "X", width = 4, nextNumber = 1 }, Acme.HrAdmin);

        rule.GetProperty("example").GetString().Should().Be("R-0010");
        ErrorCode(await JsonAsync(noVersion)).Should().Be("employee.row_version_required");
        ErrorCode(await JsonAsync(backwards)).Should().Be("employee.code_rule.next_number_invalid");
        forwards.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(read)).GetProperty("example").GetString().Should().Be("R2-000020");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- masters protected from deletion ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CoreMasters_UsedByAnEmployee_CannotBeDeleted()
    {
        var designation = await CreateMasterAsync("/designations", new { code = $"D{Guid.NewGuid():N}"[..8], name = "Only here" });
        var unit = await CreateMasterAsync("/org-units", new { code = $"U{Guid.NewGuid():N}"[..8], name = "Only unit", orgUnitTypeId = Acme.DepartmentTypeId, parentId = Acme.RootUnitId, effectiveFrom = "2020-01-01" });
        var location = await CreateMasterAsync("/locations", new { code = $"L{Guid.NewGuid():N}"[..8], name = "Only site", addressLine1 = "1 Road", city = "Pune", countryCode = "IN", timeZoneId = "Asia/Kolkata" });
        var body = new
        {
            joiningDate = "2026-01-01",
            personal = new { firstName = "Used", lastName = "Masters", dateOfBirth = "1990-05-17", gender = "other" },
            job = new
            {
                legalEntityId = Acme.LegalEntityId,
                locationId = location,
                orgUnitId = unit,
                designationId = designation,
                employeeTypeId = Acme.EmployeeTypeId,
                noticePeriodDays = 30,
            },
        };
        await CreateEmployeeAsync(Acme, body);

        using var designationDelete = await DeleteAsync($"/designations/{designation}", Acme.OrgAdmin);
        using var unitDelete = await DeleteAsync($"/org-units/{unit}", Acme.OrgAdmin);
        using var locationDelete = await DeleteAsync($"/locations/{location}", Acme.OrgAdmin);

        designationDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(designationDelete)).Should().Be("core.designation.in_use");
        ErrorCode(await JsonAsync(unitDelete)).Should().Be("core.org_unit.in_use");
        ErrorCode(await JsonAsync(locationDelete)).Should().Be("core.location.in_use");
    }

    // --- permissions ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Endpoints_NeedTheirPermission()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var id = Id(employee);

        (await GetAsync("/employees", null)).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        (await GetAsync("/employees", Acme.Nobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync($"/employees/{id}/profile", Acme.Nobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync("/employees", CreateBody(Acme), Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync($"/employees/{id}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme), rowVersion = 1 }, Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync($"/employees/{id}/confirm", new { rowVersion = 1 }, Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PutAsync($"/employees/{id}/separation", new { type = "other", lastWorkingDay = "2026-12-31" }, Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PutAsync($"/employees/{id}/statutory-ids/PAN", new { value = "ABCDE1234F" }, Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync($"/employees/{id}/statutory-ids", Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync($"/employees/{id}/documents", Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync($"/employees/{id}/devices", new { deviceId = "d", platform = "web" }, Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync($"/employees/{id}/consents", new { consentType = "X", version = "1" }, Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync("/employee-code-rules", Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync($"/employees/{id}/profile", Acme.Viewer)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- tenant isolation -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnotherTenant_CannotSeeOrChangeAnEmployee_OrAnythingHangingOffOne()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var id = Id(employee);
        using (await PutAsync($"/employees/{id}/statutory-ids/PAN", new { value = "ABCDE1234F" }, Acme.HrAdmin))
        {
        }

        var responses = new[]
        {
            await GetAsync($"/employees/{id}/profile", Globex.HrAdmin),
            await GetAsync($"/employees/{id}/jobs", Globex.HrAdmin),
            await PutAsync($"/employees/{id}/personal", new { personal = new { firstName = "X", lastName = "Y", dateOfBirth = "1990-05-17", gender = "female" }, rowVersion = 1 }, Globex.HrAdmin),
            await PostAsync($"/employees/{id}/confirm", new { rowVersion = Version(employee) }, Globex.HrAdmin),
            await PostAsync($"/employees/{id}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Globex), rowVersion = Version(employee) }, Globex.HrAdmin),
            await PutAsync($"/employees/{id}/separation", new { type = "other", lastWorkingDay = "2026-12-31" }, Globex.HrAdmin),
            await GetAsync($"/employees/{id}/statutory-ids", Globex.HrSensitive),
            await PutAsync($"/employees/{id}/statutory-ids/PAN", new { value = "HACKED12345" }, Globex.HrAdmin),
            await GetAsync($"/employees/{id}/documents", Globex.HrAdmin),
            await PostAsync($"/employees/{id}/devices", new { deviceId = "spy", platform = "web" }, Globex.HrAdmin),
            await PostAsync($"/employees/{id}/consents", new { consentType = "X", version = "1" }, Globex.HrAdmin),
        };

        responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.NotFound, "an employee of another tenant simply does not exist");
        using var list = await GetAsync($"/employees?search={employee.GetProperty("code").GetString()}&status=all", Globex.HrAdmin);
        (await JsonAsync(list)).GetProperty("total").GetInt64().Should().Be(0);
        var intact = await ProfileAsync(id, Acme.HrSensitive);
        intact.GetProperty("statutoryIds")[0].GetProperty("value").GetString().Should().Be("ABCDE1234F");
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task AnEmployeeOfAnotherTenant_CannotBeNamedAsManagerOrMaster()
    {
        var globexBoss = await CreateEmployeeAsync(Globex);

        using var response = await PostAsync("/employees", CreateBody(Acme, manager: Id(globexBoss)), Acme.HrAdmin);

        ErrorCode(await JsonAsync(response)).Should().Be("employee.job.manager_not_found");
    }

    // --- audit actor -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Changes_AreStampedWithTheSignedInUser()
    {
        var employee = await CreateEmployeeAsync(Acme);
        using var meResponse = await Host.SendAsync(HttpMethod.Get, "/me", Acme.HrAdmin);
        var sub = (await JsonAsync(meResponse)).GetProperty("sub").GetGuid();

        await using var db = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var stored = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == Id(employee));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.EmployeeId == Id(employee));

        stored.CreatedBy.Should().Be(sub);
        job.CreatedBy.Should().Be(sub);
    }

    private async Task<Guid> CreateMasterAsync(string path, object body)
    {
        using var response = await PostAsync(path, body, Acme.OrgAdmin);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, json.ToString());
        return Id(json);
    }
}
