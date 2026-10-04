#include "odx/math.hpp"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace odx {

namespace {
double rad(double deg) { return deg * std::numbers::pi / 180.0; }
Vec3 scale(Vec3 v, double s) { return {v.x * s, v.y * s, v.z * s}; }
Vec3 neg(Vec3 v) { return {-v.x, -v.y, -v.z}; }
}  // namespace

std::optional<Convention> parse_convention(std::string_view name) {
    if (name == "rfu") return Convention::RFU;
    if (name == "unity") return Convention::Unity;
    if (name == "unreal") return Convention::Unreal;
    if (name == "quake") return Convention::Quake;
    return std::nullopt;
}

Vec3 to_rfu(Vec3 v, Convention c) {
    switch (c) {
        case Convention::RFU: return v;
        case Convention::Unity: return {v.x, v.z, v.y};
        case Convention::Unreal: return {v.y, v.x, v.z};
        case Convention::Quake: return {-v.y, v.x, v.z};
    }
    return v;
}

Vec3 cross(Vec3 a, Vec3 b) {
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}

Vec3 normalize(Vec3 v) {
    double n = std::sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
    return scale(v, 1.0 / n);
}

Mat4 c2w_from_axes(Vec3 r, Vec3 u, Vec3 b, Vec3 p) {
    return {{{r.x, u.x, b.x, p.x}, {r.y, u.y, b.y, p.y}, {r.z, u.z, b.z, p.z}, {0, 0, 0, 1}}};
}

Mat4 c2w_from_engine(Vec3 right, Vec3 up, Vec3 forward, Vec3 position, Convention c, double units_to_world) {
    return c2w_from_axes(to_rfu(right, c), to_rfu(up, c), neg(to_rfu(forward, c)), scale(to_rfu(position, c), units_to_world));
}

Mat4 c2w_from_forward(Vec3 forward, Vec3 position) {
    Vec3 f = normalize(forward);
    Vec3 right = normalize(cross(f, {0, 0, 1}));
    Vec3 up = normalize(cross(right, f));
    return c2w_from_axes(right, up, neg(f), position);
}

Vec3 forward_from_heading_pitch(double heading_deg, double pitch_deg) {
    double h = rad(heading_deg), p = rad(pitch_deg);
    return {std::sin(h) * std::cos(p), std::cos(h) * std::cos(p), std::sin(p)};
}

RotationErrors rotation_errors(const Mat4& m) {
    double det = m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) -
                 m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0]) +
                 m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]);
    double ortho = 0;
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) {
            double dot = m[i][0] * m[j][0] + m[i][1] * m[j][1] + m[i][2] * m[j][2];
            ortho = std::max(ortho, std::abs(dot - (i == j ? 1.0 : 0.0)));
        }
    }
    return {std::abs(det - 1.0), ortho};
}

Intrinsics intrinsics_from_vfov(double vfov_deg, int width, int height) {
    double fy = height / (2.0 * std::tan(rad(vfov_deg) / 2.0));
    return {fy, fy, width / 2.0, height / 2.0};
}

Intrinsics intrinsics_from_hfov(double hfov_deg, int width, int height) {
    double fx = width / (2.0 * std::tan(rad(hfov_deg) / 2.0));
    return {fx, fx, width / 2.0, height / 2.0};
}

}  // namespace odx
