-- wezterm-quickterminal: a drop-down quick terminal for WezTerm on Windows, configured from .wezterm.lua.
--
--   local quickterminal = wezterm.plugin.require("https://github.com/iskandarovBakshi/wezterm-quickterminal")
--   quickterminal.apply_to_config(config, { hotkey = "Win+`", height = 40 })
--
-- WezTerm's Lua can't register a global hotkey or hide windows, so this plugin starts a small
-- hidden helper (quickterminal.ps1 + QuickTerminalHelper.cs, run by the Windows PowerShell that ships with
-- Windows). Nothing else needs installing. On every config load the plugin exports the options
-- to %APPDATA%\wezterm-quickterminal\settings.json, which the helper watches. Inside the drop-down
-- window (WEZTERM_QUICKTERMINAL=1) it also applies the quick-terminal look.

local wezterm = require("wezterm")

local M = {}

M.VERSION = "0.4.0"

--- True inside the window started by the helper. Use it for quick-terminal-only tweaks:
---   if quickterminal.is_quickterminal then config.window_background_opacity = 0.95 end
M.is_quickterminal = os.getenv("WEZTERM_QUICKTERMINAL") ~= nil

local defaults = {
  hotkey = "Win+`", -- Ctrl / Alt / Shift / Win + key, e.g. "Ctrl+Alt+T", "F12"
  position = "bottom", -- "bottom" or "top" edge of the monitor
  height = 40, -- % of the monitor's work area
  hide_on_focus_loss = true, -- hide when another app takes focus
  autostart = false, -- also start the helper at Windows login, before any WezTerm window
}

local function is_windows()
  return wezterm.target_triple:find("windows") ~= nil
end

local function read_file(path)
  local f = io.open(path, "rb")
  if not f then
    return nil
  end
  local content = f:read("*a")
  f:close()
  return content
end

local function file_size(path)
  local f = io.open(path, "rb")
  if not f then
    return 0
  end
  local size = f:seek("end")
  f:close()
  return size or 0
end

--- Folder containing quickterminal.ps1. Found automatically for wezterm.plugin.require. When loading a
--- local copy with dofile, set it yourself: quickterminal.plugin_dir = "C:/path/to/wezterm-quickterminal/plugin"
M.plugin_dir = nil

local function plugin_dir()
  if M.plugin_dir then
    return (M.plugin_dir:gsub("/", "\\"))
  end
  -- WezTerm's Lua has no `debug` library, so look ourselves up in the installed plugins.
  for _, p in ipairs(wezterm.plugin.list()) do
    if p.url:find("wezterm%-quickterminal") then
      return p.plugin_dir .. "\\plugin"
    end
  end
  return nil
end

local function helper_paths()
  local dir = plugin_dir()
  if not dir then
    return nil
  end
  local ps1 = dir .. "\\quickterminal.ps1"
  -- The helper DLL is cached per version; include the source sizes so local edits recompile.
  local version = M.VERSION .. "+" .. file_size(dir .. "\\QuickTerminalHelper.cs") .. "." .. file_size(ps1)
  return ps1, version
end

local function powershell(ps1, version, extra)
  local args = {
    "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-WindowStyle", "Hidden", "-File", ps1, "-Version", version,
  }
  if extra then
    table.insert(args, extra)
  end
  return args
end

local function export(opts, ps1)
  local appdata = os.getenv("APPDATA")
  if not appdata then
    return
  end

  local dir = appdata .. "\\wezterm-quickterminal"
  local path = dir .. "\\settings.json"
  local json = wezterm.json_encode({
    hotkey = opts.hotkey,
    position = opts.position,
    heightPercent = opts.height,
    hideOnFocusLoss = opts.hide_on_focus_loss,
    autostart = opts.autostart,
    weztermPath = wezterm.executable_dir .. "\\wezterm-gui.exe",
    helperScript = ps1,
  })

  -- WezTerm evaluates the config several times per process; only write real changes.
  if read_file(path) == json then
    return
  end

  local f = io.open(path, "wb")
  if not f then
    wezterm.run_child_process({ "cmd.exe", "/c", "mkdir", dir })
    f = io.open(path, "wb")
  end
  if f then
    f:write(json)
    f:close()
  end
end

--- Starts the helper once per WezTerm GUI process (again if the plugin was updated).
local function launch(ps1, version)
  if not wezterm.gui then
    return -- not from CLI runs like `wezterm show-keys`
  end
  -- The quick-terminal window may launch it too: if you only use the drop-down, it's the only WezTerm
  -- that ever reloads the config, so it must be able to start an updated helper.
  if wezterm.GLOBAL.quickterminal_helper_version == version then
    return
  end
  wezterm.GLOBAL.quickterminal_helper_version = version
  -- A helper that's already running with this version exits immediately; an older one hands over.
  wezterm.background_child_process(powershell(ps1, version))
end

--- Exports the settings, starts the helper and, inside the quick-terminal window, applies the drop-down look.
--- Call it last, just before `return config`.
function M.apply_to_config(config, opts)
  local o = {}
  for k, v in pairs(defaults) do
    o[k] = v
  end
  for k, v in pairs(opts or {}) do
    if defaults[k] == nil then
      wezterm.log_warn("wezterm-quickterminal: unknown option '" .. tostring(k) .. "'")
    end
    o[k] = v
  end
  if o.position ~= "top" and o.position ~= "bottom" then
    wezterm.log_warn("wezterm-quickterminal: position must be \"top\" or \"bottom\", using \"bottom\"")
    o.position = "bottom"
  end

  if is_windows() then
    local ok, err = pcall(function()
      local ps1, version = helper_paths()
      if not ps1 then
        wezterm.log_warn("wezterm-quickterminal: can't find the plugin folder")
        return
      end
      export(o, ps1)
      launch(ps1, version)
    end)
    if not ok then
      wezterm.log_warn("wezterm-quickterminal: " .. tostring(err))
    end
  end

  if M.is_quickterminal then
    config.window_decorations = "RESIZE" -- no title bar; drag the bottom edge to resize
    config.use_resize_increments = false -- let the helper set the exact full width
    config.window_close_confirmation = "NeverPrompt"
  end

  return config
end

--- Stops the helper. The drop-down window stays open as a normal WezTerm window.
--- Use in a key binding: wezterm.action_callback(function() quickterminal.stop_helper() end)
function M.stop_helper()
  local ps1, version = helper_paths()
  if ps1 then
    wezterm.run_child_process(powershell(ps1, version, "-Quit"))
    wezterm.GLOBAL.quickterminal_helper_version = nil
  end
end

--- Restarts the helper (for example after it was stopped).
function M.restart_helper()
  local ps1, version = helper_paths()
  if ps1 then
    wezterm.run_child_process(powershell(ps1, version, "-Quit"))
    wezterm.GLOBAL.quickterminal_helper_version = version
    wezterm.background_child_process(powershell(ps1, version))
  end
end

return M
