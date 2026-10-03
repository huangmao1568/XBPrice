using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

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

        /// <summary>
        /// 进入页面后加载筛选选项与首屏数据。
        ///
        /// 筛选下拉框在每次进入页面时都重新填充：主页面新抓取的数据可能带来
        /// 新的城市 / 规格 / 牌号，若只在首次加载时填充，用户得退出重进才能选到。
        /// 页面已在 Frame 中缓存，用 Navigate 重新创建实例即可触发本方法。
        /// </summary>
        private void DatabasePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (this._loaded)
            {
                return;
            }

            this._loaded = true;

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
                        MinPrice = row.MinPrice,
                        AvgPrice = row.AvgPrice,
                        MaxPrice = row.MaxPrice,
                        AvgPriceText = row.AvgPriceText,
                        RangeText = row.RangeText
                    });
                }

                this.UpdateOverview(city, month, products, specs, grades);
                this.rowCountText.Text = $"共 {this.Rows.Count} 行";
                // 左下角总条数：取汇总行覆盖的原始报价条数之和
                this.UpdateTotalCount(this.Rows.Sum(r => r.ItemCount));
                this.emptyPanel.Visibility = this.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                this.UpdateExportButtons();

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
                    Price = r.Price,
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
            this.UpdateExportButtons();
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

        // ============================================================
        // 导出 CSV
        // ============================================================

        /// <summary>
        /// 刷新导出按钮的可用状态：当前视图必须有数据才允许导出。
        /// </summary>
        private void UpdateExportButtons()
        {
            this.exportButton.IsEnabled = this._summaryMode
                ? this.Rows.Count > 0
                : this.RawRows.Count > 0;
        }

        /// <summary>
        /// 导出当前视图的数据为 CSV：
        /// 月均汇总视图导出聚合结果，原始数据视图导出每一条报价。
        /// 导出内容始终与屏幕所见一致——按当前视图分派，不给用户导出另一口径数据的机会。
        /// </summary>
        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            bool summary = this._summaryMode;
            int count = summary ? this.Rows.Count : this.RawRows.Count;

            if (count == 0)
            {
                this.ShowStatus(
                    summary ? "当前没有可导出的月均数据" : "当前没有可导出的原始报价数据",
                    InfoBarSeverity.Warning);
                return;
            }

            string prefix = summary ? "月均价" : "原始报价";

            // 弹出资源管理器的「另存为」对话框，让用户自选文件名与保存位置
            Windows.Storage.StorageFile file = await this.PickSaveFileAsync(prefix);
            if (file is null)
            {
                // 用户点了取消
                return;
            }

            try
            {
                string csv = summary
                    ? CsvExporter.BuildMonthlyCsv(this.Rows)
                    : CsvExporter.BuildRawCsv(this.RawRows);

                CsvExporter.WriteTo(file.Path, csv);

                string unit = summary ? "行" : "条";
                this.ShowStatus($"已导出 {count} {unit}到 {file.Path}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                this.ShowStatus("导出失败：" + ex.Message, InfoBarSeverity.Error);
            }
        }

        /// <summary>
        /// 弹出资源管理器的「另存为」对话框，让用户自己决定文件名和保存位置。
        /// 默认文件名带上当前筛选条件与时间戳（见 <see cref="DescribeFilters"/>），
        /// 用户仍可在对话框里随意改名或换目录。
        /// 用户取消时返回 null。
        /// </summary>
        private async Task<Windows.Storage.StorageFile> PickSaveFileAsync(string prefix)
        {
            FileSavePicker picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = CsvExporter.BuildFileName(prefix, this.DescribeFilters())
            };
            picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });

            // 免打包应用必须为拾取器指定窗口句柄 (HWND)，否则调用会失败
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            return await picker.PickSaveFileAsync();
        }

        /// <summary>
        /// 收集当前生效的筛选条件，用于生成可读的导出文件名。
        /// 未选（全部）的维度不参与，避免文件名被一堆「全部」污染。
        /// </summary>
        private List<string> DescribeFilters()
        {
            List<string> parts = new List<string>();

            void Add(string boxName, ComboBox box)
            {
                string value = this.SelectedValue(box);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }
            }

            Add("city", this.cityFilter);
            Add("month", this.monthFilter);
            Add("product", this.productFilter);
            Add("grade", this.gradeFilter);
            Add("spec", this.specFilter);

            return parts;
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
