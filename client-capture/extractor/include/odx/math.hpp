// Camera geometry for the canonical format (Capture Spec v2 §1.1.2.3–1.1.2.5).
// Mirrors client_capture/geometry.py.
//
// Canonical world: RFU — X right/east, Y forward/north, Z up, right-handed.
// Canonical camera: C2W 4x4, row-major, columns = Right | Up | Backward | Translation (RUB).
#pragma once

#include <array>
#include <optional>
#include <string_view>

namespace odx {

struct Vec3 {
    double x = 0, y = 0, z = 0;
};

using Mat4 = std::array<std::array<double, 4>, 4>;

// Engine world frames that can be mapped to RFU (vectors are remapped individually, spec v2 §2.2).
enum class Convention {
    RFU,     // GTA V / RAGE
    Unity,   // left-handed, X right, Y up, Z forward
    Unreal,  // left-handed, X forward, Y right, Z up
    Quake,   // right-handed, X forward, Y left, Z up (Quake / Source)
};

std::optional<Convention> parse_convention(std::string_view name);

Vec3 to_rfu(Vec3 v, Convention c);
Vec3 cross(Vec3 a, Vec3 b);
Vec3 normalize(Vec3 v);

Mat4 c2w_from_axes(Vec3 right, Vec3 up, Vec3 backward, Vec3 position);
Mat4 c2w_from_engine(Vec3 right, Vec3 up, Vec3 forward, Vec3 position, Convention c, double units_to_world = 1.0);
// Roll-free reconstruction from a forward vector already in RFU.
Mat4 c2w_from_forward(Vec3 forward, Vec3 position);
// Heading is a compass bearing: 0 = north (+Y), 90 = east (+X).
Vec3 forward_from_heading_pitch(double heading_deg, double pitch_deg);

struct RotationErrors {
    double det;    // |det(R) - 1|
    double ortho;  // max |R·Rᵀ - I|
};
RotationErrors rotation_errors(const Mat4& c2w);

struct Intrinsics {
    double fx = 0, fy = 0, cx = 0, cy = 0;
};
Intrinsics intrinsics_from_vfov(double vfov_deg, int width, int height);
Intrinsics intrinsics_from_hfov(double hfov_deg, int width, int height);

}  // namespace odx
