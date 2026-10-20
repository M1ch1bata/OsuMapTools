# OsuTrainerXSlim 代码审查指南

面向其他智能体/开发者的代码审查入口。

## 项目概述

osu! stable（mania）的极简练习工具，WPF + .NET Framework 4.8（x64），
构建产物是**单文件 exe**（Costura 内嵌托管依赖，web 资源首次启动解包）。

功能：

- 变速（保持音高 / 变调），支持按 BPM 变速
- 修改 OD / HP
- LN 变换：FULL LN（反键）、RICE（面转米）、Release（随机面尾）
- 难度预览：osu! SR（rosu-pp）+ 段位（Azusa）+ MSD（Etterna MinaCalc）
- 自动跟随 osu! 当前谱面（内存优先，tosu 可选）
- 内置歌曲列表（扫描 Songs 目录）
- 输出到同目录或外部目录，后缀规则：速度 + 模式 + HP + OD

## 技术栈

| 模块 | 实现 |
|---|---|
| UI | WPF，深色 lazer 风格（`App.xaml` 资源） |
| 谱面解析/序列化 | `OsuFile.cs` |
| 变速/音频 | NAudio + NVorbis + SoundTouch.Net（`AudioProcessor.cs`） |
| 难度计算 | 隐藏 WebView2 窗口加载 ManiaMapAnalyser（`assets/web`） |
| osu! SR | rosu-pp WASM（`assets/web/js/rosu`） |
| 内存读取 | OsuMemoryDataProvider 0.10.3（`MemoryWatcher.cs`） |
| tosu 备用 | WebSocket v2（`TosuWatcher.cs`） |
| 单文件打包 | Fody + Costura.Fody（`Slim.App.csproj`、`FodyWeavers.xml`、`AssetPaths.cs`） |

## 目录结构

```
src/Slim.App/                 WPF 主程序
  MainWindow.xaml(.cs)        主界面、谱面加载、预览、生成
  AnalysisWindow.xaml(.cs)    隐藏 WebView2，宿主 JS 分析器
  MemoryWatcher.cs            osu! 内存轮询
  TosuWatcher.cs              tosu WebSocket
  Transforms.cs               LN 变换、后缀规则、输出路径
  OsuFile.cs                  .osu 解析/序列化
  AudioProcessor.cs           变速 MP3
  SongPicker.xaml(.cs)        内置歌曲列表
  AssetPaths.cs               单文件资源解包
  App.xaml(.cs)              资源与启动参数
src/Slim.AnalysisTest/        分析器冒烟测试
assets/web/                   分析器前端（ManiaMapAnalyser + rosu-pp）
build-to-d.cmd                构建并部署单文件 exe 到 D:\APPs\OsuTrainerXSlim
```

## 构建

```bat
cd src\Slim.App
dotnet build -c Release
```

本机 SDK 路径与临时目录见 `build-to-d.cmd`（便携 SDK）。
产物：`bin\Release\net48\OsuTrainerXSlim.exe`（单文件，约 3.6 MB）。

## 运行约束（审查时注意）

1. **不能放在工作区目录运行**：`D:\vibe\osu\` 带沙箱 ACL，
   进程被限制后读不到 osu! 内存，必须放 `D:\APPs\...` 或桌面等普通目录。
2. 需要系统 WebView2 运行时（Win10/11 自带）。
3. 内存读取要求 osu! 与工具权限一致；管理员运行的 osu! 需要管理员运行本工具。
4. 首次启动会把 `web` 资源解包到 `%LOCALAPPDATA%\OsuTrainerXSlim\web\<版本>`。

## 审查建议关注点

- `MainWindow.xaml.cs`：异步预览版本控制（`_previewVersion`）、生成守卫（`HasChanges`）、异常处理
- `Transforms.cs`：FULL LN / RICE / Release 与 Lazer 算法一致性；后缀拼接
- `MemoryWatcher.cs`：轮询线程、失败重试、进程枚举限制下的降级
- `TosuWatcher.cs`：WebSocket 重连、JSON 解析、路径拼接
- `AudioProcessor.cs`：SoundTouch 读取循环、变调重采样、编码参数
- `AnalysisWindow.xaml.cs`：WebView2 生命周期、消息协议、超时
- 单文件：`AssetPaths.cs` 的解包路径与并发（多实例）、Costura 与原生 loader
