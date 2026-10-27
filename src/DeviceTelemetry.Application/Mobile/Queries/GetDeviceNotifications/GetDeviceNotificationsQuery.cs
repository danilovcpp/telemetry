using System.Net.Mail;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceNotifications;

public sealed record GetDeviceNotificationsQuery(int DeviceId) : IQuery<DeviceNotificationsDto>;

/// <param name="Enabled">Emails are sent for this device now: not switched off on the device, and an address exists.</param>
/// <param name="Reason">Why not, when <paramref name="Enabled"/> is <c>false</c>: <c>disabled</c> (switched off on the device) or <c>no_address</c>.</param>
/// <param name="Language">Language of the emails: the device's, else the customer's, else <c>en</c>.</param>
/// <param name="Codes">Status codes that send an email in that language, ascending.</param>
public sealed record DeviceNotificationsDto(bool Enabled, string? Reason, string Language, IReadOnlyList<int> Codes);

/// <summary>
/// The notification settings in effect for a device, by the same rules <see cref="TelemetryNotifier"/> applies: template in
/// the language, else English, else neutral; the chosen template decides even when inactive. No address is exposed.
/// </summary>
public sealed class GetDeviceNotificationsQueryHandler(IApplicationDbContext context)
    : IQueryHandler<GetDeviceNotificationsQuery, DeviceNotificationsDto>
{
    public async Task<Result<DeviceNotificationsDto>> HandleAsync(GetDeviceNotificationsQuery query, CancellationToken ct)
    {
        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == query.DeviceId)
            .Select(d => new
            {
                d.NotificationsDisabled,
                Address = d.NotifyEmail ?? d.Customer.Email,
                Language = d.NotifyLanguage ?? d.Customer.Language,
            })
            .SingleOrDefaultAsync(ct);

        if (device is null)
            return MobileErrors.DeviceNotFound;

        var language = device.Language ?? NotificationLanguages.Default;

        // The template table is small (one row per code and language).
        var templates = await context.Templates
            .AsNoTracking()
            .Where(t => t.Language == language || t.Language == NotificationLanguages.Default || t.Language == null)
            .ToListAsync(ct);

        var codes = templates
            .GroupBy(t => t.StatusCode)
            .Where(g => TelemetryNotifier.SelectTemplate(g, language)?.IsActive == true)
            .Select(g => g.Key)
            .Order()
            .ToList();

        string? reason = device.NotificationsDisabled ? "disabled"
            : string.IsNullOrWhiteSpace(device.Address) || !MailAddress.TryCreate(device.Address, out _) ? "no_address"
            : null;

        return new DeviceNotificationsDto(reason is null, reason, language, codes);
    }
}
