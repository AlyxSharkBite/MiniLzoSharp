namespace MiniLzoSharp.Tests;

/// <summary>A read-only, non-seekable wrapper that also returns deliberately tiny partial
/// reads, to prove the codec never assumes seekability or full reads.</summary>
internal sealed class TricklingReadStream(Stream inner, int maxChunk) : Stream
{
    private readonly Random _random = new(12345);

    public bool WasDisposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => inner.Read(buffer, offset, Math.Min(count, _random.Next(1, maxChunk + 1)));

    public override int Read(Span<byte> buffer)
        => inner.Read(buffer[..Math.Min(buffer.Length, _random.Next(1, maxChunk + 1))]);

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>A write-only, non-seekable stream that records disposal.</summary>
internal sealed class NonSeekableWriteStream(Stream inner) : Stream
{
    public bool WasDisposed { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}
