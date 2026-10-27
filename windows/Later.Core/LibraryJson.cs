using System.Globalization;
using System.Text.Json;

namespace Later.Core;

/// Reads and writes the same JSON the Mac app writes. Hand-written so it needs no reflection and fails exactly where
/// Swift's synthesized Codable would. Dates are seconds since 2001-01-01T00:00:00Z as JSON numbers.
public static class LibraryJson
{
    public static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static DateTime ToDate(double seconds) => ReferenceDate.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    static double FromDate(DateTime d) => (d.ToUniversalTime() - ReferenceDate).Ticks / (double)TimeSpan.TicksPerSecond;

    public static byte[] Encode(LibraryData data)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", data.Version);
            w.WriteStartArray("items");
            foreach (var i in data.Items) WriteItem(w, i);
            w.WriteEndArray();
            w.WriteStartObject("corrections");
            foreach (var (type, c) in data.Corrections)
            {
                w.WriteStartObject(type);
                if (c.IsSpecific is { } s) w.WriteBoolean("isSpecific", s);
                if (c.Kind is { } k) w.WriteString("kind", k.Wire());
                w.WriteEndObject();
            }
            w.WriteEndObject();
            WriteDates(w, "seen", data.Seen);
            w.WriteStartArray("dwellSamples");
            foreach (var s in data.DwellSamples) w.WriteNumberValue(s);
            w.WriteEndArray();
            WriteDates(w, "dismissed", data.Dismissed);
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    static void WriteDates(Utf8JsonWriter w, string name, Dictionary<string, DateTime> map)
    {
        w.WriteStartObject(name);
        foreach (var (hash, when) in map) w.WriteNumber(hash, FromDate(when));
        w.WriteEndObject();
    }

    static void WriteItem(Utf8JsonWriter w, Item i)
    {
        w.WriteStartObject();
        w.WriteString("id", i.Id.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());   // Swift writes UUIDs uppercase
        w.WriteString("url", i.Url); w.WriteString("title", i.Title); w.WriteString("host", i.Host);
        w.WriteString("kind", i.Kind.Wire()); w.WriteString("pageType", i.PageType); w.WriteString("browser", i.Browser.Wire());
        w.WriteNumber("firstSaved", FromDate(i.FirstSaved)); w.WriteNumber("lastSeen", FromDate(i.LastSeen));
        w.WriteNumber("visits", i.Visits);
        w.WriteString("status", i.Status == ItemStatus.Done ? "done" : "active");
        if (i.DoneAt is { } d) w.WriteNumber("doneAt", FromDate(d));
        if (i.OpenedAt is { } o) w.WriteNumber("openedAt", FromDate(o));
        if (i.HistoryVisits is { } hv) w.WriteNumber("historyVisits", hv);
        if (i.TabIndex is { } t) w.WriteNumber("tabIndex", t);
        if (i.WindowRank is { } r) w.WriteNumber("windowRank", r);
        if (i.HistoryChecked is { } hc) w.WriteString("historyChecked", hc);
        w.WriteEndObject();
    }

    public static LibraryData Decode(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("not an object");
            var data = new LibraryData { Version = Required(root, "version").GetInt32() };
            foreach (var e in Required(root, "items").EnumerateArray()) data.Items.Add(ReadItem(e));
            foreach (var p in Required(root, "corrections").EnumerateObject())
            {
                bool? specific = Optional(p.Value, "isSpecific") is { } s ? s.GetBoolean() : null;
                Kind? kind = Optional(p.Value, "kind") is { } k ? KindInfo.FromWire(k.GetString() ?? throw new FormatException("kind")) : null;
                data.Corrections[p.Name] = new Correction(specific, kind);
            }
            foreach (var p in Required(root, "seen").EnumerateObject()) data.Seen[p.Name] = ToDate(p.Value.GetDouble());
            foreach (var e in Required(root, "dwellSamples").EnumerateArray()) data.DwellSamples.Add(e.GetDouble());
            if (Optional(root, "dismissed") is { } dismissed)   // absent in files from before dismissals existed
                foreach (var p in dismissed.EnumerateObject()) data.Dismissed[p.Name] = ToDate(p.Value.GetDouble());
            return data;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new FormatException("The library file can't be read", e);
        }
    }

    static JsonElement Required(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : throw new KeyNotFoundException(name);

    static JsonElement? Optional(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    static Item ReadItem(JsonElement e)
    {
        var browser = BrowserInfo.FromWire(Required(e, "browser").GetString()!) ?? throw new FormatException("browser");
        var status = Required(e, "status").GetString() switch { "active" => ItemStatus.Active, "done" => ItemStatus.Done, _ => throw new FormatException("status") };
        return new Item(
            Id: Guid.ParseExact(Required(e, "id").GetString()!, "D"), Url: Required(e, "url").GetString()!, Title: Required(e, "title").GetString()!,
            Host: Required(e, "host").GetString()!, Kind: KindInfo.FromWire(Required(e, "kind").GetString()!),
            PageType: Required(e, "pageType").GetString()!, Browser: browser,
            FirstSaved: ToDate(Required(e, "firstSaved").GetDouble()), LastSeen: ToDate(Required(e, "lastSeen").GetDouble()),
            Visits: Required(e, "visits").GetInt32(), Status: status,
            DoneAt: Optional(e, "doneAt") is { } d ? ToDate(d.GetDouble()) : null,
            OpenedAt: Optional(e, "openedAt") is { } o ? ToDate(o.GetDouble()) : null,
            HistoryVisits: Optional(e, "historyVisits")?.GetInt32(), TabIndex: Optional(e, "tabIndex")?.GetInt32(),
            WindowRank: Optional(e, "windowRank")?.GetInt32(), HistoryChecked: Optional(e, "historyChecked")?.GetString());
    }
}
