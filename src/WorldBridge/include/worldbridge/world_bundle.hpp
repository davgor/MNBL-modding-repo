// Immutable world data bundle (MMR-29). One directory per world, named by its WorldContentId:
//
//   source/      the producer split, byte for byte (world.json, scene.json, plans/...)
//   compiled/    terrain/ (heightfield, masks, weights, height-q16, projection.json), campaign/ (id-map, the records after
//                the travel exclusions), travel/ (travel-graph, ports, cells.bin), validation.json (the static report),
//                compatibility.json (game and War Sails versions, required runtime capabilities: part of the identity)
//   policies/    every policy, contract and calibration the compilers read, byte for byte
//   manifest.json  canonical: identity, inventory (path, bytes, sha256), producer, compiler, policies, engine versions,
//                required runtime capabilities, coordinate contract, validation report hash, retention metadata
//   DATA_READY   written last: the manifest hash and WorldContentId. The bundle is published only after it
//
// WorldContentId is identity::world_content over source/, compiled/ and policies/ (mnbl.world-content-id/1). Nothing
// volatile enters the bundle: timings, absolute paths and logs go to a separate diagnostics directory. DATA_READY is
// not activation: the bundle never holds an activation receipt; only U03 writes one, outside the bundle, after
// W02-W05 and C07 (worldgen/contracts/activation-receipt.schema.json).
//
// Publication is a transaction: every file is written and flushed into a fresh staging directory under the root,
// re-read and checked, then the manifest and DATA_READY (flushed), then the whole staging directory is validated as a
// candidate, then one atomic same-volume rename to <root>/<WorldContentId>. The rename itself is journaled but not
// flushed when the call returns: after a power loss either the staging directory or the bundle exists, each complete.
// An existing bundle is never overwritten: identical content with the same description is reused (provenance members
// may differ), anything else is refused. A crash or cancel before the rename leaves only a staging directory, which
// list_ready never reports (MMR-62 cleans abandoned staging).
#pragma once
#include <cstdint>
#include <filesystem>
#include <functional>
#include <map>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/travel_graph.hpp"

namespace worldbridge::bundle {
namespace fs = std::filesystem;

constexpr const char* kStagingPrefix = ".staging-";
// Longest bundle path in bytes. A host root of up to 259 - 65 - 1 - 120 = 73 characters (the LocalAppData store's worlds
// directory for ordinary user names) keeps every file below MAX_PATH, which a .NET Framework host without the long-path
// opt-in needs. publish refuses a root whose bundle paths would not fit (path_too_long) before writing anything.
constexpr std::size_t kMaxBundlePath = 120;
constexpr std::size_t kMaxFilePath = 259, kMaxDirectoryPath = 247;   // MAX_PATH less the NUL; CreateDirectory's limit

// A bundle path: under source/, compiled/ or policies/, single '/' separators, at most kMaxBundlePath bytes, and no segment
// that Windows cannot hold or would rewrite (empty, '.', '..', reserved characters, a trailing dot or space, a DOS device
// name such as CON, NUL, COM1 or LPT1 with or without an extension).
bool safe_bundle_path(const std::string& path);

// Files every world bundle holds (the documented layout); validation refuses a bundle without one of them.
const std::vector<std::string>& required_files();

// Manifest members that record how a bundle was first made (recipe, producer and compiler binaries, runtime lock). They
// may differ when identical content is published again; every other member must be equal for the bundle to be reused.
const std::vector<std::string>& provenance_members();

// What the bundle writer packages: every file by bundle path, plus the manifest members that describe them.
struct Content {
    std::map<std::string, std::string> files;   // "source/...", "compiled/...", "policies/..." -> bytes
    cjson::Value description;                   // manifest members other than identity and inventory (object)
};

// Test hook for publication boundaries: called with the step name and the staging directory before the step's effect
// is final. Throwing SimulatedCrash leaves the staging directory as a killed process would; any other exception is a
// cancel or failure, after which the staging directory is removed.
struct SimulatedCrash : std::runtime_error { using std::runtime_error::runtime_error; };
using StepHook = std::function<void(const std::string& step, const fs::path& staging)>;

struct Published { std::string world_content_id, manifest_sha256; fs::path directory; bool existed = false; };
// Steps: staging, file:<path> (after each file is written and flushed), content-id, verified, manifest, ready,
// validated (the staging directory passed validate as a candidate), rename, published.
Published publish(const Content& content, const fs::path& root, const StepHook& hook = {});

struct Report {
    bool valid = false;
    std::vector<std::string> problems;
    std::string world_content_id, manifest_sha256;
    cjson::Value manifest;
};
// Static validation of a published (or candidate) bundle; problems are reported, never thrown. name_must_match:
// the directory name must equal the WorldContentId (true for published bundles).
Report validate(const fs::path& directory, bool name_must_match = true);
// Sealed bundles under root: directories named by a WorldContentId (64 lowercase hex digits) holding DATA_READY. Listing is
// not validation: a consumer validates (or the host verifies) a bundle before using it.
std::vector<fs::path> list_ready(const fs::path& root);

// The whole compile, in memory, into bundle Content (refuses a rejected travel graph with its quality reasons).
struct Sources {
    fs::path split, contract, producer_lock, runtime_lock;
    travel::Policies policies;
    std::optional<fs::path> recipe;   // the attempt's request.json, when the bundle comes from a generation attempt
};
struct Built { Content content; travel::Result travel; };
Built build(const Sources& sources);

// worldbridge --bundle SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE --content FILE
//   --cultures FILE --playability FILE --producer-lock FILE --runtime-lock FILE [--recipe FILE] --root DIR --diagnostics DIR
int bundle_main(int argc, char** argv);
// worldbridge --inspect-bundle DIR   (validation and a summary, no Bannerlord; exit 0 valid, 2 invalid)
int inspect_main(int argc, char** argv);

}  // namespace worldbridge::bundle
