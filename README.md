# XBPrice —— 钢市报价抓取与查询工具

基于 **WinUI 3 / Windows App SDK** 的桌面应用：按工作日抓取西本新干线（steelx2.com）各城市钢材报价网页，
解析入库到本地 SQLite，并提供月均价格看板、涨跌对照与 CSV 导出。

打包形态为**单工程 MSIX**（Microsoft Store 打包应用），可侧载安装，也可直接提交合作伙伴中心上架。

---

## 一、功能概览

### 1. 报价查询（MainPage）

- **城市选择**：内置 `shanghai` / `beijing` / `chengdo` / `nanjing` / `hangzhou` 五个城市，
  也可切换到「手动输入」自行填写城市拼音标识。
- **日期区间**：默认「一个月前的工作日 ~ 今天」；只抓取周一至周五，自动跳过周末。
- **网页预览**：内嵌 WebView2 显示报价原页面，支持放大 / 缩小 / **整页适配**
  （首次加载后自动按预览区宽度算出合适缩放比例，页面横向铺满不留白边）。
- **一键抓取**：并发下载区间内全部工作日的报价表，解析后**幂等入库**
  （同城市 + 同日期 + 同品名 + 同规格 + 同牌号只保留一行，重复抓取只刷新价格）。
  **逐日容错**：个别日期失败不中断整批，失败日期会在状态栏与抓取日志中列出。
- **结果表格**：日期、品名 + 规格、牌号、价格、涨跌。
  **涨跌按国内行情惯例着色：涨红、跌绿、持平灰**，且随系统浅色 / 深色主题自动取色。
- **导出 CSV**：写入 UTF-8 **BOM**，Excel 直接打开不乱码；表头为
  `报价日期,城市,品名,规格型号,牌号,价格(元/吨)`。

### 2. 数据库看板（DatabasePage）

- **双视图切换**：`月均汇总`（按月聚合均价与价格区间）/ `原始数据`（逐条列明细，不做聚合）。
- **五维筛选**：城市 / 月份 / 品名 / 规格 / 牌号，全部为单选，首项均为「全部」。
  默认 `品名 = 螺纹钢`、`牌号 = HRB400E`、其余「全部」。
- **空结果兜底**：默认组合在库中不存在时，自动放宽牌号限制并给出提示，避免一打开就是空表。
- **概览卡片**：整体均价、价格区间、月份数、城市数、品名数；左下角显示当前筛选覆盖的**原始报价条数**。
- **数据库路径回显**：页面顶部显示 `.db` 文件完整路径，便于备份与排查。

---

## 二、技术栈

| 项目 | 选型 |
| --- | --- |
| 界面框架 | WinUI 3（Windows App SDK **2.5.1**），Fluent 2 设计令牌 |
| 目标框架 | `net10.0-windows10.0.26100.0`，最低运行 `10.0.17763.0`（Win10 1809） |
| 语言 | C#（`ImplicitUsings` 关闭，`LangVersion` latest） |
| 数据访问 | EF Core + SQLite（`Microsoft.EntityFrameworkCore.Sqlite` 10.0.12） |
| 网页预览 | WebView2（随 Windows App SDK 一并提供） |
| 控件扩展 | `CommunityToolkit.WinUI.Controls.Sizers` 8.2（GridSplitter） |
| 打包 | MSIX 单工程（`Microsoft.Windows.SDK.BuildTools.MSIX` 1.7），架构 `x86 / x64 / ARM64` |

> 说明：界面中间语言为简体中文（`DefaultLanguage = zh-CN`）。

---

## 三、项目结构

```
xbprice/
├─ App.xaml / App.xaml.cs          应用入口：亚克力背景、按屏幕自适应窗口、Frame 导航
├─ MainPage.xaml(.cs)              报价查询页：抓取、预览缩放、结果表格、CSV 导出
├─ DatabasePage.xaml(.cs)          数据库页：月均汇总 / 原始数据双视图看板
├─ Entities.cs                     EF 实体：SteelPriceRecord / ProductCategory / FetchLog
├─ ViewModels.cs                   数据模型：DailySummary / MonthlyPriceRow / SaveResult
├─ UiModels.cs                     界面绑定模型：MonthlyRow / RawRow（含刷子的类型隔离）
├─ Model.cs                        表格模型：ColumnData / QuoteRow（涨跌刷子）
├─ SteelPriceFetcher.cs            抓取与解析：网址生成 + 并发下载（连接池 / 重试）+ HTML 表格逐行解析
├─ SteelPriceRepository.cs         数据访问层：幂等 upsert + 各类查询聚合
├─ XbPriceDbContext.cs             DbContext：建表、索引、种子数据
├─ Themes/ThemeTokens.xaml         设计令牌（浅/深色涨跌色等）
├─ Assets/                         图标与磁贴素材（Tiles/、price.ico、price.png）
├─ Package.appxmanifest            MSIX 清单：标识、磁贴、runFullTrust + internetClient
├─ XBPrice.csproj / XBPrice.sln    工程与解决方案
└─ app.manifest                    应用清单（DPI 感知等）
```

---

## 四、数据存储

**数据库文件位置：**

```
%USERPROFILE%\Documents\XBPrice\XBPrice.db
```

> **为什么不放在 `%LOCALAPPDATA%`：** 打包（MSIX）运行时 `%LOCALAPPDATA%` 会被重定向到包沙箱
> （`...\Packages\<包族名>\LocalCache\Local\`），每次重新部署（`Remove-AppxPackage` + `Add-AppxPackage`）
> 都会清空该沙箱，已抓取数据全部丢失。放在「文档」目录可跨部署持久保留，也便于用户直接备份查看。
> 程序启动时还会做一次性搬迁：若检测到旧的包沙箱数据库而新位置尚无数据，自动复制过来，保证升级不丢数据。

**三张表：**

| 表 | 说明 |
| --- | --- |
| `SteelPrice` | 报价明细。一条 = 某城市 + 某报价日 + 某条钢材规格的价格 |
| `ProductCategory` | 品名分类维度（高线→线材、螺纹钢→螺纹钢、盘螺、圆钢、焊管→管材） |
| `FetchLog` | 抓取批次日志：日期区间、城市、条数、新增 / 更新数、成败与错误信息 |

**关键索引：**

- 唯一索引 `(City, QuoteDate, ProductName, Spec, Grade)` —— 保证重复抓取幂等；
- 普通索引 `(City, QuoteDate)` —— 按城市 + 日期区间查询；
- 普通索引 `(ProductName, Spec)` —— 同规格跨日期比价。

**聚合方式：** 日汇总、月均一律用 **LINQ 分组投影**在查询时完成（EF 翻译成 `GROUP BY` + 聚合函数下推到 SQLite），
**不建数据库视图** —— 视图定义变更需 `DROP + CREATE`，且改列要同时改 SQL 与实体映射两处。

---

## 五、抓取与解析要点

**网址模板**（城市拼音 + `yyyyMMdd`）：

```
http://{city}.steelx2.com/city/Quotation/quotation/1/{yyyyMMdd}/index.html
```

**解析策略：按 `<tr>` 逐行处理**，按行内实际列数判断：

- 5 列 = 品名 / 规格 / 牌号 / 价格 / **优质品牌推荐**（第 5 列丢弃）
- 4 列 = 品名 / 规格 / 牌号 / 价格

> **踩坑记录：** 旧版实现「提取全部 `<td>` 后按固定步长 4 取值」，但每个品类的**第一条规格**会多出一列
> 「优质品牌推荐」，导致行内列数 5/4 混排，从第二条起整列错位。按行解析后彻底修正。

**其他细节：**

- 正文锚点截断：取「总机服务」之前的内容，剔除页脚；
- 只取第一张含「品名」表头的表格；
- **规格归一化**：`φ20*12` 是「直径 × 定尺米数」而非管材的「外径 × 壁厚」，
  统一改写为 `φ20（定尺12m）`，消除歧义；排序时常规货排在定尺货之前；
- 价格合法区间 `100 ~ 100000` 元/吨，越界视为解析异常丢弃；
- **抓取性能**：并发上限 **8**（`SemaphoreSlim`）。实测同一城市连续工作日页面：
  并发 4 → 8.2 页/秒，并发 8 → **14.3 页/秒**，并发 16 → 15.4 页/秒
  （单页耗时由 0.56s 涨到 1.04s），故 8 是「收益 / 对端压力」的平衡点。
  共用单个 `HttpClient`，连接池上限与并发数对齐、连接 5 分钟定期重建、
  开启 gzip 压缩，超时 20 秒；
- **瞬时故障自动重试**：超时 / 连接失败 / 5xx / 429 最多尝试 3 次
  （退避 400ms → 1200ms + 0~150ms 随机抖动），4xx 属确定性失败不重试；
- **逐日容错**：个别日期抓取失败不会中断整批，成功的天照常入库，
  失败日期在状态栏与抓取日志（`FetchLog.Status = 部分失败`）中列出，便于只补抓那几天；
- 支持 `"3780"` / `"3,780"` / `"3780元/吨"` 等价格写法。

---

## 六、构建与运行

### 开发调试

用 Visual Studio 2022+ 打开 `XBPrice.sln`，部署配置选 **x64**，F5 直接运行（VS 会自动处理 MSIX 部署）。

### 命令行构建

```bash
dotnet build XBPrice.csproj -p:Platform=x64 -c Release
```

### 打包 MSIX（推荐用 VS 自带的 64 位 MSBuild）

```bash
& "$VS\MSBuild\Current\Bin\amd64\MSBuild.exe" XBPrice.csproj /restore `
    /p:Configuration=Release /p:Platform=x64 `
    /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=StoreAndSideload
```

产物（`AppPackages\` 下）：

- `XBPrice_<版本>_x64.msixupload` —— 直接上传合作伙伴中心
- `XBPrice_<版本>_x64_Test\` —— 侧载安装包 + `Add-AppDevPackage.ps1`

需同时支持 ARM64 时，把 `/p:Platform=x64` 换成 `/p:Platform=ARM64` 再执行一次，
两个 `.msixupload` 可在同一次提交中一起上传。

**两点注意事项：**

1. 使用 64 位 `MSBuild.exe` 是为了让它找到 VC 工具链里的 `mspdbcmf.exe`，
   从而一并生成商店崩溃分析所需的 `.appxsym` 符号包；
   若改用 `dotnet build` 打包，需加 `-p:AppxSymbolPackageEnabled=false`，否则符号包步骤报 `MSB6011`。
2. `PublishTrimmed` / `PublishReadyToRun` 固定关闭：前者会因 WinUI 3 依赖 XAML 反射导致运行时崩溃，
   后者在无 `RuntimeIdentifier` 时打包会报 `NETSDK1094`。

### 提交商店前需对齐

`Package.appxmanifest` 中的以下三项须与合作伙伴中心保持一致：

- `Identity / Name` ← 保留的应用名称
- `Identity / Publisher` ← `CN=xxxxxxxx-xxxx-...` 形式
- `Version` ← 每次提交必须递增（`1.0.0.0` → `1.0.1.0` …）

清单已声明 `runFullTrust`（WinUI 3 桌面应用必需，属受限能力，首次提交需在「提交选项」页说明用途）
与 `internetClient`（抓取报价网页）。

---

## 七、代码约定

- **中文注释**：所有类、方法、字段均带中文说明，重点写清「为什么这么选」而不只是「做了什么」。
- **界面类型与数据模型分离**：`UiModels.cs` / `Model.cs` 只放界面绑定类型（含 `SolidColorBrush`），
  数据层不依赖它们 —— 这样纯数据场景可只引用 `ViewModels.cs` 而无需引入 WinUI 依赖。
- **查询优先用 LINQ**：聚合、分组、筛选均以 LINQ 表达；仅在 LINQ 无法表达时才写原生 SQL 并注明原因。
- **容错不阻断**：主题色解析失败有兜底色、沙箱数据库搬迁失败静默跳过、缩放适配失败退回保守值，
  均保证主流程可用。

---

## 八、许可与归属

个人项目，作者 `huangdaxia`（包标识 `huangdaxia.XBPrice`）。
报价数据版权归数据来源网站所有，本工具仅用于个人学习与工作查价，请合理控制抓取频率。
