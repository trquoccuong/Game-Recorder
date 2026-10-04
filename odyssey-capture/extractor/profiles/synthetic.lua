-- Profile for the fake game used by tests and odx_synth (see include/odx/fake_game.hpp).
-- Doubles as a template for real profiles: resolve addresses once in init(), read them in sample().

local camera_global -- address of the global pointer to the camera struct

return {
  game = "Synthetic",
  engine = "fake-unreal",
  convention = "unreal",   -- X forward, Y right, Z up, left-handed
  units_to_world = 0.01,   -- centimetres -> metres
  fov_axis = "horizontal",
  camera_model = "PINHOLE",
  mouse_convention = {
    maps_to = "camera_look_velocity",
    dx_positive = "right", dx_negative = "left",
    dy_positive = "down", dy_negative = "up",
  },
  bindings = { W = "Forward", A = "Left", S = "Backward", D = "Right", Space = "Jump", Left = "Fire" },

  init = function()
    -- mov rax,[rip+disp32]; test rax,rax; jz short
    local hit = mem.scan("game.exe", "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ??")
    assert(hit, "camera signature not found")
    camera_global = mem.rip(hit, 3, 7)
  end,

  sample = function()
    local cam = mem.read_ptr(camera_global)
    return {
      position = mem.read_vec3(cam),
      forward = mem.read_vec3(cam + 12),
      right = mem.read_vec3(cam + 24),
      up = mem.read_vec3(cam + 36),
      fov = mem.read_f32(cam + 48),
    }
  end,
}
