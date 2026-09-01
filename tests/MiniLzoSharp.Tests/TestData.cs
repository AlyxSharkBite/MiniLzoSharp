namespace MiniLzoSharp.Tests;

/// <summary>Deterministic test-data generators. Every generator is seeded so any failure
/// reproduces exactly.</summary>
internal static class TestData
{
    internal static byte[] Zeros(int length) => new byte[length];

    internal static byte[] Filled(int length, byte value)
    {
        byte[] data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    internal static byte[] Sequential(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)i;
        }

        return data;
    }

    internal static byte[] Repetitive(int length, int period)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i % period) + 1);
        }

        return data;
    }

    internal static byte[] Random(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>English-like text: repeated words with seeded variation, similar match
    /// structure to real prose.</summary>
    internal static byte[] Text(int length, int seed)
    {
        string[] words =
        [
            "compression", "the", "dictionary", "match", "literal", "offset", "token",
            "stream", "block", "lzo", "data", "and", "with", "of", "quick", "brown",
        ];
        var random = new Random(seed);
        var builder = new System.Text.StringBuilder(length + 16);
        while (builder.Length < length)
        {
            builder.Append(words[random.Next(words.Length)]);
            builder.Append(random.Next(12) == 0 ? ".\n" : " ");
        }

        return System.Text.Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    /// <summary>Executable-like data: short repeated opcode motifs separated by
    /// low-entropy filler and occasional random immediates.</summary>
    internal static byte[] ExecutableLike(int length, int seed)
    {
        var random = new Random(seed);
        byte[] motif = [0x48, 0x8B, 0x45, 0x08, 0x48, 0x89, 0xC7, 0xE8];
        byte[] data = new byte[length];
        int i = 0;
        while (i < length)
        {
            int choice = random.Next(4);
            if (choice == 0)
            {
                for (int j = 0; j < motif.Length && i < length; j++)
                {
                    data[i++] = motif[j];
                }
            }
            else if (choice == 1)
            {
                int run = random.Next(1, 24);
                for (int j = 0; j < run && i < length; j++)
                {
                    data[i++] = 0x90;
                }
            }
            else
            {
                int run = random.Next(1, 8);
                for (int j = 0; j < run && i < length; j++)
                {
                    data[i++] = (byte)random.Next(256);
                }
            }
        }

        return data;
    }

    /// <summary>Mixed binary: alternating compressible and incompressible sections.</summary>
    internal static byte[] Mixed(int length, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[length];
        int i = 0;
        while (i < length)
        {
            int sectionLength = Math.Min(random.Next(64, 4096), length - i);
            switch (random.Next(3))
            {
                case 0:
                    random.NextBytes(data.AsSpan(i, sectionLength));
                    break;
                case 1:
                    data.AsSpan(i, sectionLength).Fill((byte)random.Next(256));
                    break;
                default:
                    byte[] text = Text(sectionLength, seed + i);
                    text.CopyTo(data.AsSpan(i));
                    break;
            }

            i += sectionLength;
        }

        return data;
    }
}
