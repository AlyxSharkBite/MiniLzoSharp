using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;

namespace MiniLzoSharp.Benchmarks;

/// <summary>
/// Evaluates hardware-intrinsic candidates for the compressor's match-length scan — the
/// hottest inner loop — against the 8-byte XOR + TZCNT approach the codec actually uses
/// (which itself compiles to BMI1 TZCNT through <see cref="BitOperations"/>).
///
/// This exists to satisfy the "measure before keeping intrinsics" rule: the explicit
/// SSE2/AVX2 variants are kept here, in the benchmark project only, as evidence for the
/// choice documented in the README. LZO1X-1 matches are short (median well under 16
/// bytes), so wider compares pay their fixed cost without finding longer runs to amortize
/// it over.
/// </summary>
public class MatchScanBenchmarks
{
    private byte[] _data = [];
    private (int A, int B)[] _pairs = [];

    /// <summary>Average length of the common prefix at each probed pair.</summary>
    [Params(6, 32, 250)]
    public int TypicalMatchLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1234);
        _data = new byte[1 << 20];
        random.NextBytes(_data);

        // Plant pairs of positions sharing a common prefix of roughly the configured
        // length, mimicking the pairs the hash table hands the compressor.
        _pairs = new (int, int)[4096];
        for (int i = 0; i < _pairs.Length; i++)
        {
            int a = random.Next(0, _data.Length / 2 - 512);
            int b = random.Next(_data.Length / 2, _data.Length - 512);
            int matchLength = Math.Max(4, TypicalMatchLength + random.Next(-3, 4));
            Array.Copy(_data, a, _data, b, matchLength);
            _pairs[i] = (a, b);
        }
    }

    [Benchmark(Baseline = true)]
    public int Scalar64BitXorTzcnt()
    {
        int total = 0;
        ref byte basis = ref MemoryMarshal.GetArrayDataReference(_data);
        foreach ((int a, int b) in _pairs)
        {
            int length = 0;
            while (length < 264)
            {
                ulong diff = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref basis, a + length))
                           ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref basis, b + length));
                if (diff != 0)
                {
                    length += BitOperations.TrailingZeroCount(diff) >> 3;
                    break;
                }

                length += 8;
            }

            total += length;
        }

        return total;
    }

    [Benchmark]
    public int Sse2CompareMoveMask()
    {
        if (!Sse2.IsSupported)
        {
            return Scalar64BitXorTzcnt();
        }

        int total = 0;
        ref byte basis = ref MemoryMarshal.GetArrayDataReference(_data);
        foreach ((int a, int b) in _pairs)
        {
            int length = 0;
            while (length < 264)
            {
                var va = Vector128.LoadUnsafe(ref Unsafe.Add(ref basis, a + length));
                var vb = Vector128.LoadUnsafe(ref Unsafe.Add(ref basis, b + length));
                int mask = Sse2.MoveMask(Sse2.CompareEqual(va, vb));
                if (mask != 0xFFFF)
                {
                    length += BitOperations.TrailingZeroCount(~mask & 0xFFFF);
                    break;
                }

                length += 16;
            }

            total += length;
        }

        return total;
    }

    [Benchmark]
    public int Avx2CompareMoveMask()
    {
        if (!Avx2.IsSupported)
        {
            return Scalar64BitXorTzcnt();
        }

        int total = 0;
        ref byte basis = ref MemoryMarshal.GetArrayDataReference(_data);
        foreach ((int a, int b) in _pairs)
        {
            int length = 0;
            while (length < 264)
            {
                var va = Vector256.LoadUnsafe(ref Unsafe.Add(ref basis, a + length));
                var vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref basis, b + length));
                uint mask = (uint)Avx2.MoveMask(Avx2.CompareEqual(va, vb));
                if (mask != 0xFFFFFFFF)
                {
                    length += BitOperations.TrailingZeroCount(~mask);
                    break;
                }

                length += 32;
            }

            total += length;
        }

        return total;
    }
}
