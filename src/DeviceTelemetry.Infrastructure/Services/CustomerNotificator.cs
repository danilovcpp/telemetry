using DeviceTelemetry.Application.Abstractions;
using DeviceTelemetry.Application.Abstractions.Persistence;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Infrastructure.Services;

public class CustomerNotificator(
    IApplicationDbContext context,
    IEmailMessageBuilder emailMessageBuilder,
    IEmailSender emailSender) : ICustomerNotificator
{
    public async Task SendNotificationAsync(string customerId, string statusCode, string message)
    {
        var template = await context.Templates
            .FirstOrDefaultAsync(t => t.StatusCode == statusCode);
        
        // todo: create email body based on template
        // template can contain variables like {DeviceId}, {StatusCode}
        // need to replace it with real values
        var emailMessage = emailMessageBuilder.CreateEmailBody(customerId, message);
        
        throw new NotImplementedException();
    }
}