using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TroiaLens.Npp;

namespace TroiaLens
{
    internal static partial class Plugin
    {
        public const string Name = "TroiaLens";

        public static NppData Npp;
        public static readonly FuncItems Funcs = new FuncItems();

        private static int _cmdPanel, _cmdAutoOpen, _cmdTabSwitch, _cmdSlideOver, _cmdSyntax;
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
        private static string _docPath, _docName;
        private static long _docLength = -1;
        private static int _docGen;
        private static long[] _lineStarts;

        // Edits to each open document, counted. The page keeps analyses of recent documents and shows one again
        // without the text as long as its count has not changed.
        private static readonly Dictionary<IntPtr, int> Generations = new Dictionary<IntPtr, int>();
        // Line starts of recently shown documents, so coming back does not scan the text again
        private static readonly List<(IntPtr Buffer, int Gen, long[] Starts)> RecentLineStarts = new List<(IntPtr, int, long[])>();
        private const int RecentLineStartsKept = 4;

        // Trace line of the editor caret, last reported to or moved by the page; unchanged lines are not reported again
        private static int _caretLine = -1;

        // Editor marks for the selected tree row: its method's span (background + margin bar), its own lines and
        // the method name. Markers and indicators belong to the document, so they follow it across views.
        private static int _markSpan = -1, _markRow, _markBar, _indWord;
        // Indicator for every occurrence of the word double-clicked in the analysed document, and that word
        private static int _indPick;
        private static string _pickedWord;
        // Theme the mark styles were defined for
        private static bool? _marksDark;
        // Span currently drawn ("from:to:word"); moving within one method only moves the row mark
        private static string _markedSpan;
        // Editor lines of that span and of the part of it carrying marks: a long span is marked only around the
        // view, and the marks move along as the view scrolls (a whole 800K-line method would take seconds)
        private static long _spanFirst = -1, _spanLast = -1, _drawnFirst = -1, _drawnLast = -1;
        private static string _spanWord;
        private const int SpanWhole = 50000;
        private const int SpanReach = 5000;
        private const int Amber = 0x0B9EF5; // #F59E0B as Scintilla BGR
        private const int LightYellow = 0x9DF5FF; // #FFF59D
        private const int Teal = 0xA9BB27; // #27BBA9
        private const int Fuchsia = 0xEF46D9; // #D946EF

        private static string PluginDir => Path.GetDirectoryName(typeof(Plugin).Assembly.Location);

        public static void CommandMenuInit()
        {
            _configDir = GetPluginsConfigDir();
            _settings = new Settings(_configDir);
            _settings.Load();
            CleanTemp();

            _cmdPanel = Funcs.Add("TroiaLens paneli", () => Safe(TogglePanel));
            Funcs.Add("Aktif dosyayı analiz et", () => Safe(AnalyzeCommand));
            Funcs.Add("", null);
            _cmdAutoOpen = Funcs.Add("Trace dosyalarında paneli otomatik aç", () => Safe(ToggleAutoOpen), _settings.AutoOpen);
            _cmdTabSwitch = Funcs.Add("Sekme değişince yeniden analiz et", () => Safe(ToggleTabSwitch), _settings.AnalyzeOnTabSwitch);
            _cmdSlideOver = Funcs.Add("Satır ayrıntısını sağda kayan panelde aç", () => Safe(ToggleSlideOver), _settings.DetailSlideOver);
            _cmdSyntax = Funcs.Add("Sözdizimi renkleri", () => Safe(ToggleSyntax), _settings.SyntaxColors);
            Funcs.Add("", null);
            // F12 may not reach the panel: Notepad++ has its own use for it
            if (_settings.DevTools) Funcs.Add("Geliştirici araçları", () => Safe(OpenDevTools));
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
                        // The other view may show a trace as well
                        PaintSyntaxViews();
                        break;
                    case NppMsg.NPPN_BUFFERACTIVATED:
                        if (_ready) OnBufferActivated(id);
                        break;
                    case NppMsg.NPPN_FILESAVED:
                        // Analysed anew if it was edited since
                        if (_ready && _panelVisible && id == CurrentBufferId()) Analyze(force: false);
                        break;
                    case NppMsg.NPPN_DARKMODECHANGED:
                        _panel?.ApplyTheme(IsDarkMode());
                        if (_markSpan >= 0) DefineMarkStyles();
                        SyntaxThemeChanged();
                        break;
                    case NppMsg.NPPN_WORDSTYLESUPDATED:
                        SyntaxThemeChanged();
                        break;
                    case NppMsg.NPPN_FILECLOSED:
                        SyntaxClosed(id);
                        Generations.Remove(id);
                        RecentLineStarts.RemoveAll(r => r.Buffer == id);
                        _panel?.Forget(id);
                        break;
                    case NppMsg.NPPN_SHUTDOWN:
                        // Not saving the settings here: menu changes are saved as they happen, and a hand edit made
                        // while Notepad++ runs would be written over
                        _panel?.Shutdown();
                        break;
                }
            });
        }

        private static void OnBufferActivated(IntPtr buffer)
        {
            bool isTrace = IsTraceDocument();
            SyntaxActivated(buffer, isTrace);

            if (isTrace && _settings.AutoOpen && !_panelVisible && !Dismissed.Contains(buffer))
            {
                ShowPanel();
                _autoOpened = true;
                return;
            }
            if (!isTrace && _autoOpened && _panelVisible)
            {
                HidePanel();
                return;
            }
            if (_panelVisible && (isTrace || _settings.AnalyzeOnTabSwitch)) Analyze(force: false);
        }

        // ---- Menu commands ----

        private static void TogglePanel()
        {
            if (_panelVisible)
            {
                if (IsTraceDocument()) Dismissed.Add(CurrentBufferId());
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

        private static void ToggleSlideOver() => SetDetailSlideOver(!_settings.DetailSlideOver);

        /// <summary>From the menu or the detail pane's own switch: saves the choice, ticks the menu item and tells the page.</summary>
        internal static void SetDetailSlideOver(bool on)
        {
            _settings.DetailSlideOver = on;
            SetCheck(_cmdSlideOver, on);
            _settings.Save();
            _panel?.SetDetailSlideOver(on);
        }

        private static void OpenDevTools()
        {
            if (!_panelVisible) ShowPanel();
            _panel.OpenDevTools();
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
                _panel = new PanelForm(Path.Combine(PluginDir, "web"), PluginDir, userData, IsDarkMode(), _settings.DevTools, _settings.DetailSlideOver);
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
            ClearMarks(pick: true);
            if (_panel != null) Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_DMMHIDE, IntPtr.Zero, _panel.Handle);
            _panelVisible = false;
            _autoOpened = false;
            SetCheck(_cmdPanel, false);
        }

        /// <summary>The user closed the panel with its own close button.</summary>
        public static void OnPanelClosed()
        {
            if (IsTraceDocument()) Dismissed.Add(CurrentBufferId());
            ClearMarks(pick: true);
            _panelVisible = false;
            _autoOpened = false;
            SetCheck(_cmdPanel, false);
        }

        public static void ReloadRequested() => Safe(() => Analyze(force: true));

        /// <summary>
        /// Writes a dump exported by the page to %TEMP%\TroiaLens (UTF-8 with BOM, so Excel reads Turkish letters)
        /// and opens it with its default program (Excel). Files sit beside the trace copy folders; CleanCsv removes
        /// them after a day.
        /// </summary>
        public static void SaveCsv(string name, string text) => Safe(() =>
        {
            Directory.CreateDirectory(TempRoot);
            CleanCsv();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name?.Replace(c, '_');
            if (string.IsNullOrWhiteSpace(name)) name = "dump.csv";
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            string file = Path.Combine(TempRoot, name);
            for (int n = 2; File.Exists(file); n++) file = Path.Combine(TempRoot, $"{stem} ({n}){ext}");
            File.WriteAllText(file, text, new UTF8Encoding(true));
            Process.Start(file);
        });

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
            int gen = Generation(buffer);
            if (!force && buffer == _docBuffer && gen == _docGen && length == _docLength) return;

            // The page starts over without a selection
            if (_markSpan >= 0)
            {
                ClearMarks(sci);
                ClearPick(sci);
            }
            _caretLine = -1;

            string path = CurrentPath();
            string name = string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;

            _docBuffer = buffer;
            _docPath = path;
            _docName = name;
            _docLength = length;
            _docGen = gen;
            _lineStarts = null;

            // The document is never copied whole into memory: a trace may be as large as Notepad++ can open.
            // The character pointer stays valid until the document changes, which cannot happen during this call.
            IntPtr text = Sci(sci, SciMsg.SCI_GETCHARACTERPOINTER);
            bool utf8 = Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8;
            var head = new byte[Math.Min(length, 1 << 20)];
            if (head.Length > 0) Marshal.Copy(text, head, 0, head.Length);

            if (!_settings.IsTraceExtension(path) && !LooksLikeTrace(head, utf8))
            {
                _panel.ShowNotTrace(buffer, name);
                return;
            }

            _lineStarts = LineStartsOf(buffer, gen, text, length);
            // The page may still hold this version's analysis and asks for the text only when it does not
            if (force) SendDocument();
            else _panel.ShowDocument(buffer, gen, name);
        }

        /// <summary>The page has no analysis of this version of the shown document and asks for its text.</summary>
        public static void DocumentRequested(IntPtr buffer, int gen) => Safe(() =>
        {
            if (_panel == null || _lineStarts == null || buffer != _docBuffer || gen != _docGen) return;
            if (CurrentBufferId() != buffer || Generation(buffer) != gen) return;
            SendDocument();
        });

        private static void SendDocument()
        {
            IntPtr sci = CurrentScintilla();
            IntPtr text = Sci(sci, SciMsg.SCI_GETCHARACTERPOINTER);
            bool utf8 = Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8;
            // The page reads the saved file itself when it holds exactly the document; otherwise a temporary UTF-8 copy
            if (IsSavedAsIs(sci, _docBuffer, _docPath, _docLength)) _panel.ShowTrace(_docBuffer, _docGen, _docName, _docPath, temporary: false);
            else _panel.ShowTrace(_docBuffer, _docGen, _docName, WriteCopy(text, _docLength, utf8, _docName), temporary: true);
        }

        private static int Generation(IntPtr buffer) => Generations.TryGetValue(buffer, out int gen) ? gen : 0;

        /// <summary>Text was inserted or deleted in a view: its document is another version now.</summary>
        public static void OnTextModified(IntPtr sci, int type)
        {
            if ((type & (SciMsg.SC_MOD_INSERTTEXT | SciMsg.SC_MOD_DELETETEXT)) == 0 || !_ready) return;
            Safe(() =>
            {
                IntPtr buffer = ViewBuffer(ViewOf(sci));
                if (buffer != IntPtr.Zero) Generations[buffer] = Generation(buffer) + 1;
            });
        }

        private static long[] LineStartsOf(IntPtr buffer, int gen, IntPtr text, long length)
        {
            int i = RecentLineStarts.FindIndex(r => r.Buffer == buffer);
            long[] starts = i >= 0 && RecentLineStarts[i].Gen == gen ? RecentLineStarts[i].Starts : LineStarts(text, length);
            if (i >= 0) RecentLineStarts.RemoveAt(i);
            RecentLineStarts.Add((buffer, gen, starts));
            if (RecentLineStarts.Count > RecentLineStartsKept) RecentLineStarts.RemoveAt(0);
            return starts;
        }

        /// <summary>
        /// The document is unmodified and its file has the editor's UTF-8 bytes (an optional BOM aside), so the page
        /// can read the file from disk.
        /// </summary>
        private static bool IsSavedAsIs(IntPtr sci, IntPtr buffer, string path, long length)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            if (Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() != SciMsg.SC_CP_UTF8) return false;
            if (Sci(sci, SciMsg.SCI_GETMODIFY) != IntPtr.Zero) return false;
            int encoding = Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETBUFFERENCODING, buffer, IntPtr.Zero).ToInt32();
            if (encoding != NppMsg.UNI_UTF8 && encoding != NppMsg.UNI_COOKIE && encoding != NppMsg.UNI_7BIT) return false;
            try
            {
                return new FileInfo(path).Length == length + (encoding == NppMsg.UNI_UTF8 ? 3 : 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string TempRoot => Path.Combine(Path.GetTempPath(), Name);

        /// <summary>
        /// Writes the document as UTF-8 to a folder of its own under %TEMP%\TroiaLens, piece by piece. Non-UTF-8
        /// documents use the system ANSI code page (windows-1254 on Turkish Windows).
        /// </summary>
        private static string WriteCopy(IntPtr text, long length, bool utf8, string name)
        {
            string dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string file = Path.Combine(dir, string.IsNullOrWhiteSpace(name) ? "trace.txt" : name);

            var chunk = new byte[4 << 20];
            Decoder decoder = utf8 ? null : Encoding.Default.GetDecoder();
            Encoder encoder = utf8 ? null : new UTF8Encoding(false).GetEncoder();
            char[] chars = utf8 ? null : new char[chunk.Length];
            byte[] converted = utf8 ? null : new byte[Encoding.UTF8.GetMaxByteCount(chunk.Length)];
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
            {
                for (long at = 0; at < length;)
                {
                    int n = (int)Math.Min(chunk.Length, length - at);
                    Marshal.Copy(new IntPtr(text.ToInt64() + at), chunk, 0, n);
                    at += n;
                    if (utf8)
                    {
                        output.Write(chunk, 0, n);
                        continue;
                    }
                    bool last = at >= length;
                    int c = decoder.GetChars(chunk, 0, n, chars, 0, last);
                    output.Write(converted, 0, encoder.GetBytes(chars, 0, c, converted, 0, last));
                }
            }
            return file;
        }

        /// <summary>Removes copies left behind by an earlier session (the panel deletes its own when it moves on).</summary>
        private static void CleanTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                foreach (string dir in Directory.GetDirectories(TempRoot))
                {
                    try
                    {
                        // Another Notepad++ instance may still be showing a recent one
                        if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-12)) Directory.Delete(dir, true);
                    }
                    catch (Exception)
                    {
                        // In use; next time
                    }
                }
                CleanCsv();
            }
            catch (Exception)
            {
                // Temp folder not readable; nothing to clean
            }
        }

        /// <summary>Removes exported CSV files older than a day; Notepad++ may stay open for days, so SaveCsv calls it too.</summary>
        private static void CleanCsv()
        {
            try
            {
                foreach (string file in Directory.GetFiles(TempRoot, "*.csv"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file);
                    }
                    catch (Exception)
                    {
                        // Open in Excel; next time
                    }
                }
            }
            catch (Exception)
            {
                // Temp folder not readable; nothing to clean
            }
        }

        /// <summary>
        /// Active document has a trace extension, or its beginning looks like a trace (Canias also saves traces
        /// as .txt). Reads only the first 64 KB, so it is cheap on every tab switch.
        /// </summary>
        private static bool IsTraceDocument() => IsTraceDocument(CurrentScintilla(), CurrentPath());

        /// <summary>The same for the document shown in a view.</summary>
        private static bool IsTraceDocument(IntPtr sci, string path)
        {
            if (_settings.IsTraceExtension(path)) return true;
            int length = (int)Math.Min(Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64(), 1 << 16);
            if (length <= 0) return false;
            var head = new byte[length];
            Marshal.Copy(Sci(sci, SciMsg.SCI_GETRANGEPOINTER, IntPtr.Zero, (IntPtr)length), head, 0, length);
            return LooksLikeTrace(head, Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8);
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

        /// <summary>Start of every "\n" separated line, read from the document piece by piece.</summary>
        private static long[] LineStarts(IntPtr text, long length)
        {
            var starts = new List<long>(1 << 16) { 0 };
            var chunk = new byte[4 << 20];
            for (long at = 0; at < length; at += chunk.Length)
            {
                int n = (int)Math.Min(chunk.Length, length - at);
                Marshal.Copy(new IntPtr(text.ToInt64() + at), chunk, 0, n);
                for (int i = Array.IndexOf(chunk, (byte)'\n', 0, n); i >= 0; i = Array.IndexOf(chunk, (byte)'\n', i + 1, n - i - 1))
                    starts.Add(at + i + 1);
            }
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
                long pos = TracePos(sci, line);
                long docLine = Sci(sci, SciMsg.SCI_LINEFROMPOSITION, (IntPtr)pos).ToInt64();
                // The caret move below must not come back to the page as a caret report
                _caretLine = TraceLineAt(pos);
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

        /// <summary>Reports caret moves in the analysed document so the call tree can select the matching row.</summary>
        public static void OnEditorUpdate(IntPtr sci)
        {
            if (!_panelVisible || _panel == null || _lineStarts == null) return;
            Safe(() =>
            {
                if (sci != CurrentScintilla() || CurrentBufferId() != _docBuffer) return;
                DrawSpan(sci);
                int line = TraceLineAt(Sci(sci, SciMsg.SCI_GETCURRENTPOS).ToInt64());
                if (line == _caretLine) return;
                _caretLine = line;
                _panel.PostCaret(line);
            });
        }

        /// <summary>
        /// Double-clicking a word in the analysed document marks all its occurrences there (whole word, any case);
        /// double-clicking the same word again or outside a word removes them.
        /// </summary>
        public static void OnDoubleClick(IntPtr sci, long pos)
        {
            if (!_panelVisible || _lineStarts == null) return;
            Safe(() =>
            {
                if (sci != CurrentScintilla() || CurrentBufferId() != _docBuffer) return;
                DefineMarks();
                long start = Sci(sci, SciMsg.SCI_WORDSTARTPOSITION, (IntPtr)pos, (IntPtr)1).ToInt64();
                long end = Sci(sci, SciMsg.SCI_WORDENDPOSITION, (IntPtr)pos, (IntPtr)1).ToInt64();
                string word = end > start && end - start <= 200 ? TextRange(sci, start, end) : null;
                string previous = _pickedWord;

                ClearPick(sci);
                if (string.IsNullOrEmpty(word) || string.Equals(word, previous, StringComparison.OrdinalIgnoreCase)) return;
                _pickedWord = word;
                MarkAll(sci, _indPick, 0, Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64(), word, SciMsg.SCFIND_WHOLEWORD);
            });
        }

        private static void ClearPick(IntPtr sci)
        {
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indPick);
            Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, IntPtr.Zero, Sci(sci, SciMsg.SCI_GETLENGTH));
            _pickedWord = null;
        }

        private static string TextRange(IntPtr sci, long start, long end)
        {
            var bytes = new byte[end - start];
            Marshal.Copy(Sci(sci, SciMsg.SCI_GETRANGEPOINTER, (IntPtr)start, (IntPtr)bytes.Length), bytes, 0, bytes.Length);
            bool utf8 = Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8;
            return (utf8 ? Encoding.UTF8 : Encoding.Default).GetString(bytes);
        }

        /// <summary>
        /// Marks a tree row in the analysed document: trace lines from..to (its method, 0 = none) get a tinted
        /// background and a margin bar, line..lineEnd a stronger tint, and whole-word matches of word inside the span a box.
        /// </summary>
        public static void Mark(int from, int to, int line, int lineEnd, string word)
        {
            Safe(() =>
            {
                if (_lineStarts == null || CurrentBufferId() != _docBuffer) return;
                IntPtr sci = CurrentScintilla();
                DefineMarks();
                ShowMarksInText(sci);

                string span = $"{from}:{to}:{word}";
                if (span != _markedSpan)
                {
                    ClearMarks(sci);
                    if (from > 0 && to >= from)
                    {
                        _spanFirst = DocLine(sci, from);
                        _spanLast = DocLineEnd(sci, to);
                        _spanWord = word;
                    }
                    _markedSpan = span;
                }
                DrawSpan(sci);

                Sci(sci, SciMsg.SCI_MARKERDELETEALL, (IntPtr)_markRow);
                if (line > 0)
                {
                    long first = DocLine(sci, line), last = DocLineEnd(sci, Math.Max(line, lineEnd));
                    for (long l = first; l <= last && l - first < 1000; l++) Sci(sci, SciMsg.SCI_MARKERADD, (IntPtr)l, (IntPtr)_markRow);
                }
            });
        }

        /// <summary>Removes the row marks (and with pick the double-clicked word's marks) from the analysed document, if it is the active one.</summary>
        public static void ClearMarks(bool pick = false)
        {
            Safe(() =>
            {
                if (_markSpan < 0 || _lineStarts == null || CurrentBufferId() != _docBuffer) return;
                ClearMarks(CurrentScintilla());
                if (pick) ClearPick(CurrentScintilla());
            });
        }

        private static void ClearMarks(IntPtr sci)
        {
            Sci(sci, SciMsg.SCI_MARKERDELETEALL, (IntPtr)_markSpan);
            Sci(sci, SciMsg.SCI_MARKERDELETEALL, (IntPtr)_markRow);
            Sci(sci, SciMsg.SCI_MARKERDELETEALL, (IntPtr)_markBar);
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indWord);
            Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, IntPtr.Zero, Sci(sci, SciMsg.SCI_GETLENGTH));
            _markedSpan = null;
            _spanFirst = _spanLast = _drawnFirst = _drawnLast = -1;
            _spanWord = null;
        }

        /// <summary>
        /// Marks the span's lines (background, margin bar, method name): all of a short span; of a long one the lines
        /// around the view, redrawn whenever the view nears the edge of what is marked.
        /// </summary>
        private static void DrawSpan(IntPtr sci)
        {
            if (_spanFirst < 0) return;
            long first = _spanFirst, last = _spanLast;
            if (last - first >= SpanWhole)
            {
                // The span's line numbers no longer fit an edited document; Analyze starts over when it runs again
                if (Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64() != _docLength) return;
                long screen = Sci(sci, SciMsg.SCI_LINESONSCREEN).ToInt64();
                long visible = Sci(sci, SciMsg.SCI_GETFIRSTVISIBLELINE).ToInt64();
                long top = Sci(sci, SciMsg.SCI_DOCLINEFROMVISIBLE, (IntPtr)visible).ToInt64();
                // A folded block on screen may hide any number of lines; those need no marks
                long bottom = Math.Min(Sci(sci, SciMsg.SCI_DOCLINEFROMVISIBLE, (IntPtr)(visible + screen)).ToInt64(), top + screen);
                bool covered = _drawnFirst >= 0
                    && (_drawnFirst == first || top - _drawnFirst > SpanReach / 2)
                    && (_drawnLast == last || _drawnLast - bottom > SpanReach / 2);
                if (covered) return;
                first = Math.Max(first, top - SpanReach);
                last = Math.Min(last, bottom + SpanReach);
                // The view is away from the span; whatever is marked is off screen
                if (first > last) return;
            }
            else if (_drawnFirst >= 0) return;

            ShowMarksInText(sci);
            // Only the lines leaving and entering the window change, so scrolling stays smooth
            bool overlap = _drawnFirst >= 0 && _drawnFirst <= last && first <= _drawnLast;
            if (_drawnFirst >= 0)
            {
                if (overlap)
                {
                    SetSpanLines(sci, _drawnFirst, first - 1, SciMsg.SCI_MARKERDELETE);
                    SetSpanLines(sci, last + 1, _drawnLast, SciMsg.SCI_MARKERDELETE);
                }
                else SetSpanLines(sci, _drawnFirst, _drawnLast, SciMsg.SCI_MARKERDELETE);
                Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indWord);
                long from = Sci(sci, SciMsg.SCI_POSITIONFROMLINE, (IntPtr)_drawnFirst).ToInt64();
                Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, (IntPtr)from,
                    (IntPtr)(Sci(sci, SciMsg.SCI_GETLINEENDPOSITION, (IntPtr)_drawnLast).ToInt64() - from));
            }
            if (overlap)
            {
                SetSpanLines(sci, first, _drawnFirst - 1, SciMsg.SCI_MARKERADD);
                SetSpanLines(sci, _drawnLast + 1, last, SciMsg.SCI_MARKERADD);
            }
            else SetSpanLines(sci, first, last, SciMsg.SCI_MARKERADD);
            if (!string.IsNullOrEmpty(_spanWord))
                MarkAll(sci, _indWord, Sci(sci, SciMsg.SCI_POSITIONFROMLINE, (IntPtr)first).ToInt64(),
                    Sci(sci, SciMsg.SCI_GETLINEENDPOSITION, (IntPtr)last).ToInt64(), _spanWord,
                    SciMsg.SCFIND_WHOLEWORD | SciMsg.SCFIND_MATCHCASE);
            _drawnFirst = first;
            _drawnLast = last;
        }

        /// <summary>Adds or deletes the span background and margin bar on editor lines first..last.</summary>
        private static void SetSpanLines(IntPtr sci, long first, long last, uint msg)
        {
            for (long l = first; l <= last; l++)
            {
                Sci(sci, msg, (IntPtr)l, (IntPtr)_markSpan);
                Sci(sci, msg, (IntPtr)l, (IntPtr)_markBar);
            }
        }

        /// <summary>Fills an indicator over every match of word between start and end.</summary>
        private static void MarkAll(IntPtr sci, int indicator, long start, long end, string word, int flags)
        {
            bool utf8 = Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8;
            byte[] needle = (utf8 ? Encoding.UTF8 : Encoding.Default).GetBytes(word);
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)indicator);
            Sci(sci, SciMsg.SCI_SETSEARCHFLAGS, (IntPtr)flags);
            for (int hits = 0; hits < 20000 && start < end; hits++)
            {
                Sci(sci, SciMsg.SCI_SETTARGETRANGE, (IntPtr)start, (IntPtr)end);
                if (Win32.SendMessage(sci, SciMsg.SCI_SEARCHINTARGET, (IntPtr)needle.Length, needle).ToInt64() < 0) break;
                long s = Sci(sci, SciMsg.SCI_GETTARGETSTART).ToInt64();
                long e = Sci(sci, SciMsg.SCI_GETTARGETEND).ToInt64();
                Sci(sci, SciMsg.SCI_INDICATORFILLRANGE, (IntPtr)s, (IntPtr)(e - s));
                start = Math.Max(e, s + 1);
            }
        }

        private static void DefineMarks()
        {
            if (_markSpan < 0)
            {
                _markSpan = Allocate(NppMsg.NPPM_ALLOCATEMARKER, 3, 10);
                _markRow = _markSpan + 1;
                _markBar = _markSpan + 2;
                _indWord = Allocate(NppMsg.NPPM_ALLOCATEINDICATOR, 2, 9);
                _indPick = _indWord + 1;
            }
            if (_marksDark != IsDarkMode()) DefineMarkStyles();
        }

        private static void DefineMarkStyles()
        {
            bool dark = IsDarkMode();
            _marksDark = dark;
            // Styles belong to each view, the marks themselves to the document
            foreach (IntPtr sci in new[] { Npp.ScintillaMainHandle, Npp.ScintillaSecondHandle })
            {
                DefineBackground(sci, _markSpan, Amber, 0x24, SciMsg.SC_LAYER_OVER_TEXT);
                // The selected row's whole block in light yellow, under the text so it stays crisp; a dark theme gets a
                // fainter tint to keep its light text readable
                DefineBackground(sci, _markRow, LightYellow, dark ? 0x48 : 0xB4, SciMsg.SC_LAYER_UNDER_TEXT);
                Sci(sci, SciMsg.SCI_MARKERDEFINE, (IntPtr)_markBar, (IntPtr)SciMsg.SC_MARK_LEFTRECT);
                Sci(sci, SciMsg.SCI_MARKERSETBACK, (IntPtr)_markBar, (IntPtr)Amber);
                DefineBox(sci, _indWord, Teal, 90);
                DefineBox(sci, _indPick, Fuchsia, dark ? 110 : 80);
            }
        }

        private static void DefineBox(IntPtr sci, int indicator, int color, int alpha)
        {
            Sci(sci, SciMsg.SCI_INDICSETSTYLE, (IntPtr)indicator, (IntPtr)SciMsg.INDIC_ROUNDBOX);
            Sci(sci, SciMsg.SCI_INDICSETFORE, (IntPtr)indicator, (IntPtr)color);
            Sci(sci, SciMsg.SCI_INDICSETALPHA, (IntPtr)indicator, (IntPtr)alpha);
            Sci(sci, SciMsg.SCI_INDICSETOUTLINEALPHA, (IntPtr)indicator, (IntPtr)220);
            Sci(sci, SciMsg.SCI_INDICSETUNDER, (IntPtr)indicator, (IntPtr)1);
        }

        private static void DefineBackground(IntPtr sci, int marker, int color, int alpha, int layer)
        {
            Sci(sci, SciMsg.SCI_MARKERDEFINE, (IntPtr)marker, (IntPtr)SciMsg.SC_MARK_BACKGROUND);
            Sci(sci, SciMsg.SCI_MARKERSETBACK, (IntPtr)marker, (IntPtr)color);
            // Scintilla 4 (Notepad++ before 8.4) knows only the alpha; Scintilla 5 draws it translucently on the given layer
            Sci(sci, SciMsg.SCI_MARKERSETALPHA, (IntPtr)marker, (IntPtr)alpha);
            Sci(sci, SciMsg.SCI_MARKERSETBACKTRANSLUCENT, (IntPtr)marker, (IntPtr)(color | (alpha << 24)));
            Sci(sci, SciMsg.SCI_MARKERSETLAYER, (IntPtr)marker, (IntPtr)layer);
        }

        /// <summary>
        /// Scintilla draws a background marker in the text only when some margin's mask holds it (margins never draw
        /// one themselves), so all marks go to the symbol margin (1), where the bar shows next to bookmarks.
        /// Notepad++ may reset the mask, so this runs before every mark.
        /// </summary>
        private static void ShowMarksInText(IntPtr sci)
        {
            int bits = (1 << _markSpan) | (1 << _markRow) | (1 << _markBar);
            int mask = Sci(sci, SciMsg.SCI_GETMARGINMASKN, (IntPtr)1).ToInt32();
            if ((mask & bits) != bits) Sci(sci, SciMsg.SCI_SETMARGINMASKN, (IntPtr)1, (IntPtr)(mask | bits));
        }

        /// <summary>Reserves marker / indicator ids no other plugin uses; Notepad++ versions without the message get fixed ones.</summary>
        private static int Allocate(uint msg, int count, int fallback) =>
            Win32.SendMessage(Npp.NppHandle, msg, (IntPtr)count, out int start) != IntPtr.Zero ? start : fallback;

        /// <summary>Editor position of a 1-based trace line.</summary>
        private static long TracePos(IntPtr sci, int line)
        {
            int index = Math.Max(0, Math.Min(_lineStarts.Length - 1, line - 1));
            return Math.Min(_lineStarts[index], Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64());
        }

        private static long DocLine(IntPtr sci, int traceLine) =>
            Sci(sci, SciMsg.SCI_LINEFROMPOSITION, (IntPtr)TracePos(sci, traceLine)).ToInt64();

        /// <summary>Last editor line of a 1-based trace line; a trace line spans several when it holds bare CRs.</summary>
        private static long DocLineEnd(IntPtr sci, int traceLine)
        {
            long length = Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64();
            // The trace line's own LF, just before the next line starts
            long end = traceLine >= 1 && traceLine < _lineStarts.Length ? _lineStarts[traceLine] - 1 : length;
            return Sci(sci, SciMsg.SCI_LINEFROMPOSITION, (IntPtr)Math.Max(0, Math.Min(end, length))).ToInt64();
        }

        /// <summary>1-based trace line containing an editor position.</summary>
        private static int TraceLineAt(long pos)
        {
            int i = Array.BinarySearch(_lineStarts, pos);
            return (i >= 0 ? i : ~i - 1) + 1;
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
