# KeyStats - Windows 键鼠统计托盘应用

> 本项目是 [debugtheworldbot/keyStats](https://github.com/debugtheworldbot/keyStats) 的 Fork，在上游基础上继续维护与调整。感谢原作者及贡献者，保留原有版权声明与 MIT 许可证。
> 上游仓库：<https://github.com/debugtheworldbot/keyStats>
> 当前 Fork 仓库：<https://github.com/kieng1997/keyStats>

KeyStats 是一个轻量的 Windows 系统托盘应用，用于统计键盘和鼠标的使用量：按键次数、鼠标点击次数、鼠标移动距离和滚动距离。它只保存聚合统计数据，不记录输入内容。

## 功能

- **键鼠总计**：今日按键次数、鼠标点击次数（左键、右键、中键、侧键后退/前进）。
- **距离与滚动**：鼠标移动距离、滚动距离；提供鼠标距离校准和滚轮校准窗口，按实际设备调整换算。
- **速度峰值**：KPS（每秒按键）与 CPS（每秒点击），同时显示今日峰值和历史峰值。
- **按键历史**：按今日、近 7 天、近 30 天、全部范围查看按键排行，提供饼图与柱状图。
- **历史图表**：在近 3 天、7 天、30 天、全部范围之间切换，查看按键、点击、移动距离、滚动距离的折线图或柱状图。
- **键盘热力图**：今日、近 7 天、近 30 天、全部范围；支持平均值/总和（默认总和）；按应用筛选仅适用于单日。
- **分应用统计**：查看各应用对应的按键与点击数据。
- **悬浮统计**：桌面悬浮窗，可选择主/次指标、单行/双行布局、字号，支持置顶和锁定位置。
- **界面**：自动跟随系统亮暗主题；界面语言支持跟随系统、简体中文、繁体中文、English。
- **日常使用**：托盘常驻、开机启动、按键/点击阈值通知、主要窗口尺寸记忆。
- **数据管理**：以 JSON 格式导入/导出统计数据。

## 系统要求

- Windows 10 (1903+) 或 Windows 11。
- .NET Framework 4.8（上述系统已预装，通常无需额外安装）。若系统版本较旧，可升级系统或[手动安装 .NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)。

## 安装与运行

从当前 Fork 的 Releases 页面下载构建包：

<https://github.com/kieng1997/keyStats/releases>

解压后运行 `KeyStats.exe`，程序会常驻系统托盘并自动开始统计。首次启动时 Windows 可能显示安全提示，确认后运行即可。

> 本文仅介绍当前 Fork 的 Windows 版本；上游 Scoop 或第三方移植版不属于本仓库的发布渠道。

## 使用

- 左键单击托盘图标打开统计面板；右键菜单可打开设置、按键历史、悬浮统计或退出程序。
- 统计面板展示今日按键、点击、移动距离、滚动距离，以及按键分布和历史图表。
- 设置窗口可调整悬浮统计、语言、通知阈值、鼠标/滚轮校准，并执行数据导入导出和同步设置。
- 所有窗口自动跟随系统亮暗主题，主要窗口会记住上次的尺寸。
- 统计数据在本地午夜自动切换新的一天，历史数据保留。

### 开机自启与管理员权限

- 普通权限运行时，自启写入当前用户的 `HKCU\...\CurrentVersion\Run`（值名 `KeyStats`）。
- 以管理员身份运行时，改为注册「仅当前用户登录时触发、最高权限、交互式令牌」的计划任务 `KeyStats-<当前用户SID>`，并移除 Run 值，避免登录时启动两次。
- 任务名只含应用名与用户 SID，不含安装路径；同一用户安装多个副本时共用一个任务，最后一次成功配置的路径生效（同时启用自启时只会启动一个实例）。
- 已有管理员任务时，普通权限进程不会把它降级成 Run；如需更换路径，请以管理员身份运行一次 KeyStats。
- 取消勾选会清理 Run 并保存关闭意图，同时尝试删除计划任务；若删除因权限失败会明确提示，残留任务仍可能自启，需要管理员身份重试。

## 隐私与数据

KeyStats **不会记录**：

- 输入的文字内容或按键顺序；
- 鼠标光标位置、点击位置或原始移动轨迹；
- 剪贴板内容。

实际保存的是聚合数据：按键与组合键次数、鼠标按键点击次数、移动/滚动距离，以及按键统计对应的应用归属。

数据默认保存在本机：

```
%LOCALAPPDATA%\KeyStats\
├── daily_stats.json   当日统计
├── history.json       历史统计
└── settings.json      用户设置
```

### 不进行使用分析

KeyStats 不包含使用分析、遥测 SDK 或使用行为上报，也不会为使用分析收集或发送页面浏览、功能点击、随机安装 ID 或系统环境信息。本地聚合统计与设置照常保存在本机；可选端到端加密同步和手动检查更新仍有各自用途所需的网络通信。

### 可选同步

多设备同步默认关闭，只有用户主动配对并启用后才会联网上传。同步内容仅包括支持的按键/组合键次数和鼠标点击次数；鼠标移动距离、滚动距离和分应用统计不会同步。数据在本机加密后上传，服务端只保存 HMAC 和密文，无法读取明文统计、按键名称或本地日期。

参与同步需要设备配对或恢复密钥。具体规则见 [同步协议说明](./contracts/sync/v1/README.md) 和 [同步服务端说明](./services/sync-worker/README.md)。

## 开发

前置条件：Windows、.NET SDK（CI 使用 8.0.x 构建 `net48`）；同步服务端需要 Node.js 22。

```bash
cd KeyStats.Windows

# Debug / Release 构建
dotnet build KeyStats/KeyStats.csproj -c Debug
dotnet build KeyStats/KeyStats.csproj -c Release

# 自动化测试（同步协议、加密向量、调度、持久化）
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj

# 打包发布，输出 dist/KeyStats-Windows-<版本>.zip；双击会请求管理员权限
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release

# 也可使用 Visual Studio 打开 KeyStats.sln 构建，按 F5 运行
```

打包使用仓库中版本号最大的 `v*` 标签；显式传入的 `-Version` 优先。输出位于 `publish/`，ZIP 位于 `dist/KeyStats-Windows-<版本>.zip`。`build.bat` 是保留的旧版批处理入口，日常打包使用 `一键打包win.bat`。

测试覆盖同步协议、加密向量、调度、批处理与统计持久化命令；UI 部分仍为手动验证。

### 同步服务地址

同步服务地址在构建时写入程序集：

```bash
# 打包脚本参数
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release -SyncServiceBaseUrl https://your-worker.workers.dev

# 或直接构建
dotnet build KeyStats/KeyStats.csproj -c Release /p:SyncServiceBaseUrl=https://your-worker.workers.dev
```

Debug 构建默认使用 staging 地址；Release 构建未提供 `SyncServiceBaseUrl` 时为空，此时同步网络功能不可用。请勿将 staging 地址用于正式发布。

### 目录结构

```
KeyStats.Windows/       Windows 托盘应用（WPF，.NET Framework 4.8）及同步测试
contracts/sync/v1/      E2EE 同步协议（JSON Schema、OpenAPI、fixtures）
services/sync-worker/   E2EE 同步的 Cloudflare Worker 服务端
scripts/                发布与 Scoop 发布脚本
```

Windows 应用源码位于 `KeyStats.Windows/KeyStats/`：

```text
KeyStats/
├── App.xaml(.cs)                 应用入口与组合根
├── Services/                     输入监控、统计管理、通知、启动项、备份与同步
├── ViewModels/                   托盘、统计弹窗、悬浮窗、历史、应用统计等视图模型
├── Views/                        统计弹窗、设置、同步、历史、热力图、校准等窗口
├── Models/                       日报、设置、应用统计与同步模型
└── Helpers/                      Windows API、键名映射、主题、本地化与 XAML 转换器
```

更多技术细节见 [`CLAUDE.md`](./CLAUDE.md)。

## 常见问题

### Q: 应用无法启动？
A: 确保 Windows 版本为 10 (1903+) 或 11；更旧系统需先升级或手动安装 .NET Framework 4.8。

### Q: 统计数据丢失了？
A: 数据存储在 `%LOCALAPPDATA%\KeyStats\`，检查 `daily_stats.json`、`history.json` 和 `settings.json` 是否存在。

### Q: 如何卸载？
A: 删除程序文件夹即可。数据会保留在用户数据目录中；如需彻底清除，可手动删除 `%LOCALAPPDATA%\KeyStats\`。

## 许可证

MIT License，见 [LICENSE](./LICENSE)。原始项目版权归上游作者所有。
