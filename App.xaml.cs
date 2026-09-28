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
        }

        /// <summary>
        /// 在应用程序由最终用户正常启动时进行调用。
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            MainWindow = new Window();
            MainWindow.Title = "报价查询";

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
