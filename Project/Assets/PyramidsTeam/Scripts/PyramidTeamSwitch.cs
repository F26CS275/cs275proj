using UnityEngine;

// Switch that only responds to the button-presser agent. When pressed it spawns the
// pyramid (whose top brick is the goal) and notifies the team area for a shaping reward.
public class PyramidTeamSwitch : MonoBehaviour
{
    public Material onMaterial;
    public Material offMaterial;
    public GameObject myButton;

    [HideInInspector] public PyramidTeamArea team;

    bool m_State;
    int m_PyramidIndex;

    public bool GetState() => m_State;

    public void ResetSwitch(int spawnAreaIndex, int pyramidSpawnIndex)
    {
        team.PlaceObject(gameObject, spawnAreaIndex);
        m_State = false;
        m_PyramidIndex = pyramidSpawnIndex;
        tag = "switchOff";
        transform.rotation = Quaternion.Euler(0f, 0f, 0f);
        if (myButton != null) myButton.GetComponent<Renderer>().material = offMaterial;
    }

    void OnCollisionEnter(Collision other)
    {
        // Strict role split: only the button presser can flip the switch.
        if (m_State) return;
        if (!other.gameObject.CompareTag("buttonPresser")) return;

        if (myButton != null) myButton.GetComponent<Renderer>().material = onMaterial;
        m_State = true;
        tag = "switchOn";
        team.CreatePyramid(m_PyramidIndex);
        team.ReportSwitchPressed();
    }
}
