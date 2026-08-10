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
local tracker = { face = false, hand = false, hand_ready = false }

local ON_COLOR = '&H66E06A&'      -- BGR: green, "this is driving the view"
local OFF_COLOR = '&H707070&'     -- grey, present but idle
local DIM_COLOR = '&H3A3A3A&'     -- very dim, feature does not exist yet

local function status_color(on, ready)
    if not ready then return DIM_COLOR end
    return on and ON_COLOR or OFF_COLOR
end

-- Outline in both states, and only a part-transparent fill when on.
--
-- A solid fill was the obvious choice and it destroyed the icons: the head was
-- filled in the same colour as its own eyes and mouth, so the face became a
-- featureless blob, and the hand's fingers merged into its palm. Keeping the
-- border means the silhouette survives being lit, and the fill still makes
-- on/off obvious at a glance.
local function shape_style(ass, colour, on, s)
    ass:append(string.format('{\\1c%s\\3c%s\\bord%.1f\\shad0\\1a%s}',
        colour, colour, 1.6 * s, on and '&H99&' or '&HFF&'))
end

local function draw_face(ass, cx, cy, r, on, ready)
    local colour = status_color(on, ready)

    ass:new_event()
    ass:pos(cx, cy)
    shape_style(ass, colour, on, r / 11)
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

local function draw_hand(ass, cx, cy, r, on, ready)
    local colour = status_color(on, ready)

    ass:new_event()
    ass:pos(cx, cy)
    shape_style(ass, colour, on, r / 11)
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
    -- Hand furthest right so the face keeps the same place when the hand icon
    -- eventually becomes interactive.
    draw_hand(ass, osd_w - pad - r, pad + r, r, tracker.hand, tracker.hand_ready)
    draw_face(ass, osd_w - pad - r - gap, pad + r, r, tracker.face, true)
    status_overlay.data = ass.text
    status_overlay:update()
end

-- face: on|off, hand: on|off|na  ("na" = gesture control not implemented yet)
mp.register_script_message('tracker-state', function(face, hand)
    tracker.face = face == 'on'
    tracker.hand = hand == 'on'
    tracker.hand_ready = hand ~= 'na'
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
