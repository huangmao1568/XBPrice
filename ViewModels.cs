using System;
using System.Collections.Generic;

namespace XBPrice
{
    /// <summary>
    /// 当日汇总：按 城市 + 报价日 汇总当日条数与价格区间。
    /// 由 LINQ 分组投影产生，不依赖数据库视图。
    /// </summary>
    public class DailySummary
    {
        /// <summary>城市。</summary>
        public string City { get; set; }

        /// <summary>报价日期，格式 yyyy-MM-dd。</summary>
        public string QuoteDate { get; set; }

        /// <summary>当日规格条数。</summary>
        public long ItemCount { get; set; }

        /// <summary>当日最低价。</summary>
        public double MinPrice { get; set; }

        /// <summary>当日平均价（已四舍五入到 2 位）。</summary>
        public double AvgPrice { get; set; }

        /// <summary>当日最高价。</summary>
        public double MaxPrice { get; set; }
    }

    /// <summary>
    /// 月均价格的界面行：按 城市 + 年月 + 品名 + 规格 + 牌号 汇总，供数据库页直接绑定。
    /// 聚合由 LINQ 完成，不再依赖数据库视图。
    /// </summary>
    public class MonthlyPriceRow
    {
        /// <summary>城市。</summary>
        public string City { get; set; }

        /// <summary>年月，格式 yyyy-MM。</summary>
        public string YearMonth { get; set; }

        /// <summary>品名，如 螺纹钢。</summary>
        public string ProductName { get; set; }

        /// <summary>规格型号，如 φ12。</summary>
        public string Spec { get; set; }

        /// <summary>牌号，如 HRB400E。</summary>
        public string Grade { get; set; }

        /// <summary>报价天数。</summary>
        public long DayCount { get; set; }

        /// <summary>规格条数。</summary>
        public long ItemCount { get; set; }

        /// <summary>月内最低价。</summary>
        public double MinPrice { get; set; }

        /// <summary>月均价。</summary>
        public double AvgPrice { get; set; }

        /// <summary>月内最高价。</summary>
        public double MaxPrice { get; set; }

        // ---------- 以下为界面展示用的派生属性 ----------

        /// <summary>月均价文本，如 3,520.50。</summary>
        public string AvgPriceText => this.AvgPrice.ToString("N2");

        /// <summary>价格区间文本，如 3,480.00 ~ 3,600.00。</summary>
        public string RangeText => $"{this.MinPrice:N2} ~ {this.MaxPrice:N2}";
    }

    /// <summary>
    /// 入库结果统计：区分新增与更新，供界面提示使用。
    /// </summary>
    public class SaveResult
    {
        /// <summary>本次提交的记录总数。</summary>
        public int Total { get; set; }

        /// <summary>其中新增的条数。</summary>
        public int Inserted { get; set; }

        /// <summary>其中命中唯一键、仅更新价格的条数。</summary>
        public int Updated { get; set; }
    }
}
