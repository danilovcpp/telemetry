using DeviceTelemetry.Application.Abstractions;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using Identity.Core;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

public class SendTelemetryCommandHandler(
    IApplicationDbContext context,
    ICustomerNotificator notificator
    ) : ICommandHandler<SendTelemetryCommand>
{
    public async Task<Result<Unit>> HandleAsync(SendTelemetryCommand command, CancellationToken ct)
    {
        // todo: save  to database
        
        // todo: send email
        await notificator.SendNotificationAsync("", "", "");
        
        throw new NotImplementedException();
    }
}