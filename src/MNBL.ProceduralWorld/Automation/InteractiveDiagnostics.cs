using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Automation
{
    // Same checked path as automation, without automatically exiting the game.
    internal static class InteractiveDiagnostics
    {
        private static IEnumerator current;
        private static Action finish;
        internal static bool Busy { get { return current != null; } }

        internal static void Start(string id)
        {
            if (Busy || AutorunHost.Busy) return;
            string directory = Path.Combine(SubModule.Store, "interactive", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ProbeContext context = null;
            Action checkpoint = () => File.WriteAllText(Path.Combine(directory, "result.json"),
                new JavaScriptSerializer().Serialize(context.Outputs));
            context = new ProbeContext(new Dictionary<string,object>(), directory,
                Process.GetCurrentProcess().Id,
                stage => File.AppendAllText(Path.Combine(directory, "progress.log"), DateTime.UtcNow.ToString("O") + " " + stage + "\n"),
                checkpoint);
            context.Outputs["complete"] = false;
            context.Outputs["probe"] = id;
            checkpoint();
            current = AutorunProbes.Create(id, context);
            finish = () => { context.Outputs["complete"] = true; checkpoint(); };
            InformationManager.DisplayMessage(new InformationMessage("Terrain diagnostic: retained campaign content; not a playable generated world."));
        }

        internal static void Tick()
        {
            if (current == null) return;
            try
            {
                if (current.MoveNext()) return;
                finish();
                current = null;
                finish = null;
            }
            catch (Exception error)
            {
                current = null;
                finish = null;
                File.AppendAllText(Path.Combine(SubModule.Store, "module.log"), "Interactive diagnostic failed: " + error + "\n");
                InformationManager.DisplayMessage(new InformationMessage("Terrain diagnostic failed: " + error.Message));
            }
        }
    }
}
