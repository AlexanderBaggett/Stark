using System.Buffers.Binary;
using System.IO.Compression;

namespace Stark.ReleaseTools;

internal sealed class DeterministicGZipWriteStream : Stream
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private readonly Stream _destination;
    private readonly DeflateStream _deflate;
    private uint _crc = 0xffffffff;
    private uint _size;
    private bool _disposed;

    public DeterministicGZipWriteStream(Stream destination)
    {
        _destination = destination;
        destination.Write([0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xff]);
        _deflate = new DeflateStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _deflate.Flush();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var value in buffer)
        {
            _crc = CrcTable[(_crc ^ value) & 0xff] ^ (_crc >> 8);
        }

        _size = unchecked(_size + (uint)buffer.Length);
        _deflate.Write(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _deflate.Dispose();
            Span<byte> trailer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(trailer, ~_crc);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], _size);
            _destination.Write(trailer);
            _destination.Flush();
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
