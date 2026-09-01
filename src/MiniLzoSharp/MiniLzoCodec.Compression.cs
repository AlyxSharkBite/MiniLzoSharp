// MiniLzoSharp -- a managed C# implementation of the MiniLZO (LZO1X-1) codec.
//
// Derived from the LZO real-time data compression library,
// Copyright (C) 1996-2017 Markus Franz Xaver Johannes Oberhumer.
// All Rights Reserved.
// https://www.oberhumer.com/opensource/lzo/
//
// This library is free software; you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation; either version 2 of the License, or (at your
// option) any later version.  See the file COPYING for details.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniLzoSharp;

internal sealed partial class MiniLzoCodec
{
    // LZO1X-1 compressor, a behavioral port of upstream lzo1x_1_compress. The token
    // format, match classes (M2/M3/M4), hash function and chunking are identical to the
    // C implementation, so the output is a valid LZO1X block decodable by upstream
    // lzo1x_decompress / lzo1x_decompress_safe.

    /// <summary>Hash table address bits (upstream D_BITS).</summary>
    private const int DictionaryBits = 14;

    /// <summary>Number of hash table entries; each entry is a 16-bit offset from the chunk start.</summary>
    internal const int DictionarySize = 1 << DictionaryBits;

    /// <summary>Multiplicative hash constant (upstream DINDEX for LZO1X-1).</summary>
    private const uint HashMultiplier = 0x1824429Du;

    /// <summary>
    /// Input is compressed in independent chunks of this many bytes (upstream caps at
    /// 49152 in deterministic mode). 49152 - 1 == 0xBFFF is exactly the largest offset an
    /// M4 match token can encode, so a 16-bit in-chunk offset can never produce an
    /// unencodable match distance. The hash table is cleared between chunks.
    /// </summary>
    private const int CompressChunkSize = 49152;

    /// <summary>
    /// The last 20 bytes of a chunk are never used as match candidates (upstream
    /// ip_end = in + in_len - 20). This tail guard is what makes all of the unconditional
    /// wide loads in the hot loop provably in-bounds; see the comments at each load.
    /// </summary>
    private const int ChunkTailGuard = 20;

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/> as one raw
    /// LZO1X block and returns the compressed length. The destination must be at least
    /// <see cref="GetMaxCompressedLength"/> + <see cref="CompressScratchSlack"/> bytes: the
    /// hot loop uses unconditional 4/8/16-byte stores that can overshoot the final output
    /// position by up to 15 bytes (they are overwritten or unused, exactly as upstream's
    /// UA_COPY paths do).
    /// </summary>
    internal static int CompressBlock(ReadOnlySpan<byte> source, Span<byte> destination, ushort[] dictionary)
    {
        Debug.Assert(destination.Length >= GetMaxCompressedLength(source.Length) + CompressScratchSlack);
        Debug.Assert(dictionary.Length >= DictionarySize);

        int sourceLength = source.Length;
        int remaining = sourceLength;
        int chunkStart = 0;
        int outPos = 0;
        int carry = 0; // literal bytes carried over from the previous chunk (upstream 't'/'ti')

        while (remaining > ChunkTailGuard)
        {
            int chunkLength = Math.Min(remaining, CompressChunkSize);
            // Upstream clears the whole dictionary before every chunk in deterministic
            // mode; entries are 16-bit offsets relative to the current chunk start.
            Array.Clear(dictionary, 0, DictionarySize);
            carry = CompressChunk(source, chunkStart, chunkLength, destination, ref outPos, carry, dictionary);
            chunkStart += chunkLength;
            remaining -= chunkLength;
        }

        carry += remaining;

        if (carry > 0)
        {
            // Emit the trailing bytes of the input as one final literal run.
            int literalStart = sourceLength - carry;
            if (outPos == 0 && carry <= 238)
            {
                // Nothing emitted yet: the whole input is one short literal run, encoded
                // in the special first byte (17 + count).
                destination[outPos++] = (byte)(17 + carry);
            }
            else if (carry <= 3)
            {
                // 1-3 literals ride in the low bits of the previous match token's last
                // offset byte. outPos >= 2 here: a run this short can only follow a match.
                destination[outPos - 2] |= (byte)carry;
            }
            else if (carry <= 18)
            {
                destination[outPos++] = (byte)(carry - 3);
            }
            else
            {
                int extra = carry - 18;
                destination[outPos++] = 0;
                while (extra > 255)
                {
                    extra -= 255;
                    destination[outPos++] = 0;
                }

                destination[outPos++] = (byte)extra;
            }

            source.Slice(literalStart, carry).CopyTo(destination.Slice(outPos));
            outPos += carry;
        }

        // End-of-stream marker: an M4 token with zero offset (0x11 0x00 0x00).
        destination[outPos++] = 0x11;
        destination[outPos++] = 0;
        destination[outPos++] = 0;
        return outPos;
    }

    /// <summary>
    /// Compresses one chunk (upstream do_compress). Returns the number of pending literal
    /// bytes at the chunk's end that the caller must carry into the next chunk (or flush
    /// as the final literal run). <paramref name="carry"/> is the same count from the
    /// previous chunk; those bytes live immediately before <paramref name="chunkStart"/>.
    /// </summary>
    private static int CompressChunk(
        ReadOnlySpan<byte> source,
        int chunkStart,
        int chunkLength,
        Span<byte> destination,
        ref int outPos,
        int carry,
        ushort[] dictionary)
    {
        // Bounds model for the Unsafe accesses below. Let inEnd = chunkStart + chunkLength
        // (inEnd <= source.Length) and ipEnd = inEnd - 20:
        //   * every match candidate position ip satisfies ip < ipEnd;
        //   * the 4-byte hash loads read at most ip + 3 and matchPos + 3 (< ipEnd + 3);
        //   * the 8-byte match-extension loads read at most ip + len + 7 where the loop
        //     guarantees ip + len < ipEnd + 8, i.e. at most ipEnd + 14 = inEnd - 6;
        //   * literal-run wide copies read at most 15 bytes past the run end, and the run
        //     ends at ip < ipEnd, i.e. at most ipEnd + 14.
        // All of those stay strictly below inEnd and therefore inside the source span.
        // Destination stores rely on the caller-provided worst-case + slack capacity
        // (asserted in CompressBlock); the compressed size of the data emitted so far can
        // never exceed the worst-case bound for the input consumed so far.
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        ref ushort dict = ref MemoryMarshal.GetArrayDataReference(dictionary);

        int inEnd = chunkStart + chunkLength;
        int ipEnd = inEnd - ChunkTailGuard;
        int ip = chunkStart + (carry < 4 ? 4 - carry : 0);
        int literalStart = chunkStart; // upstream 'ii': first byte of the pending literal run
        int op = outPos;

        for (; ; )
        {
            // literal: skip ahead; the step grows as the current literal run grows, which
            // is what makes the compressor cheap on incompressible data.
            ip += 1 + ((ip - literalStart) >> 5);

        nextMatch:
            if (ip >= ipEnd)
            {
                break;
            }

            // Safe: ip <= ipEnd - 1, so this 4-byte load ends at ipEnd + 2 < inEnd.
            // (A native-endian load hashes just as well; only consistency matters here.)
            uint value = ReadUInt32(ref src, ip);
            int hash = (int)((HashMultiplier * value) >> (32 - DictionaryBits));
            int matchPos = chunkStart + Unsafe.Add(ref dict, hash);
            Unsafe.Add(ref dict, hash) = (ushort)(ip - chunkStart);

            // matchPos < ip always: the table only holds offsets of earlier positions in
            // this chunk (or 0 from the pre-chunk clear, and ip > chunkStart on every
            // iteration because the literal step above runs before the first probe).
            // Safe: matchPos + 3 < ip + 3 < inEnd.
            if (value != ReadUInt32(ref src, matchPos))
            {
                continue; // no match: try the next position (upstream 'goto literal')
            }

            // A 4-byte match. First flush the pending literal run [literalStart, ip).
            literalStart -= carry;
            carry = 0;
            int literalCount = ip - literalStart;
            if (literalCount != 0)
            {
                if (literalCount <= 3)
                {
                    // Short runs ride in the low 2 bits of the previous match token's last
                    // offset byte (op >= 2 is guaranteed: the first run of a stream is
                    // always at least 5 bytes long, so a <=3 run only follows a match).
                    Unsafe.Add(ref dst, op - 2) |= (byte)literalCount;
                    // 4-byte store, advance by literalCount: overshoot of up to 3 bytes is
                    // overwritten by the next token (slack capacity covers the stream end).
                    // Load is safe: literalStart + 3 <= ip + 2 < inEnd.
                    WriteUInt32(ref dst, op, ReadUInt32(ref src, literalStart));
                    op += literalCount;
                }
                else if (literalCount <= 16)
                {
                    Unsafe.Add(ref dst, op++) = (byte)(literalCount - 3);
                    // Two 8-byte copies cover any count up to 16; loads end at
                    // literalStart + 15 <= ip + 11 < inEnd.
                    WriteUInt64(ref dst, op, ReadUInt64(ref src, literalStart));
                    WriteUInt64(ref dst, op + 8, ReadUInt64(ref src, literalStart + 8));
                    op += literalCount;
                }
                else
                {
                    if (literalCount <= 18)
                    {
                        Unsafe.Add(ref dst, op++) = (byte)(literalCount - 3);
                    }
                    else
                    {
                        int extra = literalCount - 18;
                        Unsafe.Add(ref dst, op++) = 0;
                        while (extra > 255)
                        {
                            extra -= 255;
                            Unsafe.Add(ref dst, op++) = 0;
                        }

                        Unsafe.Add(ref dst, op++) = (byte)extra;
                    }

                    // Copy in 16-byte strides while at least 16 bytes remain, then the
                    // tail byte-by-byte. Every 16-byte load stays inside the literal run
                    // (count >= 17 in this branch), so nothing reads past ip.
                    int count = literalCount;
                    int from = literalStart;
                    do
                    {
                        WriteUInt64(ref dst, op, ReadUInt64(ref src, from));
                        WriteUInt64(ref dst, op + 8, ReadUInt64(ref src, from + 8));
                        op += 16;
                        from += 16;
                        count -= 16;
                    }
                    while (count >= 16);

                    while (count > 0)
                    {
                        Unsafe.Add(ref dst, op++) = Unsafe.Add(ref src, from++);
                        count--;
                    }
                }
            }

            // Extend the match beyond the 4 hashed bytes, 8 bytes at a time. XOR of two
            // 8-byte loads + trailing-zero count finds the first differing byte in one
            // TZCNT instruction. Loads are safe: the loop stops once ip + matchLength
            // reaches ipEnd, so reads end before ipEnd + 15 < inEnd (see bounds model).
            int matchLength = 4;
            ulong diff = ReadUInt64(ref src, ip + 4) ^ ReadUInt64(ref src, matchPos + 4);
            if (diff == 0)
            {
                for (; ; )
                {
                    matchLength += 8;
                    if (ip + matchLength >= ipEnd)
                    {
                        goto matchLengthDone;
                    }

                    diff = ReadUInt64(ref src, ip + matchLength) ^ ReadUInt64(ref src, matchPos + matchLength);
                    if (diff != 0)
                    {
                        break;
                    }
                }
            }

            // First differing byte index within the word; on x86-64 this is TZCNT (BMI1)
            // with a JIT-provided software fallback, so no IsSupported guard is needed.
            matchLength += (BitConverter.IsLittleEndian
                ? BitOperations.TrailingZeroCount(diff)
                : BitOperations.LeadingZeroCount(diff)) >> 3;

        matchLengthDone:
            int matchOffset = ip - matchPos;
            ip += matchLength;
            literalStart = ip;

            if (matchLength <= 8 && matchOffset <= 0x0800)
            {
                // M2: length 3..8, offset 1..2048, two bytes.
                matchOffset--;
                Unsafe.Add(ref dst, op++) = (byte)(((matchLength - 1) << 5) | ((matchOffset & 7) << 2));
                Unsafe.Add(ref dst, op++) = (byte)(matchOffset >> 3);
            }
            else if (matchOffset <= 0x4000)
            {
                // M3: offset 1..16384, length 3..33 in the token or run-length extended.
                matchOffset--;
                if (matchLength <= 33)
                {
                    Unsafe.Add(ref dst, op++) = (byte)(0x20 | (matchLength - 2));
                }
                else
                {
                    int extra = matchLength - 33;
                    Unsafe.Add(ref dst, op++) = 0x20;
                    while (extra > 255)
                    {
                        extra -= 255;
                        Unsafe.Add(ref dst, op++) = 0;
                    }

                    Unsafe.Add(ref dst, op++) = (byte)extra;
                }

                Unsafe.Add(ref dst, op++) = (byte)(matchOffset << 2);
                Unsafe.Add(ref dst, op++) = (byte)(matchOffset >> 6);
            }
            else
            {
                // M4: offset 16385..49151, length 3..9 in the token or run-length extended.
                matchOffset -= 0x4000;
                if (matchLength <= 9)
                {
                    Unsafe.Add(ref dst, op++) = (byte)(0x10 | ((matchOffset >> 11) & 8) | (matchLength - 2));
                }
                else
                {
                    int extra = matchLength - 9;
                    Unsafe.Add(ref dst, op++) = (byte)(0x10 | ((matchOffset >> 11) & 8));
                    while (extra > 255)
                    {
                        extra -= 255;
                        Unsafe.Add(ref dst, op++) = 0;
                    }

                    Unsafe.Add(ref dst, op++) = (byte)extra;
                }

                Unsafe.Add(ref dst, op++) = (byte)(matchOffset << 2);
                Unsafe.Add(ref dst, op++) = (byte)(matchOffset >> 6);
            }

            goto nextMatch;
        }

        outPos = op;
        // Pending literals at the chunk end (including any carry not yet flushed) are the
        // caller's responsibility; they become the next chunk's carry or the final run.
        return inEnd - (literalStart - carry);
    }
}
