"""Require each named TRX scenario exactly once and passed, including platform qualification."""
import collections
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def verify(trx_path, required_path):
    required = json.loads(Path(required_path).read_text(encoding="utf-8"))
    if (not isinstance(required, list) or not required
            or any(not isinstance(name, str) or not name for name in required)
            or len(set(required)) != len(required)):
        raise SystemExit("Required scenarios must be a nonempty list of unique test names.")
    rows = ET.parse(trx_path).findall(".//{*}UnitTestResult")
    if collections.Counter(row.attrib.get("testName") for row in rows) != collections.Counter(required):
        raise SystemExit("Required scenario names or multiplicity differ.")
    if any(row.attrib.get("outcome") != "Passed" for row in rows):
        raise SystemExit("Every required scenario must pass without skips.")
    print(f"Qualified all {len(required)} required test cases with no skips.")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: verify-required-trx.py results.trx required-scenarios.json")
    verify(sys.argv[1], sys.argv[2])
