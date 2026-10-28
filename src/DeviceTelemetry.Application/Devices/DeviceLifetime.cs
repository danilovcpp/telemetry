using System.Globalization;
using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Devices;

/// <summary>
/// Lifetime counters a device reports in <c>payload.lifetime</c> (FW 3.43+): powered minutes, starts by reset cause, clock
/// losses, firmware changes. Read leniently: a field of an unexpected type is <c>null</c>, unknown reset causes are kept.
/// Values may go down (chip erased, board replaced); that is not an error.
/// </summary>
/// <param name="PoweredMin">Minutes under power, in total.</param>
/// <param name="Starts">Starts in total (the sum of <paramref name="Resets"/>).</param>
/// <param name="Resets">Starts by cause: <c>powerOn</c>, <c>resetPin</c>, <c>watchdog</c>, <c>supplyDrop</c>, <c>software</c>, <c>other</c>.</param>
/// <param name="ClockLost">Starts with a stopped clock.</param>
/// <param name="FwChanges">Firmware version changes.</param>
/// <param name="FirstStartUtc">First start with a valid clock since FW 3.42; <c>null</c> if the clock was never set.</param>
/// <param name="Stored"><c>false</c>: the device cannot write its flash, the counters will not survive a power loss.</param>
public sealed record DeviceLifetime(
    long? PoweredMin,
    long? Starts,
    IReadOnlyDictionary<string, long>? Resets,
    long? ClockLost,
    long? FwChanges,
    DateTime? FirstStartUtc,
    bool? Stored)
{
    /// <summary>Takes <c>lifetime</c> out of an event payload. <c>false</c> when there is none or it is not a JSON object.</summary>
    public static bool TryExtract(string? payload, out string lifetimeJson, out DeviceLifetime lifetime)
    {
        lifetimeJson = string.Empty;
        lifetime = null!;
        if (string.IsNullOrEmpty(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("lifetime", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            lifetimeJson = element.GetRawText();
            lifetime = Read(element);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads a stored <c>lifetime</c> object; <c>null</c> for <c>null</c> or unreadable JSON.</summary>
    public static DeviceLifetime? Parse(string? lifetimeJson)
    {
        if (string.IsNullOrEmpty(lifetimeJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(lifetimeJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Read(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A counter other than <see cref="PoweredMin"/> differs: worth a history point. Powered minutes grow with every report
    /// and would turn the history into a copy of the reports.
    /// </summary>
    public bool CountersDifferFrom(DeviceLifetime other) =>
        Starts != other.Starts
        || ClockLost != other.ClockLost
        || FwChanges != other.FwChanges
        || FirstStartUtc != other.FirstStartUtc
        || Stored != other.Stored
        || !SameResets(Resets, other.Resets);

    private static bool SameResets(IReadOnlyDictionary<string, long>? a, IReadOnlyDictionary<string, long>? b)
    {
        if (a is null || b is null)
            return a is null && b is null;

        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static DeviceLifetime Read(JsonElement element)
    {
        Dictionary<string, long>? resets = null;
        if (element.TryGetProperty("resets", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            resets = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var cause in r.EnumerateObject())
            {
                if (cause.Value.ValueKind == JsonValueKind.Number && cause.Value.TryGetInt64(out var count))
                    resets[cause.Name] = count;
            }
        }

        DateTime? firstStart = null;
        if (element.TryGetProperty("firstStartUtc", out var f) && f.ValueKind == JsonValueKind.String
            && DateTime.TryParse(f.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            firstStart = parsed;
        }

        bool? stored = element.TryGetProperty("stored", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? s.GetBoolean()
            : null;

        return new DeviceLifetime(
            Number(element, "poweredMin"), Number(element, "starts"), resets, Number(element, "clockLost"),
            Number(element, "fwChanges"), firstStart, stored);
    }

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
}

/// <summary>
/// Keeps a device's latest lifetime counters and their history. Runs inside the telemetry transaction after
/// <see cref="DeviceContactRecorder"/> has locked the device row, so concurrent reports cannot interleave.
/// </summary>
public sealed class DeviceLifetimeRecorder(IApplicationDbContext context)
{
    /// <summary>
    /// Stores <c>payload.lifetime</c> of <paramref name="telemetryEvent"/> when it is newer (by <c>OccurredAtUtc</c>) than
    /// the stored value; reports delivered late do not overwrite fresher counters. Adds a history point when a counter other
    /// than the powered minutes changed. The event itself is accepted in any case.
    /// </summary>
    public async Task RecordAsync(TelemetryEvent telemetryEvent, CancellationToken ct)
    {
        if (!DeviceLifetime.TryExtract(telemetryEvent.Payload, out var lifetimeJson, out var lifetime))
            return;

        var current = await context.Devices
            .Where(d => d.Id == telemetryEvent.DeviceId)
            .Select(d => new { d.Lifetime, d.LifetimeAtUtc })
            .SingleAsync(ct);

        if (current.LifetimeAtUtc is { } storedAt && storedAt >= telemetryEvent.OccurredAtUtc)
            return;

        var occurredAtUtc = telemetryEvent.OccurredAtUtc;
        await context.Devices
            .Where(d => d.Id == telemetryEvent.DeviceId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Lifetime, lifetimeJson)
                .SetProperty(d => d.LifetimeAtUtc, occurredAtUtc), ct);

        var previous = DeviceLifetime.Parse(current.Lifetime);
        if (previous is null || lifetime.CountersDifferFrom(previous))
        {
            context.DeviceLifetimeSnapshots.Add(new DeviceLifetimeSnapshot
            {
                DeviceId = telemetryEvent.DeviceId,
                EventId = telemetryEvent.Id,
                OccurredAtUtc = occurredAtUtc,
                Lifetime = lifetimeJson,
            });
        }
    }
}
