using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace viddy_well;

/// <summary>Renders document line numbers offset by the first logical line in a loaded log fragment.</summary>
public sealed class OffsetLineNumberMargin : LineNumberMargin
{
    private long _lineNumberOffset;

    /// <summary>Zero-based logical line number corresponding to document line one.</summary>
    public long LineNumberOffset
    {
        get => _lineNumberOffset;
        set
        {
            if (_lineNumberOffset == value)
            {
                return;
            }

            _lineNumberOffset = Math.Max(0, value);
            MinWidthInDigits = Math.Max(
                MinWidthInDigits,
                (_lineNumberOffset + (Document?.LineCount ?? 1)).ToString(CultureInfo.CurrentCulture).Length);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid)
        {
            return;
        }

        var foreground = GetValue(TextBlock.ForegroundProperty);
        var width = Bounds.Width;
        foreach (var visualLine in textView.VisualLines)
        {
            var logicalLineNumber = _lineNumberOffset + visualLine.FirstDocumentLine.LineNumber;
            var formattedText = new FormattedText(
                logicalLineNumber.ToString(CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface,
                EmSize,
                foreground);
            var visualY = visualLine.GetTextLineVisualYPosition(
                visualLine.TextLines[0],
                VisualYPosition.TextTop);
            drawingContext.DrawText(formattedText, new Point(width - formattedText.Width, visualY - textView.VerticalOffset));
        }
    }
}
