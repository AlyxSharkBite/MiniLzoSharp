# Security Policy

## Reporting a vulnerability

Please report security issues **privately**, not through a public issue.

Use GitHub's private vulnerability reporting, which is enabled on this repository:

**https://github.com/AlyxSharkBite/MiniLzoSharp/security/advisories/new**

That opens a private advisory visible only to you and the maintainer. If you cannot use it, open a
public issue containing no details beyond a request for a private channel, and you will be sent one.

### What to include

A report is much easier to act on with:

- The version of MiniLzoSharp affected, and the .NET runtime and architecture you observed it on
- The input that triggers the behaviour, as a file or a hex dump — for a decoder bug, the raw LZO1X
  block or framed stream itself is the reproduction
- Which API path was used: `Compress`/`Decompress` on `byte[]`, or the `Stream` overloads
- What you expected to happen, and what happened instead

### What to expect

| Stage | Target |
| --- | --- |
| Acknowledgement of your report | within 3 business days |
| Initial assessment, with a severity judgement | within 7 days |
| Fix or documented mitigation for a confirmed issue | within 30 days |
| Public advisory, credited to you unless you prefer otherwise | after the fix ships |

This is a personal project maintained in spare time, so those are honest targets rather than a
contractual SLA. If a report goes unanswered past these windows, you are free to disclose publicly.

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |
| < 1.0 | No |

Fixes land on `main` and ship in the next release. There are no long-term support branches.

## Threat model

MiniLzoSharp decodes a compressed format that is, in most real deployments, **attacker
controlled** — LZO1X is used inside game assets, firmware images, kernel dumps, filesystem
containers and network protocols. Assume any block handed to `Decompress` may be hostile. The
following are in scope for a report:

- Memory corruption, out-of-range access, or any unhandled exception type escaping `Decompress`
  other than `InvalidDataException` (or `ArgumentNullException` / `ArgumentException` for a bad
  call)
- Infinite loops or non-terminating decoding on malformed input
- Any input that makes the decoder allocate beyond the documented cap below
- Incorrect output: a valid LZO1X block that decompresses to something other than what the
  upstream reference implementation produces
- Any way to make the compressor emit a block that upstream `lzo1x_decompress_safe` rejects, or
  that does not round-trip

Only the **safe** decompressor is exposed. Upstream's faster `lzo1x_decompress`, which omits the
overrun checks and is memory-unsafe on malformed input by design, is deliberately not implemented
and will not be added — every check from `lzo1x_decompress_safe` (input, output, and lookbehind) is
ported and always on.

The test suite fuzzes malformed input on every push — every single-bit mutation of a valid block,
every strict prefix of one, and thousands of random buffers — and asserts that nothing but an
`InvalidDataException` comes back, so a crash or hang on hostile input is a real bug, not expected
behaviour.

### Decompression bombs are bounded, but the bound is large

This is the one security-relevant limitation worth stating plainly, because it is a property of
the format rather than a defect.

LZO1X encodes long runs with a run-length extension in which each additional input byte adds 255
bytes to a match length. The maximum expansion is therefore **255:1**, and that ratio is
structural, not measured: a valid stream cannot do better.

The decoder enforces exactly that bound. `Decompress(byte[])` grows its output buffer
geometrically and refuses to exceed `255 × source.Length` (or the 2 GiB .NET array limit,
whichever is smaller), throwing `InvalidDataException` rather than allocating further. Corrupt
input that claims an impossible size is rejected instead of being obeyed. But 255:1 is still a
large multiplier:

```csharp
byte[] restored = codec.Decompress(untrusted);   // up to ~255x the input size, in memory at once
```

A one megabyte hostile input can therefore produce around 255 MB of output before the cap stops
it. When decompressing anything you do not control, check `source.Length` against your own budget
before calling, or use the framed stream API, where the per-block ceiling is the stream's declared
block size (capped on read at 64 MiB, and 256 KiB for anything this library writes) and output is
written through rather than accumulated.

### Out of scope

- Weaknesses in the LZO1X format itself. It is a real-time compression scheme with no integrity or
  authenticity guarantees, and no checksum of any kind. It is not encryption, and nothing in the
  format detects deliberate tampering. Authenticate data by other means.
- Resource use within the documented 255:1 bound, per the section above.
- The MiniLzoSharp framed stream format being unreadable by other LZO tools. That is by design and
  documented; raw LZO1X has no framing of its own.
- Findings against a modified copy of this library.

## Upstream

MiniLzoSharp is derived from **miniLZO**, part of the LZO real-time data compression library by
Markus F.X.J. Oberhumer: <https://www.oberhumer.com/opensource/lzo/>. The upstream C sources it was
validated against are vendored unmodified under [`third_party/minilzo`](third_party/minilzo).

A flaw in the shared algorithm, as opposed to this C# implementation of it, likely affects the
upstream C implementation and every other port of it. Please say so in your report so it can be
raised upstream as well. Report it here first; coordinating disclosure is easier than
un-publishing.

## How this project is checked

Every push and pull request to `main` runs, and must pass before merge:

- **Correctness against the original** — CI compiles the unmodified upstream `minilzo.c` and
  cross-validates in both directions (upstream compresses, this library decompresses, and the
  reverse), so a bug shared by this compressor and this decompressor cannot hide behind a
  self-round-trip
- **SAST** — CodeQL over both the C# sources and the workflow files themselves, using the
  `security-extended` and `security-and-quality` query suites, plus a weekly re-scan so that
  newly published queries are applied to existing code
- **SCA** — a daily audit for known-vulnerable and deprecated NuGet packages, and a dependency
  review gate on every pull request
- **Secret scanning** — gitleaks and TruffleHog over the full commit history, alongside GitHub's
  native secret scanning with push protection
- **Supply chain** — every GitHub Action pinned to a full commit SHA rather than a mutable tag,
  kept current by Dependabot, with OpenSSF Scorecard auditing the repository's own posture

Findings are published to the repository's
[Security tab](https://github.com/AlyxSharkBite/MiniLzoSharp/security).
