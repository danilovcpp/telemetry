using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reference notification templates, one per (status code, language) for every language in
/// <see cref="NotificationLanguages.Supported"/>. A status code with an active template is emailed; one without a template
/// is not. 100010 (regular report, every few minutes) deliberately has none.
/// Every email ends with two time lines: the server receive time in UTC and the event time in the device's time zone.
/// Staging inserts the missing ones on start; <c>sync-templates</c> writes all of them into any database.
/// </summary>
public static class DefaultTemplates
{
    public static IReadOnlyList<int> StatusCodes { get; } =
        [100000, 100001, 100002, 100003, 100004, 100005, 100006, 100007, 100008, 190001, 190002, 190003];

    public static IReadOnlyList<Template> All { get; } =
    [
        .. English(),
        .. Russian(),
        .. German(),
    ];

    private static IEnumerable<Template> English() => Language(
        NotificationLanguages.English,
        "\n\nServer time (UTC): {ServerTime}\nDevice time: {DeviceTime}\n\nCustomer: {CustomerName}\nEvent: {EventId}",
        (100000, "{DeviceName}: device started",
            "Device {DeviceName} ({DeviceId}) has started."),
        (100001, "{DeviceName}: connectivity check — message received",
            "The server received a test message from device {DeviceName} ({DeviceId}).\nThe device → server → email chain is working."),
        (100002, "{DeviceName}: low battery",
            "Device {DeviceName} ({DeviceId}) reported a low battery."),
        (100003, "{DeviceName}: channels not responding {Channels}",
            "Device {DeviceName} ({DeviceId}) reported that channels are not responding.\nChannels: {Channels}"),
        (100004, "{DeviceName}: channels responding again {Channels}",
            "Device {DeviceName} ({DeviceId}) reported that channels are responding again.\nChannels: {Channels}"),
        (100005, "{DeviceName}: device restarted after a failure",
            "Device {DeviceName} ({DeviceId}) restarted after a failure.\nThe cause is recorded in the event data (payload.reset)."),
        (100006, "{DeviceName}: memory card recording stopped",
            "Device {DeviceName} ({DeviceId}) reported that recording to the memory card has stopped."),
        (100007, "{DeviceName}: memory card recording restored",
            "Device {DeviceName} ({DeviceId}) reported that recording to the memory card has been restored."),
        (100008, "{DeviceName}: settings file rejected",
            "Device {DeviceName} ({DeviceId}) rejected its settings file and is running on default settings."),
        (190001, "{DeviceName}: device is not reporting",
            "The server has not received data from device {DeviceName} ({DeviceId}) for longer than expected."),
        (190002, "{DeviceName}: device is reporting again",
            "Device {DeviceName} ({DeviceId}) is sending data again."),
        (190003, "{DeviceName}: notification address changed",
            "Notifications for device {DeviceName} ({DeviceId}) will now be sent to: {NotifyEmail}\n" +
            "You receive this message because notifications used to be sent to this address. " +
            "If you did not expect this change, check the device settings."));

    private static IEnumerable<Template> Russian() => Language(
        NotificationLanguages.Russian,
        "\n\nВремя сервера (UTC): {ServerTime}\nВремя прибора: {DeviceTime}\n\nЗаказчик: {CustomerName}\nСобытие: {EventId}",
        (100000, "{DeviceName}: прибор запущен",
            "Прибор {DeviceName} ({DeviceId}) запущен."),
        (100001, "{DeviceName}: проверка связи — сообщение получено",
            "Сервер получил проверочное сообщение от прибора {DeviceName} ({DeviceId}).\nЦепочка «прибор → сервер → почта» работает."),
        (100002, "{DeviceName}: низкий заряд батареи",
            "Прибор {DeviceName} ({DeviceId}) сообщил о низком заряде батареи."),
        (100003, "{DeviceName}: не отвечают каналы {Channels}",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что каналы не отвечают.\nКаналы: {Channels}"),
        (100004, "{DeviceName}: снова отвечают каналы {Channels}",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что каналы снова отвечают.\nКаналы: {Channels}"),
        (100005, "{DeviceName}: прибор перезапустился после сбоя",
            "Прибор {DeviceName} ({DeviceId}) перезапустился после сбоя.\nПричина сбоя записана в данных события (payload.reset)."),
        (100006, "{DeviceName}: запись на карту не идёт",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что запись на карту памяти не идёт."),
        (100007, "{DeviceName}: запись на карту восстановлена",
            "Прибор {DeviceName} ({DeviceId}) сообщил, что запись на карту памяти восстановлена."),
        (100008, "{DeviceName}: файл настроек прибора отвергнут",
            "Прибор {DeviceName} ({DeviceId}) отверг файл настроек и работает с настройками по умолчанию."),
        (190001, "{DeviceName}: прибор не выходит на связь",
            "Сервер не получает данные от прибора {DeviceName} ({DeviceId}) дольше допустимого."),
        (190002, "{DeviceName}: прибор снова на связи",
            "Прибор {DeviceName} ({DeviceId}) снова передаёт данные."),
        (190003, "{DeviceName}: изменён адрес уведомлений",
            "Уведомления прибора {DeviceName} ({DeviceId}) теперь отправляются на адрес: {NotifyEmail}\n" +
            "Вы получили это письмо, потому что раньше уведомления приходили на этот адрес. " +
            "Если вы не ожидали изменения, проверьте настройки прибора."));

    // German texts: have them proofread by a native speaker before customers rely on them.
    private static IEnumerable<Template> German() => Language(
        NotificationLanguages.German,
        "\n\nServerzeit (UTC): {ServerTime}\nGerätezeit: {DeviceTime}\n\nKunde: {CustomerName}\nEreignis: {EventId}",
        (100000, "{DeviceName}: Gerät gestartet",
            "Das Gerät {DeviceName} ({DeviceId}) wurde gestartet."),
        (100001, "{DeviceName}: Verbindungstest – Nachricht empfangen",
            "Der Server hat eine Testnachricht vom Gerät {DeviceName} ({DeviceId}) empfangen.\nDie Kette Gerät → Server → E-Mail funktioniert."),
        (100002, "{DeviceName}: Batterie schwach",
            "Das Gerät {DeviceName} ({DeviceId}) hat einen niedrigen Batteriestand gemeldet."),
        (100003, "{DeviceName}: Kanäle antworten nicht {Channels}",
            "Das Gerät {DeviceName} ({DeviceId}) meldet, dass Kanäle nicht antworten.\nKanäle: {Channels}"),
        (100004, "{DeviceName}: Kanäle antworten wieder {Channels}",
            "Das Gerät {DeviceName} ({DeviceId}) meldet, dass Kanäle wieder antworten.\nKanäle: {Channels}"),
        (100005, "{DeviceName}: Neustart nach einem Fehler",
            "Das Gerät {DeviceName} ({DeviceId}) wurde nach einem Fehler neu gestartet.\nDie Ursache ist in den Ereignisdaten gespeichert (payload.reset)."),
        (100006, "{DeviceName}: Aufzeichnung auf die Speicherkarte gestoppt",
            "Das Gerät {DeviceName} ({DeviceId}) meldet, dass die Aufzeichnung auf die Speicherkarte gestoppt wurde."),
        (100007, "{DeviceName}: Aufzeichnung auf die Speicherkarte wiederhergestellt",
            "Das Gerät {DeviceName} ({DeviceId}) meldet, dass die Aufzeichnung auf die Speicherkarte wiederhergestellt wurde."),
        (100008, "{DeviceName}: Einstellungsdatei abgelehnt",
            "Das Gerät {DeviceName} ({DeviceId}) hat seine Einstellungsdatei abgelehnt und arbeitet mit Standardeinstellungen."),
        (190001, "{DeviceName}: Gerät meldet sich nicht",
            "Der Server empfängt seit längerer Zeit als erwartet keine Daten vom Gerät {DeviceName} ({DeviceId})."),
        (190002, "{DeviceName}: Gerät meldet sich wieder",
            "Das Gerät {DeviceName} ({DeviceId}) sendet wieder Daten."),
        (190003, "{DeviceName}: Benachrichtigungsadresse geändert",
            "Benachrichtigungen des Geräts {DeviceName} ({DeviceId}) werden jetzt an folgende Adresse gesendet: {NotifyEmail}\n" +
            "Sie erhalten diese Nachricht, weil Benachrichtigungen bisher an diese Adresse gingen. " +
            "Falls Sie diese Änderung nicht erwartet haben, prüfen Sie die Geräteeinstellungen."));

    private static IEnumerable<Template> Language(
        string language, string footer, params (int StatusCode, string Subject, string Body)[] templates) =>
        templates.Select(t => new Template
        {
            StatusCode = t.StatusCode,
            Language = language,
            Subject = t.Subject,
            Body = t.Body + footer,
            IsHtml = false,
            IsActive = true,
        });

    /// <summary>A fresh, untracked copy for inserting.</summary>
    public static Template Copy(Template t) => new()
    {
        StatusCode = t.StatusCode, Language = t.Language, Subject = t.Subject, Body = t.Body, IsHtml = t.IsHtml, IsActive = t.IsActive,
    };
}
