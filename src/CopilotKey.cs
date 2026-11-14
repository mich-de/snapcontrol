// The Copilot key sends Win+Shift+F23. With no Copilot app installed it is free: a low-level keyboard hook
// catches F23 while Win is held, swallows it and calls the action (open/hide SnapControl). A neutral key
// (0xE8, unassigned) is injected in its place so releasing Win does not open the Start menu.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnapControl
{
    static class CopilotKey
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        struct KBD { public uint Vk, Scan, Flags, Time; public UIntPtr Extra; }

        const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
        const uint VkF23 = 0x86, KeyUp = 2;
        const int VkLWin = 0x5B, VkRWin = 0x5C;

        static IntPtr hook = IntPtr.Zero;
        static HookProc proc;   // kept referenced: the GC must not collect the delegate while the hook is set
        static Action action;

        public static bool Active { get { return hook != IntPtr.Zero; } }

        public static void Start(Action onPress)
        {
            action = onPress;
            if (hook != IntPtr.Zero) return;
            proc = Hook;
            using (var p = Process.GetCurrentProcess())
                hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(p.MainModule.ModuleName), 0);
        }

        public static void Stop()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBD)Marshal.PtrToStructure(lParam, typeof(KBD));
                int msg = wParam.ToInt32();
                bool win = (GetAsyncKeyState(VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;
                if (k.Vk == VkF23 && win && (k.Flags & 0x10) == 0)   // not injected
                {
                    if (msg == WmKeyDown || msg == WmSysKeyDown)
                    {
                        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                        keybd_event(0xE8, 0, KeyUp, UIntPtr.Zero);
                        var a = action;
                        if (a != null) a();
                    }
                    return new IntPtr(1);   // swallowed: Windows does not open Copilot/search
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }
    }
}
