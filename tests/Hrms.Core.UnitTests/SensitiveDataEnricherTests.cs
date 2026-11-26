using FluentAssertions;
using Hrms.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Hrms.Core.UnitTests;

public class SensitiveDataEnricherTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed record BankDetails(string BankAccountNumber, string Ifsc, string BranchName);

    private sealed record Employee(string Name, string Email, decimal Salary, BankDetails Bank, string[] Phones);

    private static (ILogger Logger, CollectingSink Sink) CreateLogger()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveDataEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static string Render(LogEvent logEvent, string name) => logEvent.Properties[name].ToString();

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("UserPassword")]
    [InlineData("access_token")]
    [InlineData("ApiKey")]
    [InlineData("api-key")]
    [InlineData("Authorization")]
    [InlineData("ConnectionString")]
    [InlineData("Bank_Account_Number")]
    [InlineData("bankAccount")]
    [InlineData("NationalId")]
    [InlineData("Ssn")]
    [InlineData("Salary")]
    [InlineData("GrossPay")]
    [InlineData("EmailAddress")]
    [InlineData("MobileNumber")]
    public void IsSensitive_ForSecretFinancialIdentityPayAndContactNames_ReturnsTrue(string name) =>
        SensitiveDataEnricher.IsSensitive(name).Should().BeTrue();

    [Theory]
    [InlineData("TenantId")]
    [InlineData("CorrelationId")]
    [InlineData("Method")]
    [InlineData("Path")]
    [InlineData("Company")]
    [InlineData("LegalEntityCode")]
    [InlineData("Currency")]
    [InlineData("")]
    public void IsSensitive_ForOrdinaryNames_ReturnsFalse(string name) =>
        SensitiveDataEnricher.IsSensitive(name).Should().BeFalse();

    [Fact]
    public void Enrich_ScalarPropertyWithASensitiveName_IsMaskedAndStaysMaskedInTheRenderedMessage()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Login for {User} with {Password}", "asha", "hunter2");

        var logEvent = sink.Events.Single();
        Render(logEvent, "Password").Should().Be("\"***\"");
        Render(logEvent, "User").Should().Be("\"asha\"");
        logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("***").And.NotContain("hunter2");
    }

    [Fact]
    public void Enrich_DestructuredObject_MasksSensitiveMembersAtEveryDepth()
    {
        var (logger, sink) = CreateLogger();
        var employee = new Employee(
            "Asha Rao", "asha@example.com", 1_200_000m, new BankDetails("123456789012", "HDFC0001234", "Indiranagar"), ["+91 99999 00000"]);

        logger.Information("Created {@Employee}", employee);

        var rendered = Render(sink.Events.Single(), "Employee");
        rendered.Should().Contain("Asha Rao").And.Contain("Indiranagar");
        rendered.Should().NotContain("asha@example.com").And.NotContain("1200000").And.NotContain("123456789012")
            .And.NotContain("HDFC0001234").And.NotContain("+91 99999 00000");
        rendered.Should().Contain("***");
    }

    [Fact]
    public void Enrich_DictionaryWithASensitiveKey_MasksThatValueOnly()
    {
        var (logger, sink) = CreateLogger();
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc.def.ghi", ["Accept"] = "application/json" };

        logger.Information("Headers {@Headers}", headers);

        var rendered = Render(sink.Events.Single(), "Headers");
        rendered.Should().NotContain("abc.def.ghi").And.Contain("application/json");
    }

    [Fact]
    public void Enrich_ListOfObjects_MasksSensitiveMembersInEachElement()
    {
        var (logger, sink) = CreateLogger();
        var accounts = new[] { new BankDetails("111", "A", "One"), new BankDetails("222", "B", "Two") };

        logger.Information("Accounts {@Accounts}", accounts);

        var rendered = Render(sink.Events.Single(), "Accounts");
        rendered.Should().Contain("One").And.Contain("Two").And.NotContain("111").And.NotContain("222");
    }

    [Fact]
    public void Enrich_PropertyPushedThroughLogContext_IsMasked()
    {
        var (logger, sink) = CreateLogger();

        using (Serilog.Context.LogContext.PushProperty("ApiKey", "sk_live_123"))
        {
            logger.Information("Calling out");
        }

        Render(sink.Events.Single(), "ApiKey").Should().Be("\"***\"");
    }

    [Fact]
    public void Enrich_EventWithNothingSensitive_IsLeftUntouched()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Request {Method} {Path} for {TenantId}", "GET", "/api/v1/ping", "t-1");

        var logEvent = sink.Events.Single();
        logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Be("Request \"GET\" \"/api/v1/ping\" for \"t-1\"");
        logEvent.Properties.Values.Select(v => v.ToString()).Should().NotContain("\"***\"");
    }

    [Fact]
    public void Enrich_NonStringSensitiveValue_IsMaskedToo()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Pay {Salary}", 1_500_000m);

        Render(sink.Events.Single(), "Salary").Should().Be("\"***\"");
    }
}
