# /// script
# requires-python = ">=3.11,<3.13"
# ///
"""Writes docs/media/CREDITS.md from the demo drive's sources and the edit's downloads.

    uv run demo-video/library/credits.py
"""
from __future__ import annotations

import html, json, os, re, time, urllib.request
from collections import defaultdict
from pathlib import Path

from build import HERE, REPO, UA, photo_plan

EDIT = HERE.parent / "edit"


CACHE = Path(os.environ.get("USERPROFILE", ".")) / "FindItDemo" / "cache" / "mixkit-pages.json"


def page(kind: str, item_id: int | str) -> tuple[str, str]:
    """Mixkit redirects any slug to a clip's canonical page; its <h1> is the clip's title. Results are cached."""
    cache = json.loads(CACHE.read_text(encoding="utf-8")) if CACHE.exists() else {}
    key = f"{kind}/{item_id}"
    if key in cache:
        return tuple(cache[key])
    for attempt in range(6):
        try:
            time.sleep(1.5)  # Mixkit answers bursts with 429
            with urllib.request.urlopen(urllib.request.Request(f"https://mixkit.co/{kind}/x-{item_id}/", headers=UA), timeout=60) as r:
                body = r.read().decode("utf8", "replace")
                url = r.geturl()
            break
        except OSError:
            if attempt == 5:
                raise
            time.sleep(10 * (attempt + 1))
    title = re.search(r"<h1[^>]*>\s*(.*?)\s*</h1>", body, re.S)
    result = (url, html.unescape(re.sub(r"<[^>]+>", "", title.group(1))).strip() if title else url)
    cache[key] = result
    CACHE.parent.mkdir(parents=True, exist_ok=True)
    CACHE.write_text(json.dumps(cache, indent=1), encoding="utf-8")
    return result


def main():
    spec = json.loads((HERE / "sources.json").read_text(encoding="utf-8"))
    memos = json.loads((HERE / "memos.json").read_text(encoding="utf-8"))
    assets = json.loads((EDIT / "assets.json").read_text(encoding="utf-8"))

    uses: dict[int, list[str]] = defaultdict(list)
    for relative, _, item, _ in photo_plan(spec):
        uses[item["clip"]].append(relative)
    for video in spec["videos"]:
        for clip, start, end in video["segments"]:
            uses[clip].append(f"{video['path']} ({start}–{end} s of the clip)" if len(video["segments"]) > 1 else video["path"])

    rows = []
    for clip in sorted(uses, key=lambda c: min(uses[c])):
        url, title = page("free-stock-video", clip)
        rows.append(f"| {'<br>'.join(sorted(uses[clip]))} | [{title}]({url}) |")

    # Mixkit has no page per track or sound effect, so these are credited by the title and artist in assets.json.
    sounds = [f"- {'Music' if item['kind'] == 'music' else 'Sound effect'}: *{item['title']}*" + (f" by {item['artist']}" if "artist" in item else "")
              for item in assets["downloads"]]

    voices = sorted({m["voice"] for m in memos["memos"]})
    text = f"""# Demo video credits

`findit-demo.mp4` and `findit-demo.webp` show the real FindIt app, recorded live on a demo drive built by [`demo-video/library`](../../demo-video/library). Everything on that drive is licensed for this use or made up for it. See [`demo-video/README.md`](../../demo-video/README.md) for how the video is made.

## Photos and videos

The drive's photos are stills from, and its videos are cut from, clips on [Mixkit](https://mixkit.co/) under the [Mixkit Stock Video Free License](https://mixkit.co/license/#videoFree). Photos were cropped to camera proportions and given camera-style names and dates; nothing on the drive is a real person's library.

| On the demo drive | Mixkit clip |
|---|---|
{chr(10).join(rows)}

## Voices

The voice memos are spoken by [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) (Apache-2.0), voices {", ".join(f"`{v}`" for v in voices)}, from scripts in [`memos.json`](../../demo-video/library/memos.json).

## Documents

The tenancy agreement, bills, bookings, tickets, invoice, notes and spreadsheets are fictional; [`documents.json`](../../demo-video/library/documents.json) and [`documents/`](../../demo-video/library/documents) hold their text. Names, addresses, companies and reference numbers are invented. The scanned agreement is the same text printed to PDF and made to look photographed.

## Music and sound effects

From [Mixkit](https://mixkit.co/), under the [Mixkit Stock Music Free License](https://mixkit.co/license/#musicFree) and [Sound Effects Free License](https://mixkit.co/license/#sfxFree):

{chr(10).join(sounds)}

## The app and the edit

The FindIt icon and interface are part of this repository (MIT). The video is edited with [Remotion](https://www.remotion.dev/).
"""
    target = REPO / "docs" / "media" / "CREDITS.md"
    target.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {target} ({len(rows)} clips, {len(sounds)} sounds)")


if __name__ == "__main__":
    main()
