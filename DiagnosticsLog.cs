using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PcInternetMonitor
{
    // Low-frequency application/session events only. This is not a sample logger.
    public static class DiagnosticsLog
    {
        private static readonly object gate = new object();
        private static Sink current;
        private static string initializationError = "";

        public static bool Initialize(string directory, string version, string mode)
        {
            lock (gate)
            {
                if (current != null) return current.IsInitialized;
                try
                {
                    current = new Sink(directory, version, mode);
                    bool opened = current.Open();
                    initializationError = opened ? "" : current.ErrorMessage;
                    return opened;
                }
                catch (Exception error)
                {
                    initializationError = FailureText(error);
                    return false;
                }
            }
        }

        public static string FilePath { get { lock (gate) return current == null ? "" : current.FilePath; } }
        public static string ErrorMessage { get { lock (gate) return current == null ? initializationError : current.ErrorMessage; } }
        public static bool IsInitialized { get { lock (gate) return current != null && current.IsInitialized; } }

        public static bool Write(string kind, string details, Exception error = null)
        {
            Sink sink;
            lock (gate)
            {
                sink = current;
                if (sink == null)
                {
                    if (initializationError.Length == 0) initializationError = "진단 기록이 초기화되지 않았습니다.";
                    return false;
                }
            }
            return sink.Write(kind, details, error);
        }

        public static void Shutdown(string reason)
        {
            Sink sink;
            lock (gate) sink = current;
            if (sink != null) sink.Shutdown(reason);
        }

        private sealed class Sink
        {
            private readonly object writeGate = new object();
            private readonly string directory, version, mode, runId = Guid.NewGuid().ToString("N");
            private FileStream stream;
            private StreamWriter writer;
            private string path = "", errorMessage = "";
            private int processId;
            private bool closed;

            internal Sink(string directory, string version, string mode)
            {
                this.directory = directory; this.version = version ?? ""; this.mode = mode ?? "";
            }

            internal string FilePath { get { lock (writeGate) return path; } }
            internal string ErrorMessage { get { lock (writeGate) return errorMessage; } }
            internal bool IsInitialized { get { lock (writeGate) return writer != null && !closed && errorMessage.Length == 0; } }

            internal bool Open()
            {
                lock (writeGate)
                {
                    if (writer != null) return !closed && errorMessage.Length == 0;
                    if (closed) return false;
                    try
                    {
                        string folder = Path.GetFullPath(directory);
                        Directory.CreateDirectory(folder);
                        using (Process process = Process.GetCurrentProcess()) processId = process.Id;
                        path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + processId.ToString(CultureInfo.InvariantCulture) + "-" + runId + ".jsonl");
                        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                        writer = new StreamWriter(stream, new UTF8Encoding(true), 4096);
                        return WriteLocked("app_start", "진단 기록 시작", null);
                    }
                    catch (Exception error) { FailLocked(error); return false; }
                }
            }

            internal bool Write(string kind, string details, Exception error)
            {
                lock (writeGate) return WriteLocked(kind, details, error);
            }

            private bool WriteLocked(string kind, string details, Exception error)
            {
                if (closed || writer == null)
                {
                    if (errorMessage.Length == 0) errorMessage = closed ? "진단 기록이 종료된 뒤 추가 기록을 요청했습니다." : "진단 기록 파일을 사용할 수 없습니다.";
                    return false;
                }
                try
                {
                    DateTime now = DateTime.UtcNow;
                    string exceptionText = "";
                    if (error != null)
                    {
                        try { exceptionText = error.ToString(); }
                        catch (Exception) { exceptionText = error.GetType().FullName + " (예외 상세 문자열을 읽을 수 없음)"; }
                    }
                    string line = "{\"timestamp_utc\":" + Json(now.ToString("o", CultureInfo.InvariantCulture)) +
                        ",\"timestamp_local\":" + Json(now.ToLocalTime().ToString("o", CultureInfo.InvariantCulture)) +
                        ",\"pid\":" + processId.ToString(CultureInfo.InvariantCulture) + ",\"version\":" + Json(version) +
                        ",\"mode\":" + Json(mode) + ",\"run_id\":" + Json(runId) + ",\"kind\":" + Json(kind) +
                        ",\"details\":" + Json(details) + ",\"exception\":" + Json(exceptionText) + "}";
                    writer.WriteLine(line);
                    writer.Flush();
                    stream.Flush(true);
                    return true;
                }
                catch (Exception failure) { FailLocked(failure); return false; }
            }

            internal void Shutdown(string reason)
            {
                lock (writeGate)
                {
                    if (closed) return;
                    if (writer != null) WriteLocked("app_shutdown", reason ?? "프로그램 종료", null);
                    closed = true;
                    CloseLocked();
                }
            }

            private void FailLocked(Exception error)
            {
                if (errorMessage.Length == 0) errorMessage = FailureText(error);
                CloseLocked();
            }

            private void CloseLocked()
            {
                StreamWriter oldWriter = writer; FileStream oldStream = stream;
                writer = null; stream = null;
                try { if (oldWriter != null) oldWriter.Dispose(); }
                catch (Exception error) { if (errorMessage.Length == 0) errorMessage = FailureText(error); }
                try { if (oldStream != null) oldStream.Dispose(); }
                catch (Exception error) { if (errorMessage.Length == 0) errorMessage = FailureText(error); }
            }
        }

        private static string FailureText(Exception error)
        {
            try { return "진단 기록 저장 실패: " + error.GetType().Name + " · " + error.Message; }
            catch (Exception) { return "진단 기록 저장 실패: 오류 상세를 읽을 수 없습니다."; }
        }

        private static string Json(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            foreach (char character in value ?? "")
            {
                switch (character)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 32) result.Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else result.Append(character);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        public static void SelfTest(string testRoot)
        {
            string fixture = Path.Combine(testRoot, "diagnostics-fixture-" + Guid.NewGuid().ToString("N"));
            Sink first = null, second = null, blocked = null;
            string blockedPath = Path.Combine(fixture, "blocked-directory.txt");
            Sink applicationSink;
            lock (gate) applicationSink = current;
            try
            {
                Directory.CreateDirectory(fixture);
                first = new Sink(fixture, "fixture-version", "self-test");
                Require(first.Open() && first.IsInitialized, "diagnostic file initializes");
                Exception example = null;
                try { throw new InvalidOperationException("예외 \"내용\"\r\nsecond line"); }
                catch (Exception error) { example = error; }
                Require(first.Write("fixture\tkind", "한글 \"quote\" \\ path\r\nline\tend\u0001", example), "event and exception are written");
                string flushed;
                using (FileStream file = new FileStream(first.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Require(file.ReadByte() == 239 && file.ReadByte() == 187 && file.ReadByte() == 191, "UTF-8 BOM");
                    file.Position = 0;
                    using (StreamReader reader = new StreamReader(file, Encoding.UTF8)) flushed = reader.ReadToEnd();
                }
                Require(flushed.Contains("\"kind\":\"fixture\\tkind\"") && flushed.Contains("\\\"quote\\\"") &&
                    flushed.Contains("\\\\ path\\r\\nline\\tend\\u0001"), "JSON control and string escaping");
                Require(flushed.Contains("System.InvalidOperationException") && flushed.Contains("DiagnosticsLog.SelfTest") &&
                    flushed.Contains("\"version\":\"fixture-version\"") && flushed.Contains("\"run_id\":") && flushed.Contains("\"timestamp_utc\":"), "exception stack and run metadata persist");
                Require(flushed.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length == 2, "flushed events occupy one physical line each");
                first.Shutdown("first shutdown"); first.Shutdown("duplicate shutdown");
                string completed = File.ReadAllText(first.FilePath, Encoding.UTF8);
                Require(completed.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length == 3 &&
                    completed.Contains("first shutdown") && !completed.Contains("duplicate shutdown"), "shutdown is recorded once");
                Require(!first.Write("after_close", "must fail without throwing", null) && first.ErrorMessage.Length > 0 && !first.IsInitialized, "post-shutdown write reports failure");
                second = new Sink(fixture, "fixture-version", "self-test");
                Require(second.Open() && second.FilePath != first.FilePath, "runs have unique files");
                second.Shutdown("second shutdown");
                Require(File.ReadAllText(first.FilePath, Encoding.UTF8) == completed, "new runs preserve prior diagnostic files");
                File.WriteAllText(blockedPath, "existing fixture file", Encoding.UTF8);
                blocked = new Sink(blockedPath, "fixture-version", "self-test");
                Require(!blocked.Open() && !blocked.IsInitialized && blocked.ErrorMessage.Length > 0 &&
                    !blocked.Write("unavailable", "failure must not throw", null), "unwritable directory fails safely");
                blocked.Shutdown("unavailable shutdown"); blocked.Shutdown("duplicate unavailable shutdown");
                Require(File.ReadAllText(blockedPath, Encoding.UTF8) == "existing fixture file", "failed diagnostics preserve existing files");
                lock (gate) Require(Object.ReferenceEquals(current, applicationSink), "self-test does not replace application diagnostics");
            }
            finally
            {
                if (first != null) first.Shutdown("fixture cleanup");
                if (second != null) second.Shutdown("fixture cleanup");
                if (blocked != null) blocked.Shutdown("fixture cleanup");
                foreach (Sink sink in new[] { first, second })
                    if (sink != null && sink.FilePath.Length > 0 && File.Exists(sink.FilePath)) File.Delete(sink.FilePath);
                if (File.Exists(blockedPath)) File.Delete(blockedPath);
                if (Directory.Exists(fixture) && Directory.GetFileSystemEntries(fixture).Length == 0) Directory.Delete(fixture);
            }
        }

        private static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("Diagnostics log test: " + message);
        }
    }
}
