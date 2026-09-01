namespace MiniLzoSharp.Tests;

public sealed class ErrorHandlingTests
{
    private static readonly IMiniLzoCodec Codec = MiniLzoFactory.Create();

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Codec.Compress((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => Codec.Decompress((byte[])null!));
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => Codec.Compress(null!, stream));
        Assert.Throws<ArgumentNullException>(() => Codec.Compress(stream, null!));
        Assert.Throws<ArgumentNullException>(() => Codec.Decompress(null!, stream));
        Assert.Throws<ArgumentNullException>(() => Codec.Decompress(stream, null!));
    }

    [Fact]
    public void EmptyCompressedInput_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Codec.Decompress([]));
    }

    [Fact]
    public void TruncatedEndOfStreamMarker_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Codec.Decompress([0x11]));
        Assert.Throws<InvalidDataException>(() => Codec.Decompress([0x11, 0x00]));
    }

    [Fact]
    public void TrailingBytesAfterEndOfStream_Throw()
    {
        Assert.Throws<InvalidDataException>(() => Codec.Decompress([0x11, 0x00, 0x00, 0x00]));

        byte[] valid = Codec.Compress(TestData.Text(500, seed: 7));
        byte[] withTrailing = [.. valid, 0xAB];
        Assert.Throws<InvalidDataException>(() => Codec.Decompress(withTrailing));
    }

    [Fact]
    public void EveryStrictPrefix_OfValidBlock_Throws()
    {
        // Truncation at any point removes the end-of-stream marker, so every strict
        // prefix must be rejected -- never silently decoded.
        byte[] compressed = Codec.Compress(TestData.Text(400, seed: 11));
        for (int length = 0; length < compressed.Length; length++)
        {
            byte[] prefix = compressed.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => Codec.Decompress(prefix));
        }
    }

    [Fact]
    public void InvalidMatchOffset_AfterLiteralRun_Throws()
    {
        // 21 => copy 4 literals; token 0x21 is an M3 match; offset bytes 0xFF 0xFF give
        // offset 16384, far beyond the 4 bytes decompressed so far.
        byte[] data = [21, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x21, 0xFF, 0xFF, 0x11, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => Codec.Decompress(data));
    }

    [Fact]
    public void InvalidM2MatchOffset_Throws()
    {
        // Token 0x45 is an M2 match with offset 2 + 8 * 0xFF = 2042 > 4 bytes written.
        byte[] data = [21, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x45, 0xFF, 0x11, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => Codec.Decompress(data));
    }

    [Fact]
    public void RunLengthExtensionWithoutData_Throws_Fast()
    {
        // A literal run claiming to be enormous (many 0x00 extension bytes) with no
        // literal data behind it must fail quickly with a controlled exception rather
        // than hang or allocate wildly.
        byte[] data = new byte[100_000];
        Assert.Throws<InvalidDataException>(() => Codec.Decompress(data));
    }

    [Fact]
    public void MatchLengthExtensionOverflowAttempt_Throws()
    {
        // M3 token with zero length bits followed by a huge run of 0x00 extension bytes
        // tries to build an absurd match length; the decoder must reject it without
        // overflow. (Upstream guards this with TEST_OV; we accumulate in 64-bit.)
        byte[] data = new byte[70_000];
        data[0] = 21;
        data[1] = (byte)'a';
        data[2] = (byte)'b';
        data[3] = (byte)'c';
        data[4] = (byte)'d';
        data[5] = 0x20; // M3, length bits 0 -> run-length extended
        // data[6..] is already zero: thousands of extension bytes, then truncation.
        Assert.Throws<InvalidDataException>(() => Codec.Decompress(data));
    }

    [Fact]
    public void BitFlipFuzz_NeverCrashesOrHangs()
    {
        byte[] compressed = Codec.Compress(TestData.Mixed(2000, seed: 99));
        for (int bytePos = 0; bytePos < compressed.Length; bytePos++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] mutated = (byte[])compressed.Clone();
                mutated[bytePos] ^= (byte)(1 << bit);
                try
                {
                    // A flipped bit may still form a *valid* stream (e.g. inside a
                    // literal byte), so success is acceptable; what must never happen
                    // is a crash, a hang, or a non-InvalidDataException error.
                    Codec.Decompress(mutated);
                }
                catch (InvalidDataException)
                {
                }
            }
        }
    }

    [Fact]
    public void RandomGarbage_NeverCrashesOrHangs()
    {
        var random = new Random(31337);
        for (int i = 0; i < 2000; i++)
        {
            byte[] garbage = TestData.Random(random.Next(1, 300), seed: i);
            try
            {
                Codec.Decompress(garbage);
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
