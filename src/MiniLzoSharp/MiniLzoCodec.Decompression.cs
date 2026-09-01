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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniLzoSharp;

internal sealed partial class MiniLzoCodec
{
    // Safe LZO1X decompressor, a behavioral port of upstream lzo1x_decompress_safe with
    // every overrun check enabled (LZO_TEST_OVERRUN_INPUT/OUTPUT/LOOKBEHIND == 2/2/1).
    // It accepts the full LZO1X token set — including M1 matches that lzo1x_1 itself
    // never emits but other LZO1X compressors (e.g. lzo1x_999) do.
    //
    // Control flow intentionally mirrors the C original's goto structure: that structure
    // *is* the LZO1X grammar, and restructuring it is how ports introduce corner-case
    // bugs. Every wide (4/8-byte) access is preceded by an explicit input/output
    // availability check; the checks are the same NEED_IP/NEED_OP guards as upstream, so
    // this decoder accepts exactly the streams the upstream safe decoder accepts.

    /// <summary>
    /// Decompresses one raw LZO1X block from <paramref name="source"/> into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written, or -1 if <paramref name="destination"/> is
    /// too small (the caller may retry with a larger buffer; corrupt input that claims an
    /// impossible size is diagnosed by the caller's growth cap).</returns>
    /// <exception cref="InvalidDataException">The input is truncated, contains an invalid
    /// match offset, or is otherwise malformed.</exception>
    internal static int DecompressBlock(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed)
    {
        int ipEnd = source.Length;
        int opEnd = destination.Length;
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);

        int ip = 0;
        int op = 0;
        long t; // token / length accumulator; long so run-length extension cannot overflow
        int matchPos;
        bytesConsumed = 0;

        if (ipEnd < 1)
        {
            throw Truncated();
        }

        if (Unsafe.Add(ref src, ip) > 17)
        {
            // Special first byte: 18..21 encode 1..4 literals before the first token;
            // 22..255 encode a whole short stream that is one literal run of (byte - 17).
            t = Unsafe.Add(ref src, ip++) - 17;
            if (t < 4)
            {
                goto matchNext;
            }

            if (opEnd - op < t)
            {
                return -1; // NEED_OP(t)
            }

            if (ipEnd - ip < t + 3)
            {
                throw Truncated(); // NEED_IP(t + 3)
            }

            int count = (int)t;
            CopyLiterals(ref dst, ref op, ref src, ref ip, count);
            goto firstLiteralRun;
        }

    nextToken:
        if (ipEnd - ip < 3)
        {
            throw Truncated(); // NEED_IP(3): every token needs at least itself + the EOF tail
        }

        t = Unsafe.Add(ref src, ip++);
        if (t >= 16)
        {
            goto match;
        }

        // Literal run of t + 3 bytes (t == 0 means run-length extended).
        if (t == 0)
        {
            // Safe: the NEED_IP(3) above guarantees the first read; NEED_IP(1) inside the
            // loop guarantees each subsequent one.
            while (Unsafe.Add(ref src, ip) == 0)
            {
                t += 255;
                ip++;
                if (ipEnd - ip < 1)
                {
                    throw Truncated();
                }
            }

            t += 15 + Unsafe.Add(ref src, ip++);
        }

        if (opEnd - op < t + 3)
        {
            return -1; // NEED_OP(t + 3)
        }

        if (ipEnd - ip < t + 6)
        {
            throw Truncated(); // NEED_IP(t + 6): run + the 3-byte minimum that must follow
        }

        CopyLiterals(ref dst, ref op, ref src, ref ip, (int)(t + 3));

    firstLiteralRun:
        // Safe: NEED_IP(t + 6) above (or NEED_IP(t + 3) on the special-first-byte path)
        // left at least 3 unread bytes.
        t = Unsafe.Add(ref src, ip++);
        if (t >= 16)
        {
            goto match;
        }

        // M1 directly after a literal run: 3-byte match, offset 2049..3072.
        matchPos = op - (1 + 0x0800);
        matchPos -= (int)(t >> 2);
        matchPos -= Unsafe.Add(ref src, ip++) << 2;
        if (matchPos < 0 || matchPos >= op)
        {
            throw LookbehindOverrun();
        }

        if (opEnd - op < 3)
        {
            return -1; // NEED_OP(3)
        }

        Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos++);
        Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos++);
        Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos);
        goto matchDone;

    match:
        // On every path into this label at least 2 input bytes are unread: coming from
        // nextToken/firstLiteralRun the NEED_IP(3)/NEED_IP(t + 6) guards ensured 3 before
        // the token byte was consumed; coming from matchNext, NEED_IP(t + 3) did.
        if (t >= 64)
        {
            // M2: length 3..8, offset 1..2048.
            matchPos = op - 1;
            matchPos -= (int)((t >> 2) & 7);
            matchPos -= Unsafe.Add(ref src, ip++) << 3;
            t = (t >> 5) - 1;
            if (matchPos < 0 || matchPos >= op)
            {
                throw LookbehindOverrun();
            }

            if (opEnd - op < t + 2)
            {
                return -1; // NEED_OP(t + 2)
            }

            goto copyMatchByteWise;
        }

        if (t >= 32)
        {
            // M3: offset 1..16384, length 2 + (t & 31), run-length extended when the
            // token's length bits are zero.
            t &= 31;
            if (t == 0)
            {
                while (Unsafe.Add(ref src, ip) == 0)
                {
                    t += 255;
                    ip++;
                    if (ipEnd - ip < 1)
                    {
                        throw Truncated();
                    }
                }

                t += 31 + Unsafe.Add(ref src, ip++);
                if (ipEnd - ip < 2)
                {
                    throw Truncated(); // NEED_IP(2) for the offset that follows
                }
            }

            // Safe: 2 bytes available (entry guarantee, or the explicit check above).
            matchPos = op - 1;
            matchPos -= (int)(ReadUInt16LittleEndian(ref src, ip) >> 2);
            ip += 2;
        }
        else if (t >= 16)
        {
            // M4: offset 16384..49151, length 2 + (t & 7), run-length extended when the
            // token's length bits are zero. Offset bits of zero mark end of stream.
            matchPos = op - (int)((t & 8) << 11);
            t &= 7;
            if (t == 0)
            {
                while (Unsafe.Add(ref src, ip) == 0)
                {
                    t += 255;
                    ip++;
                    if (ipEnd - ip < 1)
                    {
                        throw Truncated();
                    }
                }

                t += 7 + Unsafe.Add(ref src, ip++);
                if (ipEnd - ip < 2)
                {
                    throw Truncated(); // NEED_IP(2)
                }
            }

            // Safe: 2 bytes available (entry guarantee, or the explicit check above).
            matchPos -= (int)(ReadUInt16LittleEndian(ref src, ip) >> 2);
            ip += 2;
            if (matchPos == op)
            {
                goto endOfStream;
            }

            matchPos -= 0x4000;
        }
        else
        {
            // M1 between matches: 2-byte match, offset 1..1024.
            matchPos = op - 1;
            matchPos -= (int)(t >> 2);
            matchPos -= Unsafe.Add(ref src, ip++) << 2;
            if (matchPos < 0 || matchPos >= op)
            {
                throw LookbehindOverrun();
            }

            if (opEnd - op < 2)
            {
                return -1; // NEED_OP(2)
            }

            Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos++);
            Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos);
            goto matchDone;
        }

        // Common tail for M3/M4: copy t + 2 bytes from matchPos.
        if (matchPos < 0 || matchPos >= op)
        {
            throw LookbehindOverrun();
        }

        if (opEnd - op < t + 2)
        {
            return -1; // NEED_OP(t + 2)
        }

        if (op - matchPos >= 8)
        {
            // Non-overlapping (at word granularity): copy with 8-byte strides. All loads
            // read already-written destination bytes (matchPos + 8 <= op) and all stores
            // were bounds-checked by NEED_OP above; the copy writes exactly count bytes.
            int count = (int)(t + 2);
            while (count >= 8)
            {
                WriteUInt64(ref dst, op, ReadUInt64(ref dst, matchPos));
                op += 8;
                matchPos += 8;
                count -= 8;
            }

            if (count >= 4)
            {
                WriteUInt32(ref dst, op, ReadUInt32(ref dst, matchPos));
                op += 4;
                matchPos += 4;
                count -= 4;
            }

            while (count > 0)
            {
                Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos++);
                count--;
            }

            goto matchDone;
        }

    copyMatchByteWise:
        {
            // Overlapping or short match: byte-by-byte preserves LZO's overlapping-copy
            // semantics (an offset smaller than the length replicates the last bytes).
            int count = (int)(t + 2);
            do
            {
                Unsafe.Add(ref dst, op++) = Unsafe.Add(ref dst, matchPos++);
                count--;
            }
            while (count > 0);
        }

    matchDone:
        // The low 2 bits of the previous token's last offset byte (or of an M1/M2 token
        // byte) give the number of literals to copy before the next token.
        t = Unsafe.Add(ref src, ip - 2) & 3;
        if (t == 0)
        {
            goto nextToken;
        }

    matchNext:
        // 1..3 literals, then the next token byte.
        if (opEnd - op < t)
        {
            return -1; // NEED_OP(t)
        }

        if (ipEnd - ip < t + 3)
        {
            throw Truncated(); // NEED_IP(t + 3)
        }

        Unsafe.Add(ref dst, op++) = Unsafe.Add(ref src, ip++);
        if (t > 1)
        {
            Unsafe.Add(ref dst, op++) = Unsafe.Add(ref src, ip++);
            if (t > 2)
            {
                Unsafe.Add(ref dst, op++) = Unsafe.Add(ref src, ip++);
            }
        }

        t = Unsafe.Add(ref src, ip++);
        goto match;

    endOfStream:
        bytesConsumed = ip;
        return op;
    }

    /// <summary>
    /// Copies <paramref name="count"/> literal bytes from the input to the output in
    /// 8-byte strides. The caller has already checked that <paramref name="count"/> bytes
    /// may be read and written (NEED_IP/NEED_OP), and the strides never touch more than
    /// <paramref name="count"/> bytes on either side.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyLiterals(ref byte dst, ref int op, ref byte src, ref int ip, int count)
    {
        while (count >= 8)
        {
            WriteUInt64(ref dst, op, ReadUInt64(ref src, ip));
            op += 8;
            ip += 8;
            count -= 8;
        }

        if (count >= 4)
        {
            WriteUInt32(ref dst, op, ReadUInt32(ref src, ip));
            op += 4;
            ip += 4;
            count -= 4;
        }

        while (count > 0)
        {
            Unsafe.Add(ref dst, op++) = Unsafe.Add(ref src, ip++);
            count--;
        }
    }

    private static InvalidDataException Truncated()
        => new("The compressed data is truncated or malformed: input ended inside a token.");

    private static InvalidDataException LookbehindOverrun()
        => new("The compressed data is corrupt: a match offset points outside the data decompressed so far.");
}
