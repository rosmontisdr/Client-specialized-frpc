// frpc 托盘启动器：内置 frpc.exe 与默认配置，单文件运行。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace FrpcTray
{
    static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    sealed class Options
    {
        public string Config;
        public string Exe;
        public string Args;
        public bool Show;
        public bool Stop;

        public static Options Parse(string[] a)
        {
            Options o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "-config" && i + 1 < a.Length) o.Config = a[++i];
                else if (a[i] == "-exe" && i + 1 < a.Length) o.Exe = a[++i];
                else if (a[i] == "-args" && i + 1 < a.Length) o.Args = a[++i];
                else if (a[i] == "-show") o.Show = true;
                else if (a[i] == "-stop") o.Stop = true;
            }
            return o;
        }
    }

    static class Res
    {
        static byte[] Read(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("缺少内置资源: " + name);
                byte[] buf = new byte[s.Length];
                int n = 0, r;
                while (n < buf.Length && (r = s.Read(buf, n, buf.Length - n)) > 0) n += r;
                return buf;
            }
        }

        static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        static string Hash(string text)
        {
            return Hex(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        public static string Id(string configPath)
        {
            return Hash(configPath.ToLowerInvariant()).Substring(0, 12);
        }

        // 释放内置 frpc.exe 到本地缓存
        public static string EnsureFrpc()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "frpc-tray");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "frpc.exe");
            string mark = Path.Combine(dir, "frpc.sha1");
            byte[] data = Read("frpc.exe");
            string sum = Hex(SHA1.Create().ComputeHash(data));
            if (File.Exists(exe) && File.Exists(mark) && File.ReadAllText(mark).Trim() == sum) return exe;
            File.WriteAllBytes(exe, data);
            File.WriteAllText(mark, sum);
            return exe;
        }

        public static string DefaultConfig()
        {
            return Encoding.UTF8.GetString(Read("default.frpc.toml"));
        }

        // 窗口标题里的署名
        public const string Credit = " by 玄渊不是香厨（bilibili）";

        // 内置图标
        public static Icon AppIcon(int size)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            {
                if (s == null) return null;
                return new Icon(s, new Size(size, size));
            }
        }

        // 取内置图标位图，供托盘叠加状态点
        public static Bitmap AppBitmap(int size)
        {
            using (Icon ic = AppIcon(size))
            {
                return ic == null ? null : ic.ToBitmap();
            }
        }

        // ---- 配置存于自身末尾：<原始 exe><配置><长度4字节><魔术16字节> ----

        static readonly byte[] TailMagic = Encoding.ASCII.GetBytes("FRPCTRAYCFGv1\0\0\0");

        public static string SelfPath { get { return Application.ExecutablePath; } }

        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "frpc-tray");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string LogsDir
        {
            get
            {
                string d = Path.Combine(DataDir, "logs");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        // 交给 frpc 使用的运行配置
        public static string RuntimeConfig { get { return Path.Combine(DataDir, "frpc.toml"); } }

        public static string ReadAppended()
        {
            try
            {
                using (FileStream fs = new FileStream(SelfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < 20) return null;
                    byte[] magic = new byte[16];
                    fs.Seek(-16, SeekOrigin.End);
                    if (fs.Read(magic, 0, 16) != 16) return null;
                    for (int i = 0; i < 16; i++) if (magic[i] != TailMagic[i]) return null;
                    byte[] lb = new byte[4];
                    fs.Seek(-20, SeekOrigin.End);
                    if (fs.Read(lb, 0, 4) != 4) return null;
                    int len = BitConverter.ToInt32(lb, 0);
                    if (len <= 0 || len > 4 * 1024 * 1024 || fs.Length < 20 + len) return null;
                    byte[] data = new byte[len];
                    fs.Seek(-20 - len, SeekOrigin.End);
                    int n = 0, r;
                    while (n < len && (r = fs.Read(data, n, len - n)) > 0) n += r;
                    return n == len ? Encoding.UTF8.GetString(data) : null;
                }
            }
            catch (Exception) { return null; }
        }

        // 去掉尾部旧配置块，返回原始 exe 长度
        static int StripLength(byte[] buf)
        {
            if (buf.Length < 20) return buf.Length;
            for (int i = 0; i < 16; i++) if (buf[buf.Length - 16 + i] != TailMagic[i]) return buf.Length;
            int len = BitConverter.ToInt32(buf, buf.Length - 20);
            if (len <= 0 || buf.Length < 20 + len) return buf.Length;
            return buf.Length - 20 - len;
        }

        // 写入自身：新文件 + 改名替换（运行中的 exe 不能被直接写入）
        public static void SaveAppended(string toml)
        {
            byte[] payload = Encoding.UTF8.GetBytes(toml);
            byte[] body = File.ReadAllBytes(SelfPath);
            int baseLen = StripLength(body);

            string dir = Path.GetDirectoryName(SelfPath);
            string name = Path.GetFileName(SelfPath);
            string tmp = Path.Combine(dir, name + ".new");
            // 运行中的映像不能被覆盖，只能改名；名字唯一化以便同一次会话里反复保存
            string old = Path.Combine(dir, string.Concat(name, ".", Process.GetCurrentProcess().Id.ToString(), ".", DateTime.Now.Ticks.ToString(), ".old"));

            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(body, 0, baseLen);
                fs.Write(payload, 0, payload.Length);
                fs.Write(BitConverter.GetBytes(payload.Length), 0, 4);
                fs.Write(TailMagic, 0, 16);
                fs.Flush(true);
            }

            File.Move(SelfPath, old);
            try { File.SetAttributes(old, FileAttributes.Hidden); }   // 运行中的映像删不掉，先藏起来
            catch (Exception) { }
            try { File.Move(tmp, SelfPath); }
            catch (Exception)
            {
                try { File.SetAttributes(old, FileAttributes.Normal); }
                catch (Exception) { }
                File.Move(old, SelfPath);   // 回滚
                throw;
            }
        }

        // 清理上次保存遗留的临时文件；运行中的映像无法删除，跳过即可
        public static void CleanupOld()
        {
            try
            {
                string dir = Path.GetDirectoryName(SelfPath);
                string name = Path.GetFileName(SelfPath);
                foreach (string f in Directory.GetFiles(dir, name + ".*.old")) { try { File.Delete(f); } catch (Exception) { } }
                foreach (string f in Directory.GetFiles(dir, name + ".new")) { try { File.Delete(f); } catch (Exception) { } }
            }
            catch (Exception) { }
        }
    }

    static class Program
    {
        static Mutex _instance;   // 进程级持有，防止被回收后重复启动

        [STAThread]
        static int Main(string[] args)
        {
            Options o = Options.Parse(args);
            // 默认配置存在 exe 自身内部；-config 指定外部文件时以文件为准
            string key = Res.SelfPath;
            if (o.Config != null) { o.Config = Path.GetFullPath(o.Config); key = o.Config; }
            string id = Res.Id(key);

            bool primary;
            _instance = new Mutex(false, "Local\\frpc-tray-" + id);
            try { primary = _instance.WaitOne(TimeSpan.Zero, false); }
            catch (AbandonedMutexException) { primary = true; }

            if (!primary)
            {
                // 已有实例：转发命令并退出
                Signal("Local\\frpc-tray-" + (o.Stop ? "stop-" : "show-") + id);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Crash("未处理异常: " + e.ExceptionObject);
            };

            TrayApp app = null;
            try
            {
                app = new TrayApp(o, id);
                if (app.Cancelled)
                {
                    app.Cleanup();
                    return 0;
                }
                Application.Run(app);
                return 0;
            }
            catch (Exception ex)
            {
                if (app != null) app.Cleanup();
                Crash("启动失败: " + ex);
                MessageBox.Show(ex.Message, "frpc 托盘启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static void Crash(string text)
        {
            try
            {
                string dir = Res.LogsDir;
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("s") + " " + text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static void Signal(string name)
        {
            try
            {
                using (EventWaitHandle h = EventWaitHandle.OpenExisting(name)) h.Set();
            }
            catch (Exception) { }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly string _sourcePath;   // null 表示配置存于 exe 内部
        readonly string _logPath;
        readonly string _childExe, _childArgs;
        readonly EventWaitHandle _evShow, _evStop;
        readonly StreamWriter _writer;
        readonly object _writeLock = new object();
        readonly Queue<string> _pending = new Queue<string>();
        readonly LogWindow _win;
        readonly NotifyIcon _tray;
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _miStart, _miStop;
        readonly Icon _icoRun, _icoStop;
        readonly System.Windows.Forms.Timer _timer;

        Process _child;
        volatile bool _childExited;
        int _childCode = -1;
        bool _exiting, _cancelled, _hidOnce;

        public bool Cancelled { get { return _cancelled; } }

        public TrayApp(Options o, string id)
        {
            _sourcePath = o.Config;
            Res.CleanupOld();

            _logPath = Path.Combine(Res.LogsDir, "frpc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            FileStream fs = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
            _writer.AutoFlush = true;
            _writer.NewLine = "\n";

            _childExe = o.Exe != null ? Path.GetFullPath(o.Exe) : Res.EnsureFrpc();
            _childArgs = o.Args != null ? o.Args : "-c \"" + Res.RuntimeConfig + "\"";

            _evShow = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\frpc-tray-show-" + id);
            _evStop = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\frpc-tray-stop-" + id);

            _icoRun = MakeTrayIcon(true);
            _icoStop = MakeTrayIcon(false);

            _miStart = new ToolStripMenuItem("启动 frpc", null, delegate { StartChild(); });
            _miStop = new ToolStripMenuItem("结束 frpc 进程", null, delegate { Quit(true); });
            _miStart.Enabled = false;
            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem("显示日志窗口", null, delegate { ShowLog(); }));
            _menu.Items.Add(_miStart);
            _menu.Items.Add(new ToolStripMenuItem("编辑配置...", null, delegate { EditConfig(); }));
            _menu.Items.Add(new ToolStripMenuItem("打开日志文件夹", null, delegate { OpenLogs(); }));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miStop);

            _tray = new NotifyIcon();
            _tray.Icon = _icoRun;
            _tray.Text = "frpc";
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += OnTrayClick;
            _tray.Visible = true;

            _win = new LogWindow(OnLogHidden, Line);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 150;
            _timer.Tick += OnTick;
            _timer.Start();

            Line("[launcher] 日志: " + _logPath);
            Line("[launcher] 配置: " + (_sourcePath != null ? _sourcePath : "内置于程序"));

            LoadConfig();
            if (_cancelled) return;

            StartChild();
            ShowLog();
        }

        // ---- 子进程 ----

        void StartChild()
        {
            if (_child != null && !_child.HasExited) return;

            ProcessStartInfo psi = new ProcessStartInfo(_childExe, _childArgs);
            Line("[launcher] 启动: " + _childExe + " " + _childArgs);
            psi.WorkingDirectory = _sourcePath != null ? Path.GetDirectoryName(_sourcePath) : Application.StartupPath;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);

            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += OnChildOut;
            p.ErrorDataReceived += OnChildErr;
            p.Exited += OnChildExit;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _child = p;

            Line("[launcher] frpc 已启动 pid=" + p.Id);
            SetRunning(true);
        }

        void KillChild()
        {
            Process p = _child;
            _child = null;
            if (p == null) return;
            p.Exited -= OnChildExit;
            p.OutputDataReceived -= OnChildOut;
            p.ErrorDataReceived -= OnChildErr;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } }
            catch (Exception) { }
            try { p.Dispose(); }
            catch (Exception) { }
            Line("[launcher] frpc 已结束");
        }

        void OnChildOut(object s, DataReceivedEventArgs e) { if (e.Data != null) Line(e.Data); }
        void OnChildErr(object s, DataReceivedEventArgs e) { if (e.Data != null) Line(e.Data); }

        void OnChildExit(object s, EventArgs e)
        {
            if (_exiting) return;
            try { _childCode = ((Process)s).ExitCode; }
            catch (Exception) { }
            _childExited = true;
        }

        void HandleChildExit()
        {
            if (_child != null) { try { _child.Dispose(); } catch (Exception) { } _child = null; }
            SetRunning(false);
            Line("[launcher] frpc 意外退出 code=" + _childCode);
            _tray.ShowBalloonTip(5000, "frpc 已退出", "进程意外结束，右键菜单可重新启动。", ToolTipIcon.Warning);
        }

        // ---- 托盘 ----

        void OnTrayClick(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowLog();
        }

        void SetRunning(bool running)
        {
            _tray.Icon = running ? _icoRun : _icoStop;
            _tray.Text = running ? "frpc 运行中" : "frpc 已退出";
            _miStart.Enabled = !running;
            _miStop.Text = running ? "结束 frpc 进程" : "退出";
            _win.SetTitle((running ? "frpc - 运行中" : "frpc - 已退出") + Res.Credit);
        }

        void ShowLog()
        {
            if (_win.IsDisposed) return;
            _win.Show();
            if (_win.WindowState == FormWindowState.Minimized) _win.WindowState = FormWindowState.Normal;
            _win.Activate();
            Native.SetForegroundWindow(_win.Handle);
        }

        void OnLogHidden()
        {
            if (_hidOnce) return;
            _hidOnce = true;
            _tray.ShowBalloonTip(4000, "frpc 仍在运行", "窗口已隐藏，单击托盘图标可重新打开。", ToolTipIcon.Info);
        }

        // 界面线程定时器：刷新日志、检查外部命令
        void OnTick(object s, EventArgs e)
        {
            try { Tick(); }
            catch (Exception ex) { Line("[launcher] 界面异常: " + ex.Message); }
        }

        void Tick()
        {
            while (true)
            {
                string text;
                lock (_pending)
                {
                    if (_pending.Count == 0) break;
                    text = _pending.Dequeue();
                }
                _win.Append(text);
            }
            if (_childExited)
            {
                _childExited = false;
                HandleChildExit();
            }
            if (_evShow.WaitOne(0)) ShowLog();
            if (_evStop.WaitOne(0)) { Quit(true); return; }
        }

        // ---- 配置 ----

        // 读取配置来源：外部文件 / exe 内部 / 内置默认
        string ReadConfig(out bool exists)
        {
            if (_sourcePath != null)
            {
                exists = File.Exists(_sourcePath);
                return exists ? File.ReadAllText(_sourcePath, Encoding.UTF8) : Res.DefaultConfig();
            }
            string saved = Res.ReadAppended();
            exists = saved != null;
            return saved != null ? saved : Res.DefaultConfig();
        }

        // 写入运行配置，并持久化到 exe 或外部文件
        void StoreConfig(string text, bool warn)
        {
            File.WriteAllText(Res.RuntimeConfig, text, new UTF8Encoding(false));
            try
            {
                if (_sourcePath != null) File.WriteAllText(_sourcePath, text, new UTF8Encoding(false));
                else Res.SaveAppended(text);
            }
            catch (Exception ex)
            {
                Line("[launcher] 配置未能写入程序: " + ex.Message);
                if (warn) MessageBox.Show(ex.Message, "配置已生效，但没能保存进程序内部",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void LoadConfig()
        {
            bool exists;
            string text = ReadConfig(out exists);
            if (exists)
            {
                try { File.WriteAllText(Res.RuntimeConfig, text, new UTF8Encoding(false)); }
                catch (Exception ex) { Line("[launcher] 无法写入运行配置: " + ex.Message); }
                return;
            }
            // 首次运行：先编辑配置
            if (!EditConfig(text)) _cancelled = true;
        }

        void EditConfig()
        {
            bool exists;
            EditConfig(ReadConfig(out exists));
        }

        bool EditConfig(string text)
        {
            using (ConfigWindow dlg = new ConfigWindow(_childExe, _sourcePath, text))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return false;
                StoreConfig(dlg.Result, true);
                Line("[launcher] 配置已保存");
                KillChild();
                StartChild();
                return true;
            }
        }

        void OpenLogs()
        {
            try { Process.Start("explorer.exe", "/select,\"" + _logPath + "\""); }
            catch (Exception) { }
        }

        // ---- 输出 ----

        static readonly Regex Ansi = new Regex("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

        void Line(string s)
        {
            string text = Ansi.Replace(s, "");   // 去掉 frpc 的 ANSI 颜色码
            lock (_writeLock)
            {
                try { _writer.WriteLine(text); }
                catch (Exception) { }
            }
            lock (_pending) _pending.Enqueue(text);
        }

        // ---- 退出 ----

        public void Quit(bool stopChild)
        {
            if (_exiting) return;
            _exiting = true;
            if (stopChild) KillChild();
            Cleanup();
            ExitThread();
        }

        public void Cleanup()
        {
            _exiting = true;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
            if (_evShow != null) _evShow.Close();
            if (_evStop != null) _evStop.Close();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_menu != null) _menu.Dispose();
            if (_win != null && !_win.IsDisposed) { _win.AllowClose = true; _win.Dispose(); }
            lock (_writeLock)
            {
                try { _writer.Flush(); _writer.Dispose(); }
                catch (Exception) { }
            }
        }

        // ---- 图标 ----

        // 应用图标 + 右下角状态圆点
        static Icon MakeTrayIcon(bool running)
        {
            int size = SystemInformation.SmallIconSize.Width;
            Bitmap bmp = Res.AppBitmap(size);
            if (bmp == null) return SystemIcons.Application;
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float d = Math.Max(5f, size * 0.36f);
                    float x = size - d, y = size - d;
                    using (Brush b = new SolidBrush(running ? Color.FromArgb(40, 200, 70) : Color.FromArgb(215, 60, 60)))
                        g.FillEllipse(b, x, y, d, d);
                    using (Pen p = new Pen(Color.FromArgb(230, 0, 0, 0), 1f))
                        g.DrawEllipse(p, x, y, d, d);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
            finally { bmp.Dispose(); }
        }
    }

    // 日志窗口：关闭即隐藏
    sealed class LogWindow : Form
    {
        readonly RichTextBox _box;
        readonly Action _onHidden;
        readonly Action<string> _log;
        bool _auto = true;

        public LogWindow(Action onHidden, Action<string> log)
        {
            _onHidden = onHidden;
            _log = log;
            Text = "frpc" + Res.Credit;
            Icon = Res.AppIcon(32);
            Width = 920;
            Height = 540;
            MinimumSize = new Size(420, 200);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;

            _box = new RichTextBox();
            _box.Dock = DockStyle.Fill;
            _box.ReadOnly = true;
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Color.Black;
            _box.ForeColor = Color.FromArgb(225, 225, 225);
            _box.Font = new Font("Consolas", 10f);
            _box.WordWrap = false;
            _box.DetectUrls = false;
            _box.HideSelection = false;
            _box.ScrollBars = RichTextBoxScrollBars.Both;
            _box.ShortcutsEnabled = true;
            Controls.Add(_box);

            ToolStripMenuItem miCopy = new ToolStripMenuItem("复制", null, delegate { _box.Copy(); });
            ToolStripMenuItem miAll = new ToolStripMenuItem("全选", null, delegate { _box.SelectAll(); });
            ToolStripMenuItem miClear = new ToolStripMenuItem("清空", null, delegate { _box.Clear(); });
            ToolStripMenuItem miAuto = new ToolStripMenuItem("自动滚动", null, delegate { });
            miAuto.Checked = true;
            miAuto.CheckOnClick = true;
            miAuto.Click += delegate { _auto = miAuto.Checked; };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(miCopy);
            menu.Items.Add(miAll);
            menu.Items.Add(miClear);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miAuto);
            _box.ContextMenuStrip = menu;
        }

        public void SetTitle(string title) { Text = title; }

        public void Append(string line)
        {
            if (IsDisposed) return;
            if (_box.TextLength > 300000)
            {
                _box.Select(0, 100000);
                _box.SelectedText = "";
            }
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
            _box.SelectionColor = ColorOf(line);
            _box.AppendText(line + "\n");
            _box.SelectionColor = _box.ForeColor;
            if (_auto)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
        }

        static Color ColorOf(string line)
        {
            if (line.StartsWith("[launcher]", StringComparison.Ordinal)) return Color.FromArgb(95, 200, 255);
            if (line.IndexOf(" [E] ", StringComparison.Ordinal) >= 0) return Color.FromArgb(255, 95, 95);
            if (line.IndexOf(" [W] ", StringComparison.Ordinal) >= 0) return Color.FromArgb(255, 205, 80);
            if (line.IndexOf(" [D] ", StringComparison.Ordinal) >= 0) return Color.FromArgb(130, 130, 130);
            if (line.IndexOf(" [T] ", StringComparison.Ordinal) >= 0) return Color.FromArgb(130, 130, 130);
            return Color.FromArgb(225, 225, 225);
        }

        public bool AllowClose;   // 程序退出时置位

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 除程序自身退出外，一律只隐藏窗口
            if (!AllowClose && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                // 取消关闭后延迟隐藏，避免 Visible 状态不同步
                BeginInvoke((MethodInvoker)delegate { Hide(); });
                if (_onHidden != null) _onHidden();
            }
            base.OnFormClosing(e);
        }
    }

    // 配置编辑器：按 [表头] 分段编辑
    sealed class ConfigWindow : Form
    {
        sealed class Section
        {
            public string Header;                        // null = 文件开头（全局设置）
            public string[] Lines = new string[0];
            public Panel Host;
            public TextBox Box;
            public Label Caption;
            public int Index;                            // 代理序号，0 表示非代理段
        }

        readonly string _exe, _path;
        readonly Panel _scroll;
        readonly FlowLayoutPanel _flow;
        readonly List<Section> _sections = new List<Section>();
        int _proxyCount;

        public ConfigWindow(string exe, string path, string text)
        {
            _exe = exe;
            _path = path;
            Text = "编辑 frpc 配置" + Res.Credit;
            Icon = Res.AppIcon(32);
            Width = 900;
            Height = 700;
            MinimumSize = new Size(620, 420);
            StartPosition = FormStartPosition.CenterScreen;

            _flow = new FlowLayoutPanel();
            _flow.FlowDirection = FlowDirection.TopDown;
            _flow.WrapContents = false;
            _flow.AutoSize = true;
            _flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _flow.Dock = DockStyle.Top;
            _flow.Padding = new Padding(12, 8, 12, 8);

            _scroll = new Panel();
            _scroll.Dock = DockStyle.Fill;
            _scroll.AutoScroll = true;
            _scroll.Controls.Add(_flow);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 46;

            Button add = MakeButton("新增代理", 12);
            add.Click += delegate { AddProxy(); };
            Button verify = MakeButton("校验配置", 112);
            verify.Click += delegate { Verify(); };
            Button import = MakeButton("导入...", 212);
            import.Click += delegate { Import(); };
            Button ok = MakeButton("保存并启动", 0);
            ok.Click += delegate { Save(); };
            Button cancel = MakeButton("取消", 0);
            cancel.DialogResult = DialogResult.Cancel;

            bar.Controls.Add(add);
            bar.Controls.Add(verify);
            bar.Controls.Add(import);
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            bar.Resize += delegate
            {
                ok.Left = bar.Width - 12 - ok.Width;
                cancel.Left = ok.Left - 8 - cancel.Width;
            };

            Label hint = new Label();
            hint.Text = _path != null ? "配置文件: " + _path : "配置保存在程序内部: " + Res.SelfPath;
            hint.Dock = DockStyle.Top;
            hint.Height = 24;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Padding = new Padding(12, 0, 0, 0);

            Controls.Add(_scroll);
            Controls.Add(bar);
            Controls.Add(hint);
            CancelButton = cancel;

            SetText(text);
            _scroll.SizeChanged += delegate { Relayout(); };
        }

        // 各段按原顺序拼回，未经解析，内容不丢
        public string Result
        {
            get
            {
                List<string> all = new List<string>();
                foreach (Section s in _sections)
                {
                    if (s.Header != null)
                    {
                        // 段与段之间留空行（原文件已有空行时不重复添加）
                        if (all.Count > 0 && all[all.Count - 1].Trim().Length > 0) all.Add("");
                        all.Add(s.Header);
                    }
                    all.AddRange(BoxLines(s));
                }
                while (all.Count > 1 && all[all.Count - 1].Trim().Length == 0) all.RemoveAt(all.Count - 1);
                all.Add("");   // 结尾保留换行
                return string.Join("\n", all.ToArray());
            }
        }

        static string[] BoxLines(Section s)
        {
            return s.Box.Text.Replace("\r\n", "\n").Split('\n');
        }

        static Button MakeButton(string text, int left)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = 92;
            b.Height = 28;
            b.Top = 9;
            b.Left = left;
            return b;
        }

        // ---- 分段 ----

        static bool IsHeader(string line)
        {
            string t = line.TrimStart();
            return t.Length > 0 && t[0] == '[';
        }

        void SetText(string text)
        {
            List<Section> list = new List<Section>();
            Section cur = new Section();
            List<string> buf = new List<string>();
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (IsHeader(line))
                {
                    if (cur.Header != null || buf.Count > 0)
                    {
                        cur.Lines = buf.ToArray();
                        list.Add(cur);
                        cur = new Section();
                        buf = new List<string>();
                    }
                    cur.Header = line;
                }
                else buf.Add(line);
            }
            cur.Lines = buf.ToArray();
            list.Add(cur);
            SetSections(list);
        }

        void SetSections(List<Section> list)
        {
            _flow.SuspendLayout();
            foreach (Section s in _sections) s.Host.Dispose();
            _sections.Clear();
            _flow.Controls.Clear();
            _proxyCount = 0;
            foreach (Section s in list) AddSection(s);
            _flow.ResumeLayout(true);
            Relayout();
        }

        void AddSection(Section s)
        {
            bool isProxy = s.Header != null && s.Header.Trim().StartsWith("[[proxies]]", StringComparison.Ordinal);
            if (isProxy) s.Index = ++_proxyCount;

            TextBox box = new TextBox();
            box.Multiline = true;
            box.AcceptsReturn = true;
            box.AcceptsTab = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Font = new Font("Consolas", 10f);
            box.Dock = DockStyle.Fill;
            box.Text = string.Join("\r\n", s.Lines);   // 原生 EDIT 控件只认 CRLF，裸 LF 不换行

            Label cap = new Label();
            cap.Dock = DockStyle.Fill;
            cap.TextAlign = ContentAlignment.MiddleLeft;
            cap.Font = new Font(Font, FontStyle.Bold);

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 22;
            head.Controls.Add(cap);

            if (isProxy)
            {
                Button del = new Button();
                del.Text = "删除";
                del.Dock = DockStyle.Right;
                del.Width = 64;
                del.Click += delegate { Remove(s); };
                head.Controls.Add(del);
            }

            int rows = Math.Min(Math.Max(s.Lines.Length, 2), 24);
            Panel host = new Panel();
            host.Margin = new Padding(0, 2, 0, 10);
            host.Height = 22 + rows * box.Font.Height + 8;
            host.Controls.Add(box);
            host.Controls.Add(head);

            box.Leave += delegate { cap.Text = Title(s); };
            s.Host = host;
            s.Box = box;
            s.Caption = cap;
            s.Caption.Text = Title(s);
            _sections.Add(s);
            _flow.Controls.Add(host);
        }

        void Relayout()
        {
            int w = _scroll.ClientSize.Width - _flow.Padding.Horizontal - 4;
            if (w < 220) w = 220;
            foreach (Section s in _sections) s.Host.Width = w;
        }

        static string Title(Section s)
        {
            if (s.Header == null) return "全局设置";
            if (s.Index > 0)
            {
                string name = Value(s, "name");
                return "代理 " + s.Index + (name != null && name.Length > 0 ? "  (" + name + ")" : "");
            }
            return s.Header.Trim();
        }

        static string Value(Section s, string key)
        {
            foreach (string line in BoxLines(s))
            {
                string t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                int i = t.IndexOf('=');
                if (i <= 0) continue;
                if (t.Substring(0, i).Trim() != key) continue;
                string v = t.Substring(i + 1).Trim();
                if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
                return v;
            }
            return null;
        }

        void AddProxy()
        {
            Section s = new Section();
            s.Header = "[[proxies]]";
            s.Lines = new string[]
            {
                "name = \"new\"",
                "type = \"tcp\"",
                "localIP = \"127.0.0.1\"",
                "localPort = 0",
                "remotePort = 0"
            };
            AddSection(s);
            Relayout();
            _scroll.ScrollControlIntoView(s.Host);
            s.Box.Focus();
            s.Box.SelectAll();
        }

        void Remove(Section s)
        {
            _sections.Remove(s);
            _flow.Controls.Remove(s.Host);
            s.Host.Dispose();
            _proxyCount = 0;
            foreach (Section x in _sections)
            {
                bool isProxy = x.Header != null && x.Header.Trim().StartsWith("[[proxies]]", StringComparison.Ordinal);
                x.Index = isProxy ? ++_proxyCount : 0;
                x.Caption.Text = Title(x);
            }
            Relayout();
        }

        // ---- 按钮 ----

        void Save()
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        void Import()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "frpc 配置 (*.toml;*.ini)|*.toml;*.ini|所有文件 (*.*)|*.*";
                dlg.Title = "导入配置";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { SetText(File.ReadAllText(dlg.FileName, Encoding.UTF8)); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void Verify()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "frpc-verify-" + Guid.NewGuid().ToString("N") + ".toml");
            string output = "";
            int code = -1;
            try
            {
                File.WriteAllText(tmp, Result, new UTF8Encoding(false));
                ProcessStartInfo psi = new ProcessStartInfo(_exe, "verify -c \"" + tmp + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                using (Process p = Process.Start(psi))
                {
                    output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } }
                    code = p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
            }
            finally
            {
                try { File.Delete(tmp); }
                catch (Exception) { }
            }

            string msg = output.Length == 0 ? (code == 0 ? "配置有效。" : "校验未通过。") : output;
            MessageBox.Show(this, msg, code == 0 ? "校验通过" : "校验失败",
                MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
