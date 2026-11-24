using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AppManager.Tests
{
    public static class Program
    {
        private static int passed;
        private static string repo;

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            passed++;
            Console.WriteLine("  ok  " + message);
        }

        private static void WaitFor(Func<bool> condition, int timeoutSeconds, string label)
        {
            DateTime end = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < end)
            {
                if (condition()) return;
                Thread.Sleep(200);
            }
            throw new Exception("timeout: " + label);
        }

        private static string LogText(ManagedApp app)
        {
            List<LogLine> lines = app.Log.ReadAfter(0, 2000);
            StringBuilder sb = new StringBuilder();
            foreach (LogLine l in lines) sb.AppendLine(l.Text);
            return sb.ToString();
        }

        private static string ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                return sr.ReadToEnd();
            }
        }

        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("EX: " + ex.GetType().FullName);
                Console.WriteLine("MSG: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            string temp = Path.Combine(Path.GetTempPath(), "appmgr-desktop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Paths.Init(Path.Combine(temp, "config.json"));

            Supervisor svc = new Supervisor();
            svc.Load();
            Console.WriteLine("[1] 配置初始化");
            Check(File.Exists(Paths.ConfigPath), "生成了 config.json");
            Check(svc.Apps.Count == 0, "初始应用列表为空");

            Console.WriteLine("[2] 启动 Python 应用");
            AppConfig ticker = new AppConfig();
            ticker.name = "ticker";
            ticker.command = "py -3 -u \"" + Path.Combine(repo, "tests", "fixtures", "ticker.py") + "\"";
            ticker.cwd = repo;
            ticker.shell = "powershell";
            ticker.env = new Dictionary<string, string>();
            ticker.env["PORT"] = "8123";
            ticker.auto_start = false;
            ticker.restart_on_crash = true;
            ticker.max_log_lines = 2000;
            ticker.max_restarts = 10;
            ManagedApp app = svc.Add(ticker);
            Check(app.Id == "ticker", "应用 ID 为 ticker");

            string msg;
            Check(app.Start(true, out msg), "启动返回成功：" + msg);
            WaitFor(delegate () { return app.Snapshot().Status == "running"; }, 12, "进入 running");
            AppSnapshot snap = app.Snapshot();
            Check(snap.Pid > 0, "获得 PID " + snap.Pid);

            WaitFor(delegate () { return LogText(app).Contains("tick 1"); }, 12, "出现 tick 日志");
            string text = LogText(app);
            Check(text.Contains("ticker 启动，中文测试"), "中文输出正常");
            Check(text.Contains("PORT=8123"), "环境变量已传入");

            Console.WriteLine("[3] 停止与重启");
            int firstPid = snap.Pid;
            Check(app.Stop(out msg), "停止返回成功：" + msg);
            Check(app.Snapshot().Status == "stopped", "状态为 stopped");
            Check(app.Snapshot().Pid == 0, "PID 已清空");
            Check(app.Start(true, out msg), "重新启动成功");
            WaitFor(delegate () { return app.Snapshot().Status == "running"; }, 12, "重新 running");
            Check(app.Snapshot().Pid != firstPid, "重启后 PID 变化");
            Console.WriteLine("      停止重启后的实例");
            app.Stop(out msg);

            string logFile = Path.Combine(Paths.LogsDir, "ticker.log");
            Console.WriteLine("      检查日志文件 " + logFile);
            Check(File.Exists(logFile) && ReadShared(logFile).Contains("tick"), "日志已落盘");

            Console.WriteLine("[4] 崩溃自动重启");
            AppConfig crash = new AppConfig();
            crash.name = "crasher";
            crash.command = "py -3 -u \"" + Path.Combine(repo, "tests", "fixtures", "crasher.py") + "\"";
            crash.cwd = repo;
            crash.shell = "powershell";
            crash.env = new Dictionary<string, string>();
            crash.auto_start = false;
            crash.restart_on_crash = true;
            crash.max_restarts = 1;
            crash.max_log_lines = 2000;
            ManagedApp bad = svc.Add(crash);
            Check(bad.Start(true, out msg), "崩溃应用已启动");
            WaitFor(delegate () { return bad.Snapshot().Restarts >= 1; }, 15, "自动重启计数");
            WaitFor(delegate ()
            {
                AppSnapshot s = bad.Snapshot();
                return s.Status == "crashed" && !s.RestartPending && s.Restarts >= 1;
            }, 20, "达到重启上限");
            Check(bad.Snapshot().LastError.Contains("自动重启上限"), "重启上限提示");
            bad.Stop(out msg);

            Console.WriteLine("[5] CMD 模式");
            AppConfig cmd = new AppConfig();
            cmd.name = "cmd-echo";
            cmd.command = "echo hello-cmd";
            cmd.cwd = repo;
            cmd.shell = "cmd";
            cmd.env = new Dictionary<string, string>();
            cmd.auto_start = false;
            cmd.restart_on_crash = false;
            cmd.max_log_lines = 2000;
            cmd.max_restarts = 0;
            ManagedApp ce = svc.Add(cmd);
            Check(ce.Start(true, out msg), "CMD 应用已启动");
            WaitFor(delegate () { return LogText(ce).Contains("hello-cmd"); }, 12, "CMD 输出");
            Check(true, "CMD 输出已采集");

            Console.WriteLine("[6] 配置持久化与清空日志");
            Supervisor reload = new Supervisor();
            reload.Load();
            Check(reload.Apps.Count == 3, "重新加载后仍有 3 个应用");
            app.ClearLogs();
            Check(app.Log.ReadAfter(0, 100).Count == 0, "内存日志已清空");
            Check(new FileInfo(logFile).Length == 0, "日志文件已清空");

            foreach (ManagedApp a in svc.Apps.ToList()) svc.Remove(a.Id);
            Check(svc.Apps.Count == 0, "应用已全部删除");
            svc.StopAll();

            Console.WriteLine();
            Console.WriteLine("全部通过：" + passed + " 项检查");
            try { Directory.Delete(temp, true); }
            catch (Exception) { }
            return 0;
        }
    }
}
