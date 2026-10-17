"""Exact layout arithmetic for the observed five inline pools; never edits a DLL.

The translation describes an enlarged Mission object, not proof that every native
reference has been found. Pool-relative addresses need their own translation.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
from patch_native_copy import safe_review_output

ROOT = Path(__file__).resolve().parents[2]


def integer(value):
    return int(value, 0) if isinstance(value, str) else int(value)


class ExpandedLayout:
    def __init__(self, original, capacity, expand_jobs=False):
        if type(capacity) is not int or not 2040 <= capacity <= 32766:
            raise ValueError("Capacity must fit a positive signed-short capacity+1 sentinel (2040..32766)")
        self.original = original
        self.capacity = capacity
        self.insertions = []
        self.pools = []
        self.job_tables = []
        previous_end = 0
        for pool in original["pools"]:
            base, stride = integer(pool["missionOffset"]), integer(pool["elementStride"])
            old_count = pool["capacity"]
            table, counter = integer(pool["indexTableOffset"]), integer(pool["counterOffset"])
            high = integer(pool["highWaterOffset"])
            if old_count != 2040 or pool["indexEntryBytes"] != 2:
                raise ValueError("This profile supports only the observed 2040-entry short-index pools")
            if base < previous_end or table != base + stride * old_count or counter != table + old_count * 2 or high != counter + 4:
                raise ValueError("Original pool layout is inconsistent or overlaps")
            previous_end = high + 4
            extra = capacity - old_count
            self.insertions += [(table, extra * stride), (counter, extra * 2)]
        if expand_jobs:
            if capacity != 4080:
                raise ValueError("Expanded job-table profile is audited only for 4080 slots")
            # Mission ctor 0x1805ce3f0: three 127-page pointer/flag arrays.
            # Producers index a page by ordinal >> 4, i.e. 16 records/page.
            pages = max(127, (capacity + 15) // 16)
            for base in (0x10439A0, 0x1043E20, 0x10442A0):
                self.insertions += [(base + 0x400, (pages - 127) * 8),
                                    (base + 0x47F, pages - 127)]
                self.job_tables.append({"originalBase": hex(base), "originalPages": 127,
                                        "pages": pages, "recordsPerPage": 16})
        self.insertions.sort()
        for pool in original["pools"]:
            translated = dict(pool)
            for field in ("missionOffset", "indexTableOffset", "counterOffset", "highWaterOffset"):
                translated[field] = hex(self.mission_offset(integer(pool[field])))
            translated["capacity"] = capacity
            translated["lastSlotOffset"] = hex(integer(translated["missionOffset"]) + (capacity - 1) * integer(pool["elementStride"]))
            self.pools.append(translated)
        self.original_size = original["missionAllocationBytes"]
        if previous_end > self.original_size:
            raise ValueError("Original pools exceed Mission allocation")
        self.size = self.mission_offset(self.original_size)
        for table in self.job_tables:
            base = integer(table["originalBase"])
            table.update({"base": hex(self.mission_offset(base)),
                          "pointerBase": hex(self.mission_offset(base + 8)),
                          "flagBase": hex(self.mission_offset(base + 0x400)),
                          "endExclusive": hex(self.mission_offset(base + 0x480))})
        if self.size >= 0x80000000:
            raise ValueError("Expanded displacements exceed signed 32-bit encoding")

    def mission_offset(self, offset):
        """Translate an original Mission-relative field or boundary address."""
        if type(offset) is not int or offset < 0:
            raise ValueError("Expected a nonnegative Mission-relative offset")
        return offset + sum(amount for at, amount in self.insertions if at <= offset)

    def pool_offset(self, stride, offset):
        """Translate a pool-relative address, never an Agent-field address."""
        if stride not in (0xC60, 0x230, 0x2C8) or not 0 <= offset <= 2040 * (stride + 2) + 8:
            raise ValueError("Offset is outside a known pool layout")
        delta = self.capacity - 2040
        return offset + (delta * stride if offset >= 2040 * stride else 0) + (delta * 2 if offset >= 2040 * (stride + 2) else 0)

    def report(self):
        return {"schemaVersion": 1, "status": "layout-arithmetic-only-not-reference-coverage",
                "binarySha256": self.original["binarySha256"], "originalCapacity": 2040,
                "capacity": self.capacity, "sentinel": self.capacity + 1,
                "originalMissionAllocationBytes": self.original_size, "missionAllocationBytes": self.size,
                "additionalBytes": self.size - self.original_size,
                "insertions": [{"originalOffset": hex(a), "bytes": b} for a, b in self.insertions],
                "pools": self.pools,
                "jobPageTables": self.job_tables,
                "requirements": ["Construct expanded storage before any Agent pointer escapes; never relocate a live Mission.",
                                 "Translate every Mission-relative field access after insertion boundaries, including non-pool tail fields.",
                                 "Use pool-relative translation only with independently established pool-pointer provenance.",
                                 "Review dependent containers, stack arrays, masks, capacity checks and exception cleanup separately.",
                                 "Apply all instruction edits atomically before engine initialization; no game execution is performed by this tool." ]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capacity", type=int, default=4080)
    parser.add_argument("--pools-only", action="store_true", help="Omit newly discovered job-page table growth")
    parser.add_argument("--layout", type=Path, default=ROOT / "evidence/engine-extension/native-pool-layout.json")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    layout = ExpandedLayout(json.loads(args.layout.read_text(encoding="utf-8-sig")), args.capacity, expand_jobs=not args.pools_only)
    output = safe_review_output(args.output, [args.layout])
    output.write_text(json.dumps(layout.report(), indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"capacity": layout.capacity, "missionBytes": layout.size, "additionalBytes": layout.size-layout.original_size}))


if __name__ == "__main__":
    main()
