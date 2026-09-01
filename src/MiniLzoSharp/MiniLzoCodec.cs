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

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MiniLzoSharp;

/// <summary>
/// The LZO1X-1 codec. Stateless: every call rents its own scratch buffers, so a single
/// instance is safe for concurrent use from any number of threads.
/// </summary>
internal sealed partial class MiniLzoCodec : IMiniLzoCodec
{
    /// <summary>
    /// A valid LZO1X stream cannot expand by more than this factor: the cheapest way to
    /// produce output is a run-length extension byte, and each such input byte adds at
    /// most 255 bytes of output. Used to cap decompression buffer growth so corrupt data
    /// can never trigger uncontrolled allocation.
    /// </summary>
    private const long MaxExpansionFactor = 255;

    /// <summary>The largest allocatable byte array (<see cref="Array.MaxLength"/> is the same value).</summary>
    private const int MaxByteArrayLength = 0x7FFFFFC7;

    /// <summary>
    /// Extra bytes of scratch capacity beyond the worst-case compressed size. The
    /// compressor, like upstream MiniLZO, emits short literal runs with unconditional
    /// 4/8/16-byte wide stores that can transiently overshoot the final output position by
    /// up to 15 bytes; the slack keeps those stores inside the rented buffer.
    /// </summary>
    private const int CompressScratchSlack = 16;

    /// <inheritdoc />
    public byte[] Compress(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);

        long worstCase = GetMaxCompressedLength(source.Length);
        if (worstCase + CompressScratchSlack > MaxByteArrayLength)
        {
            throw new ArgumentException(
                "The input is so large that the worst-case compressed size could exceed the " +
                "2 GiB array limit. Use the stream overload, which processes data in blocks.",
                nameof(source));
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent((int)(worstCase + CompressScratchSlack));
        ushort[] dictionary = ArrayPool<ushort>.Shared.Rent(DictionarySize);
        try
        {
            int compressedLength = CompressBlock(source, scratch, dictionary);
            return scratch.AsSpan(0, compressedLength).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            ArrayPool<ushort>.Shared.Return(dictionary);
        }
    }

    /// <inheritdoc />
    public byte[] Decompress(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Cap the output at what a valid stream of this length could possibly describe
        // (see MaxExpansionFactor). Anything needing more is corrupt by construction.
        long maxOutput = Math.Max(Math.Min(MaxExpansionFactor * source.Length, MaxByteArrayLength), 1L);
        long size = Math.Min(Math.Max(4L * source.Length, 256L), maxOutput);

        for (; ; )
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                // Use the full rented capacity (Rent may round up), but never beyond the cap.
                int capacity = (int)Math.Min(buffer.Length, maxOutput);
                int written = DecompressBlock(source, buffer.AsSpan(0, capacity), out int consumed);
                if (written >= 0)
                {
                    if (consumed != source.Length)
                    {
                        throw new InvalidDataException(
                            "The compressed data contains trailing bytes after the end-of-stream marker.");
                    }

                    return buffer.AsSpan(0, written).ToArray();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // The destination was too small: grow geometrically and retry, up to the cap.
            if (size >= maxOutput)
            {
                throw new InvalidDataException(
                    "The compressed data would expand beyond the maximum size a valid LZO1X " +
                    "block of this length can describe; the data is corrupt.");
            }

            size = Math.Min(size * 2, maxOutput);
        }
    }

    /// <summary>
    /// Worst-case compressed size for <paramref name="uncompressedLength"/> input bytes.
    /// This is the bound published by upstream LZO for LZO1X
    /// (<c>len + len / 16 + 64 + 3</c>); computed in 64-bit so it cannot overflow.
    /// </summary>
    internal static long GetMaxCompressedLength(int uncompressedLength)
        => uncompressedLength + (long)(uncompressedLength / 16) + 64 + 3;

    // ----- Unaligned word access -----------------------------------------------------
    //
    // These helpers perform unaligned word-sized loads/stores relative to a span base
    // reference. They compile to single MOV instructions on x86-64. They carry no bounds
    // checks of their own: every call site is annotated with the reason the access is in
    // range (typically the 20-byte compressor tail guard or an explicit decompressor
    // NEED_IP/NEED_OP check performed beforehand).

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32(ref byte basis, int offset)
        => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref basis, offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(ref byte basis, int offset)
        => Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref basis, offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt32(ref byte basis, int offset, uint value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref basis, offset), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64(ref byte basis, int offset, ulong value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref basis, offset), value);

    /// <summary>Reads a 16-bit little-endian value regardless of host endianness.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt16LittleEndian(ref byte basis, int offset)
    {
        ushort value = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref basis, offset));
        // The branch is folded away by the JIT; on x86-64 this is a single MOVZX load.
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }
}
