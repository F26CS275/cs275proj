"""Local Ollama bridge for the Pyramids VLM controller demo.

Unity talks to this server using the Remote planner contract. The bridge then
calls Ollama's /api/chat endpoint with the current camera JPEG and returns the
model's structured action JSON back to Unity.
"""

from __future__ import annotations

from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import argparse
import html
import json
import sys
import time
import urllib.error
import urllib.request


DEFAULT_PROMPT = """You control a Unity ML-Agents Pyramids agent from its front-facing camera.
Instruction: {instruction}
{switch_state}
The agent can choose exactly one low-level action: none, move_forward, move_backward, turn_left, or turn_right.
Object appearance hints:
- The switch is a small low button/pad, often with a yellow/off or green/on top. It may be easy to miss near the floor.
- White or light gray stacks of blocks can be pyramids or stone pyramid decoys. Do not ignore white pyramid-like block stacks.
- The goal is a yellow/gold brick on the spawned pyramid.
Use the image as the agent's egocentric view. First describe visible corridors, walls, switches, white/gray pyramids, and yellow/gold bricks.
If the relevant target is centered and reachable, move_forward.
If a relevant target is clearly off to one side, turn toward it.
If no switch, pyramid, or gold brick is visible, actively explore corridors. Default to move_forward through an open corridor or doorway.
Only choose turn_left or turn_right during search if the forward path is visibly blocked by a wall, close obstacle, or dead end, or if an open corridor/target is clearly to that side.
Do not choose none while searching or navigating. Use none only if the task is complete or waiting is truly the only safe action.
Remember previous actions and observations in order to build a mental map of the environment and where the switch, pyramids, and gold brick are likely to be. This should inform your decisions on new areas to explore and when to commit to a direction change versus pushing forward.
The task sequence is to find and press the switch, then find the spawned pyramid/gold brick and reach the gold brick.
Return only valid JSON with this shape:
{{"command":"seek_switch|seek_goal|search|avoid_obstacle","low_level_action":"none|move_forward|move_backward|turn_left|turn_right","confidence":0.0,"scene_description":"brief description of what is visible","reasoning":"brief reason for the chosen action","rationale":"short reason","command_ttl_seconds":0.75}}
"""

ACTIVE_COMMANDS = {"search", "seek_switch", "seek_goal", "seek_pyramid", "explore"}
NO_OP_ACTIONS = {"", "none", "no_op", "noop", "stop", "wait", "idle"}
SEARCH_TURN_ACTIONS = {"turn_left", "left", "rotate_left", "turn_right", "right", "rotate_right"}
TARGET_WORDS = {"switch", "button", "pyramid", "gold", "yellow", "brick", "goal"}
NO_TARGET_PHRASES = {
    "no switch",
    "no pyramid",
    "no gold",
    "no goal",
    "no target",
    "not visible",
    "nothing visible",
    "cannot see",
    "can't see",
}
BLOCKED_PHRASES = {
    "blocked",
    "wall ahead",
    "dead end",
    "dead-end",
    "obstacle ahead",
    "close obstacle",
    "path ahead is blocked",
    "forward path is blocked",
    "corridor is blocked",
}
EVENTS = deque(maxlen=50)
SERVER_STARTED_AT = time.time()


def extract_json_object(text: str) -> dict:
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end < start:
        raise ValueError(f"response did not contain a JSON object: {text}")
    return json.loads(text[start : end + 1])


def normalize_planner_response(response: dict) -> dict:
    command = str(response.get("command") or "").strip().lower()
    action = str(response.get("low_level_action") or "").strip().lower()
    rationale = str(response.get("rationale") or response.get("reasoning") or "").strip()
    scene_text = str(response.get("scene_description") or "")
    reasoning_text = str(response.get("reasoning") or response.get("rationale") or "")
    combined_text = f"{scene_text} {reasoning_text}".lower()

    if action in NO_OP_ACTIONS and command in ACTIVE_COMMANDS:
        response["low_level_action"] = "move_forward"
        rationale = f"{rationale} Coerced from none to move_forward so search/exploration keeps moving.".strip()

    if action in NO_OP_ACTIONS and command == "avoid_obstacle":
        response["low_level_action"] = "turn_left"
        rationale = f"{rationale} Coerced from none to turn_left for obstacle recovery.".strip()

    if (
        command in ACTIVE_COMMANDS
        and action in SEARCH_TURN_ACTIONS
        and should_prefer_forward_exploration(combined_text)
    ):
        response["low_level_action"] = "move_forward"
        rationale = (
            f"{rationale} Coerced from {action} to move_forward because no target or blockage was described; "
            "corridor exploration should progress forward."
        ).strip()

    if not response.get("command"):
        response["command"] = "search"

    if not response.get("scene_description"):
        response["scene_description"] = "No scene description returned."

    if not response.get("reasoning"):
        response["reasoning"] = rationale or "No reasoning returned."

    response["rationale"] = rationale or response["reasoning"]

    if not response.get("command_ttl_seconds"):
        response["command_ttl_seconds"] = 0.75

    return response


def should_prefer_forward_exploration(text: str) -> bool:
    has_blockage = any(phrase in text for phrase in BLOCKED_PHRASES)
    if has_blockage:
        return False

    mentions_target = any(word in text for word in TARGET_WORDS)
    says_no_target = any(phrase in text for phrase in NO_TARGET_PHRASES)
    return says_no_target or not mentions_target


def add_event(event: dict):
    event["id"] = len(EVENTS) + 1 if not EVENTS else EVENTS[-1]["id"] + 1
    event["timestamp"] = time.strftime("%H:%M:%S")
    EVENTS.append(event)


def build_prompt(payload: dict) -> str:
    instruction = payload.get(
        "instruction",
        "Press the switch, then reach the gold brick on the spawned pyramid.",
    )
    debug_state = payload.get("debug_state") or {}
    switch_state = ""
    if payload.get("include_debug_state") and "switch_is_on" in debug_state:
        state_text = "on" if debug_state["switch_is_on"] else "off"
        switch_state = f"Switch state from environment: {state_text}.\n"

    return DEFAULT_PROMPT.format(instruction=instruction, switch_state=switch_state)


def call_ollama(payload: dict, ollama_url: str, model: str, timeout: int, num_predict: int) -> dict:
    image_base64 = payload.get("image_jpeg_base64") or ""
    message = {
        "role": "user",
        "content": build_prompt(payload),
    }
    if image_base64:
        message["images"] = [image_base64]

    ollama_payload = {
        "model": model,
        "messages": [message],
        "stream": False,
        "format": "json",
        "options": {
            "temperature": 0,
            "num_predict": num_predict,
        },
    }
    request = urllib.request.Request(
        ollama_url,
        data=json.dumps(ollama_payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=timeout) as response:
        response_payload = json.loads(response.read().decode("utf-8"))

    if response_payload.get("error"):
        raise RuntimeError(response_payload["error"])

    content = ((response_payload.get("message") or {}).get("content") or "").strip()
    if not content:
        raise RuntimeError(f"Ollama returned no message content: {response_payload}")

    planner_response = extract_json_object(content)
    if not planner_response.get("low_level_action") and not planner_response.get("command"):
        raise RuntimeError(f"Ollama response did not include an action: {planner_response}")

    return normalize_planner_response(planner_response)


class Handler(BaseHTTPRequestHandler):
    ollama_url = "http://127.0.0.1:11434/api/chat"
    model = "qwen2.5vl:7b"
    timeout = 120
    num_predict = 256

    def do_GET(self):
        if self.path == "/" or self.path == "/index.html":
            self.respond_html(build_dashboard_html())
            return

        if self.path == "/events":
            self.respond_json(
                {
                    "started_at": SERVER_STARTED_AT,
                    "ollama_url": self.ollama_url,
                    "model": self.model,
                    "num_predict": self.num_predict,
                    "events": list(EVENTS),
                }
            )
            return

        if self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
            return

        if self.path != "/health":
            self.send_error(404, "not found")
            return

        self.respond_json(
            {
                "ok": True,
                "ollama_url": self.ollama_url,
                "model": self.model,
                "num_predict": self.num_predict,
            }
        )

    def do_POST(self):
        content_length = int(self.headers.get("Content-Length", "0"))
        raw_body = self.rfile.read(content_length)
        started_at = time.time()
        payload = {}

        try:
            payload = json.loads(raw_body.decode("utf-8"))
            planner_response = call_ollama(
                payload,
                self.ollama_url,
                self.model,
                self.timeout,
                self.num_predict,
            )
        except json.JSONDecodeError as exc:
            error = {"error": f"invalid json: {exc}"}
            add_event(build_event(payload, None, error, time.time() - started_at))
            self.respond_json(error, status=400)
            return
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            error = {"error": f"ollama http {exc.code}: {body}"}
            add_event(build_event(payload, None, error, time.time() - started_at))
            self.respond_json(error, status=502)
            return
        except urllib.error.URLError as exc:
            error = {"error": f"ollama connection failed: {exc}"}
            add_event(build_event(payload, None, error, time.time() - started_at))
            self.respond_json(error, status=502)
            return
        except Exception as exc:
            error = {"error": str(exc)}
            add_event(build_event(payload, None, error, time.time() - started_at))
            self.respond_json(error, status=500)
            return

        add_event(build_event(payload, planner_response, None, time.time() - started_at))
        self.respond_json(planner_response)

    def respond_json(self, payload: dict, status: int = 200):
        body = json.dumps(payload).encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            print(
                "Client disconnected before bridge response was delivered. "
                "Increase Unity endpointTimeoutSeconds if this happens often.",
                file=sys.stderr,
            )

    def respond_html(self, markup: str, status: int = 200):
        body = markup.encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            print("Client disconnected before dashboard response was delivered.", file=sys.stderr)

    def log_message(self, format, *args):
        return


def build_event(payload: dict, response: dict | None, error: dict | None, duration: float) -> dict:
    image_base64 = payload.get("image_jpeg_base64") or ""
    return {
        "duration_seconds": round(duration, 3),
        "instruction": payload.get("instruction", ""),
        "image_jpeg_base64": image_base64,
        "image_format": payload.get("image_format", "jpeg_base64" if image_base64 else "none"),
        "debug_state": payload.get("debug_state") or {},
        "response": response,
        "error": error,
    }


def build_dashboard_html() -> str:
    title = "Pyramids VLM Conversation"
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{html.escape(title)}</title>
  <style>
    :root {{
      color-scheme: dark;
      --bg: #101214;
      --panel: #1a1d21;
      --panel-2: #22262b;
      --text: #f2f4f5;
      --muted: #a9b0b8;
      --line: #343a42;
      --accent: #7dd3fc;
      --good: #86efac;
      --bad: #fca5a5;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      font: 14px/1.45 -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }}
    header {{
      position: sticky;
      top: 0;
      z-index: 2;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 18px;
      border-bottom: 1px solid var(--line);
      background: rgba(16, 18, 20, 0.96);
    }}
    h1 {{
      margin: 0;
      font-size: 18px;
      font-weight: 650;
      letter-spacing: 0;
    }}
    .status {{
      display: flex;
      align-items: center;
      gap: 12px;
      color: var(--muted);
      white-space: nowrap;
    }}
    main {{
      display: grid;
      grid-template-columns: minmax(280px, 360px) minmax(0, 1fr);
      gap: 16px;
      padding: 16px;
    }}
    aside, section {{
      min-width: 0;
    }}
    .summary {{
      padding: 14px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
    }}
    .summary dl {{
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 8px 12px;
      margin: 0;
    }}
    dt {{ color: var(--muted); }}
    dd {{
      margin: 0;
      overflow-wrap: anywhere;
    }}
    .events {{
      display: grid;
      gap: 12px;
    }}
    .empty {{
      padding: 28px;
      border: 1px dashed var(--line);
      border-radius: 8px;
      color: var(--muted);
      text-align: center;
    }}
    article {{
      display: grid;
      grid-template-columns: 256px minmax(0, 1fr);
      gap: 14px;
      padding: 14px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
    }}
    img {{
      width: 256px;
      height: 256px;
      object-fit: contain;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #050607;
    }}
    .no-image {{
      display: grid;
      place-items: center;
      width: 256px;
      height: 256px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #050607;
      color: var(--muted);
    }}
    .meta {{
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-bottom: 10px;
      color: var(--muted);
    }}
    .pill {{
      display: inline-flex;
      align-items: center;
      min-height: 24px;
      padding: 2px 8px;
      border-radius: 999px;
      background: var(--panel-2);
      color: var(--text);
    }}
    .action {{ color: var(--good); }}
    .error {{ color: var(--bad); }}
    .field {{
      margin-top: 9px;
    }}
    .label {{
      margin-bottom: 2px;
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
    }}
    pre {{
      overflow: auto;
      margin: 10px 0 0;
      padding: 10px;
      border-radius: 6px;
      background: #0b0d0f;
      color: #d7dde3;
      font-size: 12px;
    }}
    @media (max-width: 820px) {{
      main {{ grid-template-columns: 1fr; }}
      article {{ grid-template-columns: 1fr; }}
      img, .no-image {{ width: 100%; height: auto; min-height: 220px; }}
    }}
  </style>
</head>
<body>
  <header>
    <h1>{html.escape(title)}</h1>
    <div class="status">
      <span id="model">model</span>
      <span id="count">0 events</span>
    </div>
  </header>
  <main>
    <aside>
      <div class="summary">
        <dl>
          <dt>Bridge</dt><dd id="bridge">loading</dd>
          <dt>Model</dt><dd id="model-detail">loading</dd>
          <dt>Last Update</dt><dd id="updated">never</dd>
        </dl>
      </div>
    </aside>
    <section>
      <div id="events" class="events">
        <div class="empty">Waiting for Unity requests.</div>
      </div>
    </section>
  </main>
  <script>
    const eventsEl = document.getElementById('events');
    const countEl = document.getElementById('count');
    const modelEl = document.getElementById('model');
    const bridgeEl = document.getElementById('bridge');
    const modelDetailEl = document.getElementById('model-detail');
    const updatedEl = document.getElementById('updated');

    function escapeHtml(value) {{
      return String(value ?? '').replace(/[&<>"']/g, ch => ({{
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
      }}[ch]));
    }}

    function renderEvent(event) {{
      const response = event.response || {{}};
      const error = event.error;
      const image = event.image_jpeg_base64
        ? `<img alt="Agent camera frame" src="data:image/jpeg;base64,${{event.image_jpeg_base64}}">`
        : '<div class="no-image">No image</div>';
      const action = response.low_level_action || 'none';
      const command = response.command || 'unknown';
      const scene = response.scene_description || '';
      const reasoning = response.reasoning || response.rationale || '';
      return `<article>
        <div>${{image}}</div>
        <div>
          <div class="meta">
            <span class="pill">#${{event.id}}</span>
            <span class="pill">${{escapeHtml(event.timestamp)}}</span>
            <span class="pill">${{escapeHtml(event.duration_seconds)}}s</span>
            ${{error ? '<span class="pill error">error</span>' : `<span class="pill action">${{escapeHtml(command)}} / ${{escapeHtml(action)}}</span>`}}
          </div>
          <div class="field"><div class="label">Instruction</div>${{escapeHtml(event.instruction)}}</div>
          ${{error ? `<div class="field error"><div class="label">Error</div>${{escapeHtml(error.error)}}</div>` : `
            <div class="field"><div class="label">What VLM Sees</div>${{escapeHtml(scene)}}</div>
            <div class="field"><div class="label">Reasoning</div>${{escapeHtml(reasoning)}}</div>
            <div class="field"><div class="label">Confidence</div>${{escapeHtml(response.confidence)}}</div>
            <pre>${{escapeHtml(JSON.stringify(response, null, 2))}}</pre>
          `}}
        </div>
      </article>`;
    }}

    async function refresh() {{
      try {{
        const res = await fetch('/events', {{ cache: 'no-store' }});
        const data = await res.json();
        const events = data.events || [];
        bridgeEl.textContent = data.ollama_url || 'unknown';
        modelEl.textContent = data.model || 'model';
        modelDetailEl.textContent = data.model || 'unknown';
        countEl.textContent = `${{events.length}} event${{events.length === 1 ? '' : 's'}}`;
        updatedEl.textContent = new Date().toLocaleTimeString();
        eventsEl.innerHTML = events.length
          ? events.slice().reverse().map(renderEvent).join('')
          : '<div class="empty">Waiting for Unity requests.</div>';
      }} catch (err) {{
        eventsEl.innerHTML = `<div class="empty error">Dashboard refresh failed: ${{escapeHtml(err.message)}}</div>`;
      }}
    }}

    refresh();
    setInterval(refresh, 1500);
  </script>
</body>
</html>"""


def parse_args():
    parser = argparse.ArgumentParser(description="Run a local Pyramids Ollama bridge.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7072)
    parser.add_argument("--ollama-url", default="http://127.0.0.1:11434/api/chat")
    parser.add_argument("--model", default="qwen2.5vl:7b")
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--num-predict", type=int, default=256)
    return parser.parse_args()


if __name__ == "__main__":
    args = parse_args()
    Handler.ollama_url = args.ollama_url
    Handler.model = args.model
    Handler.timeout = args.timeout
    Handler.num_predict = args.num_predict
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(
        "Pyramids Ollama bridge listening on "
        f"http://{args.host}:{args.port}/pyramids-ollama"
    )
    print(f"Forwarding to {args.ollama_url} with model {args.model}")
    print(f"Ollama num_predict={args.num_predict}")
    server.serve_forever()
