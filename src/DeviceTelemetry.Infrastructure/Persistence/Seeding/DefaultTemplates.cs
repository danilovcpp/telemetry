using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reference notification templates. A status code with an active template is emailed; one without a template is not.
/// 100010 (regular report, every few minutes) deliberately has none.
/// Staging inserts the missing ones on start; <c>sync-templates</c> writes all of them into any database.
/// </summary>
public static class DefaultTemplates
{
    private const string Footer = "\n\nЗаказчик: {CustomerName}\nСобытие: {EventId}, получено сервером {ReceivedAtUtc}.";

    public static IReadOnlyList<Template> All { get; } =
    [
        Text(100000, "{DeviceName}: прибор запущен",
            "Прибор {DeviceName} ({DeviceId}) запущен в {OccurredAtUtc}."),
        Text(100001, "{DeviceName}: проверка связи — сообщение получено",
            "Сервер получил проверочное сообщение от прибора {DeviceName} ({DeviceId}).\n" +
            "Цепочка «прибор → сервер → почта» работает."),
        Text(100003, "{DeviceName}: не отвечают каналы {Channels}",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что каналы не отвечают.\nКаналы: {Channels}\nВремя прибора: {OccurredAtUtc}."),
        Text(100004, "{DeviceName}: снова отвечают каналы {Channels}",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что каналы снова отвечают.\nКаналы: {Channels}\nВремя прибора: {OccurredAtUtc}."),
        Text(100005, "{DeviceName}: прибор перезапустился после сбоя",
            "Прибор {DeviceName} ({DeviceId}) перезапустился после сбоя в {OccurredAtUtc}.\n" +
            "Причина сбоя записана в данных события (payload.reset)."),
        Text(100006, "{DeviceName}: запись на карту не идёт",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что запись на карту памяти не идёт ({OccurredAtUtc})."),
        Text(100007, "{DeviceName}: запись на карту восстановлена",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что запись на карту памяти восстановлена ({OccurredAtUtc})."),
        Text(100008, "{DeviceName}: файл настроек прибора отвергнут",
            "Прибор {DeviceName} ({DeviceId}) отверг файл настроек и работает с настройками по умолчанию ({OccurredAtUtc})."),
        Text(190001, "{DeviceName}: прибор не выходит на связь",
            "Сервер не получает данные от прибора {DeviceName} ({DeviceId}) дольше допустимого.\n" +
            "Обнаружено: {OccurredAtUtc}."),
        Text(190002, "{DeviceName}: прибор снова на связи",
            "Прибор {DeviceName} ({DeviceId}) снова передаёт данные ({OccurredAtUtc})."),
    ];

    private static Template Text(int statusCode, string subject, string body) => new()
    {
        StatusCode = statusCode,
        Subject = subject,
        Body = body + Footer,
        IsHtml = false,
        IsActive = true,
    };

    /// <summary>A fresh, untracked copy for inserting.</summary>
    public static Template Copy(Template t) => new()
    {
        StatusCode = t.StatusCode, Subject = t.Subject, Body = t.Body, IsHtml = t.IsHtml, IsActive = t.IsActive,
    };
}
