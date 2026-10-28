using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace viddy_well;

/// <summary>
/// Tails a log file from a dedicated background thread. Fragments are posted to the Avalonia
/// UI thread, so subscribers can update controls without dispatching again.
/// </summary>
public sealed class OpenLogFile : IDisposable
{
    private const int ReadBufferSize = 64 * 1024;
    private const int DefaultInitialFragmentLineCount = 1_024;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly AutoResetEvent _fileChanged = new(initialState: false);
    private readonly FileSystemWatcher _watcher;
    private readonly Thread _readerThread;
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
    private long _readPosition;
    private long _initialFragmentEndOffset;
    private bool _disposed;

    /// <param name="fileName">The log file to tail.</param>
    /// <param name="initialFragmentLineCount">
    /// Maximum number of existing lines loaded on open. The reader starts at the beginning of
    /// this tail fragment rather than replaying the whole file.
    /// </param>
    /// <param name="startAtEnd">When true, skip existing content and receive only future writes.</param>
    public OpenLogFile(
        string fileName,
        int initialFragmentLineCount = DefaultInitialFragmentLineCount,
        bool startAtEnd = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(initialFragmentLineCount);

        InitialFragmentLineCount = initialFragmentLineCount;
        FileName = Path.GetFullPath(fileName);
        ReadStream = new FileStream(
            FileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: ReadBufferSize,
            FileOptions.SequentialScan);

        InitialFileLength = ReadStream.Length;
        _readPosition = startAtEnd
            ? InitialFileLength
            : FindStartOffsetForTailFragment(InitialFileLength, InitialFragmentLineCount);
        InitialFragmentStartOffset = _readPosition;
        _initialFragmentEndOffset = InitialFileLength;
        ReadStream.Position = _readPosition;

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FileName)!, Path.GetFileName(FileName))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileSystemChanged;
        _watcher.Created += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemRenamed;
        _watcher.Error += OnFileSystemWatcherError;
        _watcher.EnableRaisingEvents = true;

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

    /// <summary>Raised on the Avalonia UI thread for the initial tail fragment and later appended fragments.</summary>
    public event EventHandler<LogFragmentEventArgs>? FragmentReceived;

    /// <summary>Maximum number of pre-existing lines read when this file was opened.</summary>
    public int InitialFragmentLineCount { get; }

    /// <summary>File size captured before reading the initial tail fragment.</summary>
    public long InitialFileLength { get; }

    /// <summary>Byte offset of the initial tail fragment in <see cref="FileName"/>.</summary>
    public long InitialFragmentStartOffset { get; }

    /// <summary>Raised on the Avalonia UI thread if reading the file fails.</summary>
    public event EventHandler<Exception>? ReadFailed;

    /// <summary>
    /// Loads up to <paramref name="lineCount"/> lines beginning at the zero-based logical line
    /// <paramref name="firstLine"/>. This uses a separate read-only stream and does not disturb
    /// the tailing stream.
    /// </summary>
    /// <remarks>
    /// Until a sparse line-offset index is added, locating a historical line scans from the
    /// beginning of the file. The scan runs on a worker thread and only the requested fragment's
    /// decoded text is retained.
    /// </remarks>
    public Task<LogFileFragment> LoadFragmentByLineAsync(
        long firstLine,
        int lineCount,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(firstLine);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineCount);

        return Task.Run(() => LoadFragmentByLine(firstLine, lineCount, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Loads up to <paramref name="lineCount"/> complete lines near a byte offset. The requested
    /// offset is moved back to the preceding line boundary before decoding the fragment.
    /// </summary>
    public Task<LogFileFragment> LoadFragmentByOffsetAsync(
        long byteOffset,
        int lineCount,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineCount);

        return Task.Run(() => LoadFragmentByOffset(byteOffset, lineCount, cancellationToken), cancellationToken);
    }

    private LogFileFragment LoadFragmentByOffset(long byteOffset, int lineCount, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            FileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: ReadBufferSize,
            FileOptions.SequentialScan);

        var startOffset = FindLineStartOffset(stream, byteOffset, cancellationToken);
        stream.Position = startOffset;
        return ReadFragment(stream, firstLine: 0, startOffset, lineCount, cancellationToken);
    }

    private LogFileFragment LoadFragmentByLine(long firstLine, int lineCount, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            FileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: ReadBufferSize,
            FileOptions.SequentialScan);

        var startOffset = FindOffsetForLine(stream, firstLine, cancellationToken);
        stream.Position = startOffset;

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            using var fragmentBytes = new MemoryStream();
            var completedLines = 0;
            var lastByteWasNewline = false;

            while (completedLines < lineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                var bytesToKeep = bytesRead;
                for (var index = 0; index < bytesRead; index++)
                {
                    if (buffer[index] != '\n')
                    {
                        continue;
                    }

                    completedLines++;
                    if (completedLines != lineCount)
                    {
                        continue;
                    }

                    bytesToKeep = index + 1;
                    break;
                }

                fragmentBytes.Write(buffer, 0, bytesToKeep);
                lastByteWasNewline = bytesToKeep > 0 && buffer[bytesToKeep - 1] == '\n';
                if (completedLines == lineCount)
                {
                    break;
                }
            }

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(fragmentBytes.GetBuffer(), 0, (int)fragmentBytes.Length);
            var actualLineCount = completedLines +
                (fragmentBytes.Length > 0 && !lastByteWasNewline ? 1 : 0);

            return new LogFileFragment(
                firstLine,
                startOffset,
                startOffset + fragmentBytes.Length,
                text,
                actualLineCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static LogFileFragment ReadFragment(
        FileStream stream,
        long firstLine,
        long startOffset,
        int lineCount,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            using var fragmentBytes = new MemoryStream();
            var completedLines = 0;
            var lastByteWasNewline = false;

            while (completedLines < lineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                var bytesToKeep = bytesRead;
                for (var index = 0; index < bytesRead; index++)
                {
                    if (buffer[index] != '\n')
                    {
                        continue;
                    }

                    completedLines++;
                    if (completedLines != lineCount)
                    {
                        continue;
                    }

                    bytesToKeep = index + 1;
                    break;
                }

                fragmentBytes.Write(buffer, 0, bytesToKeep);
                lastByteWasNewline = bytesToKeep > 0 && buffer[bytesToKeep - 1] == '\n';
                if (completedLines == lineCount)
                {
                    break;
                }
            }

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(fragmentBytes.GetBuffer(), 0, (int)fragmentBytes.Length);
            var actualLineCount = completedLines +
                (fragmentBytes.Length > 0 && !lastByteWasNewline ? 1 : 0);

            return new LogFileFragment(
                firstLine,
                startOffset,
                startOffset + fragmentBytes.Length,
                text,
                actualLineCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long FindLineStartOffset(FileStream stream, long requestedOffset, CancellationToken cancellationToken)
    {
        var fileLength = stream.Length;
        var position = Math.Min(requestedOffset, fileLength);
        if (position == 0)
        {
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (position > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = (int)Math.Min(buffer.Length, position);
                position -= length;
                stream.Position = position;
                var bytesRead = ReadFully(stream, buffer, length);

                for (var index = bytesRead - 1; index >= 0; index--)
                {
                    if (buffer[index] == '\n')
                    {
                        return position + index + 1;
                    }
                }
            }

            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadFully(FileStream stream, byte[] buffer, int requestedCount)
    {
        var totalRead = 0;
        while (totalRead < requestedCount)
        {
            var read = stream.Read(buffer, totalRead, requestedCount - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static long FindOffsetForLine(FileStream stream, long targetLine, CancellationToken cancellationToken)
    {
        if (targetLine == 0)
        {
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            long offset = 0;
            long linesSeen = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    return stream.Length;
                }

                for (var index = 0; index < bytesRead; index++)
                {
                    if (buffer[index] != '\n')
                    {
                        continue;
                    }

                    linesSeen++;
                    if (linesSeen == targetLine)
                    {
                        return offset + index + 1;
                    }
                }

                offset += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private long FindStartOffsetForTailFragment(long fileLength, int lineCount)
    {
        if (fileLength == 0 || lineCount == 0)
        {
            return fileLength;
        }

        var bytes = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            // A final newline represents an empty final document line. Skip it when counting so
            // the initial fragment contains the requested number of actual log records.
            ReadStream.Position = fileLength - 1;
            var trailingNewline = ReadStream.ReadByte() == '\n';
            var newlineTarget = lineCount + (trailingNewline ? 1 : 0);
            var newlineCount = 0;
            var position = fileLength;

            while (position > 0)
            {
                var length = (int)Math.Min(bytes.Length, position);
                position -= length;
                ReadStream.Position = position;
                var bytesRead = ReadFully(bytes, length);

                for (var index = bytesRead - 1; index >= 0; index--)
                {
                    if (bytes[index] != '\n')
                    {
                        continue;
                    }

                    newlineCount++;
                    if (newlineCount == newlineTarget)
                    {
                        return position + index + 1;
                    }
                }
            }

            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private int ReadFully(byte[] buffer, int requestedCount)
    {
        var totalRead = 0;
        while (totalRead < requestedCount)
        {
            var read = ReadStream.Read(buffer, totalRead, requestedCount - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

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
                    WaitForFileChange();
                    continue;
                }

                // Keep the initial tail sample separate from entries appended while it is read.
                // The sample is used by consumers to estimate the full file's line count.
                var readableEnd = _readPosition < _initialFragmentEndOffset
                    ? Math.Min(fileLength, _initialFragmentEndOffset)
                    : fileLength;
                if (_readPosition >= readableEnd)
                {
                    WaitForFileChange();
                    continue;
                }

                var fragmentStart = _readPosition;
                ReadStream.Position = _readPosition;
                var bytesToRead = (int)Math.Min(bytes.Length, readableEnd - _readPosition);
                var bytesRead = ReadStream.Read(bytes, 0, bytesToRead);
                if (bytesRead == 0)
                {
                    WaitForFileChange();
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

    private void OnFileSystemChanged(object? sender, FileSystemEventArgs e)
    {
        SignalFileChange();
    }

    private void OnFileSystemRenamed(object? sender, RenamedEventArgs e)
    {
        SignalFileChange();
    }

    private void OnFileSystemWatcherError(object? sender, ErrorEventArgs e)
    {
        // Recheck the file after an inotify/OS watcher overflow instead of waiting indefinitely.
        SignalFileChange();
    }

    private void SignalFileChange()
    {
        try
        {
            _fileChanged.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposal may race with a final watcher callback.
        }
    }

    private void WaitForFileChange()
    {
        _fileChanged.WaitOne();
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
            {
                FragmentReceived?.Invoke(this, fragment);
            }
        });
    }

    private void PostFailure(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ReadFailed?.Invoke(this, exception);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Dispose();
        _cancellation.Cancel();
        SignalFileChange();
        _readerThread.Join();
        ReadStream.Dispose();
        _fileChanged.Dispose();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A line-addressable fragment loaded from a log file without changing the tail reader.</summary>
public sealed class LogFileFragment(
    long firstLine,
    long startOffset,
    long endOffset,
    string text,
    int lineCount)
{
    /// <summary>Zero-based logical line number corresponding to the first returned line.</summary>
    public long FirstLine { get; } = firstLine;
    public long StartOffset { get; } = startOffset;
    public long EndOffset { get; } = endOffset;
    public string Text { get; } = text;
    public int LineCount { get; } = lineCount;
}

/// <summary>A decoded, contiguous byte range read from an open log file.</summary>
public sealed class LogFragmentEventArgs(long startOffset, long endOffset, string text) : EventArgs
{
    public long StartOffset { get; } = startOffset;
    public long EndOffset { get; } = endOffset;
    public string Text { get; } = text;
}
