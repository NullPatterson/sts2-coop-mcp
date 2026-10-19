#!/usr/bin/env python3
"""Generate/update an MCP client config with one sts2-client-N entry per
LocalCoop game client.

Merges into an existing config file rather than overwriting it: any
"mcpServers" entries not matching the sts2 prefix are left untouched, and
existing sts2-client-* entries are replaced wholesale to match the current
client count (so shrinking the client count removes stale entries instead
of leaving dead config behind).

Usage:
    generate_mcp_config.py --client-count 2 \
        --mcp-dir /home/nol/sts2-coop-mcp/repos/STS2_MCP/mcp \
        --config-path /home/nol/.claude/.mcp.json

Safe to run repeatedly / idempotently for a given client count.
"""

import argparse
import json
import sys
from pathlib import Path


def build_entry(uv_path: str, mcp_dir: str, port: int) -> dict:
    return {
        "command": uv_path,
        "args": [
            "run", "--directory", mcp_dir,
            "python", "server.py",
            "--port", str(port),
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-count", type=int, required=True,
                         help="Number of LocalCoop clients (2-4).")
    parser.add_argument("--mcp-dir", type=str, required=True,
                         help="Path to STS2_MCP/mcp (contains server.py, pyproject.toml).")
    parser.add_argument("--config-path", type=str, required=True,
                         help="Path to the MCP client config file to create/update "
                              "(.mcp.json, claude_desktop_config.json, etc).")
    parser.add_argument("--base-port", type=int, default=15526,
                         help="Port for client 0. Default: 15526.")
    parser.add_argument("--uv-path", type=str, default="uv",
                         help="Path to the uv executable. Default: 'uv' (must be on PATH).")
    parser.add_argument("--prefix", type=str, default="sts2-client",
                         help="Server name prefix. Default: 'sts2-client' -> sts2-client-0, etc.")
    args = parser.parse_args()

    if not (2 <= args.client_count <= 4):
        print(f"error: --client-count must be 2 through 4, got {args.client_count}", file=sys.stderr)
        return 1

    mcp_dir = Path(args.mcp_dir).expanduser().resolve()
    if not (mcp_dir / "server.py").exists():
        print(f"error: {mcp_dir}/server.py not found; check --mcp-dir", file=sys.stderr)
        return 1

    config_path = Path(args.config_path).expanduser()
    config_path.parent.mkdir(parents=True, exist_ok=True)

    if config_path.exists():
        try:
            config = json.loads(config_path.read_text())
        except json.JSONDecodeError as e:
            print(f"error: {config_path} exists but is not valid JSON: {e}", file=sys.stderr)
            return 1
    else:
        config = {}

    servers = config.setdefault("mcpServers", {})

    # Drop any existing sts2-client-* entries so shrinking the client count
    # doesn't leave stale/dead entries behind.
    stale_keys = [k for k in servers if k.startswith(f"{args.prefix}-")]
    for k in stale_keys:
        del servers[k]

    for i in range(args.client_count):
        port = args.base_port + i
        servers[f"{args.prefix}-{i}"] = build_entry(args.uv_path, str(mcp_dir), port)

    config_path.write_text(json.dumps(config, indent=2) + "\n")

    print(f"Wrote {args.client_count} sts2-client-* entries to {config_path}")
    for i in range(args.client_count):
        print(f"  {args.prefix}-{i}  ->  port {args.base_port + i}")
    if stale_keys:
        print(f"Removed {len(stale_keys)} stale entr{'y' if len(stale_keys) == 1 else 'ies'} "
              f"from a previous run: {', '.join(stale_keys)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())