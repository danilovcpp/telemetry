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

    public static readonly DomainError FeatureNotAvailable = new(
        "feature.not_available", "This feature is not available for the device.", DomainErrorType.NotFound);

    public static readonly DomainError LogNotFound = new(
        "log.not_found", "No such month or data file for the device.", DomainErrorType.NotFound);

    public static readonly DomainError InvalidChannel = new(
        "logs.invalid_channel", "'ch' is required and must be a channel number (0 = summary file).");

    public static readonly DomainError InvalidMonth = new(
        "logs.invalid_month", "'month' must have the form YYYY-MM.");

    public static readonly DomainError InvalidCursor = new(
        "pagination.invalid_cursor", "'cursor' is not a value returned as 'nextCursor' by this endpoint.");
}
