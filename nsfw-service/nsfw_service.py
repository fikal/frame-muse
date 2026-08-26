"""
Local NSFW image screen for Frame Muse. Loads NudeNet once and exposes a tiny HTTP endpoint the
worker calls before an image is allowed onto the frame. CPU-only, ~100-300ms per image.

  POST /check   body = raw image bytes (PNG/JPEG)   ->   {"nsfw": bool, "detections": [...]}
  GET  /health  ->   {"ok": true}

Run:  E:\AI\nsfw\venv\Scripts\python.exe nsfw_service.py  [port]   (default 8190)
"""
import io
import json
import sys
import tempfile
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from nudenet import NudeDetector

detector = NudeDetector()

# Exposed-anatomy classes that block an image. Deliberately excludes MALE_BREAST_EXPOSED and any
# "covered" classes so ordinary beach/shirtless/swimwear scenes are not false-flagged.
BLOCK_CLASSES = {
    "FEMALE_GENITALIA_EXPOSED",
    "MALE_GENITALIA_EXPOSED",
    "FEMALE_BREAST_EXPOSED",
    "BUTTOCKS_EXPOSED",
    "ANUS_EXPOSED",
}
SCORE_THRESHOLD = 0.45
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8190


def screen(image_bytes: bytes) -> dict:
    # NudeNet detects from a file path; write to a temp file, detect, clean up.
    fd, path = tempfile.mkstemp(suffix=".png")
    try:
        os.write(fd, image_bytes)
        os.close(fd)
        dets = detector.detect(path)
    finally:
        try:
            os.remove(path)
        except OSError:
            pass
    flagged = [
        {"class": d["class"], "score": round(float(d["score"]), 3)}
        for d in dets
        if d["class"] in BLOCK_CLASSES and float(d["score"]) >= SCORE_THRESHOLD
    ]
    return {"nsfw": bool(flagged), "detections": flagged}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/health"):
            self._send(200, {"ok": True})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if not self.path.startswith("/check"):
            self._send(404, {"error": "not found"})
            return
        length = int(self.headers.get("Content-Length", 0))
        data = self.rfile.read(length)
        if not data:
            self._send(400, {"error": "empty body"})
            return
        try:
            self._send(200, screen(data))
        except Exception as e:  # never let a screening error crash the endpoint
            self._send(500, {"error": str(e)})

    def log_message(self, *args):
        pass  # quiet


if __name__ == "__main__":
    print(f"NSFW screen listening on http://127.0.0.1:{PORT}  (block>={SCORE_THRESHOLD})", flush=True)
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
