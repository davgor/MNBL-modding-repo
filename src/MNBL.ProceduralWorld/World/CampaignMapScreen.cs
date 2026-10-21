using System;

namespace MNBL.ProceduralWorld.World
{
    // MMR-247: is a screen the campaign map's? The MapState screen is SandBox.View.Map.MapScreen or a subclass of it: with War Sails loaded
    // (always, GeneratedGameManager requires NavalDLCSubModule) it is NavalDLC.View.Map.NavalMapScreen, which extends MapScreen (installed
    // NavalDLC.View IL: GameStateScreen(typeof(MapState)), its constructor calls MapScreen::.ctor(MapState)). An exact type-name test never
    // matched it, so the atmosphere tick and the ruin hover never ran (acc2: top_screen_changed to=NavalDLC.View.Map.NavalMapScreen).
    // The test walks the screen's base types by name, so this file needs no SandBox.View reference and compiles offline.
    internal static class CampaignMapScreen
    {
        public const string TypeName = "SandBox.View.Map.MapScreen";
        private static Type matched;

        internal static bool Is(object screen)
        {
            if (screen == null) return false;
            var type = screen.GetType();
            if (ReferenceEquals(type, matched)) return true;
            for (var t = type; t != null; t = t.BaseType)
                if (t.FullName == TypeName) { matched = type; return true; }
            return false;
        }
    }
}
