"""Audit captured sessions: matrix sanity, units, forward-vs-motion, intrinsics, inputs, and video-based checks
(pose<->video lag, fx estimated from image motion).

Usage: python tools/analyze_pose.py <root>   # root contains <game>/<session>/ folders; prints one JSON line per session

Reference tool used for document/reviews/sample-camera-pose-action-labeled.md. Reads legacy CSV and spec v1 layouts
(UUID-prefixed files, any header case). Needs numpy, ffmpeg and ffprobe.
"""
import csv, glob, json, math, os, subprocess, sys
from collections import Counter
import numpy as np

ROOT = ""


def load_csv(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        r = csv.DictReader(f)
        rows = [{k.lower(): v for k, v in row.items()} for row in r]
    return rows


def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return math.nan


def probe(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries",
                          "stream=codec_name,width,height,r_frame_rate,avg_frame_rate,nb_read_packets",
                          "-of", "json", path], capture_output=True, text=True).stdout
    return json.loads(out)["streams"][0]


def image_shift(path, w=320, h=180, limit=None):
    cmd = ["ffmpeg", "-v", "error", "-i", path, "-vf", f"scale={w}:{h},format=gray", "-f", "rawvideo", "-"]
    raw = subprocess.run(cmd, capture_output=True).stdout
    v = np.frombuffer(raw, np.uint8).reshape(-1, h, w).astype(np.float32)
    if limit:
        v = v[:limit]
    win = np.hanning(w)[None, :] * np.hanning(h)[:, None]
    F = np.fft.fft2((v - v.mean(axis=(1, 2), keepdims=True)) * win)
    sx = np.zeros(len(v))
    for i in range(1, len(v)):
        R = F[i] * np.conj(F[i - 1])
        R /= np.abs(R) + 1e-9
        c = np.fft.ifft2(R).real
        y, x = np.unravel_index(np.argmax(c), c.shape)
        # sub-pixel parabolic refinement along x
        l, m, r = c[y, (x - 1) % w], c[y, x], c[y, (x + 1) % w]
        d = 0.5 * (l - r) / (l - 2 * m + r) if (l - 2 * m + r) != 0 else 0.0
        xs = x + d
        sx[i] = xs if xs < w / 2 else xs - w
    return sx, w


def rot_rate(Rs):
    """Per-frame rotation vector (camera frame) between consecutive rotations."""
    out = np.zeros((len(Rs), 3))
    for i in range(1, len(Rs)):
        Rr = Rs[i - 1].T @ Rs[i]
        ang = math.acos(max(-1.0, min(1.0, (np.trace(Rr) - 1) / 2)))
        if ang < 1e-9:
            continue
        axis = np.array([Rr[2, 1] - Rr[1, 2], Rr[0, 2] - Rr[2, 0], Rr[1, 0] - Rr[0, 1]]) / (2 * math.sin(ang))
        out[i] = axis * ang
    return out


def xcorr(a, b, maxlag=30):
    a = (a - a.mean()) / (a.std() + 1e-12)
    b = (b - b.mean()) / (b.std() + 1e-12)
    n = min(len(a), len(b))
    best = (0, 0.0)
    for L in range(-maxlag, maxlag + 1):
        x = a[max(0, -L):n - max(0, L)]
        y = b[max(0, L):n - max(0, -L)]
        c = float(np.mean(x * y))
        if abs(c) > abs(best[1]):
            best = (L, c)
    return best  # + lag: b happens after a


def audit(sdir):
    files = os.listdir(sdir)
    csvf = [f for f in files if f.endswith(".csv")][0]
    mp4 = [f for f in files if f.endswith(".mp4")]
    jsons = [f for f in files if f.endswith(".json")]
    rows = load_csv(os.path.join(sdir, csvf))
    n = len(rows)
    res = {"session": os.path.relpath(sdir, ROOT), "rows": n, "jsons": len(jsons), "video": bool(mp4)}
    session = None
    for j in jsons:
        d = json.load(open(os.path.join(sdir, j), encoding="utf-8-sig"))
        if "fps" in d:
            session = d
    res["session_json"] = session is not None
    if session:
        res["sess_keys_missing"] = [k for k in ("localization", "platform", "input_mouse_convention", "duration_seconds",
                                                  "screen_width_px", "record_width_px") if k not in session]
        res["fps"] = session.get("fps")

    M = np.array([[num(r[f"c2w_m{i // 4}{i % 4}"]) for i in range(16)] for r in rows]).reshape(-1, 4, 4)
    R = M[:, :3, :3]
    T = M[:, :3, 3]
    dets = np.linalg.det(R)
    res["det"] = (round(float(np.nanmin(dets)), 3), round(float(np.nanmax(dets)), 3))
    ortho = np.max(np.abs(np.einsum("nij,nkj->nik", R, R) - np.eye(3)), axis=(1, 2))
    res["ortho_max"] = round(float(np.nanmax(ortho)), 4)
    res["bottom_ok"] = bool(np.allclose(M[:, 3], [0, 0, 0, 1]))
    # which column is most aligned with world +Z (should be column 1 = Up)
    zc = np.nanmean(np.abs(R[:, 2, :]), axis=0)
    res["col_abs_z"] = [round(float(x), 2) for x in zc]
    res["roll_m20_p95"] = round(float(np.nanpercentile(np.abs(R[:, 2, 0]), 95)), 3)
    # units: median speed when moving
    ts = np.array([num(r["timestamp_ms"]) for r in rows])
    dt = np.diff(ts) / 1000.0
    dts = Counter(np.round(np.diff(ts), 1))
    res["dt_ms"] = dict(dts.most_common(3))
    res["t0"] = ts[0]
    vel = np.diff(T, axis=0) / np.where(dt > 0, dt, np.nan)[:, None]
    speed = np.linalg.norm(vel[:, :2], axis=1)
    mov = speed > np.nanpercentile(speed, 60)
    res["speed_p50_moving"] = round(float(np.nanmedian(speed[mov])), 2)
    # forward vs horizontal motion for each candidate forward axis
    cand = {"-col2": -R[1:, :, 2], "+col2": R[1:, :, 2], "+col1": R[1:, :, 1], "+col0": R[1:, :, 0]}
    vh = vel.copy()
    vh[:, 2] = 0
    vn = vh / (np.linalg.norm(vh, axis=1, keepdims=True) + 1e-12)
    fwd = {}
    for name, f in cand.items():
        fh = f.copy()
        fh[:, 2] = 0
        fh /= np.linalg.norm(fh, axis=1, keepdims=True) + 1e-12
        cos = np.sum(fh * vn, axis=1)[mov]
        fwd[name] = round(float(np.nanmean(cos > 0.7)), 2)
    res["fwd_agree"] = fwd  # fraction of moving frames where axis points along motion
    if "camera_fy" in rows[0]:
        fy = np.array([num(r["camera_fy"]) for r in rows])
        fx = np.array([num(r["camera_fx"]) for r in rows])
        res["fx_fy"] = (round(float(np.nanmedian(fx)), 1), round(float(np.nanmedian(fy)), 1))
        res["fx_rec"] = float(np.nanmedian(fx))
    elif "fov_deg" in rows[0]:
        fov = np.array([num(r["fov_deg"]) for r in rows])
        res["fov"] = (float(np.nanmin(fov)), float(np.nanmax(fov)), rows[0].get("fov_axis"))
    kcol = "input_keys" if "input_keys" in rows[0] else "keyboard_input"
    keys = Counter(t for r in rows for t in r[kcol].split("|"))
    res["key_tokens"] = sorted(keys)[:14]
    res["has_actions"] = "input_actions" in rows[0]
    if res["has_actions"]:
        res["action_tokens"] = sorted(Counter(t for r in rows for t in r["input_actions"].split("|")))[:12]
    dxc = "input_mouse_dx" if "input_mouse_dx" in rows[0] else "mouse_delta_x"
    dx = np.array([num(r[dxc]) for r in rows])
    nz = dx[dx != 0]
    res["mouse_nonzero_pct"] = round(100 * len(nz) / n, 1)
    res["mouse_abs_p95"] = round(float(np.percentile(np.abs(nz), 95)), 3) if len(nz) else 0
    res["mouse_integer_pct"] = round(100 * float(np.mean(np.abs(nz - np.round(nz)) < 1e-6)), 1) if len(nz) else None

    w_rate = rot_rate(R)
    yaw_axis = int(np.argmax(np.nanvar(w_rate, axis=0)))
    yaw = w_rate[:, yaw_axis]
    res["yaw_axis_cam"] = yaw_axis
    lag_in, c_in = xcorr(dx, yaw)
    res["input_to_pose"] = f"{lag_in:+d}f r={c_in:.2f}"

    if mp4:
        p = probe(os.path.join(sdir, mp4[0]))
        res["vid"] = f"{p['codec_name']} {p['width']}x{p['height']} {p['avg_frame_rate']} frames={p['nb_read_packets']}"
        sx, w = image_shift(os.path.join(sdir, mp4[0]))
        scale = int(p["width"]) / w
        sfull = sx * scale
        m = min(len(sfull), len(yaw))
        lag, c = xcorr(yaw[:m], sfull[:m])
        res["pose_to_video"] = f"{lag:+d}f r={c:.2f}"
        # fx estimate: |shift| / |tan(yaw)| near best lag
        a = yaw[max(0, -lag):m - max(0, lag)]
        b = sfull[max(0, lag):m - max(0, -lag)]
        sel = (np.abs(a) > math.radians(0.3)) & (np.abs(a) < math.radians(4)) & (np.abs(b) > 2)
        if sel.sum() > 50:
            fx_est = float(np.median(np.abs(b[sel]) / np.abs(np.tan(a[sel]))))
            res["fx_est_video"] = round(fx_est, 0)
            if "fx_rec" in res:
                res["fx_ratio_est/rec"] = round(fx_est / res["fx_rec"], 2)
            if "fov" in res:
                H, W = int(p["height"]), int(p["width"])
                fov = res["fov"][0]
                fy_v = H / (2 * math.tan(math.radians(fov) / 2))
                fx_h = W / (2 * math.tan(math.radians(fov) / 2))
                res["fx_if_vertical"] = round(fy_v, 0)
                res["fx_if_horizontal"] = round(fx_h, 0)
        res["n_rot_frames"] = int(sel.sum())
    res.pop("fx_rec", None)
    return res


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: analyze_pose.py <root containing <game>/<session>/ folders>")
    ROOT = sys.argv[1].rstrip("/")
    for d in sorted(glob.glob(ROOT + "/*/*/")):
        try:
            print(json.dumps(audit(d), default=str))
        except Exception as e:
            print(json.dumps({"session": d, "error": repr(e)}))
        sys.stdout.flush()
