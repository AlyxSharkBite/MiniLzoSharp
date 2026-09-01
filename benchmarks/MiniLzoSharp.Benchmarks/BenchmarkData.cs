using System.Text;

namespace MiniLzoSharp.Benchmarks;

/// <summary>Deterministic data generators for the benchmark corpora.</summary>
internal static class BenchmarkData
{
    internal static byte[] Create(string kind, int size)
    {
        var random = new Random(0x5EED);
        byte[] data = new byte[size];
        switch (kind)
        {
            case "Zeros":
                break;

            case "Repetitive":
                for (int i = 0; i < size; i++)
                {
                    data[i] = (byte)((i % 7) + 1);
                }

                break;

            case "Text":
                string[] words =
                [
                    "compression", "the", "dictionary", "match", "literal", "offset",
                    "token", "stream", "block", "lzo", "data", "and", "with", "of",
                ];
                var builder = new StringBuilder(size + 16);
                while (builder.Length < size)
                {
                    builder.Append(words[random.Next(words.Length)]).Append(' ');
                }

                Encoding.ASCII.GetBytes(builder.ToString(0, size), data);
                break;

            case "Incompressible":
                random.NextBytes(data);
                break;

            case "Mixed":
                int position = 0;
                while (position < size)
                {
                    int run = Math.Min(random.Next(64, 4096), size - position);
                    if (random.Next(2) == 0)
                    {
                        random.NextBytes(data.AsSpan(position, run));
                    }
                    else
                    {
                        data.AsSpan(position, run).Fill((byte)random.Next(256));
                    }

                    position += run;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return data;
    }
}
