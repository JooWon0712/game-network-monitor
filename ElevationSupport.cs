using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;

namespace PcInternetMonitor
{
    // Carries only the visible selections; never starts measurement on launch.
    public sealed class StartupSelection
    {
        public int ProcessId;
        public string AdapterId = "";
        public string Gateway = "";
        public string Reference = "";
        public bool UseReference;
        public bool CollectGame;
        public bool CollectProcesses;

        public string ToArguments()
        {
            string[] fields = { ProcessId.ToString(CultureInfo.InvariantCulture), AdapterId ?? "", Gateway ?? "", Reference ?? "", UseReference ? "1" : "0", CollectGame ? "1" : "0", CollectProcesses ? "1" : "0" };
            foreach (string field in fields)
                if (field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0) throw new ArgumentException("선택 값에 줄바꿈을 사용할 수 없습니다.");
            // Base64 contains no whitespace, quotes or command interpreter syntax.
            return "--restore " + Convert.ToBase64String(Encoding.UTF8.GetBytes(String.Join("\n", fields)));
        }

        public static StartupSelection FromArgs(string[] args)
        {
            if (args.Length == 0 || args[0] != "--restore") return null;
            if (args.Length != 2 || args[1].Length > 4096) throw new ArgumentException("복원할 선택 값이 올바르지 않습니다.");
            string[] fields = Encoding.UTF8.GetString(Convert.FromBase64String(args[1])).Split('\n');
            int pid;
            if ((fields.Length != 6 && fields.Length != 7) || !Int32.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid < 0 ||
                fields[1].Length > 256 || (fields[4] != "0" && fields[4] != "1") || (fields[5] != "0" && fields[5] != "1") || (fields.Length == 7 && fields[6] != "0" && fields[6] != "1"))
                throw new ArgumentException("복원할 선택 값이 올바르지 않습니다.");
            if ((fields[2].Length > 0 && !ProbeRunner.IsValidTarget(fields[2])) || (fields[4] == "1" && !ProbeRunner.IsValidTarget(fields[3])))
                throw new ArgumentException("복원할 검사 대상 주소가 올바르지 않습니다.");
            return new StartupSelection { ProcessId = pid, AdapterId = fields[1], Gateway = fields[2], Reference = fields[3], UseReference = fields[4] == "1", CollectGame = fields[5] == "1", CollectProcesses = fields.Length == 7 && fields[6] == "1" };
        }

        public static void SelfTest()
        {
            StartupSelection original = new StartupSelection { ProcessId = 12776, AdapterId = "{test-adapter}", Gateway = "192.168.0.1", Reference = "1.1.1.1", UseReference = true, CollectGame = true, CollectProcesses = true };
            StartupSelection restored = FromArgs(original.ToArguments().Split(' '));
            if (restored.ProcessId != 12776 || restored.AdapterId != original.AdapterId || restored.Gateway != original.Gateway || restored.Reference != original.Reference || !restored.UseReference || !restored.CollectGame || !restored.CollectProcesses)
                throw new InvalidOperationException("선택 값 전달 검사 실패");
            if (FromArgs(new string[0]) != null) throw new InvalidOperationException("일반 실행 복원 검사 실패");
            bool rejected = false;
            try { FromArgs(new[] { "--restore", "!invalid!" }); } catch (FormatException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("잘못된 선택 값 허용");
        }
    }

    public static class ElevationSupport
    {
        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // This is called exclusively from the user's explicit UI button click.
        public static Process LaunchAdministrator(StartupSelection selection)
        {
            string arguments = selection.ToArguments();
            StartupSelection.FromArgs(arguments.Split(' '));
            string path;
            using (Process current = Process.GetCurrentProcess()) path = current.MainModule.FileName;
            return Process.Start(new ProcessStartInfo {
                FileName = path, Arguments = arguments, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true, Verb = "runas"
            });
        }
    }
}
