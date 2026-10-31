// Research-only REA runner. Reads installed binaries; never loads the game/editor.
// Arguments: installed rea-agents package path, target DLL path, output directory.
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { writeFile } from 'node:fs/promises';
const [packageRoot, target, output] = process.argv.slice(2);
if (!packageRoot || !target || !output) throw new Error('Expected REA package, target DLL, output directory');
const load = rel => import(pathToFileURL(join(packageRoot, 'dist', rel)).href);
const { parseConfig } = await load('config/parseConfig.js');
const { createBinarySession } = await load('composition/binary.js');
const { silentLogger } = await load('logger.js');
const { createEvidence } = await load('domain/evidence.js');
const config = parseConfig(process.env);
if (!config.ok) throw config.error;
const session = createBinarySession(config.value, silentLogger, process.env);
const observations = [];
const write = async () => {
  await writeFile(join(resolve(output), 'native-handler-observations.json'), JSON.stringify(observations, null, 2));
  await writeFile(join(resolve(output), 'native-handler-rea.json'), JSON.stringify(session.exportEvidenceBundle()));
};
const query = async (operation, parameters) => {
  const r = await session.execute(operation, parameters, {});
  observations.push({ operation, parameters, result: r });
  if (r.ok) {
    const v = r.value;
    const recorded = session.recordEvidence(createEvidence(v.subject, v.provider, {
      operation, parameters, result: v.result, rawResult: v.rawResult,
      analysisProfile: v.analysisProfile, limitations: v.limitations,
      locations: v.locations,
      ...(v.relationshipCoverage === undefined ? {} : { relationshipCoverage: v.relationshipCoverage })
    }));
    if (!recorded.ok) throw recorded.error;
  }
  await write();
  console.log(JSON.stringify({ operation, parameters, ok: r.ok }));
  return r;
};
try {
  const opened = await session.open(target, { providerId: 'ghidra' });
  if (!opened.ok) throw opened.error;
  const functions = new Set();
  for (const address of ['0x181428b80', '0x1814289b8', '0x181428b70', '0x18141b9b0', '0x18141bff0', '0x18141c720']) {
    const refs = await query('xrefs', { address });
    if (!refs.ok) continue;
    for (const ref of refs.value.result) {
      const resolved = await query('resolve_containing_procedure', { address: ref });
      if (resolved.ok) {
        // Preserve every shape exactly; entry extraction uses documented provider fields.
        const value = resolved.value.result;
        const entry = value?.procedure?.address ?? value?.procedure?.entrypoint ?? value?.address ?? value?.entrypoint;
        if (typeof entry === 'string') functions.add(entry);
      }
    }
  }
  // A known xref from the preceding completed REA CLI pass.
  // Procedure decompilation may also resolve containing code; errors stay recorded.
  functions.add('0x180b2a25b');
  for (const procedure of functions) await query('procedure_pseudo_code', { procedure });
} finally {
  await write();
  await session.close();
  console.log('REA session closed');
}
