using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace XBPrice
{
    /// <summary>
    /// 报价查询主页面。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly List<string> _cities = new List<string>
        {
            "shanghai", "beijing", "chengdo", "nanjing", "hangzhou"
        };

        /// <summary>开始日期默认往前推的月数。</summary>
        private const int DefaultRangeMonths = 1;

        /// <summary>默认选中的城市下标（对应 _cities，2 = "chengdo"）。</summary>
        private const int DefaultCityIndex = 2;

        /// <summary>最近一次查询结果对应的 CSV 文本。</summary>
        private string _csv;

        public MainPage()
        {
            this.InitializeComponent();

            // 给日期一个默认区间：否则 DatePicker 未选择时会返回 DateTimeOffset.MinValue，
            // 拼出 00010101 的非法网址导致服务端报错
            DateTimeOffset today = DateTimeOffset.Now;
            this.startDatePicker.Date = ToPreviousWorkday(today.AddMonths(-DefaultRangeMonths)); // start = 一个月前的工作日
            this.endDatePicker.Date = today;

            // 下拉框的默认选中与预览页面共用同一个索引，避免两处各写各的出现不一致
            int cityIndex = DefaultCityIndex >= 0 && DefaultCityIndex < _cities.Count ? DefaultCityIndex : 0;
            this.cityPicker.SelectedIndex = cityIndex;

            this.webview.Source = new Uri(new MyWebsite(_cities[cityIndex], today).Url);
        }

        /// <summary>可选城市列表（供 ComboBox 绑定）。</summary>
        public IReadOnlyList<string> Cities => _cities;

        /// <summary>查询结果（供 GridView 绑定）。</summary>
        public ObservableCollection<ColumnData> Model { get; } = new ObservableCollection<ColumnData>();

        /// <summary>抓取数据。</summary>
        private async void FetchButton_Click(object sender, RoutedEventArgs e)
        {
            string city = this.GetSelectedCity();
            if (string.IsNullOrWhiteSpace(city))
            {
                this.ShowStatus("请输入或选择城市", InfoBarSeverity.Warning);
                return;
            }

            this.statusBar.IsOpen = false;
            this.SetBusy(true);

            try
            {
                DateTimeOffset start = GetActualDate(this.startDatePicker);
                DateTimeOffset end = GetActualDate(this.endDatePicker);

                List<MyWebsite> websites = MyWebsite.GetWebsites(city, start, end);
                if (websites.Count == 0)
                {
                    this.ShowStatus("所选日期范围内没有工作日", InfoBarSeverity.Warning);
                    return;
                }

                // 确认存在工作日后才导航，避免把周末或非法日期页面加载进来
                this.webview.Source = new Uri(websites[0].Url);

                // 第一个工作日的页面只下载一次，一次取出第 1~4 列；
                // 其余工作日并发抓第 4 列。两者并行执行，与原来相比少 2 次请求且不再串行等待。
                Task<List<ColumnData>> firstPage = MyStrHandle.GetColumnsFromPage(websites[0], 1, 2, 3, 4);
                Task<List<ColumnData>> otherPages = websites.Count > 1
                    ? MyStrHandle.GetColumn(websites.Skip(1).ToList(), 4)
                    : Task.FromResult(new List<ColumnData>());
                await Task.WhenAll(firstPage, otherPages);

                List<ColumnData> rows = new List<ColumnData>(firstPage.Result);
                rows.AddRange(otherPages.Result);

                this.Model.Clear();
                StringBuilder csv = new StringBuilder();
                foreach (ColumnData row in rows)
                {
                    this.Model.Add(row);
                    csv.Append(row.DateText).Append(',')
                       .Append(row.Values.Replace('\n', ',')).Append('\n');
                }
                this._csv = csv.ToString();

                this.ShowStatus($"已获取 {this.Model.Count} 条记录", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                this.ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                this.SetBusy(false);
                this.UpdateEmptyHint();
            }
        }

        /// <summary>把结果导出为 CSV 文件。</summary>
        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            FileSavePicker picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "报价数据"
            };
            picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });

            // 免打包应用必须为拾取器指定窗口句柄 (HWND)，否则调用会失败
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            try
            {
                // Excel 按本地编码解析 CSV，必须写入 UTF-8 BOM 才不会乱码
                byte[] bom = Encoding.UTF8.GetPreamble();
                byte[] body = Encoding.UTF8.GetBytes(this._csv ?? string.Empty);
                byte[] bytes = new byte[bom.Length + body.Length];
                Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
                Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
                await FileIO.WriteBytesAsync(file, bytes);
                this.ShowStatus($"已导出到 {file.Name}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                this.ShowStatus("导出失败：" + ex.Message, InfoBarSeverity.Error);
            }
        }

        /// <summary>在“选择城市”和“手动输入拼音”之间切换。</summary>
        private void InputModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool manual = this.inputModeToggle.IsOn;
            this.cityInput.Text = string.Empty;
            this.cityInput.IsEnabled = manual;
            this.cityPicker.IsEnabled = !manual;
        }

        /// <summary>DatePicker 未选择日期时 Date 会返回 DateTimeOffset.MinValue，这里兜底成今天。</summary>
        private static DateTimeOffset ToPreviousWorkday(DateTimeOffset d)
        {
            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
            {
                d = d.AddDays(-1);
            }
            return d;
        }

        private static DateTimeOffset GetActualDate(DatePicker picker)
        {
            return picker.Date == DateTimeOffset.MinValue ? DateTimeOffset.Now : picker.Date;
        }

        private string GetSelectedCity()
        {
            string city = this.inputModeToggle.IsOn
                ? this.cityInput.Text
                : this.cityPicker.SelectedItem as string;

            return city?.Trim();
        }

        /// <summary>忙碌时禁用所有输入，避免重复点击。</summary>
        private void SetBusy(bool busy)
        {
            this.ring.IsActive = busy;
            this.ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            this.inputModeToggle.IsEnabled = !busy;
            this.cityInput.IsEnabled = !busy && this.inputModeToggle.IsOn;
            this.cityPicker.IsEnabled = !busy && !this.inputModeToggle.IsOn;
            this.startDatePicker.IsEnabled = !busy;
            this.endDatePicker.IsEnabled = !busy;
            this.fetchButton.IsEnabled = !busy;
            this.exportButton.IsEnabled = !busy && this.Model.Count > 0;
        }

        private void UpdateEmptyHint()
        {
            this.emptyHint.Visibility = this.Model.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>用 InfoBar 展示状态，替代会阻塞界面的弹窗。</summary>
        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            this.statusBar.Severity = severity;
            this.statusBar.Message = message;
            this.statusBar.IsOpen = true;
        }
    }
}
