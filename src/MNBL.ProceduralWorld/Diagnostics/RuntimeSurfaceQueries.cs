using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // Caller owns the ready, physics-enabled, natively Read scene and its application-thread lifecycle.
    // This helper never creates, clears, invalidates, or switches a scene/camera.
    public static class RuntimeSurfaceQueries
    {
        private const double HeightTolerance = .05, RayTolerance = .1, NormalTolerance = 1, PixelTolerance = 2;
        private const BodyFlags LandMask = BodyFlags.Moveable | BodyFlags.OnlyCollideWithRaycast;
        private const BodyFlags SeaMask = BodyFlags.Moveable | BodyFlags.CommonCollisionExcludeFlags;
        private const BodyFlags CursorMask = (BodyFlags)79617, CursorMoveableMask = (BodyFlags)79633;

        public static Dictionary<string, object> Measurements(Scene scene, SceneView view, Camera camera,
            DiagnosticHeightfield field, float extent, Action<string> stage, Action<Dictionary<string, object>> publish = null)
        {
            Validate(field, extent);
            if (scene == null || view == null || camera == null) throw new ArgumentNullException("Ready scene, view and camera required.");
            // These are additional gates, not proof of physics readiness or view/scene identity.
            if (!scene.IsLoadingFinished() || !view.ReadyToRender() || Utilities.GetNumberOfShaderCompilationsInProgress() != 0
                || !scene.HasTerrainHeightmap || !scene.ContainsTerrain)
                throw new InvalidOperationException("Native terrain query readiness gate failed; no rays issued.");
            var rows = new List<object>();
            var failures = new List<string>();
            var report = new Dictionary<string, object> {
                { "schema", "mnbl.runtime-surface-queries.v1" }, { "managedThreadId", Thread.CurrentThread.ManagedThreadId },
                { "scope", "Ready native terrain queries; caller must supply matching Scene/View/Camera on application thread. No campaign party, physical mouse click, navigation or allocator-reclamation proof." },
                { "scene", scene.GetName() }, { "module", scene.GetModulePath() }, { "extent", extent },
                { "gridSide", field.Side }, { "measurements", rows }, { "failures", failures },
                { "tolerances", new { heightMetres = HeightTolerance, rayMetres = RayTolerance, normalDegrees = NormalTolerance, pixels = PixelTolerance } },
                { "referenceSurface", "Unchanged quantized MNBLHF01 source; main-diagonal interpolation for physical height. Separate empirical collision vertices=0.03*Truncate(source/0.03), main diagonal (x,y)-(x+1,y+1), calibrated offline against PID32944. Not an asserted engine-internal implementation; near-bin float rounding remains unverified." },
                { "gaps", new[] { "Bump/vertex normal smoothing remains unverified; independent analytic normal assertions apply only to source-validated planar calibration patches.",
                    "Empty-entity selection exercises the stock-sized native call only, not positive entity selection or campaign consequences.",
                    "Actual TranslateMouse is separate ObserveCursor; deterministic camera rays are not physical mouse input.",
                    "Private-field MapScene binding is instrumented/nonproduction; official cache initialization, CampaignVec2.AsVec3 and actual party visuals require a live campaign." } }
            };
            if (publish != null) publish(report);
            var collision = new CollisionReference(field, extent);
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            for (int y = 0; y < field.Side; y++) for (int x = 0; x < field.Side; x++) { low = Math.Min(low, field.At(x, y)); high = Math.Max(high, field.At(x, y)); }
            try
            {
                Mark(stage, "surface_query_bounds");
                Vec2i nodes; float nodeSize; int layers, version;
                scene.GetTerrainData(out nodes, out nodeSize, out layers, out version);
                rows.Add(new { operation = "terrain_node_export", exercised = false,
                    classification = "unsupported_in_this_shipping_lifecycle: PID27392 native crash inside combined GetTerrainNodeData/GetTerrainHeightData stage; not retried; exact failing member not isolated" });
                Vec3 min, max; scene.GetBoundingBox(out min, out max);
                float nativeMin, nativeMax; bool range = scene.GetTerrainMinMaxHeight(out nativeMin, out nativeMax);
                rows.Add(new { operation = "bounds", nodesX = nodes.X, nodesY = nodes.Y, nodeSize = N(nodeSize), layers, version,
                    minimum = V(min), maximum = V(max), expectedExtent = extent, terrainRangeAvailable = range, terrainMinimum = N(nativeMin), terrainMaximum = N(nativeMax), expectedMinimum = low, expectedMaximum = high });
                foreach (string borderName in new[] { "border_min", "border_max" })
                {
                    var border = scene.GetFirstEntityWithName(borderName);
                    rows.Add(new { operation = "authored_border", name = borderName, present = border != null,
                        position = border != null ? V(border.GetGlobalFrame().origin) : null,
                        classification = "authored_entity_observation_not_campaign_cached_GetMapBorders" });
                }
                Check(failures, "terrain_extent_x", Close(nodes.X * (double)nodeSize, extent, HeightTolerance));
                Check(failures, "terrain_extent_y", Close(nodes.Y * (double)nodeSize, extent, HeightTolerance));
                Check(failures, "terrain_minimum", range && Close(nativeMin, low, HeightTolerance));
                Check(failures, "terrain_maximum", range && Close(nativeMax, high, HeightTolerance));
                Check(failures, "scene_bounds_contain_terrain", Finite(min) && Finite(max) && min.x <= HeightTolerance && min.y <= HeightTolerance
                    && max.x >= extent - HeightTolerance && max.y >= extent - HeightTolerance && min.z <= low + HeightTolerance && max.z >= high - HeightTolerance);
                // On 800 -> 640 replacement this ray lies inside the old terrain but outside the new terrain.
                // Do not call height APIs outside the domain: their out-of-bounds semantics are unspecified.
                MeasureRay(scene, view, field, collision, extent, "outside_extent_old_geometry", new Vec3(extent + 80, extent / 2, (float)(high + 500)),
                    new Vec3(extent + 80, extent / 2, (float)(low - 500)), rows, failures, stage);
                foreach (var point in Landmarks(field.Side, extent))
                {
                    string name = point.Key; Vec2 xy = point.Value;
                    Mark(stage, "surface_query_" + name + "_height");
                    double expected = NativeSourceSampleMain(field, extent, xy.x, xy.y);
                    float terrain = scene.GetTerrainHeight(xy, true), terrainNoHoles = scene.GetTerrainHeight(xy, false), hn;
                    Vec3 normal; scene.GetTerrainHeightAndNormal(xy, out hn, out normal);
                    Vec3 normalAt = scene.GetNormalAt(xy);
                    float land = float.NaN, sea = float.NaN;
                    bool landHit = scene.GetHeightAtPoint(xy, LandMask, ref land), seaHit = scene.GetHeightAtPoint(xy, SeaMask, ref sea);
                    Vec3 geometric = GeometricNormal(field, extent, xy);
                    rows.Add(new { operation = "height_normal_ground", landmark = name, position = new[] { xy.x, xy.y }, expectedHeight = expected,
                        legacyAntiDiagonalSourceHeight = Sample(field, extent, xy.x, xy.y), predictedCollisionHeight = collision.Sample(xy.x, xy.y),
                        collisionHeightError = N(terrain - collision.Sample(xy.x, xy.y)), collisionPredictionClassification = "observation_only_empirical_model; physical_source_height_limit_remains_0.05m",
                        terrainHeight = N(terrain), terrainHeightWithoutHoleCheck = N(terrainNoHoles), combinedHeight = N(hn), normal = V(normal), normalAt = V(normalAt),
                        geometricCellNormal = V(geometric), geometricAngleDegrees = N(Angle(normal, geometric)), normalAgreementDegrees = N(Angle(normal, normalAt)),
                        geometricClassification = "observation_only_native_smoothing_and_grid_vertex_normal_unverified", landHit, landHeight = N(land), landExcludeMask = (uint)LandMask,
                        seaHit, seaHeight = N(sea), seaExcludeMask = (uint)SeaMask });
                    Check(failures, name + ":terrain_height", Close(terrain, expected, HeightTolerance));
                    Check(failures, name + ":terrain_height_no_holes", Close(terrainNoHoles, expected, HeightTolerance));
                    Check(failures, name + ":combined_height", Close(hn, expected, HeightTolerance));
                    Check(failures, name + ":land_ground", landHit && Close(land, expected, HeightTolerance));
                    Check(failures, name + ":sea_ground", seaHit && Close(sea, expected, HeightTolerance));
                    Check(failures, name + ":normal_finite_unit", Finite(normal) && Math.Abs(Length(normal) - 1) < .01 && Finite(normalAt) && Math.Abs(Length(normalAt) - 1) < .01);
                    Check(failures, name + ":native_normal_agreement", Angle(normal, normalAt) <= NormalTolerance);
                    Vec3 from = new Vec3(xy.x, xy.y, (float)(high + 50)), to = new Vec3(xy.x, xy.y, (float)(low - 50));
                    MeasureRay(scene, view, field, collision, extent, name + ":vertical", from, to, rows, failures, stage);
                    from.x = Math.Max(0, xy.x - extent * .08f); to.x = Math.Min(extent, xy.x + extent * .08f);
                    MeasureRay(scene, view, field, collision, extent, name + ":oblique", from, to, rows, failures, stage);
                    Mark(stage, "surface_query_" + name + "_camera");
                    Vec3 world = new Vec3(xy.x, xy.y, (float)expected);
                    Vec3 projected = camera.WorldPointToViewPortPoint(ref world);
                    Vec3 near = Vec3.Zero, far = Vec3.Zero;
                    camera.ViewportPointToWorldRay(ref near, ref far, new Vec2(projected.x, projected.y));
                    bool insideViewport = projected.x >= 0 && projected.x <= 1 && projected.y >= 0 && projected.y <= 1;
                    rows.Add(new { operation = "camera_projection", landmark = name, world = V(world), viewport = V(projected), rayStart = V(near), rayEnd = V(far),
                        insideViewport, classification = insideViewport ? "deterministic_viewport_ray_not_mouse_input" : "outside_current_zoom_viewport_not_a_selection_test" });
                    Check(failures, name + ":projection_finite", Finite(projected) && Finite(near) && Finite(far));
                    // Zoom legitimately clips inset corner landmarks; retain their projection but do not count offscreen rays as picking.
                    if (!insideViewport || !Finite(projected)) continue;
                    Vec3? hit = MeasureRay(scene, view, field, collision, extent, name + ":camera", near, far, rows, failures, stage);
                    if (hit.HasValue)
                    {
                        Vec3 hitWorld = hit.Value; Vec3 back = camera.WorldPointToViewPortPoint(ref hitWorld);
                        double dx = (back.x - projected.x) * Screen.RealScreenResolutionWidth, dy = (back.y - projected.y) * Screen.RealScreenResolutionHeight;
                        double pixels = Math.Sqrt(dx * dx + dy * dy), landmarkError = Distance(hitWorld, world);
                        rows.Add(new { operation = "camera_roundtrip", landmark = name, reprojected = V(back), pixelError = N(pixels), landmarkDistance = N(landmarkError),
                            landmarkClassification = landmarkError <= RayTolerance ? "visible_landmark_hit" : "occluded_or_projection_mismatch_requires_inspection" });
                        Check(failures, name + ":camera_pixel_roundtrip", Finite(pixels) && pixels <= PixelTolerance);
                        // Occlusion is reported, not silently accepted as a landmark selection proof.
                    }
                }
                MeasureEmptySelection(scene, field, collision, extent, low, high, rows, failures, stage);
                MeasureAdapter(scene, field, extent, high, rows, failures, stage);
                if (Math.Abs(extent - 256) < .001) MeasurePlanarNormals(scene, field, extent, low, high, rows, failures, stage);
            }
            catch (Exception e)
            {
                failures.Add("managed_query_exception:" + e.GetType().FullName);
                report["exception"] = e.ToString(); // Preserve earlier rows; stop issuing native calls on unexpected failure.
            }
            report["passed"] = failures.Count == 0;
            report["mandatoryCoverageComplete"] = false;
            return report;
        }

        // Root calls this while an actual cursor is observed; this method never positions/injects it.
        public static Dictionary<string, object> ObserveCursor(Scene scene, SceneView view, DiagnosticHeightfield field,
            float extent, Action<string> stage)
        {
            Validate(field, extent);
            if (scene == null || view == null || !scene.IsLoadingFinished() || !view.ReadyToRender()
                || Utilities.GetNumberOfShaderCompilationsInProgress() != 0 || !scene.HasTerrainHeightmap || !scene.ContainsTerrain)
                throw new InvalidOperationException("Observed cursor requires ready native terrain.");
            Mark(stage, "surface_query_observed_cursor_translate");
            Vec3 near = Vec3.Zero, far = Vec3.Zero;
            view.TranslateMouse(ref near, ref far, -1);
            var rows = new List<object>(); var failures = new List<string>();
            var collision = new CollisionReference(field, extent);
            // HandleMouse invokes BOTH masks in order, not only on failure of the first ray.
            // Its land-face -> sea-face fallback requires Campaign.Current and is deliberately not simulated here.
            MeasureRay(scene, view, field, collision, extent, "observed_cursor_focus", near, far, rows, failures, stage, CursorMask);
            MeasureRay(scene, view, field, collision, extent, "observed_cursor_focus_exclude_moveable", near, far, rows, failures, stage, CursorMoveableMask);
            return new Dictionary<string, object> { { "scope", "Actual current TranslateMouse ray; no input injection or campaign selection consequence." },
                { "translateMouseMaxDistance", -1 }, { "stockMaskOrder", new[] { 79617, 79633 } },
                { "campaignFaceFallback", "not_exercised_requires_Campaign.Current" },
                { "measurements", rows }, { "failures", failures }, { "passed", failures.Count == 0 } };
        }

        public static void RequirePassed(Dictionary<string, object> report)
        {
            if (!(bool)report["passed"]) throw new InvalidOperationException("Surface query checks failed; persist and inspect the full report.");
        }

        private static void MeasureTerrainNodes(Scene scene, Vec2i dimensions, List<object> rows, List<string> failures, Action<string> stage)
        {
            foreach (int index in new[] { 0, 3 })
            {
                if (dimensions.X <= index || dimensions.Y <= index)
                {
                    rows.Add(new { operation = "terrain_node_data", nodeX = index, nodeY = index, classification = "not_called_outside_reported_node_dimensions" });
                    failures.Add("terrain_node_" + index + ":unavailable"); continue;
                }
                Mark(stage, "surface_query_node_" + index);
                int vertices; float quad, minimum, maximum;
                scene.GetTerrainNodeData(index, index, out vertices, out quad, out minimum, out maximum);
                float[] data = scene.GetTerrainHeightData(index, index);
                double low = double.PositiveInfinity, high = double.NegativeInfinity; int nonfinite = 0;
                if (data != null) foreach (float v in data) { if (!Finite(v)) { nonfinite++; continue; } low = Math.Min(low, v); high = Math.Max(high, v); }
                string hash = null;
                if (data != null)
                {
                    var bytes = new byte[checked(data.Length * sizeof(float))]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                }
                rows.Add(new { operation = "terrain_node_data", nodeX = index, nodeY = index, vertexCountAlongAxis = vertices,
                    quadLength = N(quad), nativeMinimum = N(minimum), nativeMaximum = N(maximum), arrayLength = data == null ? 0 : data.Length,
                    arrayMinimum = N(low), arrayMaximum = N(high), nonfiniteValues = nonfinite, arraySha256 = hash,
                    arrayHashEncoding = "IEEE754 binary32 native little-endian Windows memory order; no transpose",
                    minimumDifference = N(low - minimum), maximumDifference = N(high - maximum),
                    classification = "raw_native_node_inventory; layout/units and relation of extrema to node bounds unverified; differences observational" });
                Check(failures, "terrain_node_" + index + ":finite_data", vertices > 0 && Finite(quad) && quad > 0
                    && Finite(minimum) && Finite(maximum) && minimum <= maximum && data != null && data.Length > 0 && nonfinite == 0);
            }
        }

        private static void MeasureEmptySelection(Scene scene, DiagnosticHeightfield field, CollisionReference collision, float extent, double low, double high,
            List<object> rows, List<string> failures, Action<string> stage)
        {
            Mark(stage, "surface_query_empty_selection_precondition");
            int rootEntities = scene.RootEntityCount;
            var entities = new List<GameEntity>();
            scene.GetEntities(ref entities);
            var inventory = new List<object>();
            var knownIds = new Dictionary<UIntPtr, string>();
            int entityCount = entities.Count;
            try
            {
                foreach (var entity in entities)
                {
                    if (entity == null) throw new InvalidOperationException("Null entity in native selection inventory.");
                    string name = entity.Name;
                    inventory.Add(new { name, children = entity.ChildCount });
                    knownIds[entity.Pointer] = name; // Read a known wrapper's managed pointer; never wrap/dereference returned IDs.
                }
            }
            finally { entities.Clear(); }
            rows.Add(new { operation = "selection_entity_inventory", rootEntities, entityCount, entities = inventory,
                classification = "native runtime entity inventory; empty authored XML does not imply zero runtime entities" });
            if (entityCount > 16)
            {
                rows.Add(new { operation = "empty_entity_selection", rootEntities, entityCount, exercised = false,
                    classification = "unexpectedly_large_fixture_no_native_selection_issued" });
                failures.Add("empty_entity_selection:fixture_limit"); return;
            }
            var intersections = new Intersection[128]; var ids = new UIntPtr[128];
            Vec3 from = new Vec3(extent / 2, extent / 2, (float)(high + 50)), to = new Vec3(extent / 2, extent / 2, (float)(low - 50));
            Vec3 direction = to - from; float distance = direction.Normalize();
            if (!Finite(distance) || distance <= 0) throw new InvalidOperationException("Invalid selection ray; native call withheld.");
            var ray = new Ray(from, direction, distance);
            Mark(stage, "surface_query_empty_selection_native");
            int count = scene.SelectEntitiesCollidedWith(ref ray, intersections, ids);
            rows.Add(new { operation = "empty_entity_selection", rootEntities, exercised = true, capacity = 128, returnedCount = count,
                start = V(from), end = V(to), direction = V(direction), maxDistance = N(distance),
                classification = "stock_128_buffers_bounded_runtime_fixture; native capacity contract follows stock caller, not inferred from root count; no returned_pointer_dereference; not_positive_selection_or_campaign_consequence" });
            // Native API has no capacity argument. Reproduce stock capacity, then validate before any array operation.
            if (count < 0 || count > 128) throw new InvalidOperationException("Native selection count outside stock buffer capacity; stop all further native queries.");
            var hits = new List<object>();
            for (int i = 0; i < count; i++)
            {
                string entityName = null;
                bool known = ids[i] != UIntPtr.Zero && knownIds.TryGetValue(ids[i], out entityName);
                Intersection hit = intersections[i];
                Vec3 offset = hit.IntersectionPoint - from;
                double along = offset.x * (double)direction.x + offset.y * (double)direction.y + offset.z * (double)direction.z;
                double offRay = Distance(hit.IntersectionPoint, Lerp(from, to, along / distance));
                bool validType = hit.Type == IntersectionType.Body || hit.Type == IntersectionType.Terrain;
                bool finite = Finite(hit.IntersectionPoint) && Finite(hit.IntersectionNormal) && Finite(hit.Penetration);
                bool terrain = hit.Type == IntersectionType.Terrain;
                bool inDomain = finite && hit.IntersectionPoint.x >= 0 && hit.IntersectionPoint.x <= extent
                    && hit.IntersectionPoint.y >= 0 && hit.IntersectionPoint.y <= extent;
                double expectedHeight = terrain && inDomain ? collision.Sample(hit.IntersectionPoint.x, hit.IntersectionPoint.y) : double.NaN;
                double sourceHeight = terrain && inDomain ? NativeSourceSampleMain(field, extent, hit.IntersectionPoint.x, hit.IntersectionPoint.y) : double.NaN;
                bool withinSegment = Finite(along) && along >= -RayTolerance && along <= distance + RayTolerance;
                hits.Add(new { index = i, matchedInventoryEntity = known, entityName,
                    returnedIdIsZero = ids[i] == UIntPtr.Zero,
                    type = hit.Type.ToString(), typeValue = (int)hit.Type, details = hit.Details.ToString(), detailsValue = (int)hit.Details,
                    point = V(hit.IntersectionPoint), normal = V(hit.IntersectionNormal), penetration = N(hit.Penetration),
                    projectedDistance = N(along), perpendicularDistance = N(offRay),
                    withinRaySegment = withinSegment, sourceDomain = inDomain, expectedTerrainHeight = N(expectedHeight),
                    untruncatedSourceHeight = N(sourceHeight), sourceHeightResidual = N(hit.IntersectionPoint.z - sourceHeight),
                    terrainHeightResidual = N(hit.IntersectionPoint.z - expectedHeight),
                    geometryClassification = terrain ? "terrain_intersection_validated_against_source_and_ray; entity_ID_not_required"
                        : "body_identity_matched_to_known_inventory; geometry_observational; Penetration_is_not_assumed_ray_distance" });
                if (terrain)
                {
                    Check(failures, "entity_selection:terrain_height:" + i, inDomain && Close(hit.IntersectionPoint.z, expectedHeight, HeightTolerance));
                    Check(failures, "entity_selection:terrain_source_height:" + i, inDomain && Close(hit.IntersectionPoint.z, sourceHeight, HeightTolerance));
                    Check(failures, "entity_selection:terrain_segment:" + i, withinSegment);
                    Check(failures, "entity_selection:terrain_on_ray:" + i, Finite(offRay) && offRay <= RayTolerance);
                }
                else if (hit.Type == IntersectionType.Body) Check(failures, "entity_selection:known_inventory_id:" + i, known);
                Check(failures, "entity_selection:valid_type:" + i, validType);
                Check(failures, "entity_selection:finite_record:" + i, finite);
            }
            rows.Add(new { operation = "selection_identity_validation", returnedCount = count, hits,
                classification = "body:stock_style_known_pointer_dictionary_match; terrain:source_height_and_segment_checks_without_entity_ID; zero_hits_valid; not_campaign_selection" });
            Array.Clear(intersections, 0, count); Array.Clear(ids, 0, count);
        }

        private static FieldInfo MapField(string name, Type type)
        {
            var field = typeof(MapScene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || field.FieldType != type) throw new MissingFieldException("Installed diagnostic MapScene field contract changed: " + name);
            return field;
        }

        private static MapScene BindAdapter(Scene scene, float extent, float maximumHeight)
        {
            // Only these installed methods are valid on this incomplete object. Never publish it into Campaign.Current.
            var sceneField = MapField("_scene", typeof(Scene));
            var minField = MapField("_minimumPositionCache", typeof(Vec2));
            var maxField = MapField("_maximumPositionCache", typeof(Vec2));
            var heightField = MapField("_maximumHeightCache", typeof(float));
            var map = new MapScene();
            minField.SetValue(map, new Vec2(0, 0)); maxField.SetValue(map, new Vec2(extent, extent)); heightField.SetValue(map, maximumHeight);
            sceneField.SetValue(map, scene); return map;
        }

        private static void MeasureAdapter(Scene scene, DiagnosticHeightfield field, float extent, double high,
            List<object> rows, List<string> failures, Action<string> stage)
        {
            Mark(stage, "surface_query_instrumented_map_adapter");
            MapScene map = BindAdapter(scene, extent, (float)high);
            try
            {
                IMapScene wrapper = map;
                Vec2 minimum, maximum; float maximumHeight;
                wrapper.GetMapBorders(out minimum, out maximum, out maximumHeight);
                rows.Add(new { operation = "instrumented_map_borders", minimum = new[] { minimum.x, minimum.y }, maximum = new[] { maximum.x, maximum.y }, maximumHeight = N(maximumHeight),
                    expectedExtent = extent, expectedMaximumHeight = high, classification = "nonproduction_private_cache_injection; proves_getter_delegation_not_official_Load_initialization" });
                Check(failures, "instrumented_map_borders", Close(minimum.x, 0, HeightTolerance) && Close(minimum.y, 0, HeightTolerance)
                    && Close(maximum.x, extent, HeightTolerance) && Close(maximum.y, extent, HeightTolerance) && Close(maximumHeight, high, HeightTolerance));
                foreach (var landmark in Landmarks(field.Side, extent))
                {
                    Vec2 xy = landmark.Value; double expected = NativeSourceSampleMain(field, extent, xy.x, xy.y);
                    foreach (bool isLand in new[] { true, false })
                    {
                        var point = new CampaignVec2(xy, isLand); float height = float.NaN, direct = float.NaN;
                        bool hit = wrapper.GetHeightAtPoint(in point, ref height);
                        bool directHit = scene.GetHeightAtPoint(xy, isLand ? LandMask : SeaMask, ref direct);
                        rows.Add(new { operation = "instrumented_map_ground", landmark = landmark.Key, isLand, hit, height = N(height), directHit, directHeight = N(direct), expectedHeight = expected,
                            classification = "nonproduction_borrowed_scene_IMapScene_call; not_AsVec3_or_party_visual" });
                        Check(failures, landmark.Key + ":adapter_ground_" + isLand, hit && directHit && Close(height, direct, HeightTolerance) && Close(height, expected, HeightTolerance));
                    }
                    float h, directH; Vec3 n, directN;
                    wrapper.GetTerrainHeightAndNormal(xy, out h, out n); scene.GetTerrainHeightAndNormal(xy, out directH, out directN);
                    rows.Add(new { operation = "instrumented_map_height_normal", landmark = landmark.Key, height = N(h), normal = V(n), directHeight = N(directH), directNormal = V(directN) });
                    Check(failures, landmark.Key + ":adapter_height_normal", Close(h, directH, HeightTolerance) && Angle(n, directN) <= NormalTolerance);
                }
            }
            finally
            {
                MapField("_scene", typeof(Scene)).SetValue(map, null);
                rows.Add(new { operation = "instrumented_map_cleanup", borrowedSceneReferenceCleared = true, loadCalled = false, destroyCalled = false, campaignBindingChanged = false });
            }
        }

        // Independent analytic plane gate, checked against local quantized edge slopes before any native normal assertion.
        private static bool PlanarReference(DiagnosticHeightfield f, float extent, Vec2 point, double quantizationStep,
            out double maximumSlopeError, out double slopeTolerance, out double heightError)
        {
            double spacing = extent / (f.Side - 1.0), gx = point.x / spacing, gy = point.y / spacing;
            int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
            maximumSlopeError = 0; slopeTolerance = quantizationStep / spacing + 1e-6;
            heightError = Math.Abs(Sample(f, extent, point.x, point.y) - (2 + .02 * point.x + .05 * point.y));
            if (ix < 1 || iy < 1 || ix + 2 >= f.Side || iy + 2 >= f.Side) return false;
            for (int y = iy - 1; y <= iy + 1; y++) for (int x = ix - 1; x <= ix + 1; x++)
            {
                maximumSlopeError = Math.Max(maximumSlopeError, Math.Abs((f.At(x + 1, y) - f.At(x, y)) / spacing - .02));
                maximumSlopeError = Math.Max(maximumSlopeError, Math.Abs((f.At(x, y + 1) - f.At(x, y)) / spacing - .05));
            }
            return Finite(maximumSlopeError) && maximumSlopeError <= slopeTolerance && heightError <= quantizationStep / 2 + 1e-6;
        }

        private static void MeasurePlanarNormals(Scene scene, DiagnosticHeightfield field, float extent, double low, double high,
            List<object> rows, List<string> failures, Action<string> stage)
        {
            // Encoder bounds are floor/ceil at .001m; this conservatively bounds their quantization step.
            double stepBound = (high - low + .002) / 65535;
            Vec3 analytic = new Vec3(-.02f, -.05f, 1); analytic.Normalize();
            foreach (Vec2 p in new[] { new Vec2(20, 210), new Vec2(20.5f, 210.5f), new Vec2(21.5f, 210.5f) })
            {
                Mark(stage, "surface_query_planar_normal_" + p.x + "_" + p.y);
                double slopeError, slopeTolerance, heightError;
                bool eligible = PlanarReference(field, extent, p, stepBound, out slopeError, out slopeTolerance, out heightError);
                var row = new Dictionary<string, object> { { "operation", "analytic_planar_normal" }, { "position", new[] { p.x, p.y } },
                    { "analyticNormal", V(analytic) }, { "sourcePlaneVerified", eligible }, { "quantizationStepBound", stepBound },
                    { "maximumLocalSlopeError", N(slopeError) }, { "slopeTolerance", slopeTolerance }, { "sourceHeightError", N(heightError) },
                    { "classification", eligible ? "independent_analytic_plane_normal" : "reference_precondition_failed_no_native_normal_assertion" } };
                rows.Add(row);
                if (!eligible) { failures.Add("analytic_plane:source_precondition:" + p); continue; }
                Vec3 normal = scene.GetNormalAt(p), combined; float height;
                scene.GetTerrainHeightAndNormal(p, out height, out combined);
                row["normalAt"] = V(normal); row["combinedNormal"] = V(combined); row["combinedHeight"] = N(height);
                row["normalAtAngleDegrees"] = N(Angle(normal, analytic)); row["combinedAngleDegrees"] = N(Angle(combined, analytic));
                Check(failures, "analytic_plane:normal_at:" + p, Angle(normal, analytic) <= NormalTolerance);
                Check(failures, "analytic_plane:combined_normal:" + p, Angle(combined, analytic) <= NormalTolerance);
            }
        }

        private static Vec3? MeasureRay(Scene scene, SceneView view, DiagnosticHeightfield field, CollisionReference collision, float extent, string name,
            Vec3 from, Vec3 to, List<object> rows, List<string> failures, Action<string> stage, BodyFlags excludeMask = LandMask)
        {
            if (!Finite(from) || !Finite(to) || Distance(from, to) <= 1e-6) { failures.Add(name + ":invalid_ray"); return null; }
            double scale = (field.Side - 1) / (double)extent, fraction;
            bool expectedHit = collision.Intersect(from, to, out fraction);
            Vec3 expected = expectedHit ? Lerp(from, to, fraction) : Vec3.Zero;
            double legacyFraction;
            bool legacyHit = field.Intersect(from.x * scale, from.y * scale, from.z, to.x * scale, to.y * scale, to.z, out legacyFraction);
            Vec3 legacyPoint = legacyHit ? Lerp(from, to, legacyFraction) : Vec3.Zero;
            Vec3? first = null;
            for (int path = 0; path < 2; path++)
            {
                string api = path == 0 ? "Scene" : "SceneView";
                Mark(stage, "surface_query_" + name + "_" + api + "_ray");
                float distance; Vec3 closest;
                bool hit = path == 0
                    ? scene.RayCastForClosestEntityOrTerrain(from, to, out distance, out closest, .01f, excludeMask)
                    : view.RayCastForClosestEntityOrTerrain(from, to, out distance, out closest, .01f, excludeMask);
                double length = Distance(from, to); Vec3 reconstructed = Lerp(from, to, distance / length);
                // Match MapScreen.GetCursorIntersectionPoint: distance defines the picked point.
                // Installed Scene/SceneView AND IScene/ISceneView name the other output closestPoint, not normal.
                double error = hit && expectedHit ? Distance(reconstructed, expected) : double.NaN;
                bool sourceDomain = hit && Finite(reconstructed) && reconstructed.x >= 0 && reconstructed.x <= extent
                    && reconstructed.y >= 0 && reconstructed.y <= extent;
                double sourceResidual = sourceDomain ? reconstructed.z - NativeSourceSampleMain(field, extent, reconstructed.x, reconstructed.y) : double.NaN;
                rows.Add(new { operation = "ray", name, api, start = V(from), end = V(to), expectedHit, expectedPoint = expectedHit ? V(expected) : null,
                    referenceClassification = "empirical_0.03m_toward_zero_vertices_main_diagonal_earliest_intersection", hit, distance = N(distance), closest = V(closest), reconstructed = V(reconstructed),
                    legacySourceHit = legacyHit, legacySourcePoint = legacyHit ? V(legacyPoint) : null,
                    legacySourceReferenceError = N(hit && legacyHit ? Distance(reconstructed, legacyPoint) : double.NaN),
                    legacySourceClassification = "historical_untruncated_anti_diagonal_observation_only", sourceDomain, untruncatedMainSourceHeightResidual = N(sourceResidual),
                    nativeVectorContract = "closestPoint in installed wrapper and native-interface metadata; hit used for comparisons is reconstructed from distance as in MapScreen",
                    referenceError = N(error), returnedPointDistanceError = N(Distance(closest, reconstructed)), thickness = .01, excludeMask = (uint)excludeMask });
                Check(failures, name + ":" + api + ":hit", hit == expectedHit);
                if (hit)
                {
                    Check(failures, name + ":" + api + ":distance", Finite(distance) && distance >= 0 && distance <= length + RayTolerance);
                    Check(failures, name + ":" + api + ":point_distance_agreement", Finite(closest) && Distance(closest, reconstructed) <= RayTolerance);
                    if (expectedHit) Check(failures, name + ":" + api + ":reference", error <= RayTolerance);
                    if (sourceDomain) Check(failures, name + ":" + api + ":source_height_deviation", Math.Abs(sourceResidual) <= HeightTolerance);
                    if (path == 0) first = reconstructed;
                    else if (first.HasValue) Check(failures, name + ":scene_view_agreement", Distance(first.Value, reconstructed) <= RayTolerance);
                }
            }
            return first;
        }

        private static IEnumerable<KeyValuePair<string, Vec2>> Landmarks(int side, float extent)
        {
            int end = side - 2, mid = (side - 1) / 2;
            int[,] grid = { { 1, 1 }, { end, 1 }, { 1, end }, { end, end }, { mid, mid }, { mid, mid / 2 }, { mid / 2, mid } };
            for (int i = 0; i < grid.GetLength(0); i++) yield return new KeyValuePair<string, Vec2>("grid_" + grid[i, 0] + "_" + grid[i, 1], new Vec2(grid[i, 0] * extent / (side - 1), grid[i, 1] * extent / (side - 1)));
            if (Math.Abs(extent - 256) < .001)
                foreach (var p in new[] { new Vec2(160, 90), new Vec2(128, 90), new Vec2(200, 128) })
                    yield return new KeyValuePair<string, Vec2>("calibration_" + p.x + "_" + p.y, p);
            foreach (var p in new[] { new Vec2(50.25f, 64.6f), new Vec2(80.75f, 40.2f) })
                yield return new KeyValuePair<string, Vec2>("prospective_grid_" + p.x + "_" + p.y,
                    new Vec2(p.x * extent / (side - 1), p.y * extent / (side - 1)));
        }

        // Separate empirical collision reference: never mutates the source or shared DiagnosticHeightfield semantics.
        // Double arithmetic deliberately defines this model. Engine float rounding at exact 0.03m bin edges is unresolved.
        internal sealed class CollisionReference
        {
            private readonly int side;
            private readonly double extent, spacing;
            private readonly double[] heights;
            internal CollisionReference(DiagnosticHeightfield source, float worldExtent)
            {
                Validate(source, worldExtent); side = source.Side; extent = worldExtent; spacing = extent / (side - 1);
                heights = new double[side * side];
                for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
                    heights[y * side + x] = Quantize(source.At(x, y));
            }
            internal static double Quantize(double height) { return .03 * Math.Truncate(height / .03); }
            internal double Sample(double x, double y)
            {
                if (!Finite(x) || !Finite(y) || x < 0 || y < 0 || x > extent || y > extent) throw new ArgumentOutOfRangeException();
                double gx = x / spacing, gy = y / spacing;
                int ix = Math.Min((int)gx, side - 2), iy = Math.Min((int)gy, side - 2);
                return MainTriangle(heights[iy * side + ix], heights[iy * side + ix + 1],
                    heights[(iy + 1) * side + ix], heights[(iy + 1) * side + ix + 1], gx - ix, gy - iy);
            }
            internal bool Intersect(Vec3 from, Vec3 to, out double fraction)
            {
                if (!Finite(from) || !Finite(to)) throw new ArgumentException("Nonfinite collision-reference segment.");
                var origin = new Point(from.x, from.y, from.z);
                var delta = new Point((double)to.x - from.x, (double)to.y - from.y, (double)to.z - from.z);
                fraction = double.PositiveInfinity;
                for (int y = 0; y < side - 1; y++) for (int x = 0; x < side - 1; x++)
                {
                    var a = new Point(x * spacing, y * spacing, heights[y * side + x]);
                    var b = new Point((x + 1) * spacing, y * spacing, heights[y * side + x + 1]);
                    var c = new Point(x * spacing, (y + 1) * spacing, heights[(y + 1) * side + x]);
                    var d = new Point((x + 1) * spacing, (y + 1) * spacing, heights[(y + 1) * side + x + 1]);
                    TestTriangle(origin, delta, a, b, d, ref fraction);
                    TestTriangle(origin, delta, a, d, c, ref fraction);
                }
                return !double.IsPositiveInfinity(fraction);
            }
            private static void TestTriangle(Point origin, Point delta, Point a, Point b, Point c, ref double nearest)
            {
                Point e1 = b - a, e2 = c - a, p = Point.Cross(delta, e2);
                double determinant = Point.Dot(e1, p);
                if (Math.Abs(determinant) < 1e-12) return;
                Point s = origin - a;
                double u = Point.Dot(s, p) / determinant;
                if (u < -1e-10 || u > 1 + 1e-10) return;
                Point q = Point.Cross(s, e1);
                double v = Point.Dot(delta, q) / determinant;
                if (v < -1e-10 || u + v > 1 + 1e-10) return;
                double t = Point.Dot(e2, q) / determinant;
                if (t >= 0 && t <= 1 && t < nearest) nearest = t;
            }
            private struct Point
            {
                private readonly double x, y, z;
                internal Point(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
                public static Point operator -(Point a, Point b) { return new Point(a.x - b.x, a.y - b.y, a.z - b.z); }
                internal static Point Cross(Point a, Point b) { return new Point(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
                internal static double Dot(Point a, Point b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
            }
        }
        private static double MainTriangle(double a, double b, double c, double d, double u, double v)
        {
            return u >= v ? a * (1 - u) + b * (u - v) + d * v : a * (1 - v) + c * (v - u) + d * u;
        }
        private static double NativeSourceSampleMain(DiagnosticHeightfield f, float extent, double x, double y)
        {
            if (!Finite(x) || !Finite(y) || x < 0 || y < 0 || x > extent || y > extent) throw new ArgumentOutOfRangeException();
            double gx = x * (f.Side - 1) / extent, gy = y * (f.Side - 1) / extent;
            int ix = Math.Min((int)gx, f.Side - 2), iy = Math.Min((int)gy, f.Side - 2);
            return MainTriangle(f.At(ix, iy), f.At(ix + 1, iy), f.At(ix, iy + 1), f.At(ix + 1, iy + 1), gx - ix, gy - iy);
        }

        private static Vec3 GeometricNormal(DiagnosticHeightfield f, float extent, Vec2 point)
        {
            double scale = (f.Side - 1) / (double)extent, x = point.x * scale, y = point.y * scale;
            int ix = Math.Min((int)x, f.Side - 2), iy = Math.Min((int)y, f.Side - 2);
            double dx, dy;
            if (x - ix + y - iy <= 1) { dx = f.At(ix + 1, iy) - f.At(ix, iy); dy = f.At(ix, iy + 1) - f.At(ix, iy); }
            else { dx = f.At(ix + 1, iy + 1) - f.At(ix, iy + 1); dy = f.At(ix + 1, iy + 1) - f.At(ix + 1, iy); }
            var n = new Vec3((float)(-dx * scale), (float)(-dy * scale), 1); n.Normalize(); return n;
        }
        private static double Sample(DiagnosticHeightfield f, float extent, double x, double y) { return f.Sample(x * (f.Side - 1) / extent, y * (f.Side - 1) / extent); }
        private static void Validate(DiagnosticHeightfield f, float extent) { if (f == null || !Finite(extent) || extent <= 0) throw new ArgumentException("Finite positive extent and quantized heightfield required."); }
        private static void Mark(Action<string> stage, string value) { if (stage != null) stage(value); }
        private static void Check(List<string> failures, string name, bool passed) { if (!passed) failures.Add(name); }
        private static bool Close(double a, double b, double tolerance) { return Finite(a) && Finite(b) && Math.Abs(a - b) <= tolerance; }
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        private static bool Finite(Vec3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
        private static object N(double v) { return Finite(v) ? (object)v : v.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static object[] V(Vec3 v) { return new[] { N(v.x), N(v.y), N(v.z) }; }
        private static double Length(Vec3 v) { return Math.Sqrt(v.x * (double)v.x + v.y * (double)v.y + v.z * (double)v.z); }
        private static double Distance(Vec3 a, Vec3 b) { return Length(a - b); }
        private static Vec3 Lerp(Vec3 a, Vec3 b, double t) { return new Vec3((float)(a.x + (b.x - a.x) * t), (float)(a.y + (b.y - a.y) * t), (float)(a.z + (b.z - a.z) * t)); }
        private static double Angle(Vec3 a, Vec3 b) { double n = Length(a) * Length(b); if (!Finite(n) || n < 1e-12) return double.NaN; return Math.Acos(Math.Max(-1, Math.Min(1, (a.x * (double)b.x + a.y * (double)b.y + a.z * (double)b.z) / n))) * 180 / Math.PI; }
    }
}
