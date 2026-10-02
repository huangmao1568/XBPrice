using System;
using System.Collections.Generic;
using System.Linq;

namespace XBPrice
{
    /// <summary>
    /// 钢材报价数据访问层（EF Core + SQLite）。
    ///
    /// 职责：
    /// 幂等的批量写入：同一城市 + 日期 + 品名 + 规格 + 牌号 只保留一行，
    /// 重复抓取同一天数据时只刷新价格，不会产生重复记录；
    /// 常用查询：按城市与日期区间取明细、取当日汇总、取价格走势。
    ///
    /// 用法：构造时传入数据库路径（null 用默认位置，见
    /// <see cref="XbPriceDbContext.GetDefaultDatabasePath"/>，即「文档\XBPrice\XBPrice.db」），
    /// 首次使用调用 <see cref="Initialize"/> 完成建库；之后直接调用各查询方法。
    /// </summary>
    public sealed class SteelPriceRepository : IDisposable
    {
        /// <summary>EF Core 数据库上下文。整个仓储生命周期内共用。</summary>
        private readonly XbPriceDbContext _context;

        /// <summary>数据库文件完整路径。</summary>
        public string DatabasePath => _context.DatabasePath;

        /// <summary>
        /// 构造仓储。
        /// </summary>
        /// <param name="databasePath">数据库文件路径；null 时用默认位置。</param>
        /// <param name="initialize">
        /// 是否在构造时立即建库（建表 + 建视图 + 种子数据）。
        /// 默认 true，适合界面程序；控制台批量场景可传 false 后自行调用。
        /// </param>
        public SteelPriceRepository(string databasePath = null, bool initialize = true)
        {
            _context = new XbPriceDbContext(databasePath);

            // 迁移历史遗留的包沙箱数据库（若存在且新位置还没有数据）。
            // 早期版本把库放在 %LOCALAPPDATA%\XBPrice\ 下，打包运行时会被重定向到
            // 包沙箱 ...\Packages\<包族名>\LocalCache\Local\XBPrice\，卸载即丢失。
            // 这里做一次性搬迁，确保老用户的数据不会因为升级而丢掉。
            MigrateLegacySandboxDatabase(_context.DatabasePath);

            if (initialize)
            {
                this.Initialize();
            }
        }

        /// <summary>
        /// 把包沙箱里的旧数据库搬迁到新位置（仅当新位置尚无数据时）。
        ///
        /// 打包应用的 %LOCALAPPDATA% 被重定向，非打包进程读不到，
        /// 因此这里直接拼包沙箱物理路径查找。
        /// 找不到、或新库已有数据、或搬迁失败时静默跳过，不影响正常使用。
        /// </summary>
        private static void MigrateLegacySandboxDatabase(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath) || System.IO.File.Exists(targetPath))
                {
                    // 新位置已有库文件，无需搬迁
                    return;
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string packagesRoot = System.IO.Path.Combine(localAppData, "Packages");
                if (!System.IO.Directory.Exists(packagesRoot))
                {
                    return;
                }

                // 包沙箱里的重定向位置：Packages\<包族名>\LocalCache\Local\XBPrice\XBPrice.db
                foreach (string dir in System.IO.Directory.GetDirectories(packagesRoot, "huangdaxia.XBPrice*"))
                {
                    string legacy = System.IO.Path.Combine(
                        dir, "LocalCache", "Local", "XBPrice", "XBPrice.db");
                    if (!System.IO.File.Exists(legacy))
                    {
                        continue;
                    }

                    string directory = System.IO.Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }

                    System.IO.File.Copy(legacy, targetPath, overwrite: false);
                    return;
                }
            }
            catch
            {
                // 搬迁失败不影响主流程：新位置会直接建一个空库
            }
        }

        /// <summary>初始化数据库结构（幂等）：建表、建视图、写入品名分类种子数据。</summary>
        public void Initialize()
        {
            _context.EnsureCreated();
        }

        // ============================================================
        // 写入
        // ============================================================

        /// <summary>
        /// 批量写入报价记录（幂等 upsert）。
        /// 已存在的键只更新价格与 UpdatedAt，不新增重复行。
        /// </summary>
        /// <param name="records">待写入的记录集合，City 与 QuoteDate 必须已赋值。</param>
        /// <returns>新增 / 更新条数统计。</returns>
        public SaveResult Save(IEnumerable<SteelPriceRecord> records)
        {
            if (records is null)
            {
                return new SaveResult();
            }

            List<SteelPriceRecord> list = records.ToList();
            SaveResult result = new SaveResult { Total = list.Count };
            if (list.Count == 0)
            {
                return result;
            }

            // 一次性把涉及（城市, 日期）组合的已有记录全部加载并建立键索引，
            // 避免逐条 EXISTS 查询（本工具数据量为每天每城几十条，一次加载更高效）
            List<string> cities = list.Select(r => r.City).Distinct().ToList();
            List<string> dates = list.Select(r => r.QuoteDate).Distinct().ToList();

            Dictionary<string, SteelPriceRecord> existing = _context.SteelPrices
                .Where(r => cities.Contains(r.City) && dates.Contains(r.QuoteDate))
                .AsEnumerable()
                .ToDictionary(r => BuildKey(r), r => r);

            List<SteelPriceRecord> toAdd = new List<SteelPriceRecord>();
            foreach (SteelPriceRecord record in list)
            {
                if (existing.TryGetValue(BuildKey(record), out SteelPriceRecord hit))
                {
                    // 命中唯一键：只刷新价格与更新时间
                    hit.Price = record.Price;
                    hit.UpdatedAt = DateTime.Now;
                    result.Updated++;
                }
                else
                {
                    toAdd.Add(record);
                    result.Inserted++;
                }
            }

            if (toAdd.Count > 0)
            {
                _context.SteelPrices.AddRange(toAdd);
            }
            _context.SaveChanges();
            return result;
        }

        /// <summary>唯一键的字符串表示（与数据库唯一索引列一一对应）。</summary>
        private static string BuildKey(SteelPriceRecord r)
        {
            return string.Join("\u0001",
                r.City ?? string.Empty,
                r.QuoteDate ?? string.Empty,
                r.ProductName ?? string.Empty,
                r.Spec ?? string.Empty,
                r.Grade ?? string.Empty);
        }

        /// <summary>写入一条抓取批次日志。</summary>
        public void WriteFetchLog(string startDate, string endDate, string cities,
                                  int recordCount, int insertedCount, int updatedCount,
                                  string status, string message = null)
        {
            _context.FetchLogs.Add(new FetchLog
            {
                StartDate = startDate ?? string.Empty,
                EndDate = endDate ?? string.Empty,
                Cities = cities ?? string.Empty,
                RecordCount = recordCount,
                InsertedCount = insertedCount,
                UpdatedCount = updatedCount,
                Status = status ?? string.Empty,
                Message = message
            });
            _context.SaveChanges();
        }

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>
        /// 按城市与日期区间查询报价明细（按日期倒序、品名、规格排序）。
        /// </summary>
        /// <param name="city">城市拼音；null 或空表示不限城市。</param>
        /// <param name="from">起始日期 yyyy-MM-dd；null 表示不限。</param>
        /// <param name="to">结束日期 yyyy-MM-dd；null 表示不限。</param>
        public List<SteelPriceRecord> Query(string city = null, string from = null, string to = null)
        {
            IQueryable<SteelPriceRecord> query = _context.SteelPrices;

            if (!string.IsNullOrWhiteSpace(city))
            {
                query = query.Where(r => r.City == city.Trim());
            }
            if (!string.IsNullOrWhiteSpace(from))
            {
                string f = from.Trim();
                query = query.Where(r => string.Compare(r.QuoteDate, f, StringComparison.Ordinal) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                string t = to.Trim();
                query = query.Where(r => string.Compare(r.QuoteDate, t, StringComparison.Ordinal) <= 0);
            }

            return query
                .OrderByDescending(r => r.QuoteDate)
                .ThenBy(r => r.ProductName)
                .ThenBy(r => r.Spec)
                .ToList();
        }

        /// <summary>
        /// 查询各城市每日汇总（条数 / 最低价 / 平均价 / 最高价）。
        ///
        /// 用 LINQ 分组投影，不依赖数据库视图；EF 会把它翻译成
        /// GROUP BY City, QuoteDate 并配聚合函数，在数据库内完成计算。
        /// </summary>
        public List<DailySummary> QueryDailySummary(string city = null,
                                                    string from = null, string to = null)
        {
            IQueryable<SteelPriceRecord> query = _context.SteelPrices;

            if (!string.IsNullOrWhiteSpace(city))
            {
                string c = city.Trim();
                query = query.Where(r => r.City == c);
            }
            if (!string.IsNullOrWhiteSpace(from))
            {
                string f = from.Trim();
                query = query.Where(r => string.Compare(r.QuoteDate, f, StringComparison.Ordinal) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                string t = to.Trim();
                query = query.Where(r => string.Compare(r.QuoteDate, t, StringComparison.Ordinal) <= 0);
            }

            return query
                .GroupBy(r => new { r.City, r.QuoteDate })
                .Select(g => new DailySummary
                {
                    City = g.Key.City,
                    QuoteDate = g.Key.QuoteDate,
                    ItemCount = g.LongCount(),
                    MinPrice = g.Min(r => r.Price),
                    AvgPrice = Math.Round(g.Average(r => r.Price), 2),
                    MaxPrice = g.Max(r => r.Price)
                })
                .OrderByDescending(s => s.QuoteDate)
                .ThenBy(s => s.City)
                .ToList();
        }

        /// <summary>
        /// 查询原始报价明细（不做任何聚合，每条记录一行）。
        ///
        /// 供数据库页的「原始数据」视图使用，筛选维度与
        /// <see cref="QueryMonthlySummary"/> 保持一致（城市 / 年月 / 品名 / 规格 / 牌号），
        /// 便于两个视图在同一套筛选条件下对照。
        /// </summary>
        /// <param name="city">城市拼音；null 或空表示不限城市。</param>
        /// <param name="yearMonth">年月 yyyy-MM；null 或空表示不限月份。</param>
        /// <param name="products">品名白名单；null 或空表示不限。</param>
        /// <param name="specs">规格白名单；null 或空表示不限。</param>
        /// <param name="grades">牌号白名单；null 或空表示不限。</param>
        /// <returns>按日期倒序、品名、规格、牌号排序的原始明细。</returns>
        public List<SteelPriceRecord> QueryRawRecords(string city = null, string yearMonth = null,
                                                     ICollection<string> products = null,
                                                     ICollection<string> specs = null,
                                                     ICollection<string> grades = null)
        {
            IQueryable<SteelPriceRecord> query = _context.SteelPrices;

            if (!string.IsNullOrWhiteSpace(city))
            {
                string c = city.Trim();
                query = query.Where(r => r.City == c);
            }
            if (!string.IsNullOrWhiteSpace(yearMonth))
            {
                string ym = yearMonth.Trim();
                query = query.Where(r => r.QuoteDate.StartsWith(ym));
            }

            // 白名单为空视为「不限」，与 QueryMonthlySummary 的约定一致
            if (products is not null && products.Count > 0)
            {
                query = query.Where(r => products.Contains(r.ProductName));
            }
            if (specs is not null && specs.Count > 0)
            {
                query = query.Where(r => specs.Contains(r.Spec));
            }
            if (grades is not null && grades.Count > 0)
            {
                query = query.Where(r => grades.Contains(r.Grade));
            }

            return query
                .OrderByDescending(r => r.QuoteDate)
                .ThenBy(r => r.City)
                .ThenBy(r => r.ProductName)
                .ThenBy(r => r.Spec)
                .ThenBy(r => r.Grade)
                .ToList();
        }

        /// <summary>统计库中记录总数。</summary>
        public long Count()
        {
            return _context.SteelPrices.LongCount();
        }

        /// <summary>
        /// 查询月均价格（数据库页看板数据源）。
        ///
        /// 按 城市 + 年月 + 品名 + 规格 + 牌号 分组，全部用 LINQ 表达，EF 翻译成
        /// GROUP BY + COUNT(DISTINCT ...) / MIN / AVG / MAX 在数据库内聚合。
        ///
        /// 筛选在 C# 里对聚合结果做：月度行数很少（城市 × 月 × 品名 × 规格 × 牌号，
        /// 通常几十到几百行），内存筛选更直接且便于组合多选条件。
        /// </summary>
        /// <param name="city">城市拼音；null 或空表示全部城市。</param>
        /// <param name="yearMonth">年月 yyyy-MM；null 或空表示全部月份。</param>
        /// <param name="products">品名白名单；null 或空表示不限品名。</param>
        /// <param name="specs">规格白名单；null 或空表示不限规格（即「全选」）。</param>
        /// <param name="grades">牌号白名单；null 或空表示不限牌号。</param>
        /// <returns>按年月倒序、城市、品名、规格、牌号排序的月均价格行。</returns>
        public List<MonthlyPriceRow> QueryMonthlySummary(string city = null, string yearMonth = null,
                                                        ICollection<string> products = null,
                                                        ICollection<string> specs = null,
                                                        ICollection<string> grades = null)
        {
            IEnumerable<MonthlyPriceRow> rows = this.AggregateMonthly();

            if (!string.IsNullOrWhiteSpace(city))
            {
                string c = city.Trim();
                rows = rows.Where(r => r.City == c);
            }
            if (!string.IsNullOrWhiteSpace(yearMonth))
            {
                string ym = yearMonth.Trim();
                rows = rows.Where(r => r.YearMonth == ym);
            }

            // 白名单为空视为「不限」，避免调用方为「全选」额外造一份全集
            if (products is not null && products.Count > 0)
            {
                rows = rows.Where(r => products.Contains(r.ProductName));
            }
            if (specs is not null && specs.Count > 0)
            {
                rows = rows.Where(r => specs.Contains(r.Spec));
            }
            if (grades is not null && grades.Count > 0)
            {
                rows = rows.Where(r => grades.Contains(r.Grade));
            }

            return rows
                .OrderByDescending(r => r.YearMonth)
                .ThenBy(r => r.City)
                .ThenBy(r => r.ProductName)
                .ThenBy(r => r.Spec)
                .ThenBy(r => r.Grade)
                .ToList();
        }

        /// <summary>
        /// 全量月度聚合（按 城市 + 年月 + 品名 + 规格 + 牌号），用 LINQ 分组投影完成。
        /// 不走数据库视图，EF 会翻译成 GROUP BY + 聚合函数下推到 SQLite。
        /// </summary>
        private List<MonthlyPriceRow> AggregateMonthly()
        {
            return _context.SteelPrices
                .GroupBy(r => new
                {
                    r.City,
                    // QuoteDate 形如 2026-09-18，取前 7 位即 yyyy-MM
                    YearMonth = r.QuoteDate.Substring(0, 7),
                    r.ProductName,
                    r.Spec,
                    r.Grade
                })
                .Select(g => new MonthlyPriceRow
                {
                    City = g.Key.City,
                    YearMonth = g.Key.YearMonth,
                    ProductName = g.Key.ProductName,
                    Spec = g.Key.Spec,
                    Grade = g.Key.Grade,
                    DayCount = g.Select(r => r.QuoteDate).Distinct().LongCount(),
                    ItemCount = g.LongCount(),
                    MinPrice = g.Min(r => r.Price),
                    AvgPrice = Math.Round(g.Average(r => r.Price), 2),
                    MaxPrice = g.Max(r => r.Price)
                })
                .ToList();
        }

        /// <summary>
        /// 取月均价格的汇总统计：整体均价、涉及的月份数、城市数、品名数。
        /// 用于数据库页顶部的概览卡片。
        /// </summary>
        public (double AvgPrice, double MinPrice, double MaxPrice, int MonthCount, int CityCount, int ProductCount)
            QueryMonthlyOverview(string city = null, string yearMonth = null,
                                 ICollection<string> products = null,
                                 ICollection<string> specs = null,
                                 ICollection<string> grades = null)
        {
            List<MonthlyPriceRow> rows = this.QueryMonthlySummary(city, yearMonth, products, specs, grades);
            if (rows.Count == 0)
            {
                return (0, 0, 0, 0, 0, 0);
            }

            return (
                Math.Round(rows.Average(r => r.AvgPrice), 2),
                rows.Min(r => r.MinPrice),
                rows.Max(r => r.MaxPrice),
                rows.Select(r => r.YearMonth).Distinct().Count(),
                rows.Select(r => r.City).Distinct().Count(),
                rows.Select(r => r.ProductName).Distinct().Count());
        }

        /// <summary>列出库中已有的全部年月（yyyy-MM），倒序。</summary>
        public List<string> ListYearMonths()
        {
            return _context.SteelPrices
                .Select(r => r.QuoteDate.Substring(0, 7))
                .Distinct()
                .OrderByDescending(ym => ym)
                .ToList();
        }

        /// <summary>列出库中已有的全部品名。</summary>
        public List<string> ListProductNames()
        {
            return _context.SteelPrices
                .Select(r => r.ProductName)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }

        /// <summary>
        /// 列出库中已有的全部规格型号（如 φ6 / φ12）。
        /// 按「φ 后的数字」升序，避免字符串排序把 φ10 排到 φ6 前面。
        /// </summary>
        public List<string> ListSpecs()
        {
            List<string> raw = _context.SteelPrices
                .Select(r => r.Spec)
                .Distinct()
                .ToList();

            return raw
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderBy(SpecOrderKey)
                .ThenBy(SpecOrderKey2)
                .ThenBy(s => s)
                .ToList();
        }

        /// <summary>
        /// 规格排序主键：抽取字符串里的第一个数字（直径），用于 φ10 排在 φ6 之后。
        /// 抽不到数字时返回 double.MaxValue，让其排在最后。
        /// </summary>
        private static double SpecOrderKey(string spec)
        {
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(spec ?? string.Empty, @"\d+(\.\d+)?");
            return m.Success && double.TryParse(m.Value, out double v) ? v : double.MaxValue;
        }

        /// <summary>
        /// 规格排序次键：同直径下，常规货（无「定尺」标注）排在定尺货之前，
        /// 例如 φ20 在 φ20（定尺12m）之前。含「定尺」返回 1，否则返回 0。
        /// </summary>
        private static int SpecOrderKey2(string spec)
        {
            return (spec ?? string.Empty).Contains("定尺") ? 1 : 0;
        }

        /// <summary>列出库中已有的全部牌号（如 HRB400E / HPB300）。</summary>
        public List<string> ListGrades()
        {
            return _context.SteelPrices
                .Select(r => r.Grade)
                .Distinct()
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .OrderBy(g => g)
                .ToList();
        }

        /// <summary>列出库中已有的城市及其记录数、日期范围。</summary>
        public List<(string City, long Count, string MinDate, string MaxDate)> ListCities()
        {
            return _context.SteelPrices
                .GroupBy(r => r.City)
                .Select(g => new
                {
                    City = g.Key,
                    Count = g.LongCount(),
                    MinDate = g.Min(r => r.QuoteDate),
                    MaxDate = g.Max(r => r.QuoteDate)
                })
                .AsEnumerable()
                .Select(x => (x.City, x.Count, x.MinDate, x.MaxDate))
                .OrderBy(x => x.City)
                .ToList();
        }

        /// <summary>释放 EF Core 上下文。</summary>
        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
