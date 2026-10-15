namespace FlightLog.LogParsing;

/// <summary>Buffered forward reader over a seekable stream with absolute positioning.</summary>
internal sealed class SeekableReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[1 << 16];
    private long _bufferStart;
    private int _bufferLength;
    private int _offset;

    public SeekableReader(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        _bufferStart = stream.Position;
    }

    public long Position => _bufferStart + _offset;

    public long Length => _stream.Length;

    public void Seek(long position)
    {
        if (position >= _bufferStart && position <= _bufferStart + _bufferLength)
        {
            _offset = (int)(position - _bufferStart);
            return;
        }

        _stream.Position = position;
        _bufferStart = position;
        _bufferLength = 0;
        _offset = 0;
    }

    /// <summary>Copies <paramref name="destination"/>.Length bytes; returns false (position unchanged) at end of data.</summary>
    public bool TryRead(Span<byte> destination)
    {
        if (!Ensure(destination.Length))
        {
            return false;
        }

        _buffer.AsSpan(_offset, destination.Length).CopyTo(destination);
        _offset += destination.Length;
        return true;
    }

    /// <summary>Returns the next byte, or -1 at end of data.</summary>
    public int ReadByte()
    {
        if (!Ensure(1))
        {
            return -1;
        }

        return _buffer[_offset++];
    }

    public int PeekByte()
    {
        if (!Ensure(1))
        {
            return -1;
        }

        return _buffer[_offset];
    }

    private bool Ensure(int count)
    {
        if (_bufferLength - _offset >= count)
        {
            return true;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length);

        // Move the unread tail to the front and refill.
        var remaining = _bufferLength - _offset;
        Buffer.BlockCopy(_buffer, _offset, _buffer, 0, remaining);
        _bufferStart += _offset;
        _offset = 0;
        _bufferLength = remaining;
        _stream.Position = _bufferStart + _bufferLength;
        while (_bufferLength < count)
        {
            var read = _stream.Read(_buffer, _bufferLength, _buffer.Length - _bufferLength);
            if (read == 0)
            {
                return false;
            }

            _bufferLength += read;
        }

        return true;
    }
}
