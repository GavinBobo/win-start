using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(11, 15, 20);
        public static readonly Color Sidebar = Color.FromArgb(14, 20, 27);
        public static readonly Color TopBar = Color.FromArgb(18, 25, 36);
        public static readonly Color Panel = Color.FromArgb(19, 26, 35);
        public static readonly Color Panel3 = Color.FromArgb(30, 40, 53);
        public static readonly Color Line = Color.FromArgb(36, 48, 64);
        public static readonly Color Text = Color.FromArgb(232, 238, 246);
        public static readonly Color Muted = Color.FromArgb(147, 163, 184);
        public static readonly Color Dim = Color.FromArgb(100, 116, 139);
        public static readonly Color Accent = Color.FromArgb(45, 212, 167);
        public static readonly Color AccentDark = Color.FromArgb(6, 35, 27);
        public static readonly Color Danger = Color.FromArgb(248, 113, 113);
        public static readonly Color DangerDark = Color.FromArgb(58, 24, 28);
        public static readonly Color Warn = Color.FromArgb(251, 191, 36);
        public static readonly Color LogBg = Color.FromArgb(8, 11, 15);
        public static readonly Color LogText = Color.FromArgb(200, 211, 225);

        public static readonly Font Base = new Font("Microsoft YaHei UI", 9f);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8.25f);
        public static readonly Font Bold = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        public static readonly Font Title = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        public static readonly Font Brand = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9.5f);
        public static readonly Font MonoSmall = new Font("Consolas", 8.5f);

        public static Button MakeButton(string text, int width, Color back, Color fore, Color border)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 32;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = border;
            b.FlatAppearance.MouseOverBackColor = Mix(back, Color.White, 0.08f);
            b.FlatAppearance.MouseDownBackColor = Mix(back, Color.Black, 0.10f);
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = Bold;
            b.Cursor = Cursors.Hand;
            b.TabStop = false;
            b.UseVisualStyleBackColor = false;
            b.EnabledChanged += delegate
            {
                if (b.Enabled)
                {
                    b.BackColor = back;
                    b.ForeColor = fore;
                    b.FlatAppearance.BorderColor = border;
                }
                else
                {
                    b.BackColor = Mix(back, Bg, 0.58f);
                    b.ForeColor = Color.FromArgb(108, 120, 136);
                    b.FlatAppearance.BorderColor = Mix(border, Bg, 0.55f);
                }
            };
            return b;
        }

        public static Button PrimaryButton(string text, int width)
        {
            return MakeButton(text, width, Accent, AccentDark, Accent);
        }

        public static Button GhostButton(string text, int width)
        {
            return MakeButton(text, width, Panel3, Text, Line);
        }

        public static Button DangerButton(string text, int width)
        {
            return MakeButton(text, width, DangerDark, Danger, Color.FromArgb(120, 60, 64));
        }

        public static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        public static Color StatusColor(string status)
        {
            if (status == "running") return Accent;
            if (status == "crashed") return Danger;
            if (status == "starting" || status == "stopping") return Warn;
            return Color.FromArgb(100, 116, 139);
        }

        public static string StatusText(string status, bool restartPending)
        {
            if (status == "running") return "运行中";
            if (status == "crashed") return restartPending ? "等待自动重启" : "已崩溃";
            if (status == "starting") return "启动中";
            if (status == "stopping") return "停止中";
            return "已停止";
        }

        public static Icon LoadIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch (Exception) { }
            return SystemIcons.Application;
        }
    }

    public class DarkForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(Handle, 20, ref enabled, 4);
            }
            catch (Exception) { }
        }
    }

    public class MainForm : DarkForm
    {
        private readonly Supervisor svc;
        private readonly bool startMinimized;

        private ListBox appList;
        private List<AppSnapshot> listData = new List<AppSnapshot>();
        private string listSignature = "";
        private bool suppressSelect;

        private Panel appView;
        private Panel emptyView;
        private Label lblCount;
        private Label lblName;
        private Label lblChip;
        private Label lblUptime;
        private Label lblError;
        private Label lblCommand;
        private Label lblCwd;
        private Label lblShell;
        private Label lblPid;
        private Label lblExit;
        private Label lblAutoStart;
        private Label lblAutoRestart;
        private Label lblRestarts;
        private Label lblLogCount;

        private TextBox txtLog;
        private TextBox txtFilter;
        private CheckBox chkAutoScroll;
        private CheckBox chkWrap;

        private Button btnStart;
        private Button btnStop;
        private Button btnRestart;
        private Button btnEdit;
        private Button btnDelete;
        private Button btnTopStart;
        private Button btnTopStop;
        private Button btnTopAuto;

        private System.Windows.Forms.Timer uiTimer;
        private NotifyIcon tray;
        private bool exiting;
        private bool busy;
        private bool trayHintShown;

        private string selectedId;
        private long lastSeq;
        private List<LogLine> uiLines = new List<LogLine>();
        private ToolTip tips;

        public MainForm(Supervisor supervisor, bool minimized)
        {
            svc = supervisor;
            startMinimized = minimized;

            Text = "应用管家";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(
                Math.Max(980, Math.Min(1200, wa.Width - 80)),
                Math.Max(620, Math.Min(740, wa.Height - 80)));
            MinimumSize = new Size(1000, 640);
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            try { Icon = Theme.LoadIcon(); }
            catch (Exception) { }

            BuildUi();
            BuildTray();
            tips = new ToolTip();
            tips.AutoPopDelay = 15000;
            tips.InitialDelay = 500;

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 800;
            uiTimer.Tick += delegate { TickUi(); };
            uiTimer.Start();

            SelectFirstApp();

            Thread boot = new Thread(delegate () { svc.Boot(); });
            boot.IsBackground = true;
            boot.Start();

            Shown += delegate
            {
                if (startMinimized)
                {
                    HideToTray(false);
                }
            };
        }

        // ---------------------------------------------------------------- UI
        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.BackColor = Theme.Bg;
            root.Margin = new Padding(0);
            root.Padding = new Padding(0);

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.Margin = new Padding(0);
            body.Padding = new Padding(0);

            root.Controls.Add(BuildTopBar(), 0, 0);
            root.Controls.Add(body, 0, 1);
            body.Controls.Add(BuildSidebar(), 0, 0);
            body.Controls.Add(BuildDetail(), 1, 0);
            Controls.Add(root);
        }

        private Panel BuildTopBar()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Fill;
            bar.BackColor = Theme.TopBar;
            bar.Margin = new Padding(0);
            bar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };

            PictureBox logo = new PictureBox();
            logo.Size = new Size(34, 34);
            logo.Location = new Point(16, 14);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try
            {
                using (Icon ico = Theme.LoadIcon()) logo.Image = ico.ToBitmap();
            }
            catch (Exception) { }
            bar.Controls.Add(logo);

            Label title = new Label();
            title.Text = "应用管家";
            title.AutoSize = true;
            title.Font = Theme.Brand;
            title.ForeColor = Theme.Text;
            title.Location = new Point(60, 9);
            bar.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "DESKTOP";
            sub.AutoSize = true;
            sub.Font = Theme.Small;
            sub.ForeColor = Theme.Dim;
            sub.Location = new Point(61, 33);
            bar.Controls.Add(sub);

            Label local = new Label();
            local.Text = "●  本地服务运行中";
            local.AutoSize = true;
            local.Font = Theme.Small;
            local.ForeColor = Theme.Accent;
            local.Location = new Point(196, 22);
            bar.Controls.Add(local);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Right;
            buttons.Width = 440;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 14, 14, 0);
            buttons.BackColor = Color.Transparent;

            Button settings = Theme.GhostButton("设置", 60);
            settings.Margin = new Padding(6, 0, 0, 0);
            settings.Click += delegate { OpenSettings(); };
            buttons.Controls.Add(settings);

            btnTopAuto = Theme.GhostButton("开机自启", 88);
            btnTopAuto.Margin = new Padding(6, 0, 0, 0);
            btnTopAuto.Click += delegate { OpenAutostart(); };
            buttons.Controls.Add(btnTopAuto);

            btnTopStop = Theme.GhostButton("全部停止", 84);
            btnTopStop.Margin = new Padding(6, 0, 0, 0);
            btnTopStop.Click += delegate { DoAll(false); };
            buttons.Controls.Add(btnTopStop);

            btnTopStart = Theme.GhostButton("全部启动", 84);
            btnTopStart.Margin = new Padding(6, 0, 0, 0);
            btnTopStart.Click += delegate { DoAll(true); };
            buttons.Controls.Add(btnTopStart);

            bar.Controls.Add(buttons);
            return bar;
        }

        private Panel BuildSidebar()
        {
            Panel side = new Panel();
            side.Dock = DockStyle.Fill;
            side.BackColor = Theme.Sidebar;
            side.Margin = new Padding(0);

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 58;
            head.BackColor = Theme.Sidebar;

            Label caption = new Label();
            caption.Text = "应用";
            caption.AutoSize = true;
            caption.Font = Theme.Bold;
            caption.ForeColor = Theme.Muted;
            caption.Location = new Point(16, 19);
            head.Controls.Add(caption);

            lblCount = new Label();
            lblCount.Text = "0";
            lblCount.AutoSize = true;
            lblCount.Font = Theme.Small;
            lblCount.ForeColor = Theme.Dim;
            lblCount.Location = new Point(54, 22);
            head.Controls.Add(lblCount);

            Button add = Theme.PrimaryButton("+ 添加", 78);
            add.Size = new Size(78, 30);
            add.Location = new Point(198, 13);
            add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            add.Click += delegate { AddApp(); };
            head.Controls.Add(add);

            Panel listWrap = new Panel();
            listWrap.Dock = DockStyle.Fill;
            listWrap.Padding = new Padding(10, 0, 10, 10);
            listWrap.BackColor = Theme.Sidebar;

            appList = new ListBox();
            appList.Dock = DockStyle.Fill;
            appList.BorderStyle = BorderStyle.None;
            appList.DrawMode = DrawMode.OwnerDrawFixed;
            appList.ItemHeight = 62;
            appList.IntegralHeight = false;
            appList.BackColor = Theme.Sidebar;
            appList.ForeColor = Theme.Text;
            appList.Font = Theme.Base;
            appList.DrawItem += AppListDrawItem;
            appList.SelectedIndexChanged += delegate
            {
                if (suppressSelect) return;
                if (appList.SelectedIndex < 0 || appList.SelectedIndex >= listData.Count) return;
                SelectApp(listData[appList.SelectedIndex].Id);
            };
            listWrap.Controls.Add(appList);

            Panel foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = 46;
            foot.BackColor = Theme.Sidebar;
            foot.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, 0, foot.Width, 0);
            };

            LinkLabel sysLog = new LinkLabel();
            sysLog.Text = "服务日志";
            sysLog.AutoSize = true;
            sysLog.LinkColor = Theme.Muted;
            sysLog.ActiveLinkColor = Theme.Accent;
            sysLog.VisitedLinkColor = Theme.Muted;
            sysLog.LinkBehavior = LinkBehavior.HoverUnderline;
            sysLog.Font = Theme.Small;
            sysLog.Location = new Point(16, 14);
            sysLog.LinkClicked += delegate { ShowSystemLog(); };
            foot.Controls.Add(sysLog);

            LinkLabel openLogs = new LinkLabel();
            openLogs.Text = "打开日志目录";
            openLogs.AutoSize = true;
            openLogs.LinkColor = Theme.Dim;
            openLogs.ActiveLinkColor = Theme.Accent;
            openLogs.VisitedLinkColor = Theme.Dim;
            openLogs.LinkBehavior = LinkBehavior.HoverUnderline;
            openLogs.Font = Theme.Small;
            openLogs.Location = new Point(158, 14);
            openLogs.LinkClicked += delegate { OpenPath(Paths.LogsDir); };
            foot.Controls.Add(openLogs);

            Label ver = new Label();
            ver.Text = "v" + AppInfo.Version;
            ver.AutoSize = true;
            ver.ForeColor = Color.FromArgb(70, 86, 106);
            ver.Font = Theme.Small;
            ver.Location = new Point(248, 16);
            foot.Controls.Add(ver);

            side.Controls.Add(listWrap);
            side.Controls.Add(head);
            side.Controls.Add(foot);
            return side;
        }

        private Panel BuildDetail()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Theme.Bg;
            host.Padding = new Padding(18, 14, 18, 18);
            host.Margin = new Padding(0);

            appView = new Panel();
            appView.Dock = DockStyle.Fill;
            appView.BackColor = Theme.Bg;

            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.ColumnCount = 1;
            right.RowCount = 4;
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.Margin = new Padding(0);
            right.Padding = new Padding(0);

            right.Controls.Add(BuildAppHeader(), 0, 0);
            right.Controls.Add(BuildMeta(), 0, 1);

            lblError = new Label();
            lblError.Dock = DockStyle.Fill;
            lblError.TextAlign = ContentAlignment.MiddleLeft;
            lblError.ForeColor = Theme.Danger;
            lblError.Font = Theme.Small;
            lblError.AutoEllipsis = true;
            lblError.Visible = false;
            lblError.Margin = new Padding(0);
            right.Controls.Add(lblError, 0, 2);

            right.Controls.Add(BuildLogPanel(), 0, 3);
            appView.Controls.Add(right);

            emptyView = BuildEmptyView();
            host.Controls.Add(appView);
            host.Controls.Add(emptyView);
            return host;
        }

        private Panel BuildAppHeader()
        {
            Panel head = new Panel();
            head.Dock = DockStyle.Fill;
            head.BackColor = Theme.Bg;
            head.Margin = new Padding(0);

            FlowLayoutPanel titleFlow = new FlowLayoutPanel();
            titleFlow.Dock = DockStyle.Fill;
            titleFlow.WrapContents = false;
            titleFlow.AutoSize = false;
            titleFlow.Padding = new Padding(0, 8, 0, 0);
            titleFlow.BackColor = Color.Transparent;

            lblName = new Label();
            lblName.Text = "—";
            lblName.AutoSize = true;
            lblName.Font = Theme.Title;
            lblName.ForeColor = Theme.Text;
            lblName.Margin = new Padding(0, 0, 12, 0);
            titleFlow.Controls.Add(lblName);

            lblChip = new Label();
            lblChip.Text = "● 已停止";
            lblChip.AutoSize = true;
            lblChip.Font = Theme.Bold;
            lblChip.ForeColor = Theme.Dim;
            lblChip.Margin = new Padding(0, 10, 10, 0);
            titleFlow.Controls.Add(lblChip);

            lblUptime = new Label();
            lblUptime.Text = "";
            lblUptime.AutoSize = true;
            lblUptime.Font = Theme.Small;
            lblUptime.ForeColor = Theme.Dim;
            lblUptime.Margin = new Padding(0, 12, 0, 0);
            titleFlow.Controls.Add(lblUptime);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Right;
            actions.Width = 424;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 10, 0, 0);
            actions.BackColor = Color.Transparent;

            btnStop = Theme.DangerButton("停止", 62);
            btnStop.Margin = new Padding(6, 0, 0, 0);
            btnStop.Click += delegate { DoAction("stop"); };
            actions.Controls.Add(btnStop);

            btnRestart = Theme.GhostButton("重启", 62);
            btnRestart.Margin = new Padding(6, 0, 0, 0);
            btnRestart.Click += delegate { DoAction("restart"); };
            actions.Controls.Add(btnRestart);

            btnStart = Theme.PrimaryButton("启动", 62);
            btnStart.Margin = new Padding(6, 0, 0, 0);
            btnStart.Click += delegate { DoAction("start"); };
            actions.Controls.Add(btnStart);

            btnDelete = Theme.GhostButton("删除", 62);
            btnDelete.ForeColor = Color.FromArgb(252, 165, 165);
            btnDelete.Margin = new Padding(14, 0, 0, 0);
            btnDelete.Click += delegate { DeleteApp(); };
            actions.Controls.Add(btnDelete);

            btnEdit = Theme.GhostButton("编辑", 62);
            btnEdit.Margin = new Padding(6, 0, 0, 0);
            btnEdit.Click += delegate { EditApp(); };
            actions.Controls.Add(btnEdit);

            head.Controls.Add(titleFlow);
            head.Controls.Add(actions);
            return head;
        }

        private Panel BuildMeta()
        {
            Panel box = new Panel();
            box.Dock = DockStyle.Fill;
            box.BackColor = Theme.Panel;
            box.Padding = new Padding(14, 9, 14, 8);
            box.Margin = new Padding(0, 2, 0, 4);
            box.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, box.Width - 1, box.Height - 1);
            };

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 2;
            grid.RowCount = 1;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            grid.Margin = new Padding(0);
            grid.Padding = new Padding(0);
            grid.BackColor = Theme.Panel;

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.ColumnCount = 1;
            left.RowCount = 2;
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            left.Margin = new Padding(0);
            left.Padding = new Padding(0, 0, 14, 0);
            left.Controls.Add(MakeField("启动命令", out lblCommand, true), 0, 0);
            left.Controls.Add(MakeField("工作目录", out lblCwd, true), 0, 1);

            TableLayoutPanel stats = new TableLayoutPanel();
            stats.Dock = DockStyle.Fill;
            stats.ColumnCount = 3;
            stats.RowCount = 2;
            stats.Margin = new Padding(0);
            stats.Padding = new Padding(0);
            for (int i = 0; i < 3; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            stats.Controls.Add(MakeStat("启动方式", out lblShell), 0, 0);
            stats.Controls.Add(MakeStat("PID", out lblPid), 1, 0);
            stats.Controls.Add(MakeStat("退出码", out lblExit), 2, 0);
            stats.Controls.Add(MakeStat("开机启动", out lblAutoStart), 0, 1);
            stats.Controls.Add(MakeStat("崩溃重启", out lblAutoRestart), 1, 1);
            stats.Controls.Add(MakeStat("重启次数", out lblRestarts), 2, 1);

            grid.Controls.Add(left, 0, 0);
            grid.Controls.Add(stats, 1, 0);
            box.Controls.Add(grid);
            return box;
        }

        private Control MakeField(string label, out Label value, bool mono)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.RowCount = 2;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Margin = new Padding(0);
            t.Padding = new Padding(0);

            Label l = new Label();
            l.Text = label;
            l.Dock = DockStyle.Fill;
            l.Font = Theme.Small;
            l.ForeColor = Theme.Dim;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0);
            t.Controls.Add(l, 0, 0);

            value = new Label();
            value.Text = "—";
            value.Dock = DockStyle.Fill;
            value.Font = mono ? Theme.MonoSmall : Theme.Base;
            value.ForeColor = Theme.Text;
            value.AutoEllipsis = true;
            value.TextAlign = ContentAlignment.TopLeft;
            value.Margin = new Padding(0);
            t.Controls.Add(value, 0, 1);
            return t;
        }

        private Control MakeStat(string label, out Label value)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.RowCount = 2;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Margin = new Padding(0, 0, 8, 0);
            t.Padding = new Padding(0);

            Label l = new Label();
            l.Text = label;
            l.Dock = DockStyle.Fill;
            l.Font = Theme.Small;
            l.ForeColor = Theme.Dim;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0);
            t.Controls.Add(l, 0, 0);

            value = new Label();
            value.Text = "—";
            value.Dock = DockStyle.Fill;
            value.Font = Theme.Base;
            value.ForeColor = Theme.Text;
            value.AutoEllipsis = true;
            value.TextAlign = ContentAlignment.TopLeft;
            value.Margin = new Padding(0);
            t.Controls.Add(value, 0, 1);
            return t;
        }

        private Panel BuildLogPanel()
        {
            Panel box = new Panel();
            box.Dock = DockStyle.Fill;
            box.BackColor = Theme.LogBg;
            box.Margin = new Padding(0);
            box.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, box.Width - 1, box.Height - 1);
            };

            Panel toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 44;
            toolbar.BackColor = Theme.Panel;
            toolbar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
            };

            Label title = new Label();
            title.Text = "运行日志";
            title.AutoSize = true;
            title.Font = Theme.Bold;
            title.ForeColor = Theme.Muted;
            title.Location = new Point(14, 13);
            toolbar.Controls.Add(title);

            lblLogCount = new Label();
            lblLogCount.Text = "";
            lblLogCount.AutoSize = true;
            lblLogCount.Font = Theme.Small;
            lblLogCount.ForeColor = Theme.Dim;
            lblLogCount.Location = new Point(84, 15);
            toolbar.Controls.Add(lblLogCount);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Right;
            tools.Width = 570;
            tools.FlowDirection = FlowDirection.RightToLeft;
            tools.WrapContents = false;
            tools.Padding = new Padding(0, 8, 10, 0);
            tools.BackColor = Color.Transparent;

            Button clear = Theme.GhostButton("清空", 52);
            clear.Size = new Size(52, 27);
            clear.Font = Theme.Small;
            clear.Margin = new Padding(6, 0, 0, 0);
            clear.Click += delegate { ClearLogs(); };
            tools.Controls.Add(clear);

            Button openFile = Theme.GhostButton("打开文件", 72);
            openFile.Size = new Size(72, 27);
            openFile.Font = Theme.Small;
            openFile.Margin = new Padding(6, 0, 0, 0);
            openFile.Click += delegate { OpenLogFile(); };
            tools.Controls.Add(openFile);

            Button copy = Theme.GhostButton("复制", 52);
            copy.Size = new Size(52, 27);
            copy.Font = Theme.Small;
            copy.Margin = new Padding(6, 0, 0, 0);
            copy.Click += delegate
            {
                try { Clipboard.SetText(txtLog.Text); } catch (Exception) { }
            };
            tools.Controls.Add(copy);

            chkWrap = new CheckBox();
            chkWrap.Text = "换行";
            chkWrap.AutoSize = true;
            chkWrap.Font = Theme.Small;
            chkWrap.ForeColor = Theme.Muted;
            chkWrap.Margin = new Padding(8, 5, 0, 0);
            chkWrap.CheckedChanged += delegate { txtLog.WordWrap = chkWrap.Checked; };
            tools.Controls.Add(chkWrap);

            chkAutoScroll = new CheckBox();
            chkAutoScroll.Text = "自动滚动";
            chkAutoScroll.AutoSize = true;
            chkAutoScroll.Checked = true;
            chkAutoScroll.Font = Theme.Small;
            chkAutoScroll.ForeColor = Theme.Muted;
            chkAutoScroll.Margin = new Padding(8, 5, 0, 0);
            tools.Controls.Add(chkAutoScroll);

            txtFilter = new TextBox();
            txtFilter.Width = 150;
            txtFilter.Height = 24;
            txtFilter.BorderStyle = BorderStyle.FixedSingle;
            txtFilter.BackColor = Color.FromArgb(13, 19, 26);
            txtFilter.ForeColor = Theme.Text;
            txtFilter.Font = Theme.Small;
            txtFilter.RightToLeft = RightToLeft.No;
            txtFilter.Margin = new Padding(8, 4, 0, 0);
            txtFilter.TextChanged += delegate { RebuildLog(); };
            tools.Controls.Add(txtFilter);

            Label filterLabel = new Label();
            filterLabel.Text = "筛选";
            filterLabel.AutoSize = true;
            filterLabel.ForeColor = Theme.Dim;
            filterLabel.Font = Theme.Small;
            filterLabel.Margin = new Padding(8, 6, 0, 0);
            tools.Controls.Add(filterLabel);

            toolbar.Controls.Add(tools);

            Panel pad = new Panel();
            pad.Dock = DockStyle.Fill;
            pad.Padding = new Padding(12, 8, 6, 10);
            pad.BackColor = Theme.LogBg;

            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.WordWrap = false;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.BackColor = Theme.LogBg;
            txtLog.ForeColor = Theme.LogText;
            txtLog.Font = Theme.Mono;
            txtLog.HideSelection = false;
            pad.Controls.Add(txtLog);
            box.Controls.Add(pad);
            box.Controls.Add(toolbar);
            chkWrap.Checked = true;
            return box;
        }

        private Panel BuildEmptyView()
        {
            Panel empty = new Panel();
            empty.Dock = DockStyle.Fill;
            empty.BackColor = Theme.Bg;
            empty.Visible = false;

            Panel card = new Panel();
            card.Size = new Size(480, 260);
            card.BackColor = Theme.Bg;
            empty.Controls.Add(card);
            empty.Resize += delegate
            {
                card.Left = Math.Max(0, (empty.ClientSize.Width - card.Width) / 2);
                card.Top = Math.Max(0, (empty.ClientSize.Height - card.Height) / 2);
            };

            Label icon = new Label();
            icon.Text = "▣";
            icon.Font = new Font("Segoe UI Symbol", 30f);
            icon.ForeColor = Theme.Accent;
            icon.AutoSize = false;
            icon.TextAlign = ContentAlignment.MiddleCenter;
            icon.Size = new Size(480, 62);
            icon.Location = new Point(0, 10);
            card.Controls.Add(icon);

            Label title = new Label();
            title.Text = "还没有托管的应用";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Theme.Text;
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.Size = new Size(480, 34);
            title.Location = new Point(0, 78);
            card.Controls.Add(title);

            Label desc = new Label();
            desc.Text = "添加一个 Python 或 Node 项目，管家会在开机后自动把它拉起来，\r\n并在这里显示运行日志。";
            desc.Font = Theme.Base;
            desc.ForeColor = Theme.Muted;
            desc.AutoSize = false;
            desc.TextAlign = ContentAlignment.MiddleCenter;
            desc.Size = new Size(480, 54);
            desc.Location = new Point(0, 114);
            card.Controls.Add(desc);

            Button add = Theme.PrimaryButton("添加第一个应用", 140);
            add.Size = new Size(140, 36);
            add.Location = new Point(170, 186);
            add.Click += delegate { AddApp(); };
            card.Controls.Add(add);

            return empty;
        }

        // ------------------------------------------------------------ 列表绘制
        private void AppListDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= listData.Count) return;
            AppSnapshot a = listData[e.Index];
            Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) != 0;

            using (SolidBrush bg = new SolidBrush(selected ? Theme.Panel : Theme.Sidebar))
                g.FillRectangle(bg, e.Bounds);

            if (selected)
            {
                using (SolidBrush accent = new SolidBrush(Theme.Accent))
                    g.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 8, 3, e.Bounds.Height - 16);
            }

            int x = e.Bounds.X + 14;
            int y = e.Bounds.Y;
            using (SolidBrush dot = new SolidBrush(Theme.StatusColor(a.Status)))
                g.FillEllipse(dot, x, y + 22, 8, 8);

            Rectangle nameRect = new Rectangle(x + 18, y + 10, e.Bounds.Width - 100, 20);
            TextRenderer.DrawText(g, a.Name, Theme.Bold, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

            string cmd = (a.Command ?? "").Split('\r', '\n')[0];
            Rectangle cmdRect = new Rectangle(x + 18, y + 32, e.Bounds.Width - 42, 18);
            TextRenderer.DrawText(g, cmd, Theme.MonoSmall, cmdRect, Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

            if (a.Pid > 0)
            {
                TextRenderer.DrawText(g, "#" + a.Pid, Theme.Small,
                    new Rectangle(e.Bounds.Right - 60, y + 10, 48, 18), Theme.Dim,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        // ------------------------------------------------------------ 数据刷新
        private ManagedApp SelectedApp()
        {
            return selectedId == null ? null : svc.Find(selectedId);
        }

        private void SelectFirstApp()
        {
            if (svc.Apps.Count == 0)
            {
                selectedId = null;
                ShowEmpty(true);
                return;
            }
            if (selectedId == null || svc.Find(selectedId) == null) SelectApp(svc.Apps[0].Id);
        }

        private void SelectApp(string id)
        {
            selectedId = id;
            ManagedApp app = svc.Find(id);
            uiLines = new List<LogLine>();
            lastSeq = 0;
            txtLog.Clear();
            if (app == null)
            {
                ShowEmpty(true);
                return;
            }
            ShowEmpty(false);
            List<LogLine> tail = app.Log.ReadAfter(0, 1500);
            uiLines.AddRange(tail);
            lastSeq = tail.Count > 0 ? tail[tail.Count - 1].Seq : app.Log.Seq;
            RebuildLog();
            UpdateDetail();
        }

        private void ShowEmpty(bool empty)
        {
            emptyView.Visible = empty;
            appView.Visible = !empty;
            if (empty) emptyView.BringToFront();
        }

        private void TickUi()
        {
            if (IsDisposed || exiting) return;

            List<AppSnapshot> snaps = new List<AppSnapshot>();
            foreach (ManagedApp a in svc.Apps.ToList()) snaps.Add(a.Snapshot());

            string sig = "";
            foreach (AppSnapshot s in snaps)
                sig += s.Id + "|" + s.Name + "|" + s.Status + "|" + s.Pid + "|" + s.RestartPending + "|" + s.Command + ";";
            if (sig != listSignature)
            {
                listSignature = sig;
                listData = snaps;
                suppressSelect = true;
                try
                {
                    appList.BeginUpdate();
                    appList.Items.Clear();
                    foreach (AppSnapshot s in listData) appList.Items.Add(s.Name);
                    int idx = listData.FindIndex(delegate (AppSnapshot s) { return s.Id == selectedId; });
                    if (idx >= 0) appList.SelectedIndex = idx;
                    appList.EndUpdate();
                }
                finally
                {
                    suppressSelect = false;
                }
                lblCount.Text = listData.Count.ToString();
                if (selectedId == null && listData.Count > 0) SelectApp(listData[0].Id);
                if (listData.Count == 0)
                {
                    selectedId = null;
                    ShowEmpty(true);
                }
            }

            ManagedApp app = SelectedApp();
            if (app != null)
            {
                UpdateDetail();
                UpdateLogs(app);
            }

            btnTopAuto.Text = svc.Config == null ? "开机自启" : "开机自启";
        }

        private void UpdateDetail()
        {
            ManagedApp app = SelectedApp();
            if (app == null) return;
            AppSnapshot s = app.Snapshot();

            lblName.Text = s.Name;
            lblChip.Text = "● " + Theme.StatusText(s.Status, s.RestartPending);
            lblChip.ForeColor = Theme.StatusColor(s.Status);
            if (s.Running)
            {
                lblUptime.Text = "已运行 " + FormatDuration(s.UptimeSeconds);
            }
            else
            {
                lblUptime.Text = "";
            }

            lblCommand.Text = s.Command;
            lblCwd.Text = s.Cwd;
            lblShell.Text = s.Shell == "cmd" ? "命令提示符" : "PowerShell";
            lblPid.Text = s.Pid > 0 ? s.Pid.ToString() : "—";
            lblExit.Text = s.HasStarted && s.Pid == 0 ? (s.ExitCode >= 0 ? s.ExitCode.ToString() : "—") : "—";
            lblAutoStart.Text = s.AutoStart ? "开机自动启动" : "仅手动启动";
            lblAutoRestart.Text = s.RestartOnCrash ? "已开启（上限 " + s.MaxRestarts + "）" : "已关闭";
            lblRestarts.Text = s.Restarts.ToString();
            if (tips != null)
            {
                tips.SetToolTip(lblCommand, s.Command);
                tips.SetToolTip(lblCwd, s.Cwd);
            }

            if (!string.IsNullOrEmpty(s.LastError) && s.Status != "running")
            {
                lblError.Text = "上次退出：" + s.LastError;
                lblError.Visible = true;
            }
            else
            {
                lblError.Visible = false;
            }

            btnStart.Enabled = !s.Running && s.Status != "starting" && s.Status != "stopping" && !busy;
            btnStop.Enabled = s.Running && !busy;
            btnRestart.Enabled = s.Running && !busy;
            btnEdit.Enabled = !busy;
            btnDelete.Enabled = !busy;
        }

        private string FormatDuration(long seconds)
        {
            if (seconds >= 86400) return (seconds / 86400) + " 天 " + (seconds % 86400 / 3600) + " 小时";
            if (seconds >= 3600) return (seconds / 3600) + " 小时 " + (seconds % 3600 / 60) + " 分";
            if (seconds >= 60) return (seconds / 60) + " 分 " + (seconds % 60) + " 秒";
            return seconds + " 秒";
        }

        private void UpdateLogs(ManagedApp app)
        {
            List<LogLine> lines = app.Log.ReadAfter(lastSeq, 500);
            if (lines.Count == 0) return;
            lastSeq = lines[lines.Count - 1].Seq;
            foreach (LogLine l in lines)
            {
                uiLines.Add(l);
                if (uiLines.Count > 4000) uiLines.RemoveAt(0);
            }

            string filter = txtFilter.Text.Trim();
            StringBuilder sb = new StringBuilder();
            foreach (LogLine l in lines)
            {
                if (filter.Length > 0 && l.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.Append(l.Time.ToString("HH:mm:ss")).Append("  ").Append(l.Text).Append("\r\n");
            }
            if (sb.Length == 0)
            {
                UpdateLogCount();
                return;
            }

            if (txtLog.TextLength > 400000) RebuildLog();
            else txtLog.AppendText(sb.ToString());

            if (chkAutoScroll.Checked)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.SelectionLength = 0;
                txtLog.ScrollToCaret();
            }
            UpdateLogCount();
        }

        private void RebuildLog()
        {
            if (txtLog == null) return;
            string filter = txtFilter.Text.Trim();
            StringBuilder sb = new StringBuilder();
            foreach (LogLine l in uiLines)
            {
                if (filter.Length > 0 && l.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.Append(l.Time.ToString("HH:mm:ss")).Append("  ").Append(l.Text).Append("\r\n");
            }
            txtLog.Text = sb.ToString();
            if (chkAutoScroll.Checked)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
            UpdateLogCount();
        }

        private void UpdateLogCount()
        {
            if (lblLogCount == null) return;
            string filter = txtFilter.Text.Trim();
            if (filter.Length > 0)
                lblLogCount.Text = "筛选 " + CountFiltered() + " / " + uiLines.Count + " 行";
            else
                lblLogCount.Text = uiLines.Count + " 行";
        }

        private int CountFiltered()
        {
            string filter = txtFilter.Text.Trim();
            int n = 0;
            foreach (LogLine l in uiLines)
                if (l.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) n++;
            return n;
        }

        // ------------------------------------------------------------ 操作
        private void DoAction(string action)
        {
            ManagedApp app = SelectedApp();
            if (app == null || busy) return;
            busy = true;
            UpdateDetail();
            Thread t = new Thread(delegate ()
            {
                string msg = "";
                try
                {
                    if (action == "start") app.Start(true, out msg);
                    else if (action == "stop") app.Stop(out msg);
                    else if (action == "restart")
                    {
                        app.Stop(out msg);
                        app.Start(true, out msg);
                        msg = "已重启";
                    }
                }
                catch (Exception ex)
                {
                    msg = ex.Message;
                }
                SafeInvoke(delegate
                {
                    busy = false;
                    UpdateDetail();
                    TickUi();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoAll(bool start)
        {
            if (busy) return;
            busy = true;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    if (start) svc.StartAll();
                    else svc.StopAll();
                }
                catch (Exception) { }
                SafeInvoke(delegate { busy = false; TickUi(); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AddApp()
        {
            using (AppEditDialog dlg = new AppEditDialog(null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ManagedApp app = svc.Add(dlg.Result);
                    listSignature = "";
                    SelectApp(app.Id);
                    TickUi();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "添加失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void EditApp()
        {
            ManagedApp app = SelectedApp();
            if (app == null) return;
            using (AppEditDialog dlg = new AppEditDialog(app.Config))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    svc.Update(app.Id, dlg.Result);
                    listSignature = "";
                    UpdateDetail();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void DeleteApp()
        {
            ManagedApp app = SelectedApp();
            if (app == null) return;
            DialogResult r = MessageBox.Show(this,
                "确定删除「" + app.Name + "」吗？\r\n应用会先被停止，配置会被移除（日志文件保留）。",
                "删除应用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            busy = true;
            UpdateDetail();
            Thread t = new Thread(delegate ()
            {
                try { svc.Remove(app.Id); }
                catch (Exception) { }
                SafeInvoke(delegate
                {
                    busy = false;
                    selectedId = null;
                    listSignature = "";
                    SelectFirstApp();
                    TickUi();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ClearLogs()
        {
            ManagedApp app = SelectedApp();
            if (app == null) return;
            if (MessageBox.Show(this, "日志文件与当前显示的内容都会被清空，确定吗？", "清空日志",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            app.ClearLogs();
            uiLines.Clear();
            lastSeq = app.Log.Seq;
            txtLog.Clear();
            UpdateLogCount();
        }

        private void OpenLogFile()
        {
            ManagedApp app = SelectedApp();
            if (app == null) return;
            string path = app.Log.FilePath;
            if (File.Exists(path))
            {
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch (Exception) { OpenPath(Paths.LogsDir); }
            }
            else
            {
                OpenPath(Paths.LogsDir);
            }
        }

        private static void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception) { }
        }

        private void OpenSettings()
        {
            using (SettingsDialog dlg = new SettingsDialog(svc))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) TickUi();
            }
        }

        private void OpenAutostart()
        {
            using (AutostartDialog dlg = new AutostartDialog())
            {
                dlg.ShowDialog(this);
            }
        }

        private void ShowSystemLog()
        {
            using (SystemLogDialog dlg = new SystemLogDialog(svc))
            {
                dlg.ShowDialog(this);
            }
        }

        private void SafeInvoke(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------ 托盘与退出
        private void BuildTray()
        {
            tray = new NotifyIcon();
            try { tray.Icon = Theme.LoadIcon(); }
            catch (Exception) { tray.Icon = SystemIcons.Application; }
            tray.Text = "应用管家";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Theme.Base;
            menu.Items.Add("打开面板", null, delegate { ShowFromTray(); });
            menu.Items.Add("全部启动", null, delegate { DoAll(true); });
            menu.Items.Add("全部停止", null, delegate { DoAll(false); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出（停止所有应用）", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        private void HideToTray(bool showHint)
        {
            Hide();
            ShowInTaskbar = false;
            if (showHint && !trayHintShown)
            {
                trayHintShown = true;
                try
                {
                    tray.ShowBalloonTip(1800, "应用管家", "已最小化到托盘，受管应用继续运行。", ToolTipIcon.Info);
                }
                catch (Exception) { }
            }
        }

        private void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitApp()
        {
            if (MessageBox.Show(this, "退出会停止所有受管应用，确定吗？", "退出应用管家",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            exiting = true;
            if (!Visible) ShowFromTray();
            Text = "应用管家 - 正在停止应用…";
            Enabled = false;
            Thread t = new Thread(delegate ()
            {
                try { svc.StopAll(); }
                catch (Exception) { }
                SafeInvoke(delegate
                {
                    tray.Visible = false;
                    tray.Dispose();
                    Close();
                    Application.Exit();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && !startMinimized)
            {
                HideToTray(true);
            }
        }
    }
}
