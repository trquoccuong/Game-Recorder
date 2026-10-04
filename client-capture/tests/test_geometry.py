import math

import numpy as np
import pytest
from hypothesis import given
from hypothesis import strategies as st

from client_capture.geometry import (
    CONVENTIONS,
    c2w_from_engine,
    c2w_from_forward,
    c2w_from_unity_camera_to_world,
    forward_from_heading_pitch,
    intrinsics_from_hfov,
    intrinsics_from_projection,
    intrinsics_from_vfov,
    rotation_errors,
)

EAST, NORTH, UP = np.array([1.0, 0, 0]), np.array([0, 1.0, 0]), np.array([0, 0, 1.0])


def quaternion_rotation(q):
    w, x, y, z = q / np.linalg.norm(q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
        [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
        [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)],
    ])


quaternions = st.lists(st.floats(-1, 1), min_size=4, max_size=4).map(np.array).filter(lambda q: np.linalg.norm(q) > 0.1)


@pytest.mark.parametrize(
    "convention, right, up, forward",
    [
        ("rfu", EAST, UP, NORTH),
        ("unity", [1, 0, 0], [0, 1, 0], [0, 0, 1]),  # Unity +Z forward
        ("unreal", [0, 1, 0], [0, 0, 1], [1, 0, 0]),  # Unreal +X forward, +Y right
        ("quake", [0, -1, 0], [0, 0, 1], [1, 0, 0]),  # Quake +X forward, +Y left
    ],
)
def test_engine_camera_facing_forward_maps_to_north(convention, right, up, forward):
    c2w = c2w_from_engine(right, up, forward, [0, 0, 0], convention)
    np.testing.assert_allclose(c2w[:3, 0], EAST, atol=1e-12)
    np.testing.assert_allclose(c2w[:3, 1], UP, atol=1e-12)
    np.testing.assert_allclose(c2w[:3, 2], -NORTH, atol=1e-12)  # camera looks along −column 2
    assert rotation_errors(c2w)[0] < 1e-12


@given(quaternions, st.sampled_from(sorted(CONVENTIONS)))
def test_engine_round_trip_is_proper_rotation(q, convention):
    """A canonical pose expressed in any engine frame converts back to the same canonical C2W."""
    rot = quaternion_rotation(q)
    right, up, forward = rot @ EAST, rot @ UP, rot @ NORTH
    m_inv = CONVENTIONS[convention].T
    c2w = c2w_from_engine(m_inv @ right, m_inv @ up, m_inv @ forward, m_inv @ np.array([1.0, 2.0, 3.0]), convention)
    np.testing.assert_allclose(c2w[:3, :3], np.column_stack([right, up, -forward]), atol=1e-9)
    np.testing.assert_allclose(c2w[:3, 3], [1, 2, 3], atol=1e-9)
    det_err, ortho_err = rotation_errors(c2w)
    assert det_err < 1e-9 and ortho_err < 1e-9


@given(st.floats(-180, 180), st.floats(-89, 89))
def test_roll_free_camera_passes_level_checks(heading, pitch):
    c2w = c2w_from_forward(forward_from_heading_pitch(heading, pitch), [0, 0, 0])
    assert abs(c2w[2, 0]) < 1e-9  # right axis horizontal
    assert c2w[2, 1] > 0  # up axis points up
    assert max(rotation_errors(c2w)) < 1e-9
    np.testing.assert_array_equal(c2w[3], [0, 0, 0, 1])


def test_heading_is_compass_bearing():
    np.testing.assert_allclose(forward_from_heading_pitch(0, 0), NORTH, atol=1e-12)
    np.testing.assert_allclose(forward_from_heading_pitch(90, 0), EAST, atol=1e-12)
    np.testing.assert_allclose(forward_from_heading_pitch(0, 90), UP, atol=1e-12)


def test_unity_camera_to_world_identity_camera():
    # Unity camera at origin looking +Z: cameraToWorldMatrix = diag(1, 1, -1, 1) (OpenGL camera space).
    c2w = c2w_from_unity_camera_to_world(np.diag([1.0, 1.0, -1.0, 1.0]))
    np.testing.assert_allclose(c2w[:3, :3], np.column_stack([EAST, UP, -NORTH]), atol=1e-12)


def test_unity_position_maps_y_up_to_z_up():
    m = np.diag([1.0, 1.0, -1.0, 1.0])
    m[:3, 3] = [2.0, 5.0, 7.0]  # x right, y up, z forward
    np.testing.assert_allclose(c2w_from_unity_camera_to_world(m)[:3, 3], [2.0, 7.0, 5.0])


def test_intrinsics():
    assert intrinsics_from_vfov(90.0, 1920, 1080) == pytest.approx((540.0, 540.0, 960.0, 540.0))
    vfov = 60.0
    hfov = math.degrees(2 * math.atan(math.tan(math.radians(vfov) / 2) * 1920 / 1080))
    assert intrinsics_from_hfov(hfov, 1920, 1080) == pytest.approx(intrinsics_from_vfov(vfov, 1920, 1080))
    proj = np.zeros((4, 4))
    proj[0, 0], proj[1, 1] = 0.5625, 1.0  # P00 encodes aspect; fx must not come from it
    assert intrinsics_from_projection(proj, 1920, 1080) == pytest.approx((540.0, 540.0, 960.0, 540.0))
