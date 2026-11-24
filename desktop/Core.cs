using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AppManager
{
    public static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string TaskName = "AppManagerDesktop";
    }

    public static class Paths
    {
        public static string BaseDir;
        public static string ConfigPath;

        public static string LogsDir { get { return Path.Combine(BaseDir, "logs"); } }
        public static string RunDir { get { return Path.Combine(BaseDir, "run"); } }

        public static void Init(string configPath)
        {
            if (string.IsNullOrEmpty(configPath))
            {
                configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            }
            ConfigPath = Path.GetFullPath(configPath);
            BaseDir = Path.GetDirectoryName(ConfigPath);
            Directory.CreateDirectory(BaseDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(RunDir);
        }
    }

    public class AppConfig
    {
        public string id { get; set; }
        public string name { get; set; }
        public string command { get; set; }
        public string cwd { get; set; }
        public string shell { get; set; }
        public Dictionary<string, string> env { get; set; }
        public bool auto_start { get; set; }
        public bool restart_on_crash { get; set; }
        public int start_delay { get; set; }
        public int max_restarts { get; set; }
        public int max_log_lines { get; set; }

        public AppConfig Clone()
        {
            AppConfig c = new AppConfig();
            c.id = id;
            c.name = name;
            c.command = command;
            c.cwd = cwd;
            c.shell = shell;
            c.auto_start = auto_start;
            c.restart_on_crash = restart_on_crash;
            c.start_delay = start_delay;
            c.max_restarts = max_restarts;
            c.max_log_lines = max_log_lines;
            c.env = new Dictionary<string, string>();
            if (env != null)
            {
                foreach (KeyValuePair<string, string> kv in env) c.env[kv.Key] = kv.Value;
            }
            return c;
        }
    }

    public class RootConfig
    {
        public int port { get; set; }
        public string host { get; set; }
        public string auth_token { get; set; }
        public int startup_delay { get; set; }
        public string extra_path { get; set; }
        public Dictionary<string, string> env { get; set; }
        public List<AppConfig> apps { get; set; }
    }

    public static class ConfigStore
    {
        public static RootConfig Load()
        {
            if (!File.Exists(Paths.ConfigPath))
            {
                RootConfig fresh = new RootConfig();
                fresh.port = 8787;
                fresh.host = "127.0.0.1";
                fresh.auth_token = "";
                fresh.startup_delay = 3;
                fresh.extra_path = "";
                fresh.env = new Dictionary<string, string>();
                fresh.apps = new List<AppConfig>();
                Save(fresh);
                return fresh;
            }

            string json = File.ReadAllText(Paths.ConfigPath, Encoding.UTF8);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            RootConfig cfg = ser.Deserialize<RootConfig>(json);
            if (cfg == null) throw new InvalidOperationException("配置文件内容为空或格式不正确");
            Normalize(cfg);
            return cfg;
        }

        public static void Normalize(RootConfig cfg)
        {
            if (cfg.host == null || cfg.host.Trim().Length == 0) cfg.host = "127.0.0.1";
            if (cfg.port < 1 || cfg.port > 65535) cfg.port = 8787;
            if (cfg.startup_delay < 0) cfg.startup_delay = 0;
            if (cfg.startup_delay > 3600) cfg.startup_delay = 3600;
            if (cfg.extra_path == null) cfg.extra_path = "";
            if (cfg.auth_token == null) cfg.auth_token = "";
            if (cfg.env == null) cfg.env = new Dictionary<string, string>();
            if (cfg.apps == null) cfg.apps = new List<AppConfig>();

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<AppConfig> cleaned = new List<AppConfig>();
            foreach (AppConfig a in cfg.apps)
            {
                try
                {
                    NormalizeApp(a, null);
                    string id = a.id;
                    if (string.IsNullOrEmpty(id) || used.Contains(id)) id = UniqueId(a.name, used);
                    a.id = id;
                    used.Add(id);
                    cleaned.Add(a);
                }
                catch (ArgumentException)
                {
                    // 单条配置有问题时跳过，不让整个配置无法打开
                }
            }
            cfg.apps = cleaned;
        }

        public static AppConfig NormalizeApp(AppConfig a, string forcedId)
        {
            if (a == null) throw new ArgumentException("应用配置为空");

            a.name = (a.name ?? "").Trim();
            if (a.name.Length == 0) throw new ArgumentException("请填写应用名称");
            if (a.name.Length > 60) throw new ArgumentException("应用名称不能超过 60 个字符");

            a.command = (a.command ?? "").Trim();
            if (a.command.Length == 0) throw new ArgumentException("请填写启动命令");

            a.cwd = (a.cwd ?? "").Trim();
            if (a.cwd.Length > 0)
            {
                a.cwd = Environment.ExpandEnvironmentVariables(a.cwd);
                if (!Directory.Exists(a.cwd)) throw new ArgumentException("工作目录不存在：" + a.cwd);
                a.cwd = Path.GetFullPath(a.cwd);
            }
            else
            {
                a.cwd = Paths.BaseDir;
            }

            a.shell = (a.shell ?? "powershell").Trim().ToLowerInvariant();
            if (a.shell != "powershell" && a.shell != "cmd") a.shell = "powershell";

            if (a.env == null) a.env = new Dictionary<string, string>();
            Dictionary<string, string> env = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in a.env)
            {
                string k = (kv.Key ?? "").Trim();
                if (k.Length > 0) env[k] = kv.Value ?? "";
            }
            a.env = env;

            if (a.start_delay < 0) a.start_delay = 0;
            if (a.start_delay > 600) a.start_delay = 600;
            if (a.max_restarts < 0) a.max_restarts = 0;
            if (a.max_restarts > 100) a.max_restarts = 100;
            if (a.max_log_lines < 200) a.max_log_lines = 2000;
            if (a.max_log_lines > 20000) a.max_log_lines = 20000;

            a.id = forcedId ?? (a.id ?? "");
            return a;
        }

        public static string Slug(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in (text ?? ""))
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9') || ch == '-' || ch == '_') sb.Append(ch);
                else if (ch == ' ' || ch == '.') sb.Append('-');
            }
            string s = sb.ToString().Trim('-', '_');
            if (s.Length > 40) s = s.Substring(0, 40);
            return s;
        }

        public static string UniqueId(string name, HashSet<string> used)
        {
            string slug = Slug(name);
            if (slug.Length == 0) slug = "app";
            string candidate = slug;
            int n = 2;
            while (used.Contains(candidate))
            {
                candidate = slug + "-" + n;
                n++;
            }
            return candidate;
        }

        public static void Save(RootConfig cfg)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            string json = ser.Serialize(cfg);
            string pretty = PrettyJson(json);
            string tmp = Paths.ConfigPath + ".tmp";
            File.WriteAllText(tmp, pretty, new UTF8Encoding(false));
            if (File.Exists(Paths.ConfigPath)) File.Delete(Paths.ConfigPath);
            File.Move(tmp, Paths.ConfigPath);
        }

        public static string PrettyJson(string json)
        {
            StringBuilder sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            bool esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (inStr)
                {
                    sb.Append(ch);
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }

                if (ch == '"')
                {
                    inStr = true;
                    sb.Append(ch);
                }
                else if (ch == '{' || ch == '[')
                {
                    sb.Append(ch);
                    char closer = ch == '{' ? '}' : ']';
                    int j = i + 1;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (j < json.Length && json[j] == closer)
                    {
                        sb.Append(closer);
                        i = j;
                    }
                    else
                    {
                        indent++;
                        sb.Append('\n');
                        sb.Append(' ', indent * 2);
                    }
                }
                else if (ch == '}' || ch == ']')
                {
                    indent = Math.Max(0, indent - 1);
                    sb.Append('\n');
                    sb.Append(' ', indent * 2);
                    sb.Append(ch);
                }
                else if (ch == ',')
                {
                    sb.Append(",\n");
                    sb.Append(' ', indent * 2);
                }
                else if (ch == ':')
                {
                    sb.Append(": ");
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString() + "\n";
        }
    }

    public class LogLine
    {
        public long Seq;
        public DateTime Time;
        public string Text;
    }

    public class LogBuffer
    {
        private readonly object gate = new object();
        private readonly List<LogLine> lines = new List<LogLine>();
        private int maxLines;
        private long seq;
        private string path;
        private FileStream fs;
        private int writes;

        public string FilePath { get { return path; } }

        public LogBuffer(int max, string filePath)
        {
            maxLines = Math.Max(100, max);
            path = filePath;
        }

        public long Seq
        {
            get { lock (gate) { return seq; } }
        }

        public void SetMaxLines(int max)
        {
            lock (gate)
            {
                maxLines = Math.Max(100, max);
                if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
            }
        }

        public LogLine Append(string text)
        {
            text = (text ?? "").Replace("\0", "");
            if (text.Length > 8000) text = text.Substring(0, 8000);
            lock (gate)
            {
                seq++;
                LogLine line = new LogLine();
                line.Seq = seq;
                line.Time = DateTime.Now;
                line.Text = text;
                lines.Add(line);
                if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
                WriteFile(line);
                return line;
            }
        }

        private void WriteFile(LogLine line)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (fs == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                }
                byte[] data = Encoding.UTF8.GetBytes("[" + line.Time.ToString("yyyy-MM-dd HH:mm:ss") + "] " + line.Text + "\r\n");
                fs.Write(data, 0, data.Length);
                fs.Flush();
                writes++;
                if (writes % 200 == 0) RotateIfNeeded();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void RotateIfNeeded()
        {
            try
            {
                if (fs == null || fs.Length < 5 * 1024 * 1024) return;
                fs.Close();
                fs = null;
                for (int i = 2; i >= 1; i--)
                {
                    string src = path + "." + i;
                    string dst = path + "." + (i + 1);
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }
                if (File.Exists(path)) File.Move(path, path + ".1");
            }
            catch (IOException)
            {
            }
        }

        public List<LogLine> ReadAfter(long after, int limit)
        {
            lock (gate)
            {
                if (limit < 1) limit = 600;
                if (limit > 5000) limit = 5000;
                if (after <= 0)
                {
                    int start = Math.Max(0, lines.Count - limit);
                    return lines.Skip(start).Take(limit).ToList();
                }
                return lines.Where(delegate (LogLine x) { return x.Seq > after; }).Take(limit).ToList();
            }
        }

        public long Clear()
        {
            lock (gate)
            {
                lines.Clear();
                if (fs != null)
                {
                    try { fs.Close(); }
                    catch (IOException) { }
                    fs = null;
                }
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, new byte[0]);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return seq;
            }
        }
    }

    public class AppSnapshot
    {
        public string Id;
        public string Name;
        public string Command;
        public string Cwd;
        public string Shell;
        public string Status;
        public string LastError;
        public int Pid;
        public int ExitCode;
        public int Restarts;
        public int MaxRestarts;
        public int StartDelay;
        public bool Running;
        public bool AutoStart;
        public bool RestartOnCrash;
        public bool RestartPending;
        public long UptimeSeconds;
        public DateTime StartedAt;
        public bool HasStarted;
        public Dictionary<string, string> Env;
    }

    public class ManagedApp
    {
        private readonly object gate = new object();
        private readonly Supervisor owner;
        private AppConfig cfg;
        private Process proc;
        private string status = "stopped";
        private int pid;
        private int exitCode = -1;
        private DateTime startedAt;
        private DateTime stoppedAt;
        private bool hasStarted;
        private bool stopRequested;
        private int generation;
        private int restarts;
        private bool restartPending;
        private string lastError = "";
        public readonly LogBuffer Log;

        public ManagedApp(Supervisor owner, AppConfig cfg)
        {
            this.owner = owner;
            this.cfg = cfg;
            Log = new LogBuffer(cfg.max_log_lines, Path.Combine(Paths.LogsDir, SafeId(cfg.id) + ".log"));
        }

        public string Id { get { return cfg.id; } }
        public string Name { get { return cfg.name; } }
        public AppConfig Config { get { return cfg; } }

        public static string SafeId(string id)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in (id ?? ""))
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            }
            return sb.Length == 0 ? "app" : sb.ToString();
        }

        public bool IsAlive()
        {
            try { return proc != null && !proc.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        public AppSnapshot Snapshot()
        {
            lock (gate)
            {
                AppSnapshot s = new AppSnapshot();
                s.Id = cfg.id;
                s.Name = cfg.name;
                s.Command = cfg.command;
                s.Cwd = cfg.cwd;
                s.Shell = cfg.shell;
                s.Status = status;
                s.LastError = lastError;
                s.Pid = pid;
                s.ExitCode = exitCode;
                s.Restarts = restarts;
                s.MaxRestarts = cfg.max_restarts;
                s.StartDelay = cfg.start_delay;
                s.Running = IsAlive();
                s.AutoStart = cfg.auto_start;
                s.RestartOnCrash = cfg.restart_on_crash;
                s.RestartPending = restartPending;
                s.HasStarted = hasStarted;
                s.StartedAt = startedAt;
                s.UptimeSeconds = (s.Running && hasStarted) ? (long)Math.Max(0, (DateTime.Now - startedAt).TotalSeconds) : 0;
                s.Env = new Dictionary<string, string>(cfg.env);
                return s;
            }
        }

        public void UpdateConfig(AppConfig next)
        {
            lock (gate)
            {
                cfg = next;
                Log.SetMaxLines(next.max_log_lines);
            }
        }

        private string BuildLaunch(out string fileName, out string arguments)
        {
            string cwd = cfg.cwd;
            string command = cfg.command.TrimStart();
            if (cfg.shell == "cmd")
            {
                string cmdPath = Path.Combine(Paths.RunDir, SafeId(cfg.id) + ".cmd");
                StringBuilder cb = new StringBuilder();
                cb.Append("@echo off\r\n");
                cb.Append("chcp 65001 >nul\r\n");
                cb.Append("cd /d \"").Append(cwd).Append("\"\r\n");
                cb.Append(cfg.command).Append("\r\n");
                cb.Append("exit /b %ERRORLEVEL%\r\n");
                File.WriteAllText(cmdPath, cb.ToString(), new UTF8Encoding(false));
                fileName = "cmd.exe";
                arguments = "/d /c \"" + cmdPath + "\"";
                return cmdPath;
            }

            if (command.StartsWith("\"") || command.StartsWith("'")) command = "& " + command;
            string quotedCwd = cwd.Replace("'", "''");
            StringBuilder sb = new StringBuilder();
            sb.Append("$ErrorActionPreference = 'Continue'\r\n");
            sb.Append("$ProgressPreference = 'SilentlyContinue'\r\n");
            sb.Append("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false\r\n");
            sb.Append("$OutputEncoding = [Console]::OutputEncoding\r\n");
            sb.Append("Set-Location -LiteralPath '").Append(quotedCwd).Append("'\r\n");
            sb.Append(command).Append("\r\n");
            sb.Append("exit $LASTEXITCODE\r\n");

            string script = sb.ToString();
            string psPath = Path.Combine(Paths.RunDir, SafeId(cfg.id) + ".ps1");
            File.WriteAllText(psPath, script, new UTF8Encoding(true));

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            fileName = "powershell.exe";
            arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded;
            return psPath;
        }

        public bool Start(bool manual, out string message)
        {
            message = "";
            int gen;
            Process p;
            lock (gate)
            {
                if (IsAlive())
                {
                    message = "应用已在运行";
                    return false;
                }
                if (status == "starting" || status == "stopping")
                {
                    message = "应用正在切换状态，请稍候";
                    return false;
                }
                if (manual) restarts = 0;
                status = "starting";
                stopRequested = false;
                lastError = "";
                generation++;
                gen = generation;
            }

            try
            {
                string fileName;
                string arguments;
                BuildLaunch(out fileName, out arguments);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                psi.WorkingDirectory = cfg.cwd;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                string extra = owner.Config.extra_path;
                if (!string.IsNullOrEmpty(extra))
                {
                    string oldPath = psi.EnvironmentVariables["PATH"];
                    psi.EnvironmentVariables["PATH"] = extra.TrimEnd(';') + ";" + (oldPath ?? "");
                }
                foreach (KeyValuePair<string, string> kv in owner.Config.env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
                foreach (KeyValuePair<string, string> kv in cfg.env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;

                p = new Process();
                p.StartInfo = psi;
                p.Start();
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    status = "stopped";
                    lastError = "启动失败：" + ex.Message;
                }
                Log.Append("[管家] 启动失败：" + ex.Message);
                message = lastError;
                return false;
            }

            lock (gate)
            {
                proc = p;
                pid = p.Id;
                status = "running";
                startedAt = DateTime.Now;
                hasStarted = true;
            }
            Log.Append("[管家] 已启动，PID " + p.Id + "（" + cfg.shell + "）");
            owner.SystemLog.Append("[" + cfg.name + "] 已启动，PID " + p.Id);

            Thread to = new Thread(delegate () { Pump(p.StandardOutput.BaseStream); });
            to.IsBackground = true;
            to.Name = "out-" + cfg.id;
            to.Start();
            Thread te = new Thread(delegate () { Pump(p.StandardError.BaseStream); });
            te.IsBackground = true;
            te.Name = "err-" + cfg.id;
            te.Start();
            Thread tw = new Thread(delegate () { WaitExit(p, gen); });
            tw.IsBackground = true;
            tw.Name = "wait-" + cfg.id;
            tw.Start();

            message = "已启动";
            return true;
        }

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding Gbk = SafeGbk();

        private static Encoding SafeGbk()
        {
            try { return Encoding.GetEncoding(936); }
            catch (Exception) { return Encoding.UTF8; }
        }

        private void Emit(byte[] buffer, int length)
        {
            if (length <= 0) return;
            string text;
            try
            {
                text = StrictUtf8.GetString(buffer, 0, length);
            }
            catch (DecoderFallbackException)
            {
                try { text = Gbk.GetString(buffer, 0, length); }
                catch (Exception) { text = Encoding.UTF8.GetString(buffer, 0, length); }
            }
            Log.Append(text);
        }

        private void Pump(Stream stream)
        {
            byte[] buf = new byte[8192];
            byte[] line = new byte[8192];
            int lineLen = 0;
            while (true)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (Exception) { break; }
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 10 || b == 13)
                    {
                        if (lineLen > 0) { Emit(line, lineLen); lineLen = 0; }
                    }
                    else
                    {
                        line[lineLen++] = b;
                        if (lineLen >= line.Length) { Emit(line, lineLen); lineLen = 0; }
                    }
                }
            }
            if (lineLen > 0) Emit(line, lineLen);
        }

        private void WaitExit(Process p, int gen)
        {
            int code;
            try { p.WaitForExit(); code = p.ExitCode; }
            catch (Exception) { code = -1; }
            Thread.Sleep(200);

            bool stoppedByUser;
            bool shouldRestart = false;
            int attempt = 0;
            lock (gate)
            {
                if (proc != p) return;
                double runtime = hasStarted ? (DateTime.Now - startedAt).TotalSeconds : 0;
                if (runtime > 120) restarts = 0;
                proc = null;
                pid = 0;
                exitCode = code;
                stoppedAt = DateTime.Now;
                stoppedByUser = stopRequested;
                if (stoppedByUser)
                {
                    status = "stopped";
                }
                else if (code != 0)
                {
                    status = "crashed";
                    lastError = "进程异常退出（代码 " + code + "）";
                }
                else
                {
                    status = "stopped";
                    lastError = "进程已退出（代码 0）";
                }

                shouldRestart = !stoppedByUser && cfg.restart_on_crash && gen == generation && restarts < cfg.max_restarts;
                if (!stoppedByUser && code != 0 && cfg.restart_on_crash && restarts >= cfg.max_restarts)
                {
                    lastError = "进程异常退出（代码 " + code + "），已达到自动重启上限（" + cfg.max_restarts + " 次）";
                }
                if (shouldRestart)
                {
                    restarts++;
                    restartPending = true;
                    attempt = restarts;
                }
                else
                {
                    restartPending = false;
                }
            }

            if (!stoppedByUser)
            {
                Log.Append("[管家] 进程退出，代码 " + code);
                owner.SystemLog.Append("[" + cfg.name + "] 进程退出，代码 " + code);
            }

            if (shouldRestart)
            {
                int delay = Math.Min(30, 1 << Math.Min(attempt, 5));
                Log.Append("[管家] " + delay + " 秒后自动重启（第 " + attempt + " 次）");
                Thread t = new Thread(delegate ()
                {
                    Thread.Sleep(delay * 1000);
                    lock (gate)
                    {
                        if (gen != generation || stopRequested) { restartPending = false; return; }
                        restartPending = false;
                    }
                    if (IsAlive()) return;
                    string msg;
                    if (!Start(false, out msg)) Log.Append("[管家] 自动重启失败：" + msg);
                });
                t.IsBackground = true;
                t.Start();
            }
        }

        public bool Stop(out string message)
        {
            message = "";
            Process p;
            int currentPid;
            lock (gate)
            {
                p = proc;
                currentPid = pid;
                if (p == null || !IsAlive())
                {
                    status = "stopped";
                    proc = null;
                    pid = 0;
                    restartPending = false;
                    message = "应用已停止";
                    return true;
                }
                stopRequested = true;
                generation++;
                status = "stopping";
                restartPending = false;
            }

            KillTree(currentPid);
            DateTime deadline = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < deadline)
            {
                try { if (p.HasExited) break; }
                catch (InvalidOperationException) { break; }
                Thread.Sleep(100);
            }
            try
            {
                if (!p.HasExited)
                {
                    KillTree(currentPid);
                    p.WaitForExit(3000);
                }
            }
            catch (Exception) { }

            lock (gate)
            {
                if (proc == p)
                {
                    proc = null;
                    pid = 0;
                    status = "stopped";
                    stoppedAt = DateTime.Now;
                }
            }
            Log.Append("[管家] 已停止");
            owner.SystemLog.Append("[" + cfg.name + "] 已停止");
            message = "已停止";
            return true;
        }

        public static void KillTree(int pid)
        {
            if (pid <= 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process k = Process.Start(psi))
                {
                    if (k != null) k.WaitForExit(15000);
                }
            }
            catch (Exception) { }
        }

        public void ClearLogs()
        {
            Log.Clear();
        }
    }

    public class Supervisor
    {
        private readonly object gate = new object();
        public RootConfig Config;
        public List<ManagedApp> Apps = new List<ManagedApp>();
        public readonly LogBuffer SystemLog;
        private bool shuttingDown;

        public Supervisor()
        {
            SystemLog = new LogBuffer(1500, Path.Combine(Paths.LogsDir, "_manager.log"));
        }

        public void Load()
        {
            Config = ConfigStore.Load();
            Apps = new List<ManagedApp>();
            foreach (AppConfig a in Config.apps)
            {
                Apps.Add(new ManagedApp(this, a));
            }
            SystemLog.Append("[管家] 配置加载完成，共 " + Apps.Count + " 个应用");
        }

        public void Save()
        {
            lock (gate)
            {
                Config.apps = new List<AppConfig>();
                foreach (ManagedApp app in Apps) Config.apps.Add(app.Config);
                ConfigStore.Save(Config);
            }
        }

        public ManagedApp Find(string id)
        {
            foreach (ManagedApp app in Apps)
            {
                if (app.Id == id) return app;
            }
            return null;
        }

        public ManagedApp Add(AppConfig raw)
        {
            lock (gate)
            {
                ConfigStore.NormalizeApp(raw, null);
                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ManagedApp a in Apps) used.Add(a.Id);
                raw.id = ConfigStore.UniqueId(raw.name, used);
                ManagedApp app = new ManagedApp(this, raw);
                Apps.Add(app);
                Save();
                SystemLog.Append("[" + raw.name + "] 已添加");
                return app;
            }
        }

        public void Update(string id, AppConfig raw)
        {
            lock (gate)
            {
                ManagedApp app = Find(id);
                if (app == null) throw new ArgumentException("应用不存在");
                ConfigStore.NormalizeApp(raw, id);
                app.UpdateConfig(raw);
                Save();
                SystemLog.Append("[" + raw.name + "] 配置已更新");
            }
        }

        public void Remove(string id)
        {
            ManagedApp app;
            lock (gate)
            {
                app = Find(id);
                if (app == null) return;
            }
            string msg;
            app.Stop(out msg);
            lock (gate)
            {
                Apps.Remove(app);
                Save();
                SystemLog.Append("[" + app.Name + "] 已删除");
            }
        }

        public void StartAll()
        {
            foreach (ManagedApp app in Apps.ToList())
            {
                string msg;
                app.Start(true, out msg);
            }
        }

        public void StopAll()
        {
            shuttingDown = true;
            foreach (ManagedApp app in Apps.ToList())
            {
                try
                {
                    string msg;
                    app.Stop(out msg);
                }
                catch (Exception) { }
            }
        }

        public void Boot()
        {
            if (Config.startup_delay > 0)
            {
                SystemLog.Append("[管家] 开机启动序列：等待 " + Config.startup_delay + " 秒");
                Thread.Sleep(Config.startup_delay * 1000);
            }
            foreach (ManagedApp app in Apps.ToList())
            {
                if (shuttingDown) return;
                if (!app.Config.auto_start) continue;
                if (app.Config.start_delay > 0) Thread.Sleep(app.Config.start_delay * 1000);
                string msg;
                app.Start(true, out msg);
                SystemLog.Append("[管家] 自动启动 " + app.Name + "：" + msg);
            }
        }

        public static bool IsWebManagerRunning(int port, string host)
        {
            string h = host == "0.0.0.0" ? "127.0.0.1" : host;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://" + h + ":" + port + "/api/health");
                req.Timeout = 1200;
                req.ReadWriteTimeout = 1200;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    return resp.StatusCode == HttpStatusCode.OK && text.IndexOf("\"ok\"", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
