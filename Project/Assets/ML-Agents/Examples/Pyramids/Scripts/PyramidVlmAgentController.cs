using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
[RequireComponent(typeof(PyramidAgent))]
public class PyramidVlmAgentController : MonoBehaviour
{
    public enum PlannerMode
    {
        Disabled,
        Mock,
        Remote,
        Ollama
    }

    [Header("Planner")]
    public PlannerMode plannerMode = PlannerMode.Mock;

    [TextArea(2, 4)]
    public string instruction = "Press the switch, then reach the gold brick on the spawned pyramid.";

    public string endpointUrl = "http://localhost:7071/pyramids-vlm";
    public int endpointTimeoutSeconds = 180;
    public float requestIntervalSeconds = 2f;
    public float commandTimeoutSeconds = 2f;

    [Header("Ollama")]
    public string ollamaUrl = "http://127.0.0.1:11434/api/chat";
    public string ollamaModel = "qwen2.5vl:7b";
    public bool testOllamaConnectionOnStart = true;
    public float ollamaTemperature;
    public int ollamaNumPredict = 256;

    [Header("Camera")]
    [FormerlySerializedAs("mapCamera")]
    public Camera visionCamera;
    public bool includeImage = true;
    public int imageWidth = 384;
    public int imageHeight = 384;

    [Range(1, 100)]
    public int jpegQuality = 70;

    [Header("Debug")]
    public bool includeDebugState;
    public bool logPlannerResponses = true;
    public string lastPlannerCommand;

    [TextArea(2, 4)]
    public string lastPlannerRationale;

    PyramidAgent m_Agent;
    PyramidSwitch m_Switch;
    Coroutine m_PlannerLoop;
    int m_LastAction;
    float m_LastActionExpiresAt;
    int m_MockStep;
    bool m_HasTestedOllamaConnection;

    public bool TryGetDiscreteAction(out int action)
    {
        action = 0;

        if (plannerMode == PlannerMode.Disabled || Time.time > m_LastActionExpiresAt)
        {
            return false;
        }

        action = m_LastAction;
        return true;
    }

    void OnEnable()
    {
        m_Agent = GetComponent<PyramidAgent>();
        if (m_Agent.areaSwitch != null)
        {
            m_Switch = m_Agent.areaSwitch.GetComponent<PyramidSwitch>();
        }

        if (Application.isPlaying)
        {
            m_PlannerLoop = StartCoroutine(PlannerLoop());
        }
    }

    void OnDisable()
    {
        if (m_PlannerLoop != null)
        {
            StopCoroutine(m_PlannerLoop);
            m_PlannerLoop = null;
        }
    }

    IEnumerator PlannerLoop()
    {
        var waitSeconds = Mathf.Max(0.1f, requestIntervalSeconds);
        var wait = new WaitForSeconds(waitSeconds);

        while (enabled)
        {
            switch (plannerMode)
            {
                case PlannerMode.Mock:
                    ApplyMockDecision();
                    break;
                case PlannerMode.Remote:
                    yield return RequestRemoteDecision();
                    break;
                case PlannerMode.Ollama:
                    if (testOllamaConnectionOnStart && !m_HasTestedOllamaConnection)
                    {
                        m_HasTestedOllamaConnection = true;
                        yield return TestOllamaConnection();
                    }
                    yield return RequestOllamaDecision();
                    break;
            }

            yield return wait;
        }
    }

    IEnumerator RequestRemoteDecision()
    {
        if (string.IsNullOrEmpty(endpointUrl))
        {
            Debug.LogWarning("Pyramid VLM planner is in Remote mode but no endpointUrl is configured.");
            yield break;
        }

        var requestPayload = BuildRequestPayload();
        var requestJson = JsonUtility.ToJson(requestPayload);
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        var request = new UnityWebRequest(endpointUrl, "POST")
        {
            uploadHandler = new UploadHandlerRaw(requestBytes),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = endpointTimeoutSeconds
        };
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (RequestFailed(request))
        {
            LogRequestFailure("Pyramid VLM planner", request);
            request.Dispose();
            yield break;
        }

        var responseText = request.downloadHandler.text;
        request.Dispose();

        if (string.IsNullOrEmpty(responseText))
        {
            Debug.LogWarning("Pyramid VLM planner returned an empty response.");
            yield break;
        }

        if (!TryParsePlannerResponse(responseText, out var response))
        {
            Debug.LogWarning($"Pyramid VLM planner returned invalid JSON: {responseText}");
            yield break;
        }

        ApplyPlannerResponse(response);
    }

    IEnumerator TestOllamaConnection()
    {
        var tagsUrl = BuildOllamaTagsUrl();
        if (string.IsNullOrEmpty(tagsUrl))
        {
            yield break;
        }

        var request = UnityWebRequest.Get(tagsUrl);
        request.timeout = Mathf.Min(endpointTimeoutSeconds, 10);

        yield return request.SendWebRequest();

        if (RequestFailed(request))
        {
            LogRequestFailure("Ollama tags probe", request);
            request.Dispose();
            yield break;
        }

        if (logPlannerResponses)
        {
            Debug.Log($"Ollama tags probe succeeded at {tagsUrl}: {request.downloadHandler.text}");
        }

        request.Dispose();
    }

    IEnumerator RequestOllamaDecision()
    {
        if (string.IsNullOrEmpty(ollamaUrl))
        {
            Debug.LogWarning("Pyramid VLM planner is in Ollama mode but no ollamaUrl is configured.");
            yield break;
        }

        if (string.IsNullOrEmpty(ollamaModel))
        {
            Debug.LogWarning("Pyramid VLM planner is in Ollama mode but no ollamaModel is configured.");
            yield break;
        }

        var imageBase64 = CaptureCameraJpegBase64();
        var userMessage = new OllamaMessage
        {
            role = "user",
            content = BuildOllamaPrompt(),
            images = string.IsNullOrEmpty(imageBase64) ? new string[0] : new[] { imageBase64 }
        };
        var requestPayload = new OllamaChatRequest
        {
            model = ollamaModel,
            messages = new[] { userMessage },
            stream = false,
            format = "json",
            options = new OllamaOptions
            {
                temperature = ollamaTemperature,
                num_predict = ollamaNumPredict
            }
        };

        var requestJson = JsonUtility.ToJson(requestPayload);
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        var request = new UnityWebRequest(ollamaUrl, "POST")
        {
            uploadHandler = new UploadHandlerRaw(requestBytes),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = endpointTimeoutSeconds
        };
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (RequestFailed(request))
        {
            LogRequestFailure("Ollama planner", request);
            request.Dispose();
            yield break;
        }

        var responseText = request.downloadHandler.text;
        request.Dispose();

        if (string.IsNullOrEmpty(responseText))
        {
            Debug.LogWarning("Ollama planner returned an empty response.");
            yield break;
        }

        OllamaChatResponse ollamaResponse;
        try
        {
            ollamaResponse = JsonUtility.FromJson<OllamaChatResponse>(responseText);
        }
        catch (ArgumentException)
        {
            Debug.LogWarning($"Ollama planner returned invalid JSON: {responseText}");
            yield break;
        }

        if (ollamaResponse == null || ollamaResponse.message == null || string.IsNullOrEmpty(ollamaResponse.message.content))
        {
            if (ollamaResponse != null && !string.IsNullOrEmpty(ollamaResponse.error))
            {
                Debug.LogWarning($"Ollama planner returned an error: {ollamaResponse.error}");
                yield break;
            }

            Debug.LogWarning($"Ollama planner returned no message content: {responseText}");
            yield break;
        }

        if (!TryParsePlannerResponse(ollamaResponse.message.content, out var plannerResponse))
        {
            Debug.LogWarning($"Ollama planner message was not valid action JSON: {ollamaResponse.message.content}");
            yield break;
        }

        ApplyPlannerResponse(plannerResponse);
    }

    PyramidVlmRequest BuildRequestPayload()
    {
        return new PyramidVlmRequest
        {
            task = "pyramids_agent_control",
            instruction = instruction,
            response_schema = "Return JSON with command, low_level_action, confidence, scene_description, reasoning, rationale, and command_ttl_seconds. low_level_action must be one of: none, move_forward, move_backward, turn_left, turn_right.",
            image_format = includeImage ? "jpeg_base64" : "none",
            image_jpeg_base64 = CaptureCameraJpegBase64(),
            include_debug_state = includeDebugState,
            debug_state = BuildDebugState()
        };
    }

    PyramidVlmDebugState BuildDebugState()
    {
        return new PyramidVlmDebugState
        {
            switch_is_on = includeDebugState && m_Switch != null && m_Switch.GetState(),
            agent_position = transform.position,
            agent_forward = transform.forward
        };
    }

    string CaptureCameraJpegBase64()
    {
        if (!includeImage || visionCamera == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return "";
        }

        var width = Mathf.Clamp(imageWidth, 16, 1024);
        var height = Mathf.Clamp(imageHeight, 16, 1024);
        var previousTarget = visionCamera.targetTexture;
        var previousActive = RenderTexture.active;
        var renderTexture = RenderTexture.GetTemporary(width, height, 24);
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

        try
        {
            visionCamera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;
            visionCamera.Render();
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            return Convert.ToBase64String(texture.EncodeToJPG(jpegQuality));
        }
        finally
        {
            visionCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(renderTexture);
            Destroy(texture);
        }
    }

    string BuildOllamaPrompt()
    {
        var switchText = includeDebugState && m_Switch != null
            ? $"Switch state from environment: {(m_Switch.GetState() ? "on" : "off")}.\n"
            : "";

        return
            "You control a Unity ML-Agents Pyramids agent from its front-facing camera.\n" +
            $"Instruction: {instruction}\n" +
            switchText +
            "The agent can choose exactly one low-level action: none, move_forward, move_backward, turn_left, or turn_right.\n" +
            "Object appearance hints: the switch is a small low button or pad, often red/off or green/on and easy to miss near the floor. " +
            "White or light gray stacks of blocks can be pyramids or stone pyramid decoys; do not ignore white pyramid-like block stacks. " +
            "The goal is a yellow/gold brick on the spawned pyramid. " +
            "Use the image as the agent's egocentric view. First describe visible corridors, walls, switches, white/gray pyramids, and yellow/gold bricks. " +
            "If the relevant target is centered and reachable, move_forward. " +
            "If a relevant target is clearly off to one side, turn toward it. " +
            "If no switch, pyramid, or gold brick is visible, actively explore corridors. Default to move_forward through an open corridor or doorway. " +
            "Only choose turn_left or turn_right during search if the forward path is visibly blocked by a wall, close obstacle, or dead end, or if an open corridor/target is clearly to that side. " +
            "Do not choose none while searching or navigating. Use none only if the task is complete or waiting is truly the only safe action. " +
            "The task sequence is to find and press the switch, then find the spawned pyramid/gold brick and reach the gold brick.\n" +
            "Return only valid JSON with this shape: " +
            "{\"command\":\"seek_switch|seek_goal|search|avoid_obstacle\",\"low_level_action\":\"none|move_forward|move_backward|turn_left|turn_right\",\"confidence\":0.0,\"scene_description\":\"brief description of what is visible\",\"reasoning\":\"brief reason for the chosen action\",\"rationale\":\"short reason\",\"command_ttl_seconds\":0.75}";
    }

    string BuildOllamaTagsUrl()
    {
        const string chatPath = "/api/chat";
        var trimmedUrl = ollamaUrl.TrimEnd('/');

        if (trimmedUrl.EndsWith(chatPath, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedUrl.Substring(0, trimmedUrl.Length - chatPath.Length) + "/api/tags";
        }

        var apiIndex = trimmedUrl.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (apiIndex >= 0)
        {
            return trimmedUrl.Substring(0, apiIndex) + "/api/tags";
        }

        return "";
    }

    void ApplyPlannerResponse(PyramidVlmResponse response)
    {
        if (response == null)
        {
            Debug.LogWarning("Pyramid VLM planner response could not be parsed.");
            return;
        }

        var requestedAction = string.IsNullOrEmpty(response.low_level_action)
            ? response.command
            : response.low_level_action;

        if (IsNoOpActionName(requestedAction) && IsActiveCommand(response.command))
        {
            requestedAction = "move_forward";
            response.reasoning = $"{PreferredReasoning(response)} Coerced from none to move_forward so search/exploration keeps moving.";
            response.rationale = response.reasoning;
        }
        else if (IsNoOpActionName(requestedAction) && IsAvoidObstacleCommand(response.command))
        {
            requestedAction = "turn_left";
            response.reasoning = $"{PreferredReasoning(response)} Coerced from none to turn_left for obstacle recovery.";
            response.rationale = response.reasoning;
        }
        else if (IsActiveCommand(response.command) &&
            IsSearchTurnActionName(requestedAction) &&
            ShouldPreferForwardExploration(response))
        {
            var originalAction = requestedAction;
            requestedAction = "move_forward";
            response.reasoning = $"{PreferredReasoning(response)} Coerced from {originalAction} to move_forward because no target or blockage was described; corridor exploration should progress forward.";
            response.rationale = response.reasoning;
        }

        if (!TryMapDiscreteAction(requestedAction, out var discreteAction))
        {
            Debug.LogWarning($"Pyramid VLM planner returned unsupported action '{requestedAction}'.");
            return;
        }

        var ttl = response.command_ttl_seconds > 0f ? response.command_ttl_seconds : commandTimeoutSeconds;
        ApplyDecision(discreteAction, requestedAction, PreferredReasoning(response), ttl);
    }

    string PreferredReasoning(PyramidVlmResponse response)
    {
        if (!string.IsNullOrEmpty(response.reasoning))
        {
            return response.reasoning;
        }

        if (!string.IsNullOrEmpty(response.rationale))
        {
            return response.rationale;
        }

        return "No reasoning returned.";
    }

    bool TryParsePlannerResponse(string rawResponse, out PyramidVlmResponse response)
    {
        response = null;

        if (string.IsNullOrEmpty(rawResponse))
        {
            return false;
        }

        var json = ExtractJsonObject(rawResponse);
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            response = JsonUtility.FromJson<PyramidVlmResponse>(json);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return response != null &&
            (!string.IsNullOrEmpty(response.low_level_action) || !string.IsNullOrEmpty(response.command));
    }

    string ExtractJsonObject(string rawResponse)
    {
        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');

        if (start < 0 || end < start)
        {
            return "";
        }

        return rawResponse.Substring(start, end - start + 1);
    }

    bool IsActiveCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return false;
        }

        var normalized = command.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return normalized == "search" ||
            normalized == "seek_switch" ||
            normalized == "seek_goal" ||
            normalized == "seek_pyramid" ||
            normalized == "explore";
    }

    bool IsAvoidObstacleCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return false;
        }

        var normalized = command.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return normalized == "avoid_obstacle";
    }

    bool IsNoOpActionName(string actionName)
    {
        if (string.IsNullOrEmpty(actionName))
        {
            return true;
        }

        var normalized = actionName.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return normalized == "none" ||
            normalized == "no_op" ||
            normalized == "noop" ||
            normalized == "stop" ||
            normalized == "wait" ||
            normalized == "idle";
    }

    bool IsSearchTurnActionName(string actionName)
    {
        if (string.IsNullOrEmpty(actionName))
        {
            return false;
        }

        var normalized = actionName.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return normalized == "turn_left" ||
            normalized == "left" ||
            normalized == "rotate_left" ||
            normalized == "turn_right" ||
            normalized == "right" ||
            normalized == "rotate_right";
    }

    bool ShouldPreferForwardExploration(PyramidVlmResponse response)
    {
        var combinedText = $"{response.scene_description} {response.reasoning} {response.rationale}".ToLowerInvariant();

        if (ContainsAny(combinedText,
            "blocked",
            "wall ahead",
            "dead end",
            "dead-end",
            "obstacle ahead",
            "close obstacle",
            "path ahead is blocked",
            "forward path is blocked",
            "corridor is blocked"))
        {
            return false;
        }

        var saysNoTarget = ContainsAny(combinedText,
            "no switch",
            "no pyramid",
            "no gold",
            "no goal",
            "no target",
            "not visible",
            "nothing visible",
            "cannot see",
            "can't see");

        var mentionsTarget = ContainsAny(combinedText,
            "switch",
            "button",
            "pyramid",
            "gold",
            "yellow",
            "brick",
            "goal");

        return saysNoTarget || !mentionsTarget;
    }

    bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle))
            {
                return true;
            }
        }

        return false;
    }

    void ApplyMockDecision()
    {
        var switchIsOn = m_Switch != null && m_Switch.GetState();
        var action = switchIsOn
            ? ((m_MockStep % 7 == 0) ? 4 : 1)
            : ((m_MockStep % 5 == 0) ? 3 : 1);
        var command = switchIsOn ? "mock_seek_goal" : "mock_seek_switch";
        var rationale = "Mock mode exercises the action bridge. Use Remote mode for a real VLM planner.";

        m_MockStep++;
        ApplyDecision(action, command, rationale, commandTimeoutSeconds);
    }

    void ApplyDecision(int discreteAction, string command, string rationale, float ttlSeconds)
    {
        m_LastAction = discreteAction;
        m_LastActionExpiresAt = Time.time + Mathf.Max(0.1f, ttlSeconds);
        lastPlannerCommand = command;
        lastPlannerRationale = rationale;

        if (logPlannerResponses)
        {
            Debug.Log($"Pyramid VLM planner command '{command}' mapped to action {discreteAction}: {rationale}");
        }
    }

    bool TryMapDiscreteAction(string actionName, out int discreteAction)
    {
        discreteAction = 0;

        if (string.IsNullOrEmpty(actionName))
        {
            return false;
        }

        var normalized = actionName.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        switch (normalized)
        {
            case "none":
            case "no_op":
            case "noop":
            case "stop":
            case "wait":
            case "idle":
                discreteAction = 0;
                return true;
            case "forward":
            case "move_forward":
            case "advance":
                discreteAction = 1;
                return true;
            case "backward":
            case "move_backward":
            case "reverse":
                discreteAction = 2;
                return true;
            case "right":
            case "turn_right":
            case "rotate_right":
            case "clockwise":
                discreteAction = 3;
                return true;
            case "left":
            case "turn_left":
            case "rotate_left":
            case "counter_clockwise":
            case "counterclockwise":
                discreteAction = 4;
                return true;
            default:
                return false;
        }
    }

    bool RequestFailed(UnityWebRequest request)
    {
#if UNITY_2020_2_OR_NEWER
        return request.result == UnityWebRequest.Result.ConnectionError ||
            request.result == UnityWebRequest.Result.ProtocolError ||
            request.result == UnityWebRequest.Result.DataProcessingError;
#else
        return request.isNetworkError || request.isHttpError;
#endif
    }

    void LogRequestFailure(string label, UnityWebRequest request)
    {
        var body = request.downloadHandler != null ? request.downloadHandler.text : "";
        var bodySuffix = string.IsNullOrEmpty(body) ? "" : $" Body: {body}";
#if UNITY_2020_2_OR_NEWER
        var result = request.result.ToString();
#else
        var result = request.isNetworkError ? "NetworkError" : request.isHttpError ? "HttpError" : "Unknown";
#endif
        Debug.LogWarning($"{label} request failed. Result: {result}. HTTP: {request.responseCode}. Error: {request.error}.{bodySuffix}");
    }

    [Serializable]
    class PyramidVlmRequest
    {
        public string task;
        public string instruction;
        public string response_schema;
        public string image_format;
        public string image_jpeg_base64;
        public bool include_debug_state;
        public PyramidVlmDebugState debug_state;
    }

    [Serializable]
    class PyramidVlmDebugState
    {
        public bool switch_is_on;
        public Vector3 agent_position;
        public Vector3 agent_forward;
    }

    [Serializable]
    class PyramidVlmResponse
    {
        public string command;
        public string low_level_action;
        public float confidence;
        public string scene_description;
        public string reasoning;
        public string rationale;
        public float command_ttl_seconds;
    }

    [Serializable]
    class OllamaChatRequest
    {
        public string model;
        public OllamaMessage[] messages;
        public bool stream;
        public string format;
        public OllamaOptions options;
    }

    [Serializable]
    class OllamaMessage
    {
        public string role;
        public string content;
        public string[] images;
    }

    [Serializable]
    class OllamaOptions
    {
        public float temperature;
        public int num_predict;
    }

    [Serializable]
    class OllamaChatResponse
    {
        public string model;
        public OllamaMessage message;
        public bool done;
        public string done_reason;
        public string error;
    }
}
