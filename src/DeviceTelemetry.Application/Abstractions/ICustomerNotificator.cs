namespace DeviceTelemetry.Application.Abstractions;

public interface ICustomerNotificator
{
    Task SendNotificationAsync(string customerId, string statusCode, string message);
}