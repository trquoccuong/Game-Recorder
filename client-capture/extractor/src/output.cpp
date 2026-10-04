#include "odx/output.hpp"

#include <charconv>
#include <cstdio>
#include <sstream>

namespace odx {

const std::vector<std::string>& frame_columns() {
    static const std::vector<std::string> cols = [] {
        std::vector<std::string> c = {"frame_id", "timestamp_ms"};
        for (int r = 0; r < 4; ++r)
            for (int k = 0; k < 4; ++k) c.push_back("c2w_m" + std::to_string(r) + std::to_string(k));
        c.insert(c.end(), {"camera_model", "camera_fx", "camera_fy", "camera_cx", "camera_cy"});
        for (int i = 1; i <= 6; ++i) c.push_back("camera_radial_k" + std::to_string(i));
        c.insert(c.end(), {"camera_tangential_p1", "camera_tangential_p2", "input_keys", "input_actions",
                           "input_mouse_buttons", "input_mouse_dx", "input_mouse_dy"});
        return c;
    }();
    return cols;
}

std::string format_double(double v) {
    char buf[64];
    auto [end, ec] = std::to_chars(buf, buf + sizeof(buf), v);
    std::string s(buf, end);
    if (s.find_first_of(".eEn") == std::string::npos) s += ".0";  // "n" covers nan/inf
    return s;
}

namespace {
// Values never contain commas or quotes except free-form input names; quote defensively.
std::string csv_field(const std::string& v) {
    if (v.find_first_of(",\"\n") == std::string::npos) return v;
    std::string q = "\"";
    for (char c : v) q += (c == '"') ? std::string("\"\"") : std::string(1, c);
    return q + "\"";
}

std::string json_string(const std::string& v) {
    std::string out = "\"";
    for (char c : v) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                } else {
                    out += c;
                }
        }
    }
    return out + "\"";
}
}  // namespace

bool FramesCsvWriter::open(const std::string& path) {
    out_.open(path, std::ios::out | std::ios::trunc);
    if (!out_) return false;
    const auto& cols = frame_columns();
    for (size_t i = 0; i < cols.size(); ++i) out_ << (i ? "," : "") << cols[i];
    out_ << "\n";
    return true;
}

void FramesCsvWriter::write(const FrameRow& r) {
    std::ostringstream line;
    line << r.frame_id << ',' << r.timestamp_ms;
    for (int i = 0; i < 16; ++i) line << ',' << (r.c2w ? format_double((*r.c2w)[i / 4][i % 4]) : "");
    line << ',' << r.camera_model;
    const auto& k = r.intrinsics;
    for (double v : {k ? k->fx : 0.0, k ? k->fy : 0.0, k ? k->cx : 0.0, k ? k->cy : 0.0})
        line << ',' << (k ? format_double(v) : "");
    line << ",,,,,,,,";  // radial k1..k6 + tangential p1..p2: not captured -> null
    line << ',' << csv_field(r.input.keys) << ',' << csv_field(r.input.actions) << ','
         << csv_field(r.input.buttons) << ',' << format_double(r.input.dx) << ',' << format_double(r.input.dy);
    out_ << line.str() << '\n';
}

void FramesCsvWriter::close() { out_.close(); }

std::string session_json(const SessionInfo& s) {
    std::ostringstream o;
    o << "{\n"
      << "  \"vendor_name\": " << json_string(s.vendor_name) << ",\n"
      << "  \"game_title\": " << json_string(s.game_title) << ",\n"
      << "  \"session_id\": " << json_string(s.session_id) << ",\n"
      << "  \"created_at_utc\": " << json_string(s.created_at_utc) << ",\n"
      << "  \"ended_at_utc\": " << json_string(s.ended_at_utc) << ",\n"
      << "  \"duration_ms\": " << format_double(s.duration_ms) << ",\n"
      << "  \"duration_seconds\": " << format_double(s.duration_ms / 1000.0) << ",\n"
      << "  \"fps\": " << format_double(s.fps) << ",\n"
      << "  \"frame_count\": " << s.frame_count << ",\n"
      << "  \"record_width_px\": " << s.record_width_px << ",\n"
      << "  \"record_height_px\": " << s.record_height_px << ",\n"
      << "  \"screen_width_px\": " << s.screen_width_px << ",\n"
      << "  \"screen_height_px\": " << s.screen_height_px << ",\n"
      << "  \"localization\": " << json_string(s.localization) << ",\n"
      << "  \"platform\": " << json_string(s.platform) << ",\n"
      << "  \"input_mouse_convention\": {\n"
      << "    \"maps_to\": " << json_string(s.mouse.maps_to) << ",\n"
      << "    \"dx_positive\": " << json_string(s.mouse.dx_positive) << ",\n"
      << "    \"dx_negative\": " << json_string(s.mouse.dx_negative) << ",\n"
      << "    \"dy_positive\": " << json_string(s.mouse.dy_positive) << ",\n"
      << "    \"dy_negative\": " << json_string(s.mouse.dy_negative) << "\n"
      << "  }\n"
      << "}\n";
    return o.str();
}

}  // namespace odx
