using FluentAssertions;
using Hrms.Employee.Domain;

namespace Hrms.Employee.UnitTests;

public class EmployeeRecordTests
{
    private static readonly DateOnly Joined = new(2026, 3, 1);

    private static EmployeeRecord New() => EmployeeRecord.Create("emp-001", Joined).Value;

    [Fact]
    public void Create_NormalisesTheCode_AndKeepsTheJoiningDate()
    {
        var employee = New();

        employee.Code.Should().Be("EMP-001");
        employee.JoiningDate.Should().Be(Joined);
        employee.ConfirmationDate.Should().BeNull();
        employee.LastWorkingDay.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-X")]
    [InlineData("has space")]
    [InlineData("0123456789012345678901234567890123")]
    public void Create_WithABadCode_Fails(string code)
    {
        EmployeeRecord.Create(code, Joined).Error.Code.Should().Be("employee.employee.code_invalid");
    }

    [Fact]
    public void Create_WithoutAJoiningDate_Fails()
    {
        EmployeeRecord.Create("E1", default).Error.Code.Should().Be("employee.employee.joining_date_required");
    }

    [Theory]
    [InlineData(2026, 2, 28, false)]
    [InlineData(2026, 3, 1, true)]
    [InlineData(2030, 1, 1, true)]
    public void IsEmployedOn_WithoutASeparation_StartsOnTheJoiningDate(int y, int m, int d, bool expected)
    {
        New().IsEmployedOn(new DateOnly(y, m, d)).Should().Be(expected);
    }

    [Fact]
    public void IsEmployedOn_EndsAfterTheLastWorkingDayInclusive()
    {
        var employee = New();
        employee.SetLastWorkingDay(new DateOnly(2026, 6, 30)).IsSuccess.Should().BeTrue();

        employee.IsEmployedOn(new DateOnly(2026, 6, 30)).Should().BeTrue();
        employee.IsEmployedOn(new DateOnly(2026, 7, 1)).Should().BeFalse();
    }

    [Fact]
    public void Confirm_SetsTheDateOnce()
    {
        var employee = New();

        employee.Confirm(new DateOnly(2026, 9, 1)).IsSuccess.Should().BeTrue();
        var again = employee.Confirm(new DateOnly(2026, 10, 1));

        again.Error.Code.Should().Be("employee.employee.already_confirmed");
        employee.ConfirmationDate.Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Confirm_BeforeJoining_OrAfterLeaving_Fails()
    {
        var employee = New();
        employee.SetLastWorkingDay(new DateOnly(2026, 6, 30));

        employee.Confirm(Joined.AddDays(-1)).Error.Code.Should().Be("employee.employee.confirmation_before_joining");
        employee.Confirm(new DateOnly(2026, 7, 1)).Error.Code.Should().Be("employee.employee.confirmation_after_leaving");
    }

    [Fact]
    public void SetLastWorkingDay_BeforeJoiningOrConfirmation_Fails_AndChangesNothing()
    {
        var employee = New();
        employee.Confirm(new DateOnly(2026, 9, 1));

        employee.SetLastWorkingDay(Joined.AddDays(-1)).Error.Code.Should().Be("employee.separation.before_joining");
        employee.SetLastWorkingDay(new DateOnly(2026, 8, 31)).Error.Code.Should().Be("employee.separation.before_confirmation");
        employee.LastWorkingDay.Should().BeNull();
    }

    [Fact]
    public void ClearLastWorkingDay_MakesTheEmployeeOpenEndedAgain()
    {
        var employee = New();
        employee.SetLastWorkingDay(new DateOnly(2026, 6, 30));

        employee.ClearLastWorkingDay();

        employee.IsEmployedOn(new DateOnly(2030, 1, 1)).Should().BeTrue();
    }
}

public class EmployeeJobTests
{
    private static readonly Guid Employee = Guid.CreateVersion7();
    private static readonly DateOnly From = new(2026, 3, 1);

    private static JobDetails Details(Guid? manager = null, int notice = 30, Guid? grade = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), grade, null, manager, null, notice);

    [Fact]
    public void Create_KeepsEveryField()
    {
        var details = Details(Guid.NewGuid(), 60, Guid.NewGuid()) with { ProbationEnd = new DateOnly(2026, 9, 1), CostCentreId = Guid.NewGuid() };

        var job = EmployeeJob.Create(Employee, details, JobChangeReason.Joining, From).Value;

        job.ToDetails().Should().Be(details);
        job.EffectiveFrom.Should().Be(From);
        job.EffectiveTo.Should().BeNull();
        job.Reason.Should().Be(JobChangeReason.Joining);
        job.EventPublishedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithoutDetailsOrADate_Fails()
    {
        EmployeeJob.Create(Employee, null, JobChangeReason.Joining, From).Error.Code.Should().Be("employee.job.details_required");
        EmployeeJob.Create(Employee, Details(), JobChangeReason.Joining, default).Error.Code.Should().Be("employee.job.effective_from_required");
    }

    [Fact]
    public void Create_WithAMissingRequiredReference_Fails()
    {
        var missing = Details() with { LocationId = Guid.Empty };

        EmployeeJob.Create(Employee, missing, JobChangeReason.Joining, From).Error.Code.Should().Be("employee.job.reference_required");
    }

    [Fact]
    public void Create_WithAnEmptyOptionalReference_Fails()
    {
        EmployeeJob.Create(Employee, Details() with { GradeId = Guid.Empty }, JobChangeReason.Joining, From).Error.Code.Should().Be("employee.job.reference_invalid");
        EmployeeJob.Create(Employee, Details() with { ManagerId = Guid.Empty }, JobChangeReason.Joining, From).Error.Code.Should().Be("employee.job.reference_invalid");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void Create_WithAnImpossibleNoticePeriod_Fails(int days)
    {
        EmployeeJob.Create(Employee, Details(notice: days), JobChangeReason.Joining, From).Error.Code.Should().Be("employee.job.notice_period_invalid");
    }

    [Fact]
    public void Create_WithTheEmployeeAsTheirOwnManager_Fails()
    {
        EmployeeJob.Create(Employee, Details(manager: Employee), JobChangeReason.Change, From).Error.Code.Should().Be("employee.job.manager_self");
    }

    [Fact]
    public void Create_WithAnUnknownReason_Fails()
    {
        EmployeeJob.Create(Employee, Details(), (JobChangeReason)99, From).Error.Code.Should().Be("employee.job.reason_invalid");
    }

    [Fact]
    public void EndOn_ClosesThePeriod_AndAnAnswerForAPastDateIsPossible()
    {
        var job = EmployeeJob.Create(Employee, Details(), JobChangeReason.Joining, From).Value;

        job.EndOn(new DateOnly(2026, 5, 31));

        job.IsEffectiveOn(new DateOnly(2026, 5, 31)).Should().BeTrue();
        job.IsEffectiveOn(new DateOnly(2026, 6, 1)).Should().BeFalse();
        job.IsEffectiveOn(new DateOnly(2026, 2, 28)).Should().BeFalse();
    }
}

public class JobTimelineTests
{
    private static readonly DateOnly Joined = new(2026, 3, 1);

    private static EmployeeRecord Employee() => EmployeeRecord.Create("E1", Joined).Value;

    [Fact]
    public void FirstPeriod_MustStartOnTheJoiningDate()
    {
        JobTimeline.CheckAppend(Employee(), [], Joined).IsSuccess.Should().BeTrue();
        JobTimeline.CheckAppend(Employee(), [], Joined.AddDays(1)).Error.Code.Should().Be("employee.job.first_must_start_on_joining");
        JobTimeline.CheckAppend(Employee(), [], Joined.AddDays(-1)).Error.Code.Should().Be("employee.job.first_must_start_on_joining");
    }

    [Fact]
    public void LaterPeriod_MustStartAfterTheLatestOne()
    {
        DateOnly[] starts = [Joined, new DateOnly(2026, 6, 1)];

        JobTimeline.CheckAppend(Employee(), starts, new DateOnly(2026, 6, 2)).IsSuccess.Should().BeTrue();
        JobTimeline.CheckAppend(Employee(), starts, new DateOnly(2026, 6, 1)).Error.Code.Should().Be("employee.job.change_not_after_latest");
        JobTimeline.CheckAppend(Employee(), starts, new DateOnly(2026, 4, 1)).Error.Code.Should().Be("employee.job.change_not_after_latest");
    }

    [Fact]
    public void LaterPeriod_CannotPrecedeJoining()
    {
        // Only reachable when the history is corrupt, but the rule is stated anyway.
        JobTimeline.CheckAppend(Employee(), [Joined.AddDays(-30)], Joined.AddDays(-1)).Error.Code.Should().Be("employee.job.before_joining");
    }

    [Fact]
    public void LaterPeriod_CannotStartAfterTheLastWorkingDay()
    {
        var employee = Employee();
        employee.SetLastWorkingDay(new DateOnly(2026, 8, 31));

        JobTimeline.CheckAppend(employee, [Joined], new DateOnly(2026, 8, 31)).IsSuccess.Should().BeTrue();
        JobTimeline.CheckAppend(employee, [Joined], new DateOnly(2026, 9, 1)).Error.Code.Should().Be("employee.job.after_leaving");
    }

    [Fact]
    public void AFutureDatedPeriod_IsAcceptedWhenItFollowsTheLatest()
    {
        JobTimeline.CheckAppend(Employee(), [Joined], new DateOnly(2099, 1, 1)).IsSuccess.Should().BeTrue();
    }
}

public class ManagerChainTests
{
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid C = Guid.CreateVersion7();
    private static readonly Guid D = Guid.CreateVersion7();

    private static readonly DateOnly Jan = new(2026, 1, 1);
    private static readonly DateOnly Jun = new(2026, 6, 1);
    private static readonly DateOnly Dec = new(2026, 12, 1);

    private static ManagerLink Link(Guid employee, Guid? manager, DateOnly from, DateOnly? to = null) => new(employee, manager, from, to);

    private static Func<Guid, CancellationToken, Task<IReadOnlyList<ManagerLink>>> Loader(params ManagerLink[] links) =>
        (employee, _) => Task.FromResult<IReadOnlyList<ManagerLink>>([.. links.Where(l => l.EmployeeId == employee)]);

    // C reports to B, B reports to A, A has no manager.
    private static ManagerLink[] Chain() => [Link(A, null, Jan), Link(B, A, Jan), Link(C, B, Jan)];

    [Fact]
    public async Task Check_WithoutAManager_IsFine()
    {
        (await ManagerChain.CheckAsync(A, null, Jun, Loader(Chain()), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Check_AnUnrelatedManager_IsFine()
    {
        var links = Chain().Append(Link(D, null, Jan)).ToArray();

        (await ManagerChain.CheckAsync(D, C, Jun, Loader(links), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Check_ManagingYourself_IsRefused()
    {
        var result = await ManagerChain.CheckAsync(A, A, Jun, Loader(Chain()), CancellationToken.None);

        result.Error.Code.Should().Be("employee.job.manager_self");
    }

    [Fact]
    public async Task Check_ADirectReportAsManager_IsACycle()
    {
        var result = await ManagerChain.CheckAsync(A, B, Jun, Loader(Chain()), CancellationToken.None);

        result.Error.Code.Should().Be("employee.job.manager_cycle");
    }

    [Fact]
    public async Task Check_ADeepDescendantAsManager_IsACycle()
    {
        var result = await ManagerChain.CheckAsync(A, C, Jun, Loader(Chain()), CancellationToken.None);

        result.Error.Code.Should().Be("employee.job.manager_cycle");
    }

    [Fact]
    public async Task Check_ThatIsSafeTodayButBreaksOnAFutureChange_IsRefused()
    {
        // D reports to A. From December B reports to D. Making D report to B from June is fine until December, then D -> B -> D.
        ManagerLink[] links =
        [
            Link(A, null, Jan), Link(D, A, Jan),
            Link(B, A, Jan, new DateOnly(2026, 11, 30)), Link(B, D, Dec),
        ];

        var result = await ManagerChain.CheckAsync(D, B, Jun, Loader(links), CancellationToken.None);

        result.Error.Code.Should().Be("employee.job.manager_cycle");
    }

    [Fact]
    public async Task Check_JudgesTheArrangementOnTheStartDate_NotTheOldOne()
    {
        // B reported to A, but from June B is a top manager. From December making A report to B is no cycle.
        ManagerLink[] links = [Link(A, null, Jan), Link(B, A, Jan, new DateOnly(2026, 5, 31)), Link(B, null, Jun)];

        (await ManagerChain.CheckAsync(A, B, Dec, Loader(links), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Check_WithALoopAlreadyInTheData_RefusesInsteadOfHanging()
    {
        ManagerLink[] links = [Link(B, C, Jan), Link(C, B, Jan), Link(D, null, Jan)];

        var result = await ManagerChain.CheckAsync(D, B, Jun, Loader(links), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Check_LoadsEachEmployeeOnTheWayUpOnlyOnce()
    {
        var loads = new List<Guid>();
        Task<IReadOnlyList<ManagerLink>> Counting(Guid employee, CancellationToken _)
        {
            loads.Add(employee);
            return Task.FromResult<IReadOnlyList<ManagerLink>>([.. Chain().Where(l => l.EmployeeId == employee)]);
        }

        await ManagerChain.CheckAsync(D, C, Jun, Counting, CancellationToken.None);

        loads.Should().OnlyHaveUniqueItems();
        loads.Should().BeEquivalentTo([C, B, A]);
    }
}

public class EmployeePersonalTests
{
    private static readonly DateOnly Today = new(2026, 11, 26);

    private static PersonalDetails Details(string? first = "Asha", string? last = "Rao", DateOnly? dob = null, string? email = "asha@example.com", string? phone = "+91 98765 43210") =>
        new(first, null, last, dob ?? new DateOnly(1990, 5, 17), Gender.Female, email, phone);

    [Fact]
    public void Create_KeepsTheDetails_AndLowerCasesTheEmail()
    {
        var personal = EmployeePersonal.Create(Guid.NewGuid(), Details(email: "Asha@Example.COM"), Today).Value;

        personal.DisplayName.Should().Be("Asha Rao");
        personal.PersonalEmail.Should().Be("asha@example.com");
        personal.Phone.Should().Be("+91 98765 43210");
    }

    [Theory]
    [InlineData(null, "Rao")]
    [InlineData("Asha", "")]
    [InlineData("  ", "Rao")]
    public void Create_WithoutBothNames_Fails(string? first, string? last)
    {
        EmployeePersonal.Create(Guid.NewGuid(), Details(first, last), Today).Error.Code.Should().Be("employee.personal.name_invalid");
    }

    [Fact]
    public void Create_WithATooLongName_Fails()
    {
        EmployeePersonal.Create(Guid.NewGuid(), Details(first: new string('a', 101)), Today).Error.Code.Should().Be("employee.personal.name_invalid");
    }

    [Theory]
    [InlineData(2026, 11, 27)]
    [InlineData(1899, 12, 31)]
    public void Create_WithAnImpossibleDateOfBirth_Fails(int y, int m, int d)
    {
        EmployeePersonal.Create(Guid.NewGuid(), Details(dob: new DateOnly(y, m, d)), Today).Error.Code.Should().Be("employee.personal.date_of_birth_invalid");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    public void Create_WithABadEmail_Fails(string email)
    {
        EmployeePersonal.Create(Guid.NewGuid(), Details(email: email), Today).Error.Code.Should().Be("employee.personal.email_invalid");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("+1 (415) 555-0100 ext 99999")]
    public void Create_WithABadPhone_Fails(string phone)
    {
        EmployeePersonal.Create(Guid.NewGuid(), Details(phone: phone), Today).Error.Code.Should().Be("employee.personal.phone_invalid");
    }

    [Fact]
    public void Create_WithoutEmailOrPhone_IsFine()
    {
        EmployeePersonal.Create(Guid.NewGuid(), Details(email: null, phone: " "), Today).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Update_WithBadDetails_ChangesNothing()
    {
        var personal = EmployeePersonal.Create(Guid.NewGuid(), Details(), Today).Value;

        personal.Update(Details(first: "Changed", email: "broken"), Today).IsFailure.Should().BeTrue();

        personal.FirstName.Should().Be("Asha");
    }
}

public class EmployeeCodeRuleTests
{
    [Fact]
    public void Allocate_HandsOutPaddedCodes_AndMovesOn()
    {
        var rule = EmployeeCodeRule.Create(Guid.NewGuid(), "emp-", 5, 1).Value;

        rule.Allocate().Should().Be("EMP-00001");
        rule.Allocate().Should().Be("EMP-00002");
        rule.NextNumber.Should().Be(3);
    }

    [Fact]
    public void Allocate_PastTheWidth_JustGetsLonger()
    {
        var rule = EmployeeCodeRule.Create(Guid.NewGuid(), "E", 2, 99).Value;

        rule.Allocate().Should().Be("E99");
        rule.Allocate().Should().Be("E100");
    }

    [Fact]
    public void Create_WithoutAPrefix_Works()
    {
        EmployeeCodeRule.Create(Guid.NewGuid(), null, 6, 1000).Value.Allocate().Should().Be("001000");
    }

    [Theory]
    [InlineData("HAS SPACE", 5, 1, "prefix_invalid")]
    [InlineData("TOOLONGPREFIX1", 5, 1, "prefix_invalid")]
    [InlineData("E", 0, 1, "width_invalid")]
    [InlineData("E", 13, 1, "width_invalid")]
    [InlineData("E", 5, 0, "next_number_invalid")]
    public void Create_WithBadValues_Fails(string prefix, int width, long start, string reason)
    {
        EmployeeCodeRule.Create(Guid.NewGuid(), prefix, width, start).Error.Code.Should().Be($"employee.code_rule.{reason}");
    }

    [Fact]
    public void Update_CanChangeThePattern_ButTheNumberNeverGoesBack()
    {
        var rule = EmployeeCodeRule.Create(Guid.NewGuid(), "E", 4, 1).Value;
        rule.Allocate();
        rule.Allocate();

        rule.Update("EMP", 6, 3).IsSuccess.Should().BeTrue();
        rule.Update("EMP", 6, 2).Error.Code.Should().Be("employee.code_rule.next_number_invalid");

        rule.Allocate().Should().Be("EMP000003");
    }

    [Fact]
    public void Format_IsDeterministic()
    {
        EmployeeCodeRule.Format("X-", 7, 4).Should().Be("X-0007");
    }
}

public class SeparationTests
{
    private static readonly Guid Employee = Guid.CreateVersion7();

    [Fact]
    public void Create_KeepsTheDetails()
    {
        var separation = Separation.Create(Employee, SeparationType.Resignation, new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 30), " personal reasons ").Value;

        separation.LastWorkingDay.Should().Be(new DateOnly(2026, 6, 30));
        separation.Reason.Should().Be("personal reasons");
        separation.EventPublishedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithNoticeAfterTheLastDay_OrNoLastDay_OrABadType_Fails()
    {
        Separation.Create(Employee, SeparationType.Resignation, new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 30), null).Error.Code
            .Should().Be("employee.separation.notice_after_last_day");
        Separation.Create(Employee, SeparationType.Resignation, null, default, null).Error.Code.Should().Be("employee.separation.last_working_day_required");
        Separation.Create(Employee, (SeparationType)42, null, new DateOnly(2026, 6, 30), null).Error.Code.Should().Be("employee.separation.type_invalid");
        Separation.Create(Employee, SeparationType.Other, null, new DateOnly(2026, 6, 30), new string('x', 501)).Error.Code.Should().Be("employee.separation.reason_invalid");
    }

    [Fact]
    public void Update_RearmsThePublishedMarker()
    {
        var separation = Separation.Create(Employee, SeparationType.Termination, null, new DateOnly(2026, 6, 30), null).Value;
        separation.MarkEventPublished(DateTime.UtcNow);

        separation.Update(SeparationType.Termination, null, new DateOnly(2026, 7, 15), null).IsSuccess.Should().BeTrue();

        separation.LastWorkingDay.Should().Be(new DateOnly(2026, 7, 15));
        separation.EventPublishedAt.Should().BeNull();
    }
}

public class SupportingRecordTests
{
    private static readonly Guid Employee = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StatutoryId_NormalisesTypeAndValue_AndKeepsNothingInTheClear()
    {
        var type = StatutoryId.NormaliseType(" pan ").Value;
        var value = StatutoryId.NormaliseValue("abcde 1234 f").Value;

        var id = StatutoryId.Create(Employee, type, "v1.dev.cipher");

        type.Should().Be("PAN");
        value.Should().Be("ABCDE1234F");
        id.ProtectedValue.Should().Be("v1.dev.cipher");
        id.ProtectedValue.Should().NotContain("ABCDE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a-b")]
    public void StatutoryId_WithABadType_Fails(string type)
    {
        StatutoryId.NormaliseType(type).Error.Code.Should().Be("employee.statutory_id.type_invalid");
    }

    [Fact]
    public void StatutoryId_EmptyOrOverlongValues_AreRefused()
    {
        StatutoryId.NormaliseValue("12").Value.Should().Be("12");
        StatutoryId.NormaliseValue("   ").Error.Code.Should().Be("employee.statutory_id.value_invalid");
        StatutoryId.NormaliseValue(new string('1', 65)).Error.Code.Should().Be("employee.statutory_id.value_invalid");
    }

    [Fact]
    public void Document_KeepsTheDetails_AndKnowsWhenItIsExpired()
    {
        var document = EmployeeDocument.Create(Employee, "visa", "Work visa", "docs/visa-1.pdf", new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31)).Value;

        document.DocumentType.Should().Be("VISA");
        document.IsExpiredOn(new DateOnly(2026, 12, 31)).Should().BeFalse("it is valid through the expiry date");
        document.IsExpiredOn(new DateOnly(2027, 1, 1)).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Title", null)]
    [InlineData("TYPE", "", null)]
    [InlineData("TYPE", "Title", "../etc/passwd")]
    [InlineData("TYPE", "Title", "/absolute/path")]
    public void Document_WithBadDetails_Fails(string type, string title, string? key)
    {
        EmployeeDocument.Create(Employee, type, title, key, null, null).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Document_ExpiringBeforeItWasIssued_Fails()
    {
        EmployeeDocument.Create(Employee, "ID", "Passport", null, new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 1)).Error.Code
            .Should().Be("employee.document.expiry_before_issue");
    }

    [Fact]
    public void Device_RegistersActive_AndRevokesOnce()
    {
        var device = EmployeeDevice.Register(Employee, "abc-123", DevicePlatform.Android, "Pixel", Now).Value;

        device.Status.Should().Be(DeviceStatus.Active);
        device.Revoke(Now.AddDays(1)).IsSuccess.Should().BeTrue();
        device.Status.Should().Be(DeviceStatus.Revoked);
        device.RevokedAt.Should().Be(Now.AddDays(1));
        device.Revoke(Now.AddDays(2)).Error.Code.Should().Be("employee.device.already_revoked");
    }

    [Fact]
    public void Device_WithBadDetails_Fails()
    {
        EmployeeDevice.Register(Employee, " ", DevicePlatform.Web, null, Now).Error.Code.Should().Be("employee.device.device_id_invalid");
        EmployeeDevice.Register(Employee, "d", (DevicePlatform)9, null, Now).Error.Code.Should().Be("employee.device.platform_invalid");
        EmployeeDevice.Register(Employee, "d", DevicePlatform.Web, new string('n', 101), Now).Error.Code.Should().Be("employee.device.name_invalid");
    }

    [Fact]
    public void Consent_IsGrantedAndWithdrawnOnce()
    {
        var consent = EmployeeConsent.Grant(Employee, "location_tracking", "v2", "mobile-app", Now).Value;

        consent.ConsentType.Should().Be("LOCATION_TRACKING");
        consent.IsActive.Should().BeTrue();
        consent.Withdraw(Now.AddDays(3)).IsSuccess.Should().BeTrue();
        consent.IsActive.Should().BeFalse();
        consent.Withdraw(Now.AddDays(4)).Error.Code.Should().Be("employee.consent.already_withdrawn");
    }

    [Fact]
    public void Consent_WithoutATypeOrVersion_Fails()
    {
        EmployeeConsent.Grant(Employee, "", "v1", null, Now).Error.Code.Should().Be("employee.consent.details_invalid");
        EmployeeConsent.Grant(Employee, "T", " ", null, Now).Error.Code.Should().Be("employee.consent.details_invalid");
    }

    [Fact]
    public void Outbox_RecordsAttempts_AndClearsTheErrorWhenProcessed()
    {
        var message = OutboxMessage.Create("EmployeeCreated", "{}", Now);

        message.MarkFailed(new string('e', 800));
        message.Attempts.Should().Be(1);
        message.LastError!.Length.Should().Be(500);

        message.MarkProcessed(Now.AddMinutes(1));
        message.ProcessedAt.Should().Be(Now.AddMinutes(1));
        message.LastError.Should().BeNull();
    }
}
