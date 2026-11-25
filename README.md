# Plate Kinetics Live (BMG Omega)

[中文](#中文) · [English](#english)

## 中文

Plate Kinetics Live 是一个 Windows 程序：用 Omega 控制软件的 BMG LABTECH 读板仪（已验证 FLUOstar Omega，Omega 软件 6.20 和 6.40）
测量时，它在旁边**只读**仪器软件写下的数据文件，实时画出每个孔的动力学曲线，并把每次实验自动存成一个记录文件（`.pk`）和 Excel 表。
分析页面和网站 [Plate Kinetics Analysis](https://tools.fow.mobi/kinetics/) 是同一份。

- 不控制仪器、不写入或锁住仪器软件的文件；
- 不联网：网页服务只监听本机 `127.0.0.1:47815`，不收集任何统计；
- 安装版带一个后台服务（开机就记录，不用开窗口）和托盘通知；也有免安装的便携版。

> 仅供科研使用，不用于临床诊断。本项目独立开发，与 BMG LABTECH 无关，也没有得到它的认可。本仓库不含 BMG 的任何程序、库文件、说明书或数据文件；
> 使用时需要你自己合法安装的 Omega 软件。BMG LABTECH、Omega、FLUOstar、MARS 是 BMG LABTECH GmbH 的商标，这里只用来说明本程序读取哪个软件的文件。
> 详见 [help/NOTICE.txt](help/NOTICE.txt)。

### 编译

需要：Windows 10/11（自带 .NET Framework 4.8）和 [.NET SDK](https://dotnet.microsoft.com/download)（6 或更新，只用来编译）。
不需要联网下载任何包：WebView2 的三个 DLL 已经放在 `vendor\webview2\`（微软签名，1.0.4370.26）。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

成品在 `dist\`：

| 文件 | 用法 |
|---|---|
| `PlateKineticsLive-Setup.exe` | 安装版，双击安装（要管理员权限），默认装到 `C:\Program Files\Plate Kinetics Live (BMG)` |
| `PlateKineticsLive-portable.zip` | 便携版：解压后运行 `PlateKineticsLive.exe`，窗口开着时才记录 |

可选参数：`-Version 2.2.1.0`（版本号）；`-Offline`（另做离线包，要先把微软的 .NET 4.8 和 WebView2 离线安装程序放进 `redist\`）。

### 使用

1. 在装有 Omega 软件的电脑上安装（或解压便携版）并打开程序。
2. 开始测量后，程序自动找到正在跑的实验，显示实时曲线。
3. 每次实验的记录在数据文件夹 `PlateKineticsLive-data\Experiments\` 里（`.pk` 和 `.xlsx`），可以在程序里打开、标注样本、分组和导出。

Omega 软件不在默认位置时，用 `--bmg-root <文件夹>` 指定。其他命令行参数和详细说明见 [help/index.html](help/index.html)。

### 文件夹

| 位置 | 内容 |
|---|---|
| `src\Bmg\` | 读 Omega 的数据文件和数据库，整理实验记录 |
| `src\Common\` | 窗口（WebView2）、本机网页服务、后台服务、托盘、数据文件夹、.pk 和 Excel 文件 |
| `src\Setup\` | 安装程序（安装、更新、搬家、卸载） |
| `web\` | 界面：分析页面 `PlateKineticsAnalysis.html` 和外壳 `shell.js`、`shell.css`、`live.js` |
| `help\` | 说明书和许可文本（装到程序的 `Help\` 文件夹） |
| `vendor\webview2\` | 微软 WebView2 SDK 的 DLL 和它的许可 |
| `tools\make-icon.ps1` | 生成程序图标 `src\app.ico` |

## English

Plate Kinetics Live is a Windows program for BMG LABTECH plate readers run by the Omega control software (verified:
FLUOstar Omega with Omega 6.20 and 6.40). While the reader measures, it
**reads** (never writes or locks) the files the Omega software writes, draws each well's kinetic curve live, and
saves every run as a record (`.pk`) and an Excel workbook. The analysis page is the same as the website
[Plate Kinetics Analysis](https://tools.fow.mobi/kinetics/).

- It never controls the instrument and never changes the instrument software's files.
- No network use: its web server listens on `127.0.0.1:47815` only; no usage statistics.
- The installed version runs a background service (records from boot, no window needed) with tray notifications;
  a portable version needs no installation.

> For research use only, not for diagnostic use. Developed independently; not affiliated with or endorsed by
> BMG LABTECH. This repository contains no BMG program, library, manual or data file; it needs your own licensed
> installation of the Omega software. BMG LABTECH, Omega, FLUOstar and MARS are trademarks of BMG LABTECH GmbH, used
> here only to say whose software's files the program reads. See [help/NOTICE.txt](help/NOTICE.txt).

### Build

Needs Windows 10/11 (with its .NET Framework 4.8) and the [.NET SDK](https://dotnet.microsoft.com/download)
(6 or later, for building only). Nothing is downloaded: the WebView2 DLLs are in `vendor\webview2\`.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output in `dist\`: `PlateKineticsLive-Setup.exe` (installer, run as administrator) and
`PlateKineticsLive-portable.zip` (unzip, run `PlateKineticsLive.exe`). Options: `-Version <a.b.c.d>`,
`-Offline` (also an offline package; put Microsoft's .NET 4.8 and WebView2 offline installers in `redist\` first).

### Use

Install it on the PC with the Omega software and open it. When a measurement starts, the program finds the run and
shows live curves. Records are kept in `PlateKineticsLive-data\Experiments\`. Use `--bmg-root <folder>` when the
Omega software is not in its default location. The full manual is [help/index.html](help/index.html).

## License

MIT, see [LICENSE](LICENSE). Third-party components (Microsoft WebView2 SDK, SheetJS) keep their own licences, see
[help/THIRD-PARTY-NOTICES.txt](help/THIRD-PARTY-NOTICES.txt).
