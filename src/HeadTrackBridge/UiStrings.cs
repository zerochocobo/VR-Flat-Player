namespace HeadTrackBridge;

/// <summary>
/// Every piece of text the player shows a user: the native menu bar, its
/// dialogs, and the toasts drawn over the video.
///
/// It used to be the menu bar only, and that was the bug behind "the Chinese is
/// only partial" — the menu was translated while every OSD message the bridge
/// emitted was a hardcoded English literal at its call site. Anything a user
/// reads belongs in here.
///
/// Deliberately not .resx satellite assemblies: the whole point of
/// <see cref="Localization"/> is that one resolved language tag drives mpv,
/// uosc, vrmenu.lua and this table alike, and satellite assemblies would put
/// the menu on a completely separate mechanism (CurrentUICulture at first
/// access, resolved per-assembly) that the config file could no longer
/// override.
///
/// Unknown keys return the key itself rather than throwing — a missing string
/// should look wrong on screen, not take the player down at startup.
/// </summary>
public sealed class UiStrings
{
    private readonly IReadOnlyDictionary<string, string> _map;

    private UiStrings(IReadOnlyDictionary<string, string> map) => _map = map;

    /// <summary>Blank line inside a message box, spelled out so no escape sequence is involved.</summary>
    private const string Sep = "\n\n";

    public string this[string key] =>
        _map.TryGetValue(key, out var s) ? s
        : English.TryGetValue(key, out var e) ? e
        : key;

    /// <summary><see cref="this[string]"/> with <see cref="string.Format(string,object[])"/> applied.</summary>
    public string F(string key, params object[] args) => string.Format(this[key], args);

    /// <summary>
    /// The one language the process runs in.
    ///
    /// A static because it is genuinely process-wide and set once, before any
    /// window or IPC connection exists. The alternative was threading a
    /// parameter through PlayerSession, PlayerModeController and every class
    /// that shows a toast, which is a lot of plumbing to express "the user's
    /// language did not change while the player was running".
    ///
    /// Defaults to English so anything reading it before <see cref="Init"/>
    /// still renders words rather than throwing.
    ///
    /// Resolved on first read rather than in a field initialiser: static fields
    /// initialise in declaration order, and the tables below are declared after
    /// this, so `= For("en")` here would capture a null dictionary and every
    /// lookup would throw.
    /// </summary>
    public static UiStrings Current => _current ??= For("en");

    private static UiStrings? _current;

    public static void Init(string ownTag) => _current = For(ownTag);

    /// <summary>
    /// Every language with a table here. Exists so the unit tests can check
    /// that the tables have identical key sets — a missing key silently falls
    /// back to English, which is how the player ended up part-translated
    /// without anyone noticing.
    /// </summary>
    public static readonly string[] Tags = ["en", "zh-hans", "zh-hant", "ja", "ko"];

    public IEnumerable<string> Keys => _map.Keys;

    /// <param name="ownTag">A tag from <see cref="Localization.ResolveOwnLanguage"/>.</param>
    public static UiStrings For(string ownTag) => new(ownTag switch
    {
        "zh-hans" => Simplified,
        "zh-hant" => Traditional,
        "ja" => Japanese,
        "ko" => Korean,
        _ => English,
    });

    // "&" marks the Alt accelerator. Only the English table has them: CJK menus
    // conventionally show the Latin letter in parentheses, e.g. 文件(&F).
    private static readonly Dictionary<string, string> English = new()
    {
        ["menu.file"] = "&File",
        ["file.open"] = "Open File...",
        ["file.openUrl"] = "Open Network Stream...",
        ["file.close"] = "Close File",
        ["file.exit"] = "Exit",

        ["menu.playback"] = "&Playback",
        ["play.playPause"] = "Play / Pause",
        ["play.stop"] = "Stop",
        ["play.back10"] = "Back 10 s",
        ["play.forward30"] = "Forward 30 s",
        ["play.prevFrame"] = "Previous Frame",
        ["play.nextFrame"] = "Next Frame",
        ["play.speed"] = "Speed",
        ["play.speedDown"] = "Slower",
        ["play.speedUp"] = "Faster",
        ["play.speedReset"] = "Normal",
        ["play.loop"] = "Loop File",
        ["play.prevFile"] = "Previous File",
        ["play.nextFile"] = "Next File",

        ["menu.decoder"] = "Hardware Decoding",
        ["dec.default"] = "Default (recommended)",
        ["dec.auto"] = "Automatic",
        ["dec.needs"] = "needs the {0} renderer",
        ["dec.switchTitle"] = "Switch renderer too?",
        ["dec.switchBody"] =
            "{0} cannot hand frames to the current renderer, so mpv fell back to "
            + "software decoding." + Sep
            + "It needs a {1} renderer. Switch to “{2}”?",
        ["dec.d3d11va"] = "Direct3D 11",
        ["dec.dxva2"] = "DXVA2 (older GPUs)",
        ["dec.nvdec"] = "NVDEC (NVIDIA)",
        ["dec.vulkan"] = "Vulkan",
        ["dec.off"] = "Software (no GPU decoding)",

        ["menu.renderer"] = "Renderer",
        ["ren.default"] = "Default - gpu-next / D3D11",
        ["ren.compat"] = "Compatible - gpu / D3D11",
        ["ren.vulkan"] = "Vulkan (modern, often fastest)",
        ["ren.opengl"] = "OpenGL",
        ["ren.angle"] = "ANGLE (OpenGL over Direct3D)",
        ["ren.d3d9"] = "Direct3D 9 (last resort)",
        ["ren.note"] = "Try the next one down if video is black or stutters.",
        ["ren.restartTitle"] = "Restart to apply",
        ["ren.restartBody"] =
            "The renderer changes when the player starts, because mpv builds its "
            + "graphics context once at startup." + Sep + "Restart now?",

        ["menu.audio"] = "&Audio",
        ["audio.track"] = "Audio Track",
        ["audio.mute"] = "Mute",
        ["audio.volUp"] = "Volume Up",
        ["audio.volDown"] = "Volume Down",

        ["menu.subtitle"] = "&Subtitle",
        ["sub.track"] = "Subtitle Track",
        ["sub.load"] = "Add Subtitle File...",
        ["sub.none"] = "Off",

        ["menu.vr"] = "&VR",
        ["vr.geometry"] = "Projection",
        ["vr.stereo"] = "Stereo Layout",
        ["vr.eye"] = "Eye",
        ["vr.fovIn"] = "Narrower Field of View",
        ["vr.fovOut"] = "Wider Field of View",
        ["vr.fovReset"] = "Reset Field of View",
        ["vr.resetView"] = "Reset View",
        ["vr.recenter"] = "Recentre Head Position",
        ["vr.tracking"] = "Head Tracking",
        ["vr.sensitivity"] = "Tracking Sensitivity",
        ["vr.sensLower"] = "Less Sensitive",
        ["vr.sensHigher"] = "More Sensitive",
        ["vr.sensReset"] = "Reset to Default",
        ["vr.panel"] = "Show Mode Panel",

        ["menu.camera"] = "&Camera",
        ["cam.test"] = "Test Camera...",
        ["cam.busyBody"] =
            "Head tracking is already using the camera, and a camera can only be "
            + "opened by one thing at a time." + Sep
            + "Turn head tracking off first, then try again.",
        ["cam.startFailedBody"] =
            "The camera could not be started." + Sep
            + "If the face detector is missing, run tools\\install-models.bat. Otherwise "
            + "check that nothing else is using the camera, and that camera access is "
            + "allowed under Windows Settings > Privacy > Camera.",

        ["menu.view"] = "Vie&w",
        ["view.fullscreen"] = "Fullscreen",
        ["view.onTop"] = "Always on Top",
        ["view.shape"] = "Window Shape",
        ["view.stats"] = "Playback Statistics",

        ["menu.help"] = "&Help",
        ["help.keys"] = "Keyboard Shortcuts",
        ["help.about"] = "About",

        ["geo.flat"] = "Flat (VR off)",
        ["geo.180"] = "180",
        ["geo.360"] = "360",
        ["geo.fisheye"] = "Fisheye",
        ["geo.cylindrical"] = "Cylindrical",
        ["geo.eac"] = "Equi-Angular Cubemap",

        ["stereo.mono"] = "2D (mono)",
        ["stereo.sbs"] = "3D side by side",
        ["stereo.tb"] = "3D over / under",

        ["eye.left"] = "Left",
        ["eye.right"] = "Right",
        ["eye.both"] = "Both (headset only)",

        ["dlg.videoFiles"] = "Video files",
        ["dlg.subFiles"] = "Subtitle files",
        ["dlg.allFiles"] = "All files",
        ["dlg.urlTitle"] = "Open Network Stream",
        ["dlg.urlPrompt"] = "Address:",
        ["dlg.ok"] = "OK",
        ["dlg.cancel"] = "Cancel",
        ["dlg.trackNone"] = "None",

        // Toasts drawn over the video by the bridge. {0}-style holes rather than
        // sentences assembled from fragments: word order differs per language,
        // and "using software" glued onto a decoder name lands in the wrong
        // place in Japanese and Korean.
        ["osd.recentred"] = "head tracking: recentred",
        ["osd.viewReset"] = "head tracking: view reset",
        ["osd.trackingOn"] = "head tracking: on",
        ["osd.trackingOff"] = "head tracking: off",
        ["osd.trackingLost"] = "head tracking: signal lost — view held",
        ["osd.trackingBack"] = "head tracking: signal back",
        ["osd.gain"] = "gain  yaw {0}°  pitch {1}°",
        ["osd.decoder"] = "decoder: {0}",
        ["osd.decoderNeedsRenderer"] = "decoder: {0} needs the {1} renderer — switching to software",
        ["osd.decoderUnavailable"] = "decoder: {0} is not available here — using software",
        ["osd.renderer"] = "renderer: {0} — restart to apply",
        ["osd.vrMode"] = "VR: {0}",
        ["osd.comboUnsupported"] = "{0} + {1} is not supported — using {2}",
        ["mode.flatOff"] = "Flat (360 off)",

        ["about.body"] =
            "An open-source PC player for 180/360 VR video.\n" +
            "Decoding and rendering by mpv; projection by mpv360; control bar by uosc.\n\n" +
            "Licensed under the GNU General Public License v3 or later.\n" +
            "This program comes with absolutely no warranty.",
        ["keys.body"] =
            "In the player window\n" +
            "  Left-drag        look around\n" +
            "  Space            play / pause\n" +
            "  F / Esc          fullscreen on / off\n" +
            "  Left / Right     seek 5 s\n" +
            "  Tab              mode panel\n" +
            "  Ctrl+Shift+G     cycle projection\n" +
            "  Ctrl+Shift+D     cycle stereo layout\n" +
            "  Ctrl+Shift+Y     cycle eye\n" +
            "  Ctrl+Shift+R     recentre head position\n" +
            "  Ctrl+Shift+V     reset view\n" +
            "  Ctrl+Shift+H     head tracking on / off\n" +
            "  Ctrl+Shift+I     playback statistics\n" +
            "  Ctrl+Shift+B     next renderer (use if the picture is black)\n",
    };

    private static readonly Dictionary<string, string> Simplified = new()
    {
        ["menu.file"] = "媒体(&F)",
        ["file.open"] = "打开文件...",
        ["file.openUrl"] = "打开网络串流...",
        ["file.close"] = "关闭文件",
        ["file.exit"] = "退出",

        ["menu.playback"] = "播放(&P)",
        ["play.playPause"] = "播放 / 暂停",
        ["play.stop"] = "停止",
        ["play.back10"] = "后退 10 秒",
        ["play.forward30"] = "前进 30 秒",
        ["play.prevFrame"] = "上一帧",
        ["play.nextFrame"] = "下一帧",
        ["play.speed"] = "播放速度",
        ["play.speedDown"] = "减速",
        ["play.speedUp"] = "加速",
        ["play.speedReset"] = "正常",
        ["play.loop"] = "单文件循环",
        ["play.prevFile"] = "上一个文件",
        ["play.nextFile"] = "下一个文件",

        ["menu.decoder"] = "硬件解码",
        ["dec.default"] = "默认（推荐）",
        ["dec.auto"] = "自动",
        ["dec.needs"] = "需要 {0} 渲染器",
        ["dec.switchTitle"] = "要一起换渲染器吗？",
        ["dec.switchBody"] =
            "{0} 没法把画面交给当前的渲染器，mpv 已经退回软件解码。" + Sep
            + "它需要 {1} 渲染器。切换到「{2}」？",
        ["dec.d3d11va"] = "Direct3D 11",
        ["dec.dxva2"] = "DXVA2（较老的显卡）",
        ["dec.nvdec"] = "NVDEC（NVIDIA）",
        ["dec.vulkan"] = "Vulkan",
        ["dec.off"] = "软件解码（不用显卡）",

        ["menu.renderer"] = "渲染器",
        ["ren.default"] = "默认 - gpu-next / D3D11",
        ["ren.compat"] = "兼容 - gpu / D3D11",
        ["ren.vulkan"] = "Vulkan（现代 API，通常最快）",
        ["ren.opengl"] = "OpenGL",
        ["ren.angle"] = "ANGLE（用 Direct3D 模拟 OpenGL）",
        ["ren.d3d9"] = "Direct3D 9（最后的退路）",
        ["ren.note"] = "画面全黑或卡顿时，从上往下逐个试。",
        ["ren.restartTitle"] = "需要重启才能生效",
        ["ren.restartBody"] =
            "渲染器只在播放器启动时才会切换 —— mpv 的图形上下文是启动时一次性建立的。"
            + Sep + "现在重启？",

        ["menu.audio"] = "音频(&A)",
        ["audio.track"] = "音轨",
        ["audio.mute"] = "静音",
        ["audio.volUp"] = "增大音量",
        ["audio.volDown"] = "减小音量",

        ["menu.subtitle"] = "字幕(&S)",
        ["sub.track"] = "字幕轨",
        ["sub.load"] = "添加字幕文件...",
        ["sub.none"] = "关闭",

        ["menu.vr"] = "VR(&V)",
        ["vr.geometry"] = "投影方式",
        ["vr.stereo"] = "立体格式",
        ["vr.eye"] = "眼别",
        ["vr.fovIn"] = "缩小视野",
        ["vr.fovOut"] = "扩大视野",
        ["vr.fovReset"] = "重置视野",
        ["vr.resetView"] = "重置视角",
        ["vr.recenter"] = "重设头部中心",
        ["vr.tracking"] = "头部追踪",
        ["vr.sensitivity"] = "追踪灵敏度",
        ["vr.sensLower"] = "降低灵敏度",
        ["vr.sensHigher"] = "提高灵敏度",
        ["vr.sensReset"] = "恢复默认",
        ["vr.panel"] = "显示模式面板",

        ["menu.camera"] = "摄像头(&C)",
        ["cam.test"] = "摄像头测试...",
        ["cam.busyBody"] =
            "头部追踪正在使用摄像头，而摄像头同一时间只能被一个程序打开。" + Sep
            + "请先关闭头部追踪，再试一次。",
        ["cam.startFailedBody"] =
            "摄像头启动失败。" + Sep
            + "如果是缺少人脸检测模型，运行 tools\\install-models.bat。否则请检查摄像头"
            + "是否被其他程序占用，以及 Windows 设置 > 隐私 > 相机 是否允许访问。",

        ["menu.view"] = "视图(&W)",
        ["view.fullscreen"] = "全屏",
        ["view.onTop"] = "窗口置顶",
        ["view.shape"] = "窗口比例",
        ["view.stats"] = "播放统计信息",

        ["menu.help"] = "帮助(&H)",
        ["help.keys"] = "快捷键",
        ["help.about"] = "关于",

        ["geo.flat"] = "平面（关闭 VR）",
        ["geo.180"] = "180",
        ["geo.360"] = "360",
        ["geo.fisheye"] = "鱼眼",
        ["geo.cylindrical"] = "柱面",
        ["geo.eac"] = "等角立方体贴图",

        ["stereo.mono"] = "2D（单眼）",
        ["stereo.sbs"] = "3D 左右",
        ["stereo.tb"] = "3D 上下",

        ["eye.left"] = "左眼",
        ["eye.right"] = "右眼",
        ["eye.both"] = "双眼（仅头显）",

        ["dlg.videoFiles"] = "视频文件",
        ["dlg.subFiles"] = "字幕文件",
        ["dlg.allFiles"] = "所有文件",
        ["dlg.urlTitle"] = "打开网络串流",
        ["dlg.urlPrompt"] = "地址：",
        ["dlg.ok"] = "确定",
        ["dlg.cancel"] = "取消",
        ["dlg.trackNone"] = "无",

        ["osd.recentred"] = "头部追踪：已重设中心",
        ["osd.viewReset"] = "头部追踪：视角已重置",
        ["osd.trackingOn"] = "头部追踪：开",
        ["osd.trackingOff"] = "头部追踪：关",
        ["osd.trackingLost"] = "头部追踪：信号丢失 —— 视角保持不动",
        ["osd.trackingBack"] = "头部追踪：信号恢复",
        ["osd.gain"] = "灵敏度  水平 {0}°  垂直 {1}°",
        ["osd.decoder"] = "解码：{0}",
        ["osd.decoderNeedsRenderer"] = "解码：{0} 需要 {1} 渲染器 —— 已改用软解",
        ["osd.decoderUnavailable"] = "解码：这台机器上没有 {0} —— 已改用软解",
        ["osd.renderer"] = "渲染器：{0} —— 重启后生效",
        ["osd.vrMode"] = "VR：{0}",
        ["osd.comboUnsupported"] = "{0} + {1} 不支持 —— 改用 {2}",
        ["mode.flatOff"] = "平面（VR 已关闭）",

        ["about.body"] =
            "VR 视频平面播放器 —— 开源的 PC 端 180/360 VR 视频播放器。\n" +
            "解码与渲染由 mpv 完成，投影由 mpv360 提供，控制栏来自 uosc。\n\n" +
            "依据 GNU 通用公共许可证 v3 或更高版本授权。\n" +
            "本程序不提供任何担保。",
        ["keys.body"] =
            "在播放窗口中\n" +
            "  左键拖动        转动视角\n" +
            "  空格            播放 / 暂停\n" +
            "  F / Esc         进入 / 退出全屏\n" +
            "  左 / 右         快退 / 快进 5 秒\n" +
            "  Tab             模式面板\n" +
            "  Ctrl+Shift+G    切换投影方式\n" +
            "  Ctrl+Shift+D    切换立体格式\n" +
            "  Ctrl+Shift+Y    切换眼别\n" +
            "  Ctrl+Shift+R    重设头部中心\n" +
            "  Ctrl+Shift+V    重置视角\n" +
            "  Ctrl+Shift+H    头部追踪开 / 关\n" +
            "  Ctrl+Shift+I    播放统计信息\n" +
            "  Ctrl+Shift+B    切换渲染器（画面全黑时用）\n",
    };

    private static readonly Dictionary<string, string> Traditional = new()
    {
        ["menu.file"] = "媒體(&F)",
        ["file.open"] = "開啟檔案...",
        ["file.openUrl"] = "開啟網路串流...",
        ["file.close"] = "關閉檔案",
        ["file.exit"] = "結束",

        ["menu.playback"] = "播放(&P)",
        ["play.playPause"] = "播放 / 暫停",
        ["play.stop"] = "停止",
        ["play.back10"] = "後退 10 秒",
        ["play.forward30"] = "前進 30 秒",
        ["play.prevFrame"] = "上一格",
        ["play.nextFrame"] = "下一格",
        ["play.speed"] = "播放速度",
        ["play.speedDown"] = "減速",
        ["play.speedUp"] = "加速",
        ["play.speedReset"] = "正常",
        ["play.loop"] = "單檔循環",
        ["play.prevFile"] = "上一個檔案",
        ["play.nextFile"] = "下一個檔案",

        ["menu.decoder"] = "硬體解碼",
        ["dec.default"] = "預設（建議）",
        ["dec.auto"] = "自動",
        ["dec.needs"] = "需要 {0} 算繪器",
        ["dec.switchTitle"] = "要一起換算繪器嗎？",
        ["dec.switchBody"] =
            "{0} 沒法把畫面交給目前的算繪器，mpv 已經退回軟體解碼。" + Sep
            + "它需要 {1} 算繪器。切換到「{2}」？",
        ["dec.d3d11va"] = "Direct3D 11",
        ["dec.dxva2"] = "DXVA2（較舊的顯示卡）",
        ["dec.nvdec"] = "NVDEC（NVIDIA）",
        ["dec.vulkan"] = "Vulkan",
        ["dec.off"] = "軟體解碼（不用顯示卡）",

        ["menu.renderer"] = "算繪器",
        ["ren.default"] = "預設 - gpu-next / D3D11",
        ["ren.compat"] = "相容 - gpu / D3D11",
        ["ren.vulkan"] = "Vulkan（現代 API，通常最快）",
        ["ren.opengl"] = "OpenGL",
        ["ren.angle"] = "ANGLE（用 Direct3D 模擬 OpenGL）",
        ["ren.d3d9"] = "Direct3D 9（最後的退路）",
        ["ren.note"] = "畫面全黑或卡頓時，由上往下逐一嘗試。",
        ["ren.restartTitle"] = "需要重新啟動才會生效",
        ["ren.restartBody"] =
            "算繪器只在播放器啟動時才會切換 —— mpv 的圖形內容是啟動時一次性建立的。"
            + Sep + "現在重新啟動？",

        ["menu.audio"] = "音訊(&A)",
        ["audio.track"] = "音軌",
        ["audio.mute"] = "靜音",
        ["audio.volUp"] = "提高音量",
        ["audio.volDown"] = "降低音量",

        ["menu.subtitle"] = "字幕(&S)",
        ["sub.track"] = "字幕軌",
        ["sub.load"] = "加入字幕檔...",
        ["sub.none"] = "關閉",

        ["menu.vr"] = "VR(&V)",
        ["vr.geometry"] = "投影方式",
        ["vr.stereo"] = "立體格式",
        ["vr.eye"] = "眼別",
        ["vr.fovIn"] = "縮小視野",
        ["vr.fovOut"] = "擴大視野",
        ["vr.fovReset"] = "重設視野",
        ["vr.resetView"] = "重設視角",
        ["vr.recenter"] = "重設頭部中心",
        ["vr.tracking"] = "頭部追蹤",
        ["vr.sensitivity"] = "追蹤靈敏度",
        ["vr.sensLower"] = "降低靈敏度",
        ["vr.sensHigher"] = "提高靈敏度",
        ["vr.sensReset"] = "恢復預設",
        ["vr.panel"] = "顯示模式面板",

        ["menu.camera"] = "攝影機(&C)",
        ["cam.test"] = "攝影機測試...",
        ["cam.busyBody"] =
            "頭部追蹤正在使用攝影機，而攝影機同一時間只能被一個程式開啟。" + Sep
            + "請先關閉頭部追蹤，再試一次。",
        ["cam.startFailedBody"] =
            "攝影機啟動失敗。" + Sep
            + "如果是缺少人臉偵測模型，執行 tools\\install-models.bat。否則請檢查攝影機"
            + "是否被其他程式佔用，以及 Windows 設定 > 隱私權 > 相機 是否允許存取。",

        ["menu.view"] = "檢視(&W)",
        ["view.fullscreen"] = "全螢幕",
        ["view.onTop"] = "視窗置頂",
        ["view.shape"] = "視窗比例",
        ["view.stats"] = "播放統計資訊",

        ["menu.help"] = "說明(&H)",
        ["help.keys"] = "快速鍵",
        ["help.about"] = "關於",

        ["geo.flat"] = "平面（關閉 VR）",
        ["geo.180"] = "180",
        ["geo.360"] = "360",
        ["geo.fisheye"] = "魚眼",
        ["geo.cylindrical"] = "柱面",
        ["geo.eac"] = "等角立方體貼圖",

        ["stereo.mono"] = "2D（單眼）",
        ["stereo.sbs"] = "3D 左右",
        ["stereo.tb"] = "3D 上下",

        ["eye.left"] = "左眼",
        ["eye.right"] = "右眼",
        ["eye.both"] = "雙眼（僅頭顯）",

        ["dlg.videoFiles"] = "影片檔",
        ["dlg.subFiles"] = "字幕檔",
        ["dlg.allFiles"] = "所有檔案",
        ["dlg.urlTitle"] = "開啟網路串流",
        ["dlg.urlPrompt"] = "位址：",
        ["dlg.ok"] = "確定",
        ["dlg.cancel"] = "取消",
        ["dlg.trackNone"] = "無",

        ["osd.recentred"] = "頭部追蹤：已重設中心",
        ["osd.viewReset"] = "頭部追蹤：視角已重設",
        ["osd.trackingOn"] = "頭部追蹤：開",
        ["osd.trackingOff"] = "頭部追蹤：關",
        ["osd.trackingLost"] = "頭部追蹤：訊號中斷 —— 視角保持不動",
        ["osd.trackingBack"] = "頭部追蹤：訊號恢復",
        ["osd.gain"] = "靈敏度  水平 {0}°  垂直 {1}°",
        ["osd.decoder"] = "解碼：{0}",
        ["osd.decoderNeedsRenderer"] = "解碼：{0} 需要 {1} 算繪器 —— 已改用軟解",
        ["osd.decoderUnavailable"] = "解碼：這台機器上沒有 {0} —— 已改用軟解",
        ["osd.renderer"] = "算繪器：{0} —— 重新啟動後生效",
        ["osd.vrMode"] = "VR：{0}",
        ["osd.comboUnsupported"] = "{0} + {1} 不支援 —— 改用 {2}",
        ["mode.flatOff"] = "平面（VR 已關閉）",

        ["about.body"] =
            "VR 影片平面播放器 —— 開源的 PC 端 180/360 VR 影片播放器。\n" +
            "解碼與算繪由 mpv 完成，投影由 mpv360 提供，控制列來自 uosc。\n\n" +
            "依據 GNU 通用公共授權條款 v3 或更高版本授權。\n" +
            "本程式不提供任何擔保。",
        ["keys.body"] =
            "在播放視窗中\n" +
            "  左鍵拖曳        轉動視角\n" +
            "  空白鍵          播放 / 暫停\n" +
            "  F / Esc         進入 / 離開全螢幕\n" +
            "  左 / 右         倒轉 / 快轉 5 秒\n" +
            "  Tab             模式面板\n" +
            "  Ctrl+Shift+G    切換投影方式\n" +
            "  Ctrl+Shift+D    切換立體格式\n" +
            "  Ctrl+Shift+Y    切換眼別\n" +
            "  Ctrl+Shift+R    重設頭部中心\n" +
            "  Ctrl+Shift+V    重設視角\n" +
            "  Ctrl+Shift+H    頭部追蹤開 / 關\n" +
            "  Ctrl+Shift+I    播放統計資訊\n" +
            "  Ctrl+Shift+B    切換算繪器（畫面全黑時用）\n",
    };

    private static readonly Dictionary<string, string> Japanese = new()
    {
        ["menu.file"] = "メディア(&F)",
        ["file.open"] = "ファイルを開く...",
        ["file.openUrl"] = "ネットワークストリームを開く...",
        ["file.close"] = "ファイルを閉じる",
        ["file.exit"] = "終了",

        ["menu.playback"] = "再生(&P)",
        ["play.playPause"] = "再生 / 一時停止",
        ["play.stop"] = "停止",
        ["play.back10"] = "10 秒戻る",
        ["play.forward30"] = "30 秒進む",
        ["play.prevFrame"] = "前のフレーム",
        ["play.nextFrame"] = "次のフレーム",
        ["play.speed"] = "再生速度",
        ["play.speedDown"] = "遅く",
        ["play.speedUp"] = "速く",
        ["play.speedReset"] = "標準",
        ["play.loop"] = "1 ファイルをリピート",
        ["play.prevFile"] = "前のファイル",
        ["play.nextFile"] = "次のファイル",

        ["menu.decoder"] = "ハードウェアデコード",
        ["dec.default"] = "既定（推奨）",
        ["dec.auto"] = "自動",
        ["dec.needs"] = "{0} レンダラーが必要",
        ["dec.switchTitle"] = "レンダラーも切り替えますか？",
        ["dec.switchBody"] =
            "{0} は現在のレンダラーにフレームを渡せないため、mpv はソフトウェア"
            + "デコードにフォールバックしました。" + Sep
            + "{1} レンダラーが必要です。「{2}」に切り替えますか？",
        ["dec.d3d11va"] = "Direct3D 11",
        ["dec.dxva2"] = "DXVA2（旧世代の GPU）",
        ["dec.nvdec"] = "NVDEC（NVIDIA）",
        ["dec.vulkan"] = "Vulkan",
        ["dec.off"] = "ソフトウェア（GPU デコードなし）",

        ["menu.renderer"] = "レンダラー",
        ["ren.default"] = "既定 - gpu-next / D3D11",
        ["ren.compat"] = "互換 - gpu / D3D11",
        ["ren.vulkan"] = "Vulkan（最新の API、多くの場合最速）",
        ["ren.opengl"] = "OpenGL",
        ["ren.angle"] = "ANGLE（Direct3D 上の OpenGL）",
        ["ren.d3d9"] = "Direct3D 9（最終手段）",
        ["ren.note"] = "映像が真っ黒またはカクつく場合は、上から順に試してください。",
        ["ren.restartTitle"] = "再起動で反映されます",
        ["ren.restartBody"] =
            "mpv はグラフィックスコンテキストを起動時に一度だけ構築するため、"
            + "レンダラーの変更は次回の起動時に反映されます。" + Sep + "今すぐ再起動しますか？",

        ["menu.audio"] = "音声(&A)",
        ["audio.track"] = "音声トラック",
        ["audio.mute"] = "ミュート",
        ["audio.volUp"] = "音量を上げる",
        ["audio.volDown"] = "音量を下げる",

        ["menu.subtitle"] = "字幕(&S)",
        ["sub.track"] = "字幕トラック",
        ["sub.load"] = "字幕ファイルを追加...",
        ["sub.none"] = "オフ",

        ["menu.vr"] = "VR(&V)",
        ["vr.geometry"] = "投影方式",
        ["vr.stereo"] = "立体方式",
        ["vr.eye"] = "表示する目",
        ["vr.fovIn"] = "視野を狭く",
        ["vr.fovOut"] = "視野を広く",
        ["vr.fovReset"] = "視野をリセット",
        ["vr.resetView"] = "視点をリセット",
        ["vr.recenter"] = "頭の中心を設定し直す",
        ["vr.tracking"] = "ヘッドトラッキング",
        ["vr.sensitivity"] = "トラッキング感度",
        ["vr.sensLower"] = "感度を下げる",
        ["vr.sensHigher"] = "感度を上げる",
        ["vr.sensReset"] = "既定に戻す",
        ["vr.panel"] = "モードパネルを表示",

        ["menu.camera"] = "カメラ(&C)",
        ["cam.test"] = "カメラをテスト...",
        ["cam.busyBody"] =
            "ヘッドトラッキングがカメラを使用中です。カメラは同時に一つのプログラムからしか"
            + "開けません。" + Sep
            + "ヘッドトラッキングをオフにしてから、もう一度お試しください。",
        ["cam.startFailedBody"] =
            "カメラを開始できませんでした。" + Sep
            + "顔検出モデルが無い場合は tools\\install-models.bat を実行してください。"
            + "それ以外の場合は、カメラが他のアプリに使われていないか、Windows の設定 > "
            + "プライバシー > カメラ でアクセスが許可されているかをご確認ください。",

        ["menu.view"] = "表示(&W)",
        ["view.fullscreen"] = "全画面表示",
        ["view.onTop"] = "常に手前に表示",
        ["view.shape"] = "ウィンドウ比率",
        ["view.stats"] = "再生統計情報",

        ["menu.help"] = "ヘルプ(&H)",
        ["help.keys"] = "キーボードショートカット",
        ["help.about"] = "バージョン情報",

        ["geo.flat"] = "平面（VR オフ）",
        ["geo.180"] = "180",
        ["geo.360"] = "360",
        ["geo.fisheye"] = "魚眼",
        ["geo.cylindrical"] = "円筒",
        ["geo.eac"] = "等角キューブマップ",

        ["stereo.mono"] = "2D（単眼）",
        ["stereo.sbs"] = "3D 左右",
        ["stereo.tb"] = "3D 上下",

        ["eye.left"] = "左目",
        ["eye.right"] = "右目",
        ["eye.both"] = "両目（ヘッドセット用）",

        ["dlg.videoFiles"] = "動画ファイル",
        ["dlg.subFiles"] = "字幕ファイル",
        ["dlg.allFiles"] = "すべてのファイル",
        ["dlg.urlTitle"] = "ネットワークストリームを開く",
        ["dlg.urlPrompt"] = "アドレス：",
        ["dlg.ok"] = "OK",
        ["dlg.cancel"] = "キャンセル",
        ["dlg.trackNone"] = "なし",

        ["osd.recentred"] = "ヘッドトラッキング：中心を設定し直しました",
        ["osd.viewReset"] = "ヘッドトラッキング：視点をリセットしました",
        ["osd.trackingOn"] = "ヘッドトラッキング：オン",
        ["osd.trackingOff"] = "ヘッドトラッキング：オフ",
        ["osd.trackingLost"] = "ヘッドトラッキング：信号が途絶えました —— 視点を保持します",
        ["osd.trackingBack"] = "ヘッドトラッキング：信号が回復しました",
        ["osd.gain"] = "感度  水平 {0}°  垂直 {1}°",
        ["osd.decoder"] = "デコード：{0}",
        ["osd.decoderNeedsRenderer"] = "デコード：{0} には {1} レンダラーが必要です —— ソフトウェアに切り替えます",
        ["osd.decoderUnavailable"] = "デコード：この環境では {0} を利用できません —— ソフトウェアを使用します",
        ["osd.renderer"] = "レンダラー：{0} —— 再起動後に反映されます",
        ["osd.vrMode"] = "VR：{0}",
        ["osd.comboUnsupported"] = "{0} + {1} は未対応です —— {2} を使用します",
        ["mode.flatOff"] = "平面（VR オフ）",

        ["about.body"] =
            "180/360 VR 動画のためのオープンソース PC プレーヤー。\n" +
            "デコードと描画は mpv、投影は mpv360、コントロールバーは uosc によるものです。\n\n" +
            "GNU General Public License v3 以降のもとで公開されています。\n" +
            "本プログラムには一切の保証がありません。",
        ["keys.body"] =
            "プレーヤーウィンドウ内\n" +
            "  左ドラッグ       視点を回す\n" +
            "  Space            再生 / 一時停止\n" +
            "  F / Esc          全画面表示の切り替え\n" +
            "  左 / 右          5 秒シーク\n" +
            "  Tab              モードパネル\n" +
            "  Ctrl+Shift+G     投影方式を切り替え\n" +
            "  Ctrl+Shift+D     立体方式を切り替え\n" +
            "  Ctrl+Shift+Y     表示する目を切り替え\n" +
            "  Ctrl+Shift+R     頭の中心を設定し直す\n" +
            "  Ctrl+Shift+V     視点をリセット\n" +
            "  Ctrl+Shift+H     ヘッドトラッキングのオン / オフ\n" +
            "  Ctrl+Shift+I     再生統計情報\n" +
            "  Ctrl+Shift+B     次のレンダラー（映像が真っ黒なときに）\n",
    };

    private static readonly Dictionary<string, string> Korean = new()
    {
        ["menu.file"] = "미디어(&F)",
        ["file.open"] = "파일 열기...",
        ["file.openUrl"] = "네트워크 스트림 열기...",
        ["file.close"] = "파일 닫기",
        ["file.exit"] = "종료",

        ["menu.playback"] = "재생(&P)",
        ["play.playPause"] = "재생 / 일시정지",
        ["play.stop"] = "정지",
        ["play.back10"] = "10초 뒤로",
        ["play.forward30"] = "30초 앞으로",
        ["play.prevFrame"] = "이전 프레임",
        ["play.nextFrame"] = "다음 프레임",
        ["play.speed"] = "재생 속도",
        ["play.speedDown"] = "느리게",
        ["play.speedUp"] = "빠르게",
        ["play.speedReset"] = "보통",
        ["play.loop"] = "한 파일 반복",
        ["play.prevFile"] = "이전 파일",
        ["play.nextFile"] = "다음 파일",

        ["menu.decoder"] = "하드웨어 디코딩",
        ["dec.default"] = "기본값(권장)",
        ["dec.auto"] = "자동",
        ["dec.needs"] = "{0} 렌더러 필요",
        ["dec.switchTitle"] = "렌더러도 바꿀까요?",
        ["dec.switchBody"] =
            "{0}이(가) 현재 렌더러에 프레임을 전달할 수 없어 mpv가 소프트웨어 "
            + "디코딩으로 대체했습니다." + Sep
            + "{1} 렌더러가 필요합니다. “{2}”(으)로 바꿀까요?",
        ["dec.d3d11va"] = "Direct3D 11",
        ["dec.dxva2"] = "DXVA2(구형 GPU)",
        ["dec.nvdec"] = "NVDEC(NVIDIA)",
        ["dec.vulkan"] = "Vulkan",
        ["dec.off"] = "소프트웨어(GPU 디코딩 안 함)",

        ["menu.renderer"] = "렌더러",
        ["ren.default"] = "기본값 - gpu-next / D3D11",
        ["ren.compat"] = "호환 - gpu / D3D11",
        ["ren.vulkan"] = "Vulkan(최신 API, 대개 가장 빠름)",
        ["ren.opengl"] = "OpenGL",
        ["ren.angle"] = "ANGLE(Direct3D 기반 OpenGL)",
        ["ren.d3d9"] = "Direct3D 9(최후의 수단)",
        ["ren.note"] = "화면이 검거나 끊기면 위에서부터 차례로 시도해 보세요.",
        ["ren.restartTitle"] = "다시 시작해야 적용됩니다",
        ["ren.restartBody"] =
            "mpv는 그래픽 컨텍스트를 시작할 때 한 번만 만들기 때문에, 렌더러 변경은 "
            + "플레이어를 다시 시작할 때 적용됩니다." + Sep + "지금 다시 시작할까요?",

        ["menu.audio"] = "오디오(&A)",
        ["audio.track"] = "오디오 트랙",
        ["audio.mute"] = "음소거",
        ["audio.volUp"] = "음량 높이기",
        ["audio.volDown"] = "음량 낮추기",

        ["menu.subtitle"] = "자막(&S)",
        ["sub.track"] = "자막 트랙",
        ["sub.load"] = "자막 파일 추가...",
        ["sub.none"] = "끔",

        ["menu.vr"] = "VR(&V)",
        ["vr.geometry"] = "투영 방식",
        ["vr.stereo"] = "입체 방식",
        ["vr.eye"] = "표시할 눈",
        ["vr.fovIn"] = "시야 좁게",
        ["vr.fovOut"] = "시야 넓게",
        ["vr.fovReset"] = "시야 초기화",
        ["vr.resetView"] = "시점 초기화",
        ["vr.recenter"] = "머리 중심 재설정",
        ["vr.tracking"] = "헤드 트래킹",
        ["vr.sensitivity"] = "트래킹 감도",
        ["vr.sensLower"] = "감도 낮추기",
        ["vr.sensHigher"] = "감도 높이기",
        ["vr.sensReset"] = "기본값으로",
        ["vr.panel"] = "모드 패널 표시",

        ["menu.camera"] = "카메라(&C)",
        ["cam.test"] = "카메라 테스트...",
        ["cam.busyBody"] =
            "헤드 트래킹이 카메라를 사용 중입니다. 카메라는 한 번에 하나의 프로그램만 열 수 "
            + "있습니다." + Sep
            + "헤드 트래킹을 끈 뒤 다시 시도하세요.",
        ["cam.startFailedBody"] =
            "카메라를 시작하지 못했습니다." + Sep
            + "얼굴 검출 모델이 없다면 tools\\install-models.bat 을 실행하세요. 그렇지 "
            + "않다면 다른 프로그램이 카메라를 쓰고 있지 않은지, Windows 설정 > 개인 정보 > "
            + "카메라 에서 접근이 허용되어 있는지 확인하세요.",

        ["menu.view"] = "보기(&W)",
        ["view.fullscreen"] = "전체 화면",
        ["view.onTop"] = "항상 위에 표시",
        ["view.shape"] = "창 비율",
        ["view.stats"] = "재생 통계",

        ["menu.help"] = "도움말(&H)",
        ["help.keys"] = "키보드 단축키",
        ["help.about"] = "정보",

        ["geo.flat"] = "평면(VR 끔)",
        ["geo.180"] = "180",
        ["geo.360"] = "360",
        ["geo.fisheye"] = "어안",
        ["geo.cylindrical"] = "원통",
        ["geo.eac"] = "등각 큐브맵",

        ["stereo.mono"] = "2D(단안)",
        ["stereo.sbs"] = "3D 좌우",
        ["stereo.tb"] = "3D 상하",

        ["eye.left"] = "왼쪽 눈",
        ["eye.right"] = "오른쪽 눈",
        ["eye.both"] = "양쪽 눈(헤드셋 전용)",

        ["dlg.videoFiles"] = "동영상 파일",
        ["dlg.subFiles"] = "자막 파일",
        ["dlg.allFiles"] = "모든 파일",
        ["dlg.urlTitle"] = "네트워크 스트림 열기",
        ["dlg.urlPrompt"] = "주소:",
        ["dlg.ok"] = "확인",
        ["dlg.cancel"] = "취소",
        ["dlg.trackNone"] = "없음",

        ["osd.recentred"] = "헤드 트래킹: 중심을 재설정했습니다",
        ["osd.viewReset"] = "헤드 트래킹: 시점을 초기화했습니다",
        ["osd.trackingOn"] = "헤드 트래킹: 켬",
        ["osd.trackingOff"] = "헤드 트래킹: 끔",
        ["osd.trackingLost"] = "헤드 트래킹: 신호가 끊겼습니다 —— 시점을 유지합니다",
        ["osd.trackingBack"] = "헤드 트래킹: 신호가 복구되었습니다",
        ["osd.gain"] = "감도  수평 {0}°  수직 {1}°",
        ["osd.decoder"] = "디코딩: {0}",
        ["osd.decoderNeedsRenderer"] = "디코딩: {0}에는 {1} 렌더러가 필요합니다 —— 소프트웨어로 전환합니다",
        ["osd.decoderUnavailable"] = "디코딩: 이 환경에서는 {0}을(를) 쓸 수 없습니다 —— 소프트웨어를 사용합니다",
        ["osd.renderer"] = "렌더러: {0} —— 다시 시작하면 적용됩니다",
        ["osd.vrMode"] = "VR: {0}",
        ["osd.comboUnsupported"] = "{0} + {1}은(는) 지원하지 않습니다 —— {2}을(를) 사용합니다",
        ["mode.flatOff"] = "평면(VR 끔)",

        ["about.body"] =
            "180/360 VR 영상을 위한 오픈 소스 PC 플레이어.\n" +
            "디코딩과 렌더링은 mpv, 투영은 mpv360, 컨트롤 바는 uosc를 사용합니다.\n\n" +
            "GNU General Public License v3 이상에 따라 배포됩니다.\n" +
            "이 프로그램은 어떠한 보증도 제공하지 않습니다.",
        ["keys.body"] =
            "플레이어 창에서\n" +
            "  왼쪽 드래그      시점 돌리기\n" +
            "  Space            재생 / 일시정지\n" +
            "  F / Esc          전체 화면 켜기 / 끄기\n" +
            "  왼쪽 / 오른쪽    5초 이동\n" +
            "  Tab              모드 패널\n" +
            "  Ctrl+Shift+G     투영 방식 전환\n" +
            "  Ctrl+Shift+D     입체 방식 전환\n" +
            "  Ctrl+Shift+Y     표시할 눈 전환\n" +
            "  Ctrl+Shift+R     머리 중심 재설정\n" +
            "  Ctrl+Shift+V     시점 초기화\n" +
            "  Ctrl+Shift+H     헤드 트래킹 켜기 / 끄기\n" +
            "  Ctrl+Shift+I     재생 통계\n" +
            "  Ctrl+Shift+B     다음 렌더러(화면이 검을 때)\n",
    };
}
