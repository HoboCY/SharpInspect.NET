using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Images;

/// <summary>
/// Bounded streaming PNG for the canonical Mono8/Mono16/Bgr24 evidence formats. Streams
/// belong to the caller. This codec never names, publishes, deletes or grants authority
/// over a file. PNG byte order, filtering and CRC follow https://www.w3.org/TR/png-3/.
/// </summary>
internal static class CanonicalPngCodec
{
    internal const int ChunkBufferBytes = 64 * 1024;
    private const int CompressionWorkingReserve = 2 * 1024 * 1024;
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Read-only startup proof of the complete stage; grants no commit authority.</summary>
    internal static void VerifyStage(Stream canonicalStage, CanonicalPngDescriptor descriptor,
        PngCodecLimits limits, StoreDeadline deadline, CancellationToken token = default)
    {
        var context = Prepare(descriptor, limits, deadline, token);
        RequireInput(canonicalStage, context.CanonicalLength);
        if (canonicalStage.Length != context.CanonicalLength) throw Invalid("StageLengthInvalid");
        Span<byte> header = stackalloc byte[32];
        ReadExactly(canonicalStage, header, context);
        if (!header.SequenceEqual(context.Envelope)) throw Invalid("StageEnvelopeMismatch");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        var row = new byte[context.RowBytes];
        for (var y = 0; y < descriptor.Height; y++)
        {
            ReadExactly(canonicalStage, row, context);
            CheckMono16(row, descriptor);
            hash.AppendData(row);
        }
        context.Check();
        if (canonicalStage.Position != context.CanonicalLength ||
            Convert.ToHexString(hash.GetHashAndReset()) != descriptor.CanonicalPixelHash)
            throw Invalid("StagePixelHashMismatch");
    }

    internal static long Encode(Stream canonicalStage, Stream png, CanonicalPngDescriptor descriptor,
        PngCodecLimits limits, StoreDeadline deadline, CancellationToken token = default)
    {
        var context = Prepare(descriptor, limits, deadline, token);
        RequireInput(canonicalStage, context.CanonicalLength);
        if (canonicalStage.Length != context.CanonicalLength)
            throw Invalid("StageLengthInvalid");
        ArgumentNullException.ThrowIfNull(png);
        if (!png.CanWrite || !png.CanSeek || png.Position != 0 || png.Length != 0)
            throw Invalid("OutputMustBeNewSeekableStream");

        Span<byte> envelope = stackalloc byte[32];
        ReadExactly(canonicalStage, envelope, context);
        if (!envelope.SequenceEqual(context.Envelope)) throw Invalid("StageEnvelopeMismatch");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(envelope);
        var output = new PngOutput(png, context);
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)descriptor.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)descriptor.Height);
        header[8] = descriptor.PixelFormat == VisionPixelFormat.Mono16 ? (byte)16 : (byte)8;
        header[9] = descriptor.PixelFormat == VisionPixelFormat.Bgr24 ? (byte)2 : (byte)0;
        output.WriteChunk("IHDR", header);
        var row = new byte[context.RowBytes];
        using (var idat = new IdatWriteStream(output))
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < descriptor.Height; y++)
                {
                    ReadExactly(canonicalStage, row, context);
                    CheckMono16(row, descriptor);
                    hash.AppendData(row);
                    SwapPixelOrder(row, descriptor.PixelFormat);
                    zlib.WriteByte(0); // Filter None; no rendering or sample-value transform.
                    zlib.Write(row);
                    context.Check();
                }
            }
            idat.Complete();
        }
        context.Check();
        if (canonicalStage.Position != context.CanonicalLength ||
            !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), descriptor.CanonicalPixelHash,
                StringComparison.Ordinal)) throw Invalid("StagePixelHashMismatch");
        output.WriteChunk("IEND", ReadOnlySpan<byte>.Empty);
        context.Check();
        return output.BytesWritten;
    }

    internal static VerifiedCanonicalPng Verify(Stream png, CanonicalPngDescriptor descriptor,
        PngCodecLimits limits, StoreDeadline deadline, CancellationToken token = default)
    {
        var context = Prepare(descriptor, limits, deadline, token);
        RequireInput(png, limits.MaximumEncodedBytes);
        var encodedLength = png.Length;
        Span<byte> signature = stackalloc byte[8];
        ReadExactly(png, signature, context);
        if (!signature.SequenceEqual(Signature)) throw Invalid("SignatureInvalid");
        var chunks = new PngChunkReader(png, context);
        chunks.Next();
        if (chunks.Type != "IHDR" || chunks.Remaining != 13) throw Invalid("HeaderInvalid");
        Span<byte> header = stackalloc byte[13];
        chunks.ReadDataExactly(header);
        chunks.FinishChunk();
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != descriptor.Width ||
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != descriptor.Height ||
            header[8] != (descriptor.PixelFormat == VisionPixelFormat.Mono16 ? 16 : 8) ||
            header[9] != (descriptor.PixelFormat == VisionPixelFormat.Bgr24 ? 2 : 0) ||
            header[10] != 0 || header[11] != 0 || header[12] != 0)
            throw Invalid("HeaderDescriptorMismatch");
        chunks.Next();
        while (chunks.Type != "IDAT")
        {
            chunks.SkipAncillary();
            chunks.Next();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(context.Envelope);
        var row = new byte[context.RowBytes];
        var previous = new byte[context.RowBytes];
        using (var idat = new IdatReadStream(chunks))
        {
            using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
            {
                Span<byte> filter = stackalloc byte[1];
                for (var y = 0; y < descriptor.Height; y++)
                {
                    ReadExactly(zlib, filter, context);
                    ReadExactly(zlib, row, context);
                    Unfilter(row, previous, context.BytesPerPixel, filter[0]);
                    SwapPixelOrder(row, descriptor.PixelFormat);
                    CheckMono16(row, descriptor);
                    hash.AppendData(row);
                    // PNG filters refer to the preceding row in PNG byte order.
                    SwapPixelOrder(row, descriptor.PixelFormat);
                    (row, previous) = (previous, row);
                }
                context.Check();
                if (zlib.Read(filter) != 0) throw Invalid("DecodedLengthInvalid");
            }
            // Includes CRCs for bytes buffered by zlib and permitted unused IDAT bytes.
            idat.Complete();
        }
        while (chunks.Type != "IEND")
        {
            chunks.SkipAncillary();
            chunks.Next();
        }
        if (chunks.Remaining != 0) throw Invalid("EndChunkInvalid");
        chunks.FinishChunk();
        context.Check();
        if (png.Position != encodedLength) throw Invalid("TrailingFileContent");
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(digest, descriptor.CanonicalPixelHash, StringComparison.Ordinal))
            throw Invalid("PixelHashMismatch");
        return new VerifiedCanonicalPng(encodedLength, digest);
    }

    private static CodecContext Prepare(CanonicalPngDescriptor descriptor, PngCodecLimits limits,
        StoreDeadline deadline, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(deadline);
        token.ThrowIfCancellationRequested();
        if (deadline.Expired) throw new TimeoutException("ProductionPngDeadlineExceeded");
        var envelope = CanonicalImagePixelContent.CreateEnvelope(descriptor.Width, descriptor.Height,
            descriptor.PixelFormat, descriptor.ValidBits);
        if (descriptor.CanonicalPixelHash is not { Length: 64 } ||
            descriptor.CanonicalPixelHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw Invalid("DescriptorHashInvalid");
        var bytesPerPixel = descriptor.PixelFormat == VisionPixelFormat.Mono16 ? 2 :
            descriptor.PixelFormat == VisionPixelFormat.Bgr24 ? 3 : 1;
        var rowBytes = checked(descriptor.Width * bytesPerPixel);
        var length = checked(32L + (long)rowBytes * descriptor.Height);
        if (limits.MaximumCanonicalBytes is < 33 or > ProductionImageStageOptions.MaximumAllowedStageBytes ||
            limits.MaximumEncodedBytes is < 1 or > 1024L * 1024 * 1024 ||
            limits.MaximumWorkingBytes is < 1 or > 8 * 1024 * 1024 ||
            limits.MaximumChunks is < 3 or > 1_000_000)
            throw Invalid("LimitsInvalid");
        if (length > limits.MaximumCanonicalBytes ||
            CompressionWorkingReserve + ChunkBufferBytes + 2 * rowBytes > limits.MaximumWorkingBytes)
            throw Invalid("MemoryOrPixelBudgetExceeded");
        return new CodecContext(envelope, rowBytes, bytesPerPixel, length, limits, deadline, token);
    }

    private static void RequireInput(Stream input, long maximumLength)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek || input.Position != 0)
            throw Invalid("InputMustBeSeekableAtOrigin");
        if (input.Length < 1 || input.Length > maximumLength) throw Invalid("InputLengthBudgetExceeded");
    }

    private static void ReadExactly(Stream stream, Span<byte> destination, CodecContext context)
    {
        while (!destination.IsEmpty)
        {
            context.Check();
            var count = stream.Read(destination);
            if (count <= 0) throw Invalid("TruncatedContent");
            destination = destination[count..];
        }
    }

    private static void SwapPixelOrder(Span<byte> row, VisionPixelFormat format)
    {
        if (format == VisionPixelFormat.Mono16)
            for (var i = 0; i < row.Length; i += 2) (row[i], row[i + 1]) = (row[i + 1], row[i]);
        else if (format == VisionPixelFormat.Bgr24)
            for (var i = 0; i < row.Length; i += 3) (row[i], row[i + 2]) = (row[i + 2], row[i]);
    }

    private static void CheckMono16(ReadOnlySpan<byte> row, CanonicalPngDescriptor descriptor)
    {
        if (descriptor.PixelFormat != VisionPixelFormat.Mono16 || descriptor.ValidBits == 16) return;
        var maximum = (1 << descriptor.ValidBits!.Value) - 1;
        for (var i = 0; i < row.Length; i += 2)
            if (BinaryPrimitives.ReadUInt16LittleEndian(row[i..]) > maximum)
                throw Invalid("Mono16HighBitsInvalid");
    }

    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, byte filter)
    {
        if (filter > 4) throw Invalid("FilterInvalid");
        if (filter == 0) return;
        for (var i = 0; i < row.Length; i++)
        {
            var left = i < bpp ? 0 : row[i - bpp];
            var up = previous[i];
            var corner = i < bpp ? 0 : previous[i - bpp];
            var predictor = filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                _ => Paeth(left, up, corner)
            };
            row[i] = unchecked((byte)(row[i] + predictor));
        }
    }

    private static int Paeth(int left, int up, int corner)
    {
        var p = left + up - corner;
        var a = Math.Abs(p - left);
        var b = Math.Abs(p - up);
        var c = Math.Abs(p - corner);
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    private static InvalidDataException Invalid(string reason) => new("ProductionPng" + reason);

    private sealed record CodecContext(byte[] Envelope, int RowBytes, int BytesPerPixel,
        long CanonicalLength, PngCodecLimits Limits, StoreDeadline Deadline, CancellationToken Token)
    {
        internal void Check()
        {
            Token.ThrowIfCancellationRequested();
            if (Deadline.Expired) throw new TimeoutException("ProductionPngDeadlineExceeded");
        }
    }

    private sealed class PngOutput
    {
        private readonly Stream _stream;
        private readonly CodecContext _context;
        private int _chunks;
        internal long BytesWritten { get; private set; }
        internal PngOutput(Stream stream, CodecContext context) { _stream = stream; _context = context; }
        internal void Write(ReadOnlySpan<byte> bytes)
        {
            _context.Check();
            if (bytes.Length > _context.Limits.MaximumEncodedBytes - BytesWritten)
                throw Invalid("EncodedBudgetExceeded");
            _stream.Write(bytes);
            BytesWritten += bytes.Length;
        }
        internal void WriteChunk(string type, ReadOnlySpan<byte> data)
        {
            if (++_chunks > _context.Limits.MaximumChunks) throw Invalid("ChunkBudgetExceeded");
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
            for (var i = 0; i < 4; i++) header[i + 4] = (byte)type[i];
            Write(header);
            Write(data);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, ~UpdateCrc(UpdateCrc(uint.MaxValue, header[4..]), data));
            Write(crc);
        }
    }

    private sealed class PngChunkReader
    {
        private readonly Stream _stream;
        private readonly CodecContext _context;
        private readonly byte[] _skip = new byte[ChunkBufferBytes];
        private uint _crc;
        private int _chunks;
        private bool _open;
        internal string Type { get; private set; } = string.Empty;
        internal long Remaining { get; private set; }
        internal PngChunkReader(Stream stream, CodecContext context) { _stream = stream; _context = context; }
        internal void Next()
        {
            if (_open) throw Invalid("ChunkNotConsumed");
            if (++_chunks > _context.Limits.MaximumChunks) throw Invalid("ChunkBudgetExceeded");
            Span<byte> header = stackalloc byte[8];
            ReadExactly(_stream, header, _context);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > int.MaxValue || length > _stream.Length - _stream.Position - 4)
                throw Invalid("ChunkLengthInvalid");
            for (var i = 4; i < 8; i++)
                if (header[i] is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw Invalid("ChunkTypeInvalid");
            if ((header[6] & 32) != 0) throw Invalid("ChunkReservedBitInvalid");
            Type = System.Text.Encoding.ASCII.GetString(header[4..]);
            Remaining = length;
            _crc = UpdateCrc(uint.MaxValue, header[4..]);
            _open = true;
        }
        internal int ReadData(Span<byte> bytes)
        {
            _context.Check();
            var count = (int)Math.Min(Remaining, bytes.Length);
            if (count == 0) return 0;
            ReadExactly(_stream, bytes[..count], _context);
            _crc = UpdateCrc(_crc, bytes[..count]);
            Remaining -= count;
            return count;
        }
        internal void ReadDataExactly(Span<byte> bytes)
        {
            if (ReadData(bytes) != bytes.Length) throw Invalid("ChunkDataLengthInvalid");
        }
        internal void FinishChunk()
        {
            if (!_open || Remaining != 0) throw Invalid("ChunkNotConsumed");
            Span<byte> checksum = stackalloc byte[4];
            ReadExactly(_stream, checksum, _context);
            if (BinaryPrimitives.ReadUInt32BigEndian(checksum) != ~_crc) throw Invalid("ChunkCrcMismatch");
            _open = false;
        }
        internal void SkipData()
        {
            while (Remaining != 0) _ = ReadData(_skip);
            FinishChunk();
        }
        internal void SkipAncillary()
        {
            // Canonical evidence has no palette, transparency or animation. Colour/profile
            // metadata never changes the raw samples used for our canonical digest.
            if (Type.Length != 4 || (Type[0] & 32) == 0 || Type is "tRNS" or "acTL" or "fcTL" or "fdAT")
                throw Invalid("UnsupportedChunk");
            SkipData();
        }
    }

    private sealed class IdatWriteStream : Stream
    {
        private readonly PngOutput _output;
        private readonly byte[] _buffer = new byte[ChunkBufferBytes];
        private int _count;
        internal IdatWriteStream(PngOutput output) => _output = output;
        internal void Complete() { if (_count != 0) Flush(); }
        public override void Flush()
        {
            if (_count == 0) return;
            _output.WriteChunk("IDAT", _buffer.AsSpan(0, _count));
            _count = 0;
        }
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var count = Math.Min(bytes.Length, _buffer.Length - _count);
                bytes[..count].CopyTo(_buffer.AsSpan(_count));
                _count += count;
                bytes = bytes[count..];
                if (_count == _buffer.Length) Flush();
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class IdatReadStream : Stream
    {
        private readonly PngChunkReader _chunks;
        private bool _ended;
        internal IdatReadStream(PngChunkReader chunks) => _chunks = chunks;
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            while (!_ended)
            {
                if (_chunks.Remaining != 0) return _chunks.ReadData(buffer);
                _chunks.FinishChunk();
                _chunks.Next();
                if (_chunks.Type != "IDAT") _ended = true;
            }
            // A complete zlib stream stops itself after its validated checksum. Returning
            // EOF here would let ZLibStream accept some truncated deflate/footer streams.
            throw Invalid("CompressedStreamTruncated");
        }
        internal void Complete()
        {
            while (!_ended)
            {
                _chunks.SkipData();
                _chunks.Next();
                if (_chunks.Type != "IDAT") _ended = true;
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var value = n;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[n] = value;
        }
        return table;
    }
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }
}
