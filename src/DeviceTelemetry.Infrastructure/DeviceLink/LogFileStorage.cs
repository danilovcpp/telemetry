using System.Globalization;
using DeviceTelemetry.Application.Abstractions.DeviceLink;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>
/// Copies of device data files on the local disk: <c>&lt;DataRoot&gt;/&lt;deviceId&gt;/&lt;device path&gt;</c>. Paths are
/// validated with <see cref="LogPaths"/> and the resolved path must stay inside the device directory.
/// </summary>
public sealed class LogFileStorage(IOptions<StorageOptions> options) : ILogFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.DataRoot);

    public Stream? OpenRead(int deviceId, string path, long length)
    {
        var fullPath = Resolve(deviceId, path);
        if (fullPath is null)
            return null;

        if (length == 0)
            return new MemoryStream([], writable: false);

        if (!File.Exists(fullPath))
            return null;

        // The sync may append to the file while it is served; the response is limited to the copied length.
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        if (stream.Length < length)
        {
            stream.Dispose();
            return null;
        }

        return new BoundedReadStream(stream, length);
    }

    /// <summary>
    /// Writes <paramref name="data"/> at <paramref name="offset"/>. Bytes after the offset are cut off first (left over from
    /// a write whose metadata update failed); offset 0 starts a new file.
    /// </summary>
    public async Task WriteAsync(int deviceId, string path, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var fullPath = Resolve(deviceId, path) ?? throw new ArgumentException("Invalid device file path.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = new FileStream(
            fullPath,
            offset == 0 ? FileMode.Create : FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            4096,
            useAsync: true);

        if (stream.Length < offset)
            throw new InvalidDataException($"The stored copy is shorter ({stream.Length}) than the expected offset ({offset}).");
        if (stream.Length > offset)
            stream.SetLength(offset);

        stream.Position = offset;
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public void Delete(int deviceId, string path)
    {
        if (Resolve(deviceId, path) is { } fullPath && File.Exists(fullPath))
            File.Delete(fullPath);
    }

    private string? Resolve(int deviceId, string path)
    {
        if (!LogPaths.IsValid(path))
            return null;

        var deviceRoot = Path.Combine(_root, deviceId.ToString(CultureInfo.InvariantCulture)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(deviceRoot, path.TrimStart('/')));
        return fullPath.StartsWith(deviceRoot, StringComparison.Ordinal) ? fullPath : null;
    }

    /// <summary>A seekable read-only view of the first <c>length</c> bytes, so that range processing sees the copied size.</summary>
    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Limit(count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, Limit(count), cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private int Limit(int count) => (int)Math.Max(0, Math.Min(count, length - inner.Position));
    }
}
