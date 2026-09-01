# MiniLzoSharp

A modern, fully managed C# implementation of the **MiniLZO** subset of the LZO real-time
data compression library: the **LZO1X-1** compressor and the **safe** LZO1X decompressor.

MiniLzoSharp is a behavioral re-implementation, not a line-by-line translation. It keeps
the exact LZO1X block format — the hash-based match finder, the M1–M4 token classes,
overlapping match-copy semantics, and the end-of-stream marker — while expressing the
algorithm with modern .NET techniques (`Span<T>`, `ArrayPool<T>`, unaligned word access,
`BitOperations`).

- **Upstream project:** LZO by Markus F.X.J. Oberhumer —
  <https://www.oberhumer.com/opensource/lzo/>
- **Derived from:** `minilzo.c` / `minilzo.h`, vendored unmodified under
  [`third_party/minilzo`](third_party/minilzo).

## Interoperability

Raw blocks are wire-compatible with upstream MiniLZO in both directions:

| Direction | Compatible with |
|---|---|
| `Compress(byte[])` output | upstream `lzo1x_decompress` / `lzo1x_decompress_safe` |
| `Decompress(byte[])` input | upstream `lzo1x_1_compress` output (and any valid LZO1X stream, including M1 tokens that `lzo1x_999` emits) |

Byte-for-byte identity with the C compressor's output is not a contractual guarantee of
this library (any valid LZO1X block would satisfy interoperability), but because the
match finder mirrors upstream exactly, the output **is byte-identical to
`lzo1x_1_compress`** on every corpus in the test suite — zeros, repetitive, sequential,
text, executable-like, mixed, and random data — and a test pins that property. The test
suite additionally cross-validates decompression in both directions against the real
upstream C code compiled from this repository (see *Interop tests* below).

The **stream** methods use a MiniLzoSharp-specific framed container (documented below),
because raw LZO1X is block-oriented and has no length or end-of-stream framing of its
own. Framed streams are readable only by MiniLzoSharp.

## Requirements

- .NET 10 (`net10.0`)
- Primary target platform: **x64**. The library is portable managed IL (no P/Invoke, no
  native DLLs, no `unsafe` blocks) and also runs on AnyCPU/arm64, but it is written and
  benchmarked for modern x86-64.

## Build

```bash
dotnet build MiniLzoSharp.slnx -c Release
```

Run the tests (the interop tests skip themselves unless the reference harness has been
built — see below):

```bash
dotnet test tests/MiniLzoSharp.Tests/MiniLzoSharp.Tests.csproj -c Release
```

The interop tests shell out to a small CLI built from the unmodified upstream
`minilzo.c`. They skip themselves if it is not present, so build it first to run the full
suite. On Windows (requires Visual Studio with the C++ workload):

```bash
powershell -ExecutionPolicy Bypass -File tools/minilzo-ref/build.ps1
```

On Linux or macOS (requires any C compiler):

```bash
sh tools/minilzo-ref/build.sh
```

CI builds it on every push, so the cross-validation against the original C implementation
runs there too rather than silently skipping.

## Usage

### Byte arrays (raw LZO1X blocks)

```csharp
using MiniLzoSharp;

IMiniLzoCodec codec = MiniLzoFactory.Create();

byte[] compressed = codec.Compress(originalBytes);
byte[] restored   = codec.Decompress(compressed);
```

No lengths need to be passed anywhere: `Compress` uses the array's own `Length`, and
`Decompress` does **not** need the original uncompressed length — it grows its output
buffer safely as the data demands (bounded by the maximum expansion a valid LZO1X stream
of that input length can describe).

### Streams (MiniLzoSharp framed format)

```csharp
IMiniLzoCodec codec = MiniLzoFactory.Create();

using (FileStream input = File.OpenRead("data.bin"))
using (FileStream output = File.Create("data.bin.mlzs"))
{
    codec.Compress(input, output);
}

using (FileStream input = File.OpenRead("data.bin.mlzs"))
using (FileStream output = File.Create("data.restored.bin"))
{
    codec.Decompress(input, output);
}
```

The stream methods process data incrementally in independent 256 KiB blocks: they never
load the whole stream into memory, they support non-seekable streams and streams that
return partial reads, and they never close or dispose the caller's streams. Blocks that
compression would not shrink are stored verbatim. `Decompress` stops reading directly
after the end-of-stream marker, so framed data can be followed by other content.

## Framed stream format

All integers are **little-endian**. The format is specific to MiniLzoSharp.

```text
Stream    := Header Block* EndMarker
Header    := Magic Version BlockSize
Magic     := 89 4D 4C 5A 53 0D 0A 1A            ; "\x89MLZS\r\n\x1A", 8 bytes
Version   := 01                                 ; 1 byte, format version
BlockSize := uint32                             ; declared maximum uncompressed block
                                                ; length; writer uses 262144 (256 KiB);
                                                ; readers accept 1 .. 67108864 (64 MiB)
Block     := BlockType UncompressedLength CompressedLength Payload
BlockType := 01 (compressed) | 02 (stored)      ; 1 byte
UncompressedLength := uint32                    ; 1 .. BlockSize
CompressedLength   := uint32                    ; stored:     == UncompressedLength
                                                ; compressed: >= 1 and < UncompressedLength
Payload   := CompressedLength bytes             ; compressed: one complete raw LZO1X block
                                                ; stored:     the original bytes verbatim
EndMarker := 00                                 ; 1 byte, unambiguous end of stream
```

Every field is strictly validated on read: the magic, the version, the block-size range,
both lengths per block, the block type, and — for compressed blocks — that the payload
decompresses to *exactly* the declared uncompressed length while consuming *exactly* the
declared compressed length. An empty input produces `Header EndMarker` (14 bytes).
Blocks are fully independent (no shared dictionary), so each block can be validated and
decompressed on its own.

## Error behavior

| Condition | Exception |
|---|---|
| `null` array or stream argument | `ArgumentNullException` |
| unreadable source / unwritable destination / same stream instance for both | `ArgumentException` |
| input too large for the 2 GiB array worst case (byte-array compress only) | `ArgumentException` |
| corrupt, truncated, or malformed compressed data; invalid match offsets; trailing bytes after the end-of-stream marker; framing violations; data whose decompressed size would exceed what a valid stream of that length can describe | `InvalidDataException` |

Malformed data always produces a controlled exception — never an access violation, an
infinite loop, silent truncation, or unbounded allocation. The decompressor is a port of
upstream's `lzo1x_decompress_safe` with **all** overrun checks enabled (input, output,
and lookbehind), and decompression buffer growth is capped by the provable maximum
expansion factor of the format (each input byte can contribute at most 255 output bytes),
so a hostile input cannot request more memory than a valid input of the same size could.

## Thread safety

The codec is stateless. Every call rents its own scratch buffers from `ArrayPool<T>` and
returns them before completing; nothing mutable is shared between calls. A single
instance from `MiniLzoFactory.Create()` can be used concurrently from any number of
threads (covered by a dedicated stress test).

## Performance notes

Representative numbers from `CodecBenchmarks` (BenchmarkDotNet, Release x64, .NET 10,
1 MiB buffers, internal core APIs which are allocation-free; the public array APIs add
exactly one result-array allocation):

| Data | Compress | Decompress |
|---|---|---|
| Incompressible (random) | ~43 GB/s | ~24 GB/s |
| All zeros | ~16.6 GB/s | ~1.0 GB/s (offset-1 overlap copies) |
| Repetitive (period 7) | ~16.2 GB/s | ~3.8 GB/s |
| Mixed binary | ~13.8 GB/s | ~3.1 GB/s |
| Text | ~1.0 GB/s | ~0.9 GB/s |

Small-buffer latency (4 KiB, incompressible): ~0.3 µs compress core, ~0.17 µs
decompress core.


- **Unaligned word access, no `unsafe` blocks.** The hot loops use
  `Unsafe.ReadUnaligned`/`WriteUnaligned` over span references (single `mov` instructions
  on x86-64) instead of per-byte span indexing. Every such access sits behind either the
  compressor's structural 20-byte tail guard or an explicit decompressor bounds check,
  and each site carries a comment stating why it is in range.
- **Match-length scanning** XORs 8-byte words and finds the first differing byte with
  `BitOperations.TrailingZeroCount`, which the JIT compiles to `TZCNT` (BMI1) with an
  automatic software fallback — the same technique upstream uses on 64-bit targets.
- **Wide copies.** Literal runs and non-overlapping match copies move 8 bytes per
  iteration; overlapping matches use the byte-by-byte copy that LZO's replication
  semantics require.
- **Pooled buffers everywhere.** The array APIs allocate exactly one result array per
  call; all scratch memory (compressor dictionary, worst-case output, stream block
  buffers) comes from `ArrayPool<T>` and is reused. The internal core methods are
  allocation-free.
- **Worst-case output bound** is precomputed (`len + len/16 + 64 + 3`, the bound
  published by upstream), so the compressor never reallocates or bounds-checks the
  destination in its hot loop.
- **No LINQ, no iterators, minimal branching** in the codec paths; control flow mirrors
  the upstream C, which is the shape 25 years of LZO tuning converged on.

### Hardware intrinsics: evaluated, and where they landed

Explicit SIMD paths (`Sse2`/`Avx2` compare + `MoveMask` for match scanning) were
implemented and benchmarked against the 8-byte scalar-wide path
(`benchmarks/MiniLzoSharp.Benchmarks/MatchScanBenchmarks.cs` keeps the candidates, each
guarded by `IsSupported` with a scalar fallback). Measured on this project's benchmark
machine (Release, x64):

| Typical match length | SSE2 vs scalar | AVX2 vs scalar |
|---|---|---|
| ~6 bytes (the LZO1X-1 common case) | 1.06× (slower) | 1.33× (slower) |
| ~32 bytes | 0.79× | 0.70× |
| ~250 bytes | 0.61× | 0.47× |

For short matches — which dominate text and mixed binary, the workloads where match
scanning is actually the bottleneck — the wider compares lose: the match usually ends
inside the first 8-byte word, so the vector compare pays its fixed cost with nothing to
amortize it over. SIMD only wins on long matches, and long matches occur precisely on
highly repetitive data where the compressor is already an order of magnitude faster than
on text (few hash probes, huge literal skips), so the win does not move end-to-end
throughput meaningfully. Per the measure-before-keeping rule, the codec ships the
simpler 64-bit path, which already uses hardware acceleration through `BitOperations`
(TZCNT/BMI1). A wide load is also never issued past the end of a validated input range
merely because the page would usually be mapped: every wide access is proven in-bounds
first.

Benchmark it yourself (Release, x64):

```bash
dotnet run -c Release --project benchmarks/MiniLzoSharp.Benchmarks -- --filter "*" --job short
```

## Tests

`tests/MiniLzoSharp.Tests` covers: empty input; every size 1–80; literal-run and
match-length boundary sizes; compressor chunk boundaries (49152); repetitive, all-zero,
all-`0xFF`, sequential, text, executable-like, mixed, and random data; multi-block and
non-seekable/partial-read streams; concurrent use of one codec instance; corrupt,
truncated (every strict prefix of a valid block), and bit-flipped input; invalid match
offsets; integer-overflow attempts; and a seeded randomized corpus.

**Interop tests** compile the *unmodified* upstream `minilzo.c` into a small CLI harness
(`tools/minilzo-ref`) and verify byte-exact round trips in both directions:
upstream-compress → MiniLzoSharp-decompress, and MiniLzoSharp-compress →
upstream-decompress. This guards against a compressor and decompressor sharing the same
bug. All random data is generated from fixed seeds so failures reproduce exactly.

## Interoperability limitations

- The framed stream format is MiniLzoSharp-specific by design; only the byte-array APIs
  speak raw LZO1X.
- The byte-array `Compress` rejects inputs whose worst-case compressed size exceeds the
  2 GiB .NET array limit (use the stream API instead); `Decompress` rejects blocks whose
  output would exceed that limit.
- Only LZO1X-1 compression is implemented (as in MiniLZO itself); the decompressor
  accepts any valid LZO1X stream regardless of which LZO1X compressor produced it.

## License and attribution

MiniLzoSharp is derived from the LZO real-time data compression library:

> Copyright (C) 1996-2017 Markus Franz Xaver Johannes Oberhumer. All Rights Reserved.
> <https://www.oberhumer.com/opensource/lzo/>

LZO and MiniLZO are licensed under the **GNU General Public License, version 2 or later**
(GPL-2.0-or-later). As a derived work, MiniLzoSharp is distributed under the same
license; see [LICENSE](LICENSE) for the full text. The upstream C sources are vendored
**unmodified** under [`third_party/minilzo/`](third_party/minilzo), together with their
own `COPYING` and `README.LZO`, so the interoperability tests can be reproduced from a
clone.

C# implementation Copyright (c) 2026 Alyx Dallagiacomo.
