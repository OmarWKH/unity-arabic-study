#!/usr/bin/env python3
"""Fetch candidate fonts listed in tools/candidate-fonts.txt into a local
cache. Skips files already downloaded. Adds a small delay between fetches
and retries on transient failures so we don't trip rate limits.

Usage:
  python tools/fetch-fonts.py
  python tools/fetch-fonts.py --cache-dir font-cache/
  python tools/fetch-fonts.py --list tools/candidate-fonts.txt
  python tools/fetch-fonts.py --force      # re-download even if exists

Default cache dir is ./font-cache/ (gitignored).
"""

from __future__ import annotations
import argparse
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

# Polite delay between successful requests (seconds).
INTER_REQUEST_DELAY = 0.25
# Retry policy.
MAX_RETRIES = 3
RETRY_BASE_DELAY = 1.5  # seconds; exponential backoff


def parse_list(path: Path) -> list:
    """Read the candidate list. Returns list of (filename, url) tuples."""
    entries = []
    for ln, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        s = raw.strip()
        if not s or s.startswith("#"):
            continue
        parts = s.split()
        if len(parts) != 2:
            print(f"  line {ln}: skipping malformed entry: {s!r}", file=sys.stderr)
            continue
        entries.append((parts[0], parts[1]))
    return entries


def download(url: str, dest: Path) -> tuple[bool, str]:
    """Download url to dest, with retries. Returns (success, status_string)."""
    last_err = ""
    for attempt in range(1, MAX_RETRIES + 1):
        try:
            req = urllib.request.Request(
                url,
                headers={"User-Agent": "arabic-font-search/1.0 (+local research)"},
            )
            with urllib.request.urlopen(req, timeout=30) as resp:
                if resp.status != 200:
                    last_err = f"HTTP {resp.status}"
                    continue
                data = resp.read()
            tmp = dest.with_suffix(dest.suffix + ".part")
            tmp.write_bytes(data)
            tmp.replace(dest)
            return True, f"ok ({len(data)} bytes)"
        except urllib.error.HTTPError as e:
            last_err = f"HTTP {e.code}"
            if e.code == 404:
                return False, "not found (404)"
            if e.code == 429:
                # Rate limited — back off harder.
                time.sleep(RETRY_BASE_DELAY * (2 ** attempt) * 2)
                continue
        except (urllib.error.URLError, TimeoutError) as e:
            last_err = f"{type(e).__name__}: {e}"
        except Exception as e:
            last_err = f"{type(e).__name__}: {e}"
        # Backoff before next attempt.
        if attempt < MAX_RETRIES:
            time.sleep(RETRY_BASE_DELAY * (2 ** (attempt - 1)))
    return False, last_err


def main(argv):
    p = argparse.ArgumentParser(description="Fetch candidate Arabic fonts.")
    p.add_argument("--list", default="tools/candidate-fonts.txt",
                   help="path to candidate list (default: tools/candidate-fonts.txt)")
    p.add_argument("--cache-dir", default="font-cache",
                   help="local cache directory (default: font-cache)")
    p.add_argument("--force", action="store_true",
                   help="re-download even if the file exists")
    args = p.parse_args(argv[1:])

    list_path = Path(args.list)
    if not list_path.exists():
        print(f"list not found: {list_path}", file=sys.stderr)
        return 1

    cache_dir = Path(args.cache_dir)
    cache_dir.mkdir(parents=True, exist_ok=True)

    entries = parse_list(list_path)
    if not entries:
        print("no entries in list", file=sys.stderr)
        return 1

    print(f"fetching {len(entries)} fonts into {cache_dir}/ ...", file=sys.stderr)

    n_ok = n_skip = n_fail = 0
    for i, (name, url) in enumerate(entries, 1):
        dest = cache_dir / name
        prefix = f"  [{i:3d}/{len(entries)}] {name:<45s}"
        if dest.exists() and not args.force:
            print(f"{prefix} skip (already present, {dest.stat().st_size} bytes)",
                  file=sys.stderr)
            n_skip += 1
            continue
        success, status = download(url, dest)
        if success:
            print(f"{prefix} {status}", file=sys.stderr)
            n_ok += 1
        else:
            print(f"{prefix} FAILED: {status}", file=sys.stderr)
            n_fail += 1
        time.sleep(INTER_REQUEST_DELAY)

    print(f"\ndone: {n_ok} ok, {n_skip} skipped, {n_fail} failed", file=sys.stderr)
    return 0 if n_fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
