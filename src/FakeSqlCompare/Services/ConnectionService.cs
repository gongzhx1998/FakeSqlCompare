using Microsoft.Data.SqlClient;

namespace FakeSqlCompare;

public static class ConnectionService
{
    public static string Build(ConnectionProfile profile, string? database = null, int timeoutSeconds = 12)
    {
        var db = string.IsNullOrWhiteSpace(database) ? profile.Database : database;
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(db) ? "master" : db.Trim(),
            IntegratedSecurity = profile.IsWindowsAuth,
            TrustServerCertificate = true,
            ConnectTimeout = timeoutSeconds,
            ApplicationName = "FakeSqlCompare"
        };
        csb.Encrypt = SqlConnectionEncryptOption.Optional;
        if (profile.IsSqlAuth)
        {
            csb.IntegratedSecurity = false;
            csb.UserID = profile.User;
            csb.Password = profile.Password;
        }
        return csb.ConnectionString;
    }

    public static async Task TestAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var db = string.IsNullOrWhiteSpace(profile.Database) ? "master" : profile.Database;
        await using var conn = new SqlConnection(Build(profile, database: db));
        await conn.OpenAsync(ct);
    }

    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(Build(profile, database: "master"));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name
            FROM sys.databases
            WHERE state = 0
              AND name NOT IN (N'tempdb')
            ORDER BY CASE WHEN name IN (N'master', N'model', N'msdb') THEN 1 ELSE 0 END, name;
            """;
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public static string Describe(Exception ex)
    {
        if (ex is SqlException sql)
            return sql.Errors.Count > 0 ? sql.Errors[0].Message : sql.Message;
        return ex.InnerException?.Message ?? ex.Message;
    }
}
