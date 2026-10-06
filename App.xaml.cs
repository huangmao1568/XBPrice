using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace XBPrice
{
    /// <summary>
    /// 提供特定于应用程序的行为，以补充默认的应用程序类。
    /// WinUI 3 版本：不再有 Window.Current 和 Suspending 事件。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 主窗口。WinUI 3 需要在应用内自行持有窗口引用
        /// （拾取器、对话框等需要用它来获取窗口句柄 HWND）。
        /// </summary>
        public static Window MainWindow { get; private set; }

        public App()
        {
            this.InitializeComponent();

            // 全局兜底：把未处理的托管异常写进日志。
            // 原生层的访问冲突（0xc0000005）无法在此捕获，但绝大多数
            // 「点了按钮就崩」其实都是逃逸出 async void 的托管异常，
            // 有这份日志就能直接定位，不必再靠猜。
            this.UnhandledException += OnUnhandledException;
        }

        /// <summary>崩溃日志路径：程序目录下 unhandled.log。</summary>
        private static string CrashLogPath => System.IO.Path.Combine(
            AppContext.BaseDirectory, "unhandled.log");

        /// <summary>
        /// 记录未处理异常。标记为已处理，让程序继续跑——
        /// 一次网络抖动不该让整个程序退出。
        /// </summary>
        private void OnUnhandledException(object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                System.IO.File.AppendAllText(
                    CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}"
                    + new string('-', 60) + Environment.NewLine);
            }
            catch
            {
                // 日志写不了就算了，不能因此再抛一次
            }

            e.Handled = true;
        }

        /// <summary>
        /// 在应用程序由最终用户正常启动时进行调用。
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            MainWindow = new Window();
            MainWindow.Title = "报价查询";

            // 让内容延伸到标题栏区域，去掉系统标题栏下方的那条留白。
            // 开启后由页面内的自定义标题栏（TitleBar 控件）接管拖动与窗口按钮留白。
            MainWindow.ExtendsContentIntoTitleBar = true;

            Frame rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;

            MainWindow.Content = rootFrame;

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), args.Arguments);
            }

            ApplyFluentBackdrop(MainWindow);
            ResizeToFitScreen(MainWindow);
            MainWindow.Activate();
        }

        /// <summary>
        /// 按屏幕工作区调整窗口大小并居中，避免在小屏或虚拟机上超出可视区域。
        /// </summary>
        private static void ResizeToFitScreen(Window window)
        {
            DisplayArea area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
            if (area is null)
            {
                return;
            }

            RectInt32 work = area.WorkArea;
            int width = Math.Min(1360, work.Width - 40);
            int height = Math.Min(860, work.Height - 40);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            window.AppWindow.MoveAndResize(new RectInt32(
                work.X + ((work.Width - width) / 2),
                work.Y + ((work.Height - height) / 2),
                width,
                height));
        }

        /// <summary>
        /// 给窗口加上亚克力材质背景（Windows 10 1809+ 支持）。
        /// 系统不支持时静默跳过，不影响功能。
        /// </summary>
        private static void ApplyFluentBackdrop(Window window)
        {
            try
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
                // 忽略不支持亚克力的系统
            }
        }

        /// <summary>
        /// 导航到特定页失败时调用
        /// </summary>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
