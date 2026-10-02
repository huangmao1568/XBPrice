using System;
using System.Collections.Generic;

namespace XBPrice
{
    /// <summary>
    /// 钢材报价实体：映射数据库 SteelPrice 表。
    /// 一条记录 = 某城市 + 某报价日 + 某一条钢材规格的价格。
    /// </summary>
    public class SteelPriceRecord
    {
        /// <summary>自增主键。</summary>
        public long Id { get; set; }

        /// <summary>城市（网址中的拼音标识，如 chengdo）。</summary>
        public string City { get; set; }

        /// <summary>报价日期，格式 yyyy-MM-dd。</summary>
        public string QuoteDate { get; set; }

        /// <summary>品名（高线 / 螺纹钢 / 盘螺 / 圆钢 / 焊管）。</summary>
        public string ProductName { get; set; }

        /// <summary>规格型号（φ6.5、φ18 等）。</summary>
        public string Spec { get; set; }

        /// <summary>牌号 / 材质（Q235、HPB300、HRB400 等）。</summary>
        public string Grade { get; set; }

        /// <summary>价格（元/吨）。</summary>
        public double Price { get; set; }

        /// <summary>抓取入库时间（首次插入时由数据库默认值生成）。</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>最后一次价格更新时间（重复抓取命中唯一键时刷新）。</summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>拼出“品名 规格 牌号”的展示文本，便于界面直接绑定。</summary>
        public string Display => $"{this.ProductName} {this.Spec} {this.Grade}";
    }

    /// <summary>
    /// 品名分类维度表：映射数据库 ProductCategory 表。
    /// 把网页上的自由文本品名归一到有限集合，便于分类汇总。
    /// </summary>
    public class ProductCategory
    {
        /// <summary>品名（主键）。</summary>
        public string ProductName { get; set; }

        /// <summary>所属大类（用于汇总统计）。</summary>
        public string Category { get; set; }

        /// <summary>排序序号，控制报表中的展示顺序。</summary>
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// 抓取批次日志：映射数据库 FetchLog 表。
    /// 记录每次抓取的成败，便于排查“某天数据没入库”的问题。
    /// </summary>
    public class FetchLog
    {
        /// <summary>自增主键。</summary>
        public long Id { get; set; }

        /// <summary>抓取起始日期（yyyy-MM-dd）。</summary>
        public string StartDate { get; set; }

        /// <summary>抓取截止日期（yyyy-MM-dd）。</summary>
        public string EndDate { get; set; }

        /// <summary>涉及城市，多个用逗号分隔。</summary>
        public string Cities { get; set; }

        /// <summary>本次解析出的记录条数。</summary>
        public int RecordCount { get; set; }

        /// <summary>本次新增的条数。</summary>
        public int InsertedCount { get; set; }

        /// <summary>本次命中唯一键、仅更新价格的条数。</summary>
        public int UpdatedCount { get; set; }

        /// <summary>结果状态：成功 / 失败。</summary>
        public string Status { get; set; }

        /// <summary>失败时的错误信息。</summary>
        public string Message { get; set; }

        /// <summary>记录时间。</summary>
        public DateTime CreatedAt { get; set; }
    }
}
