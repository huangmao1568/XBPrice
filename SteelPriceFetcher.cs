using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 钢价抓取器：下载报价页并解析为结构化记录。
    ///
    /// 【重要】解析采用按 &lt;tr&gt; 逐行的方式。原 XBPrice 程序曾用
    /// “提取全部 &lt;td&gt; 后按固定步长 4 取值”的做法，但网页每个品类的
    /// 第一条规格会多出一列“优质品牌推荐”，行内列数 5/4 混排，
    /// 导致从第二条起整列错位。按行解析并按行内实际列数判断，彻底修正该问题。
    /// </summary>
    public static class SteelPriceFetcher
    {
        /// <summary>正文锚点，锚点之后是网站页脚，需截断。</summary>
        private const string BodyAnchor = "总机服务";

        /// <summary>匹配整行 &lt;tr&gt; 的正则（单行模式，允许跨行）。</summary>
        private const string RowPattern = "<tr.*?</tr>";

        /// <summary>匹配行内单元格 &lt;td&gt; / &lt;th&gt; 的正则。</summary>
        private const string CellPattern = "<t[dh][^>]*>(.*?)</t[dh]>";

        /// <summary>价格合法区间（元/吨），超出视为解析异常。</summary>
        private const double PriceMin = 100d;
        private const double PriceMax = 100000d;

        /// <summary>并发下载上限，避免一次性把对端打满。</summary>
        private const int MaxConcurrency = 4;

        /// <summary>共用同一个 HttpClient，避免反复创建连接池。</summary>
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        static SteelPriceFetcher()
        {
            // 部分站点对无 UA 请求做拦截，伪装成常见浏览器
            Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        /// <summary>
        /// 抓取指定城市多日的数据，每个日期返回其全部规格记录（并发下载）。
        /// </summary>
        /// <param name="websites">要抓取的网址集合（每个工作日一条）。</param>
        /// <returns>按日期展开的全部报价记录。</returns>
        public static async Task<List<SteelPriceRecord>> GetRecords(IReadOnlyList<MyWebsite> websites)
        {
            if (websites is null || websites.Count == 0)
            {
                throw new InvalidOperationException("网址错误");
            }

            using (SemaphoreSlim gate = new SemaphoreSlim(MaxConcurrency))
            {
                Task<List<SteelPriceRecord>>[] tasks = new Task<List<SteelPriceRecord>>[websites.Count];
                for (int i = 0; i < websites.Count; i++)
                {
                    tasks[i] = FetchRecordsAsync(gate, websites[i]);
                }

                List<SteelPriceRecord>[] groups = await Task.WhenAll(tasks);
                List<SteelPriceRecord> all = new List<SteelPriceRecord>();
                foreach (List<SteelPriceRecord> group in groups)
                {
                    all.AddRange(group);
                }
                return all;
            }
        }

        private static async Task<List<SteelPriceRecord>> FetchRecordsAsync(
            SemaphoreSlim gate, MyWebsite website)
        {
            await gate.WaitAsync();
            try
            {
                string html = await Client.GetStringAsync(new Uri(website.Url));
                List<SteelPriceRecord> rows = ParsePriceTable(html);

                // 补上城市与日期：这两个字段来自请求上下文，不在网页表格里
                string dateText = website.Date.ToString("yyyy-MM-dd");
                foreach (SteelPriceRecord row in rows)
                {
                    row.City = website.City;
                    row.QuoteDate = dateText;
                }

                return rows;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>下载单个工作日的报价记录（串行调用入口，供控制台回补工具使用）。</summary>
        public static async Task<List<SteelPriceRecord>> FetchOneDayAsync(MyWebsite website)
        {
            string html = await Client.GetStringAsync(new Uri(website.Url));
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
            List<SteelPriceRecord> rows = new List<SteelPriceRecord>();
            if (string.IsNullOrEmpty(html))
            {
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
            foreach (Match tableMatch in Regex.Matches(body, "<table.*?</table>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                if (tableMatch.Value.Contains("品名"))
                {
                    table = tableMatch.Value;
                    break;
                }
            }

            if (table is null)
            {
                return rows;
            }

            foreach (Match rowMatch in Regex.Matches(table, RowPattern,
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                MatchCollection cells = Regex.Matches(rowMatch.Value, CellPattern,
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
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

            return rows;
        }

        /// <summary>去掉标签与 HTML 实体，压缩空白，得到单元格纯文本。</summary>
        private static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string plain = Regex.Replace(text, "<[^>]+>", " ");
            plain = plain.Replace("&gt;", ">").Replace("&lt;", "<")
                         .Replace("&amp;", "&").Replace("&nbsp;", " ");
            return Regex.Replace(plain, @"\s+", " ").Trim();
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
            Match m = Regex.Match(spec, @"^(φ\s*\d+(?:\.\d+)?)\s*[*×]\s*(\d+(?:\.\d+)?)$");
            if (!m.Success)
            {
                return spec;
            }

            string diameter = Regex.Replace(m.Groups[1].Value, @"\s+", string.Empty);
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

            Match match = Regex.Match(text, @"(\d[\d,]*\.?\d*)");
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
