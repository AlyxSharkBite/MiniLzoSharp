/* minilzo_ref.c -- CLI wrapper around the unmodified upstream miniLZO sources,
 * used by the MiniLzoSharp test suite to cross-validate interoperability.
 *
 * This file is part of MiniLzoSharp and links against minilzo.c from the
 * repository root, which is part of the LZO real-time data compression library,
 * Copyright (C) 1996-2017 Markus Franz Xaver Johannes Oberhumer.
 * https://www.oberhumer.com/opensource/lzo/
 *
 * This program is free software; you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation; either version 2 of the License, or (at your
 * option) any later version.  See the file COPYING for details.
 *
 * Usage:
 *   minilzo_ref c <infile> <outfile>            compress with lzo1x_1_compress
 *   minilzo_ref d <infile> <outfile> <maxout>   decompress with lzo1x_decompress_safe
 *
 * Exit codes: 0 success, 1 usage/io error, 2 lzo_init failed, 3 compress failed,
 * 4 decompress failed (the LZO error code is printed to stderr).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "minilzo.h"

#define HEAP_ALLOC(var, size) \
    lzo_align_t var[((size) + (sizeof(lzo_align_t) - 1)) / sizeof(lzo_align_t)]

static HEAP_ALLOC(wrkmem, LZO1X_1_MEM_COMPRESS);

static unsigned char *read_file(const char *path, size_t *out_len)
{
    FILE *f = fopen(path, "rb");
    unsigned char *data;
    long len;

    if (f == NULL)
        return NULL;
    if (fseek(f, 0, SEEK_END) != 0 || (len = ftell(f)) < 0 || fseek(f, 0, SEEK_SET) != 0)
    {
        fclose(f);
        return NULL;
    }
    data = (unsigned char *) malloc(len > 0 ? (size_t) len : 1);
    if (data == NULL)
    {
        fclose(f);
        return NULL;
    }
    if (len > 0 && fread(data, 1, (size_t) len, f) != (size_t) len)
    {
        free(data);
        fclose(f);
        return NULL;
    }
    fclose(f);
    *out_len = (size_t) len;
    return data;
}

static int write_file(const char *path, const unsigned char *data, size_t len)
{
    FILE *f = fopen(path, "wb");
    if (f == NULL)
        return -1;
    if (len > 0 && fwrite(data, 1, len, f) != len)
    {
        fclose(f);
        return -1;
    }
    fclose(f);
    return 0;
}

int main(int argc, char *argv[])
{
    unsigned char *in;
    unsigned char *out;
    size_t in_len;
    lzo_uint out_len;
    int r;

    if (argc < 4)
    {
        fprintf(stderr, "usage: %s c <in> <out> | %s d <in> <out> <maxout>\n", argv[0], argv[0]);
        return 1;
    }

    if (lzo_init() != LZO_E_OK)
    {
        fprintf(stderr, "lzo_init failed\n");
        return 2;
    }

    in = read_file(argv[2], &in_len);
    if (in == NULL)
    {
        fprintf(stderr, "cannot read %s\n", argv[2]);
        return 1;
    }

    if (argv[1][0] == 'c')
    {
        size_t out_cap = in_len + in_len / 16 + 64 + 3;
        out = (unsigned char *) malloc(out_cap);
        if (out == NULL)
        {
            fprintf(stderr, "out of memory\n");
            return 1;
        }
        out_len = 0;
        r = lzo1x_1_compress(in, (lzo_uint) in_len, out, &out_len, wrkmem);
        if (r != LZO_E_OK)
        {
            fprintf(stderr, "lzo1x_1_compress failed: %d\n", r);
            return 3;
        }
    }
    else if (argv[1][0] == 'd')
    {
        unsigned long max_out;
        if (argc < 5)
        {
            fprintf(stderr, "decompress requires <maxout>\n");
            return 1;
        }
        max_out = strtoul(argv[4], NULL, 10);
        out = (unsigned char *) malloc(max_out > 0 ? max_out : 1);
        if (out == NULL)
        {
            fprintf(stderr, "out of memory\n");
            return 1;
        }
        out_len = (lzo_uint) max_out;
        r = lzo1x_decompress_safe(in, (lzo_uint) in_len, out, &out_len, NULL);
        if (r != LZO_E_OK)
        {
            fprintf(stderr, "lzo1x_decompress_safe failed: %d\n", r);
            return 4;
        }
    }
    else
    {
        fprintf(stderr, "unknown mode '%s'\n", argv[1]);
        return 1;
    }

    if (write_file(argv[3], out, (size_t) out_len) != 0)
    {
        fprintf(stderr, "cannot write %s\n", argv[3]);
        return 1;
    }

    free(in);
    free(out);
    return 0;
}
