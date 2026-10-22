using System;
using System.IO;
using System.Linq;
using System.Text;
using TroiaLens.Npp;

namespace TroiaLens
{
    /// <summary>Stored in plugins\config\TroiaLens.ini.</summary>
    internal sealed class Settings
    {
        private const string Section = "TroiaLens";
        private readonly string _file;

        /// <summary>Open the panel automatically when a file with one of <see cref="Extensions"/> becomes active.</summary>
        public bool AutoOpen { get; set; } = true;

        /// <summary>Re-analyse on every tab switch while the panel is open, not only for trace extensions.</summary>
        public bool AnalyzeOnTabSwitch { get; set; } = true;

        public string[] Extensions { get; set; } = { ".trace" };

        /// <summary>Allows F12 developer tools in the panel.</summary>
        public bool DevTools { get; set; }

        /// <summary>Statement detail slides over from the right instead of docking under the views.</summary>
        public bool DetailSlideOver { get; set; }

        /// <summary>Colour the text of trace documents in the editor.</summary>
        public bool SyntaxColors { get; set; } = true;

        public Settings(string configDir)
        {
            _file = Path.Combine(configDir, "TroiaLens.ini");
        }

        public void Load()
        {
            AutoOpen = Read("AutoOpen", AutoOpen);
            AnalyzeOnTabSwitch = Read("AnalyzeOnTabSwitch", AnalyzeOnTabSwitch);
            DevTools = Read("DevTools", DevTools);
            DetailSlideOver = Read("DetailSlideOver", DetailSlideOver);
            SyntaxColors = Read("SyntaxColors", SyntaxColors);
            string ext = Read("Extensions", string.Join(";", Extensions));
            Extensions = ext.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.StartsWith(".") ? e : "." + e)
                .ToArray();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                Write("AutoOpen", AutoOpen ? "1" : "0");
                Write("AnalyzeOnTabSwitch", AnalyzeOnTabSwitch ? "1" : "0");
                Write("Extensions", string.Join(";", Extensions));
                Write("DevTools", DevTools ? "1" : "0");
                Write("DetailSlideOver", DetailSlideOver ? "1" : "0");
                Write("SyntaxColors", SyntaxColors ? "1" : "0");
            }
            catch (Exception)
            {
                // A read-only config folder must not break Notepad++
            }
        }

        public bool IsTraceExtension(string path) =>
            !string.IsNullOrEmpty(path) && Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        private string Read(string key, string def)
        {
            var sb = new StringBuilder(1024);
            Win32.GetPrivateProfileString(Section, key, def, sb, sb.Capacity, _file);
            return sb.ToString();
        }

        private bool Read(string key, bool def) => Read(key, def ? "1" : "0").Trim() == "1";

        private void Write(string key, string value) => Win32.WritePrivateProfileString(Section, key, value, _file);
    }
}
