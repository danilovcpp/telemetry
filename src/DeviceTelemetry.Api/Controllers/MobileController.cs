using DeviceTelemetry.Api.Http;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Mobile.Queries.GetCustomerDevices;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
[Produces("application/json")]
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
    [ProducesResponseType<IReadOnlyList<DeviceListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
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
    [ProducesResponseType<DeviceDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetDeviceDetails(
        int deviceId,
        [FromServices] IQueryHandler<GetDeviceDetailsQuery, DeviceDetailsDto> handler)
    {
        var result = await handler.HandleAsync(new GetDeviceDetailsQuery(deviceId), HttpContext.RequestAborted);
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
    /// <response code="200">A page of events (possibly empty).</response>
    /// <response code="400">Invalid <c>limit</c> or <c>cursor</c> (<c>pagination.invalid_limit</c>, <c>pagination.invalid_cursor</c>).</response>
    /// <response code="404">The device does not exist (<c>device.not_found</c>).</response>
    [HttpGet("devices/{deviceId}/events")]
    [ProducesResponseType<PagedResult<DeviceEventDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetDeviceEvents(
        int deviceId,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        [FromServices] IQueryHandler<GetDeviceEventsQuery, PagedResult<DeviceEventDto>> handler)
    {
        var query = new GetDeviceEventsQuery(deviceId, limit ?? GetDeviceEventsQuery.DefaultLimit, cursor);
        var result = await handler.HandleAsync(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }
}
