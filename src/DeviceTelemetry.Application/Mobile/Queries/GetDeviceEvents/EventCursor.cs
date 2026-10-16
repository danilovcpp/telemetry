using System.Buffers.Binary;
using System.Buffers.Text;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;

/// <summary>
/// Opaque keyset cursor: the sort key (<c>OccurredAtUtc</c>, <c>Id</c>) of the last event on a page,
/// encoded as base64url of 8 bytes of UTC ticks followed by the 16-byte id.
/// </summary>
public readonly record struct EventCursor(DateTime OccurredAtUtc, Guid Id)
{
    private const int Length = sizeof(long) + 16;

    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, OccurredAtUtc.Ticks);
        Id.TryWriteBytes(bytes[sizeof(long)..], bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecode(string value, out EventCursor cursor)
    {
        cursor = default;

        // TryDecodeFromChars throws on invalid characters, so validate first.
        if (!Base64Url.IsValid(value, out var decodedLength) || decodedLength != Length)
            return false;

        Span<byte> bytes = stackalloc byte[Length];
        Base64Url.DecodeFromChars(value, bytes);

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return false;

        cursor = new EventCursor(new DateTime(ticks, DateTimeKind.Utc), new Guid(bytes[sizeof(long)..], bigEndian: true));
        return true;
    }
}
