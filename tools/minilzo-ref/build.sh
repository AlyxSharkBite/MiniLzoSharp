#!/bin/sh
# Builds minilzo_ref, the upstream reference harness used by the interop tests.
# Compiles the UNMODIFIED upstream minilzo.c vendored under third_party/minilzo together
# with minilzo_ref.c. On Windows use build.ps1 instead.

set -eu

script_dir="$(cd "$(dirname "$0")" && pwd)"
upstream="$script_dir/../../third_party/minilzo"
bin_dir="$script_dir/bin"

mkdir -p "$bin_dir"

: "${CC:=cc}"

"$CC" -O2 -Wall -I"$upstream" \
    "$upstream/minilzo.c" "$script_dir/minilzo_ref.c" \
    -o "$bin_dir/minilzo_ref"

echo "Built $bin_dir/minilzo_ref"
