# VLM Controller Scene — Setup

This guide walks through running the **VLM-controlled Pyramids** scene, where a
vision-language model (VLM) drives the agent instead of a trained policy. Each
step the agent's front-facing camera frame is sent to a local Python bridge,
which forwards it to a VLM running in [Ollama](https://ollama.com). The model
returns a structured action (`move_forward`, `turn_left`, etc.) that is fed back
into the agent.

```
Unity (Pyramids scene)  ──POST image──▶  Python bridge  ──/api/chat──▶  Ollama VLM
   PyramidVlmAgentController              pyramids_ollama_bridge.py        qwen2.5vl:7b
        ▲                                                                      │
        └──────────────── discrete action JSON ◀──────────────────────────────┘
```

> The multi-agent scene lives in `Project/Assets/PyramidsTeam/`. This document
> only covers the VLM controller scene in
> `Project/Assets/ML-Agents/Examples/Pyramids/`.

> A executable build of the VLM controller scene is not provided due to size constraints of the VLM model. You can follow the instructions below to run the scene in Unity engine. Note: Ollama and Qwen are required and are heavy downloads totaling ~8GB. 

---

## 1. Prerequisites

- **Unity** with the project at `Project/` opened (use the Editor version the
  project was created with; ML-Agents is already included under
  `com.unity.ml-agents`).
- **Python 3.9+** (the bridge only uses the standard library — no pip installs
  needed).
- **Ollama** installed and running locally: https://ollama.com/download

---

## 2. Pull the VLM model in Ollama

The bridge defaults to the `qwen2.5vl:7b` vision model. Pull it once:

```bash
ollama pull qwen2.5vl:7b
```

Make sure the Ollama server is running (it normally starts automatically and
listens on `http://127.0.0.1:11434`). You can verify with:

```bash
ollama list
```

---

## 3. Start the bridge server

From the repo root:

```bash
python utils/pyramids_ollama_bridge.py --port 7073
```

> **Port matters.** The Unity controller is configured to call
> `http://127.0.0.1:7073/pyramids-ollama` by default, but the bridge script's
> own default is `7072`. Pass `--port 7073` so they match (or change the
> endpoint in Unity — see step 4).

You should see:

```
Pyramids Ollama bridge listening on http://127.0.0.1:7073/pyramids-ollama
Forwarding to http://127.0.0.1:11434/api/chat with model qwen2.5vl:7b
```

Useful flags:

| Flag | Default | Purpose |
|------|---------|---------|
| `--port` | `7072` | Bridge listen port (use `7073` to match Unity) |
| `--model` | `qwen2.5vl:7b` | Ollama model name |
| `--ollama-url` | `http://127.0.0.1:11434/api/chat` | Ollama chat endpoint |
| `--timeout` | `180` | Per-request timeout (seconds) |
| `--num-predict` | `256` | Max tokens the VLM may generate |

**Live dashboard:** open `http://127.0.0.1:7073/` in a browser to watch each
camera frame, the model's scene description, chosen action, and reasoning in
real time. A health check is at `http://127.0.0.1:7073/health`.

---

## 4. Open and configure the scene in Unity

1. Open the scene `Project/Assets/ML-Agents/Examples/Pyramids/Scenes/Pyramids.unity`.
2. Select the agent in the Hierarchy (the GameObject with the `PyramidAgent`
   component). It also carries:
   - **PyramidVlmAgentController** — the component that talks to the bridge.
   - **PyramidVlmMapCamera** — configures the camera that is captured each step.
3. On the agent's **Behavior Parameters**, set **Behavior Type → Heuristic
   Only**. This is required: the VLM action is delivered through the agent's
   `Heuristic()` method, not through a trained model. (If left on
   *Default*/*Inference*, the bundled `Pyramids.onnx` policy drives the agent
   instead of the VLM.)
4. On the **PyramidVlmAgentController** component, confirm:
   - **Planner Mode** = `Remote`
   - **Endpoint Url** = `http://127.0.0.1:7073/pyramids-ollama`
     (must match the bridge port from step 3)
   - **Vision Camera** is assigned (the camera with `PyramidVlmMapCamera`).
   - **Include Image** is checked.
   - **Instruction** — optionally edit the natural-language task; the default
     asks the agent to press the switch, then reach the gold brick.

---

## 5. Run

1. Make sure Ollama is running and the bridge (step 3) is listening.
2. Press **Play** in the Unity Editor.

The agent should begin acting on the VLM's decisions. Watch the bridge dashboard
(`http://127.0.0.1:7073/`) or the Unity Console — with **Log Planner Responses**
enabled the controller logs each command and the action it mapped to.

---

## How it works (quick reference)

- **`PyramidVlmAgentController.cs`** runs a coroutine every
  `requestIntervalSeconds`, captures the `visionCamera` as a JPEG, base64-encodes
  it, and POSTs it to the endpoint. The response's `low_level_action` is mapped
  to a discrete action and held until its TTL expires.
- **`PyramidVlmMapCamera.cs`** keeps the capture camera positioned (default mode
  is an egocentric `AgentForward` view).
- **`pyramids_ollama_bridge.py`** builds the prompt, calls Ollama's `/api/chat`
  with the image, and normalizes the model's JSON before returning it to Unity.
- **`PyramidAgent.Heuristic()`** asks the controller for an action each step;
  if none is available it falls back to manual WASD control.

### Action mapping

| Action name | Discrete action |
|-------------|-----------------|
| `none` / `stop` / `wait` | 0 |
| `move_forward` | 1 |
| `move_backward` | 2 |
| `turn_right` | 3 |
| `turn_left` | 4 |

---

## Troubleshooting

- **Agent doesn't move / ignores the VLM:** Behavior Type isn't `Heuristic Only`,
  or the controller's `Planner Mode` isn't `Remote`.
- **Console warns "request failed" / connection refused:** the bridge isn't
  running, or the port in `Endpoint Url` doesn't match the `--port` you launched
  the bridge with (7073 vs 7072).
- **Bridge logs "ollama connection failed":** Ollama isn't running, or the model
  hasn't been pulled (`ollama pull qwen2.5vl:7b`).
- **Responses are very slow / time out:** larger VLMs take time per frame.
  Increase the controller's `Endpoint Timeout Seconds` and/or
  `Request Interval Seconds`, or use a smaller model via `--model`.
- **Blank / black images in the dashboard:** confirm **Vision Camera** is
  assigned on the controller and **Include Image** is checked.
