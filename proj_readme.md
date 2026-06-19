# CS275 Project — Pyramids Variants

This project extends the Unity [ML-Agents](https://github.com/Unity-Technologies/ml-agents)
*Pyramids* environment with two custom scenes:

## 1. Multi-Agent Pyramids

A cooperative two-agent version of Pyramids. One agent (`ButtonPresserAgent`)
navigates to and presses the switch; its teammate (`GoldCollectorAgent`) then
finds the spawned pyramid and reaches the gold brick on top. Roles are split so
the team must coordinate to complete an episode.

- **Scene:** `Project/Assets/PyramidsTeam/Scenes/PyramidsTeam.unity`
- **Scripts:** `Project/Assets/PyramidsTeam/Scripts/`
- **Executable:** 
- **Setup & run guide:** [MultiAgent-setup.md](MultiAgent-setup.md)

## 2. VLM Controller

A single-agent Pyramids scene driven by a vision-language model instead of a
trained policy. Each step the agent's camera frame is sent to a local VLM
(via Ollama), which returns the next low-level action.

- **Setup & run guide:** [VLMcontroller-setup.md](VLMcontroller-setup.md)
- **Scene:** `Project/Assets/ML-Agents/Examples/Pyramids/Scenes/Pyramids.unity`
- **Scripts:** `Project/Assets/ML-Agents/Examples/Pyramids/Scripts/`

---

## Getting started

Both scenes run inside the Unity project under `Project/`. For setting up Unity,
Python, and the ML-Agents toolkit, follow the upstream install guide:
[docs/Installation.md](docs/Installation.md).