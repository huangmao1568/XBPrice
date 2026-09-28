using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XBPrice
{
    /// <summary>
    /// 数据提取规则：把网页中的表格单元格切分出来。
    /// </summary>
    public class MyRule
    {
        /// <summary>匹配单个单元格的正则表达式。</summary>
        public string RegexPattern { get; set; } = "<td>.{2,7}</td>";

        /// <summary>去掉单元格标签用的分隔符。</summary>
        public string[] Delimiters { get; set; } = { "<td>", "</td>" };
    }

    /// <summary>
    /// 网页文本获取工具。共用同一个 HttpClient，避免反复创建连接池。
    /// </summary>
    public static class MyHttp
    {
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public static Task<string> GetTextAsync(string address)
        {
            return Client.GetStringAsync(new Uri(address));
        }
    }

    /// <summary>
    /// 网页内容解析工具。
    /// </summary>
    public static class MyStrHandle
    {
        /// <summary>正文锚点，锚点之后的内容会被截掉。</summary>
        private const string BodyAnchor = "总机服务";

        /// <summary>截取锚点之前的内容；找不到锚点时原样返回。</summary>
        public static string GetUsefulString(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            int anchor = str.IndexOf(BodyAnchor, StringComparison.Ordinal);
            if (anchor < 0)
            {
                return str;
            }

            // 保持原有语义（跳过第 1 个字符），同时避免 index 越界抛异常
            int length = Math.Min(anchor, str.Length - 1);
            return length > 0 ? str.Substring(1, length) : str;
        }

        /// <summary>按规则提取全部单元格文本。</summary>
        public static List<string> GetCellValues(string input, MyRule rule)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(input) || rule is null)
            {
                return result;
            }

            StringBuilder builder = new StringBuilder();
            foreach (Match match in Regex.Matches(GetUsefulString(input), rule.RegexPattern, RegexOptions.IgnoreCase))
            {
                builder.Append(match.Value);
            }

            result.AddRange(builder.ToString().Split(rule.Delimiters, StringSplitOptions.RemoveEmptyEntries));
            return result;
        }

        /// <summary>并发下载上限，避免一次性把对端打满。</summary>
        private const int MaxConcurrency = 4;

        private static readonly MyRule Rule = new MyRule();

        /// <summary>抓取指定列的数据，每个网址返回一条记录（并发下载）。</summary>
        /// <param name="websites">要抓取的网址集合。</param>
        /// <param name="column">列号，从 1 开始。</param>
        public static async Task<List<ColumnData>> GetColumn(IReadOnlyList<MyWebsite> websites, int column = 4)
        {
            if (websites is null || websites.Count == 0)
            {
                throw new InvalidOperationException("网址错误");
            }

            using (SemaphoreSlim gate = new SemaphoreSlim(MaxConcurrency))
            {
                Task<ColumnData>[] tasks = new Task<ColumnData>[websites.Count];
                for (int i = 0; i < websites.Count; i++)
                {
                    tasks[i] = FetchOneAsync(gate, websites[i], column);
                }

                return new List<ColumnData>(await Task.WhenAll(tasks));
            }
        }

        /// <summary>同一张页面只下载一次，按需提取多列，结果顺序与 columns 一致。</summary>
        public static async Task<List<ColumnData>> GetColumnsFromPage(MyWebsite website, params int[] columns)
        {
            string html = await MyHttp.GetTextAsync(website.Url);

            List<ColumnData> result = new List<ColumnData>(columns.Length);
            foreach (int column in columns)
            {
                result.Add(new ColumnData(ExtractCells(html, column), website.Date));
            }

            return result;
        }

        private static async Task<ColumnData> FetchOneAsync(SemaphoreSlim gate, MyWebsite website, int column)
        {
            await gate.WaitAsync();
            try
            {
                string html = await MyHttp.GetTextAsync(website.Url);
                return new ColumnData(ExtractCells(html, column), website.Date);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>从一份 HTML 中取出第 column 列的全部单元格。</summary>
        private static List<string> ExtractCells(string html, int column)
        {
            List<string> values = GetCellValues(html, Rule);

            List<string> cells = new List<string>();
            for (int i = column - 1; i < values.Count; i += 4)
            {
                cells.Add(values[i]);
            }

            return cells;
        }
    }

    /// <summary>
    /// 报价网址：由城市与日期生成。
    /// </summary>
    public class MyWebsite
    {
        private const string UrlTemplate = "http://{0}.steelx2.com/city/Quotation/quotation/1/{1}/index.html";

        public MyWebsite(string city, DateTimeOffset time)
        {
            this.City = city;
            this.Date = time;
            this.DateText = time.ToString("yyyyMMdd");
        }

        public string City { get; }

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
                throw new ArgumentException(" 开始日期不能大于截至日期 ");
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
}
