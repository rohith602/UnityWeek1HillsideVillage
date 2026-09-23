using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Week1.EditorTools
{
    /// <summary>
    /// Builds the "Hillside Village Overlook" scene from scratch, deterministically.
    /// Run from the Tools menu, or headlessly via -executeMethod.
    /// </summary>
    public static class SceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/HillsideVillage.unity";
        const string TerrainAssetPath = "Assets/_Project/Terrain/HillsideTerrain.asset";
        const string LayerDir = "Assets/_Project/Terrain/Layers";
        const string TextureDir = "Assets/_Project/Art/Textures/Terrain";
        const string MaterialDir = "Assets/_Project/Art/Materials";
        const string ShotDir = "Docs/screenshots";

        const int Seed = 20260923;
        const int HeightRes = 513;
        const int AlphaRes = 512;
        const float MapSize = 400f;
        const float MapHeight = 140f;

        // Normalised (0-1) landmarks on the terrain.
        static readonly Vector2 PlateauCentre = new Vector2(0.44f, 0.44f);
        const float PlateauRadius = 0.14f;
        const float PlateauHeight = 0.17f;

        static readonly Vector2 LakeCentre = new Vector2(0.26f, 0.28f);
        const float LakeRadius = 0.16f;
        const float LakeFloor = 0.045f;
        const float WaterLevel = 0.082f * MapHeight;

        static Terrain _terrain;
        static TerrainData _td;
        static readonly List<Vector3> RoadPts = new List<Vector3>();
        static System.Random _rng;

        // Every camera the build renders from. Scatter keeps clear of these, otherwise a
        // randomly placed boulder lands a metre from the lens and fills half the frame.
        static readonly Vector3[] CameraStations =
        {
            new Vector3(64f, 112f, 52f),
            new Vector3(122f, 40f, 112f),
            new Vector3(206f, 44f, 56f),
        };
        const float CameraKeepout = 34f;

        [MenuItem("Tools/Week 1/Build Hillside Village Scene")]
        public static void Build()
        {
            try
            {
                _rng = new System.Random(Seed);
                Random.InitState(Seed);
                RoadPts.Clear();
                for (int i = 0; i <= 64; i++) RoadPts.Add(RoadPoint(i / 64f));

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                BuildTerrain();
                BuildLighting();
                BuildWater();
                var village = BuildVillage();
                BuildCampsite();
                BuildVehicles();
                ScatterNature(village);
                var cam = BuildCamera();

                Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                CaptureShots(cam);

                Debug.Log($"SCENEBUILD_SUCCESS objects={Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length}");
            }
            catch (System.Exception e)
            {
                Debug.LogError("SCENEBUILD_FAILED " + e);
            }
        }

        // ---------------------------------------------------------------- terrain

        static void BuildTerrain()
        {
            _td = new TerrainData
            {
                heightmapResolution = HeightRes,
                alphamapResolution = AlphaRes,
                baseMapResolution = 1024,
                size = new Vector3(MapSize, MapHeight, MapSize)
            };

            var h = new float[HeightRes, HeightRes];
            for (int z = 0; z < HeightRes; z++)
                for (int x = 0; x < HeightRes; x++)
                    h[z, x] = HeightAt(x / (HeightRes - 1f), z / (HeightRes - 1f));
            _td.SetHeights(0, 0, h);

            // Order matters: the TerrainData must exist as an asset, with its layers assigned,
            // before the splatmap is written. Painting into a TerrainData that is still purely
            // in-memory computes correct weights that are then lost when CreateAsset serialises
            // it - the terrain silently renders as 100% of the first layer.
            Directory.CreateDirectory(Path.GetDirectoryName(TerrainAssetPath));
            AssetDatabase.CreateAsset(_td, TerrainAssetPath);

            _td.terrainLayers = BuildLayers();
            PaintTerrain();
            EditorUtility.SetDirty(_td);
            AssetDatabase.SaveAssets();

            var go = Terrain.CreateTerrainGameObject(_td);
            go.name = "Terrain_Hillside";
            go.transform.position = Vector3.zero;
            _terrain = go.GetComponent<Terrain>();
            _terrain.heightmapPixelError = 3f;
            // NOTE: do not null materialTemplate - CreateTerrainGameObject already assigns
            // Default-Terrain-Standard, and clearing it renders the terrain magenta.
            _terrain.drawInstanced = true;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic);
        }

        static float HeightAt(float u, float v)
        {
            // Rolling valley floor.
            float h = 0.11f + 0.055f * Fbm(u * 2.6f, v * 2.6f, 4);

            // Warp the coordinates used for the large landforms. Without this the rim and
            // the ridge are both straight-line functions of (u,v) and read as geometric
            // terraces from above; the warp makes their contours wander like real ground.
            float wu = u + (Fbm(u * 1.8f + 5f, v * 1.8f + 9f, 3) - 0.5f) * 0.20f;
            float wv = v + (Fbm(u * 1.8f + 19f, v * 1.8f + 3f, 3) - 0.5f) * 0.20f;

            // Lift every edge into a surrounding rim so the terrain boundary is never visible
            // from inside the valley. Blending the square (Chebyshev) and round (radial)
            // distances keeps all four edges covered without a square-looking wall.
            float ex = Mathf.Abs(wu - 0.5f) * 2f, ey = Mathf.Abs(wv - 0.5f) * 2f;
            float blended = Mathf.Lerp(Mathf.Sqrt(ex * ex + ey * ey) / 1.4142f, Mathf.Max(ex, ey), 0.55f);
            float rim = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.46f, 1.02f, blended));

            // Main mountain mass, heaviest toward the north and east. Combined with a soft
            // union rather than Mathf.Max, which would leave a visible crease where the two meet.
            float ne = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.40f, 1.02f, wu * 0.35f + wv * 0.75f));
            float mountain = 1f - (1f - rim) * (1f - ne * 0.90f);
            h += mountain * (0.48f * Fbm(u * 3.4f + 11f, v * 3.4f + 7f, 5) + 0.30f);

            // Lake basin carved into the south-west.
            float ld = Vector2.Distance(new Vector2(u, v), LakeCentre) / LakeRadius;
            h = Mathf.Lerp(LakeFloor, h, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ld)));

            // Flatten the village plateau.
            float pd = Vector2.Distance(new Vector2(u, v), PlateauCentre) / PlateauRadius;
            h = Mathf.Lerp(PlateauHeight, h, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(pd)));

            // Ease the road corridor flat so buildings and vehicles sit level.
            float rd = DistToRoad(new Vector3(u * MapSize, 0f, v * MapSize));
            // Blend back to natural ground over a long run, so the road eases into the
            // hillside instead of carving a hard-edged trench beside it.
            if (rd < 34f)
                h = Mathf.Lerp(PlateauHeight, h, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(6f, 34f, rd)));

            return Mathf.Clamp01(h);
        }

        static TerrainLayer[] BuildLayers()
        {
            Directory.CreateDirectory(LayerDir);
            var names = new[] { "GrassMeadow", "GrassDry", "DirtPath", "RockCliff" };
            var sizes = new[] { 18f, 24f, 12f, 26f };
            var layers = new TerrainLayer[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/T_{names[i]}.png");
                if (tex == null) Debug.LogError($"Missing terrain texture T_{names[i]}.png");

                var layer = new TerrainLayer
                {
                    diffuseTexture = tex,
                    tileSize = new Vector2(sizes[i], sizes[i]),
                    specular = Color.black,
                    metallic = 0f,
                    smoothness = 0f
                };
                var path = $"{LayerDir}/TL_{names[i]}.terrainlayer";
                AssetDatabase.CreateAsset(layer, path);
                layers[i] = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            }
            return layers;
        }

        static void PaintTerrain()
        {
            var a = new float[AlphaRes, AlphaRes, 4];
            for (int z = 0; z < AlphaRes; z++)
            {
                float v = z / (AlphaRes - 1f);
                for (int x = 0; x < AlphaRes; x++)
                {
                    float u = x / (AlphaRes - 1f);
                    float steep = _td.GetSteepness(u, v);
                    float world = _td.GetInterpolatedHeight(u, v);
                    float grain = Fbm(u * 9f + 31f, v * 9f + 17f, 3);

                    float alt = world / MapHeight;
                    float altT = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.26f, 0.60f, alt));
                    float steepT = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 40f, steep));

                    // Meadow retreats as ground gets high or steep, so rock is not fighting it.
                    float meadow = Mathf.Lerp(1f, 0.04f, altT) * Mathf.Lerp(1f, 0.10f, steepT);
                    float dry = altT * (1f - steepT * 0.7f) * (0.7f + 0.8f * grain);
                    float rock = steepT * 1.45f
                               + 0.75f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.50f, 0.74f, alt));

                    // Dirt along the road corridor and around the lake shore.
                    float rd = DistToRoad(new Vector3(u * MapSize, 0f, v * MapSize));
                    float dirt = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(9f, 3.5f, rd)) * 3.0f;
                    float shore = Mathf.Abs(world - WaterLevel);
                    dirt += Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(5f, 0.5f, shore)) * 1.5f;
                    dirt *= 0.8f + 0.4f * grain;

                    float sum = meadow + dry + rock + dirt;
                    a[z, x, 0] = meadow / sum;
                    a[z, x, 1] = dry / sum;
                    a[z, x, 2] = dirt / sum;
                    a[z, x, 3] = rock / sum;
                }
            }
            _td.SetAlphamaps(0, 0, a);

            // Report the actual per-layer coverage: a splatmap that silently collapses to a
            // single layer looks identical to a working one in a fogged screenshot.
            var avg = new float[4];
            for (int z = 0; z < AlphaRes; z++)
                for (int x = 0; x < AlphaRes; x++)
                    for (int l = 0; l < 4; l++) avg[l] += a[z, x, l];
            float cells = AlphaRes * AlphaRes;
            Debug.Log($"SPLAT meadow={avg[0] / cells:P1} dry={avg[1] / cells:P1} " +
                      $"dirt={avg[2] / cells:P1} rock={avg[3] / cells:P1}");

            // Read back from the asset: the steepest ground must come out rock-dominant.
            var back = _td.GetAlphamaps(0, 0, AlphaRes, AlphaRes);
            int sx = 0, sz = 0; float worst = 0f;
            for (int z = 0; z < AlphaRes; z += 4)
                for (int x = 0; x < AlphaRes; x += 4)
                {
                    float st = _td.GetSteepness(x / (float)AlphaRes, z / (float)AlphaRes);
                    if (st > worst) { worst = st; sx = x; sz = z; }
                }
            Debug.Log($"SPLAT_VERIFY steepest={worst:F1}deg rockWeight={back[sz, sx, 3]:F2} " +
                      (back[sz, sx, 3] > 0.5f ? "OK" : "FAILED - splatmap did not persist"));
        }

        // ---------------------------------------------------------------- lighting

        static void BuildLighting()
        {
            Directory.CreateDirectory(MaterialDir);
            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunSize", 0.028f);
            sky.SetFloat("_SunSizeConvergence", 5f);
            sky.SetFloat("_AtmosphereThickness", 0.82f);
            sky.SetColor("_SkyTint", new Color(0.46f, 0.60f, 0.84f));
            sky.SetColor("_GroundColor", new Color(0.70f, 0.77f, 0.83f));
            sky.SetFloat("_Exposure", 1.15f);
            AssetDatabase.CreateAsset(sky, $"{MaterialDir}/M_Sky_Afternoon.mat");

            var sunGo = new GameObject("Sun_Directional");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.957f, 0.876f);
            sun.intensity = 1.0f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.72f;
            sun.shadowBias = 0.05f;
            sun.shadowNormalBias = 0.45f;
            // Low-ish afternoon sun: long shadows read the terrain relief.
            sunGo.transform.rotation = Quaternion.Euler(34f, 138f, 0f);

            RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialDir}/M_Sky_Afternoon.mat");
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.66f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.48f, 0.47f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.21f);
            RenderSettings.ambientIntensity = 0.85f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.72f, 0.80f, 0.87f);
            RenderSettings.fogStartDistance = 150f;
            RenderSettings.fogEndDistance = 600f;

            Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;
        }

        static void BuildWater()
        {
            var mat = new Material(Shader.Find("Standard"));
            mat.SetFloat("_Mode", 3f); // Transparent
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
            mat.color = new Color(0.22f, 0.50f, 0.62f, 0.78f);
            mat.SetFloat("_Glossiness", 0.88f);
            mat.SetFloat("_Metallic", 0.15f);
            AssetDatabase.CreateAsset(mat, $"{MaterialDir}/M_Water.mat");

            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water_Lake";
            water.transform.position = new Vector3(LakeCentre.x * MapSize, WaterLevel, LakeCentre.y * MapSize);
            water.transform.localScale = new Vector3(18f, 1f, 18f);
            water.GetComponent<MeshRenderer>().sharedMaterial =
                AssetDatabase.LoadAssetAtPath<Material>($"{MaterialDir}/M_Water.mat");
            Object.DestroyImmediate(water.GetComponent<MeshCollider>());
            GameObjectUtility.SetStaticEditorFlags(water, StaticEditorFlags.BatchingStatic);
        }

        // ---------------------------------------------------------------- content

        static Vector3 RoadPoint(float t)
        {
            float x = Mathf.Lerp(0.18f, 0.74f, t) * MapSize;
            float z = (0.44f + 0.10f * Mathf.Sin(t * Mathf.PI * 1.25f - 0.55f)) * MapSize;
            return new Vector3(x, 0f, z);
        }

        static float DistToRoad(Vector3 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < RoadPts.Count; i++)
            {
                float dx = RoadPts[i].x - p.x, dz = RoadPts[i].z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        // Kenney authors each kit at its own arbitrary scale (a house is 1.3 units wide, a
        // sedan 2.55 units long). Proportions are right *within* a kit but not between kits,
        // so each kit gets one factor derived from a reference model's real-world target size.
        static readonly Dictionary<string, (string model, float targetY, float meters)> KitRef =
            new Dictionary<string, (string, float, float)>
            {
                ["Kenney_CityKitSuburban"] = ("building-type-a", 1f, 6.5f),
                ["Kenney_NatureKit"]       = ("tree_pineTallA",  1f, 13.0f),
                ["Kenney_SurvivalKit"]     = ("tent",            1f, 2.6f),
                // Kenney cars are stubby (2.55 long x 1.30 tall vs a real 4.5 x 1.5), so this
                // targets height a little tall to land the *length* near 4.3 m, which is what reads.
                ["Kenney_CarKit"]          = ("sedan",           1f, 2.2f),
            };

        static readonly Dictionary<string, float> _kitScale = new Dictionary<string, float>();

        static float KitScale(string kit)
        {
            if (_kitScale.TryGetValue(kit, out float s)) return s;
            var (model, _, meters) = KitRef[kit];
            var src = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/ThirdParty/{kit}/Models/{model}.fbx");
            float h = SourceBounds(src).size.y;
            s = (h > 0.0001f) ? meters / h : 1f;
            _kitScale[kit] = s;
            Debug.Log($"KITSCALE {kit} ref={model} nativeY={h:F3} -> x{s:F2}");
            return s;
        }

        static readonly Dictionary<string, Bounds> _boundsCache = new Dictionary<string, Bounds>();

        static Bounds SourceBounds(GameObject src)
        {
            if (src == null) return new Bounds(Vector3.zero, Vector3.one);
            if (_boundsCache.TryGetValue(src.name, out var cached)) return cached;

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(src);
            inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            inst.transform.localScale = Vector3.one;
            var rends = inst.GetComponentsInChildren<Renderer>();
            var b = rends.Length > 0 ? rends[0].bounds : new Bounds(Vector3.zero, Vector3.one);
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Object.DestroyImmediate(inst);

            _boundsCache[src.name] = b;
            return b;
        }

        /// <param name="scale">Multiplier applied on top of the kit's own calibrated scale.</param>
        static GameObject Place(string kit, string model, Vector3 pos, float yaw, float scale, Transform parent)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/ThirdParty/{kit}/Models/{model}.fbx");
            if (src == null) { Debug.LogWarning($"Missing model {kit}/{model}"); return null; }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(src, parent);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * (scale * KitScale(kit));
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic |
                StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            return go;
        }

        static float Ground(float x, float z) => _terrain.SampleHeight(new Vector3(x, 0f, z));

        static List<Vector3> BuildVillage()
        {
            var root = new GameObject("Village").transform;
            var houses = new[]
            {
                "building-type-a","building-type-b","building-type-c","building-type-e","building-type-g",
                "building-type-h","building-type-j","building-type-l","building-type-n","building-type-q"
            };
            var occupied = new List<Vector3>();

            int n = 11;
            for (int i = 0; i < n; i++)
            {
                float t = (i + 0.5f) / n;
                Vector3 c = RoadPoint(t);
                Vector3 fwd = (RoadPoint(Mathf.Min(1f, t + 0.02f)) - RoadPoint(Mathf.Max(0f, t - 0.02f))).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;

                int sign = (i % 2 == 0) ? 1 : -1;
                float offset = 19f + (float)_rng.NextDouble() * 5f;
                Vector3 p = c + side * offset * sign;
                p.y = Ground(p.x, p.z) - 0.08f;

                // Face the road.
                float yaw = Quaternion.LookRotation(-side * sign, Vector3.up).eulerAngles.y;
                Place("Kenney_CityKitSuburban", houses[i % houses.Length], p, yaw, 1f, root);
                occupied.Add(p);

                // A driveway connecting the house back toward the road.
                Vector3 d = c + side * (offset - 9.5f) * sign;
                d.y = Ground(d.x, d.z) + 0.03f;
                Place("Kenney_CityKitSuburban", "driveway-long", d, yaw, 1f, root);

                if (i % 3 == 0)
                {
                    Vector3 f = p + side * 7f * sign + fwd * 6f;
                    f.y = Ground(f.x, f.z);
                    Place("Kenney_CityKitSuburban", "fence-low", f, yaw + 90f, 1f, root);
                }
                if (i % 4 == 1)
                {
                    Vector3 pl = p + fwd * 5.5f;
                    pl.y = Ground(pl.x, pl.z);
                    Place("Kenney_CityKitSuburban", "planter", pl, yaw, 1f, root);
                }
            }

            // Stone path accents along the road centre.
            for (int i = 0; i < 16; i++)
            {
                float t = (i + 0.5f) / 16f;
                Vector3 c = RoadPoint(t);
                c.y = Ground(c.x, c.z) + 0.04f;
                Vector3 fwd = (RoadPoint(Mathf.Min(1f, t + 0.02f)) - RoadPoint(Mathf.Max(0f, t - 0.02f))).normalized;
                Place("Kenney_CityKitSuburban", "path-stones-long", c,
                      Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y, 1f, root);
            }
            return occupied;
        }

        static void BuildCampsite()
        {
            var root = new GameObject("Campsite").transform;
            // A clearing on the slope between the village and the lake.
            Vector3 c = new Vector3(0.295f * MapSize, 0f, 0.345f * MapSize);
            c.y = Ground(c.x, c.z);

            Place("Kenney_SurvivalKit", "tent", c + new Vector3(-4.2f, 0f, 1.6f), 34f, 1f, root);
            Place("Kenney_SurvivalKit", "tent-canvas", c + new Vector3(4.6f, 0f, -2.4f), -58f, 1f, root);
            Place("Kenney_SurvivalKit", "campfire-stand", c, 0f, 1f, root);
            Place("Kenney_SurvivalKit", "bedroll", c + new Vector3(-1.4f, 0f, -3.6f), 12f, 1f, root);
            Place("Kenney_SurvivalKit", "barrel", c + new Vector3(3.1f, 0f, 3.4f), 0f, 1f, root);
            Place("Kenney_SurvivalKit", "box", c + new Vector3(4.2f, 0f, 4.3f), 25f, 1f, root);
            Place("Kenney_SurvivalKit", "chest", c + new Vector3(-5.6f, 0f, -1.2f), -20f, 1f, root);
            Place("Kenney_SurvivalKit", "resource-planks", c + new Vector3(1.9f, 0f, 5.1f), 48f, 1f, root);
            Place("Kenney_SurvivalKit", "signpost", c + new Vector3(7.4f, 0f, 0.8f), 96f, 1f, root);

            foreach (Transform t in root) // settle each prop onto the ground
            {
                var p = t.position; p.y = Ground(p.x, p.z); t.position = p;
            }

            Place("Kenney_NatureKit", "campfire_logs", c + new Vector3(-0.1f, 0.02f, 0.1f), 0f, 0.18f, root);
        }

        static void BuildVehicles()
        {
            var root = new GameObject("Vehicles").transform;
            var cars = new[] { "sedan", "suv", "hatchback-sports", "van", "truck" };
            float[] ts = { 0.17f, 0.38f, 0.56f, 0.74f, 0.90f };

            for (int i = 0; i < ts.Length; i++)
            {
                Vector3 c = RoadPoint(ts[i]);
                Vector3 fwd = (RoadPoint(Mathf.Min(1f, ts[i] + 0.02f)) - RoadPoint(Mathf.Max(0f, ts[i] - 0.02f))).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 p = c + side * ((i % 2 == 0) ? 7.5f : -7.5f);
                p.y = Ground(p.x, p.z);
                float yaw = Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y + (i % 2 == 0 ? 0f : 180f);
                Place("Kenney_CarKit", cars[i], p, yaw, 1f, root);
            }

            var tp = RoadPoint(0.62f) + new Vector3(24f, 0f, 20f);
            tp.y = Ground(tp.x, tp.z);
            Place("Kenney_CarKit", "tractor", tp, 212f, 1f, root);
        }

        static void ScatterNature(List<Vector3> houses)
        {
            var forest = new GameObject("Nature_Trees").transform;
            var rocks = new GameObject("Nature_Rocks").transform;
            var detail = new GameObject("Nature_Detail").transform;

            var pines = new[] { "tree_pineTallA", "tree_pineTallB", "tree_pineTallC", "tree_pineTallD",
                                "tree_pineRoundA", "tree_pineRoundB", "tree_pineRoundC",
                                "tree_pineSmallA", "tree_pineSmallB", "tree_pineGroundA" };
            var broad = new[] { "tree_default", "tree_oak", "tree_tall", "tree_thin", "tree_detailed",
                                "tree_default_fall", "tree_oak_fall", "tree_blocks_fall" };
            var rockSet = new[] { "rock_largeA", "rock_largeB", "rock_largeC", "rock_smallA", "rock_smallB",
                                  "rock_smallC", "rock_tallA", "rock_tallB", "stone_largeA", "stone_smallA", "stone_smallB" };
            var detailSet = new[] { "grass", "grass_large", "grass_leafs", "plant_bush", "plant_bushDetailed",
                                    "plant_bushSmall", "flower_redA", "flower_yellowA", "flower_purpleA",
                                    "mushroom_red", "mushroom_tan", "log", "log_stack" };

            int placedTree = 0, placedRock = 0, placedDetail = 0;

            for (int attempt = 0; attempt < 6000 && placedTree < 420; attempt++)
            {
                float u = (float)_rng.NextDouble(), v = (float)_rng.NextDouble();
                float x = u * MapSize, z = v * MapSize;
                float y = Ground(x, z);
                float steep = _td.GetSteepness(u, v);

                if (y < WaterLevel + 1.2f) continue;              // no trees in the lake
                if (steep > 34f) continue;                        // not on cliffs
                if (DistToRoad(new Vector3(x, 0, z)) < 17f) continue;
                if (NearAny(houses, x, z, 24f)) continue;
                if (NearCamera(x, z)) continue;

                // Cleared around the village, and clumped elsewhere: raising the noise to a
                // power gives stands of forest with open meadows between, rather than an
                // even sprinkle over the whole map.
                float pd = Vector2.Distance(new Vector2(u, v), PlateauCentre) / PlateauRadius;
                float clump = Mathf.Pow(Fbm(u * 4.5f, v * 4.5f, 3), 1.9f) * 2.1f;
                float chance = Mathf.Lerp(0.02f, 1f, Mathf.Clamp01(pd)) * clump;
                if (_rng.NextDouble() > chance) continue;

                bool highland = y > MapHeight * 0.33f;
                string model = highland
                    ? pines[_rng.Next(pines.Length)]
                    : (_rng.NextDouble() < 0.55 ? broad[_rng.Next(broad.Length)] : pines[_rng.Next(pines.Length)]);

                Place("Kenney_NatureKit", model, new Vector3(x, y - 0.05f, z),
                      (float)_rng.NextDouble() * 360f, 0.85f + (float)_rng.NextDouble() * 0.55f, forest);
                placedTree++;
            }

            for (int attempt = 0; attempt < 2500 && placedRock < 130; attempt++)
            {
                float u = (float)_rng.NextDouble(), v = (float)_rng.NextDouble();
                float x = u * MapSize, z = v * MapSize;
                float y = Ground(x, z);
                float steep = _td.GetSteepness(u, v);
                if (y < WaterLevel - 0.5f) continue;
                if (DistToRoad(new Vector3(x, 0, z)) < 11f) continue;
                if (NearAny(houses, x, z, 18f)) continue;
                if (NearCamera(x, z)) continue;
                if (steep < 14f && _rng.NextDouble() < 0.72) continue;   // rocks favour slopes

                Place("Kenney_NatureKit", rockSet[_rng.Next(rockSet.Length)],
                      new Vector3(x, y - 0.12f, z), (float)_rng.NextDouble() * 360f,
                      0.20f + (float)_rng.NextDouble() * 0.28f, rocks);
                placedRock++;
            }

            for (int attempt = 0; attempt < 4000 && placedDetail < 300; attempt++)
            {
                float u = (float)_rng.NextDouble(), v = (float)_rng.NextDouble();
                float x = u * MapSize, z = v * MapSize;
                float y = Ground(x, z);
                if (y < WaterLevel + 0.6f) continue;
                if (_td.GetSteepness(u, v) > 28f) continue;
                if (DistToRoad(new Vector3(x, 0, z)) < 6f) continue;
                if (NearCamera(x, z)) continue;

                Place("Kenney_NatureKit", detailSet[_rng.Next(detailSet.Length)],
                      new Vector3(x, y - 0.04f, z), (float)_rng.NextDouble() * 360f,
                      0.10f + (float)_rng.NextDouble() * 0.13f, detail);
                placedDetail++;
            }

            Debug.Log($"SCATTER trees={placedTree} rocks={placedRock} detail={placedDetail}");
        }

        static bool NearCamera(float x, float z)
        {
            foreach (var c in CameraStations)
            {
                float dx = c.x - x, dz = c.z - z;
                if (dx * dx + dz * dz < CameraKeepout * CameraKeepout) return true;
            }
            return false;
        }

        static bool NearAny(List<Vector3> pts, float x, float z, float r)
        {
            float r2 = r * r;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].x - x, dz = pts[i].z - z;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- camera

        static Camera BuildCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 900f;
            cam.clearFlags = CameraClearFlags.Skybox;

            Vector3 target = new Vector3(PlateauCentre.x * MapSize, 0f, PlateauCentre.y * MapSize);
            target.y = Ground(target.x, target.z) + 6f;

            Vector3 pos = CameraStations[0];
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((target - pos).normalized, Vector3.up);

            var orbit = go.AddComponent<Week1.Runtime.OrbitCameraController>();
            orbit.target = target;
            orbit.radius = new Vector2(pos.x - target.x, pos.z - target.z).magnitude;
            orbit.height = pos.y - target.y;
            orbit.degreesPerSecond = 3.5f;
            return cam;
        }

        static void CaptureShots(Camera cam)
        {
            Vector3 target = new Vector3(PlateauCentre.x * MapSize, 0f, PlateauCentre.y * MapSize);
            target.y = Ground(target.x, target.z) + 4f;

            Vector3 lake = new Vector3(LakeCentre.x * MapSize, WaterLevel, LakeCentre.y * MapSize);

            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                // Valley vista: village mid-ground, mountains behind, lake to the left.
                ("01_hero",    CameraStations[0],            new Vector3(186f, 24f, 198f)),
                // Street level through the village.
                ("02_village", CameraStations[1],            new Vector3(200f, 24f, 190f)),
                // Across the water toward the northern ridge.
                ("03_lake",    CameraStations[2],            lake),
                ("04_overview",new Vector3(0.50f * MapSize, 340f, -0.10f * MapSize),
                               new Vector3(0.50f * MapSize, 20f, 0.50f * MapSize)),
            };

            // Rendering to a RenderTexture requires a real graphics device. Under
            // -nographics there is none and cam.Render() segfaults the editor, so skip
            // the captures rather than take the whole build down with them.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("SHOTS_SKIPPED no graphics device (-nographics)");
                return;
            }

            Directory.CreateDirectory(ShotDir);
            var prevPos = cam.transform.position;
            var prevRot = cam.transform.rotation;

            bool fogWas = RenderSettings.fog;
            foreach (var s in shots)
            {
                // The top-down overview sits far past fogEndDistance, which would render it
                // as a flat wash of fog colour. Fog is a look for eye-level shots, not this one.
                RenderSettings.fog = fogWas && s.name != "04_overview";
                cam.transform.position = s.pos;
                cam.transform.rotation = Quaternion.LookRotation((s.look - s.pos).normalized, Vector3.up);

                var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(ShotDir, $"{s.name}.png"), tex.EncodeToPNG());

                RenderTexture.active = null;
                cam.targetTexture = null;
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
                Debug.Log($"SHOT {s.name}");
            }

            RenderSettings.fog = fogWas;
            cam.transform.position = prevPos;
            cam.transform.rotation = prevRot;
        }

        // ---------------------------------------------------------------- noise

        static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Mathf.PerlinNoise(x, y);
                norm += amp;
                amp *= 0.5f;
                x *= 2f; y *= 2f;
            }
            return sum / norm;
        }
    }
}
