using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia.Threading;

namespace viddy_well;

/// <summary>
/// Tails a log file from a dedicated background thread. Fragments are posted to the Avalonia
/// UI thread, so subscribers can update controls without dispatching again.
/// </summary>
public sealed class OpenLogFile : IDisposable
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _readerThread;
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
    private long _readPosition;
    private bool _disposed;

    public OpenLogFile(string fileName, bool startAtEnd = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        FileName = Path.GetFullPath(fileName);
        ReadStream = new FileStream(
            FileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: ReadBufferSize,
            FileOptions.SequentialScan);

        _readPosition = startAtEnd ? ReadStream.Length : 0;
        ReadStream.Position = _readPosition;

        _readerThread = new Thread(ReadUpdates)
        {
            Name = $"Log reader: {Path.GetFileName(FileName)}",
            IsBackground = true,
        };
        _readerThread.Start();
    }

    /// <summary>Absolute path of the file being tailed.</summary>
    public string FileName { get; }

    /// <summary>
    /// The read-only stream used by the tailing thread. Consumers should not change its position
    /// or dispose it; use it only for read-only inspection.
    /// </summary>
    public FileStream ReadStream { get; }

    /// <summary>Raised on the Avalonia UI thread for every decoded appended fragment.</summary>
    public event EventHandler<LogFragmentEventArgs>? FragmentReceived;

    /// <summary>Raised on the Avalonia UI thread if reading the file fails.</summary>
    public event EventHandler<Exception>? ReadFailed;

    private void ReadUpdates()
    {
        var bytes = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                long fileLength;
                try
                {
                    fileLength = ReadStream.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    PostFailure(exception);
                    return;
                }

                // Log rotation or truncation: resume reading the replacement content at byte zero.
                if (fileLength < _readPosition)
                {
                    _readPosition = 0;
                    ReadStream.Position = 0;
                    _decoder.Reset();
                }

                if (_readPosition >= fileLength)
                {
                    Thread.Sleep(200);
                    continue;
                }

                var fragmentStart = _readPosition;
                ReadStream.Position = _readPosition;
                var bytesToRead = (int)Math.Min(bytes.Length, fileLength - _readPosition);
                var bytesRead = ReadStream.Read(bytes, 0, bytesToRead);
                if (bytesRead == 0)
                {
                    Thread.Sleep(50);
                    continue;
                }

                _readPosition += bytesRead;
                var text = Decode(bytes, bytesRead);
                if (text.Length != 0)
                {
                    PostFragment(new LogFragmentEventArgs(
                        fragmentStart,
                        _readPosition,
                        text));
                }
            }
        }
        catch (Exception exception) when (!_cancellation.IsCancellationRequested)
        {
            PostFailure(exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private string Decode(byte[] bytes, int byteCount)
    {
        var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(byteCount));
        try
        {
            _decoder.Convert(
                bytes, 0, byteCount,
                characters, 0, characters.Length,
                flush: false,
                out _, out var charactersUsed, out _);
            return new string(characters, 0, charactersUsed);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(characters);
        }
    }

    private void PostFragment(LogFragmentEventArgs fragment)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                FragmentReceived?.Invoke(this, fragment);
        });
    }

    private void PostFailure(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                ReadFailed?.Invoke(this, exception);
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _readerThread.Join();
        ReadStream.Dispose();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A decoded, contiguous byte range read from an open log file.</summary>
public sealed class LogFragmentEventArgs(long startOffset, long endOffset, string text) : EventArgs
{
    public long StartOffset { get; } = startOffset;
    public long EndOffset { get; } = endOffset;
    public string Text { get; } = text;
}
