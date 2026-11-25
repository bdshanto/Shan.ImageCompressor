// Small stand-ins for APIs older runtimes lack, so one source tree builds for net5.0 through net9.0.
// On a runtime that has the real thing, none of this is compiled.

using System.IO.Compression;

namespace Shan.ImageCompressor
{
    internal static class ZLibLevel
    {
        /// <summary>The strongest deflate setting the runtime offers (.NET 5 has no SmallestSize).</summary>
#if NET6_0_OR_GREATER
        public const CompressionLevel Smallest = CompressionLevel.SmallestSize;
#else
        public const CompressionLevel Smallest = CompressionLevel.Optimal;
#endif
    }
}

#if !NET7_0_OR_GREATER
namespace System.IO
{
    internal static class StreamReadExtensions
    {
        /// <summary>Fills the buffer or throws <see cref="EndOfStreamException"/>, like Stream.ReadExactly in .NET 7.</summary>
        public static void ReadExactly(this Stream stream, Span<byte> buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer[total..]);
                if (read == 0) throw new EndOfStreamException();
                total += read;
            }
        }
    }
}
#endif

#if !NET6_0_OR_GREATER
namespace System.IO.Compression
{
    /// <summary>A zlib stream (RFC 1950): a two-byte header, a deflate body and an Adler-32 checksum. .NET 5 has only the raw deflate part.</summary>
    internal sealed class ZLibStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly bool _writing;
        private DeflateStream? _deflate;
        private uint _a = 1, _b;
        private bool _headerDone;

        public ZLibStream(Stream stream, CompressionMode mode)
        {
            if (mode != CompressionMode.Decompress) throw new ArgumentException("Use the CompressionLevel constructor to compress.", nameof(mode));
            _inner = stream;
        }

        public ZLibStream(Stream stream, CompressionLevel level, bool leaveOpen)
        {
            _inner = stream;
            _leaveOpen = leaveOpen;
            _writing = true;
            _inner.WriteByte(0x78);
            _inner.WriteByte(level == CompressionLevel.NoCompression ? (byte)0x01 : level == CompressionLevel.Fastest ? (byte)0x5E : (byte)0x9C);
            _deflate = new DeflateStream(_inner, level, leaveOpen: true);
        }

        public override bool CanRead => !_writing;
        public override bool CanWrite => _writing;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _deflate?.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_writing) throw new NotSupportedException();
            if (!_headerDone)
            {
                var cmf = _inner.ReadByte();
                var flg = _inner.ReadByte();
                if (flg < 0 || (cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
                    throw new InvalidDataException("The zlib header is invalid.");
                _deflate = new DeflateStream(_inner, CompressionMode.Decompress, leaveOpen: true);
                _headerDone = true;
            }
            return _deflate!.Read(buffer, offset, count); // the trailing checksum is not verified, as callers already validate what they decode
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_writing) throw new NotSupportedException();
            for (var i = offset; i < offset + count; i++)
            {
                _a = (_a + buffer[i]) % 65521;
                _b = (_b + _a) % 65521;
            }
            _deflate!.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_writing && _deflate != null)
                {
                    _deflate.Dispose(); // flushes the final deflate block
                    var adler = (_b << 16) | _a;
                    _inner.WriteByte((byte)(adler >> 24));
                    _inner.WriteByte((byte)(adler >> 16));
                    _inner.WriteByte((byte)(adler >> 8));
                    _inner.WriteByte((byte)adler);
                }
                else _deflate?.Dispose();
                if (!_leaveOpen) _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
#endif
