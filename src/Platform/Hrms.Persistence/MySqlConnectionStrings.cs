using MySqlConnector;

namespace Hrms.Persistence;

public static class MySqlConnectionStrings
{
    /// <summary>
    /// Applies the settings the data layer depends on, so EF and Dapper agree with the schema:
    /// ids are binary(16) (GuidFormat=Binary16) and every timestamp read back is UTC (DateTimeKind=Utc).
    /// </summary>
    public static string ForHrms(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.Binary16,
            DateTimeKind = MySqlDateTimeKind.Utc,
        }.ConnectionString;
    }
}
