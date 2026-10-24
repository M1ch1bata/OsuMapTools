# OsuMapTools

面向 **osu! stable（mania）** 的谱面处理与练习工具：变速、改 OD/HP、LN 变换、难度预览，开箱即用。

> 单文件 exe，免安装；也可以下载完整文件夹版。

![预览](docs/preview.png)

## 功能

### 变速

- 按倍速变速：**保持音高**（SoundTouch）或 **变调**（重采样，像唱片加速）
- 按 BPM 变速：多 BPM 谱面以**覆盖时间最长的主 BPM** 为参考，`倍率 = 目标 BPM ÷ 主 BPM`
- 谱面时间、TimingPoints、音频同步缩放，相对 BPM 结构保持不变

### 谱面修改

- 修改 **OD / HP**（未改动时不写入）
- **FULL LN**：把所有音符变成 LN，尾部留 1/f 拍缝隙（松散 1/2、标准 1/4、密集 1/8）；间隔不足 1/f 拍或每列最后一件保持单点。标准（1/4）与 NOODLES 谱面的规则一致；**无缝（seamless）** 让 LN 尾与下一件头完全重合（0 ms），供特殊需求使用
- **RICE**：把 LN 变成单点（Lazer `ManiaModHoldOff`）
- **Release**：保持 LN 头不变，按 `min(d/2, 拍长/f)` 随机生成尾判（f = 2/3/4/5/6/8）
- 自动清理视频、故事板与 break（输出更干净）

### 难度预览

| 项目 | 来源 |
|---|---|
| 星数 | osu! 官方 SR（rosu-pp） |
| 段位 | Azusa（与 ManiaMapAnalyser 插件一致） |
| MSD | Etterna MinaCalc |

### 识别当前谱面

- 默认读取 osu! 进程内存自动跟随（选歌界面、游玩中都可）
- 可选 **tosu** 通道（`ws://127.0.0.1:24050`），内存不可用时自动切换
- 内置**歌曲列表**：搜索谱面集、选择难度、一键载入
- 支持把 `.osu` 文件直接拖进窗口

### 输出

- 默认写回原谱面所在文件夹，也可指定外部输出目录
- 后缀规则：**倍速 + 模式 + HP + OD**（未改动的项省略），例如：
  - `[1.2x FULL LN HP8 OD8]`
  - `[180bpm RICE]`
  - `[Release]`
- 同名文件自动追加 ` (2)`，不会覆盖原谱面
- 音频按需转码为 192 kbps MP3

## 使用

1. 从 [Releases](https://github.com/M1ch1bata/OsuMapTools/releases) 下载：
   - `OsuMapTools.exe`（单文件，推荐）
   - 或完整文件夹版压缩包（解压后运行 `OsuMapTools.exe`）
2. 先打开 osu!，再打开 OsuMapTools
3. 确认右上角出现 `内存 SongSelect`（或 tosu 已连接）；也可以点「选择谱面…」从歌曲列表选择
4. 调整倍速 / OD / HP / 音符处理
5. 点「生成」，底部会显示实际输出路径

> 什么都没改时「生成」按钮是禁用的；这表示输出会和原谱面完全一致，没有生成必要。

## 系统要求

- Windows 10 / 11（x64）
- .NET Framework 4.8（系统自带）
- WebView2 Runtime（Win10/11 自带；缺失时从微软官网安装）
- osu! stable（mania 谱面）

## 命令行

```bat
OsuMapTools.exe "map.osu" --rate 1.2 --generate
OsuMapTools.exe "map.osu" --bpm 180 --mode invert --strength dense --generate
OsuMapTools.exe "map.osu" --od 8 --hp 7 --out "D:\out" --no-pitch --generate
```

| 参数 | 说明 |
|---|---|
| `--rate <x>` | 倍速（0.01–5） |
| `--bpm <bpm>` | 按目标 BPM 变速 |
| `--od <v>` / `--hp <v>` | 覆盖 OD / HP |
| `--mode keep\|invert\|holdoff\|release` | 音符处理 |
| `--strength loose\|std\|dense\|seamless` | FULL LN 缝隙 |
| `--out <dir>` | 外部输出根目录 |
| `--no-pitch` | 关闭保持音高（变调） |
| `--generate` | 生成后退出 |

退出码：`0` 成功，`1` 生成失败/无改动，`2` 谱面不存在。

## 注意事项

- 读内存需要权限对齐：如果 osu! 以管理员身份运行，本工具也需要以管理员身份运行
- 请保留原谱面备份；工具只写新文件，不修改源文件
- Release 模式按设计是随机的，预览与最终生成可能不同
- 输出目录如果选在 `Songs` 根目录，会自动落到原谱面文件夹，不会自我复制

## 许可证

[MIT](LICENSE)
