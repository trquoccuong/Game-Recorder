import csv
from pathlib import Path

from client_capture.frames import COLUMNS, InputEvent, aggregate_inputs, read_input_events, read_video_frames, write_frames_csv

TIMES = [0, 17, 33, 50, 67]


def test_held_key_spans_frames():
    rows = aggregate_inputs(TIMES, [InputEvent(10, "key_down", "W"), InputEvent(45, "key_up", "W")])
    assert [r["input_keys"] for r in rows] == [None, "W", "W", "W", None]


def test_tap_shorter_than_a_frame_is_kept():
    rows = aggregate_inputs(TIMES, [InputEvent(20, "key_down", "Space"), InputEvent(25, "key_up", "Space")])
    assert [r["input_keys"] for r in rows] == [None, None, "Space", None, None]


def test_mouse_deltas_are_summed_per_frame():
    events = [InputEvent(t, "mouse_move", dx=1.5, dy=-0.5) for t in (18, 20, 30, 33)]
    rows = aggregate_inputs(TIMES, events)
    assert [r["input_mouse_dx"] for r in rows] == [0.0, 0.0, 6.0, 0.0, 0.0]
    assert [r["input_mouse_dy"] for r in rows] == [0.0, 0.0, -2.0, 0.0, 0.0]


def test_multiple_values_are_pipe_delimited_in_press_order():
    events = [
        InputEvent(1, "key_down", "W"),
        InputEvent(2, "key_down", "Shift"),
        InputEvent(1, "action_start", "Forward"),
        InputEvent(2, "action_start", "Sprint"),
        InputEvent(3, "button_down", "Left"),
    ]
    row = aggregate_inputs([17], events)[0]
    assert (row["input_keys"], row["input_actions"], row["input_mouse_buttons"]) == ("W|Shift", "Forward|Sprint", "Left")


def test_reads_odx_input_log_fixture():
    """tests/fixtures/input_events.csv is the contract with capture-cs (InputLogWriter must produce it byte-for-byte)."""
    events = read_input_events(Path(__file__).parent / "fixtures" / "input_events.csv")
    assert events[0] == InputEvent(1000.5, "key_down", "W")
    assert events[1] == InputEvent(1001.25, "mouse_move", None, 3.0, -1.0)
    rows = aggregate_inputs([1005.0, 1015.0, 1025.0, 1035.0], events)
    assert [r["input_keys"] for r in rows] == ["W", "W", "W", None]
    assert [r["input_mouse_buttons"] for r in rows] == [None, "Left", "Left", "Left"]
    assert rows[0]["input_mouse_dx"] == 3.0


def test_reads_odx_record_frame_log_fixture():
    """tests/fixtures/video_frames.csv is the contract with capture-cs (VideoFrameLogWriter)."""
    frames = read_video_frames(Path(__file__).parent / "fixtures" / "video_frames.csv")
    assert [f.index for f in frames] == [0, 1, 2]
    assert frames[1].t_grid == 1000 + 1000 / 60
    assert frames[1].t_source == 1000.0 and frames[1].duplicate
    # Grid times are the per-frame clock for input aggregation.
    rows = aggregate_inputs([f.t_grid for f in frames], [InputEvent(1010.0, "key_down", "W")])
    assert [r["input_keys"] for r in rows] == [None, "W", "W"]


def test_writer_emits_every_column_and_null_convention(tmp_path):
    path = tmp_path / "frames.csv"
    write_frames_csv(path, [{"frame_id": 0, "timestamp_ms": 0, "input_mouse_dx": 0.0, "input_mouse_dy": 0.1 + 0.2}])
    with path.open(newline="") as f:
        reader = csv.DictReader(f)
        row = next(reader)
    assert reader.fieldnames == COLUMNS
    assert row["input_mouse_dx"] == "0.0"
    assert float(row["input_mouse_dy"]) == 0.1 + 0.2  # full float precision preserved
    assert row["c2w_m00"] == "" and row["input_keys"] == ""
