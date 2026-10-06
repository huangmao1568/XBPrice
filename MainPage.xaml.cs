using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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

        /// <summary>
        /// 空状态的文案是否该用「库里没数据」那套说法。
        /// 由「全部日期都已入库、走查库路径展示」时置true，
        /// 真正发起过网络抓取后置false，避免把抓取失败误说成库里没有。
        /// </summary>
        private bool _emptyHintFromLibrary;

        /// <summary>当前缩放比例。</summary>
        private double _zoom = 1.0;

        /// <summary>页面是否已首次加载完成（首次完成后才自动做整页适配）。</summary>
        private bool _firstNavigationDone;

        /// <summary>
        /// 页面当前是否挂在视觉树上。
        ///
        /// WebView2 是原生控件，一旦页面被移出视觉树（切到数据库页再返回时
        /// 会短暂发生），再调用 CoreWebView2 的方法就可能撞上已释放的原生对象，
        /// 直接触发 0xc0000005 访问冲突。所有 WebView2 操作前都必须先看这个标志。
        /// </summary>
        private bool _isLoaded;

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

            // 关键：必须启用页面缓存。
            // 默认情况下 Frame 导航离开会销毁 MainPage，WebView2 随之被释放；
            // 返回时又新建一个页面，但旧的 NavigationCompleted 回调与
            // Task.Delay 续体仍在运行，访问已释放的原生对象即 0xc0000005。
            // 缓存后 MainPage 只构造一次，WebView2 全程存活，问题从根上消失。
            this.NavigationCacheMode = NavigationCacheMode.Required;

            // 给日期一个默认区间：否则 DatePicker 未选择时会返回 DateTimeOffset.MinValue，
            // 拼出 00010101 的非法网址导致服务端报错
            DateTimeOffset today = DateTimeOffset.Now;
            this.startDatePicker.Date = ToPreviousWorkday(today.AddMonths(-DefaultRangeMonths)); // start = 一个月前的工作日
            this.endDatePicker.Date = today;

            // 下拉框的默认选中与预览页面共用同一个索引，避免两处各写各的出现不一致
            int cityIndex = DefaultCityIndex >= 0 && DefaultCityIndex < _cities.Count ? DefaultCityIndex : 0;
            this.cityPicker.SelectedIndex = cityIndex;

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;

            this.webview.NavigationCompleted += this.WebView_NavigationCompleted;
        }

        /// <summary>
        /// 页面挂上视觉树时才设置网页地址。
        ///
        /// 不能放在构造函数里：那时 WebView2 还没进视觉树，
        /// CoreWebView2 未初始化完成，Source 可能被丢弃或引发原生异常。
        /// </summary>
        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            this._isLoaded = true;

            // 已经设过地址就不再重复导航，避免从数据库页返回时网页白白重新加载一遍
            if (this._pendingUrl is null)
            {
                DateTimeOffset today = DateTimeOffset.Now;
                string url = new MyWebsite(_cities[DefaultCityIndex], today).Url;
                this._pendingUrl = url;
                this.webview.Source = new Uri(url);
            }
        }

        /// <summary>
        /// 页面离开视觉树：标记卸载状态，让后续的 WebView2 回调自行短路。
        /// </summary>
        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            this._isLoaded = false;
        }

        /// <summary>最近一次设置的网页地址，用于判断是否需要重新导航。</summary>
        private string _pendingUrl;

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
            // 页面已离开视觉树时不碰 WebView2：此时原生对象可能已释放，
            // 继续访问就是 0xc0000005，而不是可捕获的托管异常。
            if (!_isLoaded)
            {
                return;
            }

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

        /// <summary>
        /// 缩放按钮的统一入口。
        ///
        /// 用 ContinueWith 而非 async void：async void 里未捕获的异常会直接终止进程，
        /// 而缩放失败顶多是没缩放成功，不值得让整个程序崩掉。
        /// </summary>
        private void SetZoomAsync(double value)
        {
            _ = this.SetZoomInternalAsync(value).ContinueWith(
                t => { _ = t.Exception; },
                TaskScheduler.Default);
        }

        /// <summary>把缩放比例应用到网页内容上。</summary>
        private async Task ApplyZoomToPageAsync(double zoom)
        {
            // 三重防护：页面必须在树上、CoreWebView2 必须已初始化、调用期间不能被卸载。
            // 缺任何一个都可能在原生层崩掉。
            if (!_isLoaded || this.webview.CoreWebView2 is null)
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

            CoreWebView2 core = this.webview.CoreWebView2;
            if (core is null)
            {
                return;
            }

            await core.ExecuteScriptAsync(script);
        }

        /// <summary>刷新缩放百分比文本。</summary>
        private void UpdateZoomText()
        {
            // 页面卸载后不要再改控件，否则可能碰到已释放的原生对象
            if (_isLoaded)
            {
                this.zoomText.Text = $"{Math.Round(this._zoom * 100)}%";
            }
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
            // 页面不在树上时直接放弃，绝不触碰 WebView2。
            if (!_isLoaded)
            {
                return;
            }

            CoreWebView2 core = this.webview.CoreWebView2;

            // 注意：这里刻意不调 EnsureCoreWebView2Async。
            // WebView2 在控件已卸载后调用它属于未定义行为，会直接抛原生访问冲突，
            // 而且是无法在 try/catch 里捕获的那种。等它自己就绪即可，
            // 未就绪时 NavigationCompleted 也不会来，本方法没有执行的机会。
            if (core is null)
            {
                return;
            }

            try
            {
                // 先复位，保证量到的是 100% 下的真实内容宽度
                await this.ApplyZoomToPageAsync(1.0);
                await Task.Delay(150);

                // 延迟期间页面可能已被卸载（用户切到数据库页）
                if (!_isLoaded)
                {
                    return;
                }

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

                core = this.webview.CoreWebView2;
                if (core is null)
                {
                    return;
                }

                string raw = await core.ExecuteScriptAsync(measureScript);
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
            catch (Exception)
            {
                // 适配失败不影响使用。这里只更新缩放文本、绝不再去动 WebView2——
                // 原实现在 catch 里 await SetZoomInternalAsync，一旦二次抛出会从
                // async void 逃逸并直接终止进程，那才是真正的崩溃。
                this._zoom = 0.5;
                this.UpdateZoomText();
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

                // 确认存在工作日后才导航，避免把周末或非法日期页面加载进来。
                // 仅在页面挂上视觉树时才导航：否则 WebView2 原生对象可能已释放。
                if (_isLoaded)
                {
                    this._pendingUrl = websites[0].Url;
                    this.webview.Source = new Uri(websites[0].Url);
                }

                string startText = start.ToString("yyyy-MM-dd");
                string endText = end.ToString("yyyy-MM-dd");

                // 判重：库里已有的日期不再向目标网站重复请求，
                // 既省流量与时间，也避免站点把我们当爬虫反复拦。
                // 判定粒度为「天」——某天该城市已有任意一条报价即视为已入库。
                List<string> candidateDates = websites
                    .Select(w => w.Date.ToString("yyyy-MM-dd"))
                    .ToList();

                List<string> existingDates = this._repository.FilterExistingDates(city, candidateDates);

                List<MyWebsite> toFetch = existingDates.Count == 0
                    ? websites
                    : websites
                        .Where(w => !existingDates.Contains(w.Date.ToString("yyyy-MM-dd")))
                        .ToList();

                // 全部日期都已入库，无需任何网络请求
                if (toFetch.Count == 0)
                {
                    this._emptyHintFromLibrary = true;
                    this.LoadExistingIntoView(city, existingDates, websites.Count);
                    this._repository.WriteFetchLog(
                        startText, endText, city, 0, 0, 0, "跳过", $"所选 {websites.Count} 天均已入库");
                    return;
                }

                // 走了真实抓取路径：无论结果如何都不该再说「库里没有」
                this._emptyHintFromLibrary = false;

                // 按行解析整张报价表，并发抓取所选日期范围内的全部工作日。
                // 抓取与解析逻辑见 SteelPriceFetcher，入库见 SteelPriceRepository。
                // 逐日容错：个别日期下载失败只记录日期，不影响其余日期入库。
                FetchOutcome outcome = await SteelPriceFetcher.FetchAsync(toFetch);
                List<SteelPriceRecord> records = outcome.Records;
                this._lastRecords = records;

// 先把「已入库跳过的日期」从库里补进来，再判断是否空。
    //
    // 顺序很关键：早前把判空放在合并之前，导致「本次网络抓的全是节假日、
    // 但库里已有其他日期数据」时直接 return，库里的数据永远显示不出来——
    // 表格空着，提示却说「N 天无报价」，用户完全看不到已有数据。
    // 先合并再看是否为空，任何情况下表格都能覆盖所选区间的全部工作日。
    if (existingDates.Count > 0)
    {
        records = records
            .Concat(this._repository.QueryByCityDates(city, existingDates))
            .OrderBy(r => r.QuoteDate, StringComparer.Ordinal)
            .ToList();
        this._lastRecords = records;
    }

    int fetchedCount = outcome.Records.Count;
    int fromLibrary = records.Count - fetchedCount;

    // 一天都没抓到。这里要区分三种截然不同的情况，不能一律当成错误：
    // 1) 全部是「假期无报价」日 → 页面结构正常、只是没数据，完全不该报警。
    // 2) 下载失败（断网 / 站点不可用）→ 真正的失败，需要提示。
    // 3) 疑似网页结构变化（连报价表都找不到）→ 需要人介入，必须提示。
    if (records.Count == 0)
    {
        bool allNoQuote = outcome.FailedDates.Count == 0 && outcome.SuspectDates.Count == 0;

        if (allNoQuote)
        {
            // 全部是假期无报价：这不是故障，但**必须给一句话**。
            // 之前这里直接静默 return，导致用户点了按钮完全没反应、
            // 不知道是程序卡了还是本来就没数据——静默不等于无感。
            this._repository.WriteFetchLog(
                startText, endText, city, 0, 0, 0, "跳过",
                $"{outcome.NoDataDates.Count} 天无报价（假期）：{string.Join("、", outcome.NoDataDates)}");

            // 屏幕提示保持一句话，具体日期留到日志里
            this.ShowStatus(
                $"所选 {outcome.TotalDays} 天均无报价，多为节假日站点不出报价，可换一段日期",
                InfoBarSeverity.Success);
            return;
        }

        // 这里能走到，说明确有失败或结构异常。
        // 若库里有数据可补，先把数据显示出来，别让用户只看到一条错误提示。
        if (fromLibrary > 0)
        {
            this.BuildRows(records);
        }

        List<string> problems = new List<string>();
        if (outcome.SuspectDates.Count > 0)
        {
            problems.Add($"页面已下载但未找到报价表，网页结构可能已调整（{DescribeDates(outcome.SuspectDates)}）");
        }

        if (outcome.FailedDates.Count > 0)
        {
            problems.Add($"抓取失败 {outcome.FailedDates.Count} 天：{string.Join("、", outcome.FailedDates)}");
        }

        string reason = string.Join("；", problems);

        this._repository.WriteFetchLog(
            startText, endText, city, 0, 0, 0, "失败", reason);

        this.ShowStatus(reason, InfoBarSeverity.Error);
        return;
    }

    // 写入数据库（幂等：重复抓取同一天只刷新价格）
    SaveResult saveResult = this._repository.Save(outcome.Records);

                string status = outcome.IsAllSucceeded ? "成功" : "部分失败";
                List<string> notes = new List<string>();

                // 详细构成写进日志，供日后排查用
                notes.Add($"网络获取 {fetchedCount} 条");

                if (fromLibrary > 0)
                {
                    notes.Add($"数据库补充 {fromLibrary} 条");
                }

                if (existingDates.Count > 0)
                {
                    notes.Add($"{existingDates.Count} 天已入库跳过抓取（{DescribeDates(existingDates)}）");
                }

                // 假期无报价的日子也要记录，否则日后看不出「明明选了这些天却少了几天」
                if (outcome.NoDataDates.Count > 0)
                {
                    notes.Add($"{outcome.NoDataDates.Count} 天无报价已忽略"
                              + $"（{DescribeDates(outcome.NoDataDates)}，多为节假日）");
                }

                if (!outcome.IsAllSucceeded)
                {
                    notes.Add($"{outcome.SucceededDays}/{outcome.TotalDays} 天取到数据");

                    // 下载失败与结构异常要分开讲：前者补抓即可，后者得人去查网站改版
                    if (outcome.FailedDates.Count > 0)
                    {
                        notes.Add($"下载失败：{string.Join("、", outcome.FailedDates)}");
                    }

                    if (outcome.SuspectDates.Count > 0)
                    {
                        notes.Add($"网页结构可能已调整：{DescribeDates(outcome.SuspectDates)}");
                    }
                }

                this._repository.WriteFetchLog(
                    startText,
                    endText,
                    city,
                    saveResult.Total,
                    saveResult.Inserted,
                    saveResult.Updated,
                    status,
                    notes.Count > 0 ? string.Join("；", notes) : null);

                this.BuildRows(records);

// 屏幕提示以「数据来源」开头——用户最关心的是数据哪来的、有多少条，
    // 而节假日/已入库这类过程信息不该抢主位。
    string summary = $"共 {records.Count} 条";

    if (fromLibrary > 0)
    {
        summary += $"（数据库 {fromLibrary} 条 + 网络 {fetchedCount} 条）";
    }

    if (saveResult.Inserted > 0 || saveResult.Updated > 0)
    {
        summary += $"，入库新增 {saveResult.Inserted} 条、更新 {saveResult.Updated} 条";
    }

    // 过程信息只给一句概括，不带日期区间（详见 FetchLog）
    List<string> brief = new List<string>();

    if (fromLibrary > 0)
    {
        brief.Add($"{existingDates.Count} 天已入库跳过抓取");
    }

    if (outcome.NoDataDates.Count > 0)
    {
        brief.Add($"{outcome.NoDataDates.Count} 天节假日无报价");
    }

    if (brief.Count > 0)
    {
        summary += "；" + string.Join("，", brief);
    }

    // 真正失败时才追加天数与原因，且用警告级
    if (!outcome.IsAllSucceeded)
    {
        summary += $"；{outcome.SucceededDays}/{outcome.TotalDays} 天成功";

        if (outcome.FailedDates.Count > 0)
        {
            summary += "，部分抓取失败";
        }

        if (outcome.SuspectDates.Count > 0)
        {
            summary += "，疑似网页结构变化";
        }

        this.ShowStatus(summary, InfoBarSeverity.Warning);
    }
    else
    {
        // 全部正常（含节假日与已入库跳过）用成功级，避免用户误以为出错
        this.ShowStatus(summary, InfoBarSeverity.Success);
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
        /// 把「库里已有、因此跳过抓取」的日期直接读出来填进表格。
        ///
        /// 用于所选区间的全部工作日都已入库的情况：此时一次网络请求都不发，
        /// 但用户按下按钮仍应看到完整报价表，而不是空白或错误提示。
        /// </summary>
        /// <param name="city">城市拼音。</param>
        /// <param name="dates">已入库的日期集合（yyyy-MM-dd）。</param>
        /// <param name="totalDays">所选区间的工作日总数，用于提示文案。</param>
        private void LoadExistingIntoView(string city, List<string> dates, int totalDays)
        {
            List<SteelPriceRecord> records =
                this._repository.QueryByCityDates(city, dates);

            this._lastRecords = records;
            this.BuildRows(records);

            // 理论上查不到任何行：FilterExistingDates 与 QueryByCityDates 的筛选口径
            // 完全一致（都是 City + QuoteDate IN 列表），所以「判定为已入库却查不到明细」
            // 正常不会发生。真发生只能是城市名不一致之类的问题，
            // 因此作为兜底分支明确说清原因，而不是含糊地说「已入库」。
            string message = records.Count > 0
                ? $"所选 {totalDays} 天已全部入库，直接显示库中数据（共 {records.Count} 条），本次未发起网络请求"
                : $"所选 {totalDays} 天已入库，但没能读出对应的报价明细，请检查城市拼音是否与抓取时一致";

            this.ShowStatus(message, InfoBarSeverity.Success);
        }

        /// <summary>
        /// 把日期列表压缩成可读文本：连续日期折叠成区间，断开处用顿号分隔。
        /// 例：2026-09-28、2026-09-29、2026-09-30 → 「2026-09-28 ~ 2026-09-30」。
        /// 跳过的日期可能很多，全列出来会把提示条撑爆，因此必须折叠。
        /// </summary>
        private static string DescribeDates(List<string> dates)
        {
            if (dates is null || dates.Count == 0)
            {
                return string.Empty;
            }

            List<DateTime> sorted = dates
                .Select(d => DateTime.TryParse(d, out DateTime dt) ? dt : (DateTime?)null)
                .Where(d => d.HasValue)
                .Select(d => d.Value)
                .OrderBy(d => d)
                .ToList();

            if (sorted.Count == 0)
            {
                return string.Join("、", dates);
            }

            List<string> parts = new List<string>();
            DateTime runStart = sorted[0];
            DateTime runEnd = sorted[0];

            void Flush()
            {
                parts.Add(runStart == runEnd
                    ? $"{runStart:yyyy-MM-dd}"
                    : $"{runStart:yyyy-MM-dd} ~ {runEnd:yyyy-MM-dd}");
            }

            for (int i = 1; i < sorted.Count; i++)
            {
                // 相邻一天（含周末）算同一段
                if ((sorted[i] - runEnd).Days <= 3)
                {
                    runEnd = sorted[i];
                }
                else
                {
                    Flush();
                    runStart = sorted[i];
                    runEnd = sorted[i];
                }
            }

            Flush();
            return string.Join("、", parts);
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

        /// <summary>
        /// 刷新空状态提示。
        ///
        /// 文案要贴合实际情况：同样「表格没有行」，可能是压根还没抓过数据，
        /// 也可能是所选日期在库里根本没有报价（比如整段都是假期）。
        /// 后一种情况若还提示「点上方获取数据开始抓取」会误导用户——
        /// 数据已经在库里了，再点一次也抓不出来。
        /// </summary>
        private void UpdateEmptyHint()
        {
            bool empty = this.Model.Count == 0;
            this.emptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            if (!empty)
            {
                return;
            }

            if (this._emptyHintFromLibrary)
            {
                // 走查库路径却查不到行：说明这些日期在库里没有报价明细，
                // 多为节假日无报价，再点「获取数据」也抓不出来，得换日期或去数据库页看
                this.emptyTitleText.Text = "所选日期暂无报价";
                this.emptyHintText.Text =
                    "这些日期在数据库里没有报价记录（多为节假日无报价）。可换一段日期，或点「数据库」查看已入库数据";
            }
            else
            {
                this.emptyTitleText.Text = "暂无数据";
                this.emptyHintText.Text = "点上方「获取数据」开始抓取所选日期范围的报价";
            }
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
