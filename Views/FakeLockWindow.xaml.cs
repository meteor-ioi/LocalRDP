using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace rdpManager.Views
{
    public partial class FakeLockWindow : Window
    {
        private static List<FakeLockWindow> _activeWindows = new List<FakeLockWindow>();
        private static IntPtr _hookID = IntPtr.Zero;
        private static string _correctPassword = "";

        private readonly bool _isPrimary;
        private DispatcherTimer? _timer;

        // Win32 APIs
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc _proc = HookCallback;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public int dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern short GetKeyState(int keyCode);

        public FakeLockWindow(string correctPassword, bool isPrimary)
        {
            InitializeComponent();
            _isPrimary = isPrimary;

            if (_isPrimary)
            {
                _correctPassword = correctPassword;
                LockContentPanel.Visibility = Visibility.Visible;
                Cursor = Cursors.Arrow;

                // 初始化时钟
                UpdateTimeText();
                _timer = new DispatcherTimer();
                _timer.Interval = TimeSpan.FromSeconds(1);
                _timer.Tick += (s, e) => UpdateTimeText();
                _timer.Start();

                this.Loaded += (s, e) =>
                {
                    TxtUnlockPassword.Focus();
                };
            }
            else
            {
                LockContentPanel.Visibility = Visibility.Collapsed;
                Cursor = Cursors.None; // 隐藏辅屏鼠标
            }

            // 防止窗口通过拖拽、关闭等手段退出
            this.Closing += (s, e) =>
            {
                // 如果锁屏仍激活，且不是通过正常的 Unlock 流程关闭，则阻止关闭
                if (_activeWindows.Contains(this))
                {
                    e.Cancel = true;
                }
            };

            // 失去焦点时重新强行置顶
            this.Deactivated += (s, e) =>
            {
                if (_activeWindows.Contains(this))
                {
                    this.Topmost = false;
                    this.Topmost = true;
                    this.Activate();
                    if (_isPrimary)
                    {
                        TxtUnlockPassword.Focus();
                    }
                }
            };
        }

        private void UpdateTimeText()
        {
            TxtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            TxtDate.Text = DateTime.Now.ToString("yyyy年M月d日 dddd");
        }

        public static void ShowLock(string targetPassword)
        {
            if (_activeWindows.Count > 0) return;

            // 1. 修改注册表临时禁用“锁定”选项，防 Ctrl+Alt+Del 后点击锁定
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (key != null)
                    {
                        key.SetValue("DisableLockWorkstation", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"修改注册表失败: {ex.Message}");
            }

            // 2. 安装低级键盘钩子
            _hookID = SetHook(_proc);

            // 3. 遍历多显示器，在各屏幕上显示锁屏窗口
            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var screen in screens)
            {
                var w = new FakeLockWindow(targetPassword, screen.Primary);
                _activeWindows.Add(w);
                
                // 将窗口显示到对应屏幕
                w.Left = screen.Bounds.Left + 10;
                w.Top = screen.Bounds.Top + 10;
                w.Show();
                w.WindowState = WindowState.Maximized;
            }
        }

        public static void UnlockAll()
        {
            // 1. 恢复注册表
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("DisableLockWorkstation", false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复注册表失败: {ex.Message}");
            }

            // 2. 卸载键盘钩子
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }

            // 3. 关闭所有锁屏窗口
            var windowsToClose = new List<FakeLockWindow>(_activeWindows);
            _activeWindows.Clear(); // 先清空，使 closing 事件中的 e.Cancel = true 不生效

            foreach (var w in windowsToClose)
            {
                if (w._timer != null)
                {
                    w._timer.Stop();
                }
                w.Close();
            }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                    GetModuleHandle(curModule.ModuleName!), 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                KBDLLHOOKSTRUCT kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                bool alt = (kb.flags & 0x20) != 0; // LLKHF_ALTDOWN
                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

                // 屏蔽 Win 键
                if (kb.vkCode == 91 || kb.vkCode == 92)
                {
                    return (IntPtr)1;
                }

                // 屏蔽 Alt + Tab
                if (kb.vkCode == 9 && alt)
                {
                    return (IntPtr)1;
                }

                // 屏蔽 Alt + Esc
                if (kb.vkCode == 27 && alt)
                {
                    return (IntPtr)1;
                }

                // 屏蔽 Ctrl + Esc
                if (kb.vkCode == 27 && ctrl)
                {
                    return (IntPtr)1;
                }

                // 屏蔽 Ctrl + Shift + Esc
                if (kb.vkCode == 27 && ctrl && shift)
                {
                    return (IntPtr)1;
                }

                // 屏蔽 Alt + F4
                if (kb.vkCode == 115 && alt)
                {
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void PerformUnlock()
        {
            if (TxtUnlockPassword.Password == _correctPassword)
            {
                UnlockAll();
            }
            else
            {
                TxtError.Visibility = Visibility.Visible;
                TxtUnlockPassword.Clear();
                TxtUnlockPassword.Focus();
            }
        }

        private void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            PerformUnlock();
        }

        private void TxtUnlockPassword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PerformUnlock();
            }
        }
    }
}
