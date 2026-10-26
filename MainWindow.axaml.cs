using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using AvaloniaEdit.Document;

namespace viddy_well;

public partial class MainWindow : Window
{
    private readonly List<OpenLogFile> _openLogs = [];

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
            StatusText.Text = "Pass one or more log-file paths on the command line.";
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

            log.FragmentReceived += (_, fragment) => logView.AppendText(fragment.Text);
            log.ReadFailed += (_, exception) => StatusText.Text = $"Unable to read {log.FileName}: {exception.Message}";

            var fullPath = Path.GetFullPath(fileName);
            var tab = new TabItem
            {
                Header = Path.GetFileName(fullPath),
                Content = new GroupBox
                {
                    Header = fullPath,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    Content = logView,
                },
            };

            LogTabs.Items.Add(tab);
            _openLogs.Add(log);
            StatusText.Text = $"Tailing {_openLogs.Count:N0} log file(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = $"Unable to open {fileName}: {exception.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach (var log in _openLogs)
            log.Dispose();

        _openLogs.Clear();
    }
}
