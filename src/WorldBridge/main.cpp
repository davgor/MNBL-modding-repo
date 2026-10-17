// Bounded, streaming ingestion of the pinned FWGC split root. No engine calls.
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <limits>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include "surface.hpp"
#include "worldbridge/json.hpp"
#include "worldbridge/protocol.hpp"
#include "worldbridge/source_world.hpp"
#include "worldbridge/projected_world.hpp"
#include "worldbridge/terrain_compiler.hpp"
#include "worldbridge/campaign_compiler.hpp"
#include "worldbridge/travel_graph.hpp"
#include "worldbridge/world_bundle.hpp"
#include "worldbridge/attempt_schedule.hpp"
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>

namespace fs = std::filesystem;
static_assert(std::endian::native == std::endian::little && sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
              "Diagnostic heightfield encoding requires little-endian IEEE binary64");
using namespace worldbridge_json;

struct WorldCheck {
    std::map<std::string, std::string> metadata;
    std::set<std::string> uids;
    size_t height_cells = 0, row = 0, column = 0, side = 0;
    double first = 0, last = 0;
    bool row_seen = false;
    static constexpr size_t maximum_side = 1025;
    void container(const Path& p, bool array) {
        if (p.size() >= 2 && p[0] == "layers" && p[1] == "height")
            if (!array || p.size() > 3) throw std::runtime_error("Height must be an array of numeric row arrays");
    }
    void scalar(const Path& p, const std::string& value, bool quoted) {
        if (p.empty()) return;
        const std::string key = p.size() > 1 ? p[0] + "." + p[1] : p[0];
        if ((p.size() == 1 && (key == "schema" || key == "schema_version" || key == "completed_stage")) ||
            (p.size() == 2 && (key == "config.size" || key == "config.seed" || key == "split.version" || key == "split.scene" || key == "seeds.layout" || key == "topology.type" || key == "topology.duplicate_seam" || key == "settlements.version" || key == "humans.version" || key == "heroes.version"))) {
            const bool string_required = key == "schema" || key == "split.scene" || key == "seeds.layout" || key == "topology.type";
            if (quoted != string_required) throw std::runtime_error("Wrong metadata scalar type: " + key);
            metadata[key] = value;
            if (key == "config.size") {
                if (value.find_first_not_of("0123456789") != std::string::npos) throw std::runtime_error("Invalid grid size");
                side = static_cast<size_t>(std::stoul(value));
                if (side < 3 || side > maximum_side || side % 2 == 0) throw std::runtime_error("Unsupported grid size");
            }
        }
        if (p.size() >= 2 && key == "layers.height") {
            if (p.size() != 4 || quoted || side == 0) throw std::runtime_error("Height must be a numeric matrix after config");
            const size_t r = std::stoul(p[2]), c = std::stoul(p[3]);
            if (r >= side || c >= side || r != height_cells / side || c != height_cells % side) throw std::runtime_error("Ragged or oversized height matrix");
            if (row_seen && r != row && std::abs(first - last) > 1e-6) throw std::runtime_error("Height seam mismatch");
            const double n = std::stod(value);
            if (c == 0) first = n;
            if ((r == 0 || r == side - 1) && std::abs(first - n) > 1e-6) throw std::runtime_error("Pole height identity mismatch");
            last = n; row = r; column = c; row_seen = true; ++height_cells;
        }
        if (p.back() == "uid" && p.size() >= 3 && (p[0] == "settlements" || p[0] == "humans")) {
            // UIDs can recur as references; preserve source strings without inferring IDs from names.
            if (!quoted || value.empty()) throw std::runtime_error("Invalid source UID");
            if (uids.size() > 100000) throw std::runtime_error("UID inventory bound exceeded");
            uids.insert(value);
        }
    }
    void finish(const fs::path& source) {
        for (const auto& pair : std::map<std::string,std::string>{{"schema","fwg.world"},{"schema_version","2"},{"completed_stage","16"},{"split.version","1"},{"topology.type","sphere"},{"topology.duplicate_seam","true"},{"settlements.version","16"},{"humans.version","10"},{"heroes.version","6"}})
            if (metadata[pair.first] != pair.second) throw std::runtime_error("Unsupported or missing contract: " + pair.first + " = " + metadata[pair.first]);
        if (!side || height_cells != side * side || column != side - 1 || std::abs(first-last) > 1e-6) throw std::runtime_error("Incomplete or discontinuous height grid");
        const auto seed = metadata["seeds.layout"];
        if (seed.empty() || seed.find_first_not_of("0123456789") != std::string::npos) throw std::runtime_error("Missing exact layout seed string");
        (void)std::stoull(seed);
        const fs::path scene(metadata["split.scene"]);
        if (scene.empty() || scene.is_absolute() || scene.has_root_name()) throw std::runtime_error("Invalid split scene path");
        for (const auto& part : scene) if (part == "..") throw std::runtime_error("Split scene traversal");
        const auto resolved = fs::weakly_canonical(source / scene);
        const auto relative = resolved.lexically_relative(fs::canonical(source));
        if (relative.empty() || *relative.begin() == ".." || !fs::is_regular_file(resolved)) throw std::runtime_error("Split scene missing or outside source root");
    }
};

// worldbridge --read-source SPLIT --contract FILE [--layers a,b] [--report FILE] [--identity FILE]: streams a
// split export through the bounded source reader and reports what it read plus the process's peak memory.
static int read_source_main(int argc, char** argv) {
    const char* usage = "Usage: worldbridge --read-source SPLIT --contract FILE [--layers a,b] [--report FILE] [--identity FILE]";
    std::map<std::string, std::string> options;
    if (argc % 2 != 1) throw std::runtime_error(usage);
    for (int i = 3; i + 1 < argc; i += 2) {
        const std::string key = argv[i];
        if (key != "--contract" && key != "--layers" && key != "--report" && key != "--identity") throw std::runtime_error("Unknown option " + key + "\n" + usage);
        if (!options.emplace(key, argv[i + 1]).second) throw std::runtime_error("Repeated option " + key);
    }
    if (!options.count("--contract")) throw std::runtime_error(usage);
    const auto contract = worldbridge::source::load_compatibility(options["--contract"]);
    const auto world = worldbridge::source::read_source(argv[2], contract);
    if (options.count("--identity")) {
        std::ofstream identity(options["--identity"], std::ios::binary);
        identity << worldbridge::source::write_identity(world.metadata) << std::endl;
        if (!identity) throw std::runtime_error("Cannot write identity file " + options["--identity"]);
    }
    std::ostringstream layers;
    layers.precision(17);
    std::stringstream names(options.count("--layers") ? options["--layers"] : "");
    std::string name; bool first = true;
    while (std::getline(names, name, ',')) {
        if (name.empty()) continue;
        const auto layer = world.read_layer(name);
        double sum = 0; for (double v : layer.values) sum += v;
        layers << (first ? "" : ",") << "{\"name\":" << escape(name) << ",\"side\":" << layer.side << ",\"samples\":" << layer.values.size()
               << ",\"sum\":" << sum << "}";
        first = false;
    }
    std::size_t harbour = 0; for (const auto& h : world.hamlets) if (h.harbour) ++harbour;
    std::size_t planned[4] = {0, 0, 0, 0};
    for (const auto& c : world.cities) planned[0] += !c.plan_path.empty();
    for (const auto& h : world.hamlets) planned[1] += !h.plan_path.empty();
    for (const auto& c : world.castles) planned[2] += !c.plan_path.empty();
    for (const auto& k : world.key_locations) planned[3] += !k.plan_path.empty();
    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory));
    const auto& m = world.metadata; const auto& st = world.statistics;
    std::ostringstream out;
    out.precision(17);
    out << "{\"schema\":\"mnbl.source-read\",\"version\":1,\"root\":" << escape(worldbridge::json::utf8(world.root))
        << ",\"metadata\":{\"schema\":" << escape(m.schema) << ",\"schemaVersion\":" << m.schema_version << ",\"completedStage\":" << m.completed_stage
        << ",\"generatorVersion\":" << escape(m.generator_version) << ",\"splitVersion\":" << m.split_version << ",\"sceneVersion\":" << m.scene_version
        << ",\"gridSide\":" << m.grid_side << ",\"layoutSeed\":" << escape(m.layout_seed) << ",\"systems\":" << m.systems.size() << "}"
        << ",\"blockVersions\":{";
    first = true;
    for (const auto& [block, v] : m.block_versions) { out << (first ? "" : ",") << escape(block) << ":" << v; first = false; }
    out << "},\"records\":{\"cities\":" << world.cities.size() << ",\"hamlets\":" << world.hamlets.size() << ",\"harbourHamlets\":" << harbour
        << ",\"castles\":" << world.castles.size() << ",\"keyLocations\":" << world.key_locations.size() << "}"
        << ",\"plans\":{\"files\":" << world.plans.size() << ",\"bytes\":" << st.plan_bytes << ",\"citiesWithPlan\":" << planned[0]
        << ",\"hamletsWithPlan\":" << planned[1] << ",\"castlesWithPlan\":" << planned[2] << ",\"keyLocationsWithPlan\":" << planned[3] << "}"
        << ",\"layers\":{\"validated\":" << st.layers << ",\"samplesValidated\":" << st.layer_samples << ",\"materialized\":[" << layers.str() << "]}"
        << ",\"bytes\":{\"root\":" << st.root_bytes << ",\"scene\":" << st.scene_bytes << ",\"plans\":" << st.plan_bytes << ",\"retained\":" << st.retained_bytes << "}"
        << ",\"seconds\":" << st.seconds
        << ",\"memory\":{\"peakWorkingSetBytes\":" << memory.PeakWorkingSetSize << ",\"privateBytes\":" << memory.PrivateUsage
        << ",\"peakPagefileBytes\":" << memory.PeakPagefileUsage << "}}";
    if (options.count("--report")) { std::ofstream report(options["--report"], std::ios::binary); report << out.str(); }
    std::cout << out.str() << std::endl;
    return 0;
}

static void self_test() {
    const Scalar noop = [](const Path&, const std::string&, bool) {};
    for (const std::string& text : std::vector<std::string>{"{\"a\":[1,-2.5e+3,true,false,null,\"\\uD83D\\uDE00\"]}", "0", "[{},[]]"}) { std::istringstream in(text); Reader(in, noop).parse(); }
    for (const std::string& text : std::vector<std::string>{"{\"a\":1,\"a\":2}","{\"a\":1,\"\\u0061\":2}","[1,]","01","1e","NaN","1e999","\"\\uD800\"","\"\\uDC00\"","{} true", "{", std::string(65,'[') + "0" + std::string(65,']'), "\"" + std::string(1048577,'a') + "\""}) {
        bool refused = false; try { std::istringstream in(text); Reader(in,noop).parse(); } catch (const std::exception&) { refused = true; }
        if (!refused) throw std::runtime_error("Malformed fixture accepted");
    }
    std::cout << "PASS: valid JSON, duplicate/escaped keys, malformed numbers, Unicode, truncation, token/depth bounds\n";
}

int main(int argc, char** argv) {
    try {
        if (argc > 1 && (std::string(argv[1]) == "--produce" || std::string(argv[1]) == "--preflight")) {
            const auto args = worldbridge::utf8_arguments();
            const std::vector<std::string> command(args.begin() + 1, args.end());
            return command[0] == "--produce" ? worldbridge::produce_main(command) : worldbridge::preflight_main(command);
        }
        if (argc > 1 && std::string(argv[1]).starts_with("--surface-")) return surface_main(argc, argv);
        if (argc > 1 && (std::string(argv[1]) == "--generate" || std::string(argv[1]) == "--new-request")) {
            const auto args = worldbridge::utf8_arguments();
            const std::vector<std::string> command(args.begin() + 1, args.end());
            try {
                return command[0] == "--generate" ? worldbridge::schedule::generate_main(command) : worldbridge::schedule::new_request_main(command);
            } catch (const worldbridge::json::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            } catch (const worldbridge::schedule::WorkCollision& error) {
                // Safe to retry: the attempt stays running and the next invocation reruns it in a new run directory.
                std::cerr << "{\"refused\":true,\"code\":\"work_collision\",\"retryable\":true,\"message\":" << escape(error.what()) << "}" << std::endl;
                return 2;
            }
        }
        if (argc > 2 && std::string(argv[1]) == "--read-source") {
            try { return read_source_main(argc, argv); }
            catch (const worldbridge::source::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"offset\":" << error.where.offset << ",\"line\":" << error.where.line
                          << ",\"column\":" << error.where.column << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            }
        }
        if (argc > 2 && (std::string(argv[1]) == "--bundle" || std::string(argv[1]) == "--inspect-bundle")) {
            try { return std::string(argv[1]) == "--bundle" ? worldbridge::bundle::bundle_main(argc, argv) : worldbridge::bundle::inspect_main(argc, argv); }
            catch (const worldbridge::json::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            }
        }
        if (argc > 2 && std::string(argv[1]) == "--compile-travel") {
            try { return worldbridge::travel::compile_travel_main(argc, argv); }
            catch (const worldbridge::json::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            }
        }
        if (argc > 2 && std::string(argv[1]) == "--compile-campaign") {
            try { return worldbridge::campaign::compile_campaign_main(argc, argv); }
            catch (const worldbridge::json::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            }
        }
        if (argc > 2 && std::string(argv[1]) == "--compile-terrain") {
            try { return worldbridge::terrain::compile_terrain_main(argc, argv); }
            catch (const worldbridge::json::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            }
        }
        if (argc > 2 && std::string(argv[1]) == "--project") {
            try { return worldbridge::projection::project_main(argc, argv); }
            catch (const worldbridge::json::Error& error) {
                std::cerr << "{\"refused\":true,\"code\":" << escape(error.code) << ",\"message\":" << escape(error.message)
                          << ",\"file\":" << escape(error.where.file) << ",\"path\":" << escape(error.where.path) << "}" << std::endl;
                return 2;
            }
        }
        if (argc == 2 && std::string(argv[1]) == "--self-test") { self_test(); return 0; }
        if (argc != 5 || std::string(argv[1]) != "--source" || std::string(argv[3]) != "--out") throw std::runtime_error("Usage: worldbridge --source SPLIT_DIRECTORY --out NEW_DIRECTORY");
        const fs::path source = fs::canonical(argv[2]), output = fs::absolute(argv[4]);
        if (fs::exists(output)) throw std::runtime_error("Output must not already exist");
        fs::create_directories(output);
        std::ifstream input(source / "world.json", std::ios::binary);
        if (!input) throw std::runtime_error("Cannot open split world.json");
        WorldCheck check;
        // A bounded, lossless diagnostic grid for runtime surface experiments.
        // It becomes readable only after the complete source contract passes.
        std::ofstream heights(output / "heightfield.pending", std::ios::binary);
        heights.exceptions(std::ios::badbit | std::ios::failbit);
        heights.write("MNBLHF01", 8);
        const uint32_t pending_side = 0;
        heights.write(reinterpret_cast<const char*>(&pending_side), sizeof(pending_side));
        Reader reader(input, [&](const Path& p, const std::string& v, bool q) {
            check.scalar(p,v,q);
            if (p.size() == 4 && p[0] == "layers" && p[1] == "height") {
                const double height = std::stod(v);
                heights.write(reinterpret_cast<const char*>(&height), sizeof(height));
            }
        }, [&](const Path& p, bool array) { check.container(p,array); });
        reader.split(output, {"schema","schema_version","config","effective_config","topology","seeds","layers","settlements","humans","heroes","roads","split"});
        check.finish(source);
        heights.seekp(8);
        const auto grid_side = static_cast<uint32_t>(check.side);
        heights.write(reinterpret_cast<const char*>(&grid_side), sizeof(grid_side));
        heights.close();
        fs::rename(output / "heightfield.pending", output / "heightfield.bin");
        std::ofstream report(output / "source-validation.json");
        report.exceptions(std::ios::badbit | std::ios::failbit);
        report << "{\"schema\":\"mnbl.source-validation\",\"version\":1,\"gridSide\":" << check.side
               << ",\"heightCells\":" << check.height_cells << ",\"layoutSeed\":\"" << check.metadata["seeds.layout"]
               << "\",\"sourceUidCount\":" << check.uids.size()
               << ",\"scope\":\"selected root streams; scene and plans not ingested\",\"campaignReady\":false}";
        report.close();
        std::cout << "Source streams validated: " << check.height_cells << " height cells. Campaign compilation remains pending.\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 2; }
}
