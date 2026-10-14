using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem.Map;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-19: the identity a generated world writes into every save header (WorldIdentityBehavior), built from its verified
    // package and the live map. A loader resolves the package from it and must check the package names the same world.
    internal static class WorldIdentity
    {
        public const string Schema = "mnbl.world-identity.v1";
        public static readonly string[] Keys = { "schema", "id", "seed", "scene", "package", "manifestSha256", "placementSha256",
            "sceneFilesSha256", "documentsSha256", "periodic", "climate", "sceneXmlCrc", "navMeshCrc" };

        public static Dictionary<string, string> Build(GeneratedCampaignRegistration registration, GeneratedWorldPackage package, IMapScene map)
        {
            var files = (Dictionary<string, object>)package.Manifest["files"];
            var scene = (Dictionary<string, object>)package.Manifest["sceneFiles"];
            return new Dictionary<string, string>(StringComparer.Ordinal) {
                ["schema"] = Schema, ["id"] = registration.WorldId, ["seed"] = package.Seed.ToString(CultureInfo.InvariantCulture),
                ["scene"] = registration.SceneName, ["package"] = package.Directory, ["manifestSha256"] = package.ManifestSha256,
                ["placementSha256"] = Convert.ToString(files["placement.json"], CultureInfo.InvariantCulture),
                ["sceneFilesSha256"] = GeneratedWorldPackage.Sha256(Encoding.UTF8.GetBytes(String.Join("\n", scene.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)))),
                ["documentsSha256"] = GeneratedWorldPackage.Sha256(Encoding.UTF8.GetBytes(String.Join("\n", package.DocumentIds.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + package.DocumentSha256(k))))),
                ["periodic"] = registration.Periodic == null ? "false" : "true",
                ["climate"] = registration.Climate == null ? "none" : registration.Climate.Pattern,
                ["sceneXmlCrc"] = map.GetSceneXmlCrc().ToString(CultureInfo.InvariantCulture),
                ["navMeshCrc"] = map.GetSceneNavigationMeshCrc().ToString(CultureInfo.InvariantCulture)
            };
        }

        // Fails closed when a header's package names another world: the header's id, scene and seed must be the package's.
        public static void RequireMatch(IDictionary<string, string> header, GeneratedWorldPackage package)
        {
            string id, scene, seed;
            if (!header.TryGetValue("id", out id) || id != package.WorldId || !header.TryGetValue("scene", out scene) || scene != package.SceneName ||
                !header.TryGetValue("seed", out seed) || seed != package.Seed.ToString(CultureInfo.InvariantCulture))
                throw new System.IO.InvalidDataException("The save header and its world package name different worlds.");
        }
    }
}
