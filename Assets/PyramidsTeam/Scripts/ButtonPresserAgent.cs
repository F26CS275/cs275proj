using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

// Role: navigate to the switch and press it. Cannot end the episode by touching the gold;
// only its teammate can do that. Tag this GameObject "buttonPresser" in the inspector.
public class ButtonPresserAgent : Agent
{
    [HideInInspector] public PyramidTeamArea team;
    public PyramidTeamSwitch areaSwitch;
    public bool useVectorObs;

    Rigidbody m_Rb;

    public override void Initialize()
    {
        m_Rb = GetComponent<Rigidbody>();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (useVectorObs)
        {
            sensor.AddObservation(areaSwitch.GetState());
            sensor.AddObservation(transform.InverseTransformDirection(m_Rb.velocity));
        }
    }

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        // Tiny per-step penalty encourages getting to the switch quickly.
        AddReward(-1f / MaxStep);
        Move(actionBuffers.DiscreteActions);
    }

    void Move(ActionSegment<int> act)
    {
        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;
        switch (act[0])
        {
            case 1: dirToGo = transform.forward; break;
            case 2: dirToGo = -transform.forward; break;
            case 3: rotateDir = transform.up; break;
            case 4: rotateDir = -transform.up; break;
        }
        transform.Rotate(rotateDir, Time.deltaTime * 200f);
        m_Rb.AddForce(dirToGo * 2f, ForceMode.VelocityChange);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        if (Input.GetKey(KeyCode.D)) d[0] = 3;
        else if (Input.GetKey(KeyCode.W)) d[0] = 1;
        else if (Input.GetKey(KeyCode.A)) d[0] = 4;
        else if (Input.GetKey(KeyCode.S)) d[0] = 2;
        else d[0] = 0;
    }

    // Per-agent reset (velocity zeroing). Placement is driven by PyramidTeamArea.
    public override void OnEpisodeBegin()
    {
        if (m_Rb != null) m_Rb.velocity = Vector3.zero;
    }
}
