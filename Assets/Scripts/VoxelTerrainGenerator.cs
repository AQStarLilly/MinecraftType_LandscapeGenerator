using System.Collections.Generic;
using UnityEngine;

public class VoxelTerrainGenerator : MonoBehaviour
{
    // -------- Inspector: Sizes & Prefabs --------
    [Header("World Size")]
    [Range(16, 256)] public int sizeX = 64;
    [Range(16, 256)] public int sizeZ = 64;
    [Range(8, 64)] public int maxHeight = 24;

    [Header("Water")]
    [Tooltip("Blocks at or below this Y will be underwater (filled up to this level).")]
    [Range(0, 63)] public int waterLevel = 8;
    [Tooltip("Replace shoreline top with sand when exactly at waterLevel.")]
    public bool useShoreSand = true;

    [Header("Algorithm Seed")]
    public bool useRandomSeed = true;
    public int seed = 12345;

    [Header("Phase 1: Heights & Dips (Proprietary)")]
    [Tooltip("Max step height for the dual random walks.")]
    [Range(1, 5)] public int maxStepPerWalk = 2;
    [Tooltip("Number of smoothing passes (box blur).")]
    [Range(0, 8)] public int smoothPasses = 3;
    [Tooltip("Chance [0..1] to flatten a cell to a local plateau before smoothing.")]
    [Range(0f, 1f)] public float plateauChance = 0.12f;
    [Tooltip("Clamp final heights into [baseHeight .. baseHeight+heightSpan].")]
    [Range(0, 32)] public int baseHeight = 6;
    [Range(8, 48)] public int heightSpan = 16;

    [Header("Phase 3: Pathing")]
    [Tooltip("Y clearance (height) for carved tunnel space (2 or 3 blocks).")]
    [Range(2, 4)] public int tunnelClearance = 3;
    [Tooltip("Maximum step up allowed without carving.")]
    [Range(1, 3)] public int maxNaturalStep = 1;
    [Tooltip("Extra path cost for climbing up vs flat/down; larger means more detours.")]
    [Range(0, 10)] public int uphillPenalty = 3;
    [Tooltip("If true, shows gizmos for path and tunnels in Scene view.")]
    public bool debugGizmos = true;

    [Header("Prefabs")]
    public GameObject grassPrefab;
    public GameObject dirtPrefab;
    public GameObject pathPrefab;
    public GameObject waterPrefab;
    public GameObject stonePrefab; 
    public GameObject sandPrefab; 

    [Header("Runtime")]
    public Transform containerParent; // optional; if null will be created

    // -------- Internal state --------
    private System.Random prng;
    private int[,] height;      // terrain surface height per cell
    private bool[,] isWater;    // if this (x,z) is below water (terrain < waterline)
    private bool[,] isPath;     // path footprint on the surface
    private Vector2Int[] pathCells; // final path sequence

    private Transform container; // holder for spawned cubes

    private void OnValidate()
    {
        heightSpan = Mathf.Max(1, heightSpan);
        maxHeight = Mathf.Clamp(maxHeight, 8, 64);
        waterLevel = Mathf.Clamp(waterLevel, 0, maxHeight - 1);
        tunnelClearance = Mathf.Clamp(tunnelClearance, 2, 4);
    }

    private void Start()
    {
        if (useRandomSeed) seed = Random.Range(0, int.MaxValue);

        Generate();
    }

    private void Update()
    {
        // Press 'R' to regenerate with a fresh seed or same seed (toggle below)
        if (Input.GetKeyDown(KeyCode.R))
        {
            if (useRandomSeed) seed = Random.Range(0, int.MaxValue);
            Generate();
        }
    }

    // -------- Public entry point --------
    [ContextMenu("Generate")]
    public void Generate()
    {
        // Container cleanup
        if (container == null)
        {
            container = containerParent != null
                ? containerParent
                : new GameObject("GeneratedTerrain").transform;
            container.SetParent(transform, false);
        }
        else
        {
            for (int i = container.childCount - 1; i >= 0; --i)
                DestroyImmediate(container.GetChild(i).gameObject);
        }

        prng = new System.Random(seed);

        // Allocate
        height = new int[sizeX, sizeZ];
        isWater = new bool[sizeX, sizeZ];
        isPath = new bool[sizeX, sizeZ];

        // === Phase 1: Heights & Dips (Proprietary) ===
        GenerateHeights_Proprietary();

        // === Phase 2: Water Fill ===
        MarkWaterAndShorelines();

        // === Phase 3: Path (avoid water; carve tunnels) ===
        BuildPath();

        // === Instantiate Cubes ===
        RenderVoxels();
    }

    // =======================
    // PHASE 1: Heights & Dips
    // =======================
    // Proprietary approach:
    //   A) Build two 1D random walks (ridge lines): one along X for each Z, and one along Z for each X.
    //   B) Blend the two fields (averaging) to create a 2D height suggestion.
    //   C) Apply sparse "plateau" jitter to create flat patches.
    //   D) Clamp and smooth to make hills not jagged
    private void GenerateHeights_Proprietary()
    {
        int[,] h = new int[sizeX, sizeZ];

        // A) random walks along X for each Z
        int[,] walkX = new int[sizeX, sizeZ];
        for (int z = 0; z < sizeZ; z++)
        {
            int v = baseHeight + prng.Next(0, heightSpan);
            for (int x = 0; x < sizeX; x++)
            {
                // step: -maxStep..+maxStep
                v += prng.Next(-maxStepPerWalk, maxStepPerWalk + 1);
                walkX[x, z] = v;
            }
        }

        // B) random walks along Z for each X
        int[,] walkZ = new int[sizeX, sizeZ];
        for (int x = 0; x < sizeX; x++)
        {
            int v = baseHeight + prng.Next(0, heightSpan);
            for (int z = 0; z < sizeZ; z++)
            {
                v += prng.Next(-maxStepPerWalk, maxStepPerWalk + 1);
                walkZ[x, z] = v;
            }
        }

        // Blend
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                h[x, z] = (walkX[x, z] + walkZ[x, z]) / 2;
            }
        }

        // C) plateau jitter: flatten some cells to local avg before smoothing
        for (int x = 1; x < sizeX - 1; x++)
        {
            for (int z = 1; z < sizeZ - 1; z++)
            {
                if (prng.NextDouble() < plateauChance)
                {
                    int sum = 0; int c = 0;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            sum += h[x + dx, z + dz]; c++;
                        }
                    h[x, z] = sum / c; // quick plateau
                }
            }
        }

        // Clamp to [baseHeight .. baseHeight+heightSpan] and to maxHeight - 1
        int maxClamp = Mathf.Min(baseHeight + heightSpan, maxHeight - 1);
        for (int x = 0; x < sizeX; x++)
            for (int z = 0; z < sizeZ; z++)
                h[x, z] = Mathf.Clamp(h[x, z], baseHeight, maxClamp);

        // D) smoothing (box blur N passes)
        for (int pass = 0; pass < smoothPasses; pass++)
        {
            int[,] sm = new int[sizeX, sizeZ];
            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    int sum = 0, c = 0;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= sizeX) continue;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int nz = z + dz;
                            if (nz < 0 || nz >= sizeZ) continue;
                            sum += h[nx, nz]; c++;
                        }
                    }
                    sm[x, z] = sum / c;
                }
            }
            h = sm;
        }

        height = h;
    }

    // ==================
    // PHASE 2: Watering
    // ==================
    // If a tile is below the set threshold, replace it with water, the block just above the water line turns into sand for a beach like shore
    private void MarkWaterAndShorelines()
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                isWater[x, z] = height[x, z] < waterLevel;
            }
        }
    }

    // ==================
    // PHASE 3: Pathing
    // ==================
    // Goal: a path from (0, midZ) -> (sizeX-1, midZ) that avoids water,
    // climbs <= maxNaturalStep normally, and when >maxNaturalStep, we "carve"
    // a tunnel: we raise the traversable height at the target cell to current+1,
    // and also clear a 2–3 block clearance above it.
    private void BuildPath()
    {
        int startZ = sizeZ / 2;
        Vector2Int start = new Vector2Int(0, startZ);
        Vector2Int goal = new Vector2Int(sizeX - 1, startZ);

        // A*: open set with (pos, g, f, parent)
        var open = new List<Node>();
        var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        var gScore = new Dictionary<Vector2Int, int>();
        var fScore = new Dictionary<Vector2Int, int>();
        var closed = new HashSet<Vector2Int>();

        Node startN = new Node(start, 0, Heuristic(start, goal));
        open.Add(startN);
        gScore[start] = 0;
        fScore[start] = startN.f;

        // A* main loop
        while (open.Count > 0)
        {
            // pick lowest f
            int bestIdx = 0; int bestF = open[0].f;
            for (int i = 1; i < open.Count; i++)
                if (open[i].f < bestF) { bestF = open[i].f; bestIdx = i; }
            Node cur = open[bestIdx];
            open.RemoveAt(bestIdx);

            if (cur.pos == goal)
            {
                ReconstructPath(cameFrom, cur.pos);
                return;
            }

            if (!closed.Add(cur.pos)) continue;

            foreach (var nb in Neighbors4(cur.pos))
            {
                if (closed.Contains(nb)) continue;

                // Skip out of bounds or water
                if (!InBounds(nb.x, nb.y)) continue;
                if (isWater[nb.x, nb.y]) continue;

                int hCur = height[cur.pos.x, cur.pos.y];
                int hNb = height[nb.x, nb.y];
                int step = hNb - hCur;

                int moveCost = 1; // base step cost

                if (step > maxNaturalStep)
                {
                    // Carve a ramp/tunnel by raising our traversable height to hCur+1 (virtually),
                    // and ensure vertical clearance.
                    int carvedTo = hCur + 1;
                    // Physically modify the terrain at neighbor to enable traversal and tunnel space.
                    CarveTunnelAt(nb.x, nb.y, carvedTo, tunnelClearance);
                    hNb = height[nb.x, nb.y]; // update after carving
                    step = hNb - hCur;
                    // Slight penalty for carving
                    moveCost += 2;
                }
                else if (step > 0)
                {
                    moveCost += uphillPenalty * step;
                }

                // Allow stepping down any amount (like a drop)

                int tentativeG = gScore[cur.pos] + moveCost;
                int nbF;
                if (!gScore.TryGetValue(nb, out int oldG) || tentativeG < oldG)
                {
                    cameFrom[nb] = cur.pos;
                    gScore[nb] = tentativeG;
                    nbF = tentativeG + Heuristic(nb, goal);
                    fScore[nb] = nbF;

                    // push/add to open
                    bool exists = false;
                    for (int i = 0; i < open.Count; i++)
                    {
                        if (open[i].pos == nb) { open[i] = new Node(nb, tentativeG, nbF); exists = true; break; }
                    }
                    if (!exists) open.Add(new Node(nb, tentativeG, nbF));
                }
            }
        }

        // Fallback if path isn't found or generated
        Debug.LogWarning("Path search failed; carving fallback trench path.");
        FallbackTrenchPath(start, goal);
    }

    private int Heuristic(Vector2Int a, Vector2Int b) => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    private IEnumerable<Vector2Int> Neighbors4(Vector2Int p)
    {
        yield return new Vector2Int(p.x + 1, p.y);
        yield return new Vector2Int(p.x - 1, p.y);
        yield return new Vector2Int(p.x, p.y + 1);
        yield return new Vector2Int(p.x, p.y - 1);
    }

    private bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < sizeX && z < sizeZ;

    private void ReconstructPath(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int cur)
    {
        var list = new List<Vector2Int> { cur };
        while (cameFrom.TryGetValue(cur, out var prev))
        {
            list.Add(prev);
            cur = prev;
        }
        list.Reverse();
        pathCells = list.ToArray();

        // Stamp path on surface (top of terrain cell; must be at or above waterline)
        foreach (var p in pathCells)
        {
            if (isWater[p.x, p.y]) continue; // safety
            isPath[p.x, p.y] = true;
        }
    }

    private void FallbackTrenchPath(Vector2Int start, Vector2Int goal)
    {
        var list = new List<Vector2Int>();
        int z = start.y;
        for (int x = start.x; x <= goal.x; x++)
        {
            list.Add(new Vector2Int(x, z));
            // Ensure the terrain there is at least waterLevel
            if (height[x, z] < waterLevel)
            {
                CarveTunnelAt(x, z, waterLevel, tunnelClearance);
            }
        }
        pathCells = list.ToArray();
        foreach (var p in pathCells) isPath[p.x, p.y] = true;
    }

    private void CarveTunnelAt(int x, int z, int floorY, int clearance)
    {
        // Raise or lower terrain at (x,z) to 'floorY' for easy stepping,
        // and clear space up to floorY + (clearance-1)
        floorY = Mathf.Clamp(floorY, 1, maxHeight - clearance);
        height[x, z] = floorY;

        // If water was present there, it's not anymore
        isWater[x, z] = false;
        // This modifies solid blocks only; water fill happens during rendering anyway
    }

    // ==================
    // Rendering
    // ==================
    private void RenderVoxels()
    {
        // Build vertical stacks per (x,z):
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int top = height[x, z];
                top = Mathf.Clamp(top, 0, maxHeight - 1);

                // Ground column: some stone at the bottom, then dirt, top is either grass or path/sand
                int stoneDepth = Mathf.Max(0, top - 4); // last 4 blocks near surface are dirt
                for (int y = 0; y <= top; y++)
                {
                    GameObject prefab;
                    if (y < stoneDepth && stonePrefab != null) prefab = stonePrefab;
                    else prefab = dirtPrefab != null ? dirtPrefab : grassPrefab;

                    if (y == top)
                    {
                        // Surface block
                        if (isPath[x, z] && pathPrefab != null) prefab = pathPrefab;
                        else if (useShoreSand && top == waterLevel && sandPrefab != null) prefab = sandPrefab;
                        else if (grassPrefab != null) prefab = grassPrefab;
                    }

                    Spawn(prefab, x, y, z);
                }

                // Water fill up to waterLevel - 1 (if terrain below)
                if (isWater[x, z] && waterPrefab != null)
                {
                    for (int y = top + 1; y < waterLevel; y++)
                        Spawn(waterPrefab, x, y, z);
                }
            }
        }
    }

    private void Spawn(GameObject prefab, int x, int y, int z)
    {
        if (prefab == null) return;
        var go = Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity, container);
        go.name = $"{prefab.name}_{x}_{y}_{z}";
    }

    // -------- Debug gizmos --------
    private void OnDrawGizmosSelected()
    {
        if (!debugGizmos || pathCells == null) return;
        Gizmos.color = Color.yellow;
        foreach (var p in pathCells)
        {
            Gizmos.DrawWireCube(new Vector3(p.x, height != null ? height[p.x, p.y] + 0.5f : 0.5f, p.y), Vector3.one);
        }
    }

    // -------- Helpers --------
    private struct Node
    {
        public Vector2Int pos;
        public int g, f;
        public Node(Vector2Int p, int g, int f) { this.pos = p; this.g = g; this.f = f; }
    }
}
