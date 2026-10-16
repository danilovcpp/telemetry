"""Extract the mobile API from the service's OpenAPI document into docs/mobile-api.openapi.json.

The service publishes its full document at /openapi/v1.json when ASPNETCORE_ENVIRONMENT=Development.
This keeps only /api/mobile/* operations and the schemas they reference, and adds mobile-specific metadata.

    python scripts/export-mobile-openapi.py http://localhost:5146/openapi/v1.json

Standard library only.
"""
import json
import sys
import urllib.request
from pathlib import Path

API_VERSION = "1.0.0"
OUTPUT = Path(__file__).resolve().parent.parent / "docs" / "mobile-api.openapi.json"

DESCRIPTION = (
    "Read API for the DeviceTelemetry mobile app. Human-readable guide: docs/mobile-api.md.\n\n"
    "**Temporarily public (MVP):** no authentication. `customerId` and `deviceId` in the URL do not prove "
    "access rights. Authentication and owner checks will be added before a public production launch, which "
    "will change this contract.\n\n"
    "Errors are RFC 9457 problem details (`application/problem+json`) with a stable `code`; request "
    "validation errors (400) carry `errors` instead. Rate limit: 120 requests per minute per client IP, "
    "exceeded requests get 429 with `Retry-After`."
)


def collect_refs(node, found):
    if isinstance(node, dict):
        ref = node.get("$ref")
        if isinstance(ref, str) and ref.startswith("#/components/schemas/"):
            found.add(ref.rsplit("/", 1)[1])
        for value in node.values():
            collect_refs(value, found)
    elif isinstance(node, list):
        for value in node:
            collect_refs(value, found)


def main(source: str) -> None:
    with urllib.request.urlopen(source) as response:
        full = json.load(response)

    paths = {path: ops for path, ops in full["paths"].items() if path.startswith("/api/mobile/")}
    if not paths:
        sys.exit("No /api/mobile/ paths in the source document.")

    schemas = full.get("components", {}).get("schemas", {})
    needed, pending = set(), set()
    collect_refs(paths, pending)
    while pending:  # schemas can reference other schemas
        name = pending.pop()
        if name in needed:
            continue
        needed.add(name)
        collect_refs(schemas[name], pending)

    tags = sorted({tag for ops in paths.values() for op in ops.values() for tag in op.get("tags", [])})
    document = {
        "openapi": full["openapi"],
        "info": {"title": "DeviceTelemetry Mobile API", "version": API_VERSION, "description": DESCRIPTION},
        "servers": [{
            "url": "{baseUrl}",
            "description": "Service base URL, e.g. the Staging VPS.",
            "variables": {"baseUrl": {"default": "http://localhost:8080"}},
        }],
        "paths": paths,
        "components": {"schemas": {name: schemas[name] for name in sorted(needed)}},
        "tags": [{"name": tag, "description": "No authentication yet; see info.description."} for tag in tags],
    }

    OUTPUT.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {OUTPUT} ({len(paths)} paths, {len(needed)} schemas)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5146/openapi/v1.json")
