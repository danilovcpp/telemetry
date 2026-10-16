using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DeviceTelemetry.IntegrationTests;

/// <summary>
/// Provides a migrated, throw-away PostgreSQL database.
/// Uses the server from <c>DEVICETELEMETRY_TEST_POSTGRES</c> (a connection string with rights to create databases)
/// when set; otherwise starts a PostgreSQL container through Testcontainers, which requires Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "DEVICETELEMETRY_TEST_POSTGRES";

    private PostgreSqlContainer? _container;
    private string? _serverConnectionString;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _serverConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            _serverConnectionString = _container.GetConnectionString();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString)
        {
            Database = $"device_telemetry_test_{Guid.NewGuid():N}",
        }.ConnectionString;

        // Creates the database and applies the real migrations, so they are tested as well.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }

    public ApplicationDbContext CreateContext(TimeProvider? timeProvider = null) => CreateContext(ConnectionString, timeProvider);

    /// <summary>Creates an additional empty, migrated database on the same server; the caller drops it.</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = $"device_telemetry_test_{Guid.NewGuid():N}",
        }.ConnectionString;

        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public static ApplicationDbContext CreateContext(string connectionString, TimeProvider? timeProvider = null) =>
        new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            timeProvider ?? TimeProvider.System);
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}
