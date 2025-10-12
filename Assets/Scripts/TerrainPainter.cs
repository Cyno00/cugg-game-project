using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem; // New Input System

/// Click & drag to sculpt Unity Terrain in real time (raise/lower/smooth).
/// Always restores the EXACT editor baseline at Play start and
/// gradually decays any deformations back to that baseline over time.
///
/// Left drag  : Raise (hold Shift to Lower)
/// Right drag : Smooth
[ExecuteAlways]
public class TerrainPainter_DecayToBaseline : MonoBehaviour
{
    public enum PaintMode { RaiseLower, Smooth }

    [Header("General")]
    public Camera sceneCamera;
    public LayerMask paintMask = ~0;

    [Header("Brush")]
    [Min(0.1f)] public float brushRadius = 4f;
    [Range(0.0f, 10f)] public float raiseStrength = 1.0f;
    [Range(0.0f, 10f)] public float smoothStrength = 2.0f;
    [Range(0f, 1f)] public float hardness = 0.3f;

    [Header("Stationary Tip Growth")]
    [Tooltip("When holding still, paint at the local height peak near the cursor instead of the cursor point.")]
    public bool followPeakWhenStationary = true;

    [Tooltip("Peak search radius (meters) relative to brush radius. 1 = same as brush.")]
    [Range(0.25f, 2f)] public float peakSearchRadiusMultiplier = 1.0f;

    [Tooltip("If the found peak is very flat, fall back to cursor point unless the peak exceeds this min delta (in meters).")]
    [Min(0f)] public float peakMinDeltaMeters = 0.02f;


    [Header("Stationary Hold Growth")]
    [Tooltip("If the cursor is not moving, still repaint this many times per second at the hit point.")]
    [Range(1, 120)] public int stationaryPaintHz = 30;

    [Tooltip("World-space distance under which we consider the cursor 'stationary'.")]
    [Min(0f)] public float stationaryThresholdMeters = 0.02f;


    [Header("Input")]
    public bool enableLeftPaint = true;
    public bool enableRightSmooth = true;

    [Header("Distance Limits")]
    public Transform playerTransform;
    [Min(0f)] public float minDistanceFromPlayer = 2.0f;
    [Min(0.5f)] public float maxClickDistance = 150f;

    [Header("Performance")]
    [Min(0.05f)] public float dragStep = 0.5f;
    public bool useDelayedLOD = true;

    [Header("Decay To Baseline")]
    [Tooltip("Seconds to wait after you finish a stroke before decay begins.")]
    [Min(0f)] public float decayDelaySeconds = 0.75f;
    [Tooltip("Seconds for a deformation to blend back to the baseline after delay.")]
    [Min(0.01f)] public float decayBlendSeconds = 2.5f;
    [Tooltip("If true, touching a pixel again during a new stroke restarts its decay timer.")]
    public bool restartDecayOnRepaint = true;

    // drag state
    private Vector3? lastWorldHit;
    private Terrain lastTerrain;
    private bool wasPaintingLastFrame;

    // per-stroke bounds we’ll hand to decayer
    private struct StrokeBounds { public Terrain t; public RectInt rect; }
    private List<RectInt> workingRects = new List<RectInt>();

    // Baseline for each terrain at PLAY START (editor-authored heights)
    private readonly Dictionary<Terrain, float[,]> baselineHeights = new Dictionary<Terrain, float[,]>(8);

    // active decays per terrain region to avoid spawning too many coroutines
    private readonly Dictionary<Terrain, List<ActiveDecay>> activeDecays = new Dictionary<Terrain, List<ActiveDecay>>(8);

    private float stationaryAccum;          // accumulator for stationary repaint timing
    private Vector3 lastStationaryPoint;    // last point considered for stationary painting


    private class ActiveDecay
    {
        public RectInt rect;
        public Coroutine routine;
        public float startTime; // for optional restart behavior
    }

    private void Reset()
    {
        if (sceneCamera == null) sceneCamera = Camera.main;
    }

    private void OnEnable()
    {
#if UNITY_EDITOR
        // When entering PLAY, capture and enforce baseline so we always start clean.
        if (Application.isPlaying)
        {
            CaptureBaselines();
            ResetAllTerrainsToBaseline();
        }
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        // When exiting PLAY, restore terrain back to the editor-authored baseline too.
        if (!Application.isPlaying)
        {
            ResetAllTerrainsToBaseline();
        }
#endif
    }

    private void Update()
    {
        if (sceneCamera == null) return;

        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null) return;

        bool leftHeld = enableLeftPaint && mouse.leftButton.isPressed;
        bool rightHeld = enableRightSmooth && mouse.rightButton.isPressed;
        bool paintingNow = leftHeld || rightHeld;

        // Stroke start
        if (paintingNow && !wasPaintingLastFrame)
        {
            lastWorldHit = null;
            lastTerrain = null;
            workingRects.Clear();
        }

        if (!paintingNow)
        {
            // Stroke end: kick off decay for all touched rects
            if (wasPaintingLastFrame && workingRects.Count > 0)
            {
                foreach (var rect in workingRects)
                    BeginDecay(rect, decayDelaySeconds, decayBlendSeconds);
            }

            ApplyDelayedIfNeeded();
            lastWorldHit = null; lastTerrain = null;
            wasPaintingLastFrame = false;
            return;
        }

        // Painting this frame
        Vector2 screen = mouse.position.ReadValue();
        Ray ray = sceneCamera.ScreenPointToRay(screen);

        if (!Physics.Raycast(ray, out RaycastHit hit, maxClickDistance, paintMask, QueryTriggerInteraction.Ignore))
        {
            wasPaintingLastFrame = paintingNow;
            return;
        }

        Terrain terrain = hit.collider.GetComponent<Terrain>();
        if (terrain == null)
        {
            wasPaintingLastFrame = paintingNow;
            return;
        }

        Vector3 currentHit = hit.point;

        // Interpolate along drag path and paint; accumulate rects we touched
        RectInt combined = new RectInt(0, 0, 0, 0);
        bool any = false;

        if (lastWorldHit.HasValue && lastTerrain == terrain)
        {
            Vector3 from = lastWorldHit.Value;
            float moveDist = Vector3.Distance(from, currentHit);

            if (moveDist >= stationaryThresholdMeters)
            {
                // Moving: sample along the path
                int steps = Mathf.Max(1, Mathf.CeilToInt(moveDist / Mathf.Max(0.01f, dragStep)));
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 p = Vector3.Lerp(from, currentHit, i / (float)steps);
                    if (IsTooCloseToPlayer(p)) continue;
                    RectInt r = PaintAtWorld(terrain, p, leftHeld, rightHeld, kb);
                    if (r.width > 0 && r.height > 0) { combined = any ? Encapsulate(combined, r) : r; any = true; }
                }
                // Reset stationary timer at new anchor
                stationaryAccum = 0f;
                Debug.Log("hold");
                lastStationaryPoint = currentHit;
            }
            else
            {
                // Stationary: repaint at a steady rate, but at the tip (local peak) instead of the cursor
                stationaryAccum += Mathf.Max(0.0001f, Application.isPlaying ? Time.deltaTime : 1f / 60f);
                float interval = 1f / Mathf.Max(1, stationaryPaintHz);

                while (stationaryAccum >= interval)
                {
                    stationaryAccum -= interval;

                    Vector3 paintPoint = currentHit; // default: cursor point
                    if (followPeakWhenStationary)
                    {
                        if (TryFindLocalPeakWorldPoint(terrain, currentHit, brushRadius * peakSearchRadiusMultiplier, out Vector3 peakWorld, out float peakDelta))
                        {
                            if (peakDelta >= peakMinDeltaMeters)
                                paintPoint = peakWorld; // paint at the tip
                        }
                    }

                    if (IsTooCloseToPlayer(paintPoint)) break;
                    RectInt r = PaintAtWorld(terrain, paintPoint, leftHeld, rightHeld, kb);
                    if (r.width > 0 && r.height > 0) { combined = any ? Encapsulate(combined, r) : r; any = true; }
                }
            }

        }
        else
        {
            // First sample of stroke on this terrain
            if (!IsTooCloseToPlayer(currentHit))
            {
                RectInt r = PaintAtWorld(terrain, currentHit, leftHeld, rightHeld, kb);
                if (r.width > 0 && r.height > 0) { combined = r; any = true; }
            }
            stationaryAccum = 0f;
            lastStationaryPoint = currentHit;
        }


        if (any)
        {
            workingRects.Add(combined);
            // If a region is being repainted, optionally restart its decay timer
            if (restartDecayOnRepaint) RestartDecayIfOverlapping(terrain, combined);
        }

        lastWorldHit = currentHit;
        lastTerrain = terrain;
        wasPaintingLastFrame = paintingNow;
    }

    private bool IsTooCloseToPlayer(Vector3 p)
    {
        if (playerTransform == null || minDistanceFromPlayer <= 0f) return false;
        Vector2 a = new Vector2(p.x, p.z);
        Vector2 b = new Vector2(playerTransform.position.x, playerTransform.position.z);
        return Vector2.Distance(a, b) < minDistanceFromPlayer;
    }

    // Returns the rect on the heightmap that was modified (for decay scheduling)
    private RectInt PaintAtWorld(Terrain terrain, Vector3 worldPos, bool leftHeld, bool rightHeld, Keyboard kb)
    {
        var data = terrain.terrainData;

        Vector3 local = worldPos - terrain.transform.position;
        int hmRes = data.heightmapResolution;
        float sizeX = data.size.x, sizeZ = data.size.z;

        float u = Mathf.Clamp01(local.x / sizeX);
        float v = Mathf.Clamp01(local.z / sizeZ);

        int cx = Mathf.RoundToInt(u * (hmRes - 1));
        int cy = Mathf.RoundToInt(v * (hmRes - 1));

        float mppX = sizeX / (hmRes - 1);
        float mppY = sizeZ / (hmRes - 1);
        float rPx = brushRadius / Mathf.Max(1e-5f, mppX);
        float rPy = brushRadius / Mathf.Max(1e-5f, mppY);

        int xMin = Mathf.Max(0, Mathf.FloorToInt(cx - rPx - 1));
        int xMax = Mathf.Min(hmRes - 1, Mathf.CeilToInt(cx + rPx + 1));
        int yMin = Mathf.Max(0, Mathf.FloorToInt(cy - rPy - 1));
        int yMax = Mathf.Min(hmRes - 1, Mathf.CeilToInt(cy + rPy + 1));

        int w = xMax - xMin + 1, h = yMax - yMin + 1;
        if (w <= 0 || h <= 0) return new RectInt(0, 0, 0, 0);

        float[,] heights = data.GetHeights(xMin, yMin, w, h);

        bool shiftLower = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
        PaintMode mode = rightHeld ? PaintMode.Smooth : PaintMode.RaiseLower;

        float dt = Mathf.Max(0.0001f, Application.isPlaying ? Time.deltaTime : 1f / 60f);
        float raiseDelta = raiseStrength * dt * (shiftLower ? -1f : 1f);
        float smoothAmt = Mathf.Clamp01(smoothStrength * dt);

        float ccx = cx - xMin, ccy = cy - yMin;
        int kernel = 5;

        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                float dx = (i - ccx) / Mathf.Max(1e-5f, rPx);
                float dy = (j - ccy) / Mathf.Max(1e-5f, rPy);
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                if (r > 1f) continue;

                float t = 1f - Mathf.Clamp01(r);
                float a = Mathf.Lerp(0.75f, 4f, hardness);
                float falloff = Mathf.Pow(t, a) * (3f - 2f * t);

                if (mode == PaintMode.RaiseLower)
                {
                    heights[j, i] = Mathf.Clamp01(heights[j, i] + (raiseDelta * falloff) / data.size.y * 10f);
                }
                else
                {
                    float sum = 0f, wsum = 0f;
                    for (int ky = -kernel; ky <= kernel; ky++)
                    {
                        int yy = Mathf.Clamp(j + ky, 0, h - 1);
                        for (int kx = -kernel; kx <= kernel; kx++)
                        {
                            int xx = Mathf.Clamp(i + kx, 0, w - 1);
                            float kdx = kx / (float)kernel, kdy = ky / (float)kernel;
                            float kr = Mathf.Clamp01(1f - Mathf.Sqrt(kdx * kdx + kdy * kdy));
                            float wt = kr * kr;
                            sum += heights[yy, xx] * wt;
                            wsum += wt;
                        }
                    }
                    float avg = (wsum > 1e-5f) ? sum / wsum : heights[j, i];
                    heights[j, i] = Mathf.Lerp(heights[j, i], avg, smoothAmt * falloff);
                }
            }
        }

        // Write back
        ApplyHeights(terrain, xMin, yMin, heights);
        return new RectInt(xMin, yMin, w, h);
    }

    // ----- Decay management -----

    private void BeginDecay(RectInt rect, float delay, float blendSeconds)
    {
        if (!Application.isPlaying) return; // Only decay during play

        // Terrain known from lastTerrain in this frame (same terrain used to compute rect)
        Terrain t = lastTerrain;
        if (t == null || !baselineHeights.ContainsKey(t)) return;

        if (!activeDecays.TryGetValue(t, out var list))
        {
            list = new List<ActiveDecay>(8);
            activeDecays[t] = list;
        }

        // If overlapping an existing decay, optionally restart it
        for (int i = 0; i < list.Count; i++)
        {
            if (Overlaps(list[i].rect, rect))
            {
                if (restartDecayOnRepaint && list[i].routine != null)
                {
                    StopCoroutine(list[i].routine);
                    list[i] = new ActiveDecay
                    {
                        rect = Encapsulate(list[i].rect, rect),
                        routine = StartCoroutine(DecayRectToBaseline(t, Encapsulate(list[i].rect, rect), delay, blendSeconds)),
                        startTime = Time.time
                    };
                }
                else
                {
                    // widen the region if necessary
                    list[i].rect = Encapsulate(list[i].rect, rect);
                }
                return;
            }
        }

        // Start a new decay for this rect
        var ad = new ActiveDecay
        {
            rect = rect,
            routine = StartCoroutine(DecayRectToBaseline(t, rect, delay, blendSeconds)),
            startTime = Time.time
        };
        list.Add(ad);
    }

    private IEnumerator DecayRectToBaseline(Terrain t, RectInt rect, float delay, float blendSeconds)
    {
        var data = t.terrainData;
        var baseHeights = baselineHeights[t];

        // clamp rect to heightmap
        rect.x = Mathf.Clamp(rect.x, 0, data.heightmapResolution - 1);
        rect.y = Mathf.Clamp(rect.y, 0, data.heightmapResolution - 1);
        rect.width = Mathf.Clamp(rect.width, 0, data.heightmapResolution - rect.x);
        rect.height = Mathf.Clamp(rect.height, 0, data.heightmapResolution - rect.y);
        if (rect.width <= 0 || rect.height <= 0) yield break;

        if (delay > 0f) yield return new WaitForSeconds(delay);

        // Pull current heights and baseline window
        float[,] cur = data.GetHeights(rect.x, rect.y, rect.width, rect.height);
        float[,] tgt = new float[rect.height, rect.width];
        for (int j = 0; j < rect.height; j++)
            for (int i = 0; i < rect.width; i++)
                tgt[j, i] = baseHeights[rect.y + j, rect.x + i];

        // Blend back
        float tNorm = 0f;
        while (tNorm < 1f)
        {
            tNorm += Mathf.Clamp01(Time.deltaTime / Mathf.Max(0.01f, blendSeconds));
            float[,] blend = new float[rect.height, rect.width];
            for (int j = 0; j < rect.height; j++)
                for (int i = 0; i < rect.width; i++)
                    blend[j, i] = Mathf.Lerp(cur[j, i], tgt[j, i], tNorm);

            ApplyHeights(t, rect.x, rect.y, blend);
            yield return null;
        }
    }

    // ----- Baseline capture & reset -----

    private void CaptureBaselines()
    {
        baselineHeights.Clear();
        foreach (var t in Terrain.activeTerrains)
        {
            var d = t.terrainData;
            baselineHeights[t] = d.GetHeights(0, 0, d.heightmapResolution, d.heightmapResolution);
        }
    }

    private void ResetAllTerrainsToBaseline()
    {
        foreach (var kv in baselineHeights)
        {
            Terrain t = kv.Key;
            var d = t != null ? t.terrainData : null;
            if (t == null || d == null) continue;

            float[,] baseH = kv.Value;
            // If dimensions changed, skip to be safe
            if (baseH.GetLength(0) != d.heightmapResolution || baseH.GetLength(1) != d.heightmapResolution) continue;

            ApplyHeights(t, 0, 0, baseH);
        }
    }

    // ----- Utilities -----

    private static bool Overlaps(RectInt a, RectInt b)
    {
        return a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin;
    }

    private static RectInt Encapsulate(RectInt a, RectInt b)
    {
        if (a.width == 0 || a.height == 0) return b;
        int xMin = Mathf.Min(a.xMin, b.xMin);
        int yMin = Mathf.Min(a.yMin, b.yMin);
        int xMax = Mathf.Max(a.xMax, b.xMax);
        int yMax = Mathf.Max(a.yMax, b.yMax);
        return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
    }

    private void ApplyHeights(Terrain t, int x, int y, float[,] h)
    {
#if UNITY_2021_2_OR_NEWER
        if (useDelayedLOD)
        {
            t.terrainData.SetHeightsDelayLOD(x, y, h);
            t.ApplyDelayedHeightmapModification();
        }
        else
        {
            t.terrainData.SetHeights(x, y, h);
        }
#else
        t.terrainData.SetHeights(x, y, h);
#if UNITY_2019_3_OR_NEWER
        t.terrainData.SyncHeightmap();
#endif
#endif
    }

    private void ApplyDelayedIfNeeded()
    {
#if UNITY_2021_2_OR_NEWER
        if (useDelayedLOD && lastTerrain != null)
            lastTerrain.ApplyDelayedHeightmapModification();
#else
        if (lastTerrain != null)
        {
#if UNITY_2019_3_OR_NEWER
            lastTerrain.terrainData.SyncHeightmap();
#endif
        }
#endif
    }

    // Restart or widen any active decay that overlaps 'rect' on this terrain.
    // If restartDecayOnRepaint == true, we fully restart the timer & blend for the union region.
    // If false, we just widen the tracked rect (existing coroutine continues).
    private void RestartDecayIfOverlapping(Terrain terrain, RectInt rect)
    {
        if (!Application.isPlaying || terrain == null) return;

        if (!activeDecays.TryGetValue(terrain, out var list) || list == null) return;

        for (int i = 0; i < list.Count; i++)
        {
            var ad = list[i];
            if (!Overlaps(ad.rect, rect)) continue;

            RectInt union = Encapsulate(ad.rect, rect);

            if (restartDecayOnRepaint)
            {
                // Stop the old decay and start a fresh one for the union area
                if (ad.routine != null) StopCoroutine(ad.routine);
                ad.rect = union;
                ad.routine = StartCoroutine(DecayRectToBaseline(
                    terrain, union, decayDelaySeconds, decayBlendSeconds));
                ad.startTime = Time.time;
                list[i] = ad; // write back
            }
            else
            {
                // Keep existing coroutine; just track the union region (note: the current
                // coroutine’s cached arrays won’t include the new area until a restart)
                ad.rect = union;
                list[i] = ad;
            }
        }
    }

    // Finds the highest point (in world space) within a search radius on the terrain around a world position.
    // Returns true and sets peakWorld if a meaningful peak is found. peakDelta = (peakHeight - centerHeight) in meters.
    private bool TryFindLocalPeakWorldPoint(Terrain terrain, Vector3 aroundWorld, float searchRadiusMeters, out Vector3 peakWorld, out float peakDeltaMeters)
    {
        peakWorld = aroundWorld;
        peakDeltaMeters = 0f;

        var data = terrain.terrainData;
        Vector3 local = aroundWorld - terrain.transform.position;

        int hmRes = data.heightmapResolution;
        float sizeX = data.size.x, sizeZ = data.size.z, sizeY = data.size.y;

        // Center indices on heightmap
        float u = Mathf.Clamp01(local.x / sizeX);
        float v = Mathf.Clamp01(local.z / sizeZ);
        int cx = Mathf.RoundToInt(u * (hmRes - 1));
        int cy = Mathf.RoundToInt(v * (hmRes - 1));

        // Radius in pixels
        float mppX = sizeX / (hmRes - 1);
        float mppY = sizeZ / (hmRes - 1);
        float rPx = Mathf.Max(1f, searchRadiusMeters / Mathf.Max(1e-5f, mppX));
        float rPy = Mathf.Max(1f, searchRadiusMeters / Mathf.Max(1e-5f, mppY));

        int xMin = Mathf.Max(0, Mathf.FloorToInt(cx - rPx));
        int xMax = Mathf.Min(hmRes - 1, Mathf.CeilToInt(cx + rPx));
        int yMin = Mathf.Max(0, Mathf.FloorToInt(cy - rPy));
        int yMax = Mathf.Min(hmRes - 1, Mathf.CeilToInt(cy + rPy));

        int w = xMax - xMin + 1, h = yMax - yMin + 1;
        if (w <= 1 || h <= 1) return false;

        float[,] heights = data.GetHeights(xMin, yMin, w, h);

        // Center height (for delta check)
        int lcX = Mathf.Clamp(cx - xMin, 0, w - 1);
        int lcY = Mathf.Clamp(cy - yMin, 0, h - 1);
        float centerHNorm = heights[lcY, lcX];

        float bestH = -1f;
        int bestI = lcX, bestJ = lcY;

        // Search within ellipse radius; prefer the highest height
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                // Elliptical cutoff to keep it local
                float dx = (i - lcX) / Mathf.Max(1f, rPx);
                float dy = (j - lcY) / Mathf.Max(1f, rPy);
                if (dx * dx + dy * dy > 1f) continue;

                float hNorm = heights[j, i];
                if (hNorm > bestH)
                {
                    bestH = hNorm; bestI = i; bestJ = j;
                }
            }
        }

        if (bestH < 0f) return false;

        // Convert peak indices back to world position
        int gX = xMin + bestI, gY = yMin + bestJ;
        float peakX = (gX / (float)(hmRes - 1)) * sizeX;
        float peakZ = (gY / (float)(hmRes - 1)) * sizeZ;
        float peakY = bestH * sizeY;

        peakWorld = terrain.transform.position + new Vector3(peakX, peakY, peakZ);
        peakDeltaMeters = (bestH - centerHNorm) * sizeY;
        return true;
    }


}
