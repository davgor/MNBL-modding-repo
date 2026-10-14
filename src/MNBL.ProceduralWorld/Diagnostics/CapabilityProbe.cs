using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using MNBL.ProceduralWorld.Automation;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // A managed-symbol snapshot only. It never invokes the discovered engine members.
    internal static class CapabilityProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            string operation = context.Str("operation", "inventory");
            context.Outputs["operation"] = operation;
            context.Outputs["runtimeSupportEstablished"] = false;
            context.Outputs["nativeCallsInvoked"] = false;
            context.Outputs["managedThreadId"] = Thread.CurrentThread.ManagedThreadId;
            if (operation != "inventory")
            {
                context.Outputs["outcome"] = "unsupported_operation";
                context.Outputs["reason"] = "This entrypoint inventories loaded symbols only. Engine execution requires a named, isolated probe and its lifecycle prerequisites.";
                context.Outputs["supportedOperations"] = new[] { "inventory" };
                yield break;
            }

            context.Stage("capability_inventory_begin");
            var snapshots = new List<object>();
            snapshots.Add(Inspect("TaleWorlds.Engine.Scene", "Read", "CreateNewScene", "GetTerrainHeight", "GetTerrainHeightAndNormal", "GetTerrainHeightData", "get_HasTerrainHeightmap", "ImportNavigationMeshPrefabWithFrame", "ClearNavMesh", "SetNavMeshRegionMap", "GetNavMeshFaceIndex", "GetPathBetweenAIFaces", "RayCastForClosestEntityOrTerrain"));
            snapshots.Add(Inspect("TaleWorlds.Engine.Mesh", "CreateMesh", "AddTriangle", "ComputeNormals"));
            snapshots.Add(Inspect("TaleWorlds.Engine.GameEntity", "CreateEmpty", "AddMesh", "CookTrianglePhysxMesh", "Remove"));
            snapshots.Add(Inspect("TaleWorlds.CampaignSystem.Map.IMapScene"));
            snapshots.Add(Inspect("TaleWorlds.CampaignSystem.Map.IMapSceneCreator"));
            snapshots.Add(Inspect("SandBox.MapScene", "Load", "GetFaceIndex", "GetPathBetweenAIFaces", "GetTerrainHeightAndNormal"));
            snapshots.Add(Inspect("TaleWorlds.CampaignSystem.ComponentInterfaces.PartyNavigationModel"));
            snapshots.Add(Inspect("TaleWorlds.CampaignSystem.ComponentInterfaces.MapDistanceModel"));
            snapshots.Add(Inspect("TaleWorlds.ObjectSystem.MBObjectManager", "RegisterType", "RegisterObject", "LoadXML", "LoadXml", "ReInitialize"));
            snapshots.Add(Inspect("SandBox.SandBoxGameManager", "OnLoadFinished", "DoLoadingForGameManager"));
            context.Outputs["types"] = snapshots;
            context.Outputs["outcome"] = "inventory_complete";
            context.Outputs["limits"] = new[]
            {
                "A present symbol is not shipping-client runtime support. An absent loaded type is not proof the installation lacks it.",
                "This snapshot does not establish a heightfield setter, native picking, generated routing, periodic topology, cache rebinding or early generated-object restoration.",
                "Assembly hashes identify this loaded managed build; native prerequisites and resource ownership require separate evidence."
            };
            context.Stage("capability_inventory_end");
            yield break;
        }

        private static object Inspect(string typeName, params string[] memberNames)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, false)).Where(type => type != null).ToArray();
            if (matches.Length != 1)
                return new { type = typeName, outcome = matches.Length == 0 ? "type_not_loaded" : "ambiguous_loaded_type", matches = matches.Length };
            Type selected = matches[0];
            var methods = selected.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => memberNames.Length == 0 || memberNames.Contains(method.Name))
                .OrderBy(method => method.ToString(), StringComparer.Ordinal).ToArray();
            string location = selected.Assembly.Location;
            string hash;
            using (var input = File.OpenRead(location))
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            return new
            {
                type = typeName,
                outcome = "loaded_symbols_only",
                assembly = selected.Assembly.FullName,
                assemblyPath = location,
                assemblySha256 = hash,
                missingRequestedMembers = memberNames.Where(name => !methods.Any(method => method.Name == name)).ToArray(),
                methods = methods.Select(method => new
                {
                    declaringType = method.DeclaringType.FullName,
                    name = method.Name,
                    returnType = method.ReturnType.ToString(),
                    parameters = method.GetParameters().Select(parameter => new { name = parameter.Name, type = parameter.ParameterType.ToString() }).ToArray(),
                    isStatic = method.IsStatic,
                    isVirtual = method.IsVirtual,
                    metadataToken = method.MetadataToken
                }).ToArray()
            };
        }
    }
}
