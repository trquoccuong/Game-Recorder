-- Extractor runtime config. Copied next to odx_extractor.dll at build time; edit the copy per machine.
-- Relative paths resolve against this file's directory.
return {
  profile = "profiles/synthetic.lua", -- the game profile to load
  out_dir = "C:/odx_capture",         -- one sub-directory per recording (camera_samples.csv + extractor.json)
  toggle_key = "F9",                  -- start/stop recording: F1..F24, A..Z or 0..9
}
