# VR 视频平面播放器

[English](README.md) · **简体中文** · [日本語](README.ja.md) · [한국어](README.ko.md)

<img src="assets/icon-256.png" width="128" alt="VR 视频平面播放器">

在**普通显示器**上舒服地观看 **180° / 360° VR 视频**的桌面播放器,支持本地 8K。
可选用普通摄像头追踪头部动作,自动转动视角。

版本 0.2,仅支持 Windows。

> 英文名 **VR Flat Player**,可执行文件、发布目录和安装路径都用英文名
> (`VRFlatPlayer.exe`、`dist\VR Flat Player\`)—— 路径里避开非 ASCII 字符,
> 省去一整类编码和命令行转义的麻烦。

解码和渲染复用 mpv + mpv360,本仓库提供播放器窗口和中间的追踪桥接层。

```
                          VRFlatPlayer.exe
  ┌─────────────────────────────────────────────────────────┐
  │  媒体  播放  音频  字幕  VR  视图  帮助                 │
  ├─────────────────────────────────────────────────────────┤
  │                                                         │
  │   mpv 的窗口(--wid 子窗口,另一个进程)                 │
  │     mpv360 投影着色器 · uosc 控制栏 · 模式面板          │
  │                                                         │
  └─────────────────────────────────────────────────────────┘
        ▲                                    ▲
        │ JSON IPC                           │ 左键拖动(Win32 直接读鼠标)
        │                                    │
   ┌────┴─────────────────────────────────────┴────┐
   │  桥接层:One Euro 滤波 / 增益曲线 / 回中        │
   └───────────────────────┬───────────────────────┘
                           │ UDP(可选)
                  opentrack ◄── webcam
```

## 能做什么

- **180 / 360 / 鱼眼 / 柱面 / EAC**,单目或立体,左右或上下排列。只显示一只眼睛 ——
  平面显示器只有一个视点。
- **自动识别片源格式**:先看文件名(沿用三个主流 VR 播放器确立的命名约定),
  文件名没线索时看画面比例。2:1 是真正无解的一种 —— 单目 360 和 VR180 左右排列
  在像素上完全一样 —— 所以选择会**按文件记住**,也可以随时从菜单改。
- **摄像头头部追踪**,默认关闭。YuNet 找脸,68 点模型定位,PnP 解算出头部姿态。
  无需标记点、无需额外硬件、无需安装 opentrack —— 但如果你已经在用 opentrack,
  UDP 输入依然支持。
- **鼠标拖动转视角**、键盘转视角、滚轮缩放。
- **原生菜单栏**(不是画面上的浮层),支持英文、简繁中文、日文、韩文,跟随系统语言。

## 运行要求

- Windows 10 / 11,x64
- 支持 Direct3D 11 的显卡(菜单里备有 Vulkan 和 OpenGL 作为退路)
- 头部追踪:任意摄像头
- 8K 播放需要较新的独立显卡,解码器选择见下文

## 安装

下载发布包解压到任意位置,运行 `VRFlatPlayer.exe`。不写注册表、不往文件夹外写任何东西。

想让右键"打开方式"里出现这个播放器,运行同目录下的 `register-file-types.bat`,
`unregister-file-types.bat` 可撤销。两者**只写 HKEY_CURRENT_USER**,不需要管理员权限。

## 从源码构建

```
git clone <本仓库>
cd VRHeadTrackingPlayer

tools\install-mpv360.bat      # mpv360 着色器、uosc、字体
tools\install-models.bat      # 两个 ONNX 模型

dotnet run --project tests/VideoFormatTests/VideoFormatTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

`publish.ps1` 产出 `dist\VR Flat Player\` 和带版本号的 zip。它需要一个 mpv.exe 来打包,
会自动找已安装的,也可以用 `-MpvExe <路径>` 指定。

构建需要 .NET 8 SDK。发布出来的 exe 是自包含的,**用户不需要装 .NET**。

### ONNX 模型不在本仓库里

头部追踪需要两个模型,合计约 14 MB。**没有提交进仓库** —— 二进制不该进源码历史,
而且两者都在别处发布、各有各的许可证。`tools\install-models.bat` 会把它们下载到 `models\`:

| 文件 | 模型 | 来源 | 许可证 |
| --- | --- | --- | --- |
| `face_detection_yunet.onnx` | YuNet | [opencv/opencv_zoo](https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx) | MIT |
| `face_landmark_peppa_wutz.onnx` | peppa_wutz 68 点 | [facefusion/facefusion-assets](https://github.com/facefusion/facefusion-assets/releases/download/models-3.0.0/peppa_wutz.onnx) | MIT |

没有它们播放器照常工作,只是用不了头部追踪。

### mpv 同样不在仓库里

mpv 是独立的 GPL 程序,**以独立进程的形式随发布包分发,不是链接进来的**。
仓库里只有我们自己的配置和脚本(`mpv/` 下):`mpv.conf`、`input.conf`、`vrmenu.lua`,
以及我们 fork 的 mpv360 着色器源码 `mpv/shaders-src/`。

所有来自上游的东西 —— `mpv.exe`、`mpv360.lua`、uosc、字体、编译后的着色器 ——
都由 `tools\install-mpv360.bat` 下载,并被 git 忽略。

## 快捷键

| 按键 | 作用 |
| --- | --- |
| `Home` | **视角归正** —— 以当前头部姿态为新的中立位 |
| `Alt` + 方向键 | 转视角,一次 5° |
| 滚轮 | 视场角,一格 5° |
| 左键拖动 | 转视角 |
| `Tab` | 模式面板 |
| `Ctrl+E` | 开关 360 模式 |
| `Ctrl+Shift+P` | 循环投影格式 |
| `Ctrl+Shift+E` | 切换眼别 |
| `Ctrl+Shift+↑ / ↓` | 视场角 |
| `Ctrl+Shift+V` | 视角复位(不改变头部参考) |
| `Ctrl+Shift+H` | 开关头部追踪 |
| `Ctrl+[` / `Ctrl+]` | 追踪增益 − / + |
| `Ctrl+Shift+I` | 播放统计 |
| `F` | 全屏 |

mpv 原有的按键(空格、方向键、音量)照常可用。

## 配置

`bridge.config.json` 在 exe 旁边,菜单里改设置时会自动写入。删掉它就恢复默认。
也可以用 `VRFlatPlayer.exe --config=路径.json --write-config` 生成一份全新的。

值得知道的几项:

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `yaw.outputRangeDegrees` | 70 | 头转到底时视角转多少度 |
| `yaw.stickyDegrees` | 1.0 | 头动多少度以内画面完全不动 |
| `pitch.inputRangeDegrees` | 12 | 人点头的幅度远小于摇头 |
| `video.fallback` | `vr180` | 毫无线索的 2:1 文件按什么打开 |
| `source.camera.landmarkFps` | 30 | 模型抢解码器 CPU 时调低它 |

窗口位置、每个文件的 VR 模式、运行日志分别存在
`window-state.json`、`mode-memory.json`、`mpv-last-run.log` 里 ——
**分开存是有意的**,清掉一个不会连累另外两个。

## 出问题时

exe 旁边的 `mpv-last-run.log` 记录一次运行:播放器自己的启动诊断和 mpv 的输出**交错在一起**。
里面有:选了哪个 VR 模式**以及为什么**、片源的分辨率和编码、实际用的解码器和渲染器、
以及 **mpv 最终被设成了什么** —— 通常足以分清是识别错了还是渲染错了。

画面全黑就去"播放 → 渲染器"换一个,最可能的原因是显卡驱动跑不了默认后端。

## 目录结构

```
src/HeadTrackBridge/     播放器本体:窗口、菜单、IPC、追踪、映射
  Host/                  WinForms 窗口和菜单栏
  Mpv/                   IPC 客户端、模式控制、格式识别
  Tracking/              摄像头、关键点、姿态解算
  Mapping/               滤波、增益曲线、视角合成
mpv/                     我们自己的 mpv 配置、脚本、着色器源码
tests/VideoFormatTests/  608 条断言,几秒钟跑完
tools/                   安装脚本、图标生成、打包
prompt/                  开发交接记录(中文)
```

`AGENTS.md` 是本仓库的开发守则,**每一条都是踩过坑换来的**。

## 许可证与致谢

VR 视频平面播放器是自由软件,采用 **GNU 通用公共许可证 v3.0 或更高版本**,见 [LICENSE](LICENSE)。

选 GPLv3 而不是宽松许可证,是因为发布包里捆绑了 mpv,而 mpv 是 GPLv2 **或更高版本** ——
正是那个"或更高"让两者兼容;本播放器采用同样的条款,整个下载包的授权关系才没有歧义。

它站在这些项目之上:

- **[mpv](https://mpv.io/)**(GPLv2+)—— 解码、渲染、播放。以**独立可执行文件**
  形式随发布包分发,未作修改。
- **[mpv360](https://github.com/kasper93/mpv360)**(MIT)—— 投影着色器。我们的 fork
  增加了左右排列的立体 360 和单目鱼眼,并改为按输出分辨率渲染而不是源分辨率。
- **[uosc](https://github.com/tomasklaen/uosc)**(LGPL-2.1)—— 控制栏。
- **[YuNet](https://github.com/opencv/opencv_zoo)**(MIT)—— 人脸检测。
- **peppa_wutz**(MIT)—— 68 点关键点。
- **One Euro Filter** —— Casiez、Roussel、Vogel,CHI 2012。
- **[opentrack](https://github.com/opentrack/opentrack)** —— 可选的 UDP 输入源。
