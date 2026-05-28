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
        Disabled = 0,
        Remote = 2
    }

    [Header("Planner")]
    public PlannerMode plannerMode = PlannerMode.Remote;

    [TextArea(2, 4)]
    public string instruction = "Press the switch, then reach the gold brick on the spawned pyramid. If none of the goals are in view, go to the next room in view.";

    public string endpointUrl = "http://127.0.0.1:7073/pyramids-ollama";
    public int endpointTimeoutSeconds = 180;
    public float requestIntervalSeconds = 2f;
    public float commandTimeoutSeconds = 2f;

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
            if (plannerMode == PlannerMode.Remote)
            {
                yield return RequestRemoteDecision();
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

        var normalized = NormalizeName(command);
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

        return NormalizeName(command) == "avoid_obstacle";
    }

    bool IsNoOpActionName(string actionName)
    {
        if (string.IsNullOrEmpty(actionName))
        {
            return true;
        }

        var normalized = NormalizeName(actionName);
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

        var normalized = NormalizeName(actionName);
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

    string NormalizeName(string value)
    {
        return value.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
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

        var normalized = NormalizeName(actionName);
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
}
