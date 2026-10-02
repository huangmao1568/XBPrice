using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace XBPrice
{
    /// <summary>
    /// 报价查询主页面。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly List<string> _cities = new List<string>
        {
            "shanghai", "beijing", "chengdo", "nanjing", "hangzhou"
        };

        /// <summary>开始日期默认往前推的月数。</summary>
        private const int DefaultRangeMonths = 1;

        /// <summary>默认选中的城市下标（对应 _cities，2 = "chengdo"）。</summary>
        private const int DefaultCityIndex = 2;

        /// <summary>缩放下限 / 上限。</summary>
        private const double MinZoom = 0.25;
        private const double MaxZoom = 2.0;

        /// <summary>缩放步进。</summary>
        private const double ZoomStep = 0.1;

        /// <summary>最近一次查询结果对应的 CSV 文本。</summary>
        private string _csv;

        /// <summary>当前缩放比例。</summary>
        private double _zoom = 1.0;

        /// <summary>页面是否已首次加载完成（首次完成后才自动做整页适配）。</summary>
        private bool _firstNavigationDone;

        /// <summary>数据仓储。整个页面共用一个实例，避免反复建库。</summary>
        private readonly SteelPriceRepository _repository = new SteelPriceRepository();

        /// <summary>最近一次抓取结果（结构化记录），导出时直接复用。</summary>
        private List<SteelPriceRecord> _lastRecords = new List<SteelPriceRecord>();

        // ============================================================
        // 涨跌色：从主题令牌解析，随系统浅色/深色主题自动适配
        //
        // 之前用静态 SolidColorBrush 写死浅色主题下的深红/深绿，
        // 在深色主题下对比度不足（深红压在 #202020 上几乎看不清）。
        // 现在改为从 App.xaml 合并的 ThemeTokens.xaml 中按主题取刷子：
        // 浅色用深红/深绿，深色用亮红/亮绿，高对比度交由系统色。
        // ============================================================

        /// <summary>涨跌色：涨红。按当前主题解析，取不到时回退到内置危险色。</summary>
        private static SolidColorBrush UpBrush => ResolveTrendBrush("AppTrendUpBrush", "#C42B1C");

        /// <summary>涨跌色：跌绿。按当前主题解析，取不到时回退到内置成功色。</summary>
        private static SolidColorBrush DownBrush => ResolveTrendBrush("AppTrendDownBrush", "#0F7B0F");

        /// <summary>涨跌色：持平 / 无数据（中性灰）。</summary>
        private static SolidColorBrush FlatBrush => ResolveTrendBrush("AppTrendFlatBrush", "#888888");

        /// <summary>
        /// 按当前主题从应用资源里取涨跌刷子。
        ///
        /// 说明：ThemeResource 标记扩展只在 XAML 中可用，代码里需要手动
        /// 走一遍主题字典查找。资源缺失时用 <paramref name="fallbackHex"/>
        /// 兜底，保证任何情况下都有颜色可用、不会抛异常。
        /// </summary>
        /// <param name="key">令牌键名（如 AppTrendUpBrush）。</param>
        /// <param name="fallbackHex">兜底色（十六进制，如 #C42B1C）。</param>
        private static SolidColorBrush ResolveTrendBrush(string key, string fallbackHex)
        {
            try
            {
                object resource = Application.Current?.Resources?[key];
                if (resource is SolidColorBrush brush)
                {
                    return brush;
                }
            }
            catch
            {
                // 资源查找失败时走下面的兜底
            }

            return new SolidColorBrush(ParseHex(fallbackHex));
        }

        /// <summary>把 #RRGGBB / #AARRGGBB 解析成 Color（不带 Alpha 时按不透明处理）。</summary>
        private static Color ParseHex(string hex)
        {
            // 解析失败时的兜底色：中性灰
            Color gray = Color.FromArgb(255, 0x88, 0x88, 0x88);

            if (string.IsNullOrEmpty(hex))
            {
                return gray;
            }

            string v = hex.TrimStart('#');

            try
            {
                if (v.Length == 6)
                {
                    return Color.FromArgb(
                        255,
                        Convert.ToByte(v.Substring(0, 2), 16),
                        Convert.ToByte(v.Substring(2, 2), 16),
                        Convert.ToByte(v.Substring(4, 2), 16));
                }

                if (v.Length == 8)
                {
                    return Color.FromArgb(
                        Convert.ToByte(v.Substring(0, 2), 16),
                        Convert.ToByte(v.Substring(2, 2), 16),
                        Convert.ToByte(v.Substring(4, 2), 16),
                        Convert.ToByte(v.Substring(6, 2), 16));
                }
            }
            catch
            {
                // 非法十六进制串，落到兜底色
            }

            return gray;
        }

        public MainPage()
        {
            this.InitializeComponent();

            // 给日期一个默认区间：否则 DatePicker 未选择时会返回 DateTimeOffset.MinValue，
            // 拼出 00010101 的非法网址导致服务端报错
            DateTimeOffset today = DateTimeOffset.Now;
            this.startDatePicker.Date = ToPreviousWorkday(today.AddMonths(-DefaultRangeMonths)); // start = 一个月前的工作日
            this.endDatePicker.Date = today;

            // 下拉框的默认选中与预览页面共用同一个索引，避免两处各写各的出现不一致
            int cityIndex = DefaultCityIndex >= 0 && DefaultCityIndex < _cities.Count ? DefaultCityIndex : 0;
            this.cityPicker.SelectedIndex = cityIndex;

            this.webview.Source = new Uri(new MyWebsite(_cities[cityIndex], today).Url);

            this.webview.NavigationCompleted += this.WebView_NavigationCompleted;
        }

        /// <summary>可选城市列表（供 ComboBox 绑定）。</summary>
        public IReadOnlyList<string> Cities => _cities;

        /// <summary>查询结果（供表格绑定）。</summary>
        public ObservableCollection<QuoteRow> Model { get; } = new ObservableCollection<QuoteRow>();

        // ============================================================
        // 网页预览：缩放控制
        // ============================================================

        /// <summary>
        /// 页面加载完成后自动做一次“整页适配”。
        /// 只在首次导航完成后执行，之后用户手动调过缩放就不再干预。
        /// </summary>
        private async void WebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                return;
            }

            if (!this._firstNavigationDone)
            {
                this._firstNavigationDone = true;
                await this.ApplyFitAsync();
            }
        }

        /// <summary>缩小。</summary>
        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            this.SetZoomAsync(this._zoom - ZoomStep);
        }

        /// <summary>放大。</summary>
        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            this.SetZoomAsync(this._zoom + ZoomStep);
        }

        /// <summary>整页适配：把页面缩到宽度完全放入预览区。</summary>
        private async void ZoomFitButton_Click(object sender, RoutedEventArgs e)
        {
            await this.ApplyFitAsync();
        }

        /// <summary>缩放按钮的统一入口（按钮事件无法直接 await，这里 fire-and-forget）。</summary>
        private async void SetZoomAsync(double value)
        {
            await this.SetZoomInternalAsync(value);
        }

        /// <summary>把缩放比例应用到网页内容上。</summary>
        private async Task ApplyZoomToPageAsync(double zoom)
        {
            if (this.webview.CoreWebView2 is null)
            {
                return;
            }

            // transform-origin 设成左上角，缩放后内容从左上开始铺开，
            // 再把 width 按比例放大，保证内容横向铺满整个预览区而不留白边。
            string script = $@"
(function () {{
    var z = {zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)};
    var body = document.body;
    if (!body) {{ return; }}
    body.style.transformOrigin = 'top left';
    body.style.transform = 'scale(' + z + ')';
    body.style.width = (100 / z) + '%';
}})();";

            await this.webview.CoreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>刷新缩放百分比文本。</summary>
        private void UpdateZoomText()
        {
            this.zoomText.Text = $"{Math.Round(this._zoom * 100)}%";
        }

        /// <summary>
        /// 把网页缩放到整页宽度可见。
        ///
        /// 做法：先复位到 100%，量出页面内容的自然宽度，
        /// 再用「预览区宽度 / 内容宽度」算出合适的缩放比例。
        /// 取不到宽度时退回到 0.5。
        /// </summary>
        private async Task ApplyFitAsync()
        {
            try
            {
                if (this.webview.CoreWebView2 is null)
                {
                    await this.webview.EnsureCoreWebView2Async();
                }

                // 先复位，保证量到的是 100% 下的真实内容宽度
                await this.ApplyZoomToPageAsync(1.0);
                await Task.Delay(150);

                double viewportWidth = this.webview.ActualWidth;
                if (viewportWidth <= 1)
                {
                    viewportWidth = 600; // 尚未布局完成时的兜底宽度
                }

                // 报价页表格较宽，取 body 与根元素中较宽者作为内容宽度
                const string measureScript = @"
(function () {
    var b = document.body, e = document.documentElement;
    var w = Math.max(
        b ? b.scrollWidth : 0,
        e ? e.scrollWidth : 0,
        b ? b.offsetWidth : 0,
        e ? e.offsetWidth : 0
    );
    return String(w || 0);
})();";

                string raw = await this.webview.CoreWebView2.ExecuteScriptAsync(measureScript);
                double contentWidth = 0;
                if (double.TryParse(raw?.Trim('"'), out double parsed) && parsed > 0)
                {
                    contentWidth = parsed;
                }

                double target;
                if (contentWidth <= 0)
                {
                    target = 0.5; // 取不到宽度时的保守值
                }
                else
                {
                    target = viewportWidth / contentWidth;
                    // 内容本来就窄时不要放大，保持 100% 即可
                    if (target > 1.0)
                    {
                        target = 1.0;
                    }
                }

                await this.SetZoomInternalAsync(target);
            }
            catch
            {
                // 适配失败不影响使用，退回一个保守缩放
                await this.SetZoomInternalAsync(0.5);
            }
        }

        /// <summary>内部使用的异步缩放设置（供适配流程调用）。</summary>
        private async Task SetZoomInternalAsync(double value)
        {
            double z = Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 2);
            this._zoom = z;
            this.UpdateZoomText();
            await this.ApplyZoomToPageAsync(z);
        }

        // ============================================================
        // 抓取与入库
        // ============================================================

        /// <summary>抓取数据。</summary>
        private async void FetchButton_Click(object sender, RoutedEventArgs e)
        {
            string city = this.GetSelectedCity();
            if (string.IsNullOrWhiteSpace(city))
            {
                this.ShowStatus("请输入或选择城市", InfoBarSeverity.Warning);
                return;
            }

            this.statusBar.IsOpen = false;
            this.SetBusy(true);

            try
            {
                DateTimeOffset start = GetActualDate(this.startDatePicker);
                DateTimeOffset end = GetActualDate(this.endDatePicker);

                List<MyWebsite> websites = MyWebsite.GetWebsites(city, start, end);
                if (websites.Count == 0)
                {
                    this.ShowStatus("所选日期范围内没有工作日", InfoBarSeverity.Warning);
                    return;
                }

                // 确认存在工作日后才导航，避免把周末或非法日期页面加载进来
                this.webview.Source = new Uri(websites[0].Url);

                // 按行解析整张报价表，并发抓取所选日期范围内的全部工作日。
                // 抓取与解析逻辑见 SteelPriceFetcher，入库见 SteelPriceRepository。
                // 逐日容错：个别日期下载失败只记录日期，不影响其余日期入库。
                FetchOutcome outcome = await SteelPriceFetcher.FetchAsync(websites);
                List<SteelPriceRecord> records = outcome.Records;
                this._lastRecords = records;

                string startText = start.ToString("yyyy-MM-dd");
                string endText = end.ToString("yyyy-MM-dd");

                // 一天都没抓到：要么断网 / 站点不可用，要么网页结构变了导致解析为空。
                // 两种情况都记失败日志后直接返回，避免把空批次当成功写进日志。
                if (records.Count == 0)
                {
                    string reason = outcome.IsAllSucceeded
                        ? $"页面已下载但未解析到数据，网页结构可能已调整（共 {outcome.TotalDays} 天）"
                        : $"全部 {outcome.TotalDays} 天抓取失败：{string.Join("、", outcome.FailedDates)}";

                    this._repository.WriteFetchLog(
                        startText, endText, city, 0, 0, 0, "失败", reason);

                    this.ShowStatus(reason, InfoBarSeverity.Error);
                    return;
                }

                // 写入数据库（幂等：重复抓取同一天只刷新价格）
                SaveResult saveResult = this._repository.Save(records);
                this._repository.WriteFetchLog(
                    startText,
                    endText,
                    city,
                    saveResult.Total,
                    saveResult.Inserted,
                    saveResult.Updated,
                    outcome.IsAllSucceeded ? "成功" : "部分失败",
                    outcome.IsAllSucceeded ? null : $"失败日期：{string.Join("、", outcome.FailedDates)}");

                this.BuildRows(records);

                string summary =
                    $"已获取 {records.Count} 条记录，入库新增 {saveResult.Inserted} 条、更新 {saveResult.Updated} 条";
                if (outcome.IsAllSucceeded)
                {
                    this.ShowStatus(summary, InfoBarSeverity.Success);
                }
                else
                {
                    this.ShowStatus(
                        $"{summary}；{outcome.SucceededDays}/{outcome.TotalDays} 天成功，" +
                        $"失败日期：{string.Join("、", outcome.FailedDates)}",
                        InfoBarSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                this.ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                this.SetBusy(false);
                this.UpdateEmptyHint();
            }
        }

        /// <summary>
        /// 把抓取结果整理成表格行，并同时生成 CSV 文本。
        ///
        /// 涨跌按「同品名 + 同规格 + 同牌号」在所选范围内按日期排序后
        /// 与前一条比较得出，颜色遵循国内行情惯例：涨红、跌绿。
        /// </summary>
        private void BuildRows(List<SteelPriceRecord> records)
        {
            this.Model.Clear();

            StringBuilder csv = new StringBuilder();

            // 按日期升序排一遍，便于计算同规格的日间涨跌
            List<SteelPriceRecord> ordered = records
                .OrderBy(r => r.QuoteDate, StringComparer.Ordinal)
                .ThenBy(r => r.ProductName, StringComparer.Ordinal)
                .ThenBy(r => r.Spec, StringComparer.Ordinal)
                .ThenBy(r => r.Grade, StringComparer.Ordinal)
                .ToList();

            // 上一条同规格价格：键 = 品名|规格|牌号
            Dictionary<string, double> lastPrice = new Dictionary<string, double>();

            foreach (SteelPriceRecord record in ordered)
            {
                string key = $"{record.ProductName}\u0001{record.Spec}\u0001{record.Grade}";
                double? diff = null;
                if (lastPrice.TryGetValue(key, out double prev))
                {
                    diff = Math.Round(record.Price - prev, 2);
                }

                lastPrice[key] = record.Price;

                // 表格按日期倒序展示（最新在最上），逐行插入到最前面
                this.Model.Insert(0, new QuoteRow
                {
                    DateText = ShortDate(record.QuoteDate),
                    NameText = $"{record.ProductName} {record.Spec}",
                    Grade = record.Grade,
                    PriceText = record.Price.ToString("N2"),
                    TrendText = FormatTrend(diff),
                    TrendBrush = TrendColor(diff)
                });
            }

            // CSV 保持日期升序，便于在 Excel 里按时间阅读
            foreach (SteelPriceRecord record in ordered)
            {
                csv.Append(record.QuoteDate).Append(',')
                   .Append(record.City).Append(',')
                   .Append(record.ProductName).Append(',')
                   .Append(record.Spec).Append(',')
                   .Append(record.Grade).Append(',')
                   .Append(record.Price.ToString("0.##")).Append('\n');
            }

            this._csv = csv.ToString();
            this.rowCountText.Text = $"共 {this.Model.Count} 条";
        }

        /// <summary>把 yyyy-MM-dd 缩成 MM-dd。</summary>
        private static string ShortDate(string quoteDate)
        {
            if (!string.IsNullOrEmpty(quoteDate) && quoteDate.Length >= 10)
            {
                return quoteDate.Substring(5);
            }

            return quoteDate ?? string.Empty;
        }

        /// <summary>涨跌文本：正数带 +，无数据用破折号。</summary>
        private static string FormatTrend(double? diff)
        {
            if (diff is null)
            {
                return "—";
            }

            double d = diff.Value;
            if (Math.Abs(d) < 0.005)
            {
                return "0";
            }

            return d > 0 ? $"+{d:N0}" : $"{d:N0}";
        }

        /// <summary>涨跌颜色：涨红、跌绿、持平灰。</summary>
        private static SolidColorBrush TrendColor(double? diff)
        {
            if (diff is null || Math.Abs(diff.Value) < 0.005)
            {
                return FlatBrush;
            }

            return diff.Value > 0 ? UpBrush : DownBrush;
        }

        /// <summary>打开数据库页（月均价格看板）。</summary>
        private void OpenDatabaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame is not null)
            {
                this.Frame.Navigate(typeof(DatabasePage));
            }
        }

        /// <summary>把结果导出为 CSV 文件。</summary>
        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            FileSavePicker picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "报价数据"
            };
            picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });

            // 免打包应用必须为拾取器指定窗口句柄 (HWND)，否则调用会失败
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            try
            {
                // Excel 按本地编码解析 CSV，必须写入 UTF-8 BOM 才不会乱码
                byte[] bom = Encoding.UTF8.GetPreamble();
                // 首行写表头，字段顺序与数据库一致，便于直接核对入库结果
                string content = "报价日期,城市,品名,规格型号,牌号,价格(元/吨)\n"
                                 + (this._csv ?? string.Empty);
                byte[] body = Encoding.UTF8.GetBytes(content);
                byte[] bytes = new byte[bom.Length + body.Length];
                Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
                Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
                await FileIO.WriteBytesAsync(file, bytes);

                this.ShowStatus($"已导出到 {file.Name}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                this.ShowStatus("导出失败：" + ex.Message, InfoBarSeverity.Error);
            }
        }

        /// <summary>在“选择城市”和“手动输入拼音”之间切换。</summary>
        private void InputModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool manual = this.inputModeToggle.IsOn;
            this.cityInput.Text = string.Empty;
            this.cityInput.IsEnabled = manual;
            this.cityPicker.IsEnabled = !manual;
        }

        /// <summary>DatePicker 未选择日期时 Date 会返回 DateTimeOffset.MinValue，这里兜底成今天。</summary>
        private static DateTimeOffset ToPreviousWorkday(DateTimeOffset d)
        {
            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
            {
                d = d.AddDays(-1);
            }
            return d;
        }

        private static DateTimeOffset GetActualDate(DatePicker picker)
        {
            return picker.Date == DateTimeOffset.MinValue ? DateTimeOffset.Now : picker.Date;
        }

        private string GetSelectedCity()
        {
            string city = this.inputModeToggle.IsOn
                ? this.cityInput.Text
                : this.cityPicker.SelectedItem as string;

            return city?.Trim();
        }

        /// <summary>忙碌时禁用所有输入，避免重复点击。</summary>
        private void SetBusy(bool busy)
        {
            this.ring.IsActive = busy;
            this.ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            this.inputModeToggle.IsEnabled = !busy;
            this.cityInput.IsEnabled = !busy && this.inputModeToggle.IsOn;
            this.cityPicker.IsEnabled = !busy && !this.inputModeToggle.IsOn;
            this.startDatePicker.IsEnabled = !busy;
            this.endDatePicker.IsEnabled = !busy;
            this.fetchButton.IsEnabled = !busy;
            this.openDatabaseButton.IsEnabled = !busy;
            this.exportButton.IsEnabled = !busy && this.Model.Count > 0;
        }

        private void UpdateEmptyHint()
        {
            this.emptyPanel.Visibility = this.Model.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>用 InfoBar 展示状态，替代会阻塞界面的弹窗。</summary>
        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            this.statusBar.Severity = severity;
            this.statusBar.Message = message;
            this.statusBar.IsOpen = true;
        }
    }
}
