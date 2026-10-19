using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-30: the identity namespaces of a generated campaign (docs/campaign-registry.md). Pure managed.
    //
    // 1. World definition ids (immutable): engine StringIds of generated objects. They come from the compiler (MMR-27:
    //    mnbl_<8 hex world prefix>_<kind letter>_<10 hex>, derived from the source identity and world.json, never from a
    //    display name) or, for the diagnostic fixture, from the seed (mnbl_<seed:x8>_<role>_<n>). Every one starts with its
    //    world prefix "mnbl_<8 hex>_". Different worlds get different prefixes except by a 32-bit hash collision (the same
    //    seed or source is the same world, with the same ids by design); the registry refuses an id already registered and
    //    the census reports any object of another prefix, so a collision is detected rather than merged.
    // 2. Aliases: world objects installed code finds by literal id, so every world re-creates them unprefixed; they are the only
    //    generated ids without a world prefix. The player (main_hero, player_faction, player_party); the installed land bandit clans
    //    (MMR-34: DefaultAlleyModel, HideoutCampaignBehavior, BanditInteractionsCampaignBehavior, DefaultBanditDensityModel,
    //    DesertersCampaignBehavior and others look them up by id, evidence/fwg/behavior-scan.json); the retirement retreat and its
    //    component (RetirementCampaignBehavior finds retirement_retreat by id and dereferences it every hour); the installed pirate clans
    //    (MMR-35: NavalDLCVoiceOverModel compares their ids, the War Sails storyline's party factories look them up (inactive in a Sandbox
    //    campaign), and the map scene's PirateSpawnPoint entities name them).
    // 3. Campaign instance identity (mutable): campaignInstanceId (a UUID, MMR-24) names one playthrough. It is bound to a
    //    world by the activation receipt (MMR-29) and never appears in a StringId, so identical worlds can back separate
    //    campaigns.
    // 4. Save definition ids: SaveableTypeDefiner base and field ids are module-fixed, versioned constants (SaveIds). They
    //    are a different namespace from StringIds and are never derived from a world, seed or campaign.
    internal static class CampaignIdentity
    {
        // Longest generated StringId: a module-chosen bound, not an engine limit (none is known). Installed code derives ids
        // that embed ours, several deep (run 416: "garrison_party_<town>_<clan>_1", "militias_of_militias_of_<village>..."),
        // so ours are kept short. Compiler ids are 26 characters; a settlement component id adds 10.
        public const int MaxIdLength = 48;
        public static readonly IList<string> Aliases = Array.AsReadOnly(new[] { "main_hero", "player_faction", "player_party",
            "looters", "deserters", "sea_raiders", "mountain_bandits", "forest_bandits", "desert_bandits", "steppe_bandits",
            "retirement_retreat", "retirement_component", "northern_pirates", "southern_pirates" });
        private static readonly Regex Prefix = new Regex("^mnbl_[0-9a-f]{8}_", RegexOptions.CultureInvariant);
        private static readonly Regex Embedded = new Regex("mnbl_[0-9a-f]{8}_", RegexOptions.CultureInvariant);
        private static readonly Regex Charset = new Regex("^[a-z0-9_]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Compiler = new Regex("^mnbl_[0-9a-f]{8}_[a-z]_[0-9a-f]{10}$", RegexOptions.CultureInvariant);

        // "mnbl_<8 hex>_" for a world-qualified id, or null.
        public static string WorldPrefix(string id)
        {
            var m = id == null ? Match.Empty : Prefix.Match(id);
            return m.Success ? m.Value : null;
        }

        // The world prefix an id starts with or, for an id the campaign derived from a world object
        // ("garrison_party_mnbl_..._town_1_..."), the first prefix embedded in it; null for neither.
        public static string WorldOrigin(string id)
        {
            var m = id == null ? Match.Empty : Embedded.Match(id);
            return m.Success ? m.Value : null;
        }

        public static string WorldPrefixOfSeed(uint seed) { return "mnbl_" + seed.ToString("x8", CultureInfo.InvariantCulture) + "_"; }

        public static bool IsAlias(string id) { return id != null && Aliases.Contains(id); }

        public static bool IsCompilerId(string id) { return id != null && Compiler.IsMatch(id); }

        // Why a generated id is unacceptable for the world with this prefix, or null when it is acceptable.
        public static string Problem(string id, string worldPrefix)
        {
            if (String.IsNullOrEmpty(id)) return "empty id";
            if (IsAlias(id)) return null;
            if (id.Length > MaxIdLength) return "longer than " + MaxIdLength + " characters";
            if (!Charset.IsMatch(id)) return "not lowercase ASCII letters, digits and underscores";
            string prefix = WorldPrefix(id);
            if (prefix == null) return "not world-qualified (mnbl_<8 hex>_...)";
            if (worldPrefix != null && prefix != worldPrefix) return "belongs to world " + prefix.TrimEnd('_') + ", not " + worldPrefix.TrimEnd('_');
            return null;
        }
    }

    // Module-fixed save definition ids (MMR-30 reserves the namespace; MMR-50 owns the definitions and their migration). A
    // SaveableTypeDefiner gets the base id and numbers its types and containers from it; ids are never reused for another
    // type, never derived from a world, seed or campaign, and a change of a saved type's shape gets a new id plus a migration.
    // The base was chosen clear of every SaveableTypeDefiner base id installed with v1.4.8.119303 and War Sails v1.2.8.119303
    // (scan: evidence/runtime/save-definer-scan.json).
    internal static class SaveIds
    {
        public const int Base = 7_910_000;
        public const int Version = 1;
    }
}
