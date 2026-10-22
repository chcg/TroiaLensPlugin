using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TroiaLens.Npp;

namespace TroiaLens
{
    // Syntax colours in the editor (see Syntax): every document recognised as a trace is coloured around what is on
    // screen, whether the panel is open or not. Scrolling colours the lines coming into view; an edit redoes the
    // lines from the edited one on.
    internal static partial class Plugin
    {
        // Indicators for the text colour (each range carries its colour in its value) and the warning underline, and the
        // marker for the error band. Like the other marks they belong to the document; their styles belong to each view.
        private static int _indColor = -1, _indWarn, _markBand;
        // Palette of the styles and of the colours in the documents (null: none yet); a new one repaints every document
        private static bool? _syntaxDark;
        private static readonly int[] SyntaxBgr = new int[Syntax.Band + 1];
        // Bumped whenever the colours in the documents go out of date (new palette, colours switched off)
        private static int _syntaxGeneration;
        // An error stops the colouring for the session rather than come back on every scroll
        private static bool _syntaxBroken;
        // Lines coloured above and below the screen, so scrolling a little finds them done
        private const int SyntaxReach = 100;

        private sealed class SyntaxDoc
        {
            public bool Trace;
            /// <summary><see cref="_syntaxGeneration"/> of the colours the document carries; -1: it carries none.</summary>
            public int Generation = -1;
            public long Length = -1;
            /// <summary>Editor lines whose colours are up to date: sorted ranges that do not touch.</summary>
            public readonly List<(long First, long Last)> Painted = new List<(long, long)>();
        }

        private static readonly Dictionary<IntPtr, SyntaxDoc> SyntaxDocs = new Dictionary<IntPtr, SyntaxDoc>();
        // Per view (main, second): the document last coloured there, and the first line edited there since
        private static readonly IntPtr[] SyntaxBuffer = new IntPtr[2];
        private static readonly long[] SyntaxEdited = { -1, -1 };

        // A line's first MaxLine characters: at most 3 UTF-8 bytes per UTF-16 character
        private static readonly byte[] LineBytes = new byte[Syntax.MaxLine * 3];
        private static readonly char[] LineChars = new char[Syntax.MaxLine * 3];
        private static readonly int[] LineAt = new int[Syntax.MaxLine * 3 + 1];

        private static void ToggleSyntax()
        {
            _settings.SyntaxColors = !_settings.SyntaxColors;
            SetCheck(_cmdSyntax, _settings.SyntaxColors);
            _settings.Save();
            if (_settings.SyntaxColors) PaintSyntaxViews();
            else ClearSyntaxViews();
        }

        /// <summary>SCN_UPDATEUI: colours what scrolled into view or changed.</summary>
        public static void OnSyntaxUpdate(IntPtr sci, int updated)
        {
            if (!_ready || !_settings.SyntaxColors || _syntaxBroken) return;
            int view = ViewOf(sci);
            // Caret and horizontal moves show no new lines. Our own marks and the lexer also report CONTENT; that pass
            // finds everything done.
            if ((updated & (SciMsg.SC_UPDATE_CONTENT | SciMsg.SC_UPDATE_V_SCROLL)) == 0 && SyntaxEdited[view] < 0) return;
            SafeSyntax(() => PaintSyntax(sci, view));
        }

        /// <summary>SCN_MODIFIED: text was inserted or deleted; the next update redoes the colours from that line on.</summary>
        public static void OnSyntaxModified(IntPtr sci, int type, long position)
        {
            if ((type & (SciMsg.SC_MOD_INSERTTEXT | SciMsg.SC_MOD_DELETETEXT)) == 0) return;
            if (!_ready || !_settings.SyntaxColors || _syntaxBroken) return;
            SafeSyntax(() =>
            {
                int view = ViewOf(sci);
                long line = Sci(sci, SciMsg.SCI_LINEFROMPOSITION, (IntPtr)position).ToInt64();
                if (SyntaxEdited[view] < 0 || line < SyntaxEdited[view]) SyntaxEdited[view] = line;
            });
        }

        /// <summary>A document became active in the current view; isTrace is checked anew, its text may have changed.</summary>
        private static void SyntaxActivated(IntPtr buffer, bool isTrace)
        {
            SafeSyntax(() =>
            {
                IntPtr sci = CurrentScintilla();
                if (!SyntaxDocs.TryGetValue(buffer, out SyntaxDoc doc)) SyntaxDocs[buffer] = doc = new SyntaxDoc();
                doc.Trace = isTrace;
                // Colours left from when it was a trace, or from before they were switched off
                if (doc.Generation >= 0 && (!isTrace || !_settings.SyntaxColors)) ClearSyntax(sci, doc);
                if (_settings.SyntaxColors) PaintSyntax(sci, ViewOf(sci));
            });
        }

        private static void SyntaxClosed(IntPtr buffer) => SyntaxDocs.Remove(buffer);

        /// <summary>Dark mode or the style configurator changed: the palette follows the editor background.</summary>
        private static void SyntaxThemeChanged()
        {
            if (!_ready || !_settings.SyntaxColors || _indColor < 0) return;
            _syntaxDark = null;
            PaintSyntaxViews();
        }

        private static void PaintSyntaxViews()
        {
            if (!_ready) return;
            SafeSyntax(() =>
            {
                for (int view = 0; view < 2; view++)
                    if (ViewBuffer(view) != IntPtr.Zero) PaintSyntax(ViewHandle(view), view);
            });
        }

        /// <summary>Removes the colours from the documents on screen; the others lose them when they become active.</summary>
        private static void ClearSyntaxViews()
        {
            _syntaxGeneration++;
            if (!_ready || _indColor < 0) return;
            SafeSyntax(() =>
            {
                for (int view = 0; view < 2; view++)
                {
                    IntPtr buffer = ViewBuffer(view);
                    if (buffer != IntPtr.Zero && SyntaxDocs.TryGetValue(buffer, out SyntaxDoc doc) && doc.Generation >= 0)
                        ClearSyntax(ViewHandle(view), doc);
                }
            });
        }

        /// <summary>Colours the lines on screen (and reach lines around them) of the view's document that need it.</summary>
        private static void PaintSyntax(IntPtr sci, int view)
        {
            IntPtr buffer = ViewBuffer(view);
            if (buffer == IntPtr.Zero) return;

            // Edits went to the document this view showed when they were made
            long edited = SyntaxEdited[view];
            SyntaxEdited[view] = -1;
            bool editedHere = edited >= 0 && SyntaxBuffer[view] == buffer;
            if (edited >= 0 && SyntaxDocs.TryGetValue(SyntaxBuffer[view], out SyntaxDoc changed)) Truncate(changed.Painted, edited);
            SyntaxBuffer[view] = buffer;

            if (!SyntaxDocs.TryGetValue(buffer, out SyntaxDoc doc))
                SyntaxDocs[buffer] = doc = new SyntaxDoc { Trace = IsTraceDocument(sci, BufferPath(buffer)) };
            if (!doc.Trace) return;

            DefineSyntax(sci);
            long length = Sci(sci, SciMsg.SCI_GETLENGTH).ToInt64();
            if (doc.Generation != _syntaxGeneration)
            {
                if (doc.Generation >= 0) ClearSyntax(sci, doc);
                doc.Generation = _syntaxGeneration;
            }
            // Changed where no view of ours saw it (reloaded in the background)
            else if (length != doc.Length && !editedHere) doc.Painted.Clear();
            doc.Length = length;

            ShowBandInText(sci);
            bool utf8 = Sci(sci, SciMsg.SCI_GETCODEPAGE).ToInt32() == SciMsg.SC_CP_UTF8;
            // Right after an edit only the screen, so typing stays quick; the update that follows does the rest
            foreach ((long first, long last) in VisibleLines(sci, editedHere ? 0 : SyntaxReach))
            {
                foreach ((long a, long b) in Missing(doc.Painted, first, last)) PaintLines(sci, a, b, utf8);
                Add(doc.Painted, first, last);
            }
        }

        /// <summary>Document lines on screen and reach screen lines around it, as runs; folded lines are left out.</summary>
        private static List<(long, long)> VisibleLines(IntPtr sci, int reach)
        {
            var runs = new List<(long, long)>();
            long count = Sci(sci, SciMsg.SCI_GETLINECOUNT).ToInt64();
            long top = Sci(sci, SciMsg.SCI_GETFIRSTVISIBLELINE).ToInt64();
            long from = Math.Max(0, top - reach), to = top + Sci(sci, SciMsg.SCI_LINESONSCREEN).ToInt64() + reach;
            if (Sci(sci, SciMsg.SCI_GETALLLINESVISIBLE) != IntPtr.Zero)
            {
                // Nothing folded: wrapped lines aside, screen lines are document lines in a row
                long a = Sci(sci, SciMsg.SCI_DOCLINEFROMVISIBLE, (IntPtr)from).ToInt64();
                long b = Math.Min(Sci(sci, SciMsg.SCI_DOCLINEFROMVISIBLE, (IntPtr)to).ToInt64(), count - 1);
                if (a <= b) runs.Add((a, b));
                return runs;
            }
            long start = -1, previous = -1;
            for (long v = from; v <= to; v++)
            {
                long line = Sci(sci, SciMsg.SCI_DOCLINEFROMVISIBLE, (IntPtr)v).ToInt64();
                if (line >= count) break;
                if (line == previous) continue;
                if (line != previous + 1 || start < 0)
                {
                    if (start >= 0) runs.Add((start, previous));
                    start = line;
                }
                previous = line;
            }
            if (start >= 0) runs.Add((start, previous));
            return runs;
        }

        /// <summary>Replaces the colours of document lines first..last.</summary>
        private static void PaintLines(IntPtr sci, long first, long last, bool utf8)
        {
            long start = Sci(sci, SciMsg.SCI_POSITIONFROMLINE, (IntPtr)first).ToInt64();
            long end = Sci(sci, SciMsg.SCI_GETLINEENDPOSITION, (IntPtr)last).ToInt64();
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indColor);
            Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, (IntPtr)start, (IntPtr)(end - start));
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indWarn);
            Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, (IntPtr)start, (IntPtr)(end - start));
            for (long line = first; line <= last; line++) PaintLine(sci, line, utf8);
            // Other fills (Notepad++'s, the marks above) keep the default value
            Sci(sci, SciMsg.SCI_SETINDICATORVALUE, (IntPtr)1);
        }

        private static void PaintLine(IntPtr sci, long line, bool utf8)
        {
            // Deleting a line end merges the two lines' markers, so a line may carry the band more than once, and
            // MARKERDELETE takes away only one
            int band = 1 << _markBand;
            while ((Sci(sci, SciMsg.SCI_MARKERGET, (IntPtr)line).ToInt32() & band) != 0)
                Sci(sci, SciMsg.SCI_MARKERDELETE, (IntPtr)line, (IntPtr)_markBand);
            long start = Sci(sci, SciMsg.SCI_POSITIONFROMLINE, (IntPtr)line).ToInt64();
            long length = Sci(sci, SciMsg.SCI_GETLINEENDPOSITION, (IntPtr)line).ToInt64() - start;
            int n = (int)Math.Min(length, utf8 ? LineBytes.Length : Syntax.MaxLine);
            if (n <= 0) return;
            Marshal.Copy(Sci(sci, SciMsg.SCI_GETRANGEPOINTER, (IntPtr)start, (IntPtr)n), LineBytes, 0, n);
            Syntax.Result r = Syntax.TokenizeLine(utf8 ? DecodeUtf8(n) : DecodeAnsi(n));

            if (r.Band) Sci(sci, SciMsg.SCI_MARKERADD, (IntPtr)line, (IntPtr)_markBand);
            if (r.Color.Count > 0)
            {
                Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indColor);
                int value = 0;
                foreach (Syntax.Span s in r.Color)
                {
                    int v = SciMsg.SC_INDICVALUEBIT | SyntaxBgr[(int)s.Kind];
                    if (v != value) Sci(sci, SciMsg.SCI_SETINDICATORVALUE, (IntPtr)(value = v));
                    Sci(sci, SciMsg.SCI_INDICATORFILLRANGE, (IntPtr)(start + LineAt[s.Start]), (IntPtr)(LineAt[s.End] - LineAt[s.Start]));
                }
            }
            if (r.Deco.Count > 0)
            {
                Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indWarn);
                Sci(sci, SciMsg.SCI_SETINDICATORVALUE, (IntPtr)1);
                foreach (Syntax.Span s in r.Deco)
                    Sci(sci, SciMsg.SCI_INDICATORFILLRANGE, (IntPtr)(start + LineAt[s.Start]), (IntPtr)(LineAt[s.End] - LineAt[s.Start]));
            }
        }

        /// <summary>
        /// The first n bytes of <see cref="LineBytes"/> as UTF-8, with <see cref="LineAt"/> holding each character's byte
        /// offset. A byte that is not valid UTF-8 becomes one U+FFFD, as it is one position for Scintilla.
        /// </summary>
        private static string DecodeUtf8(int n)
        {
            byte[] b = LineBytes;
            int count = 0;
            for (int i = 0; i < n;)
            {
                int c = b[i], len = c < 0x80 ? 1 : c >= 0xC2 && c <= 0xDF ? 2 : c >= 0xE0 && c <= 0xEF ? 3 : c >= 0xF0 && c <= 0xF4 ? 4 : 0;
                int cp = len == 1 ? c : c & (0x7F >> len), j = 1;
                for (; j < len && i + j < n && (b[i + j] & 0xC0) == 0x80; j++) cp = (cp << 6) | (b[i + j] & 0x3F);
                bool valid = len > 0 && j == len
                    && (len < 3 || (len == 3 ? cp >= 0x800 && (cp < 0xD800 || cp > 0xDFFF) : cp >= 0x10000 && cp <= 0x10FFFF));
                LineAt[count] = i;
                if (!valid)
                {
                    LineChars[count++] = '\uFFFD';
                    i++;
                    continue;
                }
                if (cp >= 0x10000)
                {
                    LineChars[count++] = (char)(0xD800 + ((cp - 0x10000) >> 10));
                    LineAt[count] = i;
                    LineChars[count++] = (char)(0xDC00 + ((cp - 0x10000) & 0x3FF));
                }
                else LineChars[count++] = (char)cp;
                i += len;
            }
            LineAt[count] = n;
            return new string(LineChars, 0, count);
        }

        /// <summary>The first n bytes of <see cref="LineBytes"/> in the system ANSI code page, with <see cref="LineAt"/>.</summary>
        private static string DecodeAnsi(int n)
        {
            string text = Encoding.Default.GetString(LineBytes, 0, n);
            int at = 0;
            for (int i = 0; i < text.Length; i++)
            {
                LineAt[i] = at;
                // Single-byte code pages map one to one; a double-byte one is counted per character
                at = text.Length == n ? i + 1 : Math.Min(n, at + Encoding.Default.GetByteCount(text.ToCharArray(i, 1)));
            }
            LineAt[text.Length] = n;
            return text;
        }

        private static void ClearSyntax(IntPtr sci, SyntaxDoc doc)
        {
            IntPtr length = Sci(sci, SciMsg.SCI_GETLENGTH);
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indColor);
            Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, IntPtr.Zero, length);
            Sci(sci, SciMsg.SCI_SETINDICATORCURRENT, (IntPtr)_indWarn);
            Sci(sci, SciMsg.SCI_INDICATORCLEARRANGE, IntPtr.Zero, length);
            Sci(sci, SciMsg.SCI_MARKERDELETEALL, (IntPtr)_markBand);
            doc.Generation = -1;
            doc.Painted.Clear();
        }

        /// <summary>Reserves the ids once and defines the styles for the palette that suits the editor background.</summary>
        private static void DefineSyntax(IntPtr sci)
        {
            if (_indColor < 0)
            {
                _markBand = Allocate(NppMsg.NPPM_ALLOCATEMARKER, 1, 9);
                _indColor = Allocate(NppMsg.NPPM_ALLOCATEINDICATOR, 2, 11);
                _indWarn = _indColor + 1;
            }
            int back = Sci(sci, SciMsg.SCI_STYLEGETBACK, (IntPtr)SciMsg.STYLE_DEFAULT).ToInt32();
            bool dark = (299 * (back & 0xFF) + 587 * ((back >> 8) & 0xFF) + 114 * ((back >> 16) & 0xFF)) / 1000 < 128;
            if (dark == _syntaxDark) return;
            _syntaxDark = dark;
            // The colours are in the documents; every one is repainted when it is next coloured
            _syntaxGeneration++;
            for (int i = 0; i < SyntaxBgr.Length; i++) SyntaxBgr[i] = Bgr(Syntax.Rgb(dark, i));
            foreach (IntPtr view in new[] { Npp.ScintillaMainHandle, Npp.ScintillaSecondHandle })
            {
                Sci(view, SciMsg.SCI_INDICSETSTYLE, (IntPtr)_indColor, (IntPtr)SciMsg.INDIC_TEXTFORE);
                Sci(view, SciMsg.SCI_INDICSETFLAGS, (IntPtr)_indColor, (IntPtr)SciMsg.SC_INDICFLAG_VALUEFORE);
                Sci(view, SciMsg.SCI_INDICSETSTYLE, (IntPtr)_indWarn, (IntPtr)SciMsg.INDIC_SQUIGGLE);
                Sci(view, SciMsg.SCI_INDICSETFORE, (IntPtr)_indWarn, (IntPtr)SyntaxBgr[Syntax.Warning]);
                // The band is the line's own background (base layer; Scintilla 4: no alpha), so the selected row's and
                // its method's tints still draw over it, whatever the marker ids
                Sci(view, SciMsg.SCI_MARKERDEFINE, (IntPtr)_markBand, (IntPtr)SciMsg.SC_MARK_BACKGROUND);
                Sci(view, SciMsg.SCI_MARKERSETBACK, (IntPtr)_markBand, (IntPtr)SyntaxBgr[Syntax.Band]);
                Sci(view, SciMsg.SCI_MARKERSETALPHA, (IntPtr)_markBand, (IntPtr)SciMsg.SC_ALPHA_NOALPHA);
                Sci(view, SciMsg.SCI_MARKERSETLAYER, (IntPtr)_markBand, (IntPtr)SciMsg.SC_LAYER_BASE);
            }
        }

        /// <summary>Like <see cref="ShowMarksInText"/> for the error band.</summary>
        private static void ShowBandInText(IntPtr sci)
        {
            int bit = 1 << _markBand;
            int mask = Sci(sci, SciMsg.SCI_GETMARGINMASKN, (IntPtr)1).ToInt32();
            if ((mask & bit) == 0) Sci(sci, SciMsg.SCI_SETMARGINMASKN, (IntPtr)1, (IntPtr)(mask | bit));
        }

        private static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

        // ---- Painted line ranges ----

        /// <summary>Parts of first..last not in painted.</summary>
        private static IEnumerable<(long, long)> Missing(List<(long First, long Last)> painted, long first, long last)
        {
            long at = first;
            foreach ((long a, long b) in painted)
            {
                if (b < at) continue;
                if (a > last) break;
                if (a > at) yield return (at, a - 1);
                at = b + 1;
                if (at > last) yield break;
            }
            if (at <= last) yield return (at, last);
        }

        private static void Add(List<(long First, long Last)> painted, long first, long last)
        {
            int i = 0;
            while (i < painted.Count && painted[i].Last < first - 1) i++;
            while (i < painted.Count && painted[i].First <= last + 1)
            {
                first = Math.Min(first, painted[i].First);
                last = Math.Max(last, painted[i].Last);
                painted.RemoveAt(i);
            }
            painted.Insert(i, (first, last));
            // Jumping around a large document leaves islands; past a few, start over
            if (painted.Count > 64)
            {
                painted.Clear();
                painted.Add((first, last));
            }
        }

        /// <summary>Forgets line and everything after it: an edit there moved or changed those lines.</summary>
        private static void Truncate(List<(long First, long Last)> painted, long line)
        {
            for (int i = painted.Count - 1; i >= 0; i--)
            {
                if (painted[i].First >= line) painted.RemoveAt(i);
                else if (painted[i].Last >= line) painted[i] = (painted[i].First, line - 1);
            }
        }

        // ---- Views ----

        private static int ViewOf(IntPtr sci) => sci == Npp.ScintillaSecondHandle ? 1 : 0;

        private static IntPtr ViewHandle(int view) => view == 1 ? Npp.ScintillaSecondHandle : Npp.ScintillaMainHandle;

        /// <summary>Document shown in a view; zero when the view is hidden.</summary>
        private static IntPtr ViewBuffer(int view)
        {
            int index = Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETCURRENTDOCINDEX, IntPtr.Zero, (IntPtr)view).ToInt32();
            return index < 0 ? IntPtr.Zero : Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETBUFFERIDFROMPOS, (IntPtr)index, (IntPtr)view);
        }

        private static string BufferPath(IntPtr buffer)
        {
            int length = Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETFULLPATHFROMBUFFERID, buffer, IntPtr.Zero).ToInt32();
            if (length <= 0) return "";
            var sb = new StringBuilder(length + 1);
            Win32.SendMessage(Npp.NppHandle, NppMsg.NPPM_GETFULLPATHFROMBUFFERID, buffer, sb);
            return sb.ToString();
        }

        /// <summary>Like <see cref="Safe"/>, but after an error the colouring stops for the session.</summary>
        private static void SafeSyntax(Action action)
        {
            if (_syntaxBroken) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _syntaxBroken = true;
                MessageBox.Show("Sözdizimi renkleri bu oturumda durduruldu.\n\n" + ex, Name + " hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
