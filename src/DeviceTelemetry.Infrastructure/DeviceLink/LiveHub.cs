using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>Why a viewer could not be added.</summary>
public enum LiveSubscribeRefusal
{
    None,
    TooManyForDevice,
    TooManyForAddress,
}

/// <summary>
/// One mobile viewer of a live stream. At most <see cref="Capacity"/> frames wait for the client: a slow client loses the
/// oldest <c>values</c> frames (a status frame only when nothing else can go) instead of growing the server's memory. The
/// stream ends when the device refuses live values (<c>not_supported</c>).
/// </summary>
public sealed class LiveSubscription : IDisposable
{
    public const int Capacity = 2;

    private readonly LiveHub _hub;
    private readonly LinkedList<(string Frame, bool IsStatus)> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private bool _completed;
    private int _disposed;

    internal LiveSubscription(LiveHub hub, int deviceId, string address)
    {
        _hub = hub;
        DeviceId = deviceId;
        Address = address;
    }

    public int DeviceId { get; }
    internal string Address { get; }

    /// <summary>Receives frames (set by <see cref="LiveHub.Activate"/>).</summary>
    internal bool Active { get; set; }

    /// <summary>Frames waiting for the client.</summary>
    public int Count
    {
        get
        {
            lock (_queue)
                return _queue.Count;
        }
    }

    internal void Post(string frame, bool isStatus)
    {
        lock (_queue)
        {
            if (_completed)
                return;

            var dropped = false;
            if (_queue.Count >= Capacity)
            {
                var victim = _queue.First;
                for (var node = _queue.First; node is not null; node = node.Next)
                {
                    if (!node.Value.IsStatus)
                    {
                        victim = node;
                        break;
                    }
                }

                _queue.Remove(victim!);
                dropped = true;
            }

            _queue.AddLast((frame, isStatus));
            if (dropped)
                return; // The semaphore already counts the removed frame's slot.
        }

        _available.Release();
    }

    /// <summary>No more frames after the queued ones.</summary>
    internal void Complete()
    {
        lock (_queue)
        {
            if (_completed)
                return;
            _completed = true;
        }

        _available.Release();
    }

    public bool TryRead(out string frame)
    {
        lock (_queue)
        {
            if (_queue.First is { } first)
            {
                _queue.RemoveFirst();
                frame = first.Value.Frame;
                _available.Wait(0);
                return true;
            }
        }

        frame = string.Empty;
        return false;
    }

    /// <summary>Frames in order until the stream is completed and drained.</summary>
    public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            await _available.WaitAsync(ct);

            string? frame = null;
            bool completed;
            lock (_queue)
            {
                if (_queue.First is { } first)
                {
                    _queue.RemoveFirst();
                    frame = first.Value.Frame;
                }

                completed = _completed;
            }

            if (frame is not null)
                yield return frame;
            else if (completed)
                yield break;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _hub.Unsubscribe(this);
    }
}

/// <summary>
/// Live fan-out: one device stream for all viewers of a device. The first viewer starts the stream (<c>live.start</c>,
/// repeated every <see cref="LiveOptions.RenewIntervalS"/> as the lease renewal); <see cref="LiveOptions.StopDelayS"/> after
/// the last viewer left, <c>live.stop</c> is sent. A dropped link turns into <c>device_offline</c>, a new link restarts
/// the stream. Values are never logged.
/// </summary>
public sealed class LiveHub(IOptions<LiveOptions> options, TimeProvider timeProvider, ILogger<LiveHub> logger)
{
    public const string LiveCapability = "live";
    public const string FilesCapability = "files";

    public const string Connecting = "connecting";
    public const string Streaming = "streaming";
    public const string DeviceOffline = "device_offline";
    public const string NotSupported = "not_supported";

    private readonly ConcurrentDictionary<int, DeviceLive> _devices = new();
    private readonly Dictionary<string, int> _viewersPerAddress = new(StringComparer.Ordinal);
    private readonly LiveOptions _options = options.Value;

    /// <summary>Live values of the device are being forwarded to at least one viewer.</summary>
    public bool IsStreaming(int deviceId) =>
        _devices.TryGetValue(deviceId, out var device) && device.State == Streaming && device.Viewers.Count > 0;

    public int ViewerCount(int deviceId) =>
        _devices.TryGetValue(deviceId, out var device) ? device.Viewers.Count : 0;

    /// <summary>
    /// Reserves a viewer slot (limits per device and per address). Frames flow only after <see cref="Activate"/>.
    /// </summary>
    public LiveSubscription? TrySubscribe(int deviceId, string address, out LiveSubscribeRefusal refusal)
    {
        var device = _devices.GetOrAdd(deviceId, id => new DeviceLive(id));

        lock (device)
        {
            if (device.Viewers.Count >= _options.MaxViewersPerDevice)
            {
                refusal = LiveSubscribeRefusal.TooManyForDevice;
                return null;
            }

            lock (_viewersPerAddress)
            {
                var count = _viewersPerAddress.GetValueOrDefault(address);
                if (count >= _options.MaxViewersPerIp)
                {
                    refusal = LiveSubscribeRefusal.TooManyForAddress;
                    return null;
                }

                _viewersPerAddress[address] = count + 1;
            }

            var subscription = new LiveSubscription(this, deviceId, address);
            device.Viewers.Add(subscription);
            device.StopTimer?.Dispose();
            device.StopTimer = null;

            refusal = LiveSubscribeRefusal.None;
            return subscription;
        }
    }

    /// <summary>
    /// Starts delivering frames to a reserved viewer, beginning with the current status, and starts the device stream if
    /// needed. Called once the client is ready to read, so that no early frame is lost.
    /// </summary>
    public void Activate(LiveSubscription subscription)
    {
        if (!_devices.TryGetValue(subscription.DeviceId, out var device))
            return;

        lock (device)
        {
            if (subscription.Active || !device.Viewers.Contains(subscription))
                return;

            subscription.Active = true;

            if (device.Session is null)
                device.State = DeviceOffline;
            else if (!device.Session.Hello.Supports(LiveCapability))
                device.State = NotSupported;
            else if (!device.Running)
                Start(device);

            subscription.Post(StatusFrame(device.State), isStatus: true);
            if (device.State == NotSupported)
                subscription.Complete();
        }
    }

    internal void Unsubscribe(LiveSubscription subscription)
    {
        lock (_viewersPerAddress)
        {
            var count = _viewersPerAddress.GetValueOrDefault(subscription.Address) - 1;
            if (count > 0)
                _viewersPerAddress[subscription.Address] = count;
            else
                _viewersPerAddress.Remove(subscription.Address);
        }

        if (!_devices.TryGetValue(subscription.DeviceId, out var device))
            return;

        lock (device)
        {
            device.Viewers.Remove(subscription);
            subscription.Complete();

            if (device.Viewers.Count == 0 && device.Running && device.StopTimer is null)
            {
                device.StopTimer = timeProvider.CreateTimer(
                    _ => StopIfIdle(device), null, TimeSpan.FromSeconds(_options.StopDelayS), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>A device greeted on a new connection.</summary>
    public void OnLinkUp(DeviceLinkSession session)
    {
        var device = _devices.GetOrAdd(session.DeviceId, id => new DeviceLive(id));
        lock (device)
        {
            device.Session = session;
            device.Running = false;
            device.ClearTimers();

            if (device.Viewers.Count == 0)
                return;

            if (session.Hello.Supports(LiveCapability))
            {
                Start(device);
                Broadcast(device, StatusFrame(device.State));
            }
            else
            {
                Refuse(device);
            }
        }
    }

    /// <summary>The device sent a new <c>hello</c> on its current connection.</summary>
    public void OnHelloChanged(DeviceLinkSession session)
    {
        if (!_devices.TryGetValue(session.DeviceId, out var device))
            return;

        lock (device)
        {
            if (ReferenceEquals(device.Session, session) && device.Viewers.Count > 0 && !session.Hello.Supports(LiveCapability))
                Refuse(device);
        }
    }

    public void OnLinkDown(DeviceLinkSession session)
    {
        if (!_devices.TryGetValue(session.DeviceId, out var device))
            return;

        lock (device)
        {
            if (!ReferenceEquals(device.Session, session))
                return;

            device.Session = null;
            device.Running = false;
            device.ClearTimers();

            if (device.State != DeviceOffline)
            {
                device.State = DeviceOffline;
                Broadcast(device, StatusFrame(DeviceOffline));
            }
        }
    }

    /// <summary>A <c>values</c> frame from the device.</summary>
    public void OnValues(DeviceLinkSession session, JsonElement frame)
    {
        if (!_devices.TryGetValue(session.DeviceId, out var device))
            return;

        if (!frame.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            logger.LogDebug("Device {DeviceId}: values frame without a values array ignored", session.DeviceId);
            return;
        }

        lock (device)
        {
            if (!ReferenceEquals(device.Session, session) || device.Viewers.Count == 0)
                return;

            if (device.State != Streaming)
            {
                device.State = Streaming;
                Broadcast(device, StatusFrame(Streaming));
            }

            Broadcast(device, ValuesFrame(session, frame, values), isStatus: false);
        }
    }

    /// <summary>An <c>error</c> frame that answers <c>live.start</c>.</summary>
    public void OnError(DeviceLinkSession session, string code, int? retryAfterS)
    {
        if (!_devices.TryGetValue(session.DeviceId, out var device))
            return;

        lock (device)
        {
            if (!ReferenceEquals(device.Session, session))
                return;

            device.Running = false;
            device.ClearTimers();

            if (code == "busy")
            {
                var delay = TimeSpan.FromSeconds(Math.Clamp(retryAfterS ?? _options.BusyRetryS, 1, 600));
                logger.LogInformation("Device {DeviceId}: live stream busy, retrying in {Delay}", session.DeviceId, delay);
                device.RetryTimer = timeProvider.CreateTimer(_ => RetryStart(device), null, delay, Timeout.InfiniteTimeSpan);
                return;
            }

            // not_supported, bad_request or anything else: the device will not stream for this request.
            logger.LogWarning("Device {DeviceId}: live stream refused with '{Code}'", session.DeviceId, code);
            Refuse(device);
        }
    }

    private void RetryStart(DeviceLive device)
    {
        lock (device)
        {
            device.RetryTimer?.Dispose();
            device.RetryTimer = null;
            if (device.Session is not null && device.Viewers.Count > 0 && !device.Running)
                Start(device);
        }
    }

    private void StopIfIdle(DeviceLive device)
    {
        lock (device)
        {
            device.StopTimer?.Dispose();
            device.StopTimer = null;

            if (device.Viewers.Count > 0 || !device.Running || device.Session is null)
                return;

            device.Running = false;
            device.RenewTimer?.Dispose();
            device.RenewTimer = null;
            Send(device.Session, new JsonObject { ["op"] = "live.stop" });
        }
    }

    /// <summary>Sends <c>live.start</c> and keeps renewing it. Caller holds the lock; the session is set.</summary>
    private void Start(DeviceLive device)
    {
        var session = device.Session!;
        device.Running = true;
        device.State = Connecting;
        SendStart(session);

        var renew = TimeSpan.FromSeconds(_options.RenewIntervalS);
        device.RenewTimer?.Dispose();
        device.RenewTimer = timeProvider.CreateTimer(_ => SendStart(session), null, renew, renew);
    }

    private void SendStart(DeviceLinkSession session) =>
        Send(session, new JsonObject { ["op"] = "live.start", ["periodMs"] = _options.PeriodMs });

    /// <summary>The device does not stream: tell the viewers and end their streams.</summary>
    private void Refuse(DeviceLive device)
    {
        device.State = NotSupported;
        device.Running = false;
        device.ClearTimers();
        Broadcast(device, StatusFrame(NotSupported));
        foreach (var viewer in device.Viewers.Where(v => v.Active))
            viewer.Complete();
    }

    private void Send(DeviceLinkSession session, JsonObject message) =>
        _ = SendSafeAsync(session, message);

    private async Task SendSafeAsync(DeviceLinkSession session, JsonObject message)
    {
        try
        {
            await session.SendAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Device {DeviceId}: could not send {Op}", session.DeviceId, message["op"]);
        }
    }

    private static void Broadcast(DeviceLive device, string frame, bool isStatus = true)
    {
        foreach (var viewer in device.Viewers.Where(v => v.Active))
            viewer.Post(frame, isStatus);
    }

    private string StatusFrame(string state)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "status");
            writer.WriteString("state", state);
            writer.WriteNumber("periodMs", _options.PeriodMs);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary><c>{"type":"values","schema","t","atUtc","receivedAtUtc","values"}</c>; device fields are copied as sent.</summary>
    private string ValuesFrame(DeviceLinkSession session, JsonElement frame, JsonElement values)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "values");

            writer.WritePropertyName("schema");
            if (session.Hello.Schema is { } schema)
                schema.WriteTo(writer);
            else
                writer.WriteNullValue();

            writer.WritePropertyName("t");
            if (frame.TryGetProperty("t", out var t))
                t.WriteTo(writer);
            else
                writer.WriteNullValue();

            writer.WritePropertyName("atUtc");
            if (frame.TryGetProperty("atUtc", out var atUtc))
                atUtc.WriteTo(writer);
            else
                writer.WriteNullValue();

            writer.WriteString("receivedAtUtc", timeProvider.GetUtcNow().UtcDateTime);

            writer.WritePropertyName("values");
            values.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class DeviceLive(int deviceId)
    {
        public int DeviceId { get; } = deviceId;
        public List<LiveSubscription> Viewers { get; } = [];
        public DeviceLinkSession? Session { get; set; }

        /// <summary><c>live.start</c> was sent and is being renewed.</summary>
        public bool Running { get; set; }

        public string State { get; set; } = DeviceOffline;
        public ITimer? RenewTimer { get; set; }
        public ITimer? StopTimer { get; set; }
        public ITimer? RetryTimer { get; set; }

        public void ClearTimers()
        {
            RenewTimer?.Dispose();
            StopTimer?.Dispose();
            RetryTimer?.Dispose();
            RenewTimer = StopTimer = RetryTimer = null;
        }
    }
}
