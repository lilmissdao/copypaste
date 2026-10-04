// CopyPaste - a tiny Flycut-style clipboard manager for Windows.
//
// Lives in the system tray (next to the clock / volume icons) and keeps
// the last 50 text items you copied. Click the tray icon, or press
// Ctrl+Shift+V anywhere, to pick an older item.
//
// Written against C# 5 / .NET Framework 4 so it compiles with the csc.exe
// that ships inside every Windows install (see build.bat).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("CopyPaste")]
[assembly: System.Reflection.AssemblyProduct("CopyPaste")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace CopyPaste
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "CopyPaste.SingleInstance.Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("CopyPaste is already running.\nLook for its icon next to the clock.",
                        "CopyPaste", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try { NativeMethods.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    // ------------------------------------------------------------------
    // Clipboard history storage
    // ------------------------------------------------------------------
    class ClipHistory
    {
        public const int MaxItems = 50;
        const int MaxItemChars = 1000000; // skip absurdly large copies

        readonly List<string> items = new List<string>();
        readonly string path;

        public event EventHandler Changed;

        public ClipHistory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CopyPaste");
            path = Path.Combine(dir, "history.txt");
        }

        public IList<string> Items { get { return items.AsReadOnly(); } }

        public void Add(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            if (text.Length > MaxItemChars) return;

            // Like Flycut: re-copying an existing item moves it to the top.
            items.Remove(text);
            items.Insert(0, text);
            while (items.Count > MaxItems) items.RemoveAt(items.Count - 1);
            OnChanged();
        }

        public void Remove(string text)
        {
            if (items.Remove(text)) OnChanged();
        }

        public void Clear()
        {
            items.Clear();
            OnChanged();
        }

        void OnChanged()
        {
            Save();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // One base64-encoded UTF-8 item per line: survives newlines/tabs in clips.
        public void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Length == 0) continue;
                    try { items.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line))); }
                    catch (FormatException) { }
                    if (items.Count >= MaxItems) break;
                }
            }
            catch (Exception) { }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>(items.Count);
                foreach (string s in items) lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(s)));
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------
    // Tray icon, clipboard listener, global hotkey
    // ------------------------------------------------------------------
    class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "CopyPaste";

        readonly ClipHistory history = new ClipHistory();
        readonly NotifyIcon tray;
        readonly ListenerWindow listener;
        readonly HistoryPopup popup;
        readonly ToolStripMenuItem pauseItem;
        readonly ToolStripMenuItem startupItem;
        readonly Icon appIcon;

        bool paused;

        public TrayContext()
        {
            history.Load();

            appIcon = IconFactory.Create();
            popup = new HistoryPopup(history, appIcon);
            popup.ItemChosen += OnItemChosen;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show history\tCtrl+Shift+V", null, delegate { popup.ShowNearTray(IntPtr.Zero); });
            menu.Items.Add(new ToolStripSeparator());
            pauseItem = new ToolStripMenuItem("Pause recording", null, delegate { TogglePause(); });
            menu.Items.Add(pauseItem);
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            startupItem.Checked = IsStartupEnabled();
            menu.Items.Add(startupItem);
            menu.Items.Add("Clear history", null, delegate { ConfirmClear(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += OnTrayClick;
            UpdateTooltip();
            tray.Visible = true;

            history.Changed += delegate { UpdateTooltip(); };

            listener = new ListenerWindow();
            listener.ClipboardChanged += OnClipboardChanged;
            listener.HotkeyPressed += OnHotkey;
            if (!listener.RegisterHotkey())
            {
                tray.ShowBalloonTip(4000, "CopyPaste",
                    "Ctrl+Shift+V is used by another app, so the shortcut is off. Click this icon to see your history.",
                    ToolTipIcon.Info);
            }
            else if (history.Items.Count == 0)
            {
                tray.ShowBalloonTip(4000, "CopyPaste is running",
                    "Copy things as usual. Click this icon or press Ctrl+Shift+V to see your last 50 copies.",
                    ToolTipIcon.Info);
            }
        }

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // Clicking the icon while the popup is open first deactivates (hides) it;
            // don't immediately reopen it on the same click.
            if (popup.Visible) popup.Hide();
            else if ((DateTime.UtcNow - popup.LastHiddenUtc).TotalMilliseconds > 300) popup.ShowNearTray(IntPtr.Zero);
        }

        void OnHotkey(object sender, EventArgs e)
        {
            // Remember which window had focus so we can paste back into it.
            IntPtr target = NativeMethods.GetForegroundWindow();
            popup.ShowNearCursor(target);
        }

        void OnClipboardChanged(object sender, EventArgs e)
        {
            if (paused) return;
            string text = ClipboardHelper.TryGetText();
            if (text != null) history.Add(text);
        }

        void OnItemChosen(object sender, ItemChosenEventArgs e)
        {
            // Re-copying fires a clipboard update, which moves the item to the top.
            if (!ClipboardHelper.TrySetText(e.Text)) return;

            if (e.PasteInto != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(e.PasteInto);
                // Let the target window regain focus before sending Ctrl+V.
                var t = new System.Windows.Forms.Timer();
                t.Interval = 120;
                t.Tick += delegate
                {
                    t.Stop();
                    t.Dispose();
                    try { SendKeys.SendWait("^v"); } catch { }
                };
                t.Start();
            }
        }

        void TogglePause()
        {
            paused = !paused;
            pauseItem.Checked = paused;
            UpdateTooltip();
        }

        void ConfirmClear()
        {
            if (MessageBox.Show("Delete all clipboard history?", "CopyPaste",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                history.Clear();
        }

        void UpdateTooltip()
        {
            string t = "CopyPaste - " + history.Items.Count + " item" + (history.Items.Count == 1 ? "" : "s");
            if (paused) t += " (paused)";
            tray.Text = t;
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        void ToggleStartup()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (IsStartupEnabled()) key.DeleteValue(AppName, false);
                    else key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change startup setting:\n" + ex.Message, "CopyPaste");
            }
            startupItem.Checked = IsStartupEnabled();
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            tray.Dispose();
            listener.Dispose();
            popup.Dispose();
            base.ExitThreadCore();
        }
    }

    // Hidden window that receives WM_CLIPBOARDUPDATE and WM_HOTKEY.
    class ListenerWindow : NativeWindow, IDisposable
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0xC0DE;
        const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        public event EventHandler ClipboardChanged;
        public event EventHandler HotkeyPressed;
        bool hotkeyRegistered;

        public ListenerWindow()
        {
            var cp = new CreateParams();
            cp.Parent = new IntPtr(-3); // HWND_MESSAGE: message-only window
            CreateHandle(cp);
            NativeMethods.AddClipboardFormatListener(Handle);
        }

        public bool RegisterHotkey()
        {
            hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId,
                MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.V);
            return hotkeyRegistered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                if (ClipboardChanged != null) ClipboardChanged(this, EventArgs.Empty);
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                if (HotkeyPressed != null) HotkeyPressed(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                NativeMethods.RemoveClipboardFormatListener(Handle);
                if (hotkeyRegistered) NativeMethods.UnregisterHotKey(Handle, HotkeyId);
                DestroyHandle();
            }
        }
    }

    static class ClipboardHelper
    {
        // The clipboard is often briefly locked by the app that just wrote to it.
        public static string TryGetText()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return null;
                    return Clipboard.GetText(TextDataFormat.UnicodeText);
                }
                catch (ExternalException) { Thread.Sleep(30); }
            }
            return null;
        }

        public static bool TrySetText(string text)
        {
            try
            {
                Clipboard.SetDataObject(text, true, 10, 50);
                return true;
            }
            catch (ExternalException) { return false; }
        }
    }

    // ------------------------------------------------------------------
    // Popup list
    // ------------------------------------------------------------------
    class ItemChosenEventArgs : EventArgs
    {
        public string Text;
        public IntPtr PasteInto;
    }

    class HistoryPopup : Form
    {
        static readonly Color Bg = Color.FromArgb(32, 32, 36);
        static readonly Color BgAlt = Color.FromArgb(40, 40, 45);
        static readonly Color Fg = Color.FromArgb(235, 235, 240);
        static readonly Color Muted = Color.FromArgb(140, 140, 150);
        static readonly Color Accent = Color.FromArgb(0, 120, 215);

        readonly ClipHistory history;
        readonly TextBox search;
        readonly ListBox list;
        readonly Label footer;
        readonly List<string> shown = new List<string>();
        IntPtr pasteTarget;

        public DateTime LastHiddenUtc { get; private set; }

        public event EventHandler<ItemChosenEventArgs> ItemChosen;

        public HistoryPopup(ClipHistory history, Icon icon)
        {
            this.history = history;
            Text = "CopyPaste";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Color.FromArgb(70, 70, 78); // 1px border colour
            Padding = new Padding(1);
            Font = new Font("Segoe UI", 9.5f);

            int scale = Math.Max(96, DeviceDpi());
            Size = new Size(420 * scale / 96, 520 * scale / 96);

            var inner = new Panel();
            inner.Dock = DockStyle.Fill;
            inner.BackColor = Bg;
            inner.Padding = new Padding(8 * scale / 96);

            var searchHost = new Panel();
            searchHost.Dock = DockStyle.Top;
            searchHost.Height = Font.Height + 14 * scale / 96;
            searchHost.BackColor = BgAlt;
            searchHost.Padding = new Padding(6 * scale / 96, 5 * scale / 96, 6 * scale / 96, 0);

            search = new TextBox();
            search.Dock = DockStyle.Fill;
            search.BorderStyle = BorderStyle.None;
            search.BackColor = BgAlt;
            search.ForeColor = Fg;
            search.TextChanged += delegate { Refill(); };
            NativeMethods.SetCueBanner(search, "Search clipboard history...");
            searchHost.Controls.Add(search);

            var gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = 6 * scale / 96;
            gap.BackColor = Bg;

            list = new ListBox();
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Bg;
            list.ForeColor = Fg;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Font.Height * 2 + 10 * scale / 96;
            list.IntegralHeight = false;
            list.DrawItem += DrawListItem;
            list.MouseClick += delegate(object s, MouseEventArgs e)
            {
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0) Choose(i);
            };
            list.MouseMove += delegate(object s, MouseEventArgs e)
            {
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0 && i != list.SelectedIndex) list.SelectedIndex = i;
            };

            footer = new Label();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Font.Height + 8 * scale / 96;
            footer.ForeColor = Muted;
            footer.BackColor = Bg;
            footer.TextAlign = ContentAlignment.BottomLeft;
            footer.Font = new Font("Segoe UI", 8.25f);

            inner.Controls.Add(list);
            inner.Controls.Add(gap);
            inner.Controls.Add(searchHost);
            inner.Controls.Add(footer);
            Controls.Add(inner);

            history.Changed += delegate { if (Visible) Refill(); };
        }

        static int DeviceDpi()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return (int)g.DpiX;
        }

        public void ShowNearTray(IntPtr target)
        {
            // Bottom-right of the screen, just above the taskbar, like the
            // volume / network flyouts.
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int m = 12;
            Location = new Point(wa.Right - Width - m, wa.Bottom - Height - m);
            Open(target, "Click an item to copy it");
        }

        public void ShowNearCursor(IntPtr target)
        {
            Point p = Cursor.Position;
            Rectangle wa = Screen.FromPoint(p).WorkingArea;
            int x = Math.Min(Math.Max(p.X, wa.Left), wa.Right - Width);
            int y = Math.Min(Math.Max(p.Y, wa.Top), wa.Bottom - Height);
            Location = new Point(x, y);
            Open(target, "Enter to paste  ·  Del to remove  ·  Esc to close");
        }

        void Open(IntPtr target, string hint)
        {
            pasteTarget = target;
            footer.Text = hint;
            search.Text = "";
            Refill();
            Show();
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            search.Focus();
        }

        void Refill()
        {
            string q = search.Text.Trim();
            shown.Clear();
            foreach (string s in history.Items)
                if (q.Length == 0 || s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    shown.Add(s);

            list.BeginUpdate();
            list.Items.Clear();
            foreach (string s in shown) list.Items.Add(s);
            if (shown.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
            list.Invalidate();
        }

        void DrawListItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            Rectangle r = e.Bounds;

            using (var b = new SolidBrush(selected ? Accent : (e.Index % 2 == 0 ? Bg : BgAlt)))
                g.FillRectangle(b, r);

            string text = shown[e.Index];
            string oneLine = text.Replace("\r\n", " ↵ ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            if (oneLine.Length > 300) oneLine = oneLine.Substring(0, 300);

            int pad = 8;
            int numW = TextRenderer.MeasureText("50", Font).Width + 4;
            string num = (e.Index + 1).ToString();
            var numRect = new Rectangle(r.X + pad, r.Y + 5, numW, Font.Height);
            TextRenderer.DrawText(g, num, Font, numRect, selected ? Color.White : Muted,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            var textRect = new Rectangle(r.X + pad + numW + 6, r.Y + 5, r.Width - numW - pad * 2 - 6, r.Height - 8);
            TextRenderer.DrawText(g, oneLine, Font, textRect, selected ? Color.White : Fg,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                TextFormatFlags.TextBoxControl);
        }

        void Choose(int index)
        {
            if (index < 0 || index >= shown.Count) return;
            string text = shown[index];
            IntPtr target = pasteTarget;
            Hide();
            if (ItemChosen != null)
            {
                var args = new ItemChosenEventArgs();
                args.Text = text;
                args.PasteInto = target;
                ItemChosen(this, args);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    Hide();
                    return true;
                case Keys.Enter:
                    Choose(list.SelectedIndex);
                    return true;
                case Keys.Down:
                    if (list.SelectedIndex < list.Items.Count - 1) list.SelectedIndex++;
                    return true;
                case Keys.Up:
                    if (list.SelectedIndex > 0) list.SelectedIndex--;
                    return true;
                case Keys.PageDown:
                    if (list.Items.Count > 0) list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 8);
                    return true;
                case Keys.PageUp:
                    if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, list.SelectedIndex - 8);
                    return true;
                case Keys.Delete:
                    // Delete removes the highlighted clip unless you're editing the search text.
                    if (search.Text.Length == 0 && list.SelectedIndex >= 0)
                    {
                        int i = list.SelectedIndex;
                        history.Remove(shown[i]);
                        if (list.Items.Count > 0) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
                        return true;
                    }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) LastHiddenUtc = DateTime.UtcNow;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                return cp;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------
    // Tray icon drawn at runtime (a little clipboard), so no .ico is needed.
    // ------------------------------------------------------------------
    static class IconFactory
    {
        public static Icon Create()
        {
            int s = 32;
            using (var bmp = new Bitmap(s, s))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (var board = RoundedRect(new RectangleF(4, 4, 24, 27), 4))
                    using (var fill = new SolidBrush(Color.FromArgb(0, 120, 215)))
                        g.FillPath(fill, board);

                    using (var paper = new SolidBrush(Color.White))
                        g.FillRectangle(paper, 8, 9, 16, 18);

                    using (var clip = RoundedRect(new RectangleF(10, 1, 12, 7), 2))
                    using (var clipBrush = new SolidBrush(Color.FromArgb(60, 60, 70)))
                        g.FillPath(clipBrush, clip);

                    using (var line = new Pen(Color.FromArgb(0, 120, 215), 2))
                    {
                        g.DrawLine(line, 11, 14, 21, 14);
                        g.DrawLine(line, 11, 18, 21, 18);
                        g.DrawLine(line, 11, 22, 17, 22);
                    }
                }
                IntPtr h = bmp.GetHicon();
                // Clone so the Icon owns its own handle, then free the GDI one.
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                NativeMethods.DestroyIcon(h);
                return icon;
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetCueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            box.HandleCreated += delegate { SendMessage(box.Handle, EM_SETCUEBANNER, IntPtr.Zero, text); };
        }
    }
}
