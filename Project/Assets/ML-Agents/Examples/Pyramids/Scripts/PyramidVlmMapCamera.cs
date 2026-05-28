using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(Camera))]
public class PyramidVlmMapCamera : MonoBehaviour
{
    public enum CameraMode
    {
        AgentForward,
        OverheadMap
    }

    public CameraMode cameraMode = CameraMode.AgentForward;
    public PyramidAgent agent;
    public PyramidArea area;

    [Header("Agent Forward")]
    public Vector3 agentLocalPosition = new Vector3(0f, 1.2f, 0.55f);
    public Vector3 agentLocalEulerAngles = new Vector3(8f, 0f, 0f);
    public float fieldOfView = 75f;

    [Header("Overhead Map")]
    public Vector3 mapCenterOffset;
    public float height = 45f;
    public float orthographicSize = 22f;

    Camera m_Camera;

    void OnEnable()
    {
        ConfigureCamera();
    }

    void LateUpdate()
    {
        ConfigureCamera();
    }

    void ConfigureCamera()
    {
        if (m_Camera == null)
        {
            m_Camera = GetComponent<Camera>();
        }

        m_Camera.orthographic = true;
        m_Camera.orthographicSize = orthographicSize;
        m_Camera.nearClipPlane = 0.1f;

        if (cameraMode == CameraMode.AgentForward)
        {
            ConfigureAgentForwardCamera();
        }
        else
        {
            ConfigureOverheadMapCamera();
        }
    }

    void ConfigureAgentForwardCamera()
    {
        if (agent == null)
        {
            agent = GetComponentInParent<PyramidAgent>();
        }

        m_Camera.orthographic = false;
        m_Camera.fieldOfView = fieldOfView;
        m_Camera.farClipPlane = 80f;

        if (agent != null)
        {
            var agentTransform = agent.transform;
            transform.position = agentTransform.TransformPoint(agentLocalPosition);
            transform.rotation = agentTransform.rotation * Quaternion.Euler(agentLocalEulerAngles);
        }
    }

    void ConfigureOverheadMapCamera()
    {
        m_Camera.orthographic = true;
        m_Camera.orthographicSize = orthographicSize;
        m_Camera.farClipPlane = height + 60f;

        if (area != null)
        {
            transform.position = area.transform.position + mapCenterOffset + Vector3.up * height;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
