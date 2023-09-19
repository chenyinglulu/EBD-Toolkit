﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class AgentScript : MonoBehaviour
{
    public TaskScript task;                         // The task this agent is pursuing.
    private NavMeshAgent navMeshAgent;              // The NavMeshAgent component of this agent.

    // Task-locations.
    private GameObject[] start;                     // The start of this agent.
    private GameObject[] end;                       // The end of this agent.
    private GameObject[] POIs;                      // Points of interest of the agent.
    
    // Attributes defining the interest of the agent.
    private float poiTime;                          // How long the agent is at each point of interest.
    private bool revisit;                           // Can the agent revisit targets?
    private bool cnd;                               // Does the agent choose its targets non-deterministically?
    private int numberOfNeeds;                      // Number of needs that this agent needs to fulfill.

    // Attributes defining shape and locomotion of agent.
    private float agentSize;                        // Size of the agent.
    private float agentRadius;                      // Radius from center in which no other agent can intrude.
    private float agentSpeed;                       // Speed of this agent.

    // Attributes for visualisation.
    public bool visualizeTrajectories;              // Set if you want to visualize trajectories.
    public bool visualizePaths;                     // Set if you want to visualize paths.
    public int traceLength;                         // How many past positions should be considered.
    private LineRenderer lineRenderer;              // Renderer used to visualize trajectory.
    private Gradient gradient;                      // Gradient used to color trace.
    private Color agentColor;                       // Color of this specific agent.

    // Technical stuff.
    private int failSave = 100;                     // Upper bound on the retries in a while loop to avoid infinity-loop.
    private float displacementInterval = 2.0f;      // After how many seconds should we change the agents position a bit to avoid deadlock.
    private float displacement = 0.1f;              // In this range the x and z component of the deplacement-vector will be chosen.
    private float lastDisplacement;                 // Last time the agent was displaced.
    private float displacementDelta = 0.1f;         // Length of the vector that the agents needs to have travelled to not be displaced.
    public List<Vector3> trajectory;                // A list of past positions of the agent, constituting the trajectories.
    private float sampleInterval;                   // Interval that needs to pass until new location gets sampled.
    private float lastSample;                       // Last time a sample was taken.
    private Vector3 firstPos;                       // First position in simulation.

    // State of the agent.
    private bool choosingPOI = false;               // True: Needs to choose new POI.
    private bool findingPOI = false;                // Is currently walking towards the POI.
    private bool fulfillingNeed = false;            // Is currently fullfilling its need.
    private bool taskCompleted = false;             // Has fulfilled all needs.
    public bool destroyRequest = false;             // Indicates to the engine if this agent wants to be destroyed.
    private bool[] poiMask;                         // Masks the POIs that are invalid.
    private int currPOI;                            // The index of the current point of interest.
    private int needsFulfilled;                     // Number needs that this agent has already fulfilled.
    private float arrivalTime;                      // The last time the agent arrived at a POI.
    public int startIndex;

    // Start is called before the first frame update
    void Start()
    {
        // Initializing the task locations.
        start = task.start;
        end = task.end;
        POIs = task.pointsOfInterest;

        // Initializing the fields defining the interest of the agent.
        poiTime = task.poiTime;
        revisit = task.revisit;
        cnd = task.chooseNonDeterministically;
        numberOfNeeds = task.numberOfNeeds;

        // Initializing the fields defining shape and locomotion of the agent.
        agentSize = task.agentSize;
        agentRadius = task.agentRadius;
        agentSpeed = task.agentSpeed;

        // Initializing technical stuff.
        lastDisplacement = Time.realtimeSinceStartup;
        trajectory = new List<Vector3>();

        // Initializing the state of the agent.
        poiMask = new bool[POIs.Length];
        for (int i = 0; i < poiMask.Length; i++) {
            poiMask[i] = true;
        }
        currPOI = 0;
        needsFulfilled = 0;
        choosingPOI = true;

        // Choosing the starting location of the agent.
        GameObject chosenStart = start[Random.Range(0, start.Length)];
        
        // Transferring the agent to the starting-location.
        NavMeshHit hit;
        NavMesh.SamplePosition(chosenStart.transform.position, out hit, 100.0f, NavMesh.AllAreas);
        navMeshAgent = GetComponent<NavMeshAgent>();
        navMeshAgent.Warp(hit.position);

        // Initializing the visualisation-related field.
        initializeAgentColor();
        GetComponent<MeshRenderer>().material.color = agentColor;
        firstPos = hit.position;
        gradient = new Gradient();
        GradientColorKey[] colorKey = new GradientColorKey[2];
        colorKey[0].color = agentColor;
        colorKey[0].time = 0.0f;
        colorKey[1].color = agentColor;
        colorKey[1].time = 1.0f;
        GradientAlphaKey[] alphaKey = new GradientAlphaKey[2];
        alphaKey[0].alpha = 0.0f;
        alphaKey[0].time = 0.0f;
        alphaKey[1].alpha = 1.0f;
        alphaKey[1].time = 1.0f;
        gradient.colorKeys = colorKey;
        gradient.alphaKeys = alphaKey;
        lineRenderer = gameObject.AddComponent<LineRenderer>();
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.colorGradient = gradient;
        lineRenderer.positionCount = traceLength;
        Vector3[] startArray = new Vector3[traceLength];
        for (int i = 0; i < traceLength; i++) {
            startArray[i] = transform.position;
        }

        // Assigning shape and locomotion properties to agent.
        navMeshAgent.avoidancePriority = Random.Range(0, 99);
        navMeshAgent.radius = agentRadius;
        navMeshAgent.speed = agentSpeed;
        transform.localScale = new Vector3(agentSize, agentSize, agentSize);
    }

    void Update()
    {
        // Remembering current position and attempt to displace to avoid deadlock (only if the agents has travelled at least one update).
        trajectory.Add(transform.position);
        if (trajectory.Count > 1) {
            displace();
        }

        // Visualize trajectories.
        if (visualizeTrajectories) {
            visualizeTrajectory();
        }

        // State: Agent is currently choosing a new point of interest.
        if (choosingPOI) {

            // If we need to fulfill more needs, we choose a new POI.
            if (needsFulfilled < numberOfNeeds) {

                // Get next POI and try to get path. Repeat this process if fails.
                currPOI = choosePOI();
                NavMeshPath path = new NavMeshPath();

                // Find closest point on NavMesh for current and goal location. This is necessary, as goal locations
                // are not necessarily placed on NavMesh.
                Vector3 startPos = ClosestPointOnNavMesh(transform.position);
                Vector3 endPos = ClosestPointOnNavMesh(POIs[currPOI].transform.position);

                if (!NavMesh.CalculatePath(startPos, endPos, NavMesh.AllAreas, path))
                {
                    throw new System.Exception($"No valid path was found to POI {currPOI}");
                }

                if (visualizePaths) {
                    visualizePath(path);
                }
                // Setting the new path of the agent.
                navMeshAgent.path = path;
                choosingPOI = false;
                findingPOI = true;
            }

            // Else we have fulfilled all needs and we can set the end as target.
            else {
                NavMeshPath path = new NavMeshPath();
                GameObject chosenEnd = end[Random.Range(0, end.Length)];
                while (!navMeshAgent.CalculatePath(chosenEnd.transform.position, path)) {
                    throw new System.Exception(task.name + ": End is not located properly. Please readjust its position.");
                }
                visualizePath(path);
                navMeshAgent.path = path;
                choosingPOI = false;
                taskCompleted = true;
            }
        }

        // State: Agent is currently walking towards the POI.
        else if (findingPOI) {
            
            // Has completed the search.
            if (hasArrivedAtPOI()) {
                arrivalTime = Time.realtimeSinceStartup;
                findingPOI = false;
                fulfillingNeed = true;
            }
        }

        // State: Agent is fulfilling need.
        else if (fulfillingNeed) {
            if (hasFulfilledNeed()) {
                needsFulfilled++;
                if (!revisit) {
                    poiMask[currPOI] = false;
                }
                fulfillingNeed = false;
                choosingPOI = true;
            }
        }

        // State: Task is completed.
        else if (taskCompleted) {
            if (hasArrivedAtPOI()) {
                destroyRequest = true;
            }
        }
    }

    // Returns the index of the next point of interest.
    public int choosePOI() {

        // If we choose non-deterministically, we identify all valid POIs and choose one randomly.
        if (cnd) {

            // Generate list of all valid POIs.
            List<int> validPOIs = new List<int>();
            for (int i = 0; i < POIs.Length; i++) {
                if (poiMask[i]) {
                    validPOIs.Add(i);
                }
            }

            // Choose POI randomly.
            int randomPOIIndex = Random.Range(0, validPOIs.Count);
            return validPOIs[randomPOIIndex];
        }

        // Else we are just picking the next POI that has not been visited yet.
        else {

            // Go through all POIs and choose the next unvisited one.
            for (int i = 0; i < POIs.Length; i++) {
                if (poiMask[i]) {
                    return i;
                }
            }
        }

        // Should never happen.
        return 0;
    }

    // Checks if agent has arrived at POI.
    public bool hasArrivedAtPOI() {

        // Check that the path is not pending.
        bool c1 = !navMeshAgent.pathPending;

        // The remaining path is shorter than the epsilon-distance to the target.
        bool c2 = navMeshAgent.remainingDistance <= navMeshAgent.stoppingDistance;

        // The agent has no path.
        bool c3 = !navMeshAgent.hasPath;

        // The agent velocity is zero.
        bool c4 = navMeshAgent.velocity.sqrMagnitude == 0;

        return c1 && c2 && (c3 || c4);
    }

    // Checks if agent has fulfilled need.
    public bool hasFulfilledNeed() {
        return Time.realtimeSinceStartup - arrivalTime >= poiTime;
    }

    public string showState() {
        if (choosingPOI) {
            return "choosingPOI";
        } else if (findingPOI) {
            return "findingPOI";
        } else if (fulfillingNeed) {
            return "fulfillingNeed";
        } else if (taskCompleted) {
            return "taskCompleted";
        } else {
            return "ERROR";
        }
    }

    public void visualizePath(NavMeshPath path) {
        for (int i = 1; i < path.corners.Length; i++) {
            Debug.DrawLine(path.corners[i-1] + new Vector3(0.0f, 1.0f, 0.0f), path.corners[i] + new Vector3(0.0f, 1.0f, 0.0f), Color.red, 10.0f);
        }
    }

    private void displace() {

        // Enough time has passed such that we can attempt displacment.
        if (Time.realtimeSinceStartup >= lastDisplacement + displacementInterval) {
            GetComponent<NavMeshAgent>().avoidancePriority = Random.Range(0, 99);
            // But only if the distance to the last position is small enough and the agent is not currently fulfilling its needs.
            if (Vector3.Distance(trajectory[trajectory.Count - 2], trajectory[trajectory.Count - 1]) < displacementDelta && !fulfillingNeed) {

                // Set the last time the agent was displaced to now.
                lastDisplacement = Time.realtimeSinceStartup;

                // Add a randomized vector on the x-z plane.
                transform.position += new Vector3(Random.value * displacement, 0.0f, Random.value * displacement);
            }
        }
    }

    // Draws trajectory of agent.
    private void visualizeTrajectory() {
        Vector3[] trace = new Vector3[traceLength];
        if (trajectory.Count < traceLength) {
            for (int i = 0; i < traceLength - trajectory.Count; i++) {
                trace[i] = firstPos;
            }
            for (int i = traceLength - trajectory.Count; i < traceLength; i++) {
                trace[i] = trajectory[i - (traceLength - trajectory.Count)];
            }
        } else {
            trace = trajectory.GetRange(trajectory.Count - traceLength, traceLength).ToArray();
        }
        lineRenderer.SetPositions(trace);
    }

    // Modifies agent color.
    private void initializeAgentColor() {
        Color taskColor = task.taskColor;
        /*
        TODO: Remove or make optional.
        float tR = taskColor.r;
        float tG = taskColor.g;
        float tB = taskColor.b;
        float randomValue = Random.value;
        float dark = 0.3f;
        float aR = Mathf.Clamp(tR - dark + randomValue * dark, 0.0f, 1.0f);
        float aG = Mathf.Clamp(tG - dark + randomValue * dark, 0.0f, 1.0f);
        float aB = Mathf.Clamp(tB - dark + randomValue * dark, 0.0f, 1.0f);
        agentColor.r = aR;
        agentColor.g = aG;
        agentColor.b = aB;
        agentColor.a = 1.0f;
        */
        agentColor = taskColor;
    }

    // Finds closest point on NavMesh, assuming that proposed position is not further than 100 units from NavMesh.
    private Vector3 ClosestPointOnNavMesh(Vector3 proposal)
    {
        NavMeshHit hit;
        bool success = NavMesh.SamplePosition(proposal, out hit, 100.0f, NavMesh.AllAreas);  // Hardcoded to 100 units of maximal distance.
        return hit.position;
    }
}
