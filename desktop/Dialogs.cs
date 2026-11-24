using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    public static class Scheduler
    {
        private static string ScriptPath
        {
            get { return Path.Combine(Paths.RunDir, "_task.ps1"); }
        }

        public static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p =
                    new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void EnsureScript()
        {
            string[] lines = new string[]
            {
                "param(",
                "  [string]$Action = 'Install',",
                "  [string]$Mode = 'Logon',",
                "  [string]$Exe = '',",
                "  [string]$TaskName = 'AppManagerDesktop'",
                ")",
                "$ErrorActionPreference = 'Stop'",
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
                "$id = [Security.Principal.WindowsIdentity]::GetCurrent()",
                "$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
                "if ($Action -eq 'Remove') {",
                "  $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue",
                "  if (-not $t) { Write-Output 'OK: 任务不存在'; exit 0 }",
                "  try {",
                "    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop",
                "    Write-Output 'OK: 已移除计划任务'",
                "    exit 0",
                "  } catch {",
                "    Write-Output ('ERROR: ' + $_.Exception.Message)",
                "    exit 3",
                "  }",
                "}",
                "if ($Mode -eq 'Boot' -and -not $isAdmin) { Write-Output 'ERROR: NEED_ADMIN'; exit 4 }",
                "if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { Write-Output 'ERROR: 程序路径不存在'; exit 5 }",
                "$taskAction = New-ScheduledTaskAction -Execute $Exe -Argument '--minimized' -WorkingDirectory (Split-Path -Parent $Exe)",
                "if ($Mode -eq 'Boot') {",
                "  $trigger = New-ScheduledTaskTrigger -AtStartup",
                "  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest",
                "} else {",
                "  $user = $id.Name",
                "  if ($user -match '^(NT AUTHORITY|NT SERVICE)\\\\' -or $user -eq 'SYSTEM') {",
                "    $u = $null",
                "    try { $u = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName } catch { }",
                "    if ($u) { $user = $u } else { Write-Output 'ERROR: 无法确定登录用户'; exit 6 }",
                "  }",
                "  $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user",
                "  $pa = @{ UserId = $user; LogonType = 'Interactive' }",
                "  if ($isAdmin) { $pa['RunLevel'] = 'Highest' }",
                "  $principal = New-ScheduledTaskPrincipal @pa",
                "}",
                "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew",
                "Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null",
                "Write-Output ('OK: 已注册计划任务 ' + $TaskName)",
            };
            File.WriteAllText(ScriptPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
        }

        public static string RunPowerShell(string arguments, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception) { }
                    }
                    exitCode = p.HasExited ? p.ExitCode : -1;
                    return (output + "\r\n" + error).Trim();
                }
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        public static bool IsInstalled(out string state)
        {
            state = "";
            string cmd = "-NoLogo -NoProfile -Command \"$t=Get-ScheduledTask -TaskName '"
                + AppInfo.TaskName + "' -ErrorAction SilentlyContinue; if($t){'YES'}else{'NO'}\"";
            int code;
            string output = RunPowerShell(cmd, 20000, out code);
            bool installed = output.IndexOf("YES", StringComparison.OrdinalIgnoreCase) >= 0;
            state = output.Replace("\r", " ").Replace("\n", " ").Trim();
            return installed;
        }

        public static string RunScript(string action, string mode, bool elevated, out int exitCode)
        {
            EnsureScript();
            string arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath
                + "\" -Action " + action + " -Mode " + mode
                + " -Exe \"" + Application.ExecutablePath + "\" -TaskName " + AppInfo.TaskName;
            if (elevated)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", arguments);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    psi.WorkingDirectory = Paths.BaseDir;
                    Process.Start(psi);
                    exitCode = 0;
                    return "ELEVATED";
                }
                catch (Exception ex)
                {
                    exitCode = -1;
                    return "ERROR: " + ex.Message;
                }
            }
            return RunPowerShell(arguments, 40000, out exitCode);
        }
    }

    public class AppEditDialog : DarkForm
    {
        private TextBox txtName;
        private TextBox txtCommand;
        private TextBox txtCwd;
        private ComboBox cbShell;
        private NumericUpDown numDelay;
        private TextBox txtEnv;
        private CheckBox chkAutoStart;
        private CheckBox chkRestart;
        private Label lblError;
        private readonly AppConfig original;
        public AppConfig Result;

        public AppEditDialog(AppConfig existing)
        {
            original = existing == null ? null : existing.Clone();

            Text = existing == null ? "添加应用" : "编辑应用";
            ClientSize = new Size(660, 566);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.None;

            Label title = new Label();
            title.Text = Text;
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.Location = new Point(22, 16);
            Controls.Add(title);

            AddLabel("应用名称", 22, 56);
            txtName = MakeText(22, 74, 616, false);
            txtName.Text = existing == null ? "" : existing.name;
            txtName.MaxLength = 60;
            Controls.Add(txtName);

            AddLabel("启动命令（前台运行，日志会实时显示）", 22, 110);
            txtCommand = MakeText(22, 128, 616, true);
            txtCommand.Height = 76;
            txtCommand.ScrollBars = ScrollBars.Vertical;
            txtCommand.Text = existing == null ? "" : existing.command;
            Controls.Add(txtCommand);

            AddLabel("工作目录（留空则使用程序所在目录）", 22, 216);
            txtCwd = MakeText(22, 234, 522, false);
            txtCwd.Text = existing == null ? "" : existing.cwd;
            Controls.Add(txtCwd);

            Button browse = Theme.GhostButton("浏览…", 84);
            browse.Location = new Point(554, 233);
            browse.Height = 27;
            browse.Font = Theme.Small;
            browse.Click += delegate
            {
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    fb.Description = "选择应用的工作目录";
                    if (Directory.Exists(txtCwd.Text)) fb.SelectedPath = txtCwd.Text;
                    if (fb.ShowDialog(this) == DialogResult.OK) txtCwd.Text = fb.SelectedPath;
                }
            };
            Controls.Add(browse);

            AddLabel("启动方式", 22, 272);
            cbShell = new ComboBox();
            cbShell.DropDownStyle = ComboBoxStyle.DropDownList;
            cbShell.FlatStyle = FlatStyle.Flat;
            cbShell.BackColor = Color.FromArgb(14, 20, 27);
            cbShell.ForeColor = Theme.Text;
            cbShell.Location = new Point(22, 290);
            cbShell.Width = 200;
            cbShell.Items.Add("PowerShell（推荐）");
            cbShell.Items.Add("命令提示符 (cmd)");
            cbShell.SelectedIndex = (existing != null && existing.shell == "cmd") ? 1 : 0;
            Controls.Add(cbShell);

            AddLabel("启动延迟（秒）", 250, 272);
            numDelay = new NumericUpDown();
            numDelay.Minimum = 0;
            numDelay.Maximum = 600;
            numDelay.Value = existing == null ? 0 : Math.Max(0, Math.Min(600, existing.start_delay));
            numDelay.Location = new Point(250, 290);
            numDelay.Width = 100;
            numDelay.BackColor = Color.FromArgb(14, 20, 27);
            numDelay.ForeColor = Theme.Text;
            numDelay.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(numDelay);

            AddLabel("环境变量（每行一个 KEY=VALUE）", 22, 330);
            txtEnv = MakeText(22, 348, 616, true);
            txtEnv.Height = 64;
            txtEnv.ScrollBars = ScrollBars.Vertical;
            if (existing != null && existing.env != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in existing.env)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                txtEnv.Text = sb.ToString().TrimEnd();
            }
            Controls.Add(txtEnv);

            chkAutoStart = MakeCheck("开机后自动启动", 22, 426);
            chkAutoStart.Checked = existing == null || existing.auto_start;
            Controls.Add(chkAutoStart);

            chkRestart = MakeCheck("异常退出后自动重启", 22, 452);
            chkRestart.Checked = existing == null || existing.restart_on_crash;
            Controls.Add(chkRestart);

            lblError = new Label();
            lblError.ForeColor = Theme.Danger;
            lblError.Font = Theme.Small;
            lblError.AutoSize = false;
            lblError.Location = new Point(22, 482);
            lblError.Size = new Size(616, 30);
            Controls.Add(lblError);

            Button cancel = Theme.GhostButton("取消", 84);
            cancel.Location = new Point(460, 518);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            Button save = Theme.PrimaryButton("保存", 94);
            save.Location = new Point(552, 518);
            save.Click += delegate { SaveApp(); };
            Controls.Add(save);

            AcceptButton = null;
            CancelButton = cancel;
            ActiveControl = txtName;
        }

        private void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Theme.Muted;
            l.Font = Theme.Small;
            l.Location = new Point(x, y);
            Controls.Add(l);
        }

        private TextBox MakeText(int x, int y, int width, bool multiline)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Width = width;
            t.Multiline = multiline;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Color.FromArgb(14, 20, 27);
            t.ForeColor = Theme.Text;
            t.Font = multiline ? Theme.Mono : Theme.Base;
            return t;
        }

        private CheckBox MakeCheck(string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.Font = Theme.Base;
            c.FlatStyle = FlatStyle.Flat;
            c.Location = new Point(x, y);
            return c;
        }

        public static Dictionary<string, string> ParseEnv(string text)
        {
            Dictionary<string, string> env = new Dictionary<string, string>();
            string[] lines = (text ?? "").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) throw new ArgumentException("环境变量格式应为 KEY=VALUE：" + line);
                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();
                if (!Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_.-]*$"))
                    throw new ArgumentException("环境变量名不合法：" + key);
                env[key] = val;
            }
            return env;
        }

        private void SaveApp()
        {
            lblError.Text = "";
            try
            {
                AppConfig cfg = new AppConfig();
                cfg.id = original == null ? "" : original.id;
                cfg.name = txtName.Text.Trim();
                cfg.command = txtCommand.Text.Trim();
                cfg.cwd = txtCwd.Text.Trim();
                cfg.shell = cbShell.SelectedIndex == 1 ? "cmd" : "powershell";
                cfg.env = ParseEnv(txtEnv.Text);
                cfg.auto_start = chkAutoStart.Checked;
                cfg.restart_on_crash = chkRestart.Checked;
                cfg.start_delay = (int)numDelay.Value;
                cfg.max_restarts = original == null ? 10 : original.max_restarts;
                cfg.max_log_lines = original == null ? 2000 : original.max_log_lines;
                ConfigStore.NormalizeApp(cfg, original == null ? null : original.id);
                Result = cfg;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                lblError.Text = ex.Message;
            }
        }
    }

    public class SettingsDialog : DarkForm
    {
        private readonly Supervisor svc;
        private NumericUpDown numStartup;
        private TextBox txtExtraPath;
        private TextBox txtEnv;
        private Label lblError;

        public SettingsDialog(Supervisor supervisor)
        {
            svc = supervisor;
            Text = "设置";
            ClientSize = new Size(620, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.None;

            Label title = new Label();
            title.Text = "设置";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(22, 16);
            Controls.Add(title);

            Label l1 = new Label();
            l1.Text = "开机启动等待（秒）：先等一会儿再拉起应用，避免网络或磁盘尚未就绪";
            l1.AutoSize = true;
            l1.ForeColor = Theme.Muted;
            l1.Font = Theme.Small;
            l1.Location = new Point(22, 60);
            Controls.Add(l1);
            numStartup = new NumericUpDown();
            numStartup.Minimum = 0;
            numStartup.Maximum = 3600;
            numStartup.Value = Math.Max(0, Math.Min(3600, svc.Config.startup_delay));
            numStartup.Location = new Point(22, 80);
            numStartup.Width = 110;
            numStartup.BackColor = Color.FromArgb(14, 20, 27);
            numStartup.ForeColor = Theme.Text;
            numStartup.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(numStartup);

            Label l2 = new Label();
            l2.Text = "额外 PATH（找不到 node / npm 时，把它们的目录填在这里）";
            l2.AutoSize = true;
            l2.ForeColor = Theme.Muted;
            l2.Font = Theme.Small;
            l2.Location = new Point(22, 122);
            Controls.Add(l2);
            txtExtraPath = new TextBox();
            txtExtraPath.Location = new Point(22, 142);
            txtExtraPath.Width = 570;
            txtExtraPath.BorderStyle = BorderStyle.FixedSingle;
            txtExtraPath.BackColor = Color.FromArgb(14, 20, 27);
            txtExtraPath.ForeColor = Theme.Text;
            txtExtraPath.Font = Theme.MonoSmall;
            txtExtraPath.Text = svc.Config.extra_path;
            Controls.Add(txtExtraPath);

            Label l3 = new Label();
            l3.Text = "全局环境变量（每行一个 KEY=VALUE，作用于所有应用）";
            l3.AutoSize = true;
            l3.ForeColor = Theme.Muted;
            l3.Font = Theme.Small;
            l3.Location = new Point(22, 184);
            Controls.Add(l3);
            txtEnv = new TextBox();
            txtEnv.Location = new Point(22, 204);
            txtEnv.Width = 570;
            txtEnv.Height = 90;
            txtEnv.Multiline = true;
            txtEnv.ScrollBars = ScrollBars.Vertical;
            txtEnv.BorderStyle = BorderStyle.FixedSingle;
            txtEnv.BackColor = Color.FromArgb(14, 20, 27);
            txtEnv.ForeColor = Theme.Text;
            txtEnv.Font = Theme.MonoSmall;
            StringBuilder sb = new StringBuilder();
            if (svc.Config.env != null)
            {
                foreach (KeyValuePair<string, string> kv in svc.Config.env)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            }
            txtEnv.Text = sb.ToString().TrimEnd();
            Controls.Add(txtEnv);

            Label info = new Label();
            info.Text = "配置文件：" + Paths.ConfigPath + "\r\n程序位置：" + Application.ExecutablePath +
                        "\r\n版本：" + AppInfo.Version + "（原生桌面版，无需浏览器）";
            info.AutoSize = false;
            info.Size = new Size(570, 70);
            info.Location = new Point(22, 310);
            info.ForeColor = Theme.Dim;
            info.Font = Theme.Small;
            Controls.Add(info);

            LinkLabel openDir = new LinkLabel();
            openDir.Text = "打开程序目录";
            openDir.AutoSize = true;
            openDir.LinkColor = Theme.Muted;
            openDir.ActiveLinkColor = Theme.Accent;
            openDir.LinkBehavior = LinkBehavior.HoverUnderline;
            openDir.Font = Theme.Small;
            openDir.Location = new Point(22, 386);
            openDir.LinkClicked += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Paths.BaseDir + "\""); }
                catch (Exception) { }
            };
            Controls.Add(openDir);

            lblError = new Label();
            lblError.ForeColor = Theme.Danger;
            lblError.Font = Theme.Small;
            lblError.AutoSize = false;
            lblError.Location = new Point(22, 414);
            lblError.Size = new Size(570, 40);
            Controls.Add(lblError);

            Button cancel = Theme.GhostButton("取消", 84);
            cancel.Location = new Point(420, 470);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            Button save = Theme.PrimaryButton("保存设置", 104);
            save.Location = new Point(512, 470);
            save.Click += delegate { SaveSettings(); };
            Controls.Add(save);

            CancelButton = cancel;
        }

        private void SaveSettings()
        {
            lblError.Text = "";
            try
            {
                Dictionary<string, string> env = AppEditDialog.ParseEnv(txtEnv.Text);
                svc.Config.startup_delay = (int)numStartup.Value;
                svc.Config.extra_path = txtExtraPath.Text.Trim();
                svc.Config.env = env;
                svc.Save();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                lblError.Text = ex.Message;
            }
        }
    }

    public class AutostartDialog : DarkForm
    {
        private Label lblStatus;
        private RadioButton rbBoot;
        private RadioButton rbLogon;
        private TextBox txtOutput;
        private Button btnInstall;
        private Button btnRemove;
        private System.Windows.Forms.Timer pollTimer;
        private int pollCount;

        public AutostartDialog()
        {
            Text = "开机自启";
            ClientSize = new Size(600, 540);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.None;

            Label title = new Label();
            title.Text = "开机自启";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(22, 16);
            Controls.Add(title);

            lblStatus = new Label();
            lblStatus.Text = "● 检测中…";
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lblStatus.ForeColor = Theme.Dim;
            lblStatus.Location = new Point(22, 62);
            Controls.Add(lblStatus);

            Label desc = new Label();
            desc.Text = "开启后会注册一个名为 " + AppInfo.TaskName + " 的 Windows 计划任务，\r\n开机后自动在托盘运行，并拉起所有勾选了“开机后自动启动”的应用。";
            desc.AutoSize = false;
            desc.Size = new Size(556, 44);
            desc.ForeColor = Theme.Muted;
            desc.Font = Theme.Small;
            desc.Location = new Point(22, 88);
            Controls.Add(desc);

            rbBoot = new RadioButton();
            rbBoot.Text = "开机时启动（推荐，需要管理员一次）";
            rbBoot.AutoSize = true;
            rbBoot.ForeColor = Theme.Text;
            rbBoot.Font = Theme.Bold;
            rbBoot.Location = new Point(22, 146);
            rbBoot.Checked = true;
            Controls.Add(rbBoot);

            Label b1 = new Label();
            b1.Text = "以 SYSTEM 账户运行，不需要登录 Windows，适合服务器。";
            b1.AutoSize = true;
            b1.ForeColor = Theme.Dim;
            b1.Font = Theme.Small;
            b1.Location = new Point(42, 168);
            Controls.Add(b1);

            rbLogon = new RadioButton();
            rbLogon.Text = "用户登录后启动（无需管理员）";
            rbLogon.AutoSize = true;
            rbLogon.ForeColor = Theme.Text;
            rbLogon.Font = Theme.Bold;
            rbLogon.Location = new Point(22, 200);
            Controls.Add(rbLogon);

            Label b2 = new Label();
            b2.Text = "使用当前用户的环境变量，适合依赖用户级 PATH / 配置的项目。";
            b2.AutoSize = true;
            b2.ForeColor = Theme.Dim;
            b2.Font = Theme.Small;
            b2.Location = new Point(42, 222);
            Controls.Add(b2);

            btnInstall = Theme.PrimaryButton("开启开机自启", 116);
            btnInstall.Location = new Point(22, 258);
            btnInstall.Click += delegate { Install(); };
            Controls.Add(btnInstall);

            btnRemove = Theme.DangerButton("移除自启", 98);
            btnRemove.Location = new Point(148, 258);
            btnRemove.Click += delegate { Remove(); };
            Controls.Add(btnRemove);

            Button refresh = Theme.GhostButton("刷新状态", 92);
            refresh.Location = new Point(256, 258);
            refresh.Click += delegate { RefreshStatusAsync(); };
            Controls.Add(refresh);

            txtOutput = new TextBox();
            txtOutput.Location = new Point(22, 304);
            txtOutput.Size = new Size(556, 140);
            txtOutput.Multiline = true;
            txtOutput.ReadOnly = true;
            txtOutput.ScrollBars = ScrollBars.Vertical;
            txtOutput.BorderStyle = BorderStyle.FixedSingle;
            txtOutput.BackColor = Color.FromArgb(8, 11, 15);
            txtOutput.ForeColor = Theme.Muted;
            txtOutput.Font = Theme.MonoSmall;
            Controls.Add(txtOutput);

            Label note = new Label();
            note.Text = "“登录后启动”普通权限即可安装；“开机时启动”会弹出一次 Windows 管理员授权。\r\n移除自启不会停止正在运行的程序。";
            note.AutoSize = false;
            note.Size = new Size(556, 36);
            note.ForeColor = Theme.Dim;
            note.Font = Theme.Small;
            note.Location = new Point(22, 454);
            Controls.Add(note);

            Button close = Theme.GhostButton("关闭", 84);
            close.Location = new Point(494, 494);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;

            Shown += delegate { RefreshStatusAsync(); };
            FormClosed += delegate
            {
                if (pollTimer != null) pollTimer.Stop();
            };
        }

        private void SetStatus(bool installed, string detail)
        {
            if (installed)
            {
                lblStatus.Text = "● 已开启";
                lblStatus.ForeColor = Theme.Accent;
                btnRemove.Enabled = true;
            }
            else
            {
                lblStatus.Text = "● 未开启";
                lblStatus.ForeColor = Theme.Dim;
                btnRemove.Enabled = false;
            }
            if (!string.IsNullOrEmpty(detail)) txtOutput.Text = detail;
        }

        private void RefreshStatusAsync()
        {
            btnInstall.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string detail;
                bool installed = Scheduler.IsInstalled(out detail);
                SafeInvoke(delegate
                {
                    btnInstall.Enabled = true;
                    SetStatus(installed, null);
                });
            });
        }

        private void Install()
        {
            bool boot = rbBoot.Checked;
            if (boot && !Scheduler.IsAdmin())
            {
                DialogResult r = MessageBox.Show(this,
                    "“开机时启动”需要管理员权限。\r\nWindows 会弹出一次授权窗口，是否继续？",
                    "应用管家", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                int code;
                string output = Scheduler.RunScript("Install", "Boot", true, out code);
                txtOutput.Text = output == "ELEVATED"
                    ? "已请求管理员授权。请在 UAC 窗口中选择“是”，任务会在几秒内注册完成。"
                    : output;
                if (output == "ELEVATED") StartPolling();
                return;
            }

            btnInstall.Enabled = false;
            btnRemove.Enabled = false;
            string mode = boot ? "Boot" : "Logon";
            ThreadPool.QueueUserWorkItem(delegate
            {
                int code;
                string output = Scheduler.RunScript("Install", mode, false, out code);
                bool ok = output.IndexOf("OK:", StringComparison.OrdinalIgnoreCase) >= 0;
                SafeInvoke(delegate
                {
                    btnInstall.Enabled = true;
                    txtOutput.Text = output;
                    SetStatus(ok, null);
                });
            });
        }

        private void Remove()
        {
            btnInstall.Enabled = false;
            btnRemove.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int code;
                string output = Scheduler.RunScript("Remove", "Logon", false, out code);
                bool ok = output.IndexOf("OK:", StringComparison.OrdinalIgnoreCase) >= 0;
                SafeInvoke(delegate
                {
                    btnInstall.Enabled = true;
                    txtOutput.Text = output + (ok ? "" : "\r\n如果提示权限不足，请重新以管理员身份运行本程序后再移除。");
                    SetStatus(!ok, null);
                });
            });
        }

        private void StartPolling()
        {
            pollCount = 0;
            if (pollTimer != null) pollTimer.Stop();
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 2000;
            pollTimer.Tick += delegate
            {
                pollCount++;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string detail;
                    bool installed = Scheduler.IsInstalled(out detail);
                    if (installed)
                    {
                        SafeInvoke(delegate
                        {
                            pollTimer.Stop();
                            SetStatus(true, "已注册计划任务 " + AppInfo.TaskName);
                        });
                    }
                });
                if (pollCount > 10)
                {
                    pollTimer.Stop();
                    SafeInvoke(delegate { txtOutput.Text += "\r\n如果已授权但状态仍未更新，请点“刷新状态”。"; });
                }
            };
            pollTimer.Start();
        }

        private void SafeInvoke(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
    }

    public class SystemLogDialog : DarkForm
    {
        private readonly Supervisor svc;
        private TextBox txt;
        private System.Windows.Forms.Timer timer;
        private long lastSeq;

        public SystemLogDialog(Supervisor supervisor)
        {
            svc = supervisor;
            Text = "服务日志";
            ClientSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.None;

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 50;
            head.BackColor = Theme.Panel;
            Label title = new Label();
            title.Text = "服务日志";
            title.Font = Theme.Bold;
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.Location = new Point(16, 15);
            head.Controls.Add(title);
            Controls.Add(head);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.BackColor = Theme.Panel;
            Button openFile = Theme.GhostButton("打开日志文件", 104);
            openFile.Location = new Point(16, 12);
            openFile.Click += delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + Path.Combine(Paths.LogsDir, "_manager.log") + "\""); }
                catch (Exception) { }
            };
            bottom.Controls.Add(openFile);
            Button close = Theme.GhostButton("关闭", 84);
            close.Location = new Point(792, 12);
            close.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(close);
            CancelButton = close;
            Controls.Add(bottom);

            txt = new TextBox();
            txt.Dock = DockStyle.Fill;
            txt.Multiline = true;
            txt.ReadOnly = true;
            txt.ScrollBars = ScrollBars.Vertical;
            txt.BorderStyle = BorderStyle.None;
            txt.BackColor = Theme.LogBg;
            txt.ForeColor = Theme.LogText;
            txt.Font = Theme.Mono;
            txt.WordWrap = false;
            Panel pad = new Panel();
            pad.Dock = DockStyle.Fill;
            pad.Padding = new Padding(14, 10, 10, 10);
            pad.BackColor = Theme.LogBg;
            pad.Controls.Add(txt);
            Controls.Add(pad);
            pad.BringToFront();
            head.BringToFront();
            bottom.BringToFront();

            Load += delegate { LoadLines(); };
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { LoadLines(); };
            timer.Start();
            FormClosed += delegate { timer.Stop(); };
        }

        private void LoadLines()
        {
            List<LogLine> lines = svc.SystemLog.ReadAfter(lastSeq, 400);
            if (lines.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            foreach (LogLine l in lines)
            {
                sb.Append(l.Time.ToString("HH:mm:ss")).Append("  ").Append(l.Text).Append("\r\n");
                lastSeq = l.Seq;
            }
            txt.AppendText(sb.ToString());
            txt.SelectionStart = txt.TextLength;
            txt.ScrollToCaret();
        }
    }

    public static class Program
    {
        private static Mutex singleInstance;

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string config = null;
            bool minimized = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) config = args[++i];
                else if (args[i] == "--minimized") minimized = true;
            }

            Paths.Init(config);
            bool created;
            singleInstance = new Mutex(true, "AppManagerDesktop_SingleInstance", out created);
            if (!created)
            {
                if (!minimized)
                    MessageBox.Show("应用管家已经在运行，请查看系统托盘。", "应用管家",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("发生了一个错误：" + e.Exception.Message, "应用管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Supervisor svc = new Supervisor();
            try
            {
                svc.Load();
            }
            catch (Exception ex)
            {
                MessageBox.Show("配置读取失败：\r\n" + ex.Message, "应用管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (Supervisor.IsWebManagerRunning(svc.Config.port, svc.Config.host))
            {
                MessageBox.Show(
                    "检测到 Web 版应用管家正在运行（端口 " + svc.Config.port + "）。\r\n" +
                    "请先在 Web 版中停止管理服务，再打开桌面版，避免重复启动同一批应用。",
                    "应用管家", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Application.Run(new MainForm(svc, minimized));
        }
    }
}
