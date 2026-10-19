// worldbridge --inspect-bundle DIR (MMR-29): validates a world data bundle and prints its identity and summary as
// canonical JSON, without Bannerlord. Exit 0 when the bundle is valid, 2 when it is not.
#include <iostream>
#include "worldbridge/world_bundle.hpp"

namespace worldbridge::bundle {

int inspect_main(int argc, char** argv) {
    if (argc != 3) throw std::runtime_error("Usage: worldbridge --inspect-bundle DIR");
    const fs::path dir = argv[2];
    const Report r = validate(dir);
    cjson::Value out = cjson::Value::object();
    cjson::Value valid; valid.kind = cjson::Value::Kind::Bool; valid.boolean = r.valid;
    out.set("valid", valid);
    cjson::Value problems; problems.kind = cjson::Value::Kind::Array;
    for (const auto& p : r.problems) problems.items.push_back(cjson::Value::string(p));
    out.set("problems", problems);
    out.set("worldContentId", cjson::Value::string(r.world_content_id));
    out.set("manifestSha256", cjson::Value::string(r.manifest_sha256));
    // DATA_READY only for a bundle that validates: a marker beside damaged content is reported as such.
    const bool ready = fs::is_regular_file(dir / "DATA_READY");
    out.set("state", cjson::Value::string(r.valid ? "DATA_READY" : ready ? "sealed-but-invalid" : "unsealed"));
    for (const char* key : {"totals", "source", "compiler", "policies", "engine", "requiredCapabilities", "coordinates", "validation", "retention", "publication"})
        if (const cjson::Value* v = r.manifest.find(key)) out.set(key, *v);
    std::cout << cjson::canonical(out) << std::endl;
    return r.valid ? 0 : 2;
}

}  // namespace worldbridge::bundle
