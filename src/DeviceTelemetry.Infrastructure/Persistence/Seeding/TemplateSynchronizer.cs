using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Infrastructure.Persistence.Seeding;

/// <param name="Unmanaged">Templates left as they are because they are not in the reference set.</param>
public sealed record TemplateSyncResult(IReadOnlyList<(int StatusCode, string? Language)> Unmanaged);

/// <summary>
/// Writes the reference templates (<see cref="DefaultTemplates"/>): overwrites existing (status code, language) pairs and
/// inserts missing ones. Runs under the same advisory lock as <see cref="StagingDataSeeder"/>, so running it while a
/// starting Staging instance inserts templates waits for that instance instead of colliding with it.
/// </summary>
public static class TemplateSynchronizer
{
    public static async Task<TemplateSyncResult> SyncAsync(ApplicationDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SeedingLock.AcquireAsync(db, ct);

        var templates = await db.Templates.ToDictionaryAsync(t => (t.StatusCode, t.Language), ct);
        foreach (var reference in DefaultTemplates.All)
        {
            if (templates.TryGetValue((reference.StatusCode, reference.Language), out var template))
            {
                template.Subject = reference.Subject;
                template.Body = reference.Body;
                template.IsHtml = reference.IsHtml;
                template.IsActive = reference.IsActive;
            }
            else
            {
                db.Templates.Add(DefaultTemplates.Copy(reference));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var referenceKeys = DefaultTemplates.All.Select(t => (t.StatusCode, t.Language)).ToHashSet();
        return new TemplateSyncResult(templates.Keys.Where(k => !referenceKeys.Contains(k)).OrderBy(k => k.StatusCode).ToList());
    }
}

/// <summary>Transaction-scoped PostgreSQL advisory lock serializing everything that writes seed data or templates.</summary>
internal static class SeedingLock
{
    /// <summary>Arbitrary key for <c>pg_advisory_xact_lock</c>.</summary>
    private const long Key = 0x4454_5345_4544;

    public static Task AcquireAsync(ApplicationDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Key})", ct);
}
