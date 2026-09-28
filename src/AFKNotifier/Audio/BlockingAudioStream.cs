using System.Collections.Concurrent;
using System.IO;

namespace AFKNotifier.Audio;

public sealed class BlockingAudioStream : Stream
{
    private readonly BlockingCollection<byte[]> _buffers = new(64);
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private bool _disposed;

    public void Enqueue(ReadOnlySpan<byte> data)
    {
        if (_disposed || _buffers.IsAddingCompleted || data.IsEmpty)
        {
            return;
        }

        var copy = data.ToArray();
        if (_buffers.TryAdd(copy))
        {
            return;
        }

        _buffers.TryTake(out _);
        _buffers.TryAdd(copy);
    }

    public void Complete()
    {
        if (!_buffers.IsAddingCompleted)
        {
            _buffers.CompleteAdding();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var written = 0;

        while (written < count)
        {
            if (_currentBuffer is not null && _currentOffset < _currentBuffer.Length)
            {
                var bytesToCopy = Math.Min(count - written, _currentBuffer.Length - _currentOffset);
                Array.Copy(_currentBuffer, _currentOffset, buffer, offset + written, bytesToCopy);
                _currentOffset += bytesToCopy;
                written += bytesToCopy;
                continue;
            }

            _currentBuffer = null;
            _currentOffset = 0;

            if (_buffers.IsCompleted)
            {
                return written;
            }

            try
            {
                _currentBuffer = _buffers.Take();
            }
            catch (InvalidOperationException)
            {
                return written;
            }
        }

        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Complete();
            _buffers.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => -1;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => 0;
    public override void SetLength(long value)
    {
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
