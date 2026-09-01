using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace MiniLzoSharp.Benchmarks;

/// <summary>
/// Compression and decompression throughput plus allocation counts, over data classes
/// from best case (zeros) to worst case (incompressible random), at a small size (latency)
/// and a large size (throughput).
/// </summary>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private readonly IMiniLzoCodec _codec = MiniLzoFactory.Create();

    private byte[] _original = [];
    private byte[] _compressed = [];
    private byte[] _compressScratch = [];
    private byte[] _decompressScratch = [];
    private ushort[] _dictionary = [];

    [Params(4 * 1024, 1024 * 1024)]
    public int Size { get; set; }

    [Params("Zeros", "Repetitive", "Text", "Mixed", "Incompressible")]
    public string Kind { get; set; } = "Text";

    [GlobalSetup]
    public void Setup()
    {
        _original = BenchmarkData.Create(Kind, Size);
        _compressed = _codec.Compress(_original);
        _compressScratch = new byte[MiniLzoCodec.GetMaxCompressedLength(Size) + 16];
        _decompressScratch = new byte[Size];
        _dictionary = new ushort[MiniLzoCodec.DictionarySize];
    }

    /// <summary>The public array API: includes the result allocation and pooled rents.</summary>
    [Benchmark]
    public byte[] Compress() => _codec.Compress(_original);

    /// <summary>The public array API: includes buffer growth/restarts and the result allocation.</summary>
    [Benchmark]
    public byte[] Decompress() => _codec.Decompress(_compressed);

    /// <summary>The raw compressor core with caller-owned buffers: the zero-allocation hot path.</summary>
    [Benchmark]
    public int CompressCore() => MiniLzoCodec.CompressBlock(_original, _compressScratch, _dictionary);

    /// <summary>The raw safe-decompressor core with a caller-owned, exactly-sized buffer.</summary>
    [Benchmark]
    public int DecompressCore() => MiniLzoCodec.DecompressBlock(_compressed, _decompressScratch, out _);

    /// <summary>Framed stream round-trip cost including block headers and pooled buffers.</summary>
    [Benchmark]
    public long StreamCompress()
    {
        using var source = new MemoryStream(_original);
        using var destination = new MemoryStream();
        _codec.Compress(source, destination);
        return destination.Length;
    }
}
