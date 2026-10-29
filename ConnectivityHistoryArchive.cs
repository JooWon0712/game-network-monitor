using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PcInternetMonitor
{
    // Read-only, bounded loading of previous session incident versions.
    public static class ConnectivityHistoryArchive
    {
        private const int MaximumFiles = 50, MaximumBytes = 2 * 1024 * 1024, MaximumRows = 4096, MaximumIncidents = 500;
        private const int MaximumHeaderBytes = 4096, MaximumRecordCharacters = 65536;

        public static IList<ConnectivityIncident> LoadRecent(string logRoot, int limit)
        {
            List<ConnectivityIncident> empty = new List<ConnectivityIncident>();
            if (String.IsNullOrWhiteSpace(logRoot) || limit <= 0) return empty;
            int count = Math.Min(limit, MaximumIncidents);
            string root;
            try { root = Path.GetFullPath(logRoot); if (!Directory.Exists(root)) return empty; }
            catch (Exception error) { if (ExpectedIo(error)) return empty; throw; }
            List<FileInfo> files = new List<FileInfo>();
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if (!Child(root, directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                        string path = Path.Combine(directory, "connectivity-incidents.csv");
                        if (!Child(root, path) || !File.Exists(path)) continue;
                        FileInfo file = new FileInfo(path);
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        files.Add(file);
                        files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                        if (files.Count > MaximumFiles) files.RemoveAt(files.Count - 1);
                    }
                    catch (Exception error) { if (!ExpectedIo(error)) throw; }
                }
            }
            catch (Exception error) { if (!ExpectedIo(error)) throw; }
            Dictionary<string, ConnectivityIncident> incidents = new Dictionary<string, ConnectivityIncident>(StringComparer.Ordinal);
            DateTime oldest = DateTime.MinValue;
            foreach (FileInfo file in files)
            {
                try
                {
                    string text, header;
                    using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        header = ReadHeader(stream);
                        if (header != AutomaticSessionLog.ConnectivityIncidentHeader) continue;
                        long end = stream.Length;
                        // The file may be large or still growing. Only its header and
                        // the last bounded byte window as of this open are read.
                        stream.Position = Math.Max(stream.Position, end - MaximumBytes);
                        int wanted = (int)Math.Min(MaximumBytes, Math.Max(0, end - stream.Position));
                        byte[] bytes = new byte[wanted]; int read = 0, got;
                        while (read < wanted && (got = stream.Read(bytes, read, wanted - read)) > 0) read += got;
                        text = DecodeTail(bytes, read);
                    }
                    Dictionary<string, int> columns = Columns(header.Split(','));
                    // A byte tail can begin in an UTF-8 sequence or a quoted CSV
                    // field. Resynchronize at a GUID/version record prefix, parse
                    // complete quoted records, and retain the most recent records.
                    foreach (ConnectivityIncident item in RecentRecords(text, columns))
                    {
                        ConnectivityIncident previous;
                        if (incidents.TryGetValue(item.Id, out previous))
                        { if (item.Version > previous.Version) incidents[item.Id] = item; continue; }
                        if (incidents.Count >= count)
                        {
                            if (item.StartedUtc <= oldest) continue;
                            ConnectivityIncident remove = incidents.Values.OrderBy(x => x.StartedUtc).First(); incidents.Remove(remove.Id);
                        }
                        incidents.Add(item.Id, item);
                        oldest = incidents.Values.Min(x => x.StartedUtc);
                    }
                }
                catch (Exception error) { if (!ExpectedIo(error) && !(error is DecoderFallbackException) && !(error is FormatException)) throw; }
            }
            foreach (ConnectivityIncident incident in incidents.Values)
                if (incident.State == "open")
                {
                    incident.State = "ended"; incident.ClosedUtc = incident.LastObservedUtc;
                    incident.EndedUtc = incident.RecoveredUtc = null;
                    incident.EndReason = "이전 실행의 관측 기록 · 회복 미확인";
                    incident.Summary = (incident.Summary ?? "검사 실패") + " · 이전 관측 종료/회복 미확인";
                }
            return incidents.Values.OrderByDescending(x => x.StartedUtc).ToList();
        }

        private static string ReadHeader(FileStream stream)
        {
            List<byte> bytes = new List<byte>();
            for (int i = 0; i < MaximumHeaderBytes; i++)
            {
                int value = stream.ReadByte();
                if (value < 0) return "";
                if (value == '\n') return new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');
                bytes.Add((byte)value);
            }
            return "";
        }

        private static string DecodeTail(byte[] bytes, int count)
        {
            int start = 0;
            while (start < count && (bytes[start] & 0xc0) == 0x80) start++;
            if (start == count) return "";
            // A live writer can leave the final character incomplete in this read.
            int sequence = count - 1;
            while (sequence > start && (bytes[sequence] & 0xc0) == 0x80) sequence--;
            byte first = bytes[sequence];
            int width = first < 0x80 ? 1 : (first & 0xe0) == 0xc0 ? 2 : (first & 0xf0) == 0xe0 ? 3 : (first & 0xf8) == 0xf0 ? 4 : 1;
            if (count - sequence < width) count = sequence;
            return count <= start ? "" : new UTF8Encoding(false, true).GetString(bytes, start, count - start);
        }

        private static IList<ConnectivityIncident> RecentRecords(string text, Dictionary<string, int> columns)
        {
            Queue<ConnectivityIncident> records = new Queue<ConnectivityIncident>();
            int at = FindRecordStart(text, 0);
            while (at >= 0)
            {
                string[] row; int next;
                if (ReadRecord(text, at, out row, out next))
                {
                    ConnectivityIncident item = Parse(row, columns);
                    if (item != null)
                    {
                        if (records.Count >= MaximumRows) records.Dequeue();
                        records.Enqueue(item);
                    }
                    at = FindRecordStart(text, next);
                }
                else at = FindRecordStart(text, at + 1);
            }
            return records.ToList();
        }

        private static int FindRecordStart(string text, int from)
        {
            for (int i = Math.Max(0, from); i < text.Length; i++)
            {
                if (i > 0 && text[i - 1] != '\n' && text[i - 1] != '\r') continue;
                int at = i; bool quoted = text[at] == '"'; if (quoted) at++;
                if (at + 34 >= text.Length) continue;
                int guid = at;
                while (at < guid + 32 && Hex(text[at])) at++;
                if (at != guid + 32) continue;
                if (quoted) { if (text[at] != '"') continue; at++; }
                if (text[at++] != ',') continue;
                int version = at;
                while (at < text.Length && at - version < 20 && text[at] >= '0' && text[at] <= '9') at++;
                if (at > version && at < text.Length && text[at] == ',') return i;
            }
            return -1;
        }
        private static bool Hex(char ch) { return ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f' || ch >= 'A' && ch <= 'F'; }

        private static bool ReadRecord(string text, int at, out string[] row, out int next)
        {
            row = null; next = at + 1;
            List<string> fields = new List<string>(); StringBuilder value = new StringBuilder();
            bool quoted = false, quoteClosed = false;
            int until = Math.Min(text.Length, at + MaximumRecordCharacters);
            for (int i = at; i < until; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; } else { quoted = false; quoteClosed = true; } }
                    else value.Append(ch);
                }
                else if (ch == '"') { if (value.Length != 0 || quoteClosed) return false; quoted = true; }
                else if (ch == ',') { fields.Add(value.ToString()); value.Length = 0; quoteClosed = false; if (fields.Count >= 15) return false; }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    fields.Add(value.ToString()); row = fields.ToArray(); next = i + 1; return fields.Count == 15;
                }
                else { if (quoteClosed) return false; value.Append(ch); }
            }
            // Partial final records are never used to infer a recovery.
            return false;
        }

        private static bool ExpectedIo(Exception error) { return error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException; }
        private static bool Child(string parent, string path) { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        private static Dictionary<string, int> Columns(string[] names)
        {
            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++) if (!columns.ContainsKey(names[i])) columns.Add(names[i], i);
            return columns;
        }
        private static string Field(string[] row, Dictionary<string, int> columns, string name)
        { int at; return columns.TryGetValue(name, out at) && at < row.Length && row[at].Length <= 8192 ? row[at] : ""; }
        private static ConnectivityIncident Parse(string[] row, Dictionary<string, int> columns)
        {
            string id = Field(row, columns, "id"), state = Field(row, columns, "state");
            Guid guid; long version, failures; DateTime started, last;
            if (!Guid.TryParseExact(id, "N", out guid) || !Int64.TryParse(Field(row, columns, "version"), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version <= 0 ||
                !Int64.TryParse(Field(row, columns, "failed_checks"), NumberStyles.None, CultureInfo.InvariantCulture, out failures) || failures < 1 ||
                !Date(Field(row, columns, "first_failed_check_utc"), out started) || !Date(Field(row, columns, "last_observed_utc"), out last) || last < started ||
                (state != "open" && state != "recovered" && state != "ended")) return null;
            string category = Field(row, columns, "category"), kind = Field(row, columns, "kind");
            if ((category != "failure" && category != "check_error") || String.IsNullOrWhiteSpace(kind)) return null;
            string recoveredText = Field(row, columns, "recovered_utc"), closedText = Field(row, columns, "observation_closed_utc");
            DateTime? recovered = NullableDate(recoveredText), closed = NullableDate(closedText);
            if (recoveredText.Length > 0 && !recovered.HasValue || closedText.Length > 0 && !closed.HasValue) return null;
            if (state == "recovered" && (!recovered.HasValue || recovered.Value != last || closed.HasValue) ||
                state == "ended" && (!closed.HasValue || closed.Value < last || recovered.HasValue) || state == "open" && (recovered.HasValue || closed.HasValue)) return null;
            return new ConnectivityIncident {
                Id = id, Version = version, Kind = kind, Target = Field(row, columns, "target"), Category = category, Scope = Field(row, columns, "scope"), State = state,
                StartedUtc = started, LastObservedUtc = last, EndedUtc = recovered, RecoveredUtc = recovered, ClosedUtc = closed,
                Failures = failures, Summary = Field(row, columns, "summary"), EndReason = Field(row, columns, "end_reason")
            };
        }
        private static bool Date(string value, out DateTime utc)
        {
            DateTime parsed; utc = DateTime.MinValue;
            if (value.Length == 0 || !(value.EndsWith("Z", StringComparison.Ordinal) || value.Length >= 6 && (value[value.Length - 6] == '+' || value[value.Length - 6] == '-')) ||
                !DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) return false;
            utc = parsed.ToUniversalTime();
            return utc > DateTime.MinValue && utc <= DateTime.UtcNow.AddMinutes(1);
        }
        private static DateTime? NullableDate(string value) { DateTime parsed; return Date(value, out parsed) ? parsed : (DateTime?)null; }

        internal static IList<string[]> ParseCsv(string text, int maximumRecords)
        {
            List<string[]> records = new List<string[]>(); List<string> fields = new List<string>(); StringBuilder value = new StringBuilder();
            bool quoted = false, quoteClosed = false;
            for (int i = 0; i < text.Length && records.Count < maximumRecords; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; } else { quoted = false; quoteClosed = true; } }
                    else value.Append(ch);
                }
                else if (ch == '"') { if (value.Length != 0 || quoteClosed) throw new FormatException("Invalid CSV quote."); quoted = true; }
                else if (ch == ',') { fields.Add(value.ToString()); value.Length = 0; quoteClosed = false; }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    fields.Add(value.ToString()); value.Length = 0; quoteClosed = false; records.Add(fields.ToArray()); fields.Clear();
                }
                else { if (quoteClosed) throw new FormatException("Unexpected data after CSV quote."); value.Append(ch); }
            }
            // An incomplete trailing record belongs to an active write and is ignored.
            if (!quoted && records.Count < maximumRecords && (fields.Count > 0 || value.Length > 0)) { fields.Add(value.ToString()); records.Add(fields.ToArray()); }
            return records;
        }
    }
}
