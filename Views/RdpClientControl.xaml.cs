using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MSTSCLib;
using rdpManager.Helpers;

namespace rdpManager.Views
{
    public partial class RdpClientControl : UserControl
    {
        private AxMSTSCLib.AxMsTscAxNotSafeForScripting? _rdpControl;
        private string _password = string.Empty;

        public string ServerName { get; private set; } = string.Empty;
        public string UserName { get; private set; } = string.Empty;
        public bool IsConnected { get; private set; } = false;

        // 是否处于隐藏（后台保活）状态
        private bool _isHiddenSession = false;
        public bool IsHiddenSession
        {
            get => _isHiddenSession;
            set
            {
                _isHiddenSession = value;
                if (_isHiddenSession)
                {
                    this.Visibility = Visibility.Collapsed;
                }
                else
                {
                    this.Visibility = Visibility.Visible;

                    // 强制 WPF 布局更新
                    RdpHost?.InvalidateVisual();
                    RdpHost?.UpdateLayout();
                    // 延迟到渲染完成后，强制刷新底层 Win32 HWND（InvalidateVisual 仅影响 WPF 层，不触发 HWND 的 WM_PAINT）
                    Dispatcher.InvokeAsync(ForceHwndRepaint, System.Windows.Threading.DispatcherPriority.Render);
                }
            }
        }

        public event EventHandler? OnRdpConnected;
        public event EventHandler<string>? OnRdpDisconnected;

        private string? _pendingServer;
        private string? _pendingUsername;
        private string? _pendingPassword;
        private bool _pendingEnableUsb;
        private bool _pendingEnableSmartSizing;
        private bool _pendingEnableClipboard;
        private bool _pendingMuteAudio;
        private int _pendingDesktopWidth;
        private int _pendingDesktopHeight;
        private int _pendingDesktopScaleFactor;
        private bool _connectPending = false;

        private bool _isWaitingForSize = false;
        private SizeChangedEventHandler? _sizeChangedHandler;

        public RdpClientControl()
        {
            InitializeComponent();
            this.Loaded += RdpClientControl_Loaded;
        }

        private void RdpClientControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rdpControl == null)
                {
                    Logger.LogInfo("开始在 WinFormsHost 中实例化 AxMsTscAxNotSafeForScripting 控件...");
                    _rdpControl = new AxMSTSCLib.AxMsTscAxNotSafeForScripting();
                    _rdpControl.BeginInit();
                    RdpHost.Child = _rdpControl;
                    _rdpControl.EndInit();

                    // 让 WinForms 控件铺满容器
                    _rdpControl.Dock = System.Windows.Forms.DockStyle.Fill;

                    // 绑定事件
                    _rdpControl.OnConnected += (s, ev) =>
                    {
                        Logger.LogInfo($"AxMsTscAx 触发 OnConnected 回调: Server={ServerName}, ControlSize={_rdpControl?.Width}x{_rdpControl?.Height}");
                        IsConnected = true;
                        OnRdpConnected?.Invoke(this, EventArgs.Empty);
                        // 连接建立后强制刷新 HWND 画面（ActiveX 控件首帧可能不自动渲染）
                        Dispatcher.InvokeAsync(ForceHwndRepaint, System.Windows.Threading.DispatcherPriority.Render);
                    };

                    _rdpControl.OnDisconnected += (s, ev) =>
                    {
                        IsConnected = false;
                        string reason = $"连接已断开 (代码: {ev.discReason})";
                        Logger.LogWarning($"AxMsTscAx 触发 OnDisconnected 回调: Server={ServerName}, Reason={reason}");
                        OnRdpDisconnected?.Invoke(this, reason);
                    };
                }

                if (_connectPending)
                {
                    Logger.LogInfo("检测到有缓存的连接请求，触发尺寸就绪判断以准备连接。");
                    _connectPending = false;
                    
                    Connect(_pendingServer!, _pendingUsername!, _pendingPassword!, 
                        _pendingEnableUsb, _pendingEnableSmartSizing, _pendingEnableClipboard, _pendingMuteAudio,
                        _pendingDesktopWidth, _pendingDesktopHeight, _pendingDesktopScaleFactor);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("初始化 RDP 控件失败", ex);
                MessageBox.Show($"初始化 RDP 控件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 配置并连接 RDP
        /// </summary>
        public void Connect(string server, string username, string password, 
            bool enableUsb = false, bool enableSmartSizing = true, 
            bool enableClipboard = true, bool muteAudio = true,
            int desktopWidth = 0, int desktopHeight = 0, int desktopScaleFactor = 100)
        {
            Logger.LogInfo($"RdpClientControl.Connect() 被调用: Server={server}, Username={username}, EnableUsb={enableUsb}, EnableSmartSizing={enableSmartSizing}, EnableClipboard={enableClipboard}, MuteAudio={muteAudio}");
            
            // 缓存所有连接参数，供实际连接时读取
            _pendingServer = server;
            _pendingUsername = username;
            _pendingPassword = password;
            _pendingEnableUsb = enableUsb;
            _pendingEnableSmartSizing = enableSmartSizing;
            _pendingEnableClipboard = enableClipboard;
            _pendingMuteAudio = muteAudio;
            _pendingDesktopWidth = desktopWidth;
            _pendingDesktopHeight = desktopHeight;
            _pendingDesktopScaleFactor = desktopScaleFactor;

            if (_rdpControl == null)
            {
                Logger.LogInfo("WinForms RDP 控件尚未完成 Load，缓存连接请求以待加载完成后执行。");
                _connectPending = true;
                return;
            }

            // 判断容器实际宽度和高度是否均大于 0，以防止以 0x0 分辨率向 ActiveX 控件发起连接导致异常断开
            if (RdpHost.ActualWidth > 0 && RdpHost.ActualHeight > 0)
            {
                Logger.LogInfo($"容器尺寸已就绪: {RdpHost.ActualWidth}x{RdpHost.ActualHeight}，直接执行连接。");
                ExecutePendingConnect();
            }
            else
            {
                if (!_isWaitingForSize)
                {
                    Logger.LogInfo("容器尺寸尚未就绪 (0x0)，将等待 SizeChanged 事件触发后再执行连接。");
                    _isWaitingForSize = true;
                    _sizeChangedHandler = (s, args) =>
                    {
                        if (RdpHost.ActualWidth > 0 && RdpHost.ActualHeight > 0)
                        {
                            Logger.LogInfo($"容器尺寸已就绪 (通过 SizeChanged): {RdpHost.ActualWidth}x{RdpHost.ActualHeight}，开始执行连接。");
                            _isWaitingForSize = false;
                            RdpHost.SizeChanged -= _sizeChangedHandler;
                            ExecutePendingConnect();
                        }
                    };
                    RdpHost.SizeChanged += _sizeChangedHandler;
                }
                else
                {
                    Logger.LogInfo("已经在等待容器尺寸就绪的 SizeChanged 事件，仅更新连接参数。");
                }
            }
        }

        private void ExecutePendingConnect()
        {
            // 延迟到 Input 优先级执行，以确保 WPF 布局和 WinFormsHost 完全结算完成
            Dispatcher.InvokeAsync(() =>
            {
                Logger.LogInfo($"开始执行真实的 RDP 连接: Server={_pendingServer}, Host大小={RdpHost.ActualWidth}x{RdpHost.ActualHeight}");
                
                if (_rdpControl == null)
                {
                    Logger.LogWarning("ExecutePendingConnect 触发时 _rdpControl 为 null，已取消连接。");
                    return;
                }

                ServerName = _pendingServer!;
                UserName = _pendingUsername!;
                _password = _pendingPassword!;

                _rdpControl.Server = _pendingServer;
                _rdpControl.UserName = _pendingUsername;
                
                int desktopWidth = _pendingDesktopWidth;
                int desktopHeight = _pendingDesktopHeight;

                // 如果未指定分辨率，使用本机系统主屏幕分辨率作为远程桌面画布尺寸，配合 SmartSizing 适应控件
                if (desktopWidth <= 0 || desktopHeight <= 0)
                {
                    desktopWidth = (int)SystemParameters.PrimaryScreenWidth;
                    desktopHeight = (int)SystemParameters.PrimaryScreenHeight;
                }

                _rdpControl.DesktopWidth = desktopWidth;
                _rdpControl.DesktopHeight = desktopHeight;
                
                // 明确指定颜色深度为 32 位，解决部分系统默认低色深导致的黑屏问题
                var rdpClient = _rdpControl.GetOcx() as IMsRdpClient;
                if (rdpClient != null)
                {
                    rdpClient.ColorDepth = 32;
                }

                // 设置密码 (通过 COM 接口转换设置明文密码)
                var advancedSettings = (IMsRdpClientAdvancedSettings)_rdpControl.AdvancedSettings;
                advancedSettings.ClearTextPassword = _pendingPassword;

                // 启用 CredSSP 支持 (避免本地环境因为 NLA 导致黑屏或闪退)
                try
                {
                    var advancedSettings7 = _rdpControl.AdvancedSettings as IMsRdpClientAdvancedSettings7;
                    if (advancedSettings7 != null)
                    {
                        advancedSettings7.EnableCredSspSupport = true;
                    }
                }
                catch { }

                // RDP 基础优化配置
                var advancedSettings5 = (IMsRdpClientAdvancedSettings5)_rdpControl.AdvancedSettings;
                advancedSettings5.SmartSizing = _pendingEnableSmartSizing;       // 分辨率自适应缩放
                advancedSettings5.RedirectClipboard = _pendingEnableClipboard; // 启用双向剪贴板
                advancedSettings5.RedirectPrinters = false; // 禁用打印机重定向以优化速度
                advancedSettings5.RedirectSmartCards = false;
                advancedSettings5.BitmapPeristence = 0; // 禁用位图缓存，防止本地回环连接时缓存损坏导致黑屏
                advancedSettings5.AuthenticationLevel = 0; // 跳过服务器证书验证（本地回环使用自签名证书，标准验证会导致连接静默挂起）

                // 应用 DPI 缩放配置 (需要高级接口或动态绑定以兼容旧系统)
                if (_pendingDesktopScaleFactor > 100)
                {
                    try
                    {
                        dynamic advancedSettingsDynamic = _rdpControl.AdvancedSettings;
                        advancedSettingsDynamic.DesktopScaleFactor = (uint)_pendingDesktopScaleFactor;
                        advancedSettingsDynamic.DeviceScaleFactor = 100u; // 通常设备缩放比为 100%，以保证界面元素不会过小
                        Logger.LogInfo($"已注入 DPI 缩放比: {_pendingDesktopScaleFactor}%");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"当前 RDP 客户端版本不支持自定义缩放比，将忽略缩放设置: {ex.Message}");
                    }
                }

                // 音频优化：1 = 不在本地播放音频（完全静音运行，节省 CPU 开销）
                if (_rdpControl.SecuredSettings != null)
                {
                    var securedSettings = (IMsRdpClientSecuredSettings)_rdpControl.SecuredSettings;
                    securedSettings.AudioRedirectionMode = _pendingMuteAudio ? 1 : 0;
                }

                // 如果开启了外设重定向 (UmWrap 功能)
                if (_pendingEnableUsb)
                {
                    advancedSettings5.RedirectDevices = true; // 允许即插即用外设重定向（USB/摄像头）
                }

                try
                {
                    Logger.LogInfo($"调用 ActiveX 控件 Connect(): Server={_pendingServer}, ControlSize={_rdpControl.Width}x{_rdpControl.Height}, HandleCreated={_rdpControl.IsHandleCreated}");
                    _rdpControl.Connect();
                    // 连接发起后延迟强制刷新 HWND 确保初始帧渲染
                    Dispatcher.InvokeAsync(ForceHwndRepaint, System.Windows.Threading.DispatcherPriority.Render);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"调用 Connect() 抛出异常: {ex.Message}", ex);
                    OnRdpDisconnected?.Invoke(this, $"连接尝试失败: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// 主动彻底断开会话
        /// </summary>
        public void Disconnect()
        {
            if (_isWaitingForSize && _sizeChangedHandler != null)
            {
                _isWaitingForSize = false;
                RdpHost.SizeChanged -= _sizeChangedHandler;
            }

            if (_rdpControl != null && IsConnected)
            {
                try
                {
                    _rdpControl.Disconnect();
                }
                catch
                {
                    // 忽略断开异常
                }
            }
        }

        /// <summary>
        /// 截取当前后台渲染缓冲区的画面作为网格预览缩略图
        /// </summary>
        public BitmapSource? CaptureThumbnail()
        {
            if (_rdpControl == null || !IsConnected) return null;

            try
            {
                // 获取控件的实际物理尺寸
                int width = _rdpControl.Width;
                int height = _rdpControl.Height;

                if (width <= 0 || height <= 0)
                {
                    width = 800; // 默认大小兜底
                    height = 600;
                }

                using (Bitmap bmp = new Bitmap(width, height))
                {
                    // 使用 WinForms 控件自带的 DrawToBitmap 从显存/渲染表面抓取画面
                    _rdpControl.DrawToBitmap(bmp, new Rectangle(0, 0, width, height));
                    
                    return ConvertBitmapToSource(bmp);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将 GDI Bitmap 转换为 WPF 能够渲染的 BitmapSource
        /// </summary>
        private BitmapSource ConvertBitmapToSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_FRAME = 0x0400;

        /// <summary>
        /// 强制刷新底层 Win32 HWND（绕过 WPF 渲染管道，直接触发 WM_PAINT）
        /// </summary>
        private void ForceHwndRepaint()
        {
            try
            {
                if (_rdpControl != null && _rdpControl.IsHandleCreated)
                {
                    IntPtr hwnd = _rdpControl.Handle;
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME);
                    Logger.LogInfo($"已强制刷新 RDP HWND=0x{hwnd:X}, Size={_rdpControl.Width}x{_rdpControl.Height}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"ForceHwndRepaint 失败: {ex.Message}");
            }
        }

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject([In] IntPtr hObject);
    }
}
