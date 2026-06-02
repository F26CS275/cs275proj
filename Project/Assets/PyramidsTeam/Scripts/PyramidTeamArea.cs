using System;
using System.Linq;
using UnityEngine;
using Unity.MLAgents;
using Random = UnityEngine.Random;

// Owns the SimpleMultiAgentGroup that ties the two role-specialised agents together,
// and is the single place that resets the scene between episodes.
public class PyramidTeamArea : MonoBehaviour
{
    public ButtonPresserAgent buttonPresser;
    public GoldCollectorAgent goldCollector;
    public PyramidTeamSwitch areaSwitch;

    public GameObject stonePyramid;
    public GameObject pyramid;
    public GameObject[] spawnAreas;

    SimpleMultiAgentGroup m_Team;

    void Start()
    {
        m_Team = new SimpleMultiAgentGroup();
        m_Team.RegisterAgent(buttonPresser);
        m_Team.RegisterAgent(goldCollector);

        buttonPresser.team = this;
        goldCollector.team = this;
        areaSwitch.team = this;

        ResetScene();
    }

    // Called by GoldCollectorAgent when it touches the gold brick.
    public void ReportGoalReached()
    {
        m_Team.AddGroupReward(2f);
        m_Team.EndGroupEpisode();
        ResetScene();
    }

    // Optional shaping when the button is first pressed. Keep it small so the
    // collector still has to learn the harder half of the task.
    public void ReportSwitchPressed()
    {
        m_Team.AddGroupReward(0.5f);
    }

    void ResetScene()
    {
        CleanPyramids();

        // 9 spawn zones, pick 4 unique indices for: button-presser, collector,
        // switch, and the pyramid that the switch will spawn.
        var picks = Enumerable.Range(0, spawnAreas.Length)
                              .OrderBy(_ => Guid.NewGuid())
                              .Take(4)
                              .ToArray();

        PlaceAgent(buttonPresser.gameObject, picks[0]);
        PlaceAgent(goldCollector.gameObject, picks[1]);
        areaSwitch.ResetSwitch(picks[2], picks[3]);

        // Decorative stone pyramids fill the rest of the room, like the original env.
        var remaining = Enumerable.Range(0, spawnAreas.Length).Except(picks).ToArray();
        foreach (var idx in remaining)
        {
            CreateStonePyramid(idx);
        }
    }

    public void PlaceObject(GameObject obj, int spawnAreaIndex)
    {
        var t = spawnAreas[spawnAreaIndex].transform;
        var xRange = t.localScale.x / 2.1f;
        var zRange = t.localScale.z / 2.1f;
        obj.transform.position = new Vector3(Random.Range(-xRange, xRange), 2f, Random.Range(-zRange, zRange)) + t.position;
    }

    void PlaceAgent(GameObject agent, int spawnAreaIndex)
    {
        var rb = agent.GetComponent<Rigidbody>();
        if (rb != null) rb.linearVelocity = Vector3.zero;
        PlaceObject(agent, spawnAreaIndex);
        agent.transform.rotation = Quaternion.Euler(0f, Random.Range(0, 360), 0f);
    }

    public void CreatePyramid(int spawnAreaIndex)
    {
        var obj = Instantiate(pyramid, Vector3.zero, Quaternion.identity, transform);
        PlaceObject(obj, spawnAreaIndex);
    }

    void CreateStonePyramid(int spawnAreaIndex)
    {
        var obj = Instantiate(stonePyramid, Vector3.zero, Quaternion.identity, transform);
        PlaceObject(obj, spawnAreaIndex);
    }

    void CleanPyramids()
    {
        foreach (Transform child in transform)
        {
            if (child.CompareTag("pyramid"))
            {
                Destroy(child.gameObject);
            }
        }
    }
}
