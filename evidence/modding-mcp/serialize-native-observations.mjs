// Serialize already-retained raw REA replies with REA's own Evidence constructor.
// No target is opened, executed, or analyzed. Original observations remain untouched.
import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';
const [packageRoot, output] = process.argv.slice(2);
const load = rel => import(pathToFileURL(join(packageRoot, 'dist', rel)).href);
const { createEvidence } = await load('domain/evidence.js');
const { InvestigationRecords } = await load('application/investigation/InvestigationRecords.js');
const records = new InvestigationRecords();
const observations = JSON.parse(await readFile(join(output, 'native-handler-observations.json'), 'utf8'));
for (const { operation, parameters, result: r } of observations) {
  if (!r.ok) continue;
  const v = r.value;
  const recorded = records.recordEvidence(createEvidence(v.subject, v.provider, {
    operation, parameters, result: v.result, rawResult: v.rawResult,
    analysisProfile: v.analysisProfile, limitations: v.limitations,
    locations: v.locations,
    ...(v.relationshipCoverage === undefined ? {} : { relationshipCoverage: v.relationshipCoverage })
  }));
  if (!recorded.ok) throw recorded.error;
}
const bundle = records.exportEvidenceBundle();
await writeFile(join(output, 'native-handler-rea.json'), JSON.stringify(bundle));
console.log(JSON.stringify({ records: bundle.records.length, reanalyzed: false }));
