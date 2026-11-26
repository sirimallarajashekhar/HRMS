using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Employee;

[Collection(MySqlTestGroup.Name)]
public class EmployeeLifecycleApiTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    // --- create ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsCreated_WithAGeneratedCode_AndTheProfileHoldsEverything()
    {
        using var response = await PostAsync("/employees", CreateBody(Acme, "Meera", "Iyer"), Acme.HrAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var summary = await JsonAsync(response);
        summary.GetProperty("code").GetString().Should().StartWith("EMP").And.HaveLength(8);
        summary.GetProperty("displayName").GetString().Should().Be("Meera Iyer");
        summary.GetProperty("status").GetString().Should().Be("active");
        summary.GetProperty("joiningDate").GetString().Should().Be("2026-01-01");
        response.Headers.Location!.ToString().Should().EndWith($"/employees/{Id(summary)}");

        var profile = await ProfileAsync(Id(summary), Acme.HrAdmin);
        profile.GetProperty("summary").GetProperty("job").GetProperty("orgUnitId").GetGuid().Should().Be(Acme.DepartmentAId);
        profile.GetProperty("personal").GetProperty("lastName").GetString().Should().Be("Iyer");
        profile.GetProperty("jobHistory").GetArrayLength().Should().Be(1);
        profile.GetProperty("jobHistory")[0].GetProperty("reason").GetString().Should().Be("joining");
        profile.GetProperty("separation").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_HandsOutConsecutiveCodes_PerLegalEntity()
    {
        var a1 = await CreateEmployeeAsync(Acme, CreateBody(Acme, legalEntity: Acme.SecondLegalEntityId));
        var a2 = await CreateEmployeeAsync(Acme, CreateBody(Acme, legalEntity: Acme.SecondLegalEntityId));
        var other = await CreateEmployeeAsync(Acme, CreateBody(Acme, legalEntity: Acme.LegalEntityId));

        var first = a1.GetProperty("code").GetString()!;
        var second = a2.GetProperty("code").GetString()!;
        first.Should().StartWith("ENT");
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        long.Parse(second[3..], culture).Should().Be(long.Parse(first[3..], culture) + 1);
        other.GetProperty("code").GetString().Should().StartWith("EMP");
    }

    [Fact]
    public async Task Create_ForALegalEntityWithoutACodeRule_Is409()
    {
        var bare = await Fixture.SeedLegalEntityAsync(Acme.Tenant.TenantId, "ACME-BARE", "IN", "INR", "IN");

        using var response = await PostAsync("/employees", CreateBody(Acme, legalEntity: bare), Acme.HrAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(response)).Should().Be("employee.code_rule.missing");
    }

    [Fact]
    public async Task Create_InParallel_NeverDuplicatesACode()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            using var response = await PostAsync("/employees", CreateBody(Acme, $"Parallel{i}", "Case", legalEntity: Acme.ForeignLegalEntityId), Acme.HrAdmin);
            return (response.StatusCode, Body: await JsonAsync(response));
        })));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created, string.Join(" | ", responses.Select(r => r.Body.ToString())));
        var codes = responses.Select(r => r.Body.GetProperty("code").GetString()).ToList();
        codes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Create_WritesTheCreatedEventToTheOutbox_InTheSameTransaction()
    {
        var summary = await CreateEmployeeAsync(Acme);

        await using var db = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var events = await db.Outbox.AsNoTracking().Where(m => m.EventType == "EmployeeCreated" && m.Payload.Contains(Id(summary).ToString())).ToListAsync();
        events.Should().ContainSingle();
        events[0].ProcessedAt.Should().BeNull();
        events[0].Payload.Should().NotContain("Asha").And.NotContain("example.com", "events carry ids, never personal data");
    }

    [Fact]
    public async Task Create_ThatFails_LeavesNoEmployeeAndNoEvent()
    {
        await using var before = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var employees = await before.Employees.CountAsync();
        var outbox = await before.Outbox.CountAsync();
        var rule = await before.CodeRules.AsNoTracking().SingleAsync(r => r.LegalEntityId == Acme.LegalEntityId);

        var bad = new
        {
            joiningDate = "2026-01-01",
            personal = new { firstName = "", lastName = "Rao", dateOfBirth = "1990-05-17", gender = "female" },
            job = JobBody(Acme),
        };
        using var response = await PostAsync("/employees", bad, Acme.HrAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await using var after = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        (await after.Employees.CountAsync()).Should().Be(employees);
        (await after.Outbox.CountAsync()).Should().Be(outbox);
        (await after.CodeRules.AsNoTracking().SingleAsync(r => r.LegalEntityId == Acme.LegalEntityId)).NextNumber.Should().Be(rule.NextNumber, "no code is spent on a request that is refused");
    }

    public static TheoryData<string, string> BadRequests => new()
    {
        { "personal first name missing", "employee.personal.name_invalid" },
        { "gender unknown", "employee.personal.gender_invalid" },
        { "date of birth in the future", "employee.personal.date_of_birth_invalid" },
        { "email malformed", "employee.personal.email_invalid" },
        { "location unknown", "employee.job.location_not_found" },
        { "org unit unknown", "employee.job.org_unit_not_found" },
        { "notice period too long", "employee.job.notice_period_invalid" },
        { "manager unknown", "employee.job.manager_not_found" },
        { "legal entity unknown", "employee.job.legal_entity_not_found" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Create_WithABadRequest_Is422WithAStableCode(string what, string code)
    {
        var job = what switch
        {
            "location unknown" => JobBody(Acme, location: Guid.NewGuid()),
            "org unit unknown" => JobBody(Acme, unit: Guid.NewGuid()),
            "notice period too long" => JobBody(Acme, notice: 999),
            "manager unknown" => JobBody(Acme, manager: Guid.NewGuid()),
            "legal entity unknown" => JobBody(Acme, legalEntity: Guid.NewGuid()),
            _ => JobBody(Acme),
        };
        var personal = what switch
        {
            "personal first name missing" => new { firstName = " ", lastName = "Rao", dateOfBirth = "1990-05-17", gender = "female", personalEmail = (string?)null },
            "gender unknown" => new { firstName = "A", lastName = "Rao", dateOfBirth = "1990-05-17", gender = "robot", personalEmail = (string?)null },
            "date of birth in the future" => new { firstName = "A", lastName = "Rao", dateOfBirth = "2999-01-01", gender = "female", personalEmail = (string?)null },
            "email malformed" => new { firstName = "A", lastName = "Rao", dateOfBirth = "1990-05-17", gender = "female", personalEmail = (string?)"nope" },
            _ => new { firstName = "A", lastName = "Rao", dateOfBirth = "1990-05-17", gender = "female", personalEmail = (string?)null },
        };

        using var response = await PostAsync("/employees", new { joiningDate = "2026-01-01", personal, job }, Acme.HrAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be(code);
    }

    [Fact]
    public async Task Create_WithAnInactiveOrForeignMaster_Is422()
    {
        using var foreign = await PostAsync("/employees", CreateBody(Acme, location: Globex.LocationId), Acme.HrAdmin);

        // A location of its own, so other tests that share the fixture keep theirs active.
        using var made = await PostAsync("/locations", new { code = $"X{Guid.NewGuid():N}"[..8], name = "Closed site", addressLine1 = "1 Road", city = "Pune", countryCode = "IN", timeZoneId = "Asia/Kolkata" }, Acme.OrgAdmin);
        var site = await JsonAsync(made);
        using var deactivate = await PutAsync($"/locations/{Id(site)}", new
        {
            name = "Closed site",
            addressLine1 = "1 Road",
            city = "Pune",
            countryCode = "IN",
            timeZoneId = "Asia/Kolkata",
            workWeekDays = 31,
            isActive = false,
            rowVersion = Version(site),
        }, Acme.OrgAdmin);
        deactivate.StatusCode.Should().Be(HttpStatusCode.OK, await deactivate.Content.ReadAsStringAsync());
        using var inactive = await PostAsync("/employees", CreateBody(Acme, location: Id(site)), Acme.HrAdmin);

        ErrorCode(await JsonAsync(foreign)).Should().Be("employee.job.location_not_found");
        ErrorCode(await JsonAsync(inactive)).Should().Be("employee.job.location_inactive");
    }


    [Fact]
    public async Task Create_WithAManagerWhoHasNotJoinedYet_Is422()
    {
        var manager = await CreateEmployeeAsync(Acme, CreateBody(Acme, joining: new DateOnly(2026, 6, 1)));

        using var response = await PostAsync("/employees", CreateBody(Acme, joining: new DateOnly(2026, 3, 1), manager: Id(manager)), Acme.HrAdmin);

        ErrorCode(await JsonAsync(response)).Should().Be("employee.job.manager_not_employed");
    }

    [Fact]
    public async Task Create_WithANullBody_Is422_AndNeverReachesTheService()
    {
        using var response = await Host.SendAsync(HttpMethod.Post, $"{Api}/employees", Acme.HrAdmin, null);
        using var raw = new HttpRequestMessage(HttpMethod.Post, $"{Api}/employees") { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") };
        raw.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Acme.HrAdmin);
        using var nullBody = await Host.Client.SendAsync(raw);

        nullBody.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(nullBody)).Should().Be("employee.request.body_required");
        _ = response;
    }

    // --- confirm and personal data ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_SetsTheDate_OnlyOnce_AndRefusesAStaleVersion()
    {
        var employee = await CreateEmployeeAsync(Acme);

        using var stale = await PostAsync($"/employees/{Id(employee)}/confirm", new { confirmationDate = "2026-07-01", rowVersion = Version(employee) + 5 }, Acme.HrAdmin);
        using var ok = await PostAsync($"/employees/{Id(employee)}/confirm", new { confirmationDate = "2026-07-01", rowVersion = Version(employee) }, Acme.HrAdmin);
        var confirmed = await JsonAsync(ok);
        using var again = await PostAsync($"/employees/{Id(employee)}/confirm", new { confirmationDate = "2026-08-01", rowVersion = Version(confirmed) }, Acme.HrAdmin);

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.GetProperty("confirmationDate").GetString().Should().Be("2026-07-01");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(again)).Should().Be("employee.employee.already_confirmed");
    }

    [Fact]
    public async Task Confirm_BeforeJoining_Is422()
    {
        var employee = await CreateEmployeeAsync(Acme);

        using var response = await PostAsync($"/employees/{Id(employee)}/confirm", new { confirmationDate = "2025-01-01", rowVersion = Version(employee) }, Acme.HrAdmin);

        ErrorCode(await JsonAsync(response)).Should().Be("employee.employee.confirmation_before_joining");
    }

    [Fact]
    public async Task Personal_CanBeUpdated_AndEmailAndPhoneAreMaskedWithoutThePermission()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var profile = await ProfileAsync(Id(employee), Acme.HrAdmin);
        var version = Version(profile.GetProperty("personal"));

        using var update = await PutAsync($"/employees/{Id(employee)}/personal", new
        {
            personal = new { firstName = "Asha", middleName = "K", lastName = "Rao-Menon", dateOfBirth = "1990-05-17", gender = "female", personalEmail = "asha.menon@example.com", phone = "+91 90000 11111" },
            rowVersion = version,
        }, Acme.HrAdmin);
        using var stale = await PutAsync($"/employees/{Id(employee)}/personal", new
        {
            personal = new { firstName = "X", lastName = "Y", dateOfBirth = "1990-05-17", gender = "female" },
            rowVersion = version,
        }, Acme.HrAdmin);

        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var masked = (await ProfileAsync(Id(employee), Acme.HrAdmin)).GetProperty("personal");
        var revealed = (await ProfileAsync(Id(employee), Acme.HrSensitive)).GetProperty("personal");
        masked.GetProperty("lastName").GetString().Should().Be("Rao-Menon");
        masked.GetProperty("personalEmail").GetString().Should().NotContain("asha.menon").And.Contain("***");
        masked.GetProperty("phone").GetString().Should().NotBe("+91 90000 11111").And.EndWith("1111");
        revealed.GetProperty("personalEmail").GetString().Should().Be("asha.menon@example.com");
        revealed.GetProperty("phone").GetString().Should().Be("+91 90000 11111");
    }

    // --- list ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_PagesSearchesAndFilters()
    {
        var tag = $"Zq{Guid.NewGuid():N}"[..8];
        var one = await CreateEmployeeAsync(Acme, CreateBody(Acme, $"{tag}A", "Lister", unit: Acme.DepartmentAId, location: Acme.LocationId));
        var two = await CreateEmployeeAsync(Acme, CreateBody(Acme, $"{tag}B", "Lister", unit: Acme.DepartmentBId, location: Acme.SecondLocationId));
        var three = await CreateEmployeeAsync(Acme, CreateBody(Acme, $"{tag}C", "Lister", unit: Acme.SubUnitOfAId, location: Acme.LocationId));

        using var all = await GetAsync($"/employees?search={tag}&pageSize=2&page=1", Acme.Viewer);
        using var secondPage = await GetAsync($"/employees?search={tag}&pageSize=2&page=2", Acme.Viewer);
        using var byLocation = await GetAsync($"/employees?search={tag}&locationId={Acme.SecondLocationId}", Acme.Viewer);
        using var byUnitWithSubUnits = await GetAsync($"/employees?search={tag}&orgUnitId={Acme.DepartmentAId}", Acme.Viewer);
        using var byUnitOnly = await GetAsync($"/employees?search={tag}&orgUnitId={Acme.DepartmentAId}&includeSubUnits=false", Acme.Viewer);
        using var byLastName = await GetAsync($"/employees?search=Lister&pageSize=200", Acme.Viewer);

        var page1 = await JsonAsync(all);
        page1.GetProperty("total").GetInt64().Should().Be(3);
        page1.GetProperty("items").GetArrayLength().Should().Be(2);
        (await JsonAsync(secondPage)).GetProperty("items").GetArrayLength().Should().Be(1);
        Ids(await JsonAsync(byLocation)).Should().Equal(Id(two));
        Ids(await JsonAsync(byUnitWithSubUnits)).Should().BeEquivalentTo([Id(one), Id(three)]);
        Ids(await JsonAsync(byUnitOnly)).Should().Equal(Id(one));
        Ids(await JsonAsync(byLastName)).Should().Contain([Id(one), Id(two), Id(three)]);
    }

    [Fact]
    public async Task List_FiltersByStatus_ManagerAndAsOf()
    {
        var tag = $"Yq{Guid.NewGuid():N}"[..8];
        var manager = await CreateEmployeeAsync(Acme, CreateBody(Acme, $"{tag}M", "Boss"));
        var report = await CreateEmployeeAsync(Acme, CreateBody(Acme, $"{tag}R", "Report", manager: Id(manager)));
        var future = await CreateEmployeeAsync(Acme, CreateBody(Acme, $"{tag}F", "Future", joining: Today.AddDays(60)));

        using var active = await GetAsync($"/employees?search={tag}", Acme.Viewer);
        using var notJoined = await GetAsync($"/employees?search={tag}&status=notJoined", Acme.Viewer);
        using var all = await GetAsync($"/employees?search={tag}&status=all", Acme.Viewer);
        using var byManager = await GetAsync($"/employees?managerId={Id(manager)}", Acme.Viewer);
        using var badStatus = await GetAsync("/employees?status=maybe", Acme.Viewer);

        Ids(await JsonAsync(active)).Should().BeEquivalentTo([Id(manager), Id(report)]);
        Ids(await JsonAsync(notJoined)).Should().Equal(Id(future));
        Ids(await JsonAsync(all)).Should().HaveCount(3);
        Ids(await JsonAsync(byManager)).Should().Equal(Id(report));
        badStatus.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(badStatus)).Should().Be("employee.query.invalid_status");
    }

    [Fact]
    public async Task List_CapsThePageSize_AndNeverReturnsSensitiveFields()
    {
        await CreateEmployeeAsync(Acme);

        using var response = await GetAsync("/employees?pageSize=5000", Acme.Viewer);

        var json = await JsonAsync(response);
        json.GetProperty("pageSize").GetInt32().Should().Be(200);
        json.ToString().Should().NotContain("personalEmail").And.NotContain("phone").And.NotContain("dateOfBirth");
    }

    // --- job changes --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task JobChange_ClosesTheOldPeriod_KeepsHistory_AndAnswersForAPastDate()
    {
        var employee = await CreateEmployeeAsync(Acme, CreateBody(Acme, joining: new DateOnly(2026, 1, 1), unit: Acme.DepartmentAId));

        var changed = await ChangeJobAsync(Id(employee), Version(employee), new DateOnly(2026, 6, 1), JobBody(Acme, unit: Acme.DepartmentBId, location: Acme.SecondLocationId));

        changed.GetProperty("job").GetProperty("orgUnitId").GetGuid().Should().Be(Acme.DepartmentBId);
        var profile = await ProfileAsync(Id(employee), Acme.HrAdmin);
        var history = profile.GetProperty("jobHistory").EnumerateArray().ToList();
        history.Should().HaveCount(2);
        history[0].GetProperty("effectiveTo").GetString().Should().Be("2026-05-31", "the old period closes the day before the change");
        history[1].GetProperty("reason").GetString().Should().Be("promotion");

        var before = await ProfileAsync(Id(employee), Acme.HrAdmin, "?asOf=2026-03-01");
        var after = await ProfileAsync(Id(employee), Acme.HrAdmin, "?asOf=2026-07-01");
        before.GetProperty("summary").GetProperty("job").GetProperty("orgUnitId").GetGuid().Should().Be(Acme.DepartmentAId);
        after.GetProperty("summary").GetProperty("job").GetProperty("orgUnitId").GetGuid().Should().Be(Acme.DepartmentBId);
    }

    [Fact]
    public async Task JobChange_ThatIsInForce_WritesAnEventNow_ButAFutureOneWaits()
    {
        var employee = await CreateEmployeeAsync(Acme, CreateBody(Acme, joining: new DateOnly(2026, 1, 1)));
        var afterNow = await ChangeJobAsync(Id(employee), Version(employee), Today.AddDays(-5), JobBody(Acme, unit: Acme.DepartmentBId));
        await ChangeJobAsync(Id(employee), Version(afterNow), Today.AddDays(90), JobBody(Acme, unit: Acme.DepartmentAId), "transfer");

        await using var db = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var events = await db.Outbox.AsNoTracking().Where(m => m.EventType == "EmployeeJobChanged" && m.Payload.Contains(Id(employee).ToString())).ToListAsync();
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.EmployeeId == Id(employee)).OrderBy(j => j.EffectiveFrom).ToListAsync();

        events.Should().ContainSingle("only the change already in force is announced at once");
        jobs[0].EventPublishedAt.Should().NotBeNull("the joining period is announced by EmployeeCreated");
        jobs[1].EventPublishedAt.Should().NotBeNull();
        jobs[2].EventPublishedAt.Should().BeNull("the future change waits for its effective date");
    }

    [Fact]
    public async Task JobChange_NotAfterTheLatestPeriod_Is422()
    {
        var employee = await CreateEmployeeAsync(Acme, CreateBody(Acme, joining: new DateOnly(2026, 1, 1)));
        var changed = await ChangeJobAsync(Id(employee), Version(employee), new DateOnly(2026, 6, 1), JobBody(Acme, unit: Acme.DepartmentBId));

        using var same = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme), rowVersion = Version(changed) }, Acme.HrAdmin);
        using var earlier = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-03-01", reason = "change", job = JobBody(Acme), rowVersion = Version(changed) }, Acme.HrAdmin);

        ErrorCode(await JsonAsync(same)).Should().Be("employee.job.change_not_after_latest");
        ErrorCode(await JsonAsync(earlier)).Should().Be("employee.job.change_not_after_latest");
    }

    [Fact]
    public async Task JobChange_WithABadReasonOrAStaleVersion_IsRefused()
    {
        var employee = await CreateEmployeeAsync(Acme);

        using var joining = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "joining", job = JobBody(Acme), rowVersion = Version(employee) }, Acme.HrAdmin);
        using var nonsense = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "party", job = JobBody(Acme), rowVersion = Version(employee) }, Acme.HrAdmin);
        using var stale = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme), rowVersion = Version(employee) + 3 }, Acme.HrAdmin);
        using var missing = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme) }, Acme.HrAdmin);

        ErrorCode(await JsonAsync(joining)).Should().Be("employee.job.reason_invalid");
        ErrorCode(await JsonAsync(nonsense)).Should().Be("employee.job.reason_invalid");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(missing)).Should().Be("employee.row_version_required");
    }

    [Fact]
    public async Task JobChange_ThatMakesAManagerLoop_Is422()
    {
        var boss = await CreateEmployeeAsync(Acme);
        var report = await CreateEmployeeAsync(Acme, CreateBody(Acme, manager: Id(boss)));
        var deep = await CreateEmployeeAsync(Acme, CreateBody(Acme, manager: Id(report)));

        using var selfManaged = await PostAsync($"/employees/{Id(boss)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme, manager: Id(boss)), rowVersion = Version(boss) }, Acme.HrAdmin);
        using var loop = await PostAsync($"/employees/{Id(boss)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme, manager: Id(deep)), rowVersion = Version(boss) }, Acme.HrAdmin);

        ErrorCode(await JsonAsync(selfManaged)).Should().Be("employee.job.manager_self");
        ErrorCode(await JsonAsync(loop)).Should().Be("employee.job.manager_cycle");
    }

    [Fact]
    public async Task JobChange_BetweenLegalEntities_NeedsTheSameCountryAndCurrency()
    {
        var employee = await CreateEmployeeAsync(Acme, CreateBody(Acme, legalEntity: Acme.LegalEntityId));

        using var abroad = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "transfer", job = JobBody(Acme, legalEntity: Acme.ForeignLegalEntityId), rowVersion = Version(employee) }, Acme.HrAdmin);
        var moved = await ChangeJobAsync(Id(employee), Version(employee), new DateOnly(2026, 6, 1), JobBody(Acme, legalEntity: Acme.SecondLegalEntityId), "transfer");

        ErrorCode(await JsonAsync(abroad)).Should().Be("employee.job.legal_entity_change_not_allowed");
        moved.GetProperty("job").GetProperty("legalEntityId").GetGuid().Should().Be(Acme.SecondLegalEntityId);
    }

    [Fact]
    public async Task JobChange_InParallel_OnlyOneWins()
    {
        var employee = await CreateEmployeeAsync(Acme);

        var results = await Task.WhenAll(
            Task.Run(async () => { using var r = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme, unit: Acme.DepartmentBId), rowVersion = Version(employee) }, Acme.HrAdmin); return r.StatusCode; }),
            Task.Run(async () => { using var r = await PostAsync($"/employees/{Id(employee)}/jobs", new { effectiveFrom = "2026-06-01", reason = "change", job = JobBody(Acme, location: Acme.SecondLocationId), rowVersion = Version(employee) }, Acme.HrAdmin); return r.StatusCode; }));

        results.Count(s => s == HttpStatusCode.OK).Should().Be(1, string.Join(",", results));
        results.Count(s => s == HttpStatusCode.Conflict).Should().Be(1, string.Join(",", results));
        (await ProfileAsync(Id(employee), Acme.HrAdmin)).GetProperty("jobHistory").GetArrayLength().Should().Be(2);
    }

    // --- separation ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Separation_IsRecorded_ChangedAndWithdrawn()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var lastDay = Iso(Today.AddDays(30));

        using var recorded = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", noticeDate = Iso(Today), lastWorkingDay = lastDay, reason = "relocating" }, Acme.HrAdmin);
        recorded.StatusCode.Should().Be(HttpStatusCode.OK, await recorded.Content.ReadAsStringAsync());
        var separation = await JsonAsync(recorded);
        using var withoutVersion = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", lastWorkingDay = lastDay }, Acme.HrAdmin);
        using var changed = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "termination", lastWorkingDay = Iso(Today.AddDays(45)), rowVersion = Version(separation) }, Acme.HrAdmin);
        var profile = await ProfileAsync(Id(employee), Acme.HrAdmin);
        using var withdrawn = await DeleteAsync($"/employees/{Id(employee)}/separation", Acme.HrAdmin);
        var afterWithdraw = await ProfileAsync(Id(employee), Acme.HrAdmin);

        ErrorCode(await JsonAsync(withoutVersion)).Should().Be("employee.row_version_required");
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        profile.GetProperty("separation").GetProperty("type").GetString().Should().Be("termination");
        profile.GetProperty("summary").GetProperty("lastWorkingDay").GetString().Should().Be(Iso(Today.AddDays(45)));
        withdrawn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        afterWithdraw.GetProperty("separation").ValueKind.Should().Be(JsonValueKind.Null);
        afterWithdraw.GetProperty("summary").GetProperty("lastWorkingDay").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Separation_IsRefusedWhileSomeoneStillReportsToTheEmployee()
    {
        var boss = await CreateEmployeeAsync(Acme);
        var report = await CreateEmployeeAsync(Acme, CreateBody(Acme, manager: Id(boss)));

        using var blocked = await PutAsync($"/employees/{Id(boss)}/separation", new { type = "resignation", lastWorkingDay = Iso(Today.AddDays(10)) }, Acme.HrAdmin);
        await ChangeJobAsync(Id(report), Version(report), Today.AddDays(1), JobBody(Acme, manager: null), "change");
        using var allowed = await PutAsync($"/employees/{Id(boss)}/separation", new { type = "resignation", lastWorkingDay = Iso(Today.AddDays(10)) }, Acme.HrAdmin);

        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(blocked)).Should().Be("employee.separation.has_reports");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Separation_WithInvalidDetails_Is422()
    {
        var employee = await CreateEmployeeAsync(Acme);

        using var badType = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "vanished", lastWorkingDay = Iso(Today.AddDays(5)) }, Acme.HrAdmin);
        using var beforeJoining = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", lastWorkingDay = "2025-01-01" }, Acme.HrAdmin);
        using var noticeAfter = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", noticeDate = Iso(Today.AddDays(9)), lastWorkingDay = Iso(Today.AddDays(5)) }, Acme.HrAdmin);

        ErrorCode(await JsonAsync(badType)).Should().Be("employee.separation.type_invalid");
        ErrorCode(await JsonAsync(beforeJoining)).Should().Be("employee.separation.before_joining");
        ErrorCode(await JsonAsync(noticeAfter)).Should().Be("employee.separation.notice_after_last_day");
    }

    [Fact]
    public async Task Separation_WithALastDayInThePast_IsStatusSeparated_AndCannotBeWithdrawn()
    {
        var employee = await CreateEmployeeAsync(Acme, CreateBody(Acme, joining: new DateOnly(2026, 1, 1)));
        using var recorded = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "retirement", lastWorkingDay = Iso(Today.AddDays(-1)) }, Acme.HrAdmin);
        recorded.StatusCode.Should().Be(HttpStatusCode.OK, await recorded.Content.ReadAsStringAsync());

        using var list = await GetAsync($"/employees?status=separated&search={employee.GetProperty("code").GetString()}", Acme.Viewer);
        using var active = await GetAsync($"/employees?search={employee.GetProperty("code").GetString()}", Acme.Viewer);
        using var withdraw = await DeleteAsync($"/employees/{Id(employee)}/separation", Acme.HrAdmin);

        Ids(await JsonAsync(list)).Should().Equal(Id(employee));
        Ids(await JsonAsync(active)).Should().BeEmpty("the default list shows people who are employed today");
        withdraw.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(withdraw)).Should().Be("employee.separation.already_effective");
    }


    // --- helpers -----------------------------------------------------------------------------------------------------------------------

    private static List<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

}
