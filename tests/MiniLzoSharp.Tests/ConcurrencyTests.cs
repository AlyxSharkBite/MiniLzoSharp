namespace MiniLzoSharp.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public void SingleCodecInstance_IsSafeForConcurrentUse()
    {
        IMiniLzoCodec codec = MiniLzoFactory.Create();

        // Hammer one shared instance from many threads with distinct payloads; any
        // shared mutable state would corrupt at least one round trip.
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            byte[] original = (i % 3) switch
            {
                0 => TestData.Random(50000 + i * 17, seed: i),
                1 => TestData.Repetitive(80000 + i * 13, period: 1 + (i % 40)),
                _ => TestData.Mixed(120000 + i * 11, seed: i),
            };

            byte[] roundTripped = codec.Decompress(codec.Compress(original));
            Assert.Equal(original, roundTripped);

            using var source = new MemoryStream(original);
            using var framed = new MemoryStream();
            codec.Compress(source, framed);
            framed.Position = 0;
            using var output = new MemoryStream();
            codec.Decompress(framed, output);
            Assert.Equal(original, output.ToArray());
        });
    }
}
