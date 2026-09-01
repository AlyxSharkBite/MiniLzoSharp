using System.Diagnostics;

namespace MiniLzoSharp.Tests;

/// <summary>
/// Cross-validation against the unmodified upstream C implementation (minilzo.c),
/// compiled into <c>tools/minilzo-ref/bin/minilzo_ref.exe</c> by
/// <c>tools/minilzo-ref/build.ps1</c>. Both directions are tested so a bug shared by
/// this compressor and this decompressor cannot hide behind self-round-trips.
/// </summary>
public sealed class InteropTests
{
    private static readonly IMiniLzoCodec Codec = MiniLzoFactory.Create();

    private static string? FindReferenceExecutable()
    {
        // Walk up from the test binary to the solution root, then into tools/.
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "MiniLzoSharp.slnx")))
            {
                continue;
            }

            string binDir = Path.Combine(dir.FullName, "tools", "minilzo-ref", "bin");
            foreach (string name in new[] { "minilzo_ref.exe", "minilzo_ref" })
            {
                string candidate = Path.Combine(binDir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        return null;
    }

    private static string RequireReferenceExecutable()
    {
        string? exe = FindReferenceExecutable();
        if (exe is null)
        {
            Assert.Skip(
                "The upstream reference harness was not found; build it with " +
                "tools/minilzo-ref/build.ps1 (Windows) or tools/minilzo-ref/build.sh (Linux, macOS) " +
                "to enable the interop tests.");
        }

        return exe;
    }

    private static void RunReference(string exe, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"minilzo_ref {arguments[0]} failed ({process.ExitCode}): {stderr}");
    }

    public static TheoryData<string, int> Corpus => new()
    {
        { "zeros", 131072 },
        { "ones", 131072 },
        { "random", 65536 },
        { "text", 150000 },
        { "sequential", 98404 },   // spans three compressor chunks
        { "repetitive3", 100000 },
        { "repetitive40", 100000 },
        { "executable", 120000 },
        { "mixed", 300000 },
        { "tiny", 1 },
        { "boundary20", 20 },
        { "boundary21", 21 },
        { "small", 500 },
    };

    private static byte[] Materialize(string kind, int size) => kind switch
    {
        "zeros" => TestData.Zeros(size),
        "ones" => TestData.Filled(size, 0xFF),
        "random" => TestData.Random(size, seed: 77),
        "text" => TestData.Text(size, seed: 78),
        "sequential" => TestData.Sequential(size),
        "repetitive3" => TestData.Repetitive(size, period: 3),
        "repetitive40" => TestData.Repetitive(size, period: 40),
        "executable" => TestData.ExecutableLike(size, seed: 79),
        "mixed" => TestData.Mixed(size, seed: 80),
        _ => TestData.Random(size, seed: 81),
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void UpstreamCompress_MiniLzoSharpDecompress(string kind, int size)
    {
        string exe = RequireReferenceExecutable();
        byte[] original = Materialize(kind, size);

        string originalPath = Path.Combine(Path.GetTempPath(), $"mlzs_{Guid.NewGuid():N}.orig");
        string compressedPath = originalPath + ".lzo";
        try
        {
            File.WriteAllBytes(originalPath, original);
            RunReference(exe, "c", originalPath, compressedPath);
            byte[] upstreamCompressed = File.ReadAllBytes(compressedPath);

            byte[] decompressed = Codec.Decompress(upstreamCompressed);
            Assert.Equal(original, decompressed);
        }
        finally
        {
            File.Delete(originalPath);
            File.Delete(compressedPath);
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CompressedOutput_IsByteIdenticalToUpstream(string kind, int size)
    {
        // Not a requirement of the LZO1X format (any valid block would do), but the port
        // mirrors the upstream match finder exactly, so on x64 the output should be
        // byte-for-byte identical. This pins that property; if it ever diverges the
        // decode-interop tests decide correctness.
        string exe = RequireReferenceExecutable();
        byte[] original = Materialize(kind, size);

        string originalPath = Path.Combine(Path.GetTempPath(), $"mlzs_{Guid.NewGuid():N}.orig");
        string compressedPath = originalPath + ".lzo";
        try
        {
            File.WriteAllBytes(originalPath, original);
            RunReference(exe, "c", originalPath, compressedPath);
            byte[] upstreamCompressed = File.ReadAllBytes(compressedPath);
            byte[] sharpCompressed = Codec.Compress(original);
            Assert.Equal(upstreamCompressed, sharpCompressed);
        }
        finally
        {
            File.Delete(originalPath);
            File.Delete(compressedPath);
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void MiniLzoSharpCompress_UpstreamDecompress(string kind, int size)
    {
        string exe = RequireReferenceExecutable();
        byte[] original = Materialize(kind, size);
        byte[] compressed = Codec.Compress(original);

        string compressedPath = Path.Combine(Path.GetTempPath(), $"mlzs_{Guid.NewGuid():N}.lzo");
        string decompressedPath = compressedPath + ".out";
        try
        {
            File.WriteAllBytes(compressedPath, compressed);
            RunReference(exe, "d", compressedPath, decompressedPath, original.Length.ToString());
            byte[] upstreamDecompressed = File.ReadAllBytes(decompressedPath);
            Assert.Equal(original, upstreamDecompressed);
        }
        finally
        {
            File.Delete(compressedPath);
            File.Delete(decompressedPath);
        }
    }

    [Fact]
    public void RandomizedCorpus_BothDirections()
    {
        string exe = RequireReferenceExecutable();
        var random = new Random(987654321);

        for (int i = 0; i < 40; i++)
        {
            int size = random.Next(0, 250000);
            byte[] original = (i % 4) switch
            {
                0 => TestData.Random(size, seed: i),
                1 => TestData.Repetitive(Math.Max(size, 1), period: random.Next(1, 50)),
                2 => TestData.Text(size, seed: i),
                _ => TestData.Mixed(size, seed: i),
            };

            string basePath = Path.Combine(Path.GetTempPath(), $"mlzs_{Guid.NewGuid():N}");
            try
            {
                // Upstream -> MiniLzoSharp.
                File.WriteAllBytes(basePath + ".orig", original);
                RunReference(exe, "c", basePath + ".orig", basePath + ".uplzo");
                Assert.Equal(original, Codec.Decompress(File.ReadAllBytes(basePath + ".uplzo")));

                // MiniLzoSharp -> upstream.
                File.WriteAllBytes(basePath + ".shlzo", Codec.Compress(original));
                RunReference(exe, "d", basePath + ".shlzo", basePath + ".upout", original.Length.ToString());
                Assert.Equal(original, File.ReadAllBytes(basePath + ".upout"));
            }
            finally
            {
                foreach (string suffix in new[] { ".orig", ".uplzo", ".shlzo", ".upout" })
                {
                    File.Delete(basePath + suffix);
                }
            }
        }
    }
}
