using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    // A run can remove only its newly created GUID directory. Borrowed child
    // processes leave the parent's files available until the parent has read them.
    internal sealed class TestRunScope : IDisposable
    {
        private const string MarkerName = ".owner";
        private readonly object gate = new object();
        private FileStream ownerMarker;
        private bool disposed, markerRemoved;

        public string Id { get; private set; }
        public string DirectoryPath { get; private set; }
        public bool OwnsDirectory { get; private set; }

        private TestRunScope(string id, bool ownsDirectory)
        {
            Id = CanonicalId(id);
            DirectoryPath = RunPath(Id);
            OwnsDirectory = ownsDirectory;
        }

        public static TestRunScope Create()
        {
            string root = TestsPath();
            AssertUnlinkedAncestors(root);
            Directory.CreateDirectory(root);
            TestRunScope run = new TestRunScope(Guid.NewGuid().ToString("N"), true);
            run.AssertExactPath();
            if (Directory.Exists(run.DirectoryPath) || File.Exists(run.DirectoryPath))
                throw new IOException("A new test run directory already exists.");
            Directory.CreateDirectory(run.DirectoryPath);
            try
            {
                run.ownerMarker = new FileStream(run.MarkerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                byte[] id = Encoding.ASCII.GetBytes(run.Id);
                run.ownerMarker.Write(id, 0, id.Length);
                run.ownerMarker.Flush(true);
                return run;
            }
            catch
            {
                // A failed ownership claim must never remove a pre-existing file.
                // Retain an unclaimed directory rather than guess at its contents.
                if (run.ownerMarker != null) { run.ownerMarker.Dispose(); run.ownerMarker = null; }
                throw;
            }
        }

        public static TestRunScope Borrow(string id)
        {
            TestRunScope run = new TestRunScope(id, false);
            run.AssertOwnership();
            return run;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                if (!OwnsDirectory) { disposed = true; return; }
                AssertOwnership();
                for (int attempt = 0; ; attempt++)
                {
                    try { DeleteOwnedDirectory(); disposed = true; return; }
                    catch (IOException) { if (attempt >= 3) throw; }
                    catch (UnauthorizedAccessException) { if (attempt >= 3) throw; }
                    Thread.Sleep(50);
                }
            }
        }

        private string MarkerPath { get { return Path.Combine(DirectoryPath, MarkerName); } }

        private void AssertExactPath()
        {
            string expected = RunPath(Id);
            if (!String.Equals(Path.GetFullPath(DirectoryPath), expected, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(expected), TestsPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its exact run directory.");
            AssertUnlinkedAncestors(expected);
        }

        private void AssertOwnership()
        {
            AssertExactPath();
            if (!Directory.Exists(DirectoryPath)) throw new DirectoryNotFoundException("The test run directory is unavailable.");
            if ((File.GetAttributes(MarkerPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidOperationException("The test run ownership marker must be an ordinary file.");
            // The owner has write access, so this reader must allow that existing
            // access. The owner's FileShare.Read still prevents marker replacement.
            using (FileStream input = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (input.Length != 32) throw new InvalidOperationException("Invalid test run ownership marker.");
                byte[] bytes = new byte[32]; int count = 0;
                while (count < bytes.Length)
                {
                    int read = input.Read(bytes, count, bytes.Length - count);
                    if (read == 0) throw new IOException("Incomplete test run ownership marker.");
                    count += read;
                }
                if (Encoding.ASCII.GetString(bytes) != Id)
                    throw new InvalidOperationException("Test cleanup does not own this directory.");
            }
        }

        private void DeleteOwnedDirectory()
        {
            AssertExactPath();
            if (!Directory.Exists(DirectoryPath)) return;
            if (!markerRemoved) AssertOwnership();
            else
            {
                // Only an empty root can remain after our marker was removed.
                // Never expand cleanup to newly appearing unclaimed contents.
                Directory.Delete(DirectoryPath, false);
                return;
            }

            List<string> files = new List<string>(), directories = new List<string>();
            CollectPlan(DirectoryPath, files, directories);
            // Complete the entire boundary/link preflight before deleting anything.
            foreach (string file in files)
                if (!String.Equals(file, MarkerPath, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            foreach (string directory in directories) Directory.Delete(directory, false);
            AssertOwnership();
            if (ownerMarker != null) { ownerMarker.Dispose(); ownerMarker = null; }
            File.Delete(MarkerPath); markerRemoved = true;
            Directory.Delete(DirectoryPath, false);
        }

        private void CollectPlan(string directory, List<string> files, List<string> directories)
        {
            string prefix = DirectoryPath + Path.DirectorySeparatorChar;
            foreach (string raw in Directory.GetFileSystemEntries(directory))
            {
                string path = Path.GetFullPath(raw);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A test artifact escaped its run directory.");
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked test artifacts cannot be cleaned automatically.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    CollectPlan(path, files, directories);
                    directories.Add(path);
                }
                else files.Add(path);
            }
        }

        private static string CanonicalId(string id)
        {
            Guid parsed;
            if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out parsed))
                throw new ArgumentException("A test run GUID is required.", "id");
            return parsed.ToString("N");
        }

        private static string TestsPath()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string RunPath(string id) { return Path.GetFullPath(Path.Combine(TestsPath(), ".run-" + CanonicalId(id))); }

        private static void AssertUnlinkedAncestors(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Test run paths cannot pass through linked files or directories.");
            }
        }

        public static void SelfTest()
        {
            string removed;
            using (TestRunScope run = Create())
            {
                removed = run.DirectoryPath;
                string nested = Path.Combine(removed, "nested"); Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "sample.csv"), "synthetic");
                File.WriteAllText(Path.Combine(removed, "screen.png"), "synthetic");
                using (TestRunScope borrowed = Borrow(run.Id))
                    Check(!borrowed.OwnsDirectory && borrowed.DirectoryPath == removed, "borrowed child scope");
                Check(File.Exists(Path.Combine(nested, "sample.csv")), "child disposal removed parent data");
            }
            Check(!Directory.Exists(removed), "owned files were not removed");

            string failedRun = null;
            try
            {
                using (TestRunScope run = Create())
                {
                    failedRun = run.DirectoryPath;
                    File.WriteAllText(Path.Combine(failedRun, "failure.tmp"), "synthetic");
                    throw new SyntheticFailure();
                }
            }
            catch (SyntheticFailure) { }
            Check(failedRun != null && !Directory.Exists(failedRun), "exception cleanup");

            using (TestRunScope first = Create())
            using (TestRunScope second = Create())
            {
                string secondFile = Path.Combine(second.DirectoryPath, "other-run.txt");
                File.WriteAllText(secondFile, "preserve until its owner exits");
                first.Dispose(); first.Dispose();
                Check(File.Exists(secondFile) && Directory.Exists(second.DirectoryPath), "another run was removed");
            }

            bool invalidRejected = false;
            try { Borrow("..\\logs"); } catch (ArgumentException) { invalidRejected = true; }
            Check(invalidRejected, "non-GUID scope was accepted");

            using (TestRunScope run = Create())
            {
                string sentinel = Path.Combine(run.DirectoryPath, "keep-on-ownership-failure.txt");
                File.WriteAllText(sentinel, "synthetic");
                run.ownerMarker.Dispose(); run.ownerMarker = null;
                try
                {
                    File.WriteAllText(run.MarkerPath, new string('x', 32), Encoding.ASCII);
                    bool borrowRejected = false, cleanupRejected = false;
                    try { using (TestRunScope unexpected = Borrow(run.Id)) { } } catch (InvalidOperationException) { borrowRejected = true; }
                    try { run.Dispose(); } catch (InvalidOperationException) { cleanupRejected = true; }
                    Check(borrowRejected && cleanupRejected && File.Exists(sentinel), "unowned directory cleanup was allowed");
                }
                finally { File.WriteAllText(run.MarkerPath, run.Id, Encoding.ASCII); }
            }
        }

        private sealed class SyntheticFailure : Exception { }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Test run cleanup: " + message); }
    }
}
