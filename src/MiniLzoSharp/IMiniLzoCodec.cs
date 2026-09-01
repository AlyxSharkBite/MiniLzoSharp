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

namespace MiniLzoSharp;

/// <summary>
/// A codec for the LZO1X data compression format as implemented by MiniLZO.
/// </summary>
/// <remarks>
/// <para>
/// The byte-array methods operate on <b>raw LZO1X blocks</b> that are fully interoperable
/// with the upstream C implementation (<c>lzo1x_1_compress</c> / <c>lzo1x_decompress_safe</c>).
/// </para>
/// <para>
/// The stream methods use a <b>MiniLzoSharp-specific framed format</b>, because raw LZO1X is
/// block oriented and carries no length or end-of-stream framing of its own. The framed
/// layout is documented in the project README. Framed streams are <em>not</em> readable by
/// tools that expect bare LZO1X blocks.
/// </para>
/// <para>
/// Implementations returned by <see cref="MiniLzoFactory.Create"/> are stateless and safe
/// for concurrent use from multiple threads.
/// </para>
/// </remarks>
public interface IMiniLzoCodec
{
    /// <summary>
    /// Compresses a buffer into a raw LZO1X-1 block.
    /// </summary>
    /// <param name="source">The bytes to compress. May be empty.</param>
    /// <returns>A newly allocated buffer holding the compressed block, including the
    /// LZO1X end-of-stream marker. The output can be decompressed by the upstream C
    /// decompressor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is so large that the
    /// worst-case compressed size could exceed the 2 GiB array limit; use
    /// <see cref="Compress(Stream, Stream)"/> for data of that size.</exception>
    byte[] Compress(byte[] source);

    /// <summary>
    /// Decompresses a raw LZO1X block. The original uncompressed length does not need to
    /// be known; the output buffer is grown safely as needed.
    /// </summary>
    /// <param name="source">A complete raw LZO1X block, such as one produced by
    /// <see cref="Compress(byte[])"/> or by the upstream C compressor.</param>
    /// <returns>A newly allocated buffer holding the decompressed bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The input is corrupt, truncated, contains an
    /// invalid match offset, has trailing bytes after the end-of-stream marker, or would
    /// expand beyond the maximum size a valid LZO1X block of this length can describe.</exception>
    byte[] Decompress(byte[] source);

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/> using the
    /// MiniLzoSharp framed stream format, processing the data incrementally in independent
    /// blocks (256 KiB by default). Blocks that do not shrink are stored verbatim.
    /// </summary>
    /// <param name="source">The stream to read uncompressed data from until end of stream.
    /// Non-seekable streams and streams that return partial reads are supported.</param>
    /// <param name="destination">The stream the framed compressed data is written to.</param>
    /// <exception cref="ArgumentNullException">Either stream is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not readable,
    /// <paramref name="destination"/> is not writable, or both parameters are the same
    /// stream instance.</exception>
    /// <remarks>Neither stream is closed or disposed. The destination is flushed after the
    /// end-of-stream marker is written.</remarks>
    void Compress(Stream source, Stream destination);

    /// <summary>
    /// Decompresses a MiniLzoSharp framed stream from <paramref name="source"/> into
    /// <paramref name="destination"/>, block by block, without loading the whole stream
    /// into memory.
    /// </summary>
    /// <param name="source">The stream holding data produced by
    /// <see cref="Compress(Stream, Stream)"/>. Reading stops directly after the
    /// end-of-stream marker; any following bytes are left unread. Non-seekable streams and
    /// streams that return partial reads are supported.</param>
    /// <param name="destination">The stream the decompressed data is written to.</param>
    /// <exception cref="ArgumentNullException">Either stream is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not readable,
    /// <paramref name="destination"/> is not writable, or both parameters are the same
    /// stream instance.</exception>
    /// <exception cref="InvalidDataException">The stream does not start with the
    /// MiniLzoSharp magic signature, declares an unsupported version or block size, is
    /// truncated, or contains a block whose header or payload fails validation.</exception>
    /// <remarks>Neither stream is closed or disposed. The destination is flushed after the
    /// last block is written.</remarks>
    void Decompress(Stream source, Stream destination);
}
