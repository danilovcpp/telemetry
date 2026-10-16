using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeviceTelemetry.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. Creating migrations needs no database; for <c>database update</c>
/// pass <c>--connection</c> or set <c>ConnectionStrings__TelemetryDatabase</c>.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TelemetryDatabase")
                               ?? "Host=localhost;Database=device_telemetry";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options, TimeProvider.System);
    }
}
