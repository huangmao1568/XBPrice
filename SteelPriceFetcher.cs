using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XBPrice
{
    /// <summary>
    /// 报价网址：由城市与日期生成。
    /// </summary>
    public class MyWebsite
    {
        /// <summary>报价页网址模板：{0} 城市拼音，{1} 日期 yyyyMMdd。</summary>
        private const string UrlTemplate =
            "http://{0}.steelx2.com/city/Quotation/quotation/1/{1}/index.html";

        /// <summary>可选城市（拼音标识）。</summary>
        public static readonly IReadOnlyList<string> Cities =
            new[] { "shanghai", "beijing", "chengdo", "nanjing", "hangzhou" };

        public MyWebsite(string city, DateTimeOffset time)
        {
            this.City = city;
            this.Date = time;
            this.DateText = time.ToString("yyyyMMdd");
        }

        /// <summary>城市拼音。</summary>
        public string City { get; }

        /// <summary>报价日。</summary>
        public DateTimeOffset Date { get; }

        /// <summary>日期文本，例如 20160906。</summary>
        public string DateText { get; }

        /// <summary>完整请求地址。</summary>
        public string Url => string.Format(UrlTemplate, this.City, this.DateText);

        public override string ToString() => this.Url;

        /// <summary>
        /// 生成日期区间内的全部工作日（周一至周五）。
        /// </summary>
        public static List<MyWebsite> GetWebsites(string city, DateTimeOffset from, DateTimeOffset to)
        {
            DateTime start = from.Date;
            DateTime end = to.Date;

            if (end < start)
            {
                throw new ArgumentException("开始日期不能大于截至日期");
            }

            List<MyWebsite> result = new List<MyWebsite>();
            for (int i = 0; i <= (end - start).Days; i++)
            {
                DateTime date = start.AddDays(i);
                if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday)
                {
                    result.Add(new MyWebsite(city, date));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 页面解析结果的分类。
    ///
    /// 为什么要区分：报价站在法定假期（调休上班但不出报价）仍会返回页面，
    /// 只是报价表里只有表头没有数据行。这种情况属于**正常现象**，
    /// 不该和「网页结构改了导致解析失效」一样弹错误告警。
    /// </summary>
    public enum PageParseStatus
    {
        /// <summary>解析出了至少一条报价记录。</summary>
        HasData = 0,

        /// <summary>页面正常返回、能定位到报价表与表头，但表内无数据行——通常是假期无报价。</summary>
        NoQuoteTableData = 1,

        /// <summary>页面里连含「品名」的报价表都找不到——网页结构可能已调整，需要人介入。</summary>
        TableNotFound = 2,

        /// <summary>页面内容为空。</summary>
        EmptyPage = 3,
    }

    /// <summary>
    /// 一次抓取任务的汇总结果。
    ///
    /// 设计意图：旧实现直接把全部任务交给 <c>Task.WhenAll</c> 聚合，任何一天失败都会让整批抛异常，
    /// 已经抓到的数据一并作废。改成逐日容错后，成功的天照常入库，
    /// 失败的天只记录日期交给界面提示，用户补抓那几天即可。
    /// </summary>
    public sealed class FetchOutcome
    {
        /// <summary>成功抓取并解析出的全部报价记录。</summary>
        public List<SteelPriceRecord> Records { get; } = new List<SteelPriceRecord>();

        /// <summary>抓取失败的日期（yyyy-MM-dd）。为空表示全部成功。</summary>
        public List<string> FailedDates { get; } = new List<string>();

        /// <summary>
        /// 页面正常但无报价的日期（yyyy-MM-dd），通常是法定假期。
        /// 这类日期不视为失败，直接忽略即可，因此不计入 <see cref="FailedDates"/>。
        /// </summary>
        public List<string> NoDataDates { get; } = new List<string>();

        /// <summary>
        /// 疑似网页结构变化（连报价表都找不到）的日期（yyyy-MM-dd）。
        /// 这类情况需要人介入，因此按失败处理并提示。
        /// </summary>
        public List<string> SuspectDates { get; } = new List<string>();

        /// <summary>
        /// 本次任务涉及的工作日总数。
        /// </summary>
        public int TotalDays { get; set; }

        /// <summary>
        /// 实际取到报价的天数——不含失败、结构异常与假期无报价的日子。
        /// 用于界面提示「N 天抓取成功」，所以必须把三类排除的日子都算掉。
        /// </summary>
        public int SucceededDays => this.TotalDays
                                   - this.FailedDates.Count
                                   - this.SuspectDates.Count
                                   - this.NoDataDates.Count;

        /// <summary>
        /// 是否全部正常——含「下载成功但页面无数据」的假期日在内，都算正常。
        /// 只有真正的下载失败与结构异常才算不正常。
        /// </summary>
        public bool IsAllSucceeded => this.FailedDates.Count == 0 && this.SuspectDates.Count == 0;
    }

    /// <summary>
    /// 钢价抓取器：下载报价页并解析为结构化记录。
    ///
    /// 【解析要点】采用按 &lt;tr&gt; 逐行的方式。原 XBPrice 程序曾用
    /// “提取全部 &lt;td&gt; 后按固定步长 4 取值”的做法，但网页每个品类的
    /// 第一条规格会多出一列“优质品牌推荐”，行内列数 5/4 混排，
    /// 导致从第二条起整列错位。按行解析并按行内实际列数判断，彻底修正该问题。
    ///
    /// 【性能要点】并发上限、连接池、超时、失败重试四者共同决定实际吞吐：
    /// 调大并发能线性提高吞吐，但必须配套「连接池上限对齐并发数」，否则请求会
    /// 排队等连接，并发数形同虚设；再配合「连接定期重建」与「瞬时故障重试」，
    /// 才能真正把吞吐跑满，而不是把时间耗在空转重来上。
    /// </summary>
    public static partial class SteelPriceFetcher
    {
        /// <summary>正文锚点，锚点之后是网站页脚，需截断。</summary>
        private const string BodyAnchor = "总机服务";

        /// <summary>价格合法区间（元/吨），超出视为解析异常。</summary>
        private const double PriceMin = 100d;
        private const double PriceMax = 100000d;

        /// <summary>
        /// 并发下载上限。原为 4，实测压测后定为 8。
        ///
        /// 实测（同一城市连续工作日页面，独立进程并发）：
        /// 并发 4 → 8.2 页/秒；并发 8 → 14.3 页/秒；并发 16 → 15.4 页/秒，
        /// 且单页耗时从 0.56s 涨到 1.04s。可见 8 之后收益极小、单页成本翻倍，
        /// 再往上只是拿对端站点的压力换微乎其微的速度，容易触发限流或 UA 封禁。
        /// 注：本程序复用连接（见 Client 配置），实际吞吐会略高于上述进程级并发实测值。
        /// </summary>
        private const int MaxConcurrency = 8;

        /// <summary>单页最大尝试次数（含首次）。</summary>
        private const int MaxAttempts = 3;

        /// <summary>重试基础退避毫秒数（按 3 倍递增：400 / 1200）。</summary>
        private const int RetryBaseDelayMs = 400;

        /// <summary>伪装的浏览器 UA：部分站点对无 UA 请求做拦截。</summary>
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        /// <summary>
        /// 全程序共用一个 HttpClient（避免反复创建连接池、避免端口耗尽）。
        ///
        /// 关键三项配置：
        /// 1) <c>MaxConnectionsPerServer</c> 与并发上限对齐 —— 默认虽为无限，
        ///    但显式对齐可保证 8 个请求各自持有独立连接，不会互相排队；
        /// 2) <c>PooledConnectionLifetime</c> 定期重建连接 —— 长连接被服务端
        ///    悄悄掐断时，复用旧连接会直接失败，重建可规避「半开连接」；
        /// 3) <c>AutomaticDecompression</c> 开启压缩 —— 报价页约 53KB，
        ///    gzip 后通常只剩几 KB，传输耗时明显下降。
        /// </summary>
        private static readonly HttpClient Client = CreateClient();

        /// <summary>构建带连接池策略的共享 HttpClient。</summary>
        private static HttpClient CreateClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = MaxConcurrency,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            HttpClient client = new HttpClient(handler)
            {
                // 超时从 30s 收紧到 20s：尽快判定失败并交给重试，避免慢请求长时间占着并发名额
                Timeout = TimeSpan.FromSeconds(20)
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            return client;
        }

        // ------------------------------------------------------------
        // 源码生成正则（GeneratedRegex）：编译期直接生成匹配代码，
        // 没有运行时正则解析与 JIT 编译开销，反复调用时比 RegexOptions.Compiled 更快。
        // ------------------------------------------------------------

        /// <summary>匹配整行 &lt;tr&gt;（单行模式，允许跨行）。</summary>
        [GeneratedRegex("<tr.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex RowRegex();

        /// <summary>匹配行内单元格 &lt;td&gt; / &lt;th&gt;。</summary>
        [GeneratedRegex("<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex CellRegex();

        /// <summary>匹配整张 &lt;table&gt;。</summary>
        [GeneratedRegex("<table.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex TableRegex();

        /// <summary>匹配任意 HTML 标签。</summary>
        [GeneratedRegex("<[^>]+>")]
        private static partial Regex TagRegex();

        /// <summary>匹配连续空白字符（用于压缩文本）。</summary>
        [GeneratedRegex(@"\s+")]
        private static partial Regex SpaceRegex();

        /// <summary>匹配「φ数字*数字」形式的规格（直径 * 定尺米数）。</summary>
        [GeneratedRegex(@"^(φ\s*\d+(?:\.\d+)?)\s*[*×]\s*(\d+(?:\.\d+)?)$")]
        private static partial Regex SpecMeasureRegex();

        /// <summary>匹配价格文本中的数字（支持千分位）。</summary>
        [GeneratedRegex(@"(\d[\d,]*\.?\d*)")]
        private static partial Regex PriceNumberRegex();

        /// <summary>
        /// 抓取指定城市多日的数据，返回成功记录与失败日期（并发下载 + 逐日容错）。
        /// </summary>
        /// <param name="websites">要抓取的网址集合（每个工作日一条）。</param>
        public static async Task<FetchOutcome> FetchAsync(IReadOnlyList<MyWebsite> websites)
        {
            if (websites is null || websites.Count == 0)
            {
                throw new InvalidOperationException("网址错误");
            }

            FetchOutcome outcome = new FetchOutcome { TotalDays = websites.Count };

            using (SemaphoreSlim gate = new SemaphoreSlim(MaxConcurrency))
            {
                Task<DayFetchResult>[] tasks = new Task<DayFetchResult>[websites.Count];

                for (int i = 0; i < websites.Count; i++)
                {
                    tasks[i] = FetchDayAsync(gate, websites[i]);
                }

                DayFetchResult[] groups = await Task.WhenAll(tasks).ConfigureAwait(false);

                foreach (DayFetchResult day in groups)
                {
                    string dateText = day.Website.Date.ToString("yyyy-MM-dd");

                    switch (day.Status)
                    {
                        case DayFetchStatus.Ok:
                            outcome.Records.AddRange(day.Rows);
                            break;

                        // 页面正常返回但表内无数据：法定假期无报价，属正常现象，静默忽略
                        case DayFetchStatus.NoQuoteData:
                            outcome.NoDataDates.Add(dateText);
                            break;

                        // 连报价表都定位不到：网页结构可能变了，需要人介入
                        case DayFetchStatus.ParseSuspect:
                            outcome.SuspectDates.Add(dateText);
                            break;

                        case DayFetchStatus.Failed:
                            outcome.FailedDates.Add(dateText);
                            break;
                    }
                }
            }

            // 失败日期按时间先后排序，便于界面提示与补抓
            outcome.FailedDates.Sort(StringComparer.Ordinal);
            outcome.SuspectDates.Sort(StringComparer.Ordinal);
            outcome.NoDataDates.Sort(StringComparer.Ordinal);
            return outcome;
        }

        /// <summary>单日抓取的结果分类。</summary>
        private enum DayFetchStatus
        {
            /// <summary>抓取成功且有报价数据。</summary>
            Ok = 0,

            /// <summary>页面正常但无报价（假期），静默忽略。</summary>
            NoQuoteData = 1,

            /// <summary>页面结构异常（找不到报价表），需要告警。</summary>
            ParseSuspect = 2,

            /// <summary>下载失败（网络 / 站点不可用）。</summary>
            Failed = 3,
        }

        /// <summary>单日抓取的原始结果。</summary>
        private sealed class DayFetchResult
        {
            /// <summary>该日对应的网址，提供日期与城市信息。</summary>
            public MyWebsite Website { get; set; }

            /// <summary>结果分类。</summary>
            public DayFetchStatus Status { get; set; }

            /// <summary>解析出的记录（仅 Ok 时非空）。</summary>
            public List<SteelPriceRecord> Rows { get; set; } = new List<SteelPriceRecord>();
        }

        /// <summary>
        /// 抓取指定城市多日的数据，只返回记录（兼容旧调用入口）。
        /// 需要感知失败日期时请改用 <see cref="FetchAsync"/>。
        /// </summary>
        public static async Task<List<SteelPriceRecord>> GetRecords(IReadOnlyList<MyWebsite> websites)
        {
            FetchOutcome outcome = await FetchAsync(websites).ConfigureAwait(false);
            return outcome.Records;
        }

        /// <summary>
        /// 抓取单个工作日。不抛出异常，而是把结果分类回传给调用方，
        /// 这样单天异常不会拖垮整批抓取。
        ///
        /// 分类的意义在于把「假期无报价」与「真的出问题了」分开：
        /// 前者静默忽略，后者才需要提示用户。
        /// </summary>
        private static async Task<DayFetchResult> FetchDayAsync(SemaphoreSlim gate, MyWebsite website)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string html = await DownloadStringAsync(new Uri(website.Url)).ConfigureAwait(false);
                List<SteelPriceRecord> rows = ParsePriceTable(html, out PageParseStatus status);

                // 补上城市与日期：这两个字段来自请求上下文，不在网页表格里
                string dateText = website.Date.ToString("yyyy-MM-dd");
                foreach (SteelPriceRecord row in rows)
                {
                    row.City = website.City;
                    row.QuoteDate = dateText;
                }

                DayFetchStatus result = status switch
                {
                    PageParseStatus.HasData => DayFetchStatus.Ok,

                    // 页面结构正常、只是没有报价（法定假期）：静默忽略
                    PageParseStatus.NoQuoteTableData => DayFetchStatus.NoQuoteData,

                    // 找不到报价表 / 页面为空：视为结构异常，需要告警
                    _ => DayFetchStatus.ParseSuspect,
                };

                return new DayFetchResult
                {
                    Website = website,
                    Status = result,
                    Rows = rows
                };
            }
            catch (Exception)
            {
                // 下载入口已做重试，到这里说明确实拿不到，按失败日记录
                return new DayFetchResult
                {
                    Website = website,
                    Status = DayFetchStatus.Failed,
                    Rows = new List<SteelPriceRecord>()
                };
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 带重试的页面下载。仅对「瞬时故障」重试（超时 / 连接失败 / 5xx / 429），
        /// 4xx 属于确定性失败，重试只是白等。
        /// </summary>
        private static async Task<string> DownloadStringAsync(Uri uri)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await Client.GetStringAsync(uri).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
                {
                    // 退避 400ms → 1200ms，并叠加 0~150ms 随机抖动，
                    // 避免同一批并发请求在同一时刻集体重试、造成对端二次冲击
                    int delay = (RetryBaseDelayMs * (int)Math.Pow(3, attempt - 1))
                                + Random.Shared.Next(0, 150);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
        }

        /// <summary>判断异常是否属于值得重试的瞬时故障。</summary>
        private static bool IsTransient(Exception ex) => ex switch
        {
            // HttpClient.Timeout 到期时抛出的就是 TaskCanceledException
            TaskCanceledException => true,
            TimeoutException => true,
            HttpRequestException http => http.StatusCode is null
                                         || (int)http.StatusCode >= 500
                                         || http.StatusCode == HttpStatusCode.RequestTimeout
                                         || http.StatusCode == HttpStatusCode.TooManyRequests,
            _ => false
        };

        /// <summary>下载单个工作日的报价记录（串行调用入口，供控制台回补工具使用）。</summary>
        public static async Task<List<SteelPriceRecord>> FetchOneDayAsync(MyWebsite website)
        {
            string html = await DownloadStringAsync(new Uri(website.Url)).ConfigureAwait(false);
            List<SteelPriceRecord> rows = ParsePriceTable(html);

            string dateText = website.Date.ToString("yyyy-MM-dd");
            foreach (SteelPriceRecord row in rows)
            {
                row.City = website.City;
                row.QuoteDate = dateText;
            }

            return rows;
        }

        /// <summary>
        /// 解析报价表，得到按行组织的数据。
        /// 每个 &lt;tr&gt; 单独处理，按行内实际列数判断：
        /// 5 列 = 品名/规格/牌号/价格/优质品牌推荐（丢弃第 5 列）；
        /// 4 列 = 品名/规格/牌号/价格。
        /// </summary>
        public static List<SteelPriceRecord> ParsePriceTable(string html)
        {
            return ParsePriceTable(html, out _);
        }

        /// <summary>
        /// 解析报价表，同时回报页面状态（用于区分「假期无报价」与「网页结构变了」）。
        ///
        /// 两种空结果的性质完全不同，绝不能混为一谈：
        /// 法定假期（调休上班但不出报价）时页面照常返回，表头也在，只是没有数据行——
        /// 这属于正常现象，静默忽略即可；
        /// 而连含「品名」的表都定位不到，说明网站改版了，必须提示人去处理。
        /// </summary>
        /// <param name="html">页面 HTML。</param>
        /// <param name="status">回报页面状态。</param>
        public static List<SteelPriceRecord> ParsePriceTable(string html, out PageParseStatus status)
        {
            List<SteelPriceRecord> rows = new List<SteelPriceRecord>();

            if (string.IsNullOrEmpty(html))
            {
                status = PageParseStatus.EmptyPage;
                return rows;
            }

            // 截取正文锚点之前的内容，剔除页脚
            string body = html;
            int anchor = html.IndexOf(BodyAnchor, StringComparison.Ordinal);
            if (anchor > 0)
            {
                body = html.Substring(0, anchor);
            }

            // 定位正文中第一个含“品名”表头的报价表
            string table = null;
            foreach (Match tableMatch in TableRegex().Matches(body))
            {
                if (tableMatch.Value.Contains("品名"))
                {
                    table = tableMatch.Value;
                    break;
                }
            }

            if (table is null)
            {
                // 表都找不到：网页结构可能变了，交给上层告警
                status = PageParseStatus.TableNotFound;
                return rows;
            }

            foreach (Match rowMatch in RowRegex().Matches(table))
            {
                MatchCollection cells = CellRegex().Matches(rowMatch.Value);
                if (cells.Count < 4)
                {
                    continue;
                }

                string product = StripTags(cells[0].Groups[1].Value);
                string spec = StripTags(cells[1].Groups[1].Value);
                string grade = StripTags(cells[2].Groups[1].Value);

                // 规格归一化：网站把「定尺」写作 φ20*12（直径*定尺米数），
                // 直观看像管材的「外径*壁厚」容易被误判为脏数据。
                // 这里统一改写为 φ20（定尺12m），既保留信息又消除歧义。
                spec = NormalizeSpec(spec);

                // 跳过表头行
                if (product == "品名" || spec == "规格型号")
                {
                    continue;
                }

                // 关键字段缺一不可
                if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(grade))
                {
                    continue;
                }

                // 过滤链接残留等非数据行
                if (product.Contains("更多") || product.Contains("推荐"))
                {
                    continue;
                }

                if (!TryParsePrice(StripTags(cells[3].Groups[1].Value), out double price))
                {
                    continue;
                }

                rows.Add(new SteelPriceRecord
                {
                    ProductName = product,
                    Spec = spec,
                    Grade = grade,
                    Price = price
                });
            }

            // 表找到了但一行数据都没解析出来 → 假期无报价（页面结构本身是好的）
            status = rows.Count > 0
                ? PageParseStatus.HasData
                : PageParseStatus.NoQuoteTableData;

            return rows;
        }

        /// <summary>去掉标签与 HTML 实体，压缩空白，得到单元格纯文本。</summary>
        private static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string plain = TagRegex().Replace(text, " ");
            plain = plain.Replace("&gt;", ">").Replace("&lt;", "<")
                         .Replace("&amp;", "&").Replace("&nbsp;", " ");
            return SpaceRegex().Replace(plain, " ").Trim();
        }

        /// <summary>
        /// 规格归一化。
        ///
        /// 西本新干线对「定尺」螺纹钢把规格写成 φ20*12（直径 * 定尺米数），
        /// 与管材的「外径*壁厚」写法撞车，界面上容易被当成脏数据。
        /// 这里统一改写为 φ20（定尺12m），保留信息同时消除歧义。
        ///
        /// 例：φ20*12 → φ20（定尺12m）；φ20 → φ20 原样返回。
        /// </summary>
        private static string NormalizeSpec(string spec)
        {
            if (string.IsNullOrEmpty(spec))
            {
                return spec;
            }

            // 只处理「φ数字*数字」这一种形态，其余（φ8-10、φ6.5 等）原样保留
            Match m = SpecMeasureRegex().Match(spec);
            if (!m.Success)
            {
                return spec;
            }

            string diameter = SpaceRegex().Replace(m.Groups[1].Value, string.Empty);
            return $"{diameter}（定尺{m.Groups[2].Value}m）";
        }

        /// <summary>解析价格文本，支持 "3780"、"3,780"、"3780元/吨" 等写法。</summary>
        private static bool TryParsePrice(string text, out double price)
        {
            price = 0d;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            Match match = PriceNumberRegex().Match(text);
            if (!match.Success)
            {
                return false;
            }

            if (!double.TryParse(match.Groups[1].Value.Replace(",", string.Empty),
                                 System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 out double value))
            {
                return false;
            }

            if (value < PriceMin || value > PriceMax)
            {
                return false;
            }

            price = value;
            return true;
        }
    }
}
