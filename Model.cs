using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;

namespace XBPrice
{
    /// <summary>
    /// 一列报价数据：所属日期 + 该列的全部取值。
    /// </summary>
    public class ColumnData
    {
        public ColumnData()
        {
            this.Date = DateTimeOffset.Now;
            this.DateText = this.Date.ToString("yyMMdd");
            this.Values = string.Empty;
        }

        public ColumnData(IEnumerable<string> values, DateTimeOffset date)
        {
            this.Date = date;
            this.DateText = date.ToString("yyMMdd");

            List<string> cells = values is null ? new List<string>() : new List<string>(values);
            this.Values = cells.Count == 0 ? string.Empty : string.Join("\n", cells) + "\n";
        }

        /// <summary>数据所属日期。</summary>
        public DateTimeOffset Date { get; set; }

        /// <summary>日期文本，例如 160906。</summary>
        public string DateText { get; set; }

        /// <summary>该列的全部取值，以换行分隔。</summary>
        public string Values { get; set; }
    }

    /// <summary>
    /// 报价表格的一行（右侧数据看板用）。
    /// 只承载展示所需字段，避免把整个实体直接绑到界面。
    /// </summary>
    public class QuoteRow
    {
        /// <summary>报价日期，展示为 MM-dd 以节省列宽。</summary>
        public string DateText { get; set; }

        /// <summary>“品名 规格”合并文本。</summary>
        public string NameText { get; set; }

        /// <summary>牌号 / 材质。</summary>
        public string Grade { get; set; }

        /// <summary>价格（元/吨），千分位格式。</summary>
        public string PriceText { get; set; }

        /// <summary>涨跌文本，如 +20 / -10 / —。</summary>
        public string TrendText { get; set; }

        /// <summary>
        /// 涨跌文字的刷子：涨红、跌绿、无变化灰。
        /// 国内行情惯例——涨用红色、跌用绿色。
        /// </summary>
        public SolidColorBrush TrendBrush { get; set; }
    }
}