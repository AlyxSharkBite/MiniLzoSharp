namespace MiniLzoSharp.Tests;

public sealed class StreamTests
{
    private static readonly IMiniLzoCodec Codec = MiniLzoFactory.Create();

    private static byte[] CompressToBytes(byte[] original)
    {
        using var source = new MemoryStream(original);
        using var destination = new MemoryStream();
        Codec.Compress(source, destination);
        return destination.ToArray();
    }

    private static byte[] DecompressToBytes(byte[] framed)
    {
        using var source = new MemoryStream(framed);
        using var destination = new MemoryStream();
        Codec.Decompress(source, destination);
        return destination.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(262143)]      // one byte under the 256 KiB block size
    [InlineData(262144)]      // exactly one block
    [InlineData(262145)]      // one byte into the second block
    [InlineData(600000)]      // three blocks
    [InlineData(1048593)]     // 1 MiB + 17: five blocks with a short tail
    public void FramedStream_RoundTrips(int size)
    {
        byte[] original = TestData.Mixed(size, seed: size);
        byte[] framed = CompressToBytes(original);
        Assert.Equal(original, DecompressToBytes(framed));
    }

    [Fact]
    public void EmptyStream_RoundTrips()
    {
        byte[] framed = CompressToBytes([]);
        // Header (13 bytes) + end-of-stream marker (1 byte), nothing else.
        Assert.Equal(14, framed.Length);
        Assert.Empty(DecompressToBytes(framed));
    }

    [Fact]
    public void IncompressibleBlocks_AreStoredVerbatim_WithBoundedOverhead()
    {
        byte[] original = TestData.Random(600000, seed: 55);
        byte[] framed = CompressToBytes(original);
        // Stored blocks add only the 9-byte block header each; total overhead stays tiny.
        Assert.InRange(framed.Length, original.Length, original.Length + 100);
        Assert.Equal(original, DecompressToBytes(framed));
    }

    [Fact]
    public void CompressibleStream_ActuallyShrinks()
    {
        byte[] original = TestData.Repetitive(1_000_000, period: 7);
        byte[] framed = CompressToBytes(original);
        Assert.True(framed.Length < original.Length / 10, $"framed size {framed.Length}");
        Assert.Equal(original, DecompressToBytes(framed));
    }

    [Fact]
    public void NonSeekablePartialReadStreams_AreSupported_AndNotDisposed()
    {
        byte[] original = TestData.Text(300000, seed: 8);

        using var backingSource = new MemoryStream(original);
        var source = new TricklingReadStream(backingSource, maxChunk: 3);
        using var backingDestination = new MemoryStream();
        var destination = new NonSeekableWriteStream(backingDestination);

        Codec.Compress(source, destination);
        Assert.False(source.WasDisposed);
        Assert.False(destination.WasDisposed);
        Assert.True(destination.CanWrite);

        byte[] framed = backingDestination.ToArray();

        using var backingFramed = new MemoryStream(framed);
        var framedSource = new TricklingReadStream(backingFramed, maxChunk: 2);
        using var backingOutput = new MemoryStream();
        var output = new NonSeekableWriteStream(backingOutput);

        Codec.Decompress(framedSource, output);
        Assert.False(framedSource.WasDisposed);
        Assert.False(output.WasDisposed);
        Assert.Equal(original, backingOutput.ToArray());
    }

    [Fact]
    public void BytesAfterEndOfStreamMarker_AreLeftUnread()
    {
        byte[] original = TestData.Text(1000, seed: 21);
        byte[] framed = CompressToBytes(original);
        byte[] withTrailing = [.. framed, 0xDE, 0xAD];

        using var source = new MemoryStream(withTrailing);
        using var destination = new MemoryStream();
        Codec.Decompress(source, destination);
        Assert.Equal(original, destination.ToArray());
        // Exactly the two trailing bytes remain unconsumed.
        Assert.Equal(framed.Length, source.Position);
    }

    [Fact]
    public void InvalidStreamArguments_Throw()
    {
        using var readable = new MemoryStream([1, 2, 3]);
        using var writable = new MemoryStream();
        using var readOnly = new MemoryStream([1, 2, 3], writable: false);
        var writeOnly = new NonSeekableWriteStream(writable);

        Assert.Throws<ArgumentException>(() => Codec.Compress(readable, readable));
        Assert.Throws<ArgumentException>(() => Codec.Decompress(readable, readable));
        Assert.Throws<ArgumentException>(() => Codec.Compress(writeOnly, writable));
        Assert.Throws<ArgumentException>(() => Codec.Compress(readable, readOnly));
        Assert.Throws<ArgumentException>(() => Codec.Decompress(writeOnly, writable));
        Assert.Throws<ArgumentException>(() => Codec.Decompress(readable, readOnly));
    }

    // ----- Framing validation ---------------------------------------------------------

    private static byte[] ValidFramed => CompressToBytes(TestData.Text(5000, seed: 3));

    private static void AssertInvalid(byte[] framed)
    {
        using var source = new MemoryStream(framed);
        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => Codec.Decompress(source, destination));
    }

    [Fact]
    public void BadMagic_Throws()
    {
        byte[] framed = ValidFramed;
        framed[0] ^= 0xFF;
        AssertInvalid(framed);
    }

    [Fact]
    public void UnsupportedVersion_Throws()
    {
        byte[] framed = ValidFramed;
        framed[8] = 2;
        AssertInvalid(framed);
    }

    [Fact]
    public void InvalidDeclaredBlockSize_Throws()
    {
        byte[] framed = ValidFramed;
        // Block size 0.
        framed[9] = 0;
        framed[10] = 0;
        framed[11] = 0;
        framed[12] = 0;
        AssertInvalid(framed);

        framed = ValidFramed;
        // Block size far beyond the accepted maximum.
        framed[9] = 0xFF;
        framed[10] = 0xFF;
        framed[11] = 0xFF;
        framed[12] = 0xFF;
        AssertInvalid(framed);
    }

    [Fact]
    public void UnknownBlockType_Throws()
    {
        byte[] framed = ValidFramed;
        framed[13] = 3;
        AssertInvalid(framed);
    }

    [Fact]
    public void MissingEndOfStreamMarker_Throws()
    {
        byte[] framed = ValidFramed;
        AssertInvalid(framed.AsSpan(0, framed.Length - 1).ToArray());
    }

    [Fact]
    public void TruncationAtEveryStructuralPoint_Throws()
    {
        byte[] framed = ValidFramed;
        // Header, block header and payload truncations all must throw.
        foreach (int length in new[] { 0, 5, 12, 13, 14, 17, 21, 22, 40, framed.Length - 2 })
        {
            if (length < framed.Length)
            {
                AssertInvalid(framed.AsSpan(0, length).ToArray());
            }
        }
    }

    [Fact]
    public void UncompressedLengthAboveDeclaredBlockSize_Throws()
    {
        byte[] framed = ValidFramed;
        // Shrink the declared block size to below the block's uncompressed length.
        framed[9] = 1;
        framed[10] = 0;
        framed[11] = 0;
        framed[12] = 0;
        AssertInvalid(framed);
    }

    [Fact]
    public void CompressedLengthNotSmallerThanUncompressed_Throws()
    {
        // Hand-build a frame: header + compressed block claiming clen == ulen.
        byte[] framed = new byte[13 + 9 + 5 + 1];
        new byte[] { 0x89, (byte)'M', (byte)'L', (byte)'Z', (byte)'S', 0x0D, 0x0A, 0x1A }.CopyTo(framed, 0);
        framed[8] = 1;
        BitConverter.GetBytes(262144u).CopyTo(framed, 9);
        framed[13] = 1; // compressed
        BitConverter.GetBytes(5u).CopyTo(framed, 14); // uncompressed length
        BitConverter.GetBytes(5u).CopyTo(framed, 18); // compressed length == uncompressed
        AssertInvalid(framed);
    }

    [Fact]
    public void PayloadDecompressingToWrongLength_Throws()
    {
        // Hand-build a frame whose payload is a valid LZO1X block for 4 bytes but whose
        // header declares 10.
        var codec = MiniLzoFactory.Create();
        byte[] payload = codec.Compress([1, 2, 3, 4]);
        // Compress of 4 bytes yields >= 4 bytes, so grow the declared sizes such that
        // clen < ulen holds and the frame is structurally valid.
        byte[] framed = new byte[13 + 9 + payload.Length + 1];
        new byte[] { 0x89, (byte)'M', (byte)'L', (byte)'Z', (byte)'S', 0x0D, 0x0A, 0x1A }.CopyTo(framed, 0);
        framed[8] = 1;
        BitConverter.GetBytes(262144u).CopyTo(framed, 9);
        framed[13] = 1;
        BitConverter.GetBytes(10u).CopyTo(framed, 14);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(framed, 18);
        payload.CopyTo(framed, 22);
        framed[^1] = 0;
        AssertInvalid(framed);
    }

    [Fact]
    public void StoredBlockWithMismatchedLengths_Throws()
    {
        byte[] framed = new byte[13 + 9 + 6 + 1];
        new byte[] { 0x89, (byte)'M', (byte)'L', (byte)'Z', (byte)'S', 0x0D, 0x0A, 0x1A }.CopyTo(framed, 0);
        framed[8] = 1;
        BitConverter.GetBytes(262144u).CopyTo(framed, 9);
        framed[13] = 2; // stored
        BitConverter.GetBytes(5u).CopyTo(framed, 14);
        BitConverter.GetBytes(6u).CopyTo(framed, 18);
        AssertInvalid(framed);
    }
}
