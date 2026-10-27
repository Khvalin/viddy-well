using System;
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

    public LogView()
    {
        InitializeComponent();
        FragmentEditor.Document = new TextDocument();
        ReplaceLineNumberMargin();
        LogicalScrollBar.ValueChanged += OnLogicalScrollBarValueChanged;
        FragmentEditor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrollOffsetChanged;
    }

    /// <summary>The AvaloniaEdit instance used to render the currently loaded fragment.</summary>
    public TextEditor Editor => FragmentEditor;

    /// <summary>Zero-based logical line number represented by the first line in <see cref="Editor"/>.</summary>
    public long FirstFragmentLine => _firstFragmentLine;

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
        FragmentEditor.Document = document;
        SetScrollPosition(_firstFragmentByteOffset);
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

        var followTail = IsScrolledToBottom();
        var document = FragmentEditor.Document;
        document.Insert(document.TextLength, text);
        document.UndoStack.ClearAll();

        TotalLineCount = Math.Max(TotalLineCount, _firstFragmentLine + document.LineCount);
        if (!followTail)
        {
            return;
        }

        SetScrollPosition(_fragmentEndByteOffset);

        // The text view's extent is updated during layout, so scroll after that layout pass.
        Dispatcher.UIThread.Post(FragmentEditor.ScrollToEnd, DispatcherPriority.Render);
    }

    private bool IsScrolledToBottom()
    {
        var scrollable = (IScrollable)FragmentEditor.TextArea.TextView;
        const double tolerance = 1.0;
        return scrollable.Offset.Y + scrollable.Viewport.Height >= scrollable.Extent.Height - tolerance;
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
