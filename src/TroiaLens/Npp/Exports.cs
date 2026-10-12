using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TroiaLens.Npp
{
    /// <summary>
    /// The six functions every Notepad++ plugin exports. Nothing here may touch WebView2 types directly:
    /// the assembly resolver below has to be in place before those assemblies are loaded.
    /// </summary>
    internal static class Exports
    {
        private static readonly IntPtr NamePtr = Marshal.StringToHGlobalUni(Plugin.Name);

        static Exports()
        {
            string dir = Path.GetDirectoryName(typeof(Exports).Assembly.Location);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string path = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        [DllExport]
        private static bool isUnicode() => true;

        [DllExport]
        private static void setInfo(NppData notepadPlusData)
        {
            Plugin.Npp = notepadPlusData;
            Plugin.CommandMenuInit();
        }

        [DllExport]
        private static IntPtr getFuncsArray(ref int nbF)
        {
            nbF = Plugin.Funcs.Count;
            return Plugin.Funcs.Pointer;
        }

        [DllExport]
        private static uint messageProc(uint message, IntPtr wParam, IntPtr lParam) => 1;

        [DllExport]
        private static IntPtr getName() => NamePtr;

        [DllExport]
        private static void beNotified(IntPtr notifyCode)
        {
            // struct Sci_NotifyHeader { void* hwndFrom; uptr_t idFrom; unsigned int code; }
            IntPtr hwndFrom = Marshal.ReadIntPtr(notifyCode);
            IntPtr idFrom = Marshal.ReadIntPtr(notifyCode, IntPtr.Size);
            uint code = (uint)Marshal.ReadInt32(notifyCode, IntPtr.Size * 2);
            if (hwndFrom != Plugin.Npp.NppHandle) return;
            Plugin.OnNotification(code, idFrom);
        }
    }
}
