// wezterm-quickterminal helper. Compiled at runtime by Windows PowerShell 5.1 (Add-Type), so this file
// must stay C# 5: no string interpolation, ?., expression-bodied members, out var, or records.
//
// Runs hidden in the background: registers the global hotkey, spawns a dedicated WezTerm
// (--class wezterm-quickterminal, WEZTERM_QUICKTERMINAL=1) and shows / focuses / hides it as a drop-down.
// Settings come from %APPDATA%\wezterm-quickterminal\settings.json, written by plugin/init.lua.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WeztermQuickTerminal
{
    internal static class N
    {
        public const int WM_CLOSE = 0x0010;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_APP = 0x8000;

        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        public const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
            SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;

        public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
        public const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern short VkKeyScanW(char ch);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    }

    internal static class Log
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wezterm-quickterminal"); }
        }

        public static void Info(string message) { Write("INFO ", message); }
        public static void Error(string message) { Write("ERROR", message); }

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "wezterm-quickterminal.log");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>Settings exported from .wezterm.lua by plugin/init.lua.</summary>
    internal sealed class Settings
    {
        public string Hotkey = "Win+`";
        public double HeightPercent = 40;
        public bool HideOnFocusLoss = true;
        public string WeztermPath;
        public bool Autostart;
        public string HelperScript;
        /// <summary>position = "top" in Lua; otherwise the window sits on the bottom edge.</summary>
        public bool AnchorTop;

        public static string FilePath { get { return Path.Combine(Log.Dir, "settings.json"); } }

        /// <summary>Null (with an error) when the file exists but can't be read yet.</summary>
        public static Settings Load(out string error)
        {
            error = null;
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath));
                if (d == null) return s;
                object v;
                if (d.TryGetValue("hotkey", out v) && v is string && ((string)v).Trim().Length > 0) s.Hotkey = (string)v;
                if (d.TryGetValue("heightPercent", out v) && v != null) s.HeightPercent = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d.TryGetValue("hideOnFocusLoss", out v) && v is bool) s.HideOnFocusLoss = (bool)v;
                if (d.TryGetValue("weztermPath", out v) && v is string) s.WeztermPath = (string)v;
                if (d.TryGetValue("autostart", out v) && v is bool) s.Autostart = (bool)v;
                if (d.TryGetValue("helperScript", out v) && v is string) s.HelperScript = (string)v;
                if (d.TryGetValue("position", out v) && v is string)
                    s.AnchorTop = string.Equals(((string)v).Trim(), "top", StringComparison.OrdinalIgnoreCase);
                if (double.IsNaN(s.HeightPercent)) s.HeightPercent = 40;
                s.HeightPercent = Math.Max(10, Math.Min(100, s.HeightPercent));
                return s;
            }
            catch (Exception ex)
            {
                error = "Couldn't read " + FilePath + ": " + ex.Message;
                return null;
            }
        }
    }

    /// <summary>A manual resize, remembered until "height" changes in .wezterm.lua (state.json).</summary>
    internal sealed class State
    {
        public double? HeightPercent;
        public double? HeightFromLua;

        private static string FilePath { get { return Path.Combine(Log.Dir, "state.json"); } }

        public double EffectiveHeight(Settings s)
        {
            return HeightPercent.HasValue && HeightFromLua == s.HeightPercent ? HeightPercent.Value : s.HeightPercent;
        }

        public void Reconcile(Settings s)
        {
            if (HeightPercent.HasValue && HeightFromLua != s.HeightPercent)
            {
                HeightPercent = null;
                HeightFromLua = null;
                Save();
            }
        }

        public void SetHeight(double percent, Settings s)
        {
            HeightPercent = percent;
            HeightFromLua = s.HeightPercent;
            Save();
        }

        public static State Load()
        {
            State st = new State();
            try
            {
                if (!File.Exists(FilePath)) return st;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath));
                object v;
                if (d != null && d.TryGetValue("heightPercent", out v) && v != null) st.HeightPercent = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d != null && d.TryGetValue("heightFromLua", out v) && v != null) st.HeightFromLua = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't read state.json, starting fresh: " + ex.Message);
            }
            return st;
        }

        private void Save()
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["heightPercent"] = HeightPercent;
                d["heightFromLua"] = HeightFromLua;
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(d));
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't save state.json: " + ex.Message);
            }
        }
    }

    internal struct Hotkey
    {
        public uint Modifiers;
        public uint VirtualKey;
        public string Display;

        private static readonly Dictionary<string, uint> Named = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Space", 0x20 }, { "Tab", 0x09 }, { "Enter", 0x0D }, { "Return", 0x0D }, { "Esc", 0x1B }, { "Escape", 0x1B },
            { "Backspace", 0x08 }, { "Insert", 0x2D }, { "Ins", 0x2D }, { "Delete", 0x2E }, { "Del", 0x2E },
            { "Home", 0x24 }, { "End", 0x23 }, { "PageUp", 0x21 }, { "PageDown", 0x22 },
            { "Up", 0x26 }, { "Down", 0x28 }, { "Left", 0x25 }, { "Right", 0x27 },
            { "Pause", 0x13 }, { "ScrollLock", 0x91 }, { "PrintScreen", 0x2C },
            { "Backquote", 0xC0 }, { "Backtick", 0xC0 }, { "Grave", 0xC0 }, { "Tilde", 0xC0 },
        };

        private static readonly Dictionary<char, uint> Punctuation = new Dictionary<char, uint>
        {
            { '`', 0xC0 }, { '-', 0xBD }, { '=', 0xBB }, { '[', 0xDB }, { ']', 0xDD }, { '\\', 0xDC },
            { ';', 0xBA }, { '\'', 0xDE }, { ',', 0xBC }, { '.', 0xBE }, { '/', 0xBF }, { '+', 0xBB },
        };

        public static bool TryParse(string text, out Hotkey hotkey, out string error)
        {
            hotkey = new Hotkey();
            error = "";
            string s = text.Trim();
            string keyPart, modsPart;
            if (s.EndsWith("++", StringComparison.Ordinal))
            {
                keyPart = "+";
                modsPart = s.Substring(0, s.Length - 2);
            }
            else
            {
                int i = s.LastIndexOf('+');
                keyPart = (i < 0 ? s : s.Substring(i + 1)).Trim();
                modsPart = i < 0 ? "" : s.Substring(0, i);
            }

            uint mods = 0;
            foreach (string raw in modsPart.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= N.MOD_CONTROL; break;
                    case "alt": mods |= N.MOD_ALT; break;
                    case "shift": mods |= N.MOD_SHIFT; break;
                    case "win": case "super": case "meta": case "windows": mods |= N.MOD_WIN; break;
                    default:
                        error = "Unknown modifier \"" + raw.Trim() + "\" in hotkey \"" + text + "\". Use Ctrl, Alt, Shift or Win.";
                        return false;
                }
            }

            uint vk;
            string keyDisplay;
            if (keyPart.Length == 0 || !TryParseKey(keyPart, out vk, out keyDisplay))
            {
                error = "Unknown key \"" + keyPart + "\" in hotkey \"" + text + "\".";
                return false;
            }

            var parts = new List<string>();
            if ((mods & N.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & N.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & N.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & N.MOD_WIN) != 0) parts.Add("Win");
            parts.Add(keyDisplay);

            hotkey.Modifiers = mods;
            hotkey.VirtualKey = vk;
            hotkey.Display = string.Join("+", parts.ToArray());
            return true;
        }

        private static bool TryParseKey(string key, out uint vk, out string display)
        {
            display = key;
            vk = 0;
            if (key.Length == 1)
            {
                char c = key[0];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = char.ToUpperInvariant(c);
                    display = ((char)vk).ToString();
                    return true;
                }
                short scan = N.VkKeyScanW(c);
                if (scan != -1)
                {
                    vk = (uint)(scan & 0xFF);
                    return true;
                }
                return Punctuation.TryGetValue(c, out vk);
            }

            int n;
            if ((key[0] == 'F' || key[0] == 'f') && int.TryParse(key.Substring(1), out n) && n >= 1 && n <= 24)
            {
                vk = (uint)(0x70 + n - 1);
                display = "F" + n;
                return true;
            }
            if (key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.Substring(6), out n) && n >= 0 && n <= 9)
            {
                vk = (uint)(0x60 + n);
                display = "Numpad" + n;
                return true;
            }
            return Named.TryGetValue(key, out vk);
        }
    }

    internal static class Monitors
    {
        public sealed class Info
        {
            public string Device;
            public N.RECT Work;
            public bool Primary;
        }

        public static Info Get(IntPtr monitor)
        {
            var mi = new N.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFOEX));
            if (!N.GetMonitorInfoW(monitor, ref mi)) return null;
            var info = new Info();
            info.Device = mi.szDevice;
            info.Work = mi.rcWork;
            info.Primary = (mi.dwFlags & N.MONITORINFOF_PRIMARY) != 0;
            return info;
        }

        public static Info Primary()
        {
            return Get(N.MonitorFromPoint(new N.POINT(), N.MONITOR_DEFAULTTOPRIMARY));
        }

        public static Info ForWindow(IntPtr hwnd)
        {
            return Get(N.MonitorFromWindow(hwnd, N.MONITOR_DEFAULTTONEAREST)) ?? Primary();
        }

        /// <summary>The monitor the mouse cursor is on.</summary>
        public static Info UnderCursor()
        {
            N.POINT pt;
            N.GetCursorPos(out pt);
            return Get(N.MonitorFromPoint(pt, N.MONITOR_DEFAULTTONEAREST)) ?? Primary();
        }
    }

    /// <summary>Owns the dedicated quick-terminal WezTerm: spawns it lazily, then shows / focuses / hides it.</summary>
    internal sealed class Controller
    {
        public const string WindowClass = "wezterm-quickterminal";
        private readonly Helper _app;
        private IntPtr _hwnd;
        private bool _styled;
        private bool _spawning;
        private DateTime _spawnDeadline;
        private N.RECT _frameInset;
        private readonly System.Windows.Forms.Timer _spawnTimer = new System.Windows.Forms.Timer();

        public Controller(Helper app)
        {
            _app = app;
            _spawnTimer.Interval = 50;
            _spawnTimer.Tick += delegate { OnSpawnTimer(); };
        }

        /// <summary>not running → spawn · hidden → show · visible, unfocused → focus · focused → hide.</summary>
        public void Toggle()
        {
            if (_spawning) return;
            IntPtr hwnd = Locate();
            if (hwnd == IntPtr.Zero) Spawn();
            // Not yet styled = just adopted (e.g. after a helper restart): give it the quick-terminal placement.
            else if (!N.IsWindowVisible(hwnd) || N.IsIconic(hwnd) || !_styled) Show(hwnd);
            else if (N.GetForegroundWindow() != hwnd)
            {
                N.SetWindowPos(hwnd, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE);
                Focus(hwnd);
            }
            else Hide(hwnd);
        }

        public void OnForegroundChanged(IntPtr foreground)
        {
            if (!_app.Settings.HideOnFocusLoss || _spawning) return;
            IntPtr hwnd = Locate();
            // Only the drop-down; never a normal WezTerm window that was handed back on quit.
            if (hwnd == IntPtr.Zero || foreground == hwnd || !N.IsWindowVisible(hwnd) || !IsDropDown(hwnd)) return;

            // WezTerm's own dialogs and popups belong to the quick-terminal process.
            uint termPid, fgPid;
            N.GetWindowThreadProcessId(hwnd, out termPid);
            if (foreground != IntPtr.Zero)
            {
                N.GetWindowThreadProcessId(foreground, out fgPid);
                if (fgPid == termPid) return;
            }
            Hide(hwnd);
        }

        public void Relayout()
        {
            IntPtr hwnd = Locate();
            // Already on screen: stay on its current monitor instead of jumping to the mouse.
            if (hwnd != IntPtr.Zero && N.IsWindowVisible(hwnd) && IsDropDown(hwnd))
                Layout(hwnd, N.SWP_NOACTIVATE, Monitors.ForWindow(hwnd));
        }

        /// <summary>On quit: hand the window back as a normal WezTerm window.</summary>
        public void Release()
        {
            IntPtr hwnd = Locate();
            if (hwnd == IntPtr.Zero || !IsDropDown(hwnd)) return; // already a normal window
            SaveHeight(hwnd);
            N.ShowWindow(hwnd, N.SW_HIDE);
            long ex = N.GetWindowLongPtrW(hwnd, N.GWL_EXSTYLE).ToInt64();
            N.SetWindowLongPtrW(hwnd, N.GWL_EXSTYLE, new IntPtr((ex & ~N.WS_EX_TOOLWINDOW) | N.WS_EX_APPWINDOW));
            N.SetWindowPos(hwnd, N.HWND_NOTOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_FRAMECHANGED | N.SWP_SHOWWINDOW);
            Focus(hwnd);
            _styled = false;
        }

        private IntPtr Locate()
        {
            if (_hwnd != IntPtr.Zero && N.IsWindow(_hwnd)) return _hwnd;
            _hwnd = N.FindWindowW(WindowClass, null);
            _styled = false;
            return _hwnd;
        }

        private string WeztermGui()
        {
            string p = _app.Settings.WeztermPath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WezTerm\\wezterm-gui.exe");
            return File.Exists(p) ? p : null;
        }

        private void Spawn()
        {
            string exe = WeztermGui();
            if (exe == null)
            {
                Log.Error("Couldn't find wezterm-gui.exe.");
                return;
            }

            var psi = new ProcessStartInfo(exe, "start --always-new-process --class " + WindowClass);
            psi.UseShellExecute = false;
            // Don't leak the launching WezTerm's GUI variables (e.g. its socket) into the quick-terminal instance.
            var inherited = new List<string>();
            foreach (System.Collections.DictionaryEntry e in psi.EnvironmentVariables) inherited.Add((string)e.Key);
            foreach (string key in inherited)
                if (key.StartsWith("WEZTERM_", StringComparison.OrdinalIgnoreCase)) psi.EnvironmentVariables.Remove(key);
            psi.EnvironmentVariables["WEZTERM_QUICKTERMINAL"] = "1";

            try
            {
                using (Process.Start(psi)) { }
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't start WezTerm: " + ex.Message);
                return;
            }
            Log.Info("Started " + exe);
            _spawning = true;
            _spawnDeadline = DateTime.UtcNow.AddSeconds(15);
            _spawnTimer.Start();
        }

        private void OnSpawnTimer()
        {
            IntPtr hwnd = N.FindWindowW(WindowClass, null);
            if (hwnd != IntPtr.Zero && N.IsWindowVisible(hwnd))
            {
                _spawnTimer.Stop();
                _spawning = false;
                _hwnd = hwnd;
                _styled = false;
                Show(hwnd);
            }
            else if (DateTime.UtcNow > _spawnDeadline)
            {
                _spawnTimer.Stop();
                _spawning = false;
                Log.Error("No quick-terminal window appeared within 15 seconds.");
            }
        }

        private void Show(IntPtr hwnd)
        {
            EnsureStyle(hwnd);
            if (N.IsIconic(hwnd)) N.ShowWindow(hwnd, N.SW_RESTORE);
            Layout(hwnd, N.SWP_SHOWWINDOW, Monitors.UnderCursor()); // open where the mouse is
            Focus(hwnd);
        }

        private void Hide(IntPtr hwnd)
        {
            SaveHeight(hwnd);
            N.ShowWindow(hwnd, N.SW_HIDE);
        }

        private void Layout(IntPtr hwnd, uint flags, Monitors.Info mon)
        {
            if (mon == null) return;
            N.RECT work = mon.Work;
            int height = (int)Math.Round(work.Height * _app.State.EffectiveHeight(_app.Settings) / 100.0);
            Place(hwnd, work, height, flags);

            N.RECT inset = MeasureFrameInset(hwnd);
            if (inset.Left != _frameInset.Left || inset.Top != _frameInset.Top
                || inset.Right != _frameInset.Right || inset.Bottom != _frameInset.Bottom)
            {
                _frameInset = inset;
                Place(hwnd, work, height, flags);
            }
        }

        private void Place(IntPtr hwnd, N.RECT work, int height, uint flags)
        {
            N.RECT i = _frameInset;
            // Top or bottom edge of the work area (above the taskbar when it's at the bottom).
            int visibleTop = _app.Settings.AnchorTop ? work.Top : work.Bottom - height;
            N.SetWindowPos(hwnd, N.HWND_TOPMOST, work.Left - i.Left, visibleTop - i.Top,
                work.Width + i.Left + i.Right, height + i.Top + i.Bottom, flags);
        }

        /// <summary>Invisible resize borders: the difference between GetWindowRect and the DWM frame.</summary>
        private static N.RECT MeasureFrameInset(IntPtr hwnd)
        {
            N.RECT outer, visible;
            var inset = new N.RECT();
            if (!N.GetWindowRect(hwnd, out outer)) return inset;
            if (N.DwmGetWindowAttribute(hwnd, N.DWMWA_EXTENDED_FRAME_BOUNDS, out visible, Marshal.SizeOf(typeof(N.RECT))) != 0) return inset;
            inset.Left = visible.Left - outer.Left;
            inset.Top = visible.Top - outer.Top;
            inset.Right = outer.Right - visible.Right;
            inset.Bottom = outer.Bottom - visible.Bottom;
            return inset;
        }

        private void SaveHeight(IntPtr hwnd)
        {
            N.RECT rect;
            if (!IsDropDown(hwnd) || !N.IsWindowVisible(hwnd) || N.IsIconic(hwnd) || !N.GetWindowRect(hwnd, out rect)) return;
            N.RECT inset = MeasureFrameInset(hwnd);
            int visibleHeight = rect.Height - inset.Top - inset.Bottom;
            N.RECT work = Monitors.ForWindow(hwnd).Work;
            if (work.Height <= 0) return;

            double percent = Math.Max(10, Math.Min(100, Math.Round(visibleHeight * 100.0 / work.Height, 1)));
            if (Math.Abs(percent - _app.State.EffectiveHeight(_app.Settings)) < 0.5) return;
            Log.Info("Remembering resized height " + percent + "% (" + visibleHeight + "px)");
            _app.State.SetHeight(percent, _app.Settings);
        }

        /// <summary>True while the window has the drop-down style (hidden from the taskbar by us).</summary>
        private static bool IsDropDown(IntPtr hwnd)
        {
            return (N.GetWindowLongPtrW(hwnd, N.GWL_EXSTYLE).ToInt64() & N.WS_EX_TOOLWINDOW) != 0;
        }

        /// <summary>Hide from the taskbar and Alt+Tab.</summary>
        private void EnsureStyle(IntPtr hwnd)
        {
            if (_styled) return;
            long ex = N.GetWindowLongPtrW(hwnd, N.GWL_EXSTYLE).ToInt64();
            long wanted = (ex | N.WS_EX_TOOLWINDOW) & ~N.WS_EX_APPWINDOW;
            if (wanted != ex)
            {
                if (N.IsWindowVisible(hwnd)) N.ShowWindow(hwnd, N.SW_HIDE); // the taskbar only notices while hidden
                N.SetWindowLongPtrW(hwnd, N.GWL_EXSTYLE, new IntPtr(wanted));
                N.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOZORDER | N.SWP_NOACTIVATE | N.SWP_FRAMECHANGED);
            }
            _styled = true;
        }

        private static void Focus(IntPtr hwnd)
        {
            if (N.SetForegroundWindow(hwnd) && N.GetForegroundWindow() == hwnd) return;
            uint dummy;
            uint fgThread = N.GetWindowThreadProcessId(N.GetForegroundWindow(), out dummy);
            uint me = N.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && N.AttachThreadInput(me, fgThread, true);
            N.BringWindowToTop(hwnd);
            N.SetForegroundWindow(hwnd);
            if (attached) N.AttachThreadInput(me, fgThread, false);
        }
    }

    /// <summary>Hidden message window: hotkey, quit requests, display changes, settings changes.</summary>
    internal sealed class HelperWindow : NativeWindow
    {
        public const string TitlePrefix = "wezterm-quickterminal-helper|";
        public const int WM_SETTINGS_CHANGED = N.WM_APP + 4;
        /// <summary>A newer helper is taking over: exit without touching the drop-down.</summary>
        public const int WM_HANDOFF = N.WM_APP + 5;
        private readonly Helper _app;

        public HelperWindow(Helper app, string version)
        {
            _app = app;
            var cp = new CreateParams();
            cp.Caption = TitlePrefix + version;
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                switch (m.Msg)
                {
                    case N.WM_HOTKEY: _app.Term.Toggle(); return;
                    case N.WM_CLOSE: _app.Quit(true); return;
                    case WM_HANDOFF: _app.Quit(false); return;
                    case N.WM_DISPLAYCHANGE: _app.Term.Relayout(); break;
                    case WM_SETTINGS_CHANGED: _app.OnSettingsChanged(); return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
            base.WndProc(ref m);
        }
    }

    public sealed class Helper
    {
        private const string MutexName = "Local\\wezterm-quickterminal-helper";
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const int HotkeyId = 1;

        private readonly string _version;
        private HelperWindow _window;
        private IntPtr _hook;
        private N.WinEventProc _hookProc; // keep the delegate alive for native code
        private FileSystemWatcher _watcher;
        private readonly System.Windows.Forms.Timer _settingsTimer = new System.Windows.Forms.Timer();
        private bool _hotkeyRegistered;

        internal Settings Settings;
        internal State State;
        internal Controller Term;

        private Helper(string version)
        {
            _version = version;
        }

        /// <summary>Entry point called from quickterminal.ps1.</summary>
        public static void Start(string version, bool quit)
        {
            if (quit)
            {
                string ignored;
                IntPtr other = FindRunning(out ignored);
                if (other == IntPtr.Zero) return;
                N.PostMessageW(other, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                // Wait until it's gone, so a restart right after this doesn't find it still running.
                for (int i = 0; i < 60 && N.IsWindow(other); i++) Thread.Sleep(50);
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    string otherVersion;
                    IntPtr other = FindRunning(out otherVersion);
                    if (other != IntPtr.Zero && otherVersion == version) return; // already running

                    try
                    {
                        bool acquired = false;
                        if (other != IntPtr.Zero)
                        {
                            Log.Info("Replacing helper " + otherVersion + " with " + version);
                            N.PostMessageW(other, HelperWindow.WM_HANDOFF, IntPtr.Zero, IntPtr.Zero);
                            acquired = mutex.WaitOne(1500);
                            if (!acquired && N.IsWindow(other))
                            {
                                // Helpers older than the handoff message only understand WM_CLOSE.
                                N.PostMessageW(other, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        // No window: the other helper is either just exiting (it releases the lock in a
                        // moment) or still starting (then we time out and leave it running).
                        if (!acquired && !mutex.WaitOne(3000)) return;
                    }
                    catch (AbandonedMutexException) { }
                }

                try
                {
                    new Helper(version).Run();
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static IntPtr FindRunning(out string version)
        {
            IntPtr found = IntPtr.Zero;
            string foundVersion = null;
            N.EnumWindows(delegate (IntPtr hwnd, IntPtr data)
            {
                var sb = new StringBuilder(256);
                N.GetWindowTextW(hwnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.StartsWith(HelperWindow.TitlePrefix, StringComparison.Ordinal))
                {
                    found = hwnd;
                    foundVersion = title.Substring(HelperWindow.TitlePrefix.Length);
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            version = foundVersion;
            return found;
        }

        private void Run()
        {
            // Per-monitor DPI awareness (-4 = PER_MONITOR_AWARE_V2) so coordinates are physical pixels.
            try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            Directory.CreateDirectory(Log.Dir);
            string error;
            Settings = Settings.Load(out error) ?? new Settings();
            if (error != null) Log.Error(error);
            State = State.Load();
            State.Reconcile(Settings);

            _window = new HelperWindow(this, _version);
            Term = new Controller(this);
            RegisterHotkey();
            ApplyAutostart();

            _hookProc = delegate (IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                try
                {
                    if (evt == N.EVENT_SYSTEM_FOREGROUND) Term.OnForegroundChanged(hwnd);
                }
                catch (Exception ex) { Log.Error(ex.ToString()); }
            };
            _hook = N.SetWinEventHook(N.EVENT_SYSTEM_FOREGROUND, N.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc,
                0, 0, N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);

            _settingsTimer.Interval = 300;
            _settingsTimer.Tick += delegate { _settingsTimer.Stop(); ReloadSettings(); };
            WatchSettings();

            Log.Info("Helper " + _version + " started, hotkey " + Settings.Hotkey + (_hotkeyRegistered ? "" : " (NOT registered)"));
            Application.Run();
        }

        private void WatchSettings()
        {
            try
            {
                IntPtr hwnd = _window.Handle;
                _watcher = new FileSystemWatcher(Log.Dir, "settings.json");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
                FileSystemEventHandler post = delegate { N.PostMessageW(hwnd, HelperWindow.WM_SETTINGS_CHANGED, IntPtr.Zero, IntPtr.Zero); };
                _watcher.Changed += post;
                _watcher.Created += post;
                _watcher.Renamed += delegate { N.PostMessageW(hwnd, HelperWindow.WM_SETTINGS_CHANGED, IntPtr.Zero, IntPtr.Zero); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Error("Can't watch settings.json: " + ex.Message);
            }
        }

        internal void OnSettingsChanged()
        {
            // WezTerm may write several times in a row; settle first.
            _settingsTimer.Stop();
            _settingsTimer.Start();
        }

        private void ReloadSettings()
        {
            string error;
            Settings loaded = Settings.Load(out error);
            if (loaded == null)
            {
                Log.Error(error); // usually a write in progress; the next change retries
                return;
            }
            bool hotkeyChanged = loaded.Hotkey != Settings.Hotkey;
            Settings = loaded;
            State.Reconcile(Settings);
            Log.Info("Settings reloaded");
            if (hotkeyChanged || !_hotkeyRegistered) RegisterHotkey();
            ApplyAutostart();
            Term.Relayout();
        }

        private void RegisterHotkey()
        {
            N.UnregisterHotKey(_window.Handle, HotkeyId);
            _hotkeyRegistered = false;
            Hotkey hk;
            string error;
            if (!Hotkey.TryParse(Settings.Hotkey, out hk, out error))
            {
                Log.Error(error + " Fix \"hotkey\" in your .wezterm.lua.");
                return;
            }
            _hotkeyRegistered = N.RegisterHotKey(_window.Handle, HotkeyId, hk.Modifiers | N.MOD_NOREPEAT, hk.VirtualKey);
            if (_hotkeyRegistered) Log.Info("Hotkey " + hk.Display + " registered (mods 0x" + hk.Modifiers.ToString("X") + ", vk 0x" + hk.VirtualKey.ToString("X") + ")");
            else Log.Error("Hotkey " + hk.Display + " is already used by another app (error " + Marshal.GetLastWin32Error()
                + "). Change \"hotkey\" in your .wezterm.lua.");
        }

        /// <summary>Starts the helper at login (no WezTerm window needed) when autostart = true.</summary>
        private void ApplyAutostart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (Settings.Autostart && !string.IsNullOrEmpty(Settings.HelperScript))
                    {
                        string command = "conhost.exe --headless powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass "
                            + "-WindowStyle Hidden -File \"" + Settings.HelperScript + "\" -Version " + _version;
                        if ((key.GetValue("wezterm-quickterminal") as string) != command) key.SetValue("wezterm-quickterminal", command);
                    }
                    else if (key.GetValue("wezterm-quickterminal") != null)
                    {
                        key.DeleteValue("wezterm-quickterminal", false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't update autostart: " + ex.Message);
            }
        }

        /// <param name="release">True for an explicit stop: hand the drop-down back as a normal window.
        /// False when a newer helper takes over: leave it as it is so the new helper adopts it.</param>
        internal void Quit(bool release)
        {
            Log.Info("Helper " + _version + (release ? " quitting" : " handing over"));
            if (_watcher != null) _watcher.Dispose();
            if (_hook != IntPtr.Zero) N.UnhookWinEvent(_hook);
            if (release) Term.Release();
            N.UnregisterHotKey(_window.Handle, HotkeyId);
            _window.DestroyHandle();
            Application.ExitThread();
        }
    }
}
