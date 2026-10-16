using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Mobile;

public static class MobileErrors
{
    public static readonly DomainError CustomerNotFound = new(
        "customer.not_found", "The customer does not exist.", DomainErrorType.NotFound);

    public static readonly DomainError DeviceNotFound = new(
        "device.not_found", "The device does not exist.", DomainErrorType.NotFound);

    public static DomainError InvalidLimit(int max) => new(
        "pagination.invalid_limit", $"'limit' must be between 1 and {max}.");

    public static readonly DomainError InvalidCursor = new(
        "pagination.invalid_cursor", "'cursor' is not a value returned as 'nextCursor' by this endpoint.");
}
