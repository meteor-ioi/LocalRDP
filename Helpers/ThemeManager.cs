using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace rdpManager.Helpers
{
    public enum ThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    public static class ThemeManager
    {
        private const string REG_PATH = @"Software\LocalRDP";
        private const string THEME_VAL = "ThemeMode";

        public static ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

        public static void Initialize()
        {
            LoadSettings();
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        }

        private static void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    if (key != null)
                    {
                        var val = key.GetValue(THEME_VAL);
                        if (val != null && int.TryParse(val.ToString(), out int modeInt))
                        {
                            if (Enum.IsDefined(typeof(ThemeMode), modeInt))
                            {
                                CurrentMode = (ThemeMode)modeInt;
                                return;
                            }
                        }
                    }
                }
            }
            catch { }
            CurrentMode = ThemeMode.System;
        }

        public static void SaveThemeMode(ThemeMode mode)
        {
            CurrentMode = mode;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                {
                    key.SetValue(THEME_VAL, (int)mode, RegistryValueKind.DWord);
                }
            }
            catch { }
            ApplyTheme();
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && CurrentMode == ThemeMode.System)
            {
                ApplyTheme();
            }
        }

        private static bool IsSystemDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("AppsUseLightTheme");
                        if (val != null)
                        {
                            return (int)val == 0;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public static void ApplyTitleBarTheme(Window window, bool isDark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                
                int useImmersiveDarkMode = isDark ? 1 : 0;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));
                if (hr != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
            catch { }
        }

        public static bool IsDarkTheme()
        {
            switch (CurrentMode)
            {
                case ThemeMode.System: return IsSystemDark();
                case ThemeMode.Dark: return true;
                case ThemeMode.Light: return false;
            }
            return false;
        }

        public static void ApplyTheme()
        {
            bool useDark = IsDarkTheme();

            string themeName = useDark ? "Dark.xaml" : "Light.xaml";
            var uri = new Uri($"pack://application:,,,/Themes/{themeName}", UriKind.Absolute);
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dict = new ResourceDictionary { Source = uri };
                var appDicts = Application.Current.Resources.MergedDictionaries;
                
                // 替换索引0的字典
                if (appDicts.Count > 0)
                {
                    appDicts[0] = dict;
                }
                else
                {
                    appDicts.Add(dict);
                }
                
                if (Application.Current.MainWindow != null)
                {
                    ApplyTitleBarTheme(Application.Current.MainWindow, useDark);
                }
            });
        }
    }
}
