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
        while (true)
        {
            if (_currentBuffer is not null && _currentOffset < _currentBuffer.Length)
            {
                var bytesToCopy = Math.Min(count, _currentBuffer.Length - _currentOffset);
                Array.Copy(_currentBuffer, _currentOffset, buffer, offset, bytesToCopy);
                _currentOffset += bytesToCopy;
                return bytesToCopy;
            }

            _currentBuffer = null;
            _currentOffset = 0;

            if (_buffers.IsCompleted)
            {
                return 0;
            }

            try
            {
                _currentBuffer = _buffers.Take();
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
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
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
