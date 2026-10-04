-- Extractor runtime config. Copied next to client_extractor.dll at build time; edit the copy per machine.
-- Relative paths resolve against this file's directory.
return {
  profile = "profiles/synthetic.lua", -- the game profile to load
  out_dir = "C:/odx_capture",         -- one sub-directory per recording
  vendor_name = "odx",
  fps = 60,                           -- nominal capture rate written to session.json (cap the game to this)
  toggle_key = "F9",                  -- start/stop recording
}
