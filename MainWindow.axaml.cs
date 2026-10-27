using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaEdit.Document;

namespace viddy_well;

public partial class MainWindow : Window
{
    private const int FragmentLineCount = 1_000;

    private readonly List<OpenLogFile> _openLogs = [];
    private readonly List<CancellationTokenSource> _fragmentLoadCancellations = [];

    // Required by Avalonia's runtime XAML loader and useful for the visual designer.
    public MainWindow() : this([])
    {
    }

    public MainWindow(IEnumerable<string> logFileNames)
    {
        InitializeComponent();
        Closed += OnClosed;

        foreach (var fileName in logFileNames)
        {
            AddLogTab(fileName);
        }

        if (_openLogs.Count == 0)
        {
            StatusText.Text = "Pass one or more log-file paths on the command line.";
        }
    }

    private void AddLogTab(string fileName)
    {
        try
        {
            var log = new OpenLogFile(fileName);
            var document = new TextDocument();
            document.UndoStack.SizeLimit = 0;

            var logView = new LogView();
            logView.SetFragment(document, firstLogicalLine: 0);
            logView.TotalLineCount = 1;
            logView.ScrollMaximum = log.InitialFileLength;

            var fullPath = Path.GetFullPath(fileName);
            var group = new GroupBox
            {
                Header = fullPath,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Content = logView,
            };
            var tab = new TabItem
            {
                Header = Path.GetFileName(fullPath),
                Content = group,
            };

            var initialSampleComplete = log.InitialFragmentStartOffset == log.InitialFileLength;
            var estimatedLineCount = 1L;
            var averageBytesPerLine = 1.0;
            var lineCountIsEstimated = log.InitialFragmentStartOffset > 0;
            CancellationTokenSource? activeFragmentLoad = null;

            void UpdateLineCountDisplay()
            {
                var prefix = lineCountIsEstimated ? "~" : string.Empty;
                if (ReferenceEquals(LogTabs.SelectedItem, tab))
                {
                    StatusText.Text = $"Tailing {fullPath}: {prefix}{logView.TotalLineCount:N0} lines";
                }
            }

            logView.LogicalScrollRequested += async (_, request) =>
            {
                activeFragmentLoad?.Cancel();
                var cancellation = new CancellationTokenSource();
                activeFragmentLoad = cancellation;
                _fragmentLoadCancellations.Add(cancellation);
                logView.SetFragmentLoading(true);

                try
                {
                    // Avoid loading every intermediate position while the scrollbar thumb is dragged.
                    await Task.Delay(75, cancellation.Token);
                    var fragment = await log.LoadFragmentByOffsetAsync(
                        request.Offset,
                        FragmentLineCount,
                        cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();

                    if (fragment.LineCount == 0)
                    {
                        StatusText.Text = $"Byte offset {request.Offset:N0} is beyond the available file content.";
                        return;
                    }

                    var fragmentDocument = new TextDocument(fragment.Text);
                    fragmentDocument.UndoStack.SizeLimit = 0;
                    var estimatedFirstLine = (long)Math.Max(0, Math.Floor(fragment.StartOffset / averageBytesPerLine));
                    logView.SetFragment(
                        fragmentDocument,
                        estimatedFirstLine,
                        fragment.StartOffset,
                        fragment.EndOffset);
                    logView.TotalLineCount = Math.Max(estimatedLineCount, estimatedFirstLine + fragment.LineCount);
                    logView.ScrollMaximum = Math.Max(log.InitialFileLength, fragment.EndOffset);
                    logView.ScrollToByteOffset(Math.Clamp(
                        request.Offset,
                        fragment.StartOffset,
                        fragment.EndOffset));
                }
                catch (OperationCanceledException)
                {
                    // A newer scrollbar position superseded this request.
                }
                catch (Exception exception)
                {
                    StatusText.Text = $"Unable to load log fragment: {exception.Message}";
                }
                finally
                {
                    _fragmentLoadCancellations.Remove(cancellation);
                    cancellation.Dispose();
                    if (ReferenceEquals(activeFragmentLoad, cancellation))
                    {
                        activeFragmentLoad = null;
                        logView.SetFragmentLoading(false);
                    }
                }
            };

            log.FragmentReceived += (_, fragment) =>
            {
                // Do not append a tail update into a historical fragment the user is reading.
                if (ReferenceEquals(logView.Editor.Document, document))
                {
                    logView.AppendText(fragment.Text);
                    logView.UpdateFragmentByteRange(log.InitialFragmentStartOffset, fragment.EndOffset);
                }

                logView.ScrollMaximum = Math.Max(logView.ScrollMaximum, fragment.EndOffset);

                if (!initialSampleComplete && fragment.EndOffset >= log.InitialFileLength)
                {
                    initialSampleComplete = true;
                    var sampleLineCount = CountLines(document);
                    if (sampleLineCount > 0)
                    {
                        var sampleByteCount = log.InitialFileLength - log.InitialFragmentStartOffset;
                        averageBytesPerLine = sampleByteCount / (double)sampleLineCount;
                        estimatedLineCount = Math.Max(
                            document.LineCount,
                            (long)Math.Ceiling(log.InitialFileLength / averageBytesPerLine));
                        logView.TotalLineCount = estimatedLineCount;
                        logView.SetFragment(
                            document,
                            firstLogicalLine: Math.Max(0, estimatedLineCount - CountLines(document)),
                            firstByteOffset: log.InitialFragmentStartOffset,
                            endByteOffset: log.InitialFileLength);
                        logView.ScrollMaximum = log.InitialFileLength;
                    }
                }
                else if (initialSampleComplete)
                {
                    // We do not index the full file yet. Increase the estimate as complete
                    // appended records arrive instead.
                    estimatedLineCount += CountLineTerminators(fragment.Text);
                    logView.TotalLineCount = Math.Max(estimatedLineCount, document.LineCount);
                }

                UpdateLineCountDisplay();
            };
            log.ReadFailed += (_, exception) => StatusText.Text = $"Unable to read {log.FileName}: {exception.Message}";

            LogTabs.Items.Add(tab);
            _openLogs.Add(log);
            StatusText.Text = $"Tailing {_openLogs.Count:N0} log file(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = $"Unable to open {fileName}: {exception.Message}";
        }
    }

    private static long CountLines(TextDocument document)
    {
        var lineCount = document.LineCount;
        return document.TextLength > 0 && document.GetText(document.TextLength - 1, 1) == "\n"
            ? lineCount - 1
            : lineCount;
    }

    private static long CountLineTerminators(string text)
    {
        var lineCount = 0L;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lineCount++;
            }
        }

        return lineCount;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach (var cancellation in _fragmentLoadCancellations)
        {
            cancellation.Cancel();
        }

        foreach (var log in _openLogs)
        {
            log.Dispose();
        }

        _openLogs.Clear();
    }
}
