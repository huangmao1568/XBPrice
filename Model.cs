using System;
using System.Collections.Generic;

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
}
