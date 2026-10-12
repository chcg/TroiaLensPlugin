using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TroiaLens.Npp
{
    /// <summary>Marks a static method to be exported from the DLL under its own name (see tools\DllExport.ps1).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class DllExportAttribute : Attribute
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NppData
    {
        public IntPtr NppHandle;
        public IntPtr ScintillaMainHandle;
        public IntPtr ScintillaSecondHandle;
    }

    /// <summary>Docking dialog registration (tTbData in Docking.h).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TbData
    {
        public IntPtr hClient;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        public int dlgID;
        public uint uMask;
        public IntPtr hIconTab;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszAddInfo;
        public Rect rcFloat;
        public int iPrevCont;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszModuleName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NppFuncItemDelegate();

    internal static class NppMsg
    {
        private const uint NPPMSG = 0x0400 + 1000;
        private const uint RUNCOMMAND_USER = 0x0400 + 3000;

        public const uint NPPM_GETCURRENTSCINTILLA = NPPMSG + 4;
        public const uint NPPM_DMMSHOW = NPPMSG + 30;
        public const uint NPPM_DMMHIDE = NPPMSG + 31;
        public const uint NPPM_DMMREGASDCKDLG = NPPMSG + 33;
        public const uint NPPM_SWITCHTOFILE = NPPMSG + 37;
        public const uint NPPM_SETMENUITEMCHECK = NPPMSG + 40;
        public const uint NPPM_GETPLUGINSCONFIGDIR = NPPMSG + 46;
        public const uint NPPM_GETCURRENTBUFFERID = NPPMSG + 60;
        public const uint NPPM_ISDARKMODEENABLED = NPPMSG + 107;
        public const uint NPPM_GETFULLCURRENTPATH = RUNCOMMAND_USER + 1;

        private const uint NPPN_FIRST = 1000;
        public const uint NPPN_READY = NPPN_FIRST + 1;
        public const uint NPPN_FILESAVED = NPPN_FIRST + 8;
        public const uint NPPN_SHUTDOWN = NPPN_FIRST + 9;
        public const uint NPPN_BUFFERACTIVATED = NPPN_FIRST + 10;
        public const uint NPPN_DARKMODECHANGED = NPPN_FIRST + 27;

        // Docking.h
        public const uint DWS_ICONTAB = 0x00000001;
        public const uint DWS_DF_CONT_RIGHT = 1u << 28; // CONT_RIGHT
        public const int DMN_CLOSE = 1050 + 1;
    }

    internal static class SciMsg
    {
        public const uint SCI_GETLENGTH = 2006;
        public const uint SCI_GOTOPOS = 2025;
        public const uint SCI_GETCODEPAGE = 2137;
        public const uint SCI_LINEFROMPOSITION = 2166;
        public const uint SCI_VISIBLEFROMDOCLINE = 2220;
        public const uint SCI_ENSUREVISIBLE = 2232;
        public const uint SCI_LINESONSCREEN = 2370;
        public const uint SCI_GRABFOCUS = 2400;
        public const uint SCI_GETCHARACTERPOINTER = 2520;
        public const uint SCI_SETFIRSTVISIBLELINE = 2613;

        public const int SC_CP_UTF8 = 65001;
    }

    internal static class Win32
    {
        public const int WM_NOTIFY = 0x004E;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, out int lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder value, int size, string file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool WritePrivateProfileString(string section, string key, string value, string file);
    }
}
