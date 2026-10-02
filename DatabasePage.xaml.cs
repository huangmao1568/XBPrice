using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace XBPrice
{
    /// <summary>
    /// 数据库页：月均价格看板。
    ///
    /// 数据来自 <see cref="SteelPriceRepository.QueryMonthlySummary"/>，
    /// 按 城市 + 年月 + 品名 + 规格 + 牌号 用 LINQ 聚合出月均价与价格区间。
    ///
    /// 默认筛选：品名 = 螺纹钢，牌号 = HRB400E，规格 = 全选；
    /// 若结果为空（该组合在库中不存在），自动放宽为「牌号不限」兜底，保证有数据可看。
    /// </summary>
    public sealed partial class DatabasePage : Page
    {
        /// <summary>下拉框「全部」选项的显示文本。</summary>
        private const string AllOption = "全部";

        /// <summary>默认品名。</summary>
        private const string DefaultProduct = "螺纹钢";

        /// <summary>默认牌号。</summary>
        private const string DefaultGrade = "HRB400E";

        /// <summary>数据仓储。与主页面各自持有一个实例，互不干扰。</summary>
        private readonly SteelPriceRepository _repository = new SteelPriceRepository();

        /// <summary>是否已加载过一轮（避免下拉框填充时反复触发查询）。</summary>
        private bool _loaded;

        /// <summary>
        /// 当前视图模式：true = 月均汇总，false = 原始数据明细。
        /// 与界面上的两个 ToggleButton 互斥联动。
        /// </summary>
        private bool _summaryMode = true;

        public DatabasePage()
        {
            this.InitializeComponent();
            this.Loaded += this.DatabasePage_Loaded;
        }

        /// <summary>月均汇总表数据源。</summary>
        public ObservableCollection<MonthlyRow> Rows { get; } = new ObservableCollection<MonthlyRow>();

        /// <summary>原始数据表数据源。</summary>
        public ObservableCollection<RawRow> RawRows { get; } = new ObservableCollection<RawRow>();

        /// <summary>进入页面后加载筛选选项与首屏数据。</summary>
        private void DatabasePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (this._loaded)
            {
                return;
            }

            this._loaded = true;

            // 显示数据库完整路径，并额外标注存储位置类型。
            // 数据库固定放在「文档」目录下（独立于 MSIX 包沙箱），
            // 卸载 / 重新部署应用都不会删除该文件，便于用户自行备份。
            string path = this._repository.DatabasePath;
            this.dbPathText.Text = $"{path}   （存于文档目录，卸载应用不会丢失）";
            this.dbPathText.Tag = path;
            this.PopulateFilters();
            this.Reload();
        }

        /// <summary>
        /// 填充筛选控件：城市 / 月份 / 品名 / 牌号 / 规格 一律为单选，首项均为「全部」。
        /// 同时把默认值设好（品名=螺纹钢、牌号=HRB400E、规格=全部）。
        /// </summary>
        private void PopulateFilters()
        {
            // 填充期间会触发 SelectionChanged，先重置标志避免中途查询
            bool prev = this._loaded;
            this._loaded = false;

            List<string> cities = this._repository.ListCities()
                .Select(c => c.City)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .OrderBy(c => c)
                .ToList();

            List<string> months = this._repository.ListYearMonths();
            List<string> products = this._repository.ListProductNames();
            List<string> specs = this._repository.ListSpecs();
            List<string> grades = this._repository.ListGrades();

            this.FillSingle(this.cityFilter, cities);
            this.FillSingle(this.monthFilter, months);
            this.FillSingle(this.productFilter, products);
            this.FillSingle(this.gradeFilter, grades);
            this.FillSingle(this.specFilter, specs);

            // 默认：城市=全部、月份=全部、规格=全部；
            //       品名=螺纹钢、牌号=HRB400E（库里没有该值则回落到「全部」）
            this.cityFilter.SelectedIndex = 0;
            this.monthFilter.SelectedIndex = 0;
            this.specFilter.SelectedIndex = 0;
            this.productFilter.SelectedIndex = products.Contains(DefaultProduct)
                ? products.IndexOf(DefaultProduct) + 1
                : 0;
            this.gradeFilter.SelectedIndex = grades.Contains(DefaultGrade)
                ? grades.IndexOf(DefaultGrade) + 1
                : 0;

            this._loaded = prev;
        }

        /// <summary>
        /// 把一组选项填入单选下拉框：首项固定为「全部」，其后追加实际选项。
        /// 五个筛选框共用此方法，保证交互与样式完全一致。
        /// </summary>
        private void FillSingle(ComboBox box, List<string> values)
        {
            box.Items.Clear();
            box.Items.Add(AllOption);
            foreach (string v in values)
            {
                box.Items.Add(v);
            }
        }

        /// <summary>筛选变化时重新查询。</summary>
        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!this._loaded)
            {
                return;
            }

            this.Reload();
        }

        /// <summary>手动刷新。</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            this.PopulateFilters();
            this.Reload();
        }

        /// <summary>返回主页面。</summary>
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame is not null && this.Frame.CanGoBack)
            {
                this.Frame.GoBack();
            }
        }

        /// <summary>按当前筛选读取月均价格并刷新界面。</summary>
        private void Reload()
        {
            this.ring.IsActive = true;
            this.ring.Visibility = Visibility.Visible;

            try
            {
                string city = this.SelectedValue(this.cityFilter);
                string month = this.SelectedValue(this.monthFilter);
                string product = this.SelectedValue(this.productFilter);
                string grade = this.SelectedValue(this.gradeFilter);
                string spec = this.SelectedValue(this.specFilter);

                // 五个筛选框统一为单选：选到「全部」即不过滤（传 null），否则精确匹配单项。
                List<string> products = ToFilter(product);
                List<string> grades = ToFilter(grade);
                List<string> specs = ToFilter(spec);

                // 原始数据模式：不做任何聚合，直接列库中的每一条报价。
                if (!this._summaryMode)
                {
                    this.ReloadRaw(city, month, products, specs, grades);
                    return;
                }

                List<MonthlyPriceRow> data =
                    this._repository.QueryMonthlySummary(city, month, products, specs, grades);

                // 降级兜底：默认的「螺纹钢 + HRB400E」组合在库里可能不存在，
                // 此时放宽牌号限制（规格仍按用户勾选），避免打开页面就是空表。
                string fallbackNote = null;
                if (data.Count == 0 && grades is not null && grades.Count > 0)
                {
                    List<MonthlyPriceRow> relaxed =
                        this._repository.QueryMonthlySummary(city, month, products, specs, null);

                    if (relaxed.Count > 0)
                    {
                        data = relaxed;
                        grades = null;
                        fallbackNote = $"当前筛选（牌号 {grade}）无结果，已忽略牌号限制，显示全部牌号的数据";
                    }
                }

                this.Rows.Clear();
                int index = 1;
                foreach (MonthlyPriceRow row in data)
                {
                    this.Rows.Add(new MonthlyRow
                    {
                        Index = index++,
                        YearMonth = row.YearMonth,
                        City = row.City,
                        ProductName = row.ProductName,
                        Spec = row.Spec,
                        Grade = row.Grade,
                        DayCount = row.DayCount,
                        ItemCount = row.ItemCount,
                        AvgPriceText = row.AvgPriceText,
                        RangeText = row.RangeText
                    });
                }

                this.UpdateOverview(city, month, products, specs, grades);
                this.rowCountText.Text = $"共 {this.Rows.Count} 行";
                // 左下角总条数：取汇总行覆盖的原始报价条数之和
                this.UpdateTotalCount(this.Rows.Sum(r => r.ItemCount));
                this.emptyPanel.Visibility = this.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (fallbackNote is not null)
                {
                    this.ShowStatus(fallbackNote, InfoBarSeverity.Warning);
                }
                else
                {
                    this.statusBar.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                this.ShowStatus("读取数据库失败：" + ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                this.ring.IsActive = false;
                this.ring.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 原始数据模式：按当前筛选直接列出库中每一条报价，不做聚合、不体现均价。
        /// 顶部概览卡片改用原始条数口径重新计算，避免与明细行数口径矛盾。
        /// </summary>
        private void ReloadRaw(string city, string month,
                              List<string> products, List<string> specs, List<string> grades)
        {
            List<SteelPriceRecord> records =
                this._repository.QueryRawRecords(city, month, products, specs, grades);

            this.RawRows.Clear();
            int index = 1;
            foreach (SteelPriceRecord r in records)
            {
                this.RawRows.Add(new RawRow
                {
                    Index = index++,
                    QuoteDate = r.QuoteDate,
                    City = r.City,
                    ProductName = r.ProductName,
                    Grade = r.Grade,
                    Spec = r.Spec,
                    PriceText = r.Price.ToString("N2")
                });
            }

            // 概览卡片按原始记录计算：均价 = 所有记录价格的算术平均
            bool hasData = records.Count > 0;
            this.kpiAvg.Text = hasData ? records.Average(r => r.Price).ToString("N2") : "—";
            this.kpiRange.Text = hasData
                ? $"{records.Min(r => r.Price):N0} ~ {records.Max(r => r.Price):N0}"
                : "—";
            this.kpiMonths.Text = records.Select(r => r.QuoteDate.Substring(0, 7)).Distinct().Count().ToString();
            this.kpiCities.Text = records.Select(r => r.City).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Count().ToString();
            this.kpiProducts.Text = records.Select(r => r.ProductName).Distinct().Count().ToString();

            this.rowCountText.Text = $"共 {this.RawRows.Count} 条原始记录";
            this.UpdateTotalCount(records.Count);
            this.rawEmptyPanel.Visibility = this.RawRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            this.statusBar.IsOpen = false;
        }

        /// <summary>
        /// 刷新左下角的「数据总条数」标签。
        /// 无论当前处于哪个视图，展示的都是**原始报价记录条数**（数据库中真实的报价行数），
        /// 月均汇总模式下即当前筛选覆盖到的明细条数合计。
        /// </summary>
        private void UpdateTotalCount(long total)
        {
            this.totalCountText.Text = $"数据总条数：{total:N0} 条";
        }

        /// <summary>切换视图模式：月均汇总 / 原始数据。</summary>
        private void ViewModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
            {
                return;
            }

            bool wantSummary = (clicked.Tag as string) == "summary";

            // ToggleButton 可以点回未选中状态，这里强制二者始终有一个处于选中
            this.summaryViewButton.IsChecked = wantSummary;
            this.rawViewButton.IsChecked = !wantSummary;
            this._summaryMode = wantSummary;

            this.summaryPanel.Visibility = wantSummary ? Visibility.Visible : Visibility.Collapsed;
            this.rawPanel.Visibility = wantSummary ? Visibility.Collapsed : Visibility.Visible;
            this.emptyPanel.Visibility = Visibility.Collapsed;
            this.rawEmptyPanel.Visibility = Visibility.Collapsed;

            this.listTitleText.Text = wantSummary ? "月均价明细" : "原始报价明细";

            this.Reload();
        }

        /// <summary>
        /// 把单个筛选值包装成仓储需要的列表参数。
        /// 返回 null 表示「不限」（未选或选到「全部」），否则为仅含该值的单元素列表。
        /// </summary>
        private static List<string> ToFilter(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new List<string> { value };
        }

        /// <summary>取下拉框当前选中的实际值；选到「全部」或未选时返回 null。</summary>
        private string SelectedValue(ComboBox box)
        {
            string value = box.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(value) || value == AllOption)
            {
                return null;
            }

            return value;
        }

        /// <summary>刷新顶部概览卡片。</summary>
        private void UpdateOverview(string city, string month,
                                    List<string> products, List<string> specs, List<string> grades)
        {
            (double avg, double min, double max, int months, int cities, int productCount) =
                this._repository.QueryMonthlyOverview(city, month, products, specs, grades);

            bool hasData = months > 0;

            this.kpiAvg.Text = hasData ? avg.ToString("N2") : "—";
            this.kpiRange.Text = hasData ? $"{min:N0} ~ {max:N0}" : "—";
            this.kpiMonths.Text = months.ToString();
            this.kpiCities.Text = cities.ToString();
            this.kpiProducts.Text = productCount.ToString();
        }

        /// <summary>用 InfoBar 展示状态。</summary>
        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            this.statusBar.Severity = severity;
            this.statusBar.Message = message;
            this.statusBar.IsOpen = true;
        }
    }
}
