using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TroiaLens.Npp;

namespace TroiaLens
{
    internal static class Plugin
    {
        public const string Name = "TroiaLens";

        public static NppData Npp;
        public static readonly FuncItems Funcs = new FuncItems();

        private static int _cmdPanel, _cmdAutoOpen, _cmdTabSwitch;
        private static Settings _settings;
        private static string _configDir;
        private static PanelForm _panel;
        private static Icon _tabIcon;
        private static bool _ready, _panelVisible, _autoOpened;

        // Trace buffers where the user closed the automatically opened panel; it stays closed for them
        private static readonly HashSet<IntPtr> Dismissed = new HashSet<IntPtr>();

        // Document shown in the panel. Line starts map trace lines ("\r?\n" separated, as the parser counts them)
        // to editor byte positions; Scintilla also breaks lines at a lone CR, so its line numbers can differ.
        private static IntPtr _docBuffer;
        private static string _docPath;
        private static long _docLength = -1;
        private static int[] _lineStarts;

        private static string PluginDir => Path.GetDirectoryName(typeof(Plugin).Assembly.Location);

        public static void CommandMenuInit()
        {
            _configDir = GetPluginsConfigDir();
            _settings = new Settings(_configDir);
            _settings.Load();

            _cmdPanel = Funcs.Add("TroiaLens paneli", () => Safe(TogglePanel));
            Funcs.Add("Aktif dosyayı analiz et", () => Safe(AnalyzeCommand));
            Funcs.Add("", null);
            _cmdAutoOpen = Funcs.Add("Trace dosyalarında paneli otomatik aç", () => Safe(ToggleAutoOpen), _settings.AutoOpen);
            _cmdTabSwitch = Funcs.Add("Sekme değişince yeniden analiz et", () => Safe(ToggleTabSwitch), _settings.AnalyzeOnTabSwitch);
            Funcs.Add("", null);
            Funcs.Add("Hakkında", () => Safe(About));
        }

        public static void OnNotification(uint code, IntPtr id)
        {
            Safe(() =>
            {
                switch (code)
                {
                    case NppMsg.NPPN_READY:
                        _ready = true;
                        OnBufferActivated(CurrentBufferId());
                        break;
                    case NppMsg.NPPN_BUFFERACTIVATED:
                        if (_ready) OnBufferActivated(id);
                        break;
                    case NppMsg.NPPN_FILESAVED:
                        if (_ready && _panelVisible && id == CurrentBufferId()) Analyze(force: true);
                        break;
                    case NppMsg.NPPN_DARKMODECHANGED:
                        _panel?.ApplyTheme(IsDarkMode());
                        break;
                    case NppMsg.NPPN_SHUTDOWN:
                        _settings.Save();
                        _panel?.Shutdown();
                        break;
                }
            });
        }

        private static void OnBufferActivated(IntPtr buffer)
        {
            string path = CurrentPath();
            bool traceExt = _settings.IsTraceExtension(path);

            if (traceExt && _settings.AutoOpen && !_panelVisible && !Dismissed.Contains(buffer))
            {
                ShowPanel();
                _autoOpened = true;
                return;
            }
            if (!traceExt && _autoOpened && _panelVisible)
            {
                HidePanel();
                return;
            }
            if (_panelVisible && (traceExt || _settings.AnalyzeOnTabSwitch)) Analyze(force: false);
        }

        // ---- Menu commands ----

        private static void TogglePanel()
        {
            if (_panelVisible)
            {
                if (_settings.IsTraceExtension(CurrentPath())) Dismissed.Add(CurrentBufferId());
                HidePanel();
            }
            else
            {
                Dismissed.Remove(CurrentBufferId());
                ShowPanel();
            }
            _autoOpened = false;
        }

        private static void AnalyzeCommand()
        {
            Dismissed.Remove(CurrentBufferId());
            if (!_panelVisible)
            {
                ShowPanel();
                _autoOpened = false;
            }
            Analyze(force: true);
        }

        private static void ToggleAutoOpen()
        {
            _settings.AutoOpen = !_settings.AutoOpen;
            SetCheck(_cmdAutoOpen, _settings.AutoOpen);
            _settings.Save();
        }

        private static void ToggleTabSwitch()
        {
            _settings.AnalyzeOnTabSwitch = !_settings.AnalyzeOnTabSwitch;
            SetCheck(_cmdTabSwitch, _settings.AnalyzeOnTabSwitch);
            _settings.Save();
        }

        private static void About()
        {
            var v = typeof(Plugin).Assembly.GetName().Version;
            MessageBox.Show(
                $"TroiaLens {v.Major}.{v.Minor}.{v.Build}\n\n" +
                "Canias ERP Troia trace dosyalarını analiz eder.\n" +
                $"Uzantılar: {string.Join(", ", _settings.Extensions)}\n\n" +
                $"Ayarlar: {Path.Combine(_configDir, "TroiaLens.ini")}",
                Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- Panel ----

        private static void ShowPanel()
        {
            if (_panel == null)
            {
                string userData = Path.Combine(_configDir, Name, "WebView2");
                _panel = new PanelForm(Path.Combine(PluginDir, "web"), PluginDir, userData, IsDarkMode(), _settings.DevTools);
                RegisterDock(_panel);
                _panel.Start();
            }
            else
            {
                Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_DMMSHOW, IntPtr.Zero, _panel.Handle);
            }
            _panelVisible = true;
            SetCheck(_cmdPanel, true);
            if (_ready) Analyze(force: false);
        }

        private static void HidePanel()
        {
            if (_panel != null) Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_DMMHIDE, IntPtr.Zero, _panel.Handle);
            _panelVisible = false;
            _autoOpened = false;
            SetCheck(_cmdPanel, false);
        }

        /// <summary>The user closed the panel with its own close button.</summary>
        public static void OnPanelClosed()
        {
            if (_settings.IsTraceExtension(CurrentPath())) Dismissed.Add(CurrentBufferId());
            _panelVisible = false;
            _autoOpened = false;
            SetCheck(_cmdPanel, false);
        }

        public static void ReloadRequested() => Safe(() => Analyze(force: true));

        private static void RegisterDock(PanelForm form)
        {
            _tabIcon = CreateTabIcon();
            var data = new TbData
            {
                hClient = form.Handle,
                pszName = Name,
                // Notepad++ runs this command at startup to restore a panel that was open at shutdown
                dlgID = _cmdPanel,
                uMask = NppMsg.DWS_DF_CONT_RIGHT | NppMsg.DWS_ICONTAB,
                hIconTab = _tabIcon.Handle,
                pszAddInfo = null,
                iPrevCont = -1,
                pszModuleName = Path.GetFileName(typeof(Plugin).Assembly.Location),
            };
            // Notepad++ keeps pointers into this block, so it is never freed
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TbData)));
            Marshal.StructureToPtr(data, ptr, false);
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_DMMREGASDCKDLG, IntPtr.Zero, ptr);
        }

        // ---- Analysis ----

        private static void Analyze(bool force)
        {
            if (_panel == null) return;
            IntPtr buffer = CurrentBufferId();
            IntPtr sci = CurrentScintilla();
            long length = Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64();
            if (!force && buffer == _docBuffer && length == _docLength) return;

            string path = CurrentPath();
            string name = string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;

            _docBuffer = buffer;
            _docPath = path;
            _docLength = length;
            _lineStarts = null;

            if (length > int.MaxValue / 2)
            {
                _panel.ShowNotTrace(name);
                return;
            }

            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(Sci(sci, SciMsg.SCI_GETCHARACTERPOINTER), bytes, 0, (int)length);
            bool utf8 = Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8;

            if (!_settings.IsTraceExtension(path) && !LooksLikeTrace(bytes, utf8))
            {
                _panel.ShowNotTrace(name);
                return;
            }

            _lineStarts = LineStarts(bytes);
            // Non-UTF-8 documents use the system ANSI code page (windows-1254 on Turkish Windows)
            byte[] text = utf8 ? bytes : Encoding.UTF8.GetBytes(Encoding.Default.GetString(bytes));
            _panel.ShowTrace(name, text);
        }

        private static readonly byte[] TraceHeader = Encoding.ASCII.GetBytes("TROIA CODE EXECUTION TRACE");
        private static readonly byte[] ScopeOpen = { 0xE2, 0x96, 0x8E }; // "▎" in UTF-8

        private static bool LooksLikeTrace(byte[] b, bool utf8)
        {
            int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            if (StartsWith(b, start, TraceHeader)) return true;
            if (!utf8) return false;
            int limit = Math.Min(b.Length, 1 << 20) - ScopeOpen.Length;
            for (int i = 0; i <= limit; i++)
                if (b[i] == ScopeOpen[0] && b[i + 1] == ScopeOpen[1] && b[i + 2] == ScopeOpen[2]) return true;
            return false;
        }

        private static bool StartsWith(byte[] b, int at, byte[] prefix)
        {
            if (b.Length - at < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (b[at + i] != prefix[i]) return false;
            return true;
        }

        private static int[] LineStarts(byte[] b)
        {
            var starts = new List<int>(b.Length / 30 + 1) { 0 };
            for (int i = 0; i < b.Length; i++) if (b[i] == (byte)'\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        /// <summary>Moves the editor to a 1-based trace line of the analysed document.</summary>
        public static void GotoLine(int line, bool focus)
        {
            Safe(() =>
            {
                if (_lineStarts == null || _lineStarts.Length == 0) return;
                if (CurrentBufferId() != _docBuffer)
                {
                    if (string.IsNullOrEmpty(_docPath)) return;
                    Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_SWITCHTOFILE, IntPtr.Zero, _docPath);
                    if (CurrentBufferId() != _docBuffer) return;
                }

                IntPtr sci = CurrentScintilla();
                int index = Math.Max(0, Math.Min(_lineStarts.Length - 1, line - 1));
                long pos = Math.Min(_lineStarts[index], Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64());
                long docLine = Sci(sci, SciMsg.SCI_LINEFROMPOSITION, (IntPtr)pos).ToInt64();
                Sci(sci, SciMsg.SCI_ENSUREVISIBLE, (IntPtr)docLine);
                long visible = Sci(sci, SciMsg.SCI_VISIBLEFROMDOCLINE, (IntPtr)docLine).ToInt64();
                long onScreen = Sci(sci, SciMsg.SCI_LINESONSCREEN).ToInt64();
                Sci(sci, SciMsg.SCI_SETFIRSTVISIBLELINE, (IntPtr)Math.Max(0, visible - onScreen / 3));
                Sci(sci, SciMsg.SCI_GOTOPOS, (IntPtr)pos);
                if (focus)
                {
                    Sci(sci, SciMsg.SCI_GRABFOCUS);
                    Win32.SetFocus(sci);
                }
            });
        }

        // ---- Notepad++ helpers ----

        private static IntPtr CurrentScintilla()
        {
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETCURRENTSCINTILLA, IntPtr.Zero, out int which);
            return which == 1 ? Npp.ScintillaSecondHandle : Npp.ScintillaMainHandle;
        }

        private static IntPtr CurrentBufferId() =>
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETCURRENTBUFFERID, IntPtr.Zero, IntPtr.Zero);

        private static string CurrentPath()
        {
            var sb = new StringBuilder(32768);
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETFULLCURRENTPATH, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }

        private static string GetPluginsConfigDir()
        {
            var sb = new StringBuilder(32768);
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETPLUGINSCONFIGDIR, (IntPtr)sb.Capacity, sb);
            string dir = sb.ToString();
            return string.IsNullOrEmpty(dir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notepad++", "plugins", "config")
                : dir;
        }

        private static bool IsDarkMode() =>
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_ISDARKMODEENABLED, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;

        private static void SetCheck(int index, bool on) =>
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_SETMENUITEMCHECK, (IntPtr)Funcs.CmdId(index), (IntPtr)(on ? 1 : 0));

        private static IntPtr Sci(IntPtr sci, uint msg, IntPtr wParam = default, IntPtr lParam = default) =>
            Win32.SendMessage(sci, msg, wParam, lParam);

        private static Icon CreateTabIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var fill = new SolidBrush(Color.FromArgb(10, 127, 117)))
                        g.FillEllipse(fill, 0.5f, 0.5f, 15f, 15f);
                    using (var pen = new Pen(Color.White, 1.6f))
                    {
                        g.DrawEllipse(pen, 3.5f, 3.5f, 6.5f, 6.5f);
                        g.DrawLine(pen, 9.2f, 9.2f, 12.3f, 12.3f);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), Name + " hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
