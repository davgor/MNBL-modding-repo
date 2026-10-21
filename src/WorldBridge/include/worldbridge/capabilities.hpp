// The runtime capabilities a world data bundle requires of its host (manifest.json and compiled/compatibility.json
// requiredCapabilities; docs/world-bundle.md). A host that does not provide one refuses the bundle at WorldDataBundle.Open,
// before anything is activated.
#pragma once

namespace worldbridge::capabilities {

// Map epic 2 (MMR-248): the one capability that covers its three format bumps, cells.bin v2 (MNBLTC02, MMR-253/254),
// weights.bin v2 (MNBLWT02, MMR-255) and campaign.json v2 (MMR-252/279). The managed host's constant is
// WorldDataBundle.FormatsV2Capability (src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs); both must agree.
inline constexpr const char* kFormatsV2 = "R11";
inline constexpr const char* kFormatsV2Name = "map epic 2 formats: cells.bin v2 (MNBLTC02), weights.bin v2 (MNBLWT02), campaign.json v2";

}  // namespace worldbridge::capabilities
