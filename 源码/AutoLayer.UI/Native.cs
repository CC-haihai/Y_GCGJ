using System;
using System.Runtime.InteropServices;

namespace AutoLayer.UI
{
    /// <summary>
    /// 圆盘和状态条要用的 Win32 入口。全部集中在这一点，别处不直接 DllImport。
    ///
    /// 注意 SetWindowLong / GetWindowLong 用 int 版本是对的：
    /// 即使 x64 下扩展样式也只有 32 位，用 ...Ptr 反而容易踩错。
    /// </summary>
    internal static class Native
    {
        // ---------------- 钩子 ----------------

        public const int WH_MOUSE = 7;

        /// <summary>
        /// 线程级键盘钩子。**这台机器上装了不触发** —— 实测见 cadkit/keytest：
        /// 纯 WinForms 消息循环下 SetWindowsHookEx 返回成功，keybd_event 也的确进了队列
        /// （窗口自己收到了 KeyDown），但钩子回调一次都没被调用。
        /// 插件里已经不用它了，留个常量只为说明清楚。
        /// </summary>
        public const int WH_KEYBOARD = 2;

        /// <summary>
        /// 全局低层键盘钩子。实测能收到，面板快捷键走这个。
        /// 必须 dwThreadId = 0（给线程 id 会失败 err=1429 ERROR_GLOBAL_ONLY_HOOK）。
        /// 因为是全局的，回调里必须自己判断"前台是不是 CAD"，否则在别的软件里按同一个组合也会弹面板。
        /// </summary>
        public const int WH_KEYBOARD_LL = 13;

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_RBUTTONDBLCLK = 0x0206;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_XBUTTONUP = 0x020C;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int VK_ESCAPE = 0x1B;

        // 修饰键状态：快捷键要在钩子里判断 Ctrl/Alt/Shift 有没有按着
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;      // Alt
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // ---------------- 光标 / 窗口 ----------------

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        public const uint GA_ROOT = 2;

        /// <summary>取窗口所属进程 —— 全局键盘钩子靠这个判断"前台是不是 CAD 自己"。</summary>
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        public const uint GW_CHILD = 5;
        public const uint GW_HWNDNEXT = 2;

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);

        // ---------------- 窗口样式 ----------------

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        // ---------------- 命中测试（状态条"只有配置名可点"）----------------
        // WM_NCHITTEST 返回 HTTRANSPARENT 时，Windows 会把这一下转给**下层窗口**（也就是 CAD 画图区），
        // 这正是"整条状态条穿透、只有中间那块芯片可点"的做法。
        // 用 WS_EX_TRANSPARENT 做不到：那个是整窗级的，一刀切。
        public const int WM_NCHITTEST = 0x0084;
        public const int HTCLIENT = 1;
        public const int HTTRANSPARENT = -1;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                               int X, int Y, int cx, int cy, uint uFlags);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        // ---------------- 抓屏（毛玻璃底）----------------
        //
        // 半透明盘压在图纸上，底下的线稿会直接穿过文字 —— 实测"门窗""取消"这种字
        // 只要有一根白线压过去就散了。真正的毛玻璃必须先把底糊掉，所以弹盘之前
        // 抓一帧屏幕，自己上 BlurEffect。
        //
        // 不用 DWM 的 ACRYLICBLURBEHIND：那个要求窗口不开 AllowsTransparency，
        // 而圆盘必须是逐像素透明（四角要透出图纸），两者冲突。

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr hdcDst, int x, int y, int cx, int cy,
                                         IntPtr hdcSrc, int srcX, int srcY, uint rop);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        public const uint SRCCOPY = 0x00CC0020;
    }
}
