namespace DeviceTelemetry.Application.Abstractions;

public interface IEmailMessageBuilder
{
    string CreateEmailBody(string template, string device);
}