using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace XBPrice
{
    /// <summary>
    /// XBPrice 数据库上下文（SQLite）。
    ///
    /// 表：SteelPrice（报价明细）/ ProductCategory（品名分类）/ FetchLog（抓取日志）。
    ///
    /// 汇总统计（日汇总、月均）一律用 LINQ 在查询时聚合，不再建数据库视图——
    /// 视图定义一旦变更就需要 DROP + CREATE，且改列要同时改 SQL 与实体映射两处。
    /// </summary>
    public class XbPriceDbContext : DbContext
    {
        /// <summary>报价明细表。</summary>
        public DbSet<SteelPriceRecord> SteelPrices { get; set; }

        /// <summary>品名分类维度表。</summary>
        public DbSet<ProductCategory> ProductCategories { get; set; }

        /// <summary>抓取批次日志表。</summary>
        public DbSet<FetchLog> FetchLogs { get; set; }

        private readonly string _databasePath;

        /// <summary>
        /// 构造上下文。
        /// </summary>
        /// <param name="databasePath">
        /// 数据库文件路径；传 null 时使用默认位置（见 <see cref="GetDefaultDatabasePath"/>）。
        /// </param>
        public XbPriceDbContext(string databasePath = null)
        {
            this._databasePath = string.IsNullOrWhiteSpace(databasePath)
                ? GetDefaultDatabasePath()
                : databasePath;
        }

        /// <summary>
        /// 数据库根目录名（位于用户文档目录下，独立于 MSIX 包沙箱）。
        /// </summary>
        private const string DataFolderName = "XBPrice";

        /// <summary>
        /// 默认数据库位置：%USERPROFILE%\Documents\XBPrice\XBPrice.db。
        ///
        /// 为什么不放在 %LOCALAPPDATA%：
        /// 打包（MSIX）运行时 %LOCALAPPDATA% 会被重定向到包沙箱
        /// （...\Packages\&lt;包族名&gt;\LocalCache\Local\），而每次重新部署
        /// （Remove-AppxPackage + Add-AppxPackage）都会清空该沙箱，
        /// 导致已抓取的数据全部丢失。放到文档目录下可跨部署持久保留，
        /// 也方便用户直接备份、查看。
        /// </summary>
        public static string GetDefaultDatabasePath()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                // 极少数环境取不到文档目录，退回 %LOCALAPPDATA%
                documents = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            string folder = Path.Combine(documents, DataFolderName);
            return Path.Combine(folder, "XBPrice.db");
        }

        /// <summary>数据库文件完整路径。</summary>
        public string DatabasePath => this._databasePath;

        /// <summary>配置 SQLite 连接。</summary>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                // 确保目录存在，否则 SQLite 无法创建文件
                string directory = Path.GetDirectoryName(this._databasePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                optionsBuilder.UseSqlite($"Data Source={this._databasePath}");
            }
        }

        /// <summary>表 / 索引 / 视图映射配置。</summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ---------- 报价明细表 ----------
            modelBuilder.Entity<SteelPriceRecord>(entity =>
            {
                entity.ToTable("SteelPrice");
                entity.HasKey(r => r.Id);

                entity.Property(r => r.City).IsRequired();
                entity.Property(r => r.QuoteDate).IsRequired();
                entity.Property(r => r.ProductName).IsRequired();
                entity.Property(r => r.Spec).IsRequired();
                entity.Property(r => r.Grade).IsRequired();

                // 入库时间由数据库默认值生成（与原 schema.sql 的 DEFAULT 保持一致）
                entity.Property(r => r.CreatedAt)
                      .HasDefaultValueSql("datetime('now','localtime')");
                entity.Property(r => r.UpdatedAt)
                      .HasDefaultValueSql("datetime('now','localtime')");

                // 唯一键：同城市 + 同日期 + 同品名 + 同规格 + 同牌号 只允许一行
                // 保证重复抓取同一天数据时按“更新价格”处理（幂等）
                entity.HasIndex(r => new
                {
                    r.City,
                    r.QuoteDate,
                    r.ProductName,
                    r.Spec,
                    r.Grade
                }).IsUnique();

                // 常用查询索引：城市 + 日期区间
                entity.HasIndex(r => new { r.City, r.QuoteDate });

                // 同规格跨日期比价
                entity.HasIndex(r => new { r.ProductName, r.Spec });
            });

            // ---------- 品名分类维度表 ----------
            modelBuilder.Entity<ProductCategory>(entity =>
            {
                entity.ToTable("ProductCategory");
                entity.HasKey(c => c.ProductName);
                entity.Property(c => c.ProductName).IsRequired();
                entity.Property(c => c.Category).IsRequired();
            });

            // ---------- 抓取批次日志表 ----------
            modelBuilder.Entity<FetchLog>(entity =>
            {
                entity.ToTable("FetchLog");
                entity.HasKey(l => l.Id);
                entity.Property(l => l.StartDate).IsRequired();
                entity.Property(l => l.EndDate).IsRequired();
                entity.Property(l => l.Cities).IsRequired();
                entity.Property(l => l.Status).IsRequired();
                entity.Property(l => l.CreatedAt)
                      .HasDefaultValueSql("datetime('now','localtime')");
            });

        }

        /// <summary>
        /// 初始化数据库：建表（EnsureCreated）+ 品名分类种子数据。
        /// 幂等，可在每次启动时调用。
        /// </summary>
        public void EnsureCreated()
        {
            this.Database.EnsureCreated();

            // 品名分类种子数据（幂等）
            foreach (ProductCategory seed in new[]
            {
                new ProductCategory { ProductName = "高线",   Category = "线材",   SortOrder = 10 },
                new ProductCategory { ProductName = "螺纹钢", Category = "螺纹钢", SortOrder = 20 },
                new ProductCategory { ProductName = "盘螺",   Category = "盘螺",   SortOrder = 30 },
                new ProductCategory { ProductName = "圆钢",   Category = "圆钢",   SortOrder = 40 },
                new ProductCategory { ProductName = "焊管",   Category = "管材",   SortOrder = 50 }
            })
            {
                bool exists = this.ProductCategories.Any(c => c.ProductName == seed.ProductName);
                if (!exists)
                {
                    this.ProductCategories.Add(seed);
                }
            }
            this.SaveChanges();
        }
    }
}
