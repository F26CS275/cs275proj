# Pyramids VLM Control Demo

This demo adds an opt-in control path for the Pyramids scene where Unity sends a natural-language instruction plus a front-facing camera image to a local Ollama bridge. The bridge calls `qwen2.5vl:7b`, records the conversation in a dashboard, and returns a structured action for the existing `PyramidAgent`.

The project now uses one supported path:

```text
Unity PyramidVlmAgentController
        -> local Ollama bridge/dashboard
        -> Ollama qwen2.5vl:7b
        -> structured action JSON
        -> PyramidAgent heuristic action
```

The bridge remains the VLM planning layer. Unity remains the reflex/action layer.

## Unity Setup

1. Open `Project/Assets/ML-Agents/Examples/Pyramids/Scenes/Pyramids.unity`.
2. Add or keep a Camera under the agent object, then attach `PyramidVlmMapCamera`.
3. Leave the camera component's `cameraMode` as `AgentForward`.
4. Add or keep `PyramidVlmAgentController` on the agent object.
5. Drag the camera into the controller's `visionCamera` field.
6. Set the agent's Behavior Parameters to Heuristic Only for VLM-driven control.

Recommended `PyramidVlmAgentController` settings:

- `plannerMode`: `Remote`
- `endpointUrl`: `http://127.0.0.1:7073/pyramids-ollama`
- `endpointTimeoutSeconds`: `180`
- `requestIntervalSeconds`: start around `1.0` to `3.0`
- `imageWidth` / `imageHeight`: start at `384`; use `256` only if latency is too high

## Bridge

Run the bridge:

```bash
python3 utils/pyramids_ollama_bridge.py --port 7073
```

Open the dashboard:

```text
http://127.0.0.1:7073/
```

The dashboard shows each Unity request, camera frame, scene description, reasoning, command, low-level action, and any bridge errors.

The bridge forwards requests to:

```text
http://127.0.0.1:11434/api/chat
```

using model:

```text
qwen2.5vl:7b
```

If the bridge prints `Client disconnected before bridge response was delivered`, Unity timed out before Qwen finished. Increase `endpointTimeoutSeconds` on `PyramidVlmAgentController`; `180` seconds is a comfortable debugging value.

## Response Contract

Unity sends:

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

The bridge returns:

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

## Planner Guardrails

The bridge prompt gives the VLM object hints:

- The switch is a small low button/pad, often red/off or green/on.
- White or light gray block stacks may be pyramids or decoys.
- The goal is a yellow/gold brick.

The bridge also protects against common local-VLM failure modes:

- `search` / `seek_*` + `none` -> `move_forward`
- `avoid_obstacle` + `none` -> `turn_left`
- `search` / `seek_*` + habitual turn with no target or blockage described -> `move_forward`

This keeps corridor exploration moving while still allowing turns when the model describes a visible side target, side corridor, wall, obstacle, or dead end.

## Front-Facing Camera

`PyramidVlmMapCamera` defaults to an agent-eye camera:

- `cameraMode`: `AgentForward`
- `agentLocalPosition`: offset from the agent body, defaulting to a small height and forward offset
- `agentLocalEulerAngles`: slight downward pitch so the floor, switch, blocks, and pyramid stay visible
- `fieldOfView`: perspective camera field of view

You can still switch `cameraMode` to `OverheadMap` for debugging or baseline comparisons.
