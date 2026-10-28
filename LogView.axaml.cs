using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace viddy_well;

/// <summary>
/// Displays one loaded fragment of a larger, line-oriented log. Its scrollbar represents
/// logical lines in the complete log, not only the lines held by <see cref="Editor"/>.
/// </summary>
public partial class LogView : UserControl
{
    private readonly OffsetLineNumberMargin _lineNumberMargin = new();
    private bool _updatingScrollBar;
    private long _totalLineCount;
    private long _scrollMaximum;
    private long _firstFragmentLine;
    private long _firstFragmentByteOffset;
    private long _fragmentEndByteOffset;
    private bool _followingTail;
    private bool _initialTailPositionPending;
    private bool _tailLayoutPending;
    private double _tailScrollOffset;

    public LogView()
    {
        InitializeComponent();
        FragmentEditor.Document = new TextDocument();
        FragmentEditor.Options.AllowScrollBelowDocument = true;
        ReplaceLineNumberMargin();
        LogicalScrollBar.ValueChanged += OnLogicalScrollBarValueChanged;
        FragmentEditor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrollOffsetChanged;
    }

    /// <summary>The AvaloniaEdit instance used to render the currently loaded fragment.</summary>
    public TextEditor Editor => FragmentEditor;

    /// <summary>Zero-based logical line number represented by the first line in <see cref="Editor"/>.</summary>
    public long FirstFragmentLine => _firstFragmentLine;

    /// <summary>Whether the view is currently following the newest log data.</summary>
    public bool IsFollowingTail => _followingTail;

    /// <summary>Number of actual log records retained by the active document fragment.</summary>
    public int RetainedLineCount => GetActualLineCount(FragmentEditor.Document);

    /// <summary>Total number of logical lines in the complete log, exact or estimated.</summary>
    public long TotalLineCount
    {
        get => _totalLineCount;
        set => _totalLineCount = Math.Max(0, value);
    }

    /// <summary>Maximum byte offset represented by the outer scrollbar.</summary>
    public long ScrollMaximum
    {
        get => _scrollMaximum;
        set
        {
            var currentOffset = (long)Math.Round(LogicalScrollBar.Value);
            _scrollMaximum = Math.Max(0, value);
            LogicalScrollBar.Maximum = _scrollMaximum;
            SetScrollPosition(currentOffset);
        }
    }

    /// <summary>
    /// Raised when the user requests a logical line that is outside the currently loaded fragment.
    /// Load a fragment around <see cref="LogicalScrollRequestedEventArgs.Line"/> and call
    /// <see cref="SetFragment"/>.
    /// </summary>
    public event EventHandler<LogicalScrollRequestedEventArgs>? LogicalScrollRequested;

    /// <summary>Installs a UI-thread-owned document fragment and its logical/byte coordinates.</summary>
    public void SetFragment(
        TextDocument document,
        long firstLogicalLine,
        long firstByteOffset = 0,
        long endByteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(document);

        _firstFragmentLine = Math.Max(0, firstLogicalLine);
        _firstFragmentByteOffset = Math.Max(0, firstByteOffset);
        _fragmentEndByteOffset = Math.Max(_firstFragmentByteOffset, endByteOffset);
        _lineNumberMargin.LineNumberOffset = _firstFragmentLine;
        _followingTail = false;
        FragmentEditor.Document = document;
        SetScrollPosition(_firstFragmentByteOffset);
    }

    /// <summary>Enables buffered tail following for the currently loaded tail fragment.</summary>
    public void StartTailFollowing()
    {
        _followingTail = true;

        // The text view needs a completed layout before its viewport and line heights are valid.
        var textView = FragmentEditor.TextArea.TextView;
        if (((IScrollable)textView).Viewport.Height > 0 && textView.VisualLinesValid)
        {
            ScheduleTailPosition(initialPosition: true);
            return;
        }

        _initialTailPositionPending = true;
        FragmentEditor.LayoutUpdated -= OnInitialTailLayoutUpdated;
        FragmentEditor.LayoutUpdated += OnInitialTailLayoutUpdated;
        textView.VisualLinesChanged -= OnInitialTailVisualLinesChanged;
        textView.VisualLinesChanged += OnInitialTailVisualLinesChanged;
    }

    private void OnInitialTailLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_initialTailPositionPending || !_followingTail)
        {
            return;
        }

        var textView = FragmentEditor.TextArea.TextView;
        if (((IScrollable)textView).Viewport.Height <= 0 || !textView.VisualLinesValid)
        {
            return;
        }

        PositionInitialTailAfterLayout();
    }

    private void OnInitialTailVisualLinesChanged(object? sender, EventArgs e)
    {
        if (!_initialTailPositionPending || !_followingTail)
        {
            return;
        }

        if (((IScrollable)FragmentEditor.TextArea.TextView).Viewport.Height <= 0)
        {
            return;
        }

        PositionInitialTailAfterLayout();
    }

    private void PositionInitialTailAfterLayout()
    {
        _initialTailPositionPending = false;
        FragmentEditor.LayoutUpdated -= OnInitialTailLayoutUpdated;
        FragmentEditor.TextArea.TextView.VisualLinesChanged -= OnInitialTailVisualLinesChanged;
        ScheduleTailPosition(initialPosition: true);
    }

    /// <summary>Stops automatic movement while the user reads historical content.</summary>
    public void StopTailFollowing()
    {
        _followingTail = false;
        _initialTailPositionPending = false;
        FragmentEditor.LayoutUpdated -= OnInitialTailLayoutUpdated;
        FragmentEditor.TextArea.TextView.VisualLinesChanged -= OnInitialTailVisualLinesChanged;
    }

    /// <summary>Shows or hides the indeterminate indicator used while a historical fragment loads.</summary>
    public void SetFragmentLoading(bool isLoading)
    {
        LoadingIndicator.IsVisible = isLoading;
        LogicalScrollBar.Classes.Set("loading", isLoading);
    }

    /// <summary>Updates the byte range represented by the currently installed document fragment.</summary>
    public void UpdateFragmentByteRange(long firstByteOffset, long endByteOffset)
    {
        _firstFragmentByteOffset = Math.Max(0, firstByteOffset);
        _fragmentEndByteOffset = Math.Max(_firstFragmentByteOffset, endByteOffset);
    }

    /// <summary>
    /// Removes the oldest records from the active document, retaining at most
    /// <paramref name="maximumLineCount"/>. This is intended for tail-follow mode.
    /// </summary>
    public bool TrimHeadToLineCount(int maximumLineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCount);

        var document = FragmentEditor.Document;
        var retainedLineCount = GetActualLineCount(document);
        if (retainedLineCount <= maximumLineCount)
        {
            return false;
        }

        var linesToRemove = retainedLineCount - maximumLineCount;
        var firstRetainedDocumentLine = document.GetLineByNumber(linesToRemove + 1);
        var charactersToRemove = firstRetainedDocumentLine.Offset;
        var removedText = document.GetText(0, charactersToRemove);

        var textView = FragmentEditor.TextArea.TextView;
        var oldVerticalOffset = textView.VerticalOffset;
        var removedVisualHeight = textView.VisualLinesValid
            ? textView.GetVisualTopByDocumentLine(linesToRemove + 1)
            : 0;

        document.Remove(0, charactersToRemove);
        _firstFragmentLine += linesToRemove;
        _firstFragmentByteOffset += Encoding.UTF8.GetByteCount(removedText);
        _fragmentEndByteOffset = Math.Max(_fragmentEndByteOffset, _firstFragmentByteOffset);
        _lineNumberMargin.LineNumberOffset = _firstFragmentLine;

        if (_followingTail)
        {
            ScheduleTailPosition(initialPosition: false);
        }
        else
        {
            Dispatcher.UIThread.Post(
                () => ScrollEditorToVerticalOffset(Math.Max(0, oldVerticalOffset - removedVisualHeight)),
                DispatcherPriority.Background);
        }

        return true;
    }

    /// <summary>Extends the byte range after tail data is appended to the active fragment.</summary>
    public void ExtendFragmentEndByteOffset(long endByteOffset)
    {
        _fragmentEndByteOffset = Math.Max(_fragmentEndByteOffset, endByteOffset);
    }

    /// <summary>Scrolls the loaded editor fragment to an approximate byte offset.</summary>
    public void ScrollToByteOffset(long byteOffset)
    {
        var byteLength = _fragmentEndByteOffset - _firstFragmentByteOffset;
        if (byteLength <= 0 || byteOffset < _firstFragmentByteOffset || byteOffset > _fragmentEndByteOffset)
        {
            return;
        }

        var lineCount = FragmentEditor.Document.LineCount;
        var fraction = (byteOffset - _firstFragmentByteOffset) / (double)byteLength;
        var localLine = Math.Clamp((int)Math.Round(fraction * Math.Max(0, lineCount - 1)), 0, lineCount - 1);
        SetScrollPosition(byteOffset);
        FragmentEditor.ScrollToLine(localLine + 1);
    }

    /// <summary>
    /// Appends detected log text. When the current view is at its bottom, it follows the new
    /// content; when the user has scrolled away, their current position is left unchanged.
    /// </summary>
    public void AppendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        if (_followingTail && FragmentEditor.TextArea.TextView.VerticalOffset + 2 < _tailScrollOffset)
        {
            // The user moved upward from the buffered tail position.
            _followingTail = false;
        }

        var document = FragmentEditor.Document;
        document.Insert(document.TextLength, text);
        document.UndoStack.ClearAll();

        TotalLineCount = Math.Max(TotalLineCount, _firstFragmentLine + document.LineCount);
        if (_followingTail)
        {
            ScheduleTailPosition(initialPosition: false);
        }
    }

    private void ScheduleTailPosition(bool initialPosition)
    {
        if (_tailLayoutPending)
        {
            return;
        }

        _tailLayoutPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tailLayoutPending = false;
            if (!_followingTail)
            {
                return;
            }

            var textView = FragmentEditor.TextArea.TextView;
            var scrollable = (IScrollable)textView;
            var viewportHeight = scrollable.Viewport.Height;
            if (viewportHeight <= 0 || FragmentEditor.Document.LineCount == 0)
            {
                return;
            }

            var finalLineTop = textView.GetVisualTopByDocumentLine(FragmentEditor.Document.LineCount);
            var documentBottom = finalLineTop + textView.DefaultLineHeight;
            var currentOffset = scrollable.Offset.Y;
            double targetOffset;

            if (initialPosition)
            {
                // Leave roughly the lower third blank so incoming lines can fill it smoothly.
                targetOffset = documentBottom - viewportHeight * (2.0 / 3.0);
            }
            else if (documentBottom - currentOffset >= viewportHeight)
            {
                // Once the buffered third is full, advance by two thirds of a viewport.
                targetOffset = currentOffset + viewportHeight * (2.0 / 3.0);
            }
            else
            {
                return;
            }

            _tailScrollOffset = Math.Max(0, targetOffset);
            ScrollEditorToVerticalOffset(_tailScrollOffset);
        }, DispatcherPriority.Background);
    }

    private static int GetActualLineCount(TextDocument document)
    {
        if (document.TextLength == 0)
        {
            return 0;
        }

        var hasTrailingNewline = document.GetText(document.TextLength - 1, 1) == "\n";
        return document.LineCount - (hasTrailingNewline ? 1 : 0);
    }

    private void ScrollEditorToVerticalOffset(double verticalOffset)
    {
        // AvaloniaEdit 12.0.0's TextEditor.ScrollToVerticalOffset only applies its template;
        // set the underlying IScrollable offset directly to perform the actual movement.
        var scrollable = (IScrollable)FragmentEditor.TextArea;
        var currentOffset = scrollable.Offset;
        scrollable.Offset = new Vector(currentOffset.X, verticalOffset);
    }

    private void ReplaceLineNumberMargin()
    {
        var margins = FragmentEditor.TextArea.LeftMargins;
        for (var index = 0; index < margins.Count; index++)
        {
            if (margins[index] is not LineNumberMargin existingMargin)
            {
                continue;
            }

            _lineNumberMargin.SetValue(
                TextBlock.ForegroundProperty,
                existingMargin.GetValue(TextBlock.ForegroundProperty));
            margins[index] = _lineNumberMargin;
            return;
        }
    }

    private void OnEditorScrollOffsetChanged(object? sender, EventArgs e)
    {
        var textView = FragmentEditor.TextArea.TextView;
        var firstVisualLine = textView.GetVisualLineFromVisualTop(textView.VerticalOffset);
        if (firstVisualLine is null)
        {
            return;
        }

        // The editor scrolls in lines/pixels, while the outer scrollbar represents file bytes.
        // Within a small fragment, map visible lines proportionally across that byte range.
        var documentLineCount = Math.Max(1, FragmentEditor.Document.LineCount);
        var firstVisibleLine = firstVisualLine.FirstDocumentLine.LineNumber - 1;
        var fragmentByteLength = _fragmentEndByteOffset - _firstFragmentByteOffset;
        var fraction = firstVisibleLine / (double)Math.Max(1, documentLineCount - 1);
        SetScrollPosition(_firstFragmentByteOffset + (long)Math.Round(fragmentByteLength * fraction));

        var lastVisualLine = textView.GetVisualLineFromVisualTop(
            textView.VerticalOffset + ((IScrollable)textView).Viewport.Height - 1);
        if (lastVisualLine is not null)
        {
            var visibleLineCount = lastVisualLine.LastDocumentLine.LineNumber - firstVisualLine.FirstDocumentLine.LineNumber + 1;
            LogicalScrollBar.ViewportSize = Math.Max(1, fragmentByteLength * visibleLineCount / documentLineCount);
        }
    }

    private void OnLogicalScrollBarValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScrollBar)
        {
            return;
        }

        var requestedOffset = Math.Clamp((long)Math.Round(e.NewValue), 0, _scrollMaximum);
        StopTailFollowing();
        if (requestedOffset >= _firstFragmentByteOffset && requestedOffset <= _fragmentEndByteOffset)
        {
            ScrollToByteOffset(requestedOffset);
            return;
        }

        LogicalScrollRequested?.Invoke(this, new LogicalScrollRequestedEventArgs(requestedOffset));
    }

    private void SetScrollPosition(long byteOffset)
    {
        _updatingScrollBar = true;
        try
        {
            LogicalScrollBar.Value = Math.Clamp(byteOffset, 0, _scrollMaximum);
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }
}

public sealed class LogicalScrollRequestedEventArgs(long offset) : EventArgs
{
    /// <summary>Requested byte offset in the log file.</summary>
    public long Offset { get; } = offset;
}
