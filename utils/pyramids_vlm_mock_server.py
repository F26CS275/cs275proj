"""Tiny HTTP stub for the Pyramids VLM controller demo.

This does not run a vision-language model. It accepts the same request shape as
the Unity controller and returns deterministic low-level actions so the Unity
integration can be tested before wiring in a real planner.
"""

from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import time


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        content_length = int(self.headers.get("Content-Length", "0"))
        raw_body = self.rfile.read(content_length)

        try:
            payload = json.loads(raw_body.decode("utf-8"))
        except json.JSONDecodeError:
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b'{"error":"invalid json"}')
            return

        debug_state = payload.get("debug_state") or {}
        switch_is_on = bool(debug_state.get("switch_is_on", False))
        tick = int(time.time() * 2)

        if switch_is_on:
            command = "mock_seek_goal"
            action = "turn_left" if tick % 7 == 0 else "move_forward"
        else:
            command = "mock_seek_switch"
            action = "turn_right" if tick % 5 == 0 else "move_forward"

        image_size = len(payload.get("image_jpeg_base64") or "")
        response = {
            "command": command,
            "low_level_action": action,
            "confidence": 0.25,
            "scene_description": "Mock planner does not inspect the image.",
            "reasoning": f"Mock planner received image payload with {image_size} base64 chars.",
            "rationale": f"Mock planner received image payload with {image_size} base64 chars.",
            "command_ttl_seconds": 0.75,
        }

        body = json.dumps(response).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        return


if __name__ == "__main__":
    server = HTTPServer(("127.0.0.1", 7071), Handler)
    print("Pyramids VLM mock server listening on http://127.0.0.1:7071/pyramids-vlm")
    server.serve_forever()
