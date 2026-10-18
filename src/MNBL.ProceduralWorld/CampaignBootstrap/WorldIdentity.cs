using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem.Map;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-19: the identity a generated world writes into every save header (WorldIdentityBehavior), built from its verified
    // package and the live map. A loader resolves the package from it and must check the package names the same world.
    // MMR-64: a periodic world's header also carries its coordinate frame identity (PeriodicWorldCoordinates.FrameIdentity: version, tie
    // rule, canonical range, poles and layout); "none" for a world that is not periodic. Build and RequireMatch read the world's coordinates
    // through one factory (PeriodicWorld.FromPlacement, which registration uses too), so a header written for a world matches its check.
    // MMR-62: the header also binds the campaign: "instance" (a new campaign instance id, drawn when the world is created) and "contentId"
    // (the data bundle's worldContentId, "none" for a fixture world); "package" is the package directory's name, without the store path
    // (which holds the user name), and loaders resolve the package by world id and manifest hash (GeneratedWorldPackage.Resolve).
    internal static class WorldIdentity
    {
        public const string Schema = MNBL.ProceduralWorld.Persistence.WorldBinding.IdentitySchema;
        public static readonly string[] Keys = { "schema", "id", "seed", "scene", "package", "manifestSha256", "placementSha256",
            "sceneFilesSha256", "documentsSha256", "periodic", "coordinatePolicy", "climate", "sceneXmlCrc", "navMeshCrc", "instance", "contentId" };

        // contentId: the data bundle the world was built from (MMR-45 passes the bundle's worldContentId), or WorldBinding.NoContent for a
        // fixture world; EarlyWorldBindingStore.Begin then requires that bundle to verify.
        public static Dictionary<string, string> Build(GeneratedCampaignRegistration registration, GeneratedWorldPackage package, IMapScene map, string contentId)
        {
            return Build(registration.WorldId, registration.SceneName, registration.Periodic, registration.Climate, package, map, contentId, MNBL.ProceduralWorld.Persistence.WorldBinding.NewInstance());
        }

        // MMR-45: the same for any registered world, with the campaign instance the activation drew (its journal, receipt and save binding).
        public static Dictionary<string, string> Build(string worldId, string sceneName, PeriodicWorld periodic, GeneratedClimate climate, GeneratedWorldPackage package, IMapScene map,
            string contentId, string instance)
        {
            var files = (Dictionary<string, object>)package.Manifest["files"];
            var scene = (Dictionary<string, object>)package.Manifest["sceneFiles"];
            var binding = new MNBL.ProceduralWorld.Persistence.WorldBinding { WorldId = worldId, ManifestSha256 = package.ManifestSha256, Instance = instance, ContentId = contentId };
            return new Dictionary<string, string>(StringComparer.Ordinal) {
                ["schema"] = Schema, ["id"] = worldId, ["seed"] = package.Seed.ToString(CultureInfo.InvariantCulture),
                ["scene"] = sceneName, ["package"] = binding.PackageName, ["manifestSha256"] = package.ManifestSha256,
                ["instance"] = binding.Entries()["instance"], ["contentId"] = binding.ContentId,
                ["placementSha256"] = Convert.ToString(files["placement.json"], CultureInfo.InvariantCulture),
                ["sceneFilesSha256"] = GeneratedWorldPackage.Sha256(Encoding.UTF8.GetBytes(String.Join("\n", scene.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)))),
                ["documentsSha256"] = GeneratedWorldPackage.Sha256(Encoding.UTF8.GetBytes(String.Join("\n", package.DocumentIds.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + package.DocumentSha256(k))))),
                ["periodic"] = periodic == null ? "false" : "true",
                ["coordinatePolicy"] = periodic == null ? "none" : periodic.Coordinates.FrameIdentity,
                ["climate"] = climate == null ? "none" : climate.Pattern,
                ["sceneXmlCrc"] = map.GetSceneXmlCrc().ToString(CultureInfo.InvariantCulture),
                ["navMeshCrc"] = map.GetSceneNavigationMeshCrc().ToString(CultureInfo.InvariantCulture)
            };
        }

        // Fails closed when a header's package names another world: the header's id, scene and seed must be the package's.
        // MMR-64: and when the save was made under another periodic coordinate policy than this build's for the package's layout (or a
        // periodic save has none, or the header and the package disagree on whether the world is periodic).
        public static void RequireMatch(IDictionary<string, string> header, GeneratedWorldPackage package)
        {
            string id, scene, seed;
            if (!header.TryGetValue("id", out id) || id != package.WorldId || !header.TryGetValue("scene", out scene) || scene != package.SceneName ||
                !header.TryGetValue("seed", out seed) || seed != package.Seed.ToString(CultureInfo.InvariantCulture))
                throw new System.IO.InvalidDataException("The save header and its world package name different worlds.");
            var periodic = PeriodicWorld.FromPlacement(package.Placement);
            PeriodicWorldCoordinates.RequireSavedPolicy(header, periodic == null ? null : periodic.Coordinates);
        }
    }
}
