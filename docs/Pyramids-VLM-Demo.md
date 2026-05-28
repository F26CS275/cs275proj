# Pyramids VLM Control Demo

This demo adds an opt-in control path for the Pyramids scene where a planner receives a natural-language instruction plus a camera image, then returns a structured low-level action for the existing `PyramidAgent`.

The intent is to prototype the interface, not to replace the trained PPO policy with a per-frame VLM. In practice, the VLM should plan at a slower cadence and output compact commands while Unity continues to handle physics and repeated action execution.

## Unity Setup

1. Open `Project/Assets/ML-Agents/Examples/Pyramids/Scenes/Pyramids.unity`.
2. Add a Camera under the agent object, then attach `PyramidVlmMapCamera`.
3. Leave the camera component's `cameraMode` as `AgentForward` so it follows the agent's front-facing view.
4. Add `PyramidVlmAgentController` to the agent object.
5. Drag the camera into the controller's `visionCamera` field.
6. Set the agent's Behavior Parameters to Heuristic Only for VLM-driven control.
7. Use `Mock` planner mode to smoke-test the wiring, `Remote` planner mode to call a custom HTTP planner, or `Ollama` planner mode to call a local vision model directly.

The original trained-agent path remains unchanged until the VLM controller is attached and the behavior is switched to heuristic control.

For your local Qwen vision model, set:

- `plannerMode`: `Ollama`
- `ollamaUrl`: `http://127.0.0.1:11434/api/chat`
- `ollamaModel`: `qwen2.5vl:7b`
- `requestIntervalSeconds`: start around `1.5` to `3.0`
- `endpointTimeoutSeconds`: use `180` for local VLM calls, especially on first model load
- `ollamaNumPredict`: use at least `256` so the model can finish the JSON object
- `imageWidth` / `imageHeight`: start at `384`; use `256` only if latency is too high

The controller sends the current camera frame to Ollama as a base64 JPEG in `messages[0].images`, asks for strict JSON, and maps `low_level_action` into the Pyramids discrete action branch.

`testOllamaConnectionOnStart` is enabled by default. When play mode starts, Unity first calls `/api/tags` and logs whether the Ollama daemon is reachable before it sends the larger image chat request.

If Unity logs `HTTP: 0` / `ConnectionError`:

- Confirm the Inspector value is exactly `http://127.0.0.1:11434/api/chat`, not `localhost`. Existing components can keep old serialized values after script defaults change.
- Verify Ollama from Terminal with `curl http://127.0.0.1:11434/api/tags`.
- Restart Unity after changing the URL or starting Ollama.
- On macOS, check System Settings > Privacy & Security > Local Network and allow Unity/Unity Hub if it appears there.
- If running a WebGL build or another sandboxed player instead of the Unity Editor, `127.0.0.1` may refer to the browser/player sandbox rather than your Mac. Use a host-reachable address and configure Ollama with `OLLAMA_HOST=0.0.0.0:11434`.

If Unity can reach the Python mock server but gets `HTTP: 0` only when calling Ollama directly, use the local Ollama bridge:

```bash
python3 utils/pyramids_ollama_bridge.py
```

Then set the Unity controller to:

- `plannerMode`: `Remote`
- `endpointUrl`: `http://127.0.0.1:7072/pyramids-ollama`

The bridge accepts Unity's existing planner request, forwards the camera image and prompt to Ollama, and returns the same action JSON back to Unity. This keeps the demo moving even if Unity's HTTP stack and Ollama's local server disagree for direct calls.

If the bridge prints `Client disconnected before bridge response was delivered`, Unity timed out before Qwen finished. Increase `endpointTimeoutSeconds` on `PyramidVlmAgentController`; `180` seconds is a comfortable debugging value.

The bridge also protects against a common local-VLM failure mode: if the model says it is searching but chooses `none` or a habitual turn without describing a visible target or blocked forward path, the bridge coerces the action to `move_forward`. This keeps corridor exploration moving.

The bridge also serves a local conversation dashboard:

```text
http://127.0.0.1:7072/
```

It shows each Unity request, the camera frame, the model's visible-scene summary, reasoning, command, and low-level action.

For a local HTTP stub, run:

```bash
python3 utils/pyramids_vlm_mock_server.py
```

Then set `plannerMode` to `Remote` and keep `endpointUrl` at `http://localhost:7071/pyramids-vlm`.

## Front-Facing Camera

`PyramidVlmMapCamera` now defaults to an agent-eye camera:

- `cameraMode`: `AgentForward`
- `agentLocalPosition`: offset from the agent body, defaulting to a small height and forward offset
- `agentLocalEulerAngles`: slight downward pitch so the floor, switch, blocks, and pyramid stay visible
- `fieldOfView`: perspective camera field of view

You can still switch `cameraMode` to `OverheadMap` for debugging or baseline comparisons.

## Ollama Planner Contract

In `Ollama` mode, Unity calls:

```text
POST http://127.0.0.1:11434/api/chat
```

With a request shaped like:

```json
{
  "model": "qwen2.5vl:7b",
  "messages": [
    {
      "role": "user",
      "content": "You control a Unity ML-Agents Pyramids agent...",
      "images": ["...base64 jpeg..."]
    }
  ],
  "stream": false,
  "format": "json"
}
```

The model's `message.content` should be JSON with this shape:

```json
{
  "command": "seek_switch",
  "low_level_action": "turn_left",
  "confidence": 0.72,
  "scene_description": "A switch is visible on the left side of the corridor.",
  "reasoning": "The switch must be pressed before the pyramid goal can spawn, so the agent should rotate toward it.",
  "rationale": "The switch is visible to the left.",
  "command_ttl_seconds": 0.75
}
```

## Remote Planner Contract

`PyramidVlmAgentController` sends a JSON POST to `endpointUrl` every `requestIntervalSeconds`.

Example request fields:

```json
{
  "task": "pyramids_agent_control",
  "instruction": "Press the switch, then reach the gold brick on the spawned pyramid.",
  "response_schema": "Return JSON with command, low_level_action, confidence, scene_description, reasoning, rationale, and command_ttl_seconds. low_level_action must be one of: none, move_forward, move_backward, turn_left, turn_right.",
  "image_format": "jpeg_base64",
  "image_jpeg_base64": "...",
  "include_debug_state": false
}
```

Expected response:

```json
{
  "command": "seek_switch",
  "low_level_action": "turn_left",
  "confidence": 0.72,
  "scene_description": "A switch is visible on the left side of the corridor.",
  "reasoning": "The switch is the next required objective, so the agent should rotate toward it.",
  "rationale": "The switch appears left of the agent in the front-facing view.",
  "command_ttl_seconds": 0.75
}
```

Supported `low_level_action` values map directly to the Pyramids discrete action branch:

- `none` -> no action
- `move_forward` -> forward
- `move_backward` -> backward
- `turn_right` -> rotate right
- `turn_left` -> rotate left

## Suggested VLM Prompt Shape

Ask the VLM for strict JSON rather than prose:

```text
You control a Unity ML-Agents Pyramids agent.
Instruction: press the switch, then reach the gold brick.
Look at the front-facing camera image and choose one low-level action.
Return only JSON with command, low_level_action, confidence, scene_description, reasoning, rationale, and command_ttl_seconds.
Allowed low_level_action values: none, move_forward, move_backward, turn_left, turn_right.
Use none only when waiting is truly required. If the goal is not visible, choose move_forward down open corridors or turn toward open space to keep exploring.
The switch is a small low button/pad, often red/off or green/on. White or light gray block stacks may be pyramids; do not ignore them. The goal is a yellow/gold brick.
```

For a stronger version, have the VLM output high-level targets such as `seek_switch` or `seek_goal`, then feed those into a small local navigator. This branch starts with direct low-level actions because it is the smallest useful end-to-end demo.
