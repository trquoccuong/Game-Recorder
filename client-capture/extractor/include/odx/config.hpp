// Extractor runtime config, read from a Lua file (odx_config.lua next to the DLL):
//   return { profile = "profiles/game.lua", out_dir = "C:/odx_capture", vendor_name = "odx",
//            fps = 60, toggle_key = "F9" }
// Relative paths are resolved against the config file's directory.
#pragma once

#include <optional>
#include <string>

namespace odx {

struct Config {
    std::string profile;
    std::string out_dir;
    std::string vendor_name = "odx";
    double fps = 60;
    std::string toggle_key = "F9";
};

std::optional<Config> load_config(const std::string& path, std::string& error);

}  // namespace odx
