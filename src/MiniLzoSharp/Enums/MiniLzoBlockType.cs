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

namespace MiniLzoSharp.Enums;

/// <summary>
/// The block type tag that starts every block in the MiniLzoSharp framed stream format.
/// The numeric values are part of the on-disk format and must not change.
/// </summary>
internal enum MiniLzoBlockType : byte
{
    /// <summary>The unambiguous end-of-stream marker; no header fields or payload follow.</summary>
    EndOfStream = 0,

    /// <summary>A block whose payload is a raw LZO1X block, strictly smaller than the data it encodes.</summary>
    Compressed = 1,

    /// <summary>A block stored verbatim because compression would not have made it smaller.</summary>
    Stored = 2,
}
