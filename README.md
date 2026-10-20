# OsuTrainerX Slim

> **单文件分发**：构建产物是单个 `OsuTrainerXSlim.exe`（约 3.6 MB，内嵌全部依赖与 web 资源）。
> 首次启动会把分析器资源解包到 `%LOCALAPPDATA%\OsuTrainerXSlim\web\<版本>`，之后直接复用。
> 运行位置：放在普通目录（如 `D:\APPs\OsuTrainerXSlim`）里运行；放在 `C:\Users\96549\Documents\vibe\osu\` 工作区目录下时该目录带沙箱 ACL，读不到 osu! 内存。
> 需要系统自带 WebView2 运行时（Win10/11 默认已有）。
> 构建后执行 `build-to-d.cmd` 自动部署单文件 exe。


面向 osu! stable 的极简谱面练习工具：**变速、改 OD/HP、LN 处理（invert / hold off / Release）、难度预览（MSD + 段位）**。

体积 **3.3 MB**，零安装：只依赖 Windows 自带的 .NET Framework 4.8 与 Edge WebView2 运行时。

## 功能

| 功能 | 说明 |
|---|---|
| 变速 | 0.5×–2.0×，或勾选「按BPM」直接指定目标 BPM（右侧实时显示换算倍率）；「保持音高」可取消以获得变调效果；音频解码 MP3 / OGG / WAV，统一输出 192 kbps MP3 |
| OD / HP | 留空则不修改 |
| **FULL LN**（原 invert） | 对齐 osu!lazer `ManiaModInvert`：每列每个物件（note / LN 头）变成长条，`缝隙 = min(d/2, beat/f)`；可选 **松散 1/2 拍 / 标准 1/4 拍 / 密集 1/8 拍**，列尾物件丢弃，只留头音效 |
| **RICE**（原 hold off） | 对齐 osu!lazer `ManiaModHoldOff`：所有 LN 变回单点（保留头音效），note 不动 |
| **Release** | 随机面尾：从物件头重建长条，`缝隙 = min(d/2, beat/f)`，`f ∈ {2,3,4,5,6,8}` 纯随机；列尾保留单点 |
| 难度预览 | 三栏显示（数字居中输入见设置区）：**星数**（osu! 官方星级，来自 tosu `beatmap.stats.stars`，如 5.49星）· **段位**（RC 段位，LN 占比 ≥15% 时下面一行显示 LN 段位，如 `Reform 9 High` / `LN 10 Low`）· **MSD**（保留两位小数）；切换模式会实时重算；与 ManiaMapAnalyser 插件同源（段位使用插件默认的 **Azusa** 估算器） |
| 动态元素 | 新 `.osu` 中去掉 Video / Storyboard / Break 事件，原始文件保留不动 |

其中「缝隙」指**上一段 LN 抬手到下一个 LN 按下的时间间隔**。所有模式都写回**同一谱面文件夹**，文件名与 `Version` 加后缀，顺序为 **倍速 + 模式 + HP + OD**（如 `[1.2x FULL LN HP8 OD8]`、`[RICE]`、`[Release 1.2x]`）。

## 使用

1. 拖入 `.osu` 或点击「选择谱面…」；
2. 设定倍速 / OD / HP / 音符处理；
3. 预览区实时显示输出谱面的 MSD 与段位；
4. 也可以点「选择谱面…」从内置歌曲列表中选择；
5. 点「生成」（若倍速 / OD / HP / 模式 / 输出目录全部未改动，按钮会禁用，避免生成与原文完全相同的谱面）。

变速时会生成新音频文件（如 `audio [1.2x].mp3`），原音频不被覆盖。

## 命令行（自动化/自测）

```
OsuTrainerXSlim.exe <map.osu> [--rate 1.2] [--od 8] [--hp 7]
                    [--mode keep|invert|holdoff|release] [--generate]
```

## 技术实现

- `net48` + WPF，单 exe，无运行时打包；
- 难度分析：离屏 WebView2 通过虚拟域名加载插件原版 JS（`parser` + `rework` + `estimator` + `ett`），C# 与页面用 `chrome.webview` 消息通信 —— 数值与插件完全一致；
- 音频：NAudio（MediaFoundationReader / MediaFoundationEncoder）+ NVorbis（OGG）+ SoundTouch（变速不变调）；
- 事件剥离：删除 `[Events]` 中的 `Video,`、`1,0,"*.avi|mp4"`、`Sprite/Animation/Sample`、`2,`（break），保留背景图。

## 已知限制

- 谱面集根目录的 `.osb` 是**所有难度共享**的，`.osu` 无法禁用；有 `.osb` 的谱面集输出后故事板仍会显示（按需求不做提示）；
- 视频/故事板资源文件原样保留，只是新 `.osu` 不再引用；
- MP3 编码依赖 Windows Media Foundation；Windows N 版可能缺失；
- 变速后音频统一转 MP3，原始 OGG/WAV 不保留；
- FULL LN 的列尾物件按 Lazer 行为**丢弃**；Release 的列尾保留单点；
- 数字段位基于 RC（纯音符）区间；Intro 段（<1dan）只显示文字标签；
- Release 为纯随机、不可复现。

## 构建

需要 .NET SDK 8（用 `dotnet build` 即可编译 net48 目标）：

```
dotnet build -c Release
```

产物：`src/Slim.App/bin/Release/net48/`（约 3.3 MB，68 个文件）。

## 已验证

- **FULL LN**：917 个长条的起点/终点/列/打击音与 Lazer 算法参考实现**逐项一致**；
- **RICE**：921 个物件、0 个长条；
- **Release**：917 个长条的缝隙全部落在 `min(d/2, beat/f)` 允许值内，列尾 4 个单点；
- 音频：MP3 → 1.2×（300.7 s → 250.7 s，191.8 kbps）；OGG → 1.2×；48 kHz WAV → 0.8×；
- MSD 与插件逐项一致（Pendulum 4K：6.2875 / 5.993 / 5.140 / 3.460 / 6.080 / 3.940 / 4.420 / 6.2875），段位 `< Alpha Low`。
