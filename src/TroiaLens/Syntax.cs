using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TroiaLens
{
    /// <summary>
    /// Syntax colours of trace text. The rules are the web app's tokenizer carried over one to one, so the editor and
    /// the panel colour a line alike; change both together. Offsets are string (UTF-16) offsets. Colour ranges do not
    /// overlap; warnings get an underline that keeps the text colour, and error lines a background band.
    /// </summary>
    internal static class Syntax
    {
        /// <summary>Only the first 4096 characters of a line are coloured.</summary>
        public const int MaxLine = 4096;

        public enum Kind : byte { Hata, Sql, Anahtar, Tanim, Metod, Sinif, Param, Deger, Sonuk, T200, T1000 }

        public readonly struct Span
        {
            public readonly int Start, End;
            public readonly Kind Kind;

            public Span(int start, int end, Kind kind)
            {
                Start = start;
                End = end;
                Kind = kind;
            }
        }

        public sealed class Result
        {
            public readonly List<Span> Color = new List<Span>();
            /// <summary>Warning underlines (their <see cref="Span.Kind"/> means nothing).</summary>
            public readonly List<Span> Deco = new List<Span>();
            /// <summary>The line is an error: it gets the error background.</summary>
            public bool Band;
        }

        // Palette as RGB: one colour per Kind, then the warning underline and the error band
        public const int Warning = 11, Band = 12;
        private static readonly int[] LightPalette =
        {
            0xB91C1C, 0x7C3AED, 0x1D4ED8, 0xBE185D, 0x795E26, 0x15803D, 0xB4540A, 0x0E7490, 0x8A94A6, 0xD97706, 0xE11D48,
            0xEA580C, 0xFEE2E2,
        };
        private static readonly int[] DarkPalette =
        {
            0xFCA5A5, 0xC9A2FF, 0x78AEFF, 0xF58CC4, 0xDCDCAA, 0x4ADE80, 0xFDBA74, 0x67D8EE, 0x7C8696, 0xFBBF24, 0xFB7185,
            0xFB923C, 0x4A1D1F,
        };

        /// <summary>RGB colour of a palette entry: a <see cref="Kind"/>, <see cref="Warning"/> or <see cref="Band"/>.</summary>
        public static int Rgb(bool dark, int entry) => (dark ? DarkPalette : LightPalette)[entry];

        private static readonly HashSet<string> Ctrl = Words(
            "IF THEN ELSE ENDIF LOOP AT WHILE BEGIN SWITCH CASE RETURN BREAK CONTINUE PARSE FOR WHERE INTO TO WITH " +
            "WITHFLAGS DELIMITER THIS SUPER MOVE APPEND APPENDSTRING LOCATERECORD COPY CLEAR CLEARTABLE SORT DUMP AGGREGATE REMOVE ADDELEMENT READ " +
            "RUNCODE BEGINTRAN COMMITTRAN ROLLBACKTRAN ROLLBACK ENQUE DEQUE CALL DIALOG MESSAGE TRACEON TRACEOFF TRACEPAUSE SET SHUTDOWN TABLE " +
            "SEQUENTIAL COLUMNS VALUES ON INDEXED INDEX DELETEFILE");
        private static readonly HashSet<string> Sqlk = Words(
            "SELECT DISTINCT TOP FROM WHERE AND OR NOT IN LIKE BETWEEN IS NULL INNER LEFT RIGHT OUTER CROSS JOIN ON AS " +
            "ORDER GROUP BY HAVING INTO VALUES SET UPDATE DELETE INSERT UNION ALL EXISTS CASE WHEN THEN ELSE END ASC DESC COUNT SUM MAX MIN AVG");
        private static readonly HashSet<string> TableAfter = Words("FROM JOIN INTO UPDATE");
        private const string Types = "STRING|INTEGER|DECIMAL|DATETIME|DATE|TIME|TABLE|VECTOR|LONG|STRINGBUILDER";

        // The patterns are written as in JavaScript; Js() gives them JavaScript's meaning (see there)
        private static readonly Regex CodeRe = Js(
            @"('(?:[^']|'')*')" +                                     // 1 text
            @"|\b([A-Za-z_][\w$]*)\.([A-Za-z_][\w$]*)(?=\s*\()" +     // 2,3 OBJECT.METHOD(
            @"|(OBJECT:|LOCAL:|\bPARAMETERS\b)" +                     // 4 declaration
            @"|\b(" + Types + @")(\s+)([A-Za-z_@][\w@]*)(?=\s*[,;])" + // 5,6,7 TYPE name
            @"|\b(CLASS\[[\w.]+\])(\s+)([A-Za-z_@][\w@]*)" +          // 8,9,10 CLASS[X] name
            @"|\b([A-Za-z_][\w$]*)\b" +                               // 11 word
            @"|\b(\d+(?:\.\d+)?)\b" +                                 // 12 number
            @"|([▎▏])");                                              // 13 scope mark
        private static readonly Regex TrailRe = Js(@"([A-Za-z_@][\w.@]*)?\[([^\[\]]*)\]|->\s*([01])(?=\s*(?:,|$|[▎▏]))|([▎▏])");
        private static readonly Regex TimeRe = Js(@"\[\s*(\d+)\s*(ms|sec|min)");
        private static readonly Regex ScopeRe = Js(@"^(\s*)(\([^)]*\)\s*)?([\w$]+)\.([\w$]+)(\.\d+)$");
        private static readonly Regex StmtRe = Js(@"^(\s*\[[^\]]*\])?(\s+)(\[(?:\([\w.]+\)\s+)?[\w$]+\.)([\w$]+)(\.\d+ \d+\])( : )(.*)$");
        private static readonly Regex AssignRe = Js(@"^([\w.@]+)(\s*=)(?!=)(\s*.*?;)(\s*)(\[.*\])?$");
        private static readonly Regex SqlStart = Js(@"^(SELECT|INSERT\s+INTO|UPDATE|DELETE)\b");
        private static readonly Regex SqlCont = Js(@"^\t+\s*(?:FROM|WHERE|AND|OR|SET|INTO|VALUES|HAVING|JOIN|ORDER\s+BY|GROUP\s+BY|(?:INNER|LEFT|RIGHT|OUTER|CROSS)\s+JOIN)\b");
        private static readonly Regex BlockRe = Js(@"^\t(?:[+-](?:TRUE|ELSE) BLOCK (?:STARTED|ENDED)|\+CASE\s*:\s*\S*|-?END(?:LOOP|WHILE|SWITCH|PARSE|FOR)|[+-](?:START|END) ITERATION\{\d+\}|\+ACTIVE ROW:\s*\d+\s+at\s+\S+|> CONDITION:|PARSE \d+\. ELEMENT OF\b)");
        private static readonly Regex TitleRe = Js(@"^TROIA CODE EXECUTION TRACE\b");
        private static readonly Regex SectionRe = Js(@"^(?:(?:TRACE|LOGIN|GENERAL) INFORMATION|(?:DIALOG|CLASS) CROSS REFERENCES)\s*$");
        private static readonly Regex RuleRe = Js(@"^-{10,}\s*$");
        private static readonly Regex FieldRe = Js(@"^([A-Za-z][\w ./]*?)(\s*:\s*)\[(.*)\](\s*)$");
        private static readonly Regex RefRe = Js(@"^(\S+)(\s*->\s*)(\S+?)(,\s*\(SYS\w*REF\))\s*$");
        private static readonly Regex OldNewRe = Js(@"^\[Old (?:Dialog|Class)\]\s*->\s*\[New (?:Dialog|Class)\]");
        private static readonly Regex MessageRe = Js(@"^MESSAGE\s+\w+\s+([EWIQSC])\d+");
        private static readonly Regex ErrorsRe = Js(@"^\tErrors?\s*:");
        private static readonly Regex ErrorItemRe = Js(@"^\t\t\s*\*");
        private static readonly Regex WarningsRe = Js(@"^\tWarnings?\s*:");
        private static readonly Regex DbRe = Js(@"^\t(->DB)( CACHE)?(\s*\[)(.*?)(\]?)((?:\s*[▎▏])*\s*)$");
        private static readonly Regex MissingRe = Js(@"^\t([\w@]+)\[([^\]]*)\](\s*<=\s*)(\?\?\?\s*!!Missing Parameter!!)");
        private static readonly Regex ParamRe = Js(@"^\t([\w@]+)\[(.*?)\](\s*<=\s*)(.*)$");
        private static readonly Regex RowsRe = Js(@"^\t\[(Number of rows|AFFECTED ROW COUNT)(\s*:\s*)(\d+)\]");
        private static readonly Regex SelectedRe = Js(@"^\t\[(?:SELECTED|NOTSELECTED|SYS_AFFECTEDROWCOUNT|DELETED)\[");
        private static readonly Regex ChangedRe = Js(@"^\t(CHANGED COLUMNS:)(.*)$");
        private static readonly Regex LogicRe = Js(@"^\t+\s*(?:&&|\|\|)");
        private static readonly Regex PairRe = Js(@"^\t([A-Z_][A-Z0-9_]*):(\S*)\s*$");

        /// <summary>Colours of one trace line (without its line end).</summary>
        public static Result TokenizeLine(string full)
        {
            var o = new Out();
            // A bare CR (multi-line SQL source) counts as a space, as in the web
            string line = (full.Length > MaxLine ? full.Substring(0, MaxLine) : full).Replace('\r', ' ');
            // Trailing scope marks and spaces
            int cut = line.Length;
            while (cut > 0 && (IsSpace(line[cut - 1]) || IsScope(line[cut - 1]))) cut--;
            string body = line.Substring(0, cut);
            Match m;

            if (TitleRe.IsMatch(body) || SectionRe.IsMatch(body)) { o.C(0, TrimEndLength(body), Kind.Anahtar); Tail(o, line, cut); }
            else if (RuleRe.IsMatch(body)) { o.C(0, body.Length, Kind.Sonuk); Tail(o, line, cut); }
            else if ((m = FieldRe.Match(body)).Success) { o.C(m.Groups[1], Kind.Sonuk); o.C(m.Groups[3], Kind.Deger); }
            else if ((m = RefRe.Match(body)).Success) { o.C(m.Groups[1], Kind.Sonuk); o.C(m.Groups[3], Kind.Sinif); o.C(m.Groups[4], Kind.Sonuk); }
            else if (OldNewRe.IsMatch(body)) o.C(0, TrimEndLength(body), Kind.Sonuk);
            else if ((m = ScopeRe.Match(body)).Success && line.IndexOf('▎', cut) >= 0)
            {
                Group lib = m.Groups[2];
                if (lib.Success) o.C(lib.Index, lib.Index + TrimEndLength(lib.Value), Kind.Sinif);
                o.C(m.Groups[3], Kind.Sinif); o.C(m.Groups[4], Kind.Metod); o.C(m.Groups[5], Kind.Sonuk); Tail(o, line, cut);
            }
            else if ((m = StmtRe.Match(body)).Success)
            {
                if (m.Groups[1].Success) Time(o, m.Groups[1].Value, 0);
                o.C(m.Groups[3], Kind.Sonuk); o.C(m.Groups[4], Kind.Metod); o.C(m.Groups[5], Kind.Sonuk);
                int rs = m.Groups[7].Index;
                string rest = m.Groups[7].Value;
                Match a;
                if ((a = MessageRe.Match(rest)).Success)
                {
                    int i = rest.IndexOf(" : ", StringComparison.Ordinal);
                    string head = i >= 0 ? rest.Substring(0, i) : rest;
                    if (a.Groups[1].Value == "E") { o.R.Band = true; o.C(rs, rs + rest.Length, Kind.Hata); }
                    else
                    {
                        Code(o, head, rs, false);
                        if (a.Groups[1].Value == "W") o.D(rs, rs + TrimEndLength(head));
                        if (i >= 0) Trail(o, rest.Substring(i), rs + i);
                    }
                }
                else if ((a = AssignRe.Match(rest)).Success)
                {
                    o.C(a.Groups[1], Kind.Param, rs);
                    Code(o, a.Groups[3].Value, rs + a.Groups[3].Index, false);
                    Group value = a.Groups[5];
                    if (value.Success) o.C(rs + value.Index + 1, rs + value.Index + value.Length - 1, Kind.Deger);
                }
                else
                {
                    int i = rest.IndexOf(" : ", StringComparison.Ordinal);
                    string c = i >= 0 ? rest.Substring(0, i) : rest;
                    Code(o, c, rs, SqlStart.IsMatch(c));
                    if (i >= 0) Trail(o, rest.Substring(i), rs + i);
                }
                Tail(o, line, cut);
            }
            else if (ErrorsRe.IsMatch(body) || ErrorItemRe.IsMatch(body)) { o.R.Band = true; o.C(FirstNonSpace(body), TrimEndLength(body), Kind.Hata); Tail(o, line, cut); }
            else if (WarningsRe.IsMatch(body)) { o.D(1, TrimEndLength(body)); Tail(o, line, cut); }
            else if ((m = DbRe.Match(line)).Success)
            {
                o.C(m.Groups[1], Kind.Sql);
                if (m.Groups[2].Success) o.C(m.Groups[2].Index + 1, End(m.Groups[2]), Kind.Sonuk);
                if (m.Groups[4].Length > 0) Code(o, m.Groups[4].Value, m.Groups[4].Index, true);
                else o.D(m.Groups[1].Index, End(m.Groups[5]));
                for (int i = m.Groups[6].Index; i < line.Length; i++) if (IsScope(line[i])) o.C(i, i + 1, Kind.Sonuk);
            }
            else if ((m = MissingRe.Match(body)).Success)
            {
                o.C(m.Groups[1], Kind.Param); o.C(m.Groups[2], Kind.Deger); o.C(m.Groups[3], Kind.Sonuk); o.D(m.Groups[4].Index, End(m.Groups[4])); Tail(o, line, cut);
            }
            else if ((m = ParamRe.Match(body)).Success)
            {
                o.C(m.Groups[1], Kind.Param); o.C(m.Groups[2], Kind.Deger); o.C(m.Groups[3], Kind.Sonuk); Trail(o, m.Groups[4].Value, m.Groups[4].Index); Tail(o, line, cut);
            }
            else if ((m = BlockRe.Match(body)).Success) { o.C(1, m.Length, Kind.Sonuk); Trail(o, body.Substring(m.Length), m.Length); Tail(o, line, cut); }
            else if ((m = RowsRe.Match(body)).Success) { o.C(1, End(m.Groups[2]), Kind.Sonuk); o.C(m.Groups[3], Kind.Deger); Tail(o, line, cut); }
            else if (SelectedRe.IsMatch(body)) { Trail(o, body, 0); Tail(o, line, cut); }
            else if ((m = ChangedRe.Match(body)).Success) { o.C(m.Groups[1], Kind.Sonuk); Trail(o, m.Groups[2].Value, m.Groups[2].Index); Tail(o, line, cut); }
            else if (SqlCont.IsMatch(body)) { Code(o, body, 0, true); Tail(o, line, cut); }
            else if (LogicRe.IsMatch(body)) { Code(o, body, 0, false); Tail(o, line, cut); }
            else if ((m = PairRe.Match(body)).Success) { o.C(m.Groups[1], Kind.Param); o.C(m.Groups[2], Kind.Deger); Tail(o, line, cut); }
            // Anything else (HTML, message text, continuation lines) stays as it is

            // Stable sort by start, as the web app's Array.sort
            List<Span> color = o.R.Color;
            for (int i = 1; i < color.Count; i++)
            {
                Span s = color[i];
                int j = i - 1;
                for (; j >= 0 && color[j].Start > s.Start; j--) color[j + 1] = color[j];
                color[j + 1] = s;
            }
            return o.R;
        }

        /// <summary>Colours of bare code without the line prefix (statement, SQL).</summary>
        public static List<Span> TokenizeCode(string str, bool? sql = null)
        {
            var o = new Out();
            Code(o, str, 0, sql ?? SqlStart.IsMatch(str.Substring(str.Length - TrimStartLength(str))));
            return o.R.Color;
        }

        /// <summary>Colours of an evaluation trail and NAME[value] lists.</summary>
        public static Result TokenizeTrail(string str)
        {
            var o = new Out();
            Trail(o, str, 0);
            return o.R;
        }

        // Code part of a statement (before its " : "); at is its offset in the line
        private static void Code(Out o, string str, int at, bool sql)
        {
            bool tbl = false;
            for (Match m = CodeRe.Match(str); m.Success; m = m.NextMatch())
            {
                GroupCollection g = m.Groups;
                if (g[1].Success) o.C(g[1], Kind.Deger, at);
                else if (g[2].Success)
                {
                    o.C(g[2], g[2].Value == "THIS" || g[2].Value == "SUPER" ? Kind.Anahtar : Kind.Sinif, at);
                    o.C(g[3], Kind.Metod, at);
                }
                else if (g[4].Success) o.C(g[4], Kind.Tanim, at);
                else if (g[5].Success) { o.C(g[5], Kind.Sinif, at); o.C(g[7], Kind.Param, at); }
                else if (g[8].Success) { o.C(g[8], Kind.Sinif, at); o.C(g[10], Kind.Param, at); }
                else if (g[11].Success)
                {
                    string w = g[11].Value;
                    if (sql)
                    {
                        if (tbl && !Sqlk.Contains(w)) o.C(g[11], Kind.Sinif, at);
                        else if (Sqlk.Contains(w)) o.C(g[11], Kind.Sql, at);
                        tbl = TableAfter.Contains(w);
                        continue;
                    }
                    if (Ctrl.Contains(w)) o.C(g[11], Kind.Anahtar, at);
                }
                else if (g[12].Success) o.C(g[12], Kind.Deger, at);
                else if (g[13].Success) o.C(g[13], Kind.Sonuk, at);
                if (sql && !g[11].Success) tbl = false;
            }
        }

        // Evaluation trail and NAME[value] lists
        private static void Trail(Out o, string str, int at)
        {
            for (Match m = TrailRe.Match(str); m.Success; m = m.NextMatch())
            {
                GroupCollection g = m.Groups;
                if (g[4].Success) { o.C(g[4], Kind.Sonuk, at); continue; }
                if (g[3].Success) { o.C(at + m.Index, at + End(g[3]), g[3].Value == "1" ? Kind.Sinif : Kind.Sonuk); continue; }
                o.C(g[2], Kind.Deger, at);
                if (g[1].Value == "SYS_STATUS" && g[2].Length > 0 && g[2].Value[0] >= '1' && g[2].Value[0] <= '9')
                    o.D(at + m.Index, at + m.Index + m.Length);
            }
        }

        // "[12 ms]" before a statement, coloured by how long it took
        private static void Time(Out o, string str, int at)
        {
            int b = str.IndexOf('['), e = str.LastIndexOf(']') + 1;
            Match m = TimeRe.Match(str);
            Kind? k = Kind.Sonuk;
            if (m.Success)
            {
                // Capped: only "1000 or more" matters, and a long digit run must not overflow
                long n = 0;
                foreach (char c in m.Groups[1].Value) n = Math.Min(n * 10 + (c - '0'), 1000000);
                k = m.Groups[2].Value != "ms" || n >= 1000 ? Kind.T1000 : n >= 200 ? Kind.T200 : n == 0 ? Kind.Sonuk : (Kind?)null;
            }
            o.C(at + b, at + e, k);
        }

        private static void Tail(Out o, string line, int cut)
        {
            for (int i = cut; i < line.Length; i++) if (IsScope(line[i])) o.C(i, i + 1, Kind.Sonuk);
        }

        private static int End(Group g) => g.Index + g.Length;

        private static bool IsScope(char c) => c == '▎' || c == '▏';

        /// <summary>JavaScript's white space and line terminators: what its \s matches and trim() removes.</summary>
        private static bool IsSpace(char c) =>
            c == ' ' || (c >= '\t' && c <= '\r') || c == '\u00A0' || c == '\u1680' || (c >= '\u2000' && c <= '\u200A') ||
            c == '\u2028' || c == '\u2029' || c == '\u202F' || c == '\u205F' || c == '\u3000' || c == '\uFEFF';

        private static int TrimEndLength(string s)
        {
            int n = s.Length;
            while (n > 0 && IsSpace(s[n - 1])) n--;
            return n;
        }

        private static int TrimStartLength(string s)
        {
            int i = 0;
            while (i < s.Length && IsSpace(s[i])) i++;
            return s.Length - i;
        }

        private static int FirstNonSpace(string s)
        {
            for (int i = 0; i < s.Length; i++) if (!IsSpace(s[i])) return i;
            return -1;
        }

        private static HashSet<string> Words(string list) => new HashSet<string>(list.Split(' '), StringComparer.Ordinal);

        private const string JsWord = "A-Za-z0-9_";
        private const string JsDigit = "0-9";
        private const string JsSpace = @"\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
        private const string JsBoundary = "(?:(?<=[" + JsWord + "])(?![" + JsWord + "])|(?<![" + JsWord + "])(?=[" + JsWord + "]))";

        /// <summary>
        /// A JavaScript pattern (no flags) with JavaScript's meaning in .NET: \w, \d and \b are ASCII there (.NET counts
        /// Turkish letters as word characters), \s is JavaScript's white space, "." stops at every line terminator and
        /// "$" matches only at the very end.
        /// </summary>
        private static Regex Js(string pattern)
        {
            var sb = new StringBuilder(pattern.Length * 2);
            bool inClass = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    char n = pattern[++i];
                    char lower = char.ToLowerInvariant(n);
                    string set = lower == 'w' ? JsWord : lower == 'd' ? JsDigit : lower == 's' ? JsSpace : null;
                    if (set != null)
                    {
                        bool negated = n != lower;
                        if (!inClass) sb.Append(negated ? "[^" : "[").Append(set).Append(']');
                        else if (!negated) sb.Append(set);
                        else throw new ArgumentException("Negated class escape inside a class: " + pattern);
                    }
                    else if (n == 'b' && !inClass) sb.Append(JsBoundary);
                    else if (n == 'B') throw new ArgumentException(@"\B is not supported: " + pattern);
                    else sb.Append('\\').Append(n);
                }
                else if (inClass)
                {
                    if (c == ']') inClass = false;
                    sb.Append(c);
                }
                else if (c == '[')
                {
                    inClass = true;
                    sb.Append(c);
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') sb.Append(pattern[++i]);
                }
                else if (c == '.') sb.Append(@"[^\n\r\u2028\u2029]");
                else if (c == '$') sb.Append(@"\z");
                else sb.Append(c);
            }
            return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        private sealed class Out
        {
            public readonly Result R = new Result();

            public void C(int s, int e, Kind? k)
            {
                if (k.HasValue && e > s) R.Color.Add(new Span(s, e, k.Value));
            }

            public void C(Group g, Kind k, int at = 0) => C(at + g.Index, at + g.Index + g.Length, k);

            public void D(int s, int e)
            {
                if (e > s) R.Deco.Add(new Span(s, e, default));
            }
        }
    }
}
