<div align="center">
  <img src="KeyStats.Windows/KeyStats/Resources/Icons/tray-icon.png" width="80" height="80" alt="" />
  <h1>KeyStats</h1>
  <p><strong>Windows 键鼠统计 · 本地优先 · 不记录输入内容</strong></p>
  <p>
    <a href="https://github.com/kieng1997/keyStats/releases/latest"><img src="https://img.shields.io/github/v/release/kieng1997/keyStats?display_name=tag&amp;style=flat-square" alt="最新版本" /></a>
    <a href="https://github.com/kieng1997/keyStats/releases"><img src="https://img.shields.io/github/downloads/kieng1997/keyStats/total?style=flat-square" alt="Releases 累计下载量" /></a>
    <img src="https://img.shields.io/badge/Windows-10%201903%2B%20%7C%2011-0078D6?style=flat-square" alt="支持 Windows 10 1903+ 与 Windows 11" />
    <a href="./LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="MIT 许可证" /></a>
  </p>
  <p>
    <a href="https://github.com/kieng1997/keyStats/releases/latest"><strong>下载最新版</strong></a> ·
    <a href="#界面预览">界面预览</a> ·
    <a href="#隐私与数据">隐私与数据</a>
  </p>
</div>

KeyStats 常驻系统托盘，统计按键次数、鼠标点击次数、鼠标移动距离和滚动距离。

## 界面预览

点击托盘图标即可打开统计面板，在一个窗口中查看今日数据、按键排行、活跃应用和近期趋势：

<p align="center">
  <img src="docs/screenshots/tp.png" alt="托盘统计面板，汇总今日键鼠数据、按键分布、活跃应用与历史趋势" /><br />
  <sub><strong>托盘统计面板</strong> · 点击托盘图标打开，集中查看今日概览、按键排行、活跃应用与历史曲线</sub>
</p>

<p align="center">
  <img src="docs/screenshots/heatmap.png" alt="键盘热力图，用蓝色深浅区分按键次数" /><br />
  <sub><strong>键盘热力图</strong> · 看看哪些键最常用，支持按日期范围切换（演示数据）</sub>
</p>

<p align="center">
  <img src="docs/screenshots/overview.png" alt="今日统计面板，展示按键、鼠标点击、移动与滚动距离" /><br />
  <sub><strong>今日统计</strong> · 一眼看清键鼠用量和常用按键（演示数据）</sub>
</p>

## 核心功能

| 维度 | 能做什么 |
| --- | --- |
| 计数与速度 | 今日按键与鼠标点击次数（左键、右键、中键、侧键后退/前进）；KPS / CPS 的今日峰值与历史峰值。 |
| 距离与校准 | 鼠标移动距离、滚动距离；可通过鼠标距离校准和滚轮校准窗口按实际设备调整换算。 |
| 历史与图表 | 按键历史支持今日、近 7 天、近 30 天、全部范围的排行与饼图/柱状图；历史图表可在近 3 天、7 天、30 天、全部范围之间切换，查看按键、点击、移动距离和滚动距离的折线图或柱状图。 |
| 分布分析 | 键盘热力图支持今日、近 7 天、近 30 天、全部范围，平均值/总和两种口径（默认总和），按应用筛选仅适用于单日；另可查看各应用的按键与点击统计。 |
| 桌面体验 | 悬浮统计窗可选择主/次指标、单行/双行布局、字号，支持置顶与锁定位置；托盘常驻、开机启动、按键/点击阈值通知、主要窗口尺寸记忆。 |
| 界面与数据 | 自动跟随系统亮暗主题；语言支持跟随系统、简体中文、繁体中文和 English；支持 JSON 导入/导出统计数据。 |

## 下载与使用

- **系统要求**：Windows 10（1903+）或 Windows 11。上述系统已预装 .NET Framework 4.8，通常无需额外安装；更旧系统可升级系统或[手动安装 .NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)。
- **安装**：从[当前 Fork 的 Releases](https://github.com/kieng1997/keyStats/releases)下载构建包，解压后运行 `KeyStats.exe`。首次启动时 Windows 可能显示安全提示，确认后运行即可。
- **日常使用**：左键单击托盘图标打开统计面板；右键菜单可打开设置、按键历史、悬浮统计或退出。设置中可调整语言、通知阈值、鼠标/滚轮校准、悬浮统计以及数据导入导出和同步。
- **数据切换**：统计在本地午夜自动切换到新的一天，历史数据保留。

本文仅介绍当前 Fork 的 Windows 版本；上游 Scoop 或第三方移植版不属于本仓库的发布渠道。

<details>
<summary>开机自启与管理员权限（实现细节）</summary>

- 普通权限运行时，自启写入当前用户的 `HKCU\...\CurrentVersion\Run`，值名为 `KeyStats`。
- 以管理员身份运行时，改为注册「仅当前用户登录时触发、最高权限、交互式令牌」的计划任务 `KeyStats-<当前用户SID>`，并移除 Run 值，避免登录时启动两次。
- 任务名只含应用名与用户 SID，不含安装路径；同一用户安装多个副本时共用一个任务，最后一次成功配置的路径生效（同时启用自启时只会启动一个实例）。
- 已有管理员任务时，普通权限进程不会把它降级成 Run；如需更换路径，请以管理员身份运行一次 KeyStats。
- 取消勾选会清理 Run 并保存关闭意图，同时尝试删除计划任务；若删除因权限失败会明确提示，残留任务仍可能自启，需要以管理员身份重试。

</details>

## 隐私与数据

KeyStats **不会保存**输入的文字内容、按键顺序、鼠标光标与点击位置、原始移动轨迹或剪贴板内容。保存的是聚合数据：按键与组合键次数、鼠标点击次数、移动与滚动距离，以及按键和点击的应用归属。

数据默认保存在本机：

```text
%LOCALAPPDATA%\KeyStats\
├── daily_stats.json   当日统计
├── history.json       历史统计
└── settings.json      用户设置
```

KeyStats 不包含使用分析、遥测 SDK 或使用行为上报，也不会为使用分析收集或发送页面浏览、功能点击、随机安装 ID 或系统环境信息。本地聚合统计与设置照常保存在本机；可选端到端加密同步和手动检查更新仍有各自用途所需的网络通信。

### 可选同步

多设备同步默认关闭，只有主动配对并启用后才会联网上传。同步内容仅包括支持的按键/组合键次数和鼠标点击次数；鼠标移动距离、滚动距离和分应用统计不会同步。数据在本机加密后上传，服务端只保存 HMAC 和密文，无法读取明文统计、按键名称或本地日期。

参与同步需要设备配对或恢复密钥。具体规则见[同步协议说明](./contracts/sync/v1/README.md)和[同步服务端说明](./services/sync-worker/README.md)。

## 维护与开发

前置条件：Windows 与 .NET SDK（CI 使用 8.0.x 构建 `net48`）；同步服务端需要 Node.js 22。

```bash
cd KeyStats.Windows

dotnet build KeyStats/KeyStats.csproj -c Debug
dotnet build KeyStats/KeyStats.csproj -c Release
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj
```

测试覆盖同步协议、加密向量、调度、批处理与统计持久化命令；UI 部分仍为手动验证。也可用 Visual Studio 打开 `KeyStats.sln` 构建并按 F5 运行。

<details>
<summary>打包与同步服务配置</summary>

打包命令如下，输出位于 `publish/`，ZIP 位于 `dist/KeyStats-Windows-<版本>.zip`：

```bash
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

打包默认使用仓库中版本号最大的 `v*` 标签，显式传入的 `-Version` 优先。双击 `一键打包win.bat` 会自动请求管理员权限；`build.bat` 是保留的旧版批处理入口。

同步服务地址在构建时写入程序集：

```bash
# 打包脚本参数
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release -SyncServiceBaseUrl https://your-worker.workers.dev

# 或直接构建
dotnet build KeyStats/KeyStats.csproj -c Release /p:SyncServiceBaseUrl=https://your-worker.workers.dev
```

Debug 构建默认使用 staging 地址；Release 构建未提供 `SyncServiceBaseUrl` 时为空，此时同步网络功能不可用。请勿将 staging 地址用于正式发布。

</details>

<details>
<summary>目录结构</summary>

- `KeyStats.Windows/`：Windows 托盘应用（WPF，.NET Framework 4.8）及同步测试。
- `contracts/sync/v1/`：E2EE 同步协议（JSON Schema、OpenAPI、fixtures）。
- `services/sync-worker/`：E2EE 同步的 Cloudflare Worker 服务端。
- `scripts/`：发布与 Scoop 发布脚本。

Windows 应用源码位于 `KeyStats.Windows/KeyStats/`，按 `App.xaml(.cs)`、`Services/`、`ViewModels/`、`Views/`、`Models/` 和 `Helpers/` 组织。更多技术细节见 [`CLAUDE.md`](./CLAUDE.md)。

</details>

<details>
<summary>常见问题</summary>

- **应用无法启动？** 确认系统为 Windows 10（1903+）或 11；更旧系统需先升级或手动安装 .NET Framework 4.8。
- **统计数据丢失了？** 检查 `%LOCALAPPDATA%\KeyStats\` 下的 `daily_stats.json`、`history.json` 和 `settings.json` 是否存在。
- **如何卸载？** 删除程序文件夹即可，数据会保留在用户数据目录中。如需彻底清除，可手动删除 `%LOCALAPPDATA%\KeyStats\`。

</details>

## 致谢与许可证

本项目 Fork 自 [debugtheworldbot/keyStats](https://github.com/debugtheworldbot/keyStats)，在原项目基础上继续维护。感谢原作者及贡献者，保留原有版权声明与 MIT 许可证；详见 [LICENSE](./LICENSE)，原始项目版权归上游作者所有。
