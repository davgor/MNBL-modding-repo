// World data bundle transaction tests (MMR-29): bundle_transaction_tests WORK_DIR
// Publication boundaries under simulated crash (process killed: staging left as is), cancel or failure (staging
// removed) and power loss (a file torn before the kill, or torn inside a bundle whose rename survived): no partial
// bundle is ever published under a bundle name, listed as complete or validated; an earlier bundle stays valid; an
// existing bundle is reused only when it is valid and describes the content the same way, never overwritten; a
// concurrent publisher of the same content reuses the winner's bundle; a root too deep for MAX_PATH and a candidate
// that fails validation are refused before anything is published. Damaged bundles are refused by validate.
// Cross-implementation validation of resealed and linked variants: tools/test_bundle_parity.py.
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <sstream>
#include <string>
#include "worldbridge/world_bundle.hpp"
#include "worldbridge/world_identity.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using namespace worldbridge::bundle;

namespace {
int checks = 0, failures = 0;
fs::path work;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}
template <class E> bool throws(const std::function<void()>& body, const std::string& code = "") {
    try { body(); } catch (const E& e) {
        if constexpr (std::is_same_v<E, json::Error>) return code.empty() || e.code == code;
        else return true;
    } catch (...) { return false; }
    return false;
}
void spit(const fs::path& p, const std::string& text) { fs::create_directories(p.parent_path()); std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }
std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
std::string sha(const std::string& bytes) { return cjson::sha256_hex(bytes); }
cjson::Value text(const std::string& s) { return cjson::Value::string(s); }
cjson::Value parse_text(const std::string& s) { const fs::path file = work / "parse.json"; spit(file, s); return cjson::parse(file); }

// A complete minimal bundle: every required file, travel outputs naming the compiled files beside them, and a
// description with the members validation requires. `flavour` changes the content (and so the WorldContentId).
Content sample(const std::string& flavour) {
    Content c;
    auto& f = c.files;
    // One FWGC++ city that is one town at every stage (the city preservation check).
    f["source/world.json"] = "{\"world\":\"" + flavour + "\",\"settlements\":{\"founding\":{\"placed_cities\":1},\"sites\":[{\"id\":0,\"uid\":\"city-0\"}]}}";
    f["source/plans/city/a-2.json"] = "{\"plan\":1}";
    f["source/request.json"] = "{\"seed\":42,\"recipe_version\":3,\"overrides\":{\"size\":129,\"phase\":16}}";   // MMR-261
    f["compiled/terrain/heightfield.bin"] = std::string(1024, '\x7f') + flavour;
    f["compiled/terrain/masks.bin"] = std::string(512, '\x01');
    f["compiled/terrain/weights.bin"] = std::string(128, '\x02');
    f["compiled/terrain/height-q16.bin"] = std::string(256, '\x03');
    f["compiled/terrain/projection.json"] = "{\"projection\":1}";
    f["compiled/terrain/navigation-heightfield.bin"] = std::string(512, '\x7e') + flavour;   // the travel compile (MMR-278)
    f["compiled/terrain/navigation-masks.bin"] = std::string(256, '\x05');
    f["compiled/terrain/shaping.json"] = "{\"schema\":\"mnbl.terrain-shaping-record\"}";
    f["compiled/terrain/flora.bin"] = std::string("FLR2\x04\0\0\0\0\0\0\0", 12);   // map trees (MMR-255): none
    f["compiled/campaign/id-map.json"] = "{\"entries\":[{\"id\":\"t\",\"kind\":\"town\",\"uid\":\"city-0\"}]}";
    f["compiled/campaign/campaign.json"] = "{\"towns\":[{\"id\":\"t\",\"uid\":\"city-0\"}]}";
    f["compiled/travel/cells.bin"] = std::string(64, '\x04');
    const std::string inputs = "{\"idMapSha256\":\"" + sha(f["compiled/campaign/id-map.json"]) + "\",\"projectionSha256\":\"" + sha(f["compiled/terrain/projection.json"]) +
                               "\",\"terrainHeightfieldSha256\":\"" + sha(f["compiled/terrain/navigation-heightfield.bin"]) + "\",\"terrainMasksSha256\":\"" + sha(f["compiled/terrain/navigation-masks.bin"]) + "\"}";
    f["compiled/travel/travel-graph.json"] = "{\"grid\":{\"cellsSha256\":\"" + sha(f["compiled/travel/cells.bin"]) + "\"},\"inputs\":" + inputs +
                                             ",\"settlements\":[{\"id\":\"t\",\"kind\":\"town\",\"uid\":\"city-0\"}],\"status\":\"ok\"}";
    f["compiled/travel/ports.json"] = "{\"inputs\":" + inputs + "}";
    f["compiled/validation.json"] = "{\"schema\":\"mnbl.bundle-validation\",\"travelStatus\":\"ok\"}";
    const std::string capabilities = "[{\"id\":\"R02\",\"name\":\"runtime terrain\"}]";
    f["compiled/compatibility.json"] = "{\"engine\":{\"gameVersion\":\"v1.4.8.119303\",\"navalVersion\":\"v1.2.8.119303\"},\"requiredCapabilities\":" + capabilities +
                                       ",\"schema\":\"mnbl.bundle-compatibility\",\"version\":1}";
    f["policies/playability.json"] = "{\"policy\":true}";
    c.description = parse_text("{\"compiler\":{\"binarySha256\":\"first\"},\"engine\":{\"gameVersion\":\"v1.4.8.119303\",\"navalVersion\":\"v1.2.8.119303\",\"runtimeLockSha256\":\"first\"},"
                                      "\"requiredCapabilities\":" + capabilities + ",\"retention\":{\"kind\":\"world-data-bundle\"},\"source\":{\"recipe\":null,\"worldSha256\":\"w\"}}");
    cjson::Value policies = cjson::Value::object();
    policies.set("playability", text(sha(f["policies/playability.json"])));
    c.description.set("policies", policies);
    cjson::Value validation = cjson::Value::object();
    validation.set("reportPath", text("compiled/validation.json"));
    validation.set("reportSha256", text(sha(f["compiled/validation.json"])));
    validation.set("travelStatus", text("ok"));
    c.description.set("validation", validation);
    return c;
}
std::vector<fs::path> staging_dirs(const fs::path& root) {
    std::vector<fs::path> out;
    if (!fs::is_directory(root)) return out;
    for (const auto& e : fs::directory_iterator(root))
        if (e.is_directory() && fs::path(e.path()).filename().string().rfind(kStagingPrefix, 0) == 0) out.push_back(e.path());
    return out;
}
std::string tree_digest(const fs::path& dir) {
    std::string out;
    std::vector<std::string> lines;
    for (const auto& e : fs::recursive_directory_iterator(dir))
        if (e.is_regular_file()) lines.push_back(e.path().lexically_relative(dir).generic_string() + " " + cjson::sha256_file_hex(e.path()));
    std::sort(lines.begin(), lines.end());
    for (const auto& l : lines) out += l + "\n";
    return out;
}
fs::path copy_of(const fs::path& bundle, const std::string& name) {
    const fs::path dir = work / "tamper" / name / bundle.filename();
    fs::remove_all(dir.parent_path());
    fs::create_directories(dir.parent_path());
    fs::copy(bundle, dir, fs::copy_options::recursive);
    return dir;
}
bool has_problem(const Report& r, const std::string& part) {
    return std::any_of(r.problems.begin(), r.problems.end(), [&](const std::string& p) { return p.find(part) != std::string::npos; });
}
std::string first_problem(const Report& r) { return r.problems.empty() ? "" : ": " + r.problems.front(); }

void publication_tests() {
    const fs::path root = work / "root";
    fs::remove_all(root);
    // A first bundle.
    const Content a = sample("a");
    const Published pa = publish(a, root);
    const Report ra = validate(pa.directory);
    check(ra.valid && !pa.existed && pa.directory.filename().string() == pa.world_content_id && list_ready(root) == std::vector<fs::path>{pa.directory} && staging_dirs(root).empty(),
          "a bundle publishes into <root>/<worldContentId>, validates, is listed, and leaves no staging" + first_problem(ra));
    check(pa.world_content_id == identity::world_content({{"source", pa.directory / "source"}, {"compiled", pa.directory / "compiled"}, {"policies", pa.directory / "policies"}}).id,
          "the WorldContentId is identity::world_content over source/, compiled/ and policies/");
    check(ra.manifest.find("inventory")->items.size() == a.files.size() && slurp(pa.directory / "DATA_READY").find(pa.manifest_sha256) != std::string::npos,
          "the manifest lists every file and DATA_READY names the manifest hash");
    check(validate(pa.directory.wstring() + L"\\").valid, "a bundle named with a trailing separator validates");
    // Publishing the same content again reuses the bundle untouched, even when only provenance differs.
    const std::string before = tree_digest(pa.directory);
    const Published again = publish(sample("a"), root);
    Content rebuilt = sample("a");
    rebuilt.description.set("compiler", parse_text("{\"binarySha256\":\"another build\"}"));
    for (auto& [key, value] : rebuilt.description.members) if (key == "source") value.set("recipe", parse_text("{\"sha256\":\"r\",\"seed\":7}"));
    const Published reprovenanced = publish(rebuilt, root);
    check(again.existed && again.directory == pa.directory && reprovenanced.existed && reprovenanced.manifest_sha256 == pa.manifest_sha256 && tree_digest(pa.directory) == before && staging_dirs(root).empty(),
          "identical content reuses the published bundle and its first manifest, also from another build or recipe (provenance), and never rewrites it");
    Content redescribed = sample("a");
    redescribed.description.set("retention", parse_text("{\"kind\":\"something else\"}"));
    bool named = false;
    try { publish(redescribed, root); } catch (const json::Error& e) { named = e.code == "bundle_conflict" && e.message.find("retention") != std::string::npos; }
    check(named && tree_digest(pa.directory) == before && staging_dirs(root).empty(), "the same content described differently (not provenance) is refused naming the member, and the bundle is untouched");

    // Every boundary of a second publication: crash, cancel and power loss expose nothing.
    const Content b = sample("b");
    std::vector<std::string> steps{"staging"};
    for (const auto& [path, bytes] : b.files) steps.push_back("file:" + path);
    for (const char* s : {"content-id", "verified", "manifest", "ready", "validated", "rename"}) steps.push_back(s);
    bool crash_ok = true, cancel_ok = true, power_ok = true;
    std::string crash_detail;
    for (const auto& at : steps) {
        // Crash: the process dies here; staging stays, nothing new is listed, the first bundle is intact. The staging left
        // behind is an incomplete candidate before DATA_READY is flushed and a complete one after, never under a bundle name.
        const bool crashed = throws<SimulatedCrash>([&] { publish(b, root, [&](const std::string& s, const fs::path&) { if (s == at) throw SimulatedCrash("kill at " + at); }); });
        const auto leftover = staging_dirs(root);
        const bool sealed = at == "ready" || at == "validated" || at == "rename";
        const bool ok = crashed && list_ready(root) == std::vector<fs::path>{pa.directory} && validate(pa.directory).valid && leftover.size() == 1 &&
                        validate(leftover[0], false).valid == sealed && !validate(leftover[0]).valid;
        if (!ok && crash_detail.empty()) crash_detail = " (first failure at " + at + ")";
        crash_ok = crash_ok && ok;
        for (const auto& l : leftover) fs::remove_all(l);
        // Cancel or failure: staging is removed, nothing published.
        const bool cancelled = throws<std::runtime_error>([&] { publish(b, root, [&](const std::string& s, const fs::path&) { if (s == at) throw std::runtime_error("cancel at " + at); }); });
        cancel_ok = cancel_ok && cancelled && staging_dirs(root).empty() && list_ready(root) == std::vector<fs::path>{pa.directory};
        // Power loss before the rename: a staged file is torn, then the process dies. The candidate never validates.
        if (at == "file:compiled/terrain/heightfield.bin" || at == "manifest" || at == "ready" || at == "validated" || at == "rename") {
            throws<SimulatedCrash>([&] { publish(b, root, [&](const std::string& s, const fs::path& staging) {
                if (s != at) return;
                std::ofstream torn(staging / "compiled/terrain/heightfield.bin", std::ios::binary | std::ios::trunc);
                torn << "partial";
                throw SimulatedCrash("power loss at " + at);
            }); });
            const auto torn = staging_dirs(root);
            power_ok = power_ok && list_ready(root) == std::vector<fs::path>{pa.directory} && torn.size() == 1 && !validate(torn[0], false).valid;
            for (const auto& l : torn) fs::remove_all(l);
        }
    }
    check(crash_ok, "a crash at each of " + std::to_string(steps.size()) + " boundaries before the rename publishes nothing; the earlier bundle stays valid and listed; the staging is complete only after DATA_READY" + crash_detail);
    check(cancel_ok, "a cancel at each boundary removes the staging and publishes nothing");
    check(power_ok, "a torn staged file before a crash (after DATA_READY too) never validates, even as a candidate, and is never listed");
    // A crash just after the rename: the bundle is complete, because DATA_READY was flushed before it.
    fs::path published_b;
    throws<SimulatedCrash>([&] { publish(b, root, [&](const std::string& s, const fs::path& where) { if (s == "published") { published_b = where; throw SimulatedCrash("kill after rename"); } }); });
    check(!published_b.empty() && validate(published_b).valid && list_ready(root).size() == 2 && validate(pa.directory).valid, "a crash after the rename leaves a complete, valid bundle beside the earlier one");
    // Power loss after the rename reached the disk but a file's data did not: the bundle is listed as sealed, but it never
    // validates, so nothing (host verification, activation) ever uses it.
    const fs::path torn_published = copy_of(published_b, "torn-after-rename");
    spit(torn_published / "compiled/terrain/masks.bin", std::string(512, '\0').substr(0, 100));
    check(!validate(torn_published).valid && has_problem(validate(torn_published), "masks.bin"), "a bundle whose data was torn after the rename never validates");

    // A concurrent publisher of the same content wins the rename: this publish reuses its bundle.
    const Content d = sample("d");
    Published inner, outer;
    outer = publish(d, root, [&](const std::string& s, const fs::path&) { if (s == "rename" && inner.directory.empty()) inner = publish(d, root); });
    check(!inner.existed && outer.existed && outer.directory == inner.directory && validate(outer.directory).valid && staging_dirs(root).empty(),
          "a publish that loses the rename to a concurrent publisher of the same content reuses the winner's bundle");

    // An existing bundle that no longer validates is never overwritten.
    const Content c = sample("c");
    const Published pc = publish(c, root);
    spit(pc.directory / "compiled/terrain/heightfield.bin", "tampered");
    const std::string tampered = tree_digest(pc.directory);
    check(throws<json::Error>([&] { publish(c, root); }, "bundle_conflict") && tree_digest(pc.directory) == tampered && staging_dirs(root).empty(),
          "republishing over a damaged bundle is refused (bundle_conflict) and leaves it untouched");

    // A candidate that its own validation refuses is never published (a description naming a wrong policy hash).
    Content wrong = sample("w");
    wrong.description.set("policies", parse_text("{\"playability\":\"" + std::string(64, '0') + "\"}"));
    const auto listed = list_ready(root);
    check(throws<json::Error>([&] { publish(wrong, root); }, "staging_invalid") && list_ready(root) == listed && staging_dirs(root).empty(),
          "a staged bundle that fails validation is refused (staging_invalid) and nothing is published");
    // City preservation (MMR-251): a bundle whose compiled towns lost a FWGC++ city, or whose split reports another placed_cities, is refused.
    Content lost = sample("lost");
    lost.files["compiled/campaign/campaign.json"] = "{\"towns\":[]}";
    bool lost_named = false;
    try { publish(lost, root); } catch (const json::Error& e) { lost_named = e.code == "staging_invalid" && e.message.find("city preservation") != std::string::npos; }
    Content miscounted = sample("miscounted");
    miscounted.files["source/world.json"] = "{\"settlements\":{\"founding\":{\"placed_cities\":2},\"sites\":[{\"id\":0,\"uid\":\"city-0\"}]}}";
    check(lost_named && throws<json::Error>([&] { publish(miscounted, root); }, "staging_invalid") && list_ready(root) == listed && staging_dirs(root).empty(),
          "a bundle that drops a FWGC++ city from its compiled towns, or whose placed_cities differs from its sites, fails city preservation and is never published");
    // A root whose bundle paths would exceed MAX_PATH is refused before anything is written.
    const fs::path deep = work / "deep" / std::string(200, 'd');
    check(throws<json::Error>([&] { publish(sample("x"), deep); }, "path_too_long") && !fs::exists(work / "deep"), "a root too deep for MAX_PATH is refused before anything is written");

    // Content paths are checked before anything is written.
    for (const auto& bad : {std::string("compiled/../x"), std::string("manifest.json"), std::string("other/x"), std::string("compiled\\x"), std::string("compiled/a//b"),
                            std::string("compiled/a?b"), std::string("compiled/trailing."), std::string("compiled/con.json"), std::string("compiled/x/LPT1"), std::string("compiled/Aux"),
                            "compiled/" + std::string(kMaxBundlePath, 'x')}) {
        Content x = sample("x"); x.files[bad] = "y";
        check(throws<json::Error>([&] { publish(x, root); }, "invalid_path") && staging_dirs(root).empty(), "unsafe bundle path refused before writing: " + bad.substr(0, 40));
    }
    Content reserved = sample("r"); reserved.description.set("worldContentId", text("x"));
    check(throws<json::Error>([&] { publish(reserved, root); }, "invalid_value"), "the description cannot set the identity or inventory");
}

void validation_tests() {
    const fs::path root = work / "root-v";
    fs::remove_all(root);
    const Published p = publish(sample("v"), root);
    auto tamper = [&](const std::string& name, const std::function<void(const fs::path&)>& change, const std::string& expect) {
        const fs::path dir = copy_of(p.directory, name);
        change(dir);
        const Report r = validate(dir);
        check(!r.valid && has_problem(r, expect), "validation refuses " + name + " (" + expect + ")" + (has_problem(r, expect) ? "" : first_problem(r)));
    };
    tamper("missing file", [](const fs::path& d) { fs::remove(d / "source/plans/city/a-2.json"); }, "missing file");
    tamper("extra file", [](const fs::path& d) { spit(d / "compiled/extra.bin", "x"); }, "unlisted file");
    tamper("edited file", [](const fs::path& d) {
        std::string t = slurp(d / "source/world.json");
        const auto at = t.find("\"world\":\"") + 9;
        t[at] = t[at] == 'V' ? 'W' : 'V';
        spit(d / "source/world.json", t);
    }, "hash differs");
    tamper("resized file", [](const fs::path& d) { spit(d / "compiled/terrain/heightfield.bin", "short"); }, "size differs");
    tamper("an edited manifest", [](const fs::path& d) { std::string m = slurp(d / "manifest.json"); m.insert(m.size() - 1, ",\"note\":\"x\""); spit(d / "manifest.json", m); }, "DATA_READY names a different manifest");
    tamper("an unsealed bundle", [](const fs::path& d) { fs::remove(d / "DATA_READY"); }, "never sealed");
    tamper("an empty DATA_READY", [](const fs::path& d) { spit(d / "DATA_READY", ""); }, "DATA_READY");
    tamper("an activation receipt inside", [](const fs::path& d) { spit(d / "activation.receipt.json", "{}"); }, "activation receipts never live in a bundle");
    tamper("an edited policy", [](const fs::path& d) { spit(d / "policies/playability.json", "{\"policy\":false}"); }, "differs: policies/playability.json");
    const fs::path renamed = work / "tamper" / "renamed" / std::string(64, '0');
    fs::remove_all(renamed.parent_path()); fs::create_directories(renamed.parent_path());
    fs::copy(p.directory, renamed, fs::copy_options::recursive);
    check(!validate(renamed).valid && has_problem(validate(renamed), "directory name") && validate(renamed, false).valid,
          "a bundle under another name is refused as published, valid as a candidate");
    check(!validate(work / "nowhere").valid, "a missing directory is not a bundle");
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) { std::cerr << "usage: bundle_transaction_tests WORK_DIR\n"; return 2; }
    work = fs::absolute(argv[1]);
    fs::create_directories(work);
    try { publication_tests(); validation_tests(); }
    catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
