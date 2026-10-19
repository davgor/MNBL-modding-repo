"""MMR-45: writes worldgen/policies/campaign-catalog.json, the installed campaign catalog a compiled world is built against.

The world factories' policies (rosters, economy, prototypes, startup, naval content) were made from one capture of the installed merged
campaign XML (the campaign_catalog probe's receipt) and checked against one inventory of the live engine defaults (a generated_campaign run).
The activation builds a compiled world in game from the live merged XML (MBObjectManager.GetMergedXmlForManaged at the main menu) only when
every document is byte for byte the captured one, and registers it only when the live defaults are the pinned inventory: another game
version or another mod's XML refuses the world instead of building it from documents its policies were not made from. This policy carries
the hashes and the inventory, never the captured paths (they hold the user's profile path).

usage: python tools/make_campaign_catalog_pin.py
"""
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    rosters = json.load(open(os.path.join(REPO, 'worldgen/policies/rosters.json'), encoding='utf-8'))
    provenance = rosters['provenance']
    receipt_path = provenance['catalogReceipt']
    defaults_path = provenance['defaultReferences']
    receipt = json.load(open(os.path.join(REPO, receipt_path), encoding='utf-8-sig'))
    if receipt.get('probe') != 'campaign_catalog' or receipt.get('status') != 'ok' or not receipt.get('final'):
        raise SystemExit('the catalog receipt is not a completed campaign_catalog run')
    outputs = receipt['outputs']
    documents = {d['id']: d['sha256'] for d in outputs['documents']}
    for key, value in provenance['catalogDocuments'].items():
        if documents.get(key) != value:
            raise SystemExit('the rosters policy names another capture of ' + key)
    run = json.load(open(os.path.join(REPO, defaults_path), encoding='utf-8-sig'))
    if sha(os.path.join(REPO, defaults_path)) != provenance['defaultReferencesSha256']:
        raise SystemExit('the default inventory run differs from the rosters policy')
    references = sorted(run['outputs']['worlds'][0]['registration']['verifiedLiveDefaultReferences'])
    if len(references) != provenance['defaultReferenceCount']:
        raise SystemExit('the default inventory count differs from the rosters policy')
    policy = {
        'schema': 'mnbl.campaign-catalog-pin',
        'version': 1,
        'ticket': 'MMR-45',
        'gameType': outputs['gameType'],
        'rule': 'A compiled world is built in game only from live merged documents whose SHA-256 is the pinned one (every id below), and registers only when the live engine defaults are exactly the pinned inventory.',
        'documents': dict(sorted(documents.items())),
        'defaults': references,
        'provenance': {
            'catalogReceipt': receipt_path,
            'catalogReceiptSha256': sha(os.path.join(REPO, receipt_path)),
            'defaultReferences': defaults_path,
            'defaultReferencesSha256': provenance['defaultReferencesSha256'],
            'moduleSha256': receipt.get('moduleSha256'),
            'tool': 'tools/make_campaign_catalog_pin.py',
        },
    }
    out = os.path.join(REPO, 'worldgen/policies/campaign-catalog.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1, sort_keys=False)
        f.write('\n')
    print(out, len(documents), 'documents,', len(references), 'defaults')


if __name__ == '__main__':
    main()
