"""Camera geometry for the canonical format (Capture Spec v2 §1.1.2.3–1.1.2.5).

Canonical world: RFU — X right/east, Y forward/north, Z up, right-handed.
Canonical camera: C2W 4x4, row-major, columns = Right | Up | Backward | Translation (RUB).
"""

import math

import numpy as np

# Each matrix maps an engine world vector to the canonical RFU frame.
# Vectors are remapped individually (spec v2 §2.2 warns against similarity transforms).
CONVENTIONS: dict[str, np.ndarray] = {
    # GTA V / RAGE: already RFU.
    "rfu": np.eye(3),
    # Unity: left-handed, X right, Y up, Z forward.
    "unity": np.array([[1, 0, 0], [0, 0, 1], [0, 1, 0]], dtype=float),
    # Unreal: left-handed, X forward, Y right, Z up.
    "unreal": np.array([[0, 1, 0], [1, 0, 0], [0, 0, 1]], dtype=float),
    # Quake / Source: right-handed, X forward, Y left, Z up.
    "quake": np.array([[0, -1, 0], [1, 0, 0], [0, 0, 1]], dtype=float),
}


def to_rfu(v, convention: str) -> np.ndarray:
    return CONVENTIONS[convention] @ np.asarray(v, dtype=float)


def c2w_from_axes(right, up, backward, position) -> np.ndarray:
    m = np.eye(4)
    m[:3, 0] = right
    m[:3, 1] = up
    m[:3, 2] = backward
    m[:3, 3] = position
    return m


def c2w_from_engine(right, up, forward, position, convention: str, units_to_world: float = 1.0) -> np.ndarray:
    """Build a canonical C2W from an engine's camera basis vectors, expressed in that engine's world frame."""
    m = CONVENTIONS[convention]
    return c2w_from_axes(
        m @ np.asarray(right, dtype=float),
        m @ np.asarray(up, dtype=float),
        -(m @ np.asarray(forward, dtype=float)),
        (m @ np.asarray(position, dtype=float)) * units_to_world,
    )


def c2w_from_unity_camera_to_world(camera_to_world) -> np.ndarray:
    """Convert Unity's Camera.cameraToWorldMatrix (OpenGL camera: columns right, up, -forward)."""
    u = np.asarray(camera_to_world, dtype=float)
    return c2w_from_engine(u[:3, 0], u[:3, 1], -u[:3, 2], u[:3, 3], "unity")


def forward_from_heading_pitch(heading_deg: float, pitch_deg: float) -> np.ndarray:
    """Forward vector in RFU. Heading is a compass bearing: 0 = north (+Y), 90 = east (+X)."""
    h, p = math.radians(heading_deg), math.radians(pitch_deg)
    return np.array([math.sin(h) * math.cos(p), math.cos(h) * math.cos(p), math.sin(p)])


def c2w_from_forward(forward, position, world_up=(0.0, 0.0, 1.0)) -> np.ndarray:
    """Roll-free C2W from a forward vector (spec v2 §2.2 reconstruction)."""
    f = np.asarray(forward, dtype=float)
    f = f / np.linalg.norm(f)
    right = np.cross(f, world_up)
    right /= np.linalg.norm(right)
    up = np.cross(right, f)
    up /= np.linalg.norm(up)
    return c2w_from_axes(right, up, -f, position)


def rotation_errors(c2w) -> tuple[float, float]:
    """Return (|det(R) - 1|, max |R·Rᵀ - I|) for the rotation block of a C2W."""
    r = np.asarray(c2w, dtype=float)[:3, :3]
    return abs(np.linalg.det(r) - 1.0), float(np.max(np.abs(r @ r.T - np.eye(3))))


def intrinsics_from_vfov(vfov_deg: float, width: int, height: int) -> tuple[float, float, float, float]:
    """Pinhole (fx, fy, cx, cy) for square pixels from a vertical FOV."""
    fy = height / (2.0 * math.tan(math.radians(vfov_deg) / 2.0))
    return fy, fy, width / 2.0, height / 2.0


def intrinsics_from_hfov(hfov_deg: float, width: int, height: int) -> tuple[float, float, float, float]:
    """Pinhole (fx, fy, cx, cy) for square pixels from a horizontal FOV (e.g. Unreal FOVAngle)."""
    fx = width / (2.0 * math.tan(math.radians(hfov_deg) / 2.0))
    return fx, fx, width / 2.0, height / 2.0


def intrinsics_from_projection(proj, width: int, height: int) -> tuple[float, float, float, float]:
    """Pinhole intrinsics from an (unjittered) projection matrix.

    Uses fy = P[1][1]·H/2 and fx = fy; never derive fx from P[0][0] (spec v2 §1.1.3 note).
    """
    fy = float(np.asarray(proj, dtype=float)[1, 1]) * height / 2.0
    return fy, fy, width / 2.0, height / 2.0
