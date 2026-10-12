using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TroiaLens.Npp
{
    /// <summary>
    /// Native FuncItem array handed to Notepad++ by getFuncsArray. Notepad++ writes the command ids
    /// back into this memory, so it stays allocated for the life of the process.
    /// </summary>
    internal sealed class FuncItems
    {
        // struct FuncItem { wchar_t _itemName[64]; PFUNCPLUGINCMD _pFunc; int _cmdID; bool _init2Check; ShortcutKey* _pShKey; }
        private const int NameChars = 64;
        private static readonly int FuncOffset = NameChars * 2;
        private static readonly int CmdIdOffset = FuncOffset + IntPtr.Size;
        private static readonly int CheckOffset = CmdIdOffset + 4;
        private static readonly int ShortcutOffset = (CheckOffset + 1 + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
        private static readonly int ItemSize = ShortcutOffset + IntPtr.Size;

        private readonly List<(string Name, NppFuncItemDelegate Func, bool Checked)> _items = new List<(string, NppFuncItemDelegate, bool)>();
        private IntPtr _native = IntPtr.Zero;

        public int Count => _items.Count;

        /// <summary>Adds a menu command and returns its index. A null action adds a separator.</summary>
        public int Add(string name, Action action, bool isChecked = false)
        {
            if (_native != IntPtr.Zero) throw new InvalidOperationException("Menu already handed to Notepad++");
            // The delegate is kept in the list so the GC never collects a callback Notepad++ still holds
            NppFuncItemDelegate func = action == null ? null : new NppFuncItemDelegate(action);
            _items.Add((name ?? "", func, isChecked));
            return _items.Count - 1;
        }

        public IntPtr Pointer
        {
            get
            {
                if (_native != IntPtr.Zero) return _native;
                _native = Marshal.AllocHGlobal(ItemSize * _items.Count);
                for (int i = 0; i < _items.Count; i++)
                {
                    IntPtr item = _native + i * ItemSize;
                    for (int b = 0; b < ItemSize; b++) Marshal.WriteByte(item, b, 0);
                    char[] name = _items[i].Name.ToCharArray();
                    Marshal.Copy(name, 0, item, Math.Min(name.Length, NameChars - 1));
                    Marshal.WriteIntPtr(item, FuncOffset, _items[i].Func == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_items[i].Func));
                    Marshal.WriteByte(item, CheckOffset, (byte)(_items[i].Checked ? 1 : 0));
                }
                return _native;
            }
        }

        /// <summary>Command id Notepad++ assigned to the item (valid after getFuncsArray).</summary>
        public int CmdId(int index) => _native == IntPtr.Zero ? 0 : Marshal.ReadInt32(_native + index * ItemSize + CmdIdOffset);
    }
}
