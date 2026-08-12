-- vrmenu.lua — in-video mode switcher for VRHeadTrackingPlayer.
--
-- Draws a conventional VR-player mode bar (geometry x stereo packing x eye)
-- over the video and lets you click it. It holds no state of its own: the
-- bridge process owns the mode, this script just renders whatever the bridge
-- last broadcast and sends requests back.
--
-- Why not just use mpv360's own keys: it exposes one flat list of ten
-- projections where "180" and "side-by-side" are entangled in a single entry.
-- Users think in two axes, so that is what this shows.
--
-- Talks to the bridge with:   script-message headtrack <verb> [arg]
-- Receives from the bridge:   script-message-to vrmenu vr-state <g> <s> <eye> <supported>

local mp = require 'mp'
local assdraw = require 'mp.assdraw'
local msg = require 'mp.msg'
local utils = require 'mp.utils'

local opts = {
    toggle_key = 'Tab',
    -- Seconds the bar stays up after the last interaction. 0 = until dismissed.
    auto_hide = 6,
    scale = 1.0,
    -- Pop the bar up automatically when a file opens. On by default because a
    -- mode switcher nobody knows about is the same as no mode switcher: there
    -- is nothing on screen to suggest a hidden key exists.
    show_on_load = true,
    -- Always-visible clickable chip in the corner showing the current mode.
    -- Redundant once the uosc control-bar button is there, so off by default;
    -- turn it on if you run without uosc.
    show_indicator = false,
    -- Set by the bridge from the Windows UI language (see Localization.cs);
    -- mpv itself has no idea what locale the machine is in. Override with
    -- language=en / zh-hans / zh-hant / ja / ko in script-opts/vrmenu.conf.
    language = 'en',
    -- Left-drag the picture to look around.
    drag_to_look = true,
}
require('mp.options').read_options(opts, 'vrmenu')

-------------------------------------------------------------------- intl ----

-- Only the handful of strings this script draws. uosc and mpv localise
-- themselves; the bridge points all three at the same language.
local STRINGS = {
    ['zh-hans'] = {
        Projection = '投影', Stereo = '立体', Eye = '眼别', Fov = '视角',
        Flat = '平面', ['180'] = '180', ['360'] = '360',
        Fisheye = '鱼眼', Cylinder = '柱面', EAC = 'EAC',
        ['2D'] = '2D', ['3D SBS'] = '3D 左右', ['3D TB'] = '3D 上下',
        L = '左', R = '右', Both = '双眼',
        mono_source = '(单目片源)',
        reset = '复位',
        hint = '%s 关闭   ·   Ctrl+Shift+G / D / Y 不开菜单直接切换   ·   拖动画面可转视角',
        not_connected = '桥接层未连接 —— 请通过 VR Flat Player 启动播放',
        ['no hand'] = '未看到手', ['open palm'] = '张开手掌', ['fist'] = '握拳',
        ['point'] = '食指', ['thumb'] = '拇指',
        Gestures = '手势',
        ['fist - play / pause'] = '握拳 · 播放 / 暂停',
        ['index left / right - seek'] = '食指左右 · 快退 / 快进',
        ['thumb up / down - volume'] = '拇指上下 · 音量',
        ['thumb up / down - view'] = '拇指上下 · 视野',
        ['wave left / right - file'] = '挥手左右 · 上一部 / 下一部',
        ['hold palm - leave gestures'] = '手掌静止一秒 · 退出手势',
        ['hand at the edge of the picture'] = '手快出画面了',
        ['no camera hand'] = '一直没看到手 —— 检查摄像头角度和光线',
    },
    ['zh-hant'] = {
        Projection = '投影', Stereo = '立體', Eye = '眼別', Fov = '視角',
        Flat = '平面', ['180'] = '180', ['360'] = '360',
        Fisheye = '魚眼', Cylinder = '柱面', EAC = 'EAC',
        ['2D'] = '2D', ['3D SBS'] = '3D 左右', ['3D TB'] = '3D 上下',
        L = '左', R = '右', Both = '雙眼',
        mono_source = '(單目片源)',
        reset = '復位',
        hint = '%s 關閉   ·   Ctrl+Shift+G / D / Y 不開選單直接切換   ·   拖動畫面可轉視角',
        not_connected = '橋接層未連線 —— 請透過 VR Flat Player 啟動播放',
        ['no hand'] = '未看到手', ['open palm'] = '張開手掌', ['fist'] = '握拳',
        ['point'] = '食指', ['thumb'] = '拇指',
        Gestures = '手勢',
        ['fist - play / pause'] = '握拳 · 播放 / 暫停',
        ['index left / right - seek'] = '食指左右 · 快退 / 快進',
        ['thumb up / down - volume'] = '拇指上下 · 音量',
        ['thumb up / down - view'] = '拇指上下 · 視野',
        ['wave left / right - file'] = '揮手左右 · 上一部 / 下一部',
        ['hold palm - leave gestures'] = '手掌靜止一秒 · 退出手勢',
        ['hand at the edge of the picture'] = '手快出畫面了',
        ['no camera hand'] = '一直沒看到手 —— 檢查攝影機角度和光線',
    },
    ['ja'] = {
        Projection = '投影', Stereo = '立体', Eye = '目', Fov = '視野',
        Flat = '平面', ['180'] = '180', ['360'] = '360',
        Fisheye = '魚眼', Cylinder = '円筒', EAC = 'EAC',
        ['2D'] = '2D', ['3D SBS'] = '3D 左右', ['3D TB'] = '3D 上下',
        L = '左', R = '右', Both = '両目',
        mono_source = '(単眼ソース)',
        reset = 'リセット',
        hint = '%s 閉じる   ·   Ctrl+Shift+G / D / Y でパネルを開かずに切替   ·   ドラッグで視点を回す',
        not_connected = 'ブリッジ未接続 —— VR Flat Player から起動してください',
        ['no hand'] = '手が見えません', ['open palm'] = '開いた手', ['fist'] = '握りこぶし',
        ['point'] = '人差し指', ['thumb'] = '親指',
        Gestures = 'ジェスチャー',
        ['fist - play / pause'] = 'こぶし · 再生 / 一時停止',
        ['index left / right - seek'] = '人差し指 左右 · 早戻し / 早送り',
        ['thumb up / down - volume'] = '親指 上下 · 音量',
        ['thumb up / down - view'] = '親指 上下 · 視野',
        ['wave left / right - file'] = '手を振る 左右 · 前 / 次のファイル',
        ['hold palm - leave gestures'] = '手のひら静止 1 秒 · ジェスチャー終了',
        ['hand at the edge of the picture'] = '手が画面の端にあります',
        ['no camera hand'] = '手が見つかりません —— カメラの向きと明るさを確認',
    },
    ['ko'] = {
        Projection = '투영', Stereo = '입체', Eye = '눈', Fov = '시야',
        Flat = '평면', ['180'] = '180', ['360'] = '360',
        Fisheye = '어안', Cylinder = '원통', EAC = 'EAC',
        ['2D'] = '2D', ['3D SBS'] = '3D 좌우', ['3D TB'] = '3D 상하',
        L = '좌', R = '우', Both = '양안',
        mono_source = '(단안 소스)',
        reset = '초기화',
        hint = '%s 닫기   ·   Ctrl+Shift+G / D / Y 로 패널 없이 전환   ·   드래그하여 시점 회전',
        not_connected = '브리지가 연결되지 않았습니다 —— VR Flat Player 로 실행하세요',
        ['no hand'] = '손이 보이지 않음', ['open palm'] = '편 손바닥', ['fist'] = '주먹',
        ['point'] = '검지', ['thumb'] = '엄지',
        Gestures = '제스처',
        ['fist - play / pause'] = '주먹 · 재생 / 일시정지',
        ['index left / right - seek'] = '검지 좌우 · 되감기 / 빨리감기',
        ['thumb up / down - volume'] = '엄지 상하 · 음량',
        ['thumb up / down - view'] = '엄지 상하 · 시야',
        ['wave left / right - file'] = '손 흔들기 좌우 · 이전 / 다음 파일',
        ['hold palm - leave gestures'] = '손바닥 1초 정지 · 제스처 종료',
        ['hand at the edge of the picture'] = '손이 화면 가장자리에 있습니다',
        ['no camera hand'] = '손을 찾지 못했습니다 —— 카메라 각도와 조명을 확인',
    },
}

local function tr(key)
    local table_ = STRINGS[opts.language]
    return (table_ and table_[key]) or key
end

------------------------------------------------------------------- state ----

-- Mirrors of what the bridge told us. Never written by click handlers: we send
-- a request and redraw when the broadcast comes back, so the bar can never show
-- a mode the player is not actually in.
-- Seeded to the configured fallback (VR180 side-by-side), not to 360 mono.
-- These are only on screen for the moment before the first vr-state arrives,
-- but showing 360/2D there contradicted the player's own default.
local state = {
    geometry = 'Deg180',
    stereo = 'SideBySide',
    eye = 'Left',
    supported = { Mono = true, SideBySide = true, TopBottom = true },
    fov = 80,           -- matches PlayerModeController; overwritten by vr-state
    connected = false,
}

local visible = false
local hide_timer = nil
local overlay = mp.create_osd_overlay('ass-events')

local GEOMETRIES = {
    { id = 'Flat',        label = 'Flat' },
    { id = 'Deg180',      label = '180' },
    { id = 'Deg360',      label = '360' },
    { id = 'Fisheye',     label = 'Fisheye' },
    { id = 'Cylindrical', label = 'Cylinder' },
    { id = 'Eac',         label = 'EAC' },
}

local STEREOS = {
    { id = 'Mono',       label = '2D' },
    { id = 'SideBySide', label = '3D SBS' },
    { id = 'TopBottom',  label = '3D TB' },
}

local EYES = {
    { id = 'Left',  label = 'L' },
    { id = 'Right', label = 'R' },
    { id = 'Both',  label = 'Both' },
}

-- Clickable regions, rebuilt on every draw: { x1, y1, x2, y2, action }
local hitboxes = {}

------------------------------------------------------------------ layout ----

local COLORS = {
    panel      = '&H1A1A1A&',
    text       = '&HDDDDDD&',
    text_dim   = '&H707070&',
    active_bg  = '&H4A9E2F&',   -- BGR, not RGB
    active_fg  = '&HFFFFFF&',
    label      = '&H909090&',
}

local function ass_escape(s)
    return tostring(s):gsub('\\', '\\\239\187\191'):gsub('{', '\\{'):gsub('}', '\\}')
end

local function draw_box(ass, x1, y1, x2, y2, color, alpha)
    ass:new_event()
    ass:append(string.format('{\\pos(0,0)\\bord0\\shad0\\1c%s\\1a&H%02X&}', color, alpha or 0))
    ass:draw_start()
    ass:rect_cw(x1, y1, x2, y2)
    ass:draw_stop()
end

local function draw_text(ass, x, y, size, color, text, align)
    ass:new_event()
    ass:append(string.format('{\\an%d\\pos(%.1f,%.1f)\\fs%.1f\\bord0\\shad0\\1c%s}',
        align or 4, x, y, size, color))
    ass:append(ass_escape(text))
end

-- Pill geometry, all derived from the scale factor in one place so the measure
-- and draw passes cannot disagree.
local function metrics(s)
    return {
        s = s,
        pad_x = 14 * s,
        pill_h = 30 * s,
        gap = 8 * s,
        label_w = 92 * s,
        char_w = 10 * s,      -- rough advance width at font size 18*s
        font = 18 * s,
        label_font = 17 * s,
    }
end

--- Approximate rendered width in character units.
--- `#s` counts BYTES, so a translated label like "投影" would measure 6 and
--- blow the pill out. LuaJIT has no utf8 library, hence the manual decode.
--- CJK codepoints count double because they render roughly twice as wide.
local function display_len(s)
    local n, i = 0, 1
    while i <= #s do
        local b = s:byte(i)
        if b < 0x80 then n, i = n + 1, i + 1
        elseif b < 0xE0 then n, i = n + 1, i + 2
        elseif b < 0xF0 then n, i = n + 2, i + 3
        else n, i = n + 2, i + 4 end
    end
    return n
end

local function pill_width(label, m)
    return display_len(label) * m.char_w + 2 * m.pad_x
end

--- Width needed by one row, including its left-hand label column.
local function measure_row(items, m)
    local w = m.label_w
    for _, item in ipairs(items) do
        w = w + pill_width(tr(item.label), m) + m.gap
    end
    return w - m.gap
end

--- One row of pill buttons. Returns the y coordinate below the row.
local function draw_row(ass, x, y, label, items, current, enabled_fn, action_prefix, geom)
    local pad_x, pill_h, gap = geom.pad_x, geom.pill_h, geom.gap
    draw_text(ass, x, y + pill_h / 2, geom.label_font, COLORS.label, label, 4)

    local cx = x + geom.label_w
    for _, item in ipairs(items) do
        local label = tr(item.label)
        local w = pill_width(label, geom)
        local is_active = (item.id == current)
        local is_enabled = enabled_fn == nil or enabled_fn(item.id)

        local bg = is_active and COLORS.active_bg or COLORS.panel
        draw_box(ass, cx, y, cx + w, y + pill_h, bg, is_active and 30 or 140)

        local fg = COLORS.text
        if is_active then fg = COLORS.active_fg
        elseif not is_enabled then fg = COLORS.text_dim end
        draw_text(ass, cx + w / 2, y + pill_h / 2, geom.font, fg, label, 5)

        if is_enabled then
            hitboxes[#hitboxes + 1] = { cx, y, cx + w, y + pill_h, action_prefix, item.id }
        end
        cx = cx + w + gap
    end
    return y + pill_h + gap
end

--- Current mode as a short string for the corner chip.
local function mode_summary()
    local g = state.geometry
    for _, d in ipairs(GEOMETRIES) do if d.id == g then g = d.label break end end
    if state.geometry == 'Flat' then return 'Flat' end
    local s = state.stereo
    for _, d in ipairs(STEREOS) do if d.id == s then s = d.label break end end
    if state.stereo == 'Mono' then return g .. ' · ' .. s end
    return g .. ' · ' .. s .. ' · ' .. state.eye
end

--- Small always-visible chip so the mode switcher is discoverable at all.
local function draw_indicator(ass, osd_w, osd_h, s)
    local text = 'VR  ' .. mode_summary()
    local h = 26 * s
    local w = (#text * 8.5 + 26) * s
    local x2 = osd_w - 14 * s
    local x1 = x2 - w
    local y1 = 14 * s
    local y2 = y1 + h

    draw_box(ass, x1, y1, x2, y2, '&H0D0D0D&', 90)
    draw_text(ass, (x1 + x2) / 2, (y1 + y2) / 2, 15 * s, COLORS.text, text, 5)
    hitboxes[#hitboxes + 1] = { x1, y1, x2, y2, '__open', nil }
end

local function redraw()
    hitboxes = {}

    local osd_w, osd_h = mp.get_osd_size()
    if not osd_w or osd_w == 0 then return end

    if not visible then
        if opts.show_indicator then
            local s = math.max(0.75, math.min(math.min(osd_w / 1280, osd_h / 720), 2.0)) * opts.scale
            overlay.res_x, overlay.res_y = osd_w, osd_h
            local ass = assdraw.ass_new()
            draw_indicator(ass, osd_w, osd_h, s)
            overlay.data = ass.text
        else
            overlay.data = ''
        end
        overlay:update()
        return
    end

    -- Keep the bar a constant physical size regardless of window size.
    local s = math.min(osd_w / 1280, osd_h / 720)
    s = math.max(0.75, math.min(s, 2.0)) * opts.scale
    local geom = metrics(s)

    -- Pin the overlay's coordinate space to the OSD size so these coordinates
    -- and the mouse-pos we hit-test against are in the same units.
    overlay.res_x = osd_w
    overlay.res_y = osd_h

    local ass = assdraw.ass_new()

    local show_eye = not (state.stereo == 'Mono' or state.geometry == 'Flat')
    local pad = 18 * s

    -- Size the panel to its contents. Guessing fixed dimensions here is what
    -- made the pills overflow the background and run off the bottom.
    -- LuaJIT has no \u{} escapes, so these are raw UTF-8 bytes:
    -- \226\136\146 = U+2212 minus sign, \194\176 = U+00B0 degree sign.
    local FOV_ITEMS = {
        { id = '-10', label = '\226\136\146' },
        { id = 'val', label = string.format('%d\194\176', state.fov) },
        { id = '+10', label = '+' },
        { id = 'reset', label = tr('reset') },
    }

    local content_w = math.max(
        measure_row(GEOMETRIES, geom),
        measure_row(STEREOS, geom),
        show_eye and measure_row(EYES, geom) or 0,
        measure_row(FOV_ITEMS, geom))
    local rows = 4                      -- projection, stereo, eye (or hint), fov
    local panel_w = content_w + 2 * pad
    local panel_h = rows * (geom.pill_h + geom.gap) - geom.gap + 2 * pad

    -- Room for the footer hint line.
    panel_h = panel_h + 20 * s

    local x0 = math.max(8, (osd_w - panel_w) / 2)
    -- Clear uosc's whole bottom stack: timeline + controls row + margins. uosc
    -- draws over this overlay, so anything less and its buttons punch holes in
    -- the panel — measured at ~255 px from the bottom in a 720p window, hence
    -- 275 with a gap. uosc's sizes scale the same way ours do, so this holds.
    local y0 = math.min(osd_h - panel_h - 275 * s, osd_h - panel_h - 8)
    y0 = math.max(8, y0)

    draw_box(ass, x0, y0, x0 + panel_w, y0 + panel_h, '&H0D0D0D&', 55)

    local y = y0 + pad
    local x = x0 + pad

    y = draw_row(ass, x, y, tr('Projection'), GEOMETRIES, state.geometry, nil, 'set-geometry', geom)
    y = draw_row(ass, x, y, tr('Stereo'), STEREOS, state.stereo,
                 function(id) return state.supported[id] end, 'set-stereo', geom)

    if show_eye then
        y = draw_row(ass, x, y, tr('Eye'), EYES, state.eye, nil, 'set-eye', geom)
    else
        draw_text(ass, x, y + geom.pill_h / 2, geom.label_font, COLORS.label, tr('Eye'), 4)
        draw_text(ass, x + geom.label_w, y + geom.pill_h / 2, 16 * s, COLORS.text_dim, tr('mono_source'), 4)
        y = y + geom.pill_h + geom.gap
    end

    -- FOV: -, current value, +, reset. The value pill is display-only.
    y = draw_row(ass, x, y, tr('Fov'), FOV_ITEMS, 'val', nil, '__fov', geom)

    local hint = string.format(tr('hint'), opts.toggle_key)
    if not state.connected then
        hint = tr('not_connected')
    end
    draw_text(ass, x0 + pad, y0 + panel_h - 14 * s, 14 * s,
              state.connected and COLORS.text_dim or '&H5555DD&', hint, 4)

    overlay.data = ass.text
    overlay:update()
end

----------------------------------------------------------------- actions ----

-- Forward declarations. These MUST come before any function that references
-- them, or the reference resolves to a global (nil) instead of the local.
local show
local hide
local publish_button

local function bump_hide_timer()
    if hide_timer then hide_timer:kill(); hide_timer = nil end
    if visible and opts.auto_hide > 0 then
        -- Go through hide() so the mouse binding and the control-bar button's
        -- active state are updated the same way as an explicit close.
        hide_timer = mp.add_timeout(opts.auto_hide, function() hide() end)
    end
end

local function on_click()
    local pos = mp.get_property_native('mouse-pos')
    if not pos or not pos.x then return end
    for _, b in ipairs(hitboxes) do
        if pos.x >= b[1] and pos.x <= b[3] and pos.y >= b[2] and pos.y <= b[4] then
            if b[5] == '__open' then
                show()
            elseif b[5] == '__fov' then
                -- The value pill is display-only; the others map to bridge verbs.
                if b[6] == '-10' then
                    mp.commandv('script-message', 'headtrack', 'adjust-fov', '-10')
                elseif b[6] == '+10' then
                    mp.commandv('script-message', 'headtrack', 'adjust-fov', '10')
                elseif b[6] == 'reset' then
                    mp.commandv('script-message', 'headtrack', 'reset-view')
                end
                bump_hide_timer()
            elseif b[6] ~= nil then
                mp.commandv('script-message', 'headtrack', b[5], b[6])
                bump_hide_timer()
            else
                mp.commandv('script-message', 'headtrack', b[5])
                bump_hide_timer()
            end
            return
        end
    end
end

--- Rebind the mouse at the priority the current state needs.
---
--- Drag-to-look is NOT handled here. uosc takes MBTN_LEFT with a forced
--- binding for its cursor system and does not pass unhandled presses on to us,
--- and out-forcing it would break every uosc button. The bridge watches the
--- physical mouse instead; all this script has to do is tell it when the panel
--- is open so the panel's own buttons are not draggable.
local function rebind_mouse()
    mp.remove_key_binding('vrmenu-click')
    if visible then
        mp.add_forced_key_binding('MBTN_LEFT', 'vrmenu-click', on_click)
    elseif opts.show_indicator then
        mp.add_key_binding('MBTN_LEFT', 'vrmenu-click', on_click)
    end
end

show = function()
    visible = true
    rebind_mouse()
    mp.commandv('script-message', 'headtrack', 'menu-open')
    mp.commandv('script-message', 'headtrack', 'request-state')
    redraw()
    if publish_button then publish_button() end
    bump_hide_timer()
end

hide = function()
    visible = false
    if hide_timer then hide_timer:kill(); hide_timer = nil end
    rebind_mouse()
    mp.commandv('script-message', 'headtrack', 'menu-closed')
    redraw()
    if publish_button then publish_button() end
end

local function toggle()
    if visible then hide() else show() end
end

------------------------------------------------------------- bridge link ----

--- Publish the control-bar button. uosc renders `button:vrmode` from whatever
--- we last sent here, so the bar shows the live mode rather than a static icon.
--- Harmless when uosc is not installed: the message simply goes nowhere.
publish_button = function()
    -- Badge has to stay very short: uosc centres it on the button, so anything
    -- long spills over the neighbouring controls. The full
    -- "180 · 3D SBS · Left" did, and even "180·3D" overlapped. Geometry only;
    -- the rest is in the tooltip and in the menu itself.
    local badge = nil
    for _, d in ipairs(GEOMETRIES) do
        if d.id == state.geometry then badge = d.label break end
    end

    mp.commandv('script-message-to', 'uosc', 'set-button', 'vrmode', utils.format_json({
        icon = 'view_in_ar',
        badge = badge,
        active = visible,
        tooltip = 'VR mode: ' .. mode_summary() .. '   (' .. opts.toggle_key .. ')',
        command = 'script-binding vrmenu/toggle-menu',
    }))
end

------------------------------------------------------------- status ----

-- Always-visible face and hand indicators in the top right.
--
-- A second overlay, not the menu's. The menu clears its own overlay every time
-- the bar hides, so anything sharing it would blink out with the bar; these are
-- meant to be there all the time.
--
-- Drawn as vectors rather than glyphs because there is no font we can count on
-- across machines for a face or a hand, and shipping one for two icons is not
-- worth it.
local status_overlay = mp.create_osd_overlay('ass-events')
local tracker = { face = 'off', hand = 'off' }

-- Forward declarations. The hand drawing is defined further down, next to the
-- messages that feed it, but redraw_status has to be able to call it — and a
-- Lua local is not visible inside a function body written above it.
local draw_hand_preview
local draw_gesture_legend
local draw_hand_warnings

-- How much of the bottom of the window uosc is currently occupying, as a
-- fraction of its height.
--
-- uosc publishes this for exactly this purpose, and it is non-zero only while
-- the bar is up to stay — which, with `timeline_persistency=paused` in
-- script-opts/uosc.conf, means while the file is paused. That matters here more
-- than it sounds: pausing is the single most-used gesture, so the bar is up for
-- most of the time anyone is gesturing, and the hand panel was being drawn
-- underneath it.
--
-- Reading the property rather than measuring the bar keeps the two from
-- drifting apart when uosc's sizes are changed in its own config.
local osc_bottom = 0

local ON_COLOR = '&H66E06A&'      -- BGR: green, "this is driving the view"
local OFF_COLOR = '&H707070&'     -- grey, switched on but not doing anything
local DIM_COLOR = '&H3A3A3A&'     -- very dim, the feature is switched off
local PAUSED_COLOR = '&H20B0FF&'  -- BGR: amber, standing aside for the moment

-- Four states, and amber is the one that earns the extra colour.
--
-- Head tracking stops driving the view while gesture mode is on, and it has to
-- look different from having been switched off: one ends by itself in a second
-- or two and the other is waiting for the user to do something. Drawn the same
-- as off, they would be told apart only by remembering what you last pressed.
--
-- Returns the colour and whether the silhouette is filled, because a filled
-- shape is what reads as "active" across the room and only two of the four are.
local function status_color(state)
    if state == 'on' then return ON_COLOR, true end
    if state == 'paused' then return PAUSED_COLOR, true end
    if state == 'idle' then return OFF_COLOR, false end
    return DIM_COLOR, false
end

-- Outline in both states, and only a part-transparent fill when on.
--
-- A solid fill was the obvious choice and it destroyed the icons: the head was
-- filled in the same colour as its own eyes and mouth, so the face became a
-- featureless blob, and the hand's fingers merged into its palm. Keeping the
-- border means the silhouette survives being lit, and the fill still makes
-- on/off obvious at a glance.
local function shape_style(ass, colour, filled, s)
    ass:append(string.format('{\\1c%s\\3c%s\\bord%.1f\\shad0\\1a%s}',
        colour, colour, 1.6 * s, filled and '&H99&' or '&HFF&'))
end

local function draw_face(ass, cx, cy, r, state)
    local colour, filled = status_color(state)

    ass:new_event()
    ass:pos(cx, cy)
    shape_style(ass, colour, filled, r / 11)
    ass:draw_start()
    ass:round_rect_cw(-r, -r, r, r, r)      -- radius = half the box, i.e. a circle
    ass:draw_stop()

    -- Eyes and mouth, always solid so the face still reads when the head is
    -- only an outline.
    local e = r * 0.30
    ass:new_event()
    ass:pos(cx, cy)
    ass:append(string.format('{\\1c%s\\bord0\\shad0}', colour))
    ass:draw_start()
    ass:round_rect_cw(-e - r * 0.12, -r * 0.30, -e + r * 0.12, -r * 0.06, r * 0.12)
    ass:round_rect_cw(e - r * 0.12, -r * 0.30, e + r * 0.12, -r * 0.06, r * 0.12)
    ass:rect_cw(-r * 0.34, r * 0.26, r * 0.34, r * 0.40)
    ass:draw_stop()
end

local function draw_hand(ass, cx, cy, r, state)
    local colour, filled = status_color(state)

    ass:new_event()
    ass:pos(cx, cy)
    shape_style(ass, colour, filled, r / 11)
    ass:draw_start()
    -- Palm, then three fingers and a thumb. Crude, but at this size the
    -- silhouette is the whole message.
    ass:round_rect_cw(-r * 0.55, -r * 0.15, r * 0.55, r * 0.85, r * 0.25)
    ass:round_rect_cw(-r * 0.45, -r * 0.85, -r * 0.15, r * 0.10, r * 0.15)
    ass:round_rect_cw(-r * 0.05, -r * 1.00, r * 0.25, r * 0.10, r * 0.15)
    ass:round_rect_cw(r * 0.32, -r * 0.75, r * 0.60, r * 0.10, r * 0.14)
    ass:draw_stop()
end

local function redraw_status()
    local osd_w, osd_h = mp.get_osd_size()
    if not osd_w or osd_w == 0 then return end

    status_overlay.res_x, status_overlay.res_y = osd_w, osd_h

    local s = math.max(0.75, math.min(math.min(osd_w / 1280, osd_h / 720), 2.0)) * opts.scale
    local r = 11 * s
    local pad = 18 * s
    local gap = 34 * s

    local ass = assdraw.ass_new()
    -- Hand furthest right so the face keeps the same place whatever the hand is
    -- doing.
    draw_hand(ass, osd_w - pad - r, pad + r, r, tracker.hand)
    draw_face(ass, osd_w - pad - r - gap, pad + r, r, tracker.face)

    -- Bottom right, lifted clear of uosc's control bar when it is up. 42 is the
    -- gap the bar leaves when it is *not* up, so the panel never sits on the very
    -- bottom edge either.
    local bottom = osd_h - pad - 42 * s - osc_bottom * osd_h
    local panel_h = draw_hand_preview(ass, osd_w - pad, bottom, s)

    -- Above the panel, sharing its right edge. The legend and the picture of
    -- your own hand are one block: the panel says whether the camera can see
    -- you, the legend says what to do about it.
    draw_gesture_legend(ass, osd_w - pad, bottom - panel_h - 10 * s, s)

    draw_hand_warnings(ass, pad, pad, s)

    status_overlay.data = ass.text
    status_overlay:update()
end

-- uosc's bar coming and going has to move the panel, and nothing else redraws
-- this overlay on that event.
mp.observe_property('user-data/osc/margins', 'native', function(_, v)
    local b = (type(v) == 'table' and tonumber(v.b)) or 0
    if b ~= osc_bottom then
        osc_bottom = b
        redraw_status()
    end
end)

-- face: on | paused (yielding to gesture mode) | off
-- hand: on (gesture mode) | idle (watching for the palm) | off
mp.register_script_message('tracker-state', function(face, hand)
    tracker.face = face or 'off'
    tracker.hand = hand or 'off'
    redraw_status()
end)

------------------------------------------------------------ hand preview ----

-- What the camera can see of your hand, in the corner of the picture.
--
-- Head tracking needs nothing like this because it has the picture itself as
-- feedback: turn your head, the view moves, and if it does not you know at once.
-- Gestures have no such channel. Until something fires there is nothing on
-- screen at all, so "the pose was not recognised" and "my hand is outside the
-- frame" and "the camera never opened" all look identical — and the natural
-- response to all three is to keep making the gesture harder, which fixes none
-- of them.
--
-- So this draws the 21 landmarks as they arrive. It is deliberately not a
-- diagnostic: no numbers, no finger states, no confidence. The only questions
-- it answers are "does it see my hand" and "is my hand in the right place",
-- which are the two the picture cannot answer on its own.
local hand_view = {
    state = 'off',    -- off | looking | armed
    hold = 0,         -- 0..1 through the palm hold that toggles gesture mode
    pose = '',
    points = nil,     -- 42 normalised numbers, or nil
    seen_at = -1e9,
    edge = false,     -- part of the hand is against the edge of the picture
    vr = false,       -- which half of the action table applies to the thumb
    warning = nil,    -- a STRINGS key sent by the bridge
    warning_until = 0,
}
local hand_timer = nil

-- MediaPipe's own skeleton. Drawn rather than the points alone because 21 loose
-- dots do not read as a hand at this size, and reading it as a hand at a glance
-- is the entire job.
local BONES = {
    { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 },
    { 0, 5 }, { 5, 6 }, { 6, 7 }, { 7, 8 },
    { 5, 9 }, { 9, 10 }, { 10, 11 }, { 11, 12 },
    { 9, 13 }, { 13, 14 }, { 14, 15 }, { 15, 16 },
    { 13, 17 }, { 17, 18 }, { 18, 19 }, { 19, 20 },
    { 0, 17 },
}

-- Visible while gesture mode is on, and for a couple of seconds around a hand
-- being in view. Not permanently: a panel that is always there stops being
-- noticed, and this one is only wanted at the moment you raise your hand.
local function hand_visible()
    if hand_view.state == 'off' then return false end
    if hand_view.state == 'armed' then return true end
    return (mp.get_time() - hand_view.seen_at) < 2.0
end

--- Returns the height it drew, so what goes above it knows where to start.
function draw_hand_preview(ass, right, bottom, s)
    if not hand_visible() then return 0 end

    -- 16:9, because the camera is, and a hand squashed into a square panel
    -- looks like a hand held wrong.
    local w = 168 * s
    local h = w * 9 / 16
    local x0, y0 = right - w, bottom - h

    local seen = hand_view.points ~= nil
    local colour = hand_view.state == 'armed' and ON_COLOR or OFF_COLOR

    -- Backing plate. Dark enough that the landmarks read over anything.
    --
    -- The first attempt was two thirds transparent, which looked restrained
    -- against a screenshot of dark footage and vanished completely over a bright
    -- one — and "over a bright one" includes every moment this panel exists to
    -- serve, because it is on screen precisely when the user is looking for it.
    ass:new_event()
    ass:pos(0, 0)
    ass:append(string.format('{\\1c&H000000&\\1a&H55&\\3c%s\\3a&H40&\\bord%.1f\\shad0}',
                             colour, 1.2 * s))
    ass:draw_start()
    ass:round_rect_cw(x0, y0, x0 + w, y0 + h, 5 * s)
    ass:draw_stop()

    if seen then
        local p = hand_view.points
        local function px(i) return x0 + p[i * 2 + 1] * w end
        local function py(i) return y0 + p[i * 2 + 2] * h end

        -- Bones as unfilled strokes: a zero-area path with a border and a fully
        -- transparent fill is how ASS draws a line.
        ass:new_event()
        ass:pos(0, 0)
        ass:append(string.format('{\\1a&HFF&\\3c%s\\bord%.1f\\shad0}', colour, 1.6 * s))
        ass:draw_start()
        for _, b in ipairs(BONES) do
            ass:move_to(px(b[1]), py(b[1]))
            ass:line_to(px(b[2]), py(b[2]))
        end
        ass:draw_stop()

        ass:new_event()
        ass:pos(0, 0)
        ass:append(string.format('{\\1c%s\\bord0\\shad0}', colour))
        ass:draw_start()
        for i = 0, 20 do
            local r = 1.8 * s
            ass:round_rect_cw(px(i) - r, py(i) - r, px(i) + r, py(i) + r, r)
        end
        ass:draw_stop()
    end

    -- One line of text, and only ever one: the pose when there is one, or the
    -- fact that no hand is visible when there is not.
    local label = seen and (hand_view.pose ~= '' and tr(hand_view.pose) or '')
                        or tr('no hand')
    if label ~= '' then
        -- Clear of the progress bar along the bottom edge and of the plate's own
        -- border. 5 px was not: screenshotting the panel showed the label's
        -- descenders sitting on the border stroke, which is the sort of thing
        -- that is invisible in the code and obvious in the picture.
        ass:new_event()
        ass:append(string.format('{\\an2\\pos(%.1f,%.1f)\\fs%.1f\\1c%s\\bord%.1f\\3c&H000000&\\shad0}',
                                 x0 + w / 2, y0 + h - 8 * s, 12 * s, colour, 1.2 * s))
        ass:append(ass_escape(label))
    end

    -- How far through the hold that opens and closes gesture mode.
    --
    -- The single most valuable thing on this panel. Holding a palm up for a
    -- second with no acknowledgement is exactly when people conclude the
    -- feature is broken and stop; a bar that visibly fills says "yes, seen,
    -- keep going" and, when it does not move, says the pose is not being read
    -- as an open palm at all.
    if hand_view.hold > 0.01 then
        ass:new_event()
        ass:pos(0, 0)
        ass:append(string.format('{\\1c%s\\bord0\\shad0}', ON_COLOR))
        ass:draw_start()
        ass:rect_cw(x0, y0 + h - 2.5 * s, x0 + w * math.min(1, hand_view.hold), y0 + h)
        ass:draw_stop()
    end

    return h
end

------------------------------------------------------------ hand legend ----

-- The four gestures, listed for as long as gesture mode is on.
--
-- Shown for three seconds on entry in the first version, which is the wrong
-- shape for this. Three seconds is enough to know a list appeared and not
-- enough to read it, and it is gone by the moment it is wanted — the moment
-- after a gesture did the wrong thing. Left up, it costs a corner of the
-- picture during the one mode the user is not watching the film in anyway, and
-- it is the difference between learning the vocabulary and guessing at it.
--
-- One line per gesture, right-aligned against the same edge as the hand panel.
local function legend_lines()
    return {
        tr('fist - play / pause'),
        tr('index left / right - seek'),
        tr(hand_view.vr and 'thumb up / down - view' or 'thumb up / down - volume'),
        tr('wave left / right - file'),
        tr('hold palm - leave gestures'),
    }
end

function draw_gesture_legend(ass, right, bottom, s)
    if hand_view.state ~= 'armed' then return end

    local lines = legend_lines()
    local size = 13 * s
    local step = size * 1.55
    local pad = 8 * s
    local h = #lines * step + pad * 2

    -- No text measurement in this script, so the plate is sized from the longest
    -- line by character count. CJK glyphs are about twice as wide as Latin ones
    -- at the same size, and every language here is one or the other, so counting
    -- bytes and dividing by three lands close enough for a backing plate: UTF-8
    -- spends three bytes on the CJK characters and one on the Latin.
    local widest = 0
    for _, line in ipairs(lines) do
        local cjk = math.floor(#line / 3)
        local latin = #line - cjk * 3
        widest = math.max(widest, cjk * size + latin * size * 0.5)
    end
    local w = widest + pad * 2

    draw_box(ass, right - w, bottom - h, right, bottom, '&H0D0D0D&', 60)

    local y = bottom - h + pad + step / 2
    for _, line in ipairs(lines) do
        ass:new_event()
        ass:append(string.format('{\\an6\\pos(%.1f,%.1f)\\fs%.1f\\1c%s\\bord%.1f\\3c&H000000&\\shad0}',
                                 right - pad, y, size, COLORS.text, 1.2 * s))
        ass:append(ass_escape(line))
        y = y + step
    end
end

---------------------------------------------------------- hand warnings ----

-- The two things that go wrong silently, said out loud in the top left.
--
-- Both are failures with no symptom. A hand crossing the edge of the frame
-- simply stops being tracked, and a camera that cannot see hands at all never
-- reports anything — in both cases every gesture does nothing, which is exactly
-- what a switched-off feature also does. Top left because the hand panel and
-- the tracker icons already own the right-hand side, and because a warning that
-- shares a corner with the thing it is warning about is easy to miss.
function draw_hand_warnings(ass, left, top, s)
    local lines = {}

    if hand_view.state ~= 'off' and hand_view.edge and hand_view.points then
        lines[#lines + 1] = tr('hand at the edge of the picture')
    end
    if hand_view.warning and mp.get_time() < hand_view.warning_until then
        lines[#lines + 1] = tr(hand_view.warning)
    end
    if #lines == 0 then return end

    local size = 14 * s
    local step = size * 1.6
    local y = top + step / 2
    for _, line in ipairs(lines) do
        ass:new_event()
        ass:append(string.format('{\\an4\\pos(%.1f,%.1f)\\fs%.1f\\1c%s\\bord%.1f\\3c&H000000&\\shad0}',
                                 left, y, size, PAUSED_COLOR, 2.0 * s))
        ass:append(ass_escape(line))
        y = y + step
    end
end

-- state: off | looking | armed
-- hold:  0..1 through the palm hold
-- pose:  a STRINGS key, or empty
-- pts:   42 numbers "x,y,x,y,..." normalised to the camera frame, or "-"
-- edge:  'edge' when part of the hand is against the frame border
-- vr:    'vr' or 'flat' — which action table the thumb is on
mp.register_script_message('hand-preview', function(state, hold, pose, pts, edge, vr)
    hand_view.state = state or 'off'
    hand_view.hold = tonumber(hold) or 0
    hand_view.pose = pose or ''
    hand_view.edge = edge == 'edge'
    hand_view.vr = vr == 'vr'

    local p = nil
    if pts and pts ~= '-' then
        p = {}
        for n in pts:gmatch('[^,]+') do p[#p + 1] = tonumber(n) end
        if #p ~= 42 then p = nil end
    end
    hand_view.points = p
    if p then hand_view.seen_at = mp.get_time() end

    -- The panel has to disappear on its own once the hand leaves, and the
    -- bridge stops sending the moment there is nothing to send. A slow timer
    -- while gesture control is on is what closes it.
    if hand_view.state == 'off' then
        if hand_timer then hand_timer:kill(); hand_timer = nil end
    elseif not hand_timer then
        hand_timer = mp.add_periodic_timer(0.5, function() redraw_status() end)
    end
    redraw_status()
end)

-- A one-off warning from the bridge, named by STRINGS key.
--
-- Twelve seconds, which is long for a hint and deliberately so: the bridge only
-- ever sends this once per session, and it is the one message someone who thinks
-- the feature is broken has to actually catch. The timer above is what takes it
-- back down.
mp.register_script_message('hand-warning', function(key)
    hand_view.warning = key
    hand_view.warning_until = mp.get_time() + 12
    if not hand_timer then
        hand_timer = mp.add_periodic_timer(0.5, function() redraw_status() end)
    end
    redraw_status()
end)

-- The icons are positioned from the OSD size, so they have to be redrawn when
-- the window is resized or a differently-shaped file is loaded.
mp.observe_property('osd-dimensions', 'native', function() redraw_status() end)

mp.register_script_message('vr-state', function(geometry, stereo, eye, supported, fov)
    state.geometry = geometry or state.geometry
    state.stereo = stereo or state.stereo
    state.eye = eye or state.eye
    state.fov = tonumber(fov) or state.fov
    state.connected = true

    state.supported = {}
    for id in tostring(supported or ''):gmatch('[^,]+') do
        state.supported[id] = true
    end
    -- Flat has no stereo axis at all; keep every pill clickable so the user can
    -- get back out to a real projection.
    if state.geometry == 'Flat' then
        for _, sdef in ipairs(STEREOS) do state.supported[sdef.id] = true end
    end

    publish_button()
    if visible then redraw() end
end)

--------------------------------------------------------------- bindings -----

mp.add_key_binding(opts.toggle_key, 'toggle-menu', toggle)
mp.add_key_binding(nil, 'show-menu', show)
mp.add_key_binding(nil, 'hide-menu', hide)
mp.add_key_binding(nil, 'cycle-geometry', function()
    mp.commandv('script-message', 'headtrack', 'cycle-geometry')
end)
mp.add_key_binding(nil, 'cycle-stereo', function()
    mp.commandv('script-message', 'headtrack', 'cycle-stereo')
end)
mp.add_key_binding(nil, 'cycle-eye', function()
    mp.commandv('script-message', 'headtrack', 'cycle-eye')
end)

mp.observe_property('osd-dimensions', 'native', function() redraw() end)

mp.register_event('file-loaded', function()
    mp.commandv('script-message', 'headtrack', 'request-state')
    if opts.show_on_load then
        -- Small delay so the bridge's auto-detect has landed and the bar shows
        -- the mode the file actually ended up in, not the previous one.
        mp.add_timeout(0.6, show)
    else
        rebind_mouse()
        redraw()
    end
end)

-- Publish the button and draw immediately, before any file loads, so the bar
-- has something to show the moment it appears.
rebind_mouse()
publish_button()
redraw()

-- uosc forgets managed buttons if it starts after us.
mp.add_timeout(0.5, publish_button)

msg.info('vrmenu loaded — press ' .. opts.toggle_key .. ' for the mode bar')
