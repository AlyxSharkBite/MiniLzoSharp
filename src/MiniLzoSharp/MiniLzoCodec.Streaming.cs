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
using System.Diagnostics.CodeAnalysis;
using MiniLzoSharp.Enums;

namespace MiniLzoSharp;

internal sealed partial class MiniLzoCodec
{
    // The MiniLzoSharp framed stream format. Raw LZO1X is block oriented and carries no
    // length or end-of-stream information, so genuine stream processing needs a container.
    // Layout (all integers little-endian; documented in the README):
    //
    //   Header:   magic[8] = 89 4D 4C 5A 53 0D 0A 1A   ("\x89MLZS\r\n\x1A")
    //             version  = 01
    //             blockSize: uint32 (1 .. 64 MiB) — the maximum uncompressed block length
    //   Block:    type: byte (1 = compressed, 2 = stored)
    //             uncompressedLength: uint32 (1 .. blockSize)
    //             compressedLength:   uint32 (== uncompressedLength for stored blocks;
    //                                         >= 1 and < uncompressedLength for compressed)
    //             payload[compressedLength]
    //   Trailer:  type: byte = 0 (end of stream)
    //
    // Blocks are independent: each compressed payload is a complete raw LZO1X block.

    private static ReadOnlySpan<byte> FrameMagic => [0x89, (byte)'M', (byte)'L', (byte)'Z', (byte)'S', 0x0D, 0x0A, 0x1A];

    private const byte FrameVersion = 1;
    private const int FrameHeaderLength = 13; // magic + version + blockSize
    private const int BlockHeaderLength = 9;  // type + uncompressedLength + compressedLength
    private const int DefaultBlockSize = 256 * 1024;

    /// <summary>
    /// Upper bound on the block size accepted when decompressing, so a malicious 13-byte
    /// header cannot make the decoder allocate unbounded buffers.
    /// </summary>
    private const int MaxAcceptedBlockSize = 64 * 1024 * 1024;

    /// <inheritdoc />
    public void Compress(Stream source, Stream destination)
    {
        ValidateStreamArguments(source, destination);

        byte[] input = ArrayPool<byte>.Shared.Rent(DefaultBlockSize);
        byte[] output = ArrayPool<byte>.Shared.Rent(
            (int)GetMaxCompressedLength(DefaultBlockSize) + CompressScratchSlack);
        ushort[] dictionary = ArrayPool<ushort>.Shared.Rent(DictionarySize);
        try
        {
            Span<byte> header = stackalloc byte[FrameHeaderLength];
            FrameMagic.CopyTo(header);
            header[8] = FrameVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(header[9..], DefaultBlockSize);
            destination.Write(header);

            Span<byte> blockHeader = stackalloc byte[BlockHeaderLength];
            for (; ; )
            {
                // ReadAtLeast loops over partial reads and tolerates non-seekable sources.
                int read = source.ReadAtLeast(input.AsSpan(0, DefaultBlockSize), DefaultBlockSize, throwOnEndOfStream: false);
                if (read == 0)
                {
                    break;
                }

                int compressedLength = CompressBlock(input.AsSpan(0, read), output, dictionary);
                if (compressedLength < read)
                {
                    blockHeader[0] = (byte)MiniLzoBlockType.Compressed;
                    BinaryPrimitives.WriteUInt32LittleEndian(blockHeader[1..], (uint)read);
                    BinaryPrimitives.WriteUInt32LittleEndian(blockHeader[5..], (uint)compressedLength);
                    destination.Write(blockHeader);
                    destination.Write(output, 0, compressedLength);
                }
                else
                {
                    // Compression did not shrink the block: store it verbatim.
                    blockHeader[0] = (byte)MiniLzoBlockType.Stored;
                    BinaryPrimitives.WriteUInt32LittleEndian(blockHeader[1..], (uint)read);
                    BinaryPrimitives.WriteUInt32LittleEndian(blockHeader[5..], (uint)read);
                    destination.Write(blockHeader);
                    destination.Write(input, 0, read);
                }
            }

            destination.WriteByte((byte)MiniLzoBlockType.EndOfStream);
            destination.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
            ArrayPool<ushort>.Shared.Return(dictionary);
        }
    }

    /// <inheritdoc />
    public void Decompress(Stream source, Stream destination)
    {
        ValidateStreamArguments(source, destination);

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        ReadExactlyOrThrow(source, header);
        if (!header[..8].SequenceEqual(FrameMagic))
        {
            throw new InvalidDataException("The stream does not start with the MiniLzoSharp magic signature.");
        }

        if (header[8] != FrameVersion)
        {
            throw new InvalidDataException($"Unsupported MiniLzoSharp stream format version {header[8]}.");
        }

        uint declaredBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[9..]);
        if (declaredBlockSize is 0 or > MaxAcceptedBlockSize)
        {
            throw new InvalidDataException(
                $"Invalid declared block size {declaredBlockSize}; expected 1 to {MaxAcceptedBlockSize}.");
        }

        int blockSize = (int)declaredBlockSize;

        // Buffers are rented lazily and grown per observed block sizes, so a stream that
        // declares a huge block size but only carries small blocks stays cheap.
        byte[]? compressed = null;
        byte[]? uncompressed = null;
        try
        {
            Span<byte> blockHeader = stackalloc byte[BlockHeaderLength - 1];
            for (; ; )
            {
                int type = source.ReadByte();
                if (type < 0)
                {
                    throw new InvalidDataException("The stream is truncated: missing the end-of-stream marker.");
                }

                if (type == (byte)MiniLzoBlockType.EndOfStream)
                {
                    // Done. Bytes after the marker (if any) are deliberately left unread.
                    destination.Flush();
                    return;
                }

                if (type is not ((byte)MiniLzoBlockType.Compressed or (byte)MiniLzoBlockType.Stored))
                {
                    throw new InvalidDataException($"Unknown block type {type} in MiniLzoSharp stream.");
                }

                ReadExactlyOrThrow(source, blockHeader);
                uint uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
                uint compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[4..]);

                if (uncompressedLength is 0 || uncompressedLength > (uint)blockSize)
                {
                    throw new InvalidDataException(
                        $"Invalid block: uncompressed length {uncompressedLength} is outside 1 to {blockSize}.");
                }

                if (type == (byte)MiniLzoBlockType.Stored)
                {
                    if (compressedLength != uncompressedLength)
                    {
                        throw new InvalidDataException(
                            "Invalid stored block: compressed and uncompressed lengths differ.");
                    }

                    EnsureRented(ref compressed, (int)compressedLength);
                    ReadExactlyOrThrow(source, compressed.AsSpan(0, (int)compressedLength));
                    destination.Write(compressed, 0, (int)compressedLength);
                    continue;
                }

                if (compressedLength is 0 || compressedLength >= uncompressedLength)
                {
                    // A conforming writer stores blocks that compression does not shrink.
                    throw new InvalidDataException(
                        "Invalid compressed block: the compressed length must be smaller than the uncompressed length.");
                }

                EnsureRented(ref compressed, (int)compressedLength);
                EnsureRented(ref uncompressed, (int)uncompressedLength);
                ReadExactlyOrThrow(source, compressed.AsSpan(0, (int)compressedLength));

                int written = DecompressBlock(
                    compressed.AsSpan(0, (int)compressedLength),
                    uncompressed.AsSpan(0, (int)uncompressedLength),
                    out int consumed);
                if (written != (int)uncompressedLength || consumed != (int)compressedLength)
                {
                    throw new InvalidDataException(
                        "Corrupt block: the payload did not decompress to exactly the declared lengths.");
                }

                destination.Write(uncompressed, 0, written);
            }
        }
        finally
        {
            if (compressed is not null)
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }

            if (uncompressed is not null)
            {
                ArrayPool<byte>.Shared.Return(uncompressed);
            }
        }
    }

    private static void ValidateStreamArguments(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(source, destination))
        {
            throw new ArgumentException("Source and destination must not be the same stream instance.", nameof(destination));
        }

        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }
    }

    /// <summary>Reads exactly <c>buffer.Length</c> bytes or reports the stream as truncated.</summary>
    private static void ReadExactlyOrThrow(Stream source, Span<byte> buffer)
    {
        int read = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read != buffer.Length)
        {
            throw new InvalidDataException("The stream is truncated: it ended in the middle of a frame structure.");
        }
    }

    /// <summary>Rents (or re-rents a larger) pooled buffer of at least <paramref name="minimumLength"/> bytes.</summary>
    private static void EnsureRented([NotNull] ref byte[]? buffer, int minimumLength)
    {
        if (buffer is null || buffer.Length < minimumLength)
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
        }
    }
}
