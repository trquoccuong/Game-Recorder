# analyze-pose

Reference audit for captured sessions. Standalone research tool in Python; not part of the capture product (`client-capture/`, C#/C++) and not used by `odx-validate`.

Checks: matrix basis and sanity, forward vs motion, units, inputs (proxy-mouse detection), pose ↔ video lag and fx estimated from image motion. Reads spec v2 sessions and legacy layouts (UUID-prefixed files, any header case). Findings: `document/reviews/sample-camera-pose-action-labeled.md`.

```bash
python -m venv .venv
.venv/Scripts/pip install -r requirements.txt   # also needs ffmpeg + ffprobe on PATH
.venv/Scripts/python analyze_pose.py <root>     # root contains <game>/<session>/ folders; one JSON line per session
```
