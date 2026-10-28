using System;
using System.Diagnostics;

namespace viddy_well;

public static class Diagnostics
{
    private static readonly TraceSource Source = CreateTraceSource();

    public static void Info(string message)
    {
        Write(TraceEventType.Information, message);
    }

    public static void Debug(string message)
    {
        Write(TraceEventType.Verbose, message);
    }

    private static TraceSource CreateTraceSource()
    {
        var source = new TraceSource("viddy-well", SourceLevels.Verbose);
        source.Listeners.Clear();
        source.Listeners.Add(new ConsoleTraceListener(useErrorStream: false));
        return source;
    }

    private static void Write(TraceEventType eventType, string message)
    {
        Source.TraceEvent(eventType, 0, $"{DateTimeOffset.Now:O} {message}");
        Source.Flush();
    }
}
