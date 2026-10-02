namespace XBPrice
{
    /// <summary>
    /// 月均价格表格的一行（界面绑定用）。
    ///
    /// 与 MonthlyPriceRow 分开，避免把 UI 类型（刷子）挂在数据模型上。
    /// 本文件只含界面类型，数据层不依赖它——
    /// 这样纯数据场景（如控制台回补工具）可以只链接 ViewModels.cs 而不引入 WinUI 依赖。
    /// </summary>
    public class MonthlyRow
    {
        /// <summary>行号，从 1 开始，由列表填充时按顺序赋值。</summary>
        public int Index { get; set; }

        /// <summary>年月 yyyy-MM。</summary>
        public string YearMonth { get; set; }

        /// <summary>城市。</summary>
        public string City { get; set; }

        /// <summary>品名。</summary>
        public string ProductName { get; set; }

        /// <summary>规格型号，如 φ12。</summary>
        public string Spec { get; set; }

        /// <summary>牌号，如 HRB400E。</summary>
        public string Grade { get; set; }

        /// <summary>报价天数。</summary>
        public long DayCount { get; set; }

        /// <summary>规格条数。</summary>
        public long ItemCount { get; set; }

        /// <summary>月均价（千分位文本）。</summary>
        public string AvgPriceText { get; set; }

        /// <summary>价格区间文本。</summary>
        public string RangeText { get; set; }
    }

    /// <summary>
    /// 原始数据表格的一行（界面绑定用）。
    /// 直接对应库中的一条报价记录，不做任何聚合。
    /// </summary>
    public class RawRow
    {
        /// <summary>行号，从 1 开始，由列表填充时按顺序赋值。</summary>
        public int Index { get; set; }

        /// <summary>报价日期 yyyy-MM-dd。</summary>
        public string QuoteDate { get; set; }

        /// <summary>城市。</summary>
        public string City { get; set; }

        /// <summary>品名。</summary>
        public string ProductName { get; set; }

        /// <summary>牌号，如 HRB400E。</summary>
        public string Grade { get; set; }

        /// <summary>规格型号，如 φ20（定尺12m）。</summary>
        public string Spec { get; set; }

        /// <summary>价格（千分位文本）。</summary>
        public string PriceText { get; set; }
    }
}