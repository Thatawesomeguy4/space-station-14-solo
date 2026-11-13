using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Robust.Shared.Network;

namespace Content.Server.LivingStation;

internal readonly record struct BartenderCustomer(NetUserId UserId, int ProfileId);

internal enum LightIncidentWitness
{
    ChiefEngineer = 1,
    HeadOfSecurity = 2,
}

internal readonly record struct LightIncidentNotes(long? ObservedAt, long? ReportedAt);

internal sealed class LivingStationMemoryStore(string path)
{
    internal const string FileName = "living-station-memory.sqlite";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path),
        DefaultTimeout = 1,
        Pooling = false,
    }.ToString();

    private const string BartenderSchema = """
        CREATE TABLE IF NOT EXISTS bartender_memory_v1 (
            account_id TEXT NOT NULL,
            profile_id INTEGER NOT NULL,
            reported_ship_name TEXT NOT NULL,
            reported_at INTEGER NOT NULL,
            PRIMARY KEY (account_id, profile_id)
        )
        """;

    // One Saltern incident per witness. The Chief Engineer observes it and Security's only source is that engineer.
    private const string LightIncidentSchema = """
        CREATE TABLE IF NOT EXISTS saltern_light_incident_v1 (
            witness INTEGER PRIMARY KEY,
            observed_at INTEGER NOT NULL
        )
        """;

    internal string? ReadShipName(BartenderCustomer customer)
    {
        using var connection = Open(BartenderSchema);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reported_ship_name FROM bartender_memory_v1
            WHERE account_id = $account AND profile_id = $profile AND reported_at > $cutoff
            """;
        BindCustomer(command, customer);
        command.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - Retention).ToUnixTimeSeconds());
        return command.ExecuteScalar() as string;
    }

    internal void SaveShipName(BartenderCustomer customer, string name, CancellationToken cancellation)
    {
        using var connection = Open(BartenderSchema);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM bartender_memory_v1 WHERE reported_at <= $cutoff;
            INSERT INTO bartender_memory_v1 (account_id, profile_id, reported_ship_name, reported_at)
            VALUES ($account, $profile, $name, $now)
            ON CONFLICT (account_id, profile_id) DO UPDATE
            SET reported_ship_name = excluded.reported_ship_name, reported_at = excluded.reported_at;
            """;
        BindCustomer(command, customer);
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$cutoff", (now - Retention).ToUnixTimeSeconds());
        cancellation.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
        cancellation.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    internal LightIncidentNotes ReadLightIncident()
    {
        using var connection = Open(LightIncidentSchema);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT witness, observed_at FROM saltern_light_incident_v1 WHERE observed_at > $cutoff";
        command.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - Retention).UtcTicks);
        using var reader = command.ExecuteReader();
        long? observedAt = null;
        long? reportedAt = null;
        while (reader.Read())
        {
            switch ((LightIncidentWitness)reader.GetInt32(0))
            {
                case LightIncidentWitness.ChiefEngineer:
                    observedAt = reader.GetInt64(1);
                    break;
                case LightIncidentWitness.HeadOfSecurity:
                    reportedAt = reader.GetInt64(1);
                    break;
            }
        }

        return new(observedAt, reportedAt);
    }

    internal void SaveLightIncident(LightIncidentWitness witness, long observedAt)
    {
        using var connection = Open(LightIncidentSchema);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO saltern_light_incident_v1 (witness, observed_at)
            VALUES ($witness, $observedAt)
            ON CONFLICT (witness) DO UPDATE SET observed_at = excluded.observed_at
            WHERE excluded.observed_at > saltern_light_incident_v1.observed_at
            """;
        command.Parameters.AddWithValue("$witness", (int)witness);
        command.Parameters.AddWithValue("$observedAt", observedAt);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open(string schema)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = schema;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void BindCustomer(SqliteCommand command, BartenderCustomer customer)
    {
        command.Parameters.AddWithValue("$account", customer.UserId.UserId.ToString("D"));
        command.Parameters.AddWithValue("$profile", customer.ProfileId);
    }
}
