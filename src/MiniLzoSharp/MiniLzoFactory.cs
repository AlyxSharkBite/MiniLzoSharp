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
/// Creates <see cref="IMiniLzoCodec"/> instances.
/// </summary>
public static class MiniLzoFactory
{
    /// <summary>
    /// Creates an LZO1X-1 codec. The returned codec is stateless and safe for concurrent
    /// use; a single instance can be shared by an entire application.
    /// </summary>
    /// <returns>A new codec instance.</returns>
    public static IMiniLzoCodec Create() => new MiniLzoCodec();
}
