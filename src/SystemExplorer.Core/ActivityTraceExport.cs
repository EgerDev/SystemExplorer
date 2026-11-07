using System.Globalization;
using System.Text.Json;

namespace SystemExplorer.Core;

public static class TraceExport
{
    public const string Limitations = "Read-only ETW observations; no command lines or registry value data. Events may be lost, overwritten or unresolved. Seeded processes started before trace; parent not observed means no verified parent instance. Summaries describe retained events; capped distinct sets report untracked observations, not exact additional distinct counts.";
    private static string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
    private static string Number<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
    public static void WriteCsv(TextWriter writer, TraceResult result, TraceFilter? filter = null)
    {
        filter ??= new();
        void Row(params string?[] cells) => writer.Write(string.Join(",", cells.Select(Cell)) + "\r\n");
        Row("RecordType", "TraceStatus", "EventsLost", "BufferDropped", "Malformed", "UnresolvedNames", "CapsHit", "FilterProcessIndex", "FilterIncludeDescendants", "FilterCategories", "FilterText", "FilterFromUtc", "FilterToUtc", "Limitations",
            "Sequence", "TimeUtc", "RelativeMs", "Category", "Operation", "PID", "TID", "Process", "ParentPID", "Path", "Bytes", "Detail");
        Row(new string?[] { "Capture", result.Status.ToString(), Number(result.EventsLost), Number(result.BufferDropped), Number(result.Malformed), Number(result.UnresolvedNames), result.CapsHit.ToString(),
            filter.ProcessIndex?.ToString(CultureInfo.InvariantCulture), filter.IncludeDescendants.ToString(), filter.Categories.ToString(), filter.Text, filter.FromUtc?.ToString("O"), filter.ToUtc?.ToString("O"), Limitations }.Concat(new string?[12]).ToArray());
        foreach (int position in filter.Match(result.Events, result.Processes))
        {
            var e = result.Events[position]; var p = Process(result, e);
            Row(new string?[] { "Event" }.Concat(new string?[13]).Concat(new[] { Number(e.Sequence), new DateTimeOffset(e.TimestampUtc, TimeSpan.Zero).ToString("O"), Number((e.TimestampUtc - result.StartedUtc.UtcTicks) / (double)TimeSpan.TicksPerMillisecond),
                TraceEvent.Category(e.Operation).ToString(), e.Operation.ToString(), Number(e.Pid), Number(e.Tid), p?.ImageName, p is null ? "" : Number(p.ParentPid), e.Path, Number(e.Bytes), e.Detail }).ToArray());
        }
    }
    private static TraceProcess? Process(TraceResult result, TraceEvent e) => e.ProcessIndex >= 0 && e.ProcessIndex < result.Processes.Count ? result.Processes[e.ProcessIndex] : null;
    public static void WriteJson(Stream stream, TraceResult result, TraceFilter? filter = null)
    {
        filter ??= new();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("status", result.Status.ToString()); writer.WriteString("detail", result.Detail);
        writer.WriteString("startedUtc", result.StartedUtc); writer.WriteString("stoppedUtc", result.StoppedUtc);
        writer.WriteNumber("eventsLost", result.EventsLost); writer.WriteNumber("bufferDropped", result.BufferDropped);
        writer.WriteNumber("malformed", result.Malformed); writer.WriteNumber("unresolvedNames", result.UnresolvedNames);
        writer.WriteBoolean("processTableTruncated", result.ProcessTableTruncated); writer.WriteBoolean("capsHit", result.CapsHit);
        writer.WriteNumber("eventsSeen", result.EventsSeen); writer.WriteNumber("excludedSelf", result.ExcludedSelf);
        writer.WritePropertyName("options"); writer.WriteStartObject(); writer.WriteString("categories", result.Options.Categories.ToString());
        writer.WriteNumber("durationSeconds", result.Options.Duration.TotalSeconds); writer.WriteNumber("eventCap", result.Options.EventCap); writer.WriteBoolean("excludeSelf", result.Options.ExcludeSelf); writer.WriteEndObject();
        writer.WritePropertyName("filter"); writer.WriteStartObject();
        if (filter.ProcessIndex is int index) writer.WriteNumber("processIndex", index); else writer.WriteNull("processIndex");
        writer.WriteBoolean("includeDescendants", filter.IncludeDescendants); writer.WriteString("categories", filter.Categories.ToString()); writer.WriteString("text", filter.Text);
        if (filter.FromUtc is { } from) writer.WriteString("fromUtc", from); else writer.WriteNull("fromUtc");
        if (filter.ToUtc is { } to) writer.WriteString("toUtc", to); else writer.WriteNull("toUtc");
        writer.WriteEndObject(); writer.WriteString("limitations", Limitations);
        writer.WritePropertyName("categoryCounts"); writer.WriteStartObject(); foreach (var pair in result.CategoryCounts) writer.WriteNumber(pair.Key.ToString(), pair.Value); writer.WriteEndObject();
        writer.WritePropertyName("processes"); writer.WriteStartArray();
        foreach (var p in result.Processes)
        {
            writer.WriteStartObject(); writer.WriteNumber("index", p.Index); writer.WriteNumber("pid", p.Pid); writer.WriteNumber("parentPid", p.ParentPid); writer.WriteNumber("parentIndex", p.ParentIndex);
            writer.WriteString("imageName", p.ImageName); writer.WriteString("imagePath", p.ImagePath);
            if (p.CreateTimeFileTime is long created) writer.WriteNumber("createTimeFileTime", created); else writer.WriteNull("createTimeFileTime");
            if (p.StartUtc is { } start) writer.WriteString("startUtc", start); else writer.WriteNull("startUtc");
            if (p.ExitUtc is { } exit) writer.WriteString("exitUtc", exit); else writer.WriteNull("exitUtc");
            if (p.ExitCode is int code) writer.WriteNumber("exitCode", code); else writer.WriteNull("exitCode");
            writer.WriteBoolean("seeded", p.Seeded); writer.WriteNumber("threadsStarted", p.ThreadsStarted); writer.WriteNumber("threadsStopped", p.ThreadsStopped); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WritePropertyName("events"); writer.WriteStartArray();
        foreach (int position in filter.Match(result.Events, result.Processes))
        {
            var e = result.Events[position]; writer.WriteStartObject();
            writer.WriteNumber("sequence", e.Sequence); writer.WriteString("timeUtc", new DateTimeOffset(e.TimestampUtc, TimeSpan.Zero));
            writer.WriteNumber("relativeMs", (e.TimestampUtc - result.StartedUtc.UtcTicks) / (double)TimeSpan.TicksPerMillisecond);
            writer.WriteString("category", TraceEvent.Category(e.Operation).ToString()); writer.WriteString("operation", e.Operation.ToString());
            writer.WriteNumber("pid", e.Pid); writer.WriteNumber("tid", e.Tid); writer.WriteNumber("processIndex", e.ProcessIndex);
            writer.WriteString("path", e.Path); writer.WriteNumber("bytes", e.Bytes); writer.WriteString("detail", e.Detail); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
    }
}
