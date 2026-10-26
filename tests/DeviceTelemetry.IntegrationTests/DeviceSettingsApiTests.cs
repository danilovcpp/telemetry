using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeviceTelemetry.IntegrationTests.DeviceLink;
using DeviceTelemetry.Tests.Shared;

namespace DeviceTelemetry.IntegrationTests;

/// <summary>
/// <c>POST /api/telemetry/settings</c> over HTTP: <c>"notifyEmail": null</c> (emails off) must differ from an absent field,
/// and every answer carries the address in effect as top-level <c>notifyEmail</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceSettingsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private DeviceLinkFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateEmptyDatabaseAsync();
        _factory = new DeviceLinkFactory(_connectionString);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<JsonElement> PostAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry/settings")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Device-Key", TestData.DeviceKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Null_switches_emails_off_and_an_absent_field_keeps_them_off()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
            await TestData.SeedDeviceAsync(db, deviceCode: "dev-settings-api", customerEmail: "customer@example.com");
        var client = _factory.CreateClient();

        var initial = await PostAsync(client, """{"deviceId":"dev-settings-api"}""");
        Assert.Equal("customer@example.com", initial.GetProperty("notifyEmail").GetString());

        var off = await PostAsync(client, """{"deviceId":"dev-settings-api","notifyEmail":null}""");
        Assert.Equal(JsonValueKind.Null, off.GetProperty("notifyEmail").ValueKind);
        Assert.Empty(off.GetProperty("rejected").EnumerateObject());

        var still = await PostAsync(client, """{"deviceId":"dev-settings-api","name":"Logger"}""");
        Assert.Equal(JsonValueKind.Null, still.GetProperty("notifyEmail").ValueKind);

        var on = await PostAsync(client, """{"deviceId":"dev-settings-api","notifyEmail":"lab@example.org"}""");
        Assert.Equal("lab@example.org", on.GetProperty("notifyEmail").GetString());

        var rejected = await PostAsync(client, """{"deviceId":"dev-settings-api","notifyEmail":"nope"}""");
        Assert.Equal("lab@example.org", rejected.GetProperty("notifyEmail").GetString());
        Assert.True(rejected.GetProperty("rejected").TryGetProperty("notifyEmail", out _));
    }
}
