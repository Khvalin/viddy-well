using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace viddy_well;

/// <summary>
/// Displays one loaded fragment of a larger, line-oriented log. Its scrollbar represents
/// logical lines in the complete log, not only the lines held by <see cref="Editor"/>.
/// </summary>
public partial class LogView : UserControl
{
    private bool _updatingScrollBar;
    private long _totalLineCount;
    private long _firstFragmentLine;

    public LogView()
    {
        InitializeComponent();
        FragmentEditor.Document = new TextDocument();
        LogicalScrollBar.ValueChanged += OnLogicalScrollBarValueChanged;
        FragmentEditor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrollOffsetChanged;
    }

    /// <summary>The AvaloniaEdit instance used to render the currently loaded fragment.</summary>
    public TextEditor Editor => FragmentEditor;

    /// <summary>Zero-based logical line number represented by the first line in <see cref="Editor"/>.</summary>
    public long FirstFragmentLine => _firstFragmentLine;

    /// <summary>
    /// Total number of logical lines in the complete log. This determines the outer scrollbar range.
    /// </summary>
    public long TotalLineCount
    {
        get => _totalLineCount;
        set
        {
            var currentLogicalLine = (long)Math.Round(LogicalScrollBar.Value);
            _totalLineCount = Math.Max(0, value);
            LogicalScrollBar.Maximum = Math.Max(0, _totalLineCount - 1);
            // Changing the range must not move an actively reading user back to the fragment start.
            SetLogicalScrollPosition(currentLogicalLine);
        }
    }

    /// <summary>
    /// Raised when the user requests a logical line that is outside the currently loaded fragment.
    /// Load a fragment around <see cref="LogicalScrollRequestedEventArgs.Line"/> and call
    /// <see cref="SetFragment"/>.
    /// </summary>
    public event EventHandler<LogicalScrollRequestedEventArgs>? LogicalScrollRequested;

    /// <summary>Installs a UI-thread-owned document fragment at its zero-based logical line.</summary>
    public void SetFragment(TextDocument document, long firstLogicalLine)
    {
        ArgumentNullException.ThrowIfNull(document);

        _firstFragmentLine = Math.Max(0, firstLogicalLine);
        FragmentEditor.Document = document;
        SetLogicalScrollPosition(_firstFragmentLine);
    }

    /// <summary>
    /// Appends detected log text. When the current view is at its bottom, it follows the new
    /// content; when the user has scrolled away, their current position is left unchanged.
    /// </summary>
    public void AppendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;

        var followTail = IsScrolledToBottom();
        var document = FragmentEditor.Document;
        document.Insert(document.TextLength, text);
        document.UndoStack.ClearAll();

        TotalLineCount = Math.Max(TotalLineCount, _firstFragmentLine + document.LineCount);
        if (!followTail)
            return;

        var finalLogicalLine = _firstFragmentLine + document.LineCount - 1;
        SetLogicalScrollPosition(finalLogicalLine);

        // The text view's extent is updated during layout, so scroll after that layout pass.
        Dispatcher.UIThread.Post(FragmentEditor.ScrollToEnd, DispatcherPriority.Render);
    }

    private bool IsScrolledToBottom()
    {
        var scrollable = (IScrollable)FragmentEditor.TextArea.TextView;
        const double tolerance = 1.0;
        return scrollable.Offset.Y + scrollable.Viewport.Height >= scrollable.Extent.Height - tolerance;
    }

    private void OnEditorScrollOffsetChanged(object? sender, EventArgs e)
    {
        var textView = FragmentEditor.TextArea.TextView;
        var firstVisualLine = textView.GetVisualLineFromVisualTop(textView.VerticalOffset);
        if (firstVisualLine is null)
            return;

        // The editor scrolls in pixels, but the outer log scrollbar scrolls in logical lines.
        // Map the first displayed document line back into the complete-log coordinate system.
        var firstVisibleLine = firstVisualLine.FirstDocumentLine.LineNumber - 1;
        SetLogicalScrollPosition(_firstFragmentLine + firstVisibleLine);

        var lastVisualLine = textView.GetVisualLineFromVisualTop(
            textView.VerticalOffset + ((IScrollable)textView).Viewport.Height - 1);
        if (lastVisualLine is not null)
        {
            var visibleLineCount = lastVisualLine.LastDocumentLine.LineNumber - firstVisualLine.FirstDocumentLine.LineNumber + 1;
            LogicalScrollBar.ViewportSize = Math.Max(1, visibleLineCount);
        }
    }

    private void OnLogicalScrollBarValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScrollBar)
            return;

        var requestedLine = Math.Clamp((long)Math.Round(e.NewValue), 0, Math.Max(0, _totalLineCount - 1));
        var localLine = requestedLine - _firstFragmentLine;

        if (localLine >= 0 && localLine < FragmentEditor.Document.LineCount)
        {
            FragmentEditor.ScrollToLine((int)localLine + 1);
            return;
        }

        LogicalScrollRequested?.Invoke(this, new LogicalScrollRequestedEventArgs(requestedLine));
    }

    private void SetLogicalScrollPosition(long logicalLine)
    {
        _updatingScrollBar = true;
        try
        {
            LogicalScrollBar.Value = Math.Clamp(logicalLine, 0, Math.Max(0, _totalLineCount - 1));
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }
}

public sealed class LogicalScrollRequestedEventArgs(long line) : EventArgs
{
    public long Line { get; } = line;
}
