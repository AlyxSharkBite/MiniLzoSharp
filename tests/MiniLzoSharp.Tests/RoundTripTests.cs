namespace MiniLzoSharp.Tests;

public sealed class RoundTripTests
{
    private static readonly IMiniLzoCodec Codec = MiniLzoFactory.Create();

    private static void AssertRoundTrip(byte[] original)
    {
        byte[] compressed = Codec.Compress(original);
        byte[] decompressed = Codec.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void EmptyInput_RoundTrips()
    {
        byte[] compressed = Codec.Compress([]);
        // An empty block is exactly the LZO1X end-of-stream marker.
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00 }, compressed);
        Assert.Empty(Codec.Decompress(compressed));
    }

    [Fact]
    public void AllSmallSizes_RoundTrip()
    {
        // 1 byte through every small token-boundary size, with data that is
        // incompressible so each size exercises the pure-literal encodings.
        for (int size = 1; size <= 80; size++)
        {
            AssertRoundTrip(TestData.Random(size, seed: 1000 + size));
        }
    }

    [Theory]
    // Boundaries of the "17 + t" short-stream first byte (t <= 238) and of the
    // run-length-extended literal encodings (18, 273 = 18 + 255, ...).
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(237)]
    [InlineData(238)]
    [InlineData(239)]
    [InlineData(240)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(272)]
    [InlineData(273)]
    [InlineData(274)]
    [InlineData(528)]
    [InlineData(529)]
    [InlineData(530)]
    public void LiteralRunBoundaries_RoundTrip(int size)
    {
        AssertRoundTrip(TestData.Random(size, seed: 2000 + size));
    }

    [Theory]
    // Sizes straddling the compressor's 49152-byte chunking and its 20-byte tail guard.
    [InlineData(49131)]
    [InlineData(49132)]
    [InlineData(49151)]
    [InlineData(49152)]
    [InlineData(49153)]
    [InlineData(49172)]
    [InlineData(98303)]
    [InlineData(98304)]
    [InlineData(98305)]
    public void ChunkBoundaries_RoundTrip(int size)
    {
        AssertRoundTrip(TestData.Random(size, seed: 3000));
        AssertRoundTrip(TestData.Repetitive(size, period: 5));
        AssertRoundTrip(TestData.Text(size, seed: 3001));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(64)]
    public void RepetitivePeriods_RoundTrip(int period)
    {
        // Periods around the M2 (3..8), M3 (..33) and M4 (..9) match-length boundaries.
        foreach (int size in new[] { 100, 1000, 65536 })
        {
            AssertRoundTrip(TestData.Repetitive(size, period));
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(131072)]
    [InlineData(1000000)]
    public void ZerosAndFilled_RoundTrip(int size)
    {
        AssertRoundTrip(TestData.Zeros(size));
        AssertRoundTrip(TestData.Filled(size, 0xFF));
    }

    [Fact]
    public void ZerosCompressDensely()
    {
        // Sanity check that matches are actually found: 128 KiB of zeros must compress
        // to well under 1% of the input.
        byte[] compressed = Codec.Compress(TestData.Zeros(131072));
        Assert.InRange(compressed.Length, 4, 1311);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(4096)]
    [InlineData(200000)]
    public void SequentialBytes_RoundTrip(int size)
    {
        AssertRoundTrip(TestData.Sequential(size));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(100000)]
    [InlineData(500000)]
    public void TextExecutableAndMixed_RoundTrip(int size)
    {
        AssertRoundTrip(TestData.Text(size, seed: 42));
        AssertRoundTrip(TestData.ExecutableLike(size, seed: 43));
        AssertRoundTrip(TestData.Mixed(size, seed: 44));
    }

    [Fact]
    public void IncompressibleData_NeverExceedsWorstCaseBound()
    {
        foreach (int size in new[] { 1, 100, 4096, 65536, 300000 })
        {
            byte[] original = TestData.Random(size, seed: 4000 + size);
            byte[] compressed = Codec.Compress(original);
            Assert.True(
                compressed.Length <= MiniLzoCodec.GetMaxCompressedLength(size),
                $"size {size}: compressed to {compressed.Length}, bound {MiniLzoCodec.GetMaxCompressedLength(size)}");
            Assert.Equal(original, Codec.Decompress(compressed));
        }
    }

    [Fact]
    public void LargeRandomizedCorpus_RoundTrips()
    {
        // A few hundred buffers with varied sizes and content mixes, all seeded.
        var random = new Random(20260830);
        for (int i = 0; i < 300; i++)
        {
            int size = random.Next(6) switch
            {
                0 => random.Next(0, 70),
                1 => random.Next(70, 600),
                2 => random.Next(600, 5000),
                3 => random.Next(5000, 60000),
                4 => random.Next(60000, 200000),
                _ => random.Next(200000, 400000),
            };
            byte[] original = random.Next(4) switch
            {
                0 => TestData.Random(size, seed: i),
                1 => TestData.Repetitive(Math.Max(size, 1), period: random.Next(1, 40)),
                2 => TestData.Text(size, seed: i),
                _ => TestData.Mixed(size, seed: i),
            };
            AssertRoundTrip(original);
        }
    }
}
