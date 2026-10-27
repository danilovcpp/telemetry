using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using DeviceTelemetry.Api.Http;
using DeviceTelemetry.Application.Abstractions.DeviceLink;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Mobile;
using DeviceTelemetry.Application.Mobile.Queries.GetCustomerDevices;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceLogs;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceNotifications;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceState;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace DeviceTelemetry.Api.Controllers;

/// <summary>
/// Read API for the mobile app.
/// <para>
/// TEMPORARILY PUBLIC (MVP): no authentication. A <c>customerId</c> or <c>deviceId</c> in the URL proves nothing about
/// who is asking, so anyone who can reach the service can read any customer's devices and history. Must be protected
/// by authentication plus an owner check before a public production launch. <see cref="AllowAnonymousAttribute"/> is
/// explicit so that adding a global authorization policy later does not change these endpoints silently.
/// </para>
/// </summary>
[ApiController]
[Route("api/mobile")]
[AllowAnonymous]
[EnableRateLimiting(TelemetryRateLimiting.MobilePolicyName)]
[Tags("Mobile (temporarily public)")]
public class MobileController : ControllerBase
{
    /// <summary>Devices of a customer.</summary>
    /// <remarks>
    /// All devices of the customer sorted by name, then id. Not paginated: devices are provisioned manually and their
    /// number per customer is small. An existing customer without devices yields an empty array.
    ///
    ///     GET /api/mobile/customers/42/devices
    /// </remarks>
    /// <param name="customerId">Internal customer id.</param>
    /// <response code="200">The customer's devices (possibly empty).</response>
    /// <response code="404">The customer does not exist (<c>customer.not_found</c>).</response>
    [HttpGet("customers/{customerId}/devices")]
    [ProducesResponseType<IReadOnlyList<DeviceListItemDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetCustomerDevices(
        int customerId,
        [FromServices] IQueryHandler<GetCustomerDevicesQuery, IReadOnlyList<DeviceListItemDto>> handler)
    {
        var result = await handler.HandleAsync(new GetCustomerDevicesQuery(customerId), HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }

    /// <summary>Device card.</summary>
    /// <remarks>
    /// <c>lastEventAtUtc</c> / <c>lastStatusCode</c> come from the most recent event by device time (<c>occurredAtUtc</c>):
    /// the last known status, not a guaranteed current state. Both are <c>null</c> when the device has no events.
    ///
    ///     GET /api/mobile/devices/15
    /// </remarks>
    /// <param name="deviceId">Internal device id (<c>id</c> from the device list), not the device code.</param>
    /// <response code="200">The device.</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>).</response>
    [HttpGet("devices/{deviceId}")]
    [ProducesResponseType<DeviceDetailsDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetDeviceDetails(
        int deviceId,
        [FromServices] IQueryHandler<GetDeviceDetailsQuery, DeviceDetailsDto> handler)
    {
        var result = await handler.HandleAsync(new GetDeviceDetailsQuery(deviceId), HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }

    /// <summary>Current state of a device.</summary>
    /// <remarks>
    /// The last regular report (status 100010, by device time) plus the server's liveness view. Values and the state of
    /// each channel are in <c>lastReport.payload.values</c>; show only the channels present there (a channel removed on the
    /// device disappears from its reports). <c>silentSinceUtc</c> is set while the device is considered silent; this is
    /// checked only for devices that announce <c>reportIntervalS</c>.
    ///
    ///     GET /api/mobile/devices/15/state
    /// </remarks>
    /// <param name="deviceId">Internal device id, not the device code.</param>
    /// <response code="200">The device state; <c>lastReport</c> is <c>null</c> if the device never sent a report.</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>).</response>
    [HttpGet("devices/{deviceId}/state")]
    [ProducesResponseType<DeviceStateDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetDeviceState(
        int deviceId,
        [FromServices] IQueryHandler<GetDeviceStateQuery, DeviceStateDto> handler)
    {
        var result = await handler.HandleAsync(new GetDeviceStateQuery(deviceId), HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }

    /// <summary>Event history of a device, newest first.</summary>
    /// <remarks>
    /// Cursor pagination, ordered by <c>occurredAtUtc</c> descending, then <c>id</c> descending. Request the first page
    /// without <c>cursor</c>; while <c>hasMore</c> is true, pass the returned <c>nextCursor</c> unchanged to get the next
    /// page. Events with equal timestamps are never skipped or repeated. <c>payload</c> is the JSON object the device sent.
    ///
    ///     GET /api/mobile/devices/15/events?limit=20
    ///     GET /api/mobile/devices/15/events?limit=20&amp;cursor=AAjc...
    /// </remarks>
    /// <param name="deviceId">Internal device id, not the device code.</param>
    /// <param name="limit">Page size, 1–100; default 20.</param>
    /// <param name="cursor">Opaque <c>nextCursor</c> from the previous page.</param>
    /// <param name="excludeReports"><c>true</c>: leave out regular reports (100010).</param>
    /// <param name="notified"><c>true</c>: only events for which an email was queued (see <c>notification.status</c>).</param>
    /// <response code="200">A page of events (possibly empty).</response>
    /// <response code="400">Invalid <c>limit</c> or <c>cursor</c> (<c>pagination.invalid_limit</c>, <c>pagination.invalid_cursor</c>).</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>).</response>
    [HttpGet("devices/{deviceId}/events")]
    [ProducesResponseType<PagedResult<DeviceEventDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetDeviceEvents(
        int deviceId,
        [FromQuery, DefaultValue(GetDeviceEventsQuery.DefaultLimit)] int? limit,
        [FromQuery] string? cursor,
        [FromQuery] bool? excludeReports,
        [FromQuery] bool? notified,
        [FromServices] IQueryHandler<GetDeviceEventsQuery, PagedResult<DeviceEventDto>> handler)
    {
        var query = new GetDeviceEventsQuery(
            deviceId, limit ?? GetDeviceEventsQuery.DefaultLimit, cursor, excludeReports ?? false, notified ?? false);
        var result = await handler.HandleAsync(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }

    /// <summary>Email notifications of a device.</summary>
    /// <remarks>
    /// Whether emails are sent for the device now, in which language, and which status codes send one. Templates are the
    /// same for all customers (one per code and language); the device decides the language (device, else customer, else
    /// English) and may switch emails off. The address is deliberately not exposed.
    ///
    ///     GET /api/mobile/devices/15/notifications
    /// </remarks>
    /// <param name="deviceId">Internal device id, not the device code.</param>
    /// <response code="200">The notification settings in effect.</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>).</response>
    [HttpGet("devices/{deviceId}/notifications")]
    [ProducesResponseType<DeviceNotificationsDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetDeviceNotifications(
        int deviceId,
        [FromServices] IQueryHandler<GetDeviceNotificationsQuery, DeviceNotificationsDto> handler)
    {
        var result = await handler.HandleAsync(new GetDeviceNotificationsQuery(deviceId), HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }

    /// <summary>Stored data: months or daily files of one channel.</summary>
    /// <remarks>
    /// Data files copied from the device (feature <c>history</c>). Without <c>month</c>: the months that have files, ascending.
    /// With <c>month</c>: the daily files of that month, ascending by path. <c>size</c> is what the server holds now;
    /// <c>complete</c> means the day is closed and fully copied, so the file will not change any more. Channel 0 is the
    /// summary file of all channels. Served from the server copies, whether or not the device is online.
    ///
    ///     GET /api/mobile/devices/15/logs?ch=1
    ///     GET /api/mobile/devices/15/logs?ch=1&amp;month=2026-10
    /// </remarks>
    /// <param name="deviceId">Internal device id, not the device code.</param>
    /// <param name="ch">Channel number; 0 = summary file.</param>
    /// <param name="month">Month <c>YYYY-MM</c>; omit to list months.</param>
    /// <response code="200"><c>{"months":[…]}</c> without <c>month</c>, <c>{"days":[…]}</c> with it.</response>
    /// <response code="400"><c>ch</c> missing or not a number (<c>logs.invalid_channel</c>), malformed <c>month</c> (<c>logs.invalid_month</c>).</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>), has no <c>history</c> (<c>feature.not_available</c>), or no files in that month (<c>log.not_found</c>).</response>
    [HttpGet("devices/{deviceId}/logs")]
    [ProducesResponseType<DeviceLogsResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetDeviceLogs(
        int deviceId,
        [FromQuery] string? ch,
        [FromQuery] string? month,
        [FromServices] IQueryHandler<GetDeviceLogMonthsQuery, DeviceLogMonthsDto> monthsHandler,
        [FromServices] IQueryHandler<GetDeviceLogDaysQuery, DeviceLogDaysDto> daysHandler)
    {
        // Parsed by hand so that a bad value gets the usual problem body with a code.
        if (!int.TryParse(ch, NumberStyles.None, CultureInfo.InvariantCulture, out var channel))
            return this.Problem(MobileErrors.InvalidChannel);

        if (month is null)
        {
            var months = await monthsHandler.HandleAsync(new GetDeviceLogMonthsQuery(deviceId, channel), HttpContext.RequestAborted);
            return months.Match<IActionResult>(m => Ok(new DeviceLogsResponse { Months = m.Months }), this.Problem);
        }

        var days = await daysHandler.HandleAsync(new GetDeviceLogDaysQuery(deviceId, channel, month), HttpContext.RequestAborted);
        return days.Match<IActionResult>(d => Ok(new DeviceLogsResponse { Days = d.Days }), this.Problem);
    }

    /// <summary>Stored data: the bytes of one daily file.</summary>
    /// <remarks>
    /// The server copy as it is (tab-separated, as written by the device; no charset is declared). Pass <c>path</c> from
    /// the day list URL-encoded as one segment. Supports <c>HEAD</c>, <c>Range</c> (206; 416 outside the copy),
    /// <c>If-None-Match</c> and <c>If-Range</c>; the <c>ETag</c> changes whenever the copy grows or is re-read. While a
    /// day is not <c>complete</c> the file can grow: request the tail with <c>Range: bytes=&lt;size&gt;-</c>.
    ///
    ///     GET /api/mobile/devices/15/data/LOG%2FCH1%2F2026-10%2F23.TSV
    /// </remarks>
    /// <param name="deviceId">Internal device id, not the device code.</param>
    /// <param name="path">File path from the day list, URL-encoded.</param>
    /// <response code="200">The whole copy.</response>
    /// <response code="206">The requested range.</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>), has no <c>history</c> (<c>feature.not_available</c>), or no such file (<c>log.not_found</c>).</response>
    /// <response code="416">The range is outside the copy.</response>
    /// <response code="503">Not copied yet: being copied now, retry after <c>Retry-After</c> seconds (<c>sync.pending</c>), or the device is offline (<c>device.offline</c>).</response>
    [HttpGet("devices/{deviceId}/data/{**path}")]
    [HttpHead("devices/{deviceId}/data/{**path}")]
    [ProducesResponseType<Stream>(StatusCodes.Status200OK, "text/tab-separated-values")]
    [ProducesResponseType<Stream>(StatusCodes.Status206PartialContent, "text/tab-separated-values")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> GetDeviceData(
        int deviceId,
        string path,
        [FromServices] IQueryHandler<GetDeviceLogFileQuery, LogFileAccess> handler,
        [FromServices] ILogFileStorage storage,
        [FromServices] ILogger<MobileController> logger)
    {
        // Routing keeps %2F encoded inside a segment; the device path is only compared with known paths.
        var devicePath = Uri.UnescapeDataString(path);
        var result = await handler.HandleAsync(new GetDeviceLogFileQuery(deviceId, devicePath), HttpContext.RequestAborted);
        if (result.IsFailure)
            return this.Problem(result.Error);

        var access = result.Value;
        switch (access.Availability)
        {
            case LogFileAvailability.Pending:
                Response.Headers.RetryAfter = access.RetryAfterS!.Value.ToString(CultureInfo.InvariantCulture);
                return ServiceUnavailable("sync.pending", "The file is being copied from the device. Retry after the number of seconds in the Retry-After header.");

            case LogFileAvailability.Offline:
                return ServiceUnavailable("device.offline", "The file has not been copied yet and the device is offline.");
        }

        var stream = storage.OpenRead(deviceId, access.Path, access.StoredSize);
        if (stream is null)
        {
            logger.LogError("Device {DeviceId}: copy of a listed file is missing on disk", deviceId);
            return this.Problem(MobileErrors.LogNotFound);
        }

        return File(
            stream,
            "text/tab-separated-values",
            access.LastModifiedUtc is { } modified ? new DateTimeOffset(modified, TimeSpan.Zero) : null,
            new EntityTagHeaderValue(access.ETag),
            enableRangeProcessing: true);
    }

    private ObjectResult ServiceUnavailable(string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status503ServiceUnavailable, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }
}

/// <summary>Exactly one of <c>months</c> (no <c>month</c> requested) and <c>days</c> is present.</summary>
public sealed class DeviceLogsResponse
{
    /// <summary>Months with files, <c>YYYY-MM</c>, ascending.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Months { get; init; }

    /// <summary>Daily files of the month, ascending by path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeviceLogDayDto>? Days { get; init; }
}
