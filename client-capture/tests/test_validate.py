import shutil

import pytest

from client_capture.validate import main, validate_session

from sessions import MUTATIONS, copy_session, make_valid_session

needs_ffmpeg = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")


@pytest.fixture(scope="module")
def valid_session(tmp_path_factory):
    return make_valid_session(tmp_path_factory.mktemp("valid") / "session")


@needs_ffmpeg
def test_valid_session_has_no_issues(valid_session):
    assert validate_session(valid_session) == []


@needs_ffmpeg
@pytest.mark.parametrize("rule", sorted(MUTATIONS))
def test_each_violation_is_caught(rule, valid_session, tmp_path):
    session = copy_session(valid_session, tmp_path / "s")
    MUTATIONS[rule](session)
    fired = {i.rule for i in validate_session(session) if i.severity == "error"}
    assert rule in fired, f"expected {rule}, got {sorted(fired)}"


@needs_ffmpeg
def test_cli_exit_codes(valid_session, tmp_path):
    assert main([str(valid_session)]) == 0
    broken = copy_session(valid_session, tmp_path / "broken")
    MUTATIONS["c2w.det"](broken)
    assert main([str(broken), "--json"]) == 1


def test_session_without_camera_data_passes_camera_checks(tmp_path):
    from client_capture.frames import C2W_COLUMNS, INTRINSIC_COLUMNS
    from sessions import _edit_rows

    session = make_valid_session(tmp_path / "s", video=False)

    def strip_camera(rows):
        for r in rows:
            for c in [*C2W_COLUMNS, "camera_model", *INTRINSIC_COLUMNS]:
                r[c] = ""

    _edit_rows(session, strip_camera)
    assert validate_session(session, video=False) == []
