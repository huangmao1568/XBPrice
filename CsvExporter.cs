using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace XBPrice
{
    /// <summary>
    /// CSV 导出辅助：把界面表格数据写成 CSV 文件。
    ///
    /// 两个约定：
    /// 1. Excel 双击打开 CSV 时按本地编码解析，所以文件必须带 UTF-8 BOM，否则中文乱码；
    /// 2. 价格等数值一律写成纯数字（不带千分位、不带货币符号），
    ///    否则 Excel 会把「3,520.50」当成文本，无法参与计算。
    /// </summary>
    public static class CsvExporter
    {
        /// <summary>
        /// 字段转义：含逗号、引号、换行时用双引号包起来，内部引号翻倍。
        /// </summary>
        public static string Escape(string value)
        {
            string text = value ?? string.Empty;

            bool needQuote = text.IndexOf(',') >= 0
                             || text.IndexOf('"') >= 0
                             || text.IndexOf('\n') >= 0
                             || text.IndexOf('\r') >= 0;

            if (!needQuote)
            {
                return text;
            }

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>把数值格式化为 CSV 用的纯数字（固定小数位，不带千分位）。</summary>
        public static string Number(double value, int decimals = 2)
        {
            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
        }

        /// <summary>把整条记录按逗号连接成一行 CSV。</summary>
        public static string Line(params string[] fields)
        {
            return string.Join(",", fields);
        }

        /// <summary>
        /// 生成带 UTF-8 BOM 的字节内容，写入文件后 Excel 直接双击打开也不会乱码。
        /// </summary>
        public static byte[] BuildBytes(string csvContent)
        {
            byte[] bom = Encoding.UTF8.GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes(csvContent ?? string.Empty);
            byte[] bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            return bytes;
        }

        /// <summary>把 CSV 文本写入指定路径（覆盖同名文件）。</summary>
        public static void WriteTo(string filePath, string csvContent)
        {
            File.WriteAllBytes(filePath, BuildBytes(csvContent));
        }

        /// <summary>
        /// 生成导出文件名：前缀 + 筛选描述 + 时间戳，如
        /// 「月均价_成都_螺纹钢φ12HRB400E_20261003-1045.csv」。
        ///
        /// 该值用作「另存为」对话框的默认文件名，用户仍可自行改名或换目录。
        /// </summary>
        public static string BuildFileName(string prefix, IEnumerable<string> filterParts)
        {
            StringBuilder sb = new StringBuilder(prefix ?? "导出数据");

            if (filterParts is not null)
            {
                foreach (string part in filterParts)
                {
                    if (string.IsNullOrWhiteSpace(part))
                    {
                        continue;
                    }

                    sb.Append('_').Append(part.Trim());
                }
            }

            sb.Append('_')
              .Append(DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture));

            return SanitizeFileName(sb.ToString()) + ".csv";
        }

        /// <summary>
        /// 清洗文件名中的非法字符（Windows 不允许 \ / : * ? " &lt; &gt; |）。
        /// 替换为全角下划线，避免导出时抛异常。
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            return sb.ToString();
        }

        // ============================================================
        // 两种表格的 CSV 生成
        // ============================================================

        /// <summary>
        /// 生成「月均汇总」CSV。字段顺序与界面表头一致，便于与屏幕内容逐列核对。
        /// </summary>
        public static string BuildMonthlyCsv(IEnumerable<MonthlyRow> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Line("序号", "年月", "城市", "品名", "牌号", "规格",
                           "报价天数", "条数", "月均价(元/吨)", "最低价(元/吨)", "最高价(元/吨)"));
            sb.Append('\n');

            foreach (MonthlyRow r in rows)
            {
                sb.Append(Line(
                    r.Index.ToString(CultureInfo.InvariantCulture),
                    Escape(r.YearMonth),
                    Escape(r.City),
                    Escape(r.ProductName),
                    Escape(r.Grade),
                    Escape(r.Spec),
                    r.DayCount.ToString(CultureInfo.InvariantCulture),
                    r.ItemCount.ToString(CultureInfo.InvariantCulture),
                    Number(r.AvgPrice),
                    Number(r.MinPrice),
                    Number(r.MaxPrice)));
                sb.Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成「原始数据」CSV。字段顺序与界面表头一致。
        /// </summary>
        public static string BuildRawCsv(IEnumerable<RawRow> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Line("序号", "报价日期", "城市", "品名", "牌号", "规格", "价格(元/吨)"));
            sb.Append('\n');

            foreach (RawRow r in rows)
            {
                sb.Append(Line(
                    r.Index.ToString(CultureInfo.InvariantCulture),
                    Escape(r.QuoteDate),
                    Escape(r.City),
                    Escape(r.ProductName),
                    Escape(r.Grade),
                    Escape(r.Spec),
                    Number(r.Price)));
                sb.Append('\n');
            }

            return sb.ToString();
        }
    }
}