#!/usr/bin/env python3
"""
ig_media.py — Extract all media from an Instagram post via the private (mobile) API.

One API call returns everything: video URLs, image URLs, audio URL + clip timing, caption.
Outputs JSON to stdout. All diagnostics go to stderr.

Usage:
    python ig_media.py <url> [--session <file>] [--sessionid <id>] [--username <u> --password <p>]

Output (JSON):
{
  "caption": "@user — description",
  "items": [
    {"type": "video", "url": "https://..."},
    {"type": "image", "url": "https://..."},
    ...
  ],
  "audio": {
    "url": "https://...",
    "start_ms": 1500,
    "duration_ms": 30000
  }
}

Or on failure: {"error": "reason"}

Install: pip install instagrapi requests
"""

import sys, os, json, argparse

# Force UTF-8 output on Windows (prevents 'charmap' codec errors)
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("url")
    ap.add_argument("--session", default=None)
    ap.add_argument("--sessionid", default=None)
    ap.add_argument("--username", default=None)
    ap.add_argument("--password", default=None)
    ap.add_argument("--download-dir", default=None,
                    help="If set, download files to this dir instead of returning URLs")
    args = ap.parse_args()

    try:
        from instagrapi import Client
    except ImportError:
        out({"error": "instagrapi not installed"}); sys.exit(0)

    cl = Client()
    if not login(cl, args):
        out({"error": "login failed"}); sys.exit(0)

    is_story = "/stories/" in args.url
    is_highlight = "/s/" in args.url

    if is_story:
        try:
            result = fetch_story(cl, args.url)
        except Exception as e:
            err(f"Story fetch failed: {e}")
            import traceback; traceback.print_exc(file=sys.stderr)
            out({"error": f"story failed: {e}"}); sys.exit(0)
    elif is_highlight:
        try:
            result = fetch_highlight(cl, args.url)
        except Exception as e:
            err(f"Highlight fetch failed: {e}")
            import traceback; traceback.print_exc(file=sys.stderr)
            out({"error": f"highlight failed: {e}"}); sys.exit(0)
    else:
        # Regular post/reel/carousel
        try:
            pk = cl.media_pk_from_url(args.url)
        except Exception as e:
            out({"error": f"bad URL: {e}"}); sys.exit(0)

        try:
            raw = cl.private_request(f"media/{pk}/info/")
        except Exception as e:
            err(f"API error: {e}")
            try:
                raw = {"items": [cl.media_info_v1(pk).__dict__]}
            except Exception as e2:
                out({"error": f"API failed: {e}, fallback: {e2}"}); sys.exit(0)

        items_raw = raw.get("items", [])
        if not items_raw:
            out({"error": "no items in response"}); sys.exit(0)

        try:
            result = extract_all(items_raw[0])
        except Exception as e:
            err(f"extract_all failed: {e}")
            import traceback; traceback.print_exc(file=sys.stderr)
            out({"error": f"extraction failed: {e}"}); sys.exit(0)

    # If download dir specified, download files to disk
    if args.download_dir and result.get("items"):
        try:
            result = download_files(result, args.download_dir)
        except Exception as e:
            err(f"download_files failed: {e}")

    out(result)


def fetch_story(cl, url):
    """Fetch story/stories from a story URL.
    
    Two formats:
      - /stories/username/1234567890/ → single story by PK
      - /stories/username/           → all active stories for that user
    """
    from urllib.parse import urlparse

    path = urlparse(url).path
    parts = [p for p in path.split("/") if p and p != "stories"]

    if not parts:
        return {"error": "Could not parse story URL"}

    username = parts[0]
    story_pk = parts[1] if len(parts) > 1 and parts[1].isdigit() else None

    if story_pk:
        # Single story by PK
        err(f"Story PK: {story_pk}")
        story = cl.story_info(story_pk)
        return story_to_result(story)
    else:
        # All active stories for user
        err(f"Fetching all stories for @{username}")
        try:
            user_id = cl.user_id_from_username(username)
        except Exception as e:
            return {"error": f"User not found: {username} ({e})"}

        stories = cl.user_stories(user_id)
        if not stories:
            return {"error": f"No active stories for @{username}"}

        err(f"Found {len(stories)} active stories for @{username}")

        media_items = []
        for story in stories:
            if story.media_type == 2 and story.video_url:
                media_items.append({"type": "video", "url": str(story.video_url)})
            elif story.thumbnail_url:
                media_items.append({"type": "image", "url": str(story.thumbnail_url)})

        if not media_items:
            return {"error": "Could not extract any story media"}

        return {"caption": f"@{username}", "items": media_items}


def story_to_result(story):
    """Convert a single Story object to our result format."""
    username = story.user.username if story.user else ""
    caption = f"@{username}" if username else None
    media_items = []

    if story.media_type == 2 and story.video_url:
        media_items.append({"type": "video", "url": str(story.video_url)})
    elif story.thumbnail_url:
        media_items.append({"type": "image", "url": str(story.thumbnail_url)})

    if not media_items:
        return {"error": "Could not extract story media"}

    return {"caption": caption, "items": media_items}


def fetch_highlight(cl, url):
    """Fetch all stories from a highlight reel.
    
    Highlight URLs: instagram.com/s/aGlnaGxpZ2h0OjE3OTIwNDcyODE4OTYyMTQ0
    The /s/ path contains a base64-encoded highlight ID.
    """
    import base64
    from urllib.parse import urlparse

    path = urlparse(url).path
    # Extract the base64 part: /s/<base64>/
    parts = [p for p in path.split("/") if p and p != "s"]
    if not parts:
        return {"error": "Could not parse highlight URL"}

    # Decode: "aGlnaGxpZ2h0OjE3OTIwNDcyODE4OTYyMTQ0" → "highlight:17920472818962144"
    try:
        decoded = base64.urlsafe_b64decode(parts[0] + "==").decode("utf-8")
        err(f"Highlight decoded: {decoded}")
    except Exception:
        # Might be a plain highlight ID or different format
        decoded = parts[0]

    # Extract the numeric PK
    if decoded.startswith("highlight:"):
        highlight_pk = decoded.split(":")[1]
    else:
        highlight_pk = decoded

    err(f"Highlight PK: {highlight_pk}")

    info = cl.highlight_info(int(highlight_pk))
    err(f"Highlight: {info.title}, {info.media_count} items")

    username = info.user.username if info.user else ""
    caption = f"@{username}" if username else None
    media_items = []

    # Fetch each story in the highlight
    for story_pk in (info.media_ids or []):
        try:
            story = cl.story_info(story_pk)
            if story.media_type == 2 and story.video_url:
                media_items.append({"type": "video", "url": str(story.video_url)})
            elif story.thumbnail_url:
                media_items.append({"type": "image", "url": str(story.thumbnail_url)})
        except Exception as e:
            err(f"Skipping story {story_pk}: {e}")

    if not media_items:
        return {"error": "No media found in highlight"}

    err(f"Highlight: {len(media_items)} items extracted")
    return {"caption": caption, "items": media_items}


def extract_all(item):
    """Extract all media + audio from a raw Instagram API media item."""
    media_items = []

    # Caption — username only, no description text
    user = item.get("user", {})
    username = user.get("username", "")
    caption = f"@{username}" if username else None

    media_type = item.get("media_type")
    product_type = item.get("product_type", "")

    # Carousel (media_type 8)
    carousel = item.get("carousel_media")
    if carousel:
        for slide in carousel:
            slide_type = slide.get("media_type")
            if slide_type == 2:  # video
                url = best_video_url(slide)
                if url: media_items.append({"type": "video", "url": url})
            else:  # image
                url = best_image_url(slide)
                if url: media_items.append({"type": "image", "url": url})
    elif media_type == 2 or product_type in ("clips", "igtv"):
        # Single video / reel
        url = best_video_url(item)
        if url: media_items.append({"type": "video", "url": url})
    else:
        # Single image
        url = best_image_url(item)
        if url: media_items.append({"type": "image", "url": url})

    # Audio (post-level music)
    audio = extract_music(item)

    result = {
        "caption": caption,
        "items": media_items,
    }
    if audio:
        result["audio"] = audio

    return result


def best_video_url(item):
    """Get the best quality video URL from video_versions."""
    versions = item.get("video_versions", [])
    if not versions: return None
    # Sort by resolution (width * height), take largest
    versions.sort(key=lambda v: (v.get("width", 0) * v.get("height", 0)), reverse=True)
    return versions[0].get("url")


def best_image_url(item):
    """Get the best quality image URL from image_versions2."""
    iv2 = item.get("image_versions2", {})
    candidates = iv2.get("candidates", [])
    if not candidates: return None
    # First candidate is typically highest quality
    candidates.sort(key=lambda c: (c.get("width", 0) * c.get("height", 0)), reverse=True)
    return candidates[0].get("url")


def extract_music(item):
    """Extract audio URL + clip timing from music metadata."""
    for key in ("music_metadata", "clips_metadata"):
        mi = (item.get(key) or {}).get("music_info") or {}
        if not mi: continue

        asset = mi.get("music_asset_info") or {}
        cons = mi.get("music_consumption_info") or {}

        url = (asset.get("progressive_download_url") or
               asset.get("fast_start_progressive_download_url"))
        if not url: continue

        start = cons.get("overlap_start_ms") or 0
        dur = (cons.get("playback_duration_ms") or
               cons.get("audio_asset_clip_duration_in_ms") or
               asset.get("clip_duration_in_ms") or 0)

        if not dur:
            hl = asset.get("highlight_start_times_in_ms")
            if isinstance(hl, list) and hl:
                if not start: start = hl[0]
                dur = 30000

        title = asset.get("title", "")
        artist = asset.get("display_artist", "")

        err(f"Music: {artist} - {title}, start={start}ms dur={dur}ms")
        return {"url": url, "start_ms": start, "duration_ms": dur}

    return None


def download_files(result, download_dir):
    """Download media files to disk. Replaces URLs with local paths."""
    import requests
    os.makedirs(download_dir, exist_ok=True)

    for i, item in enumerate(result.get("items", [])):
        url = item.get("url")
        if not url: continue
        ext = "mp4" if item["type"] == "video" else "jpg"
        path = os.path.join(download_dir, f"{i:03d}.{ext}")
        try:
            r = requests.get(url, timeout=60)
            r.raise_for_status()
            with open(path, "wb") as f: f.write(r.content)
            item["path"] = path
            err(f"Downloaded {item['type']} {i}: {len(r.content)} bytes")
        except Exception as e:
            err(f"Download failed for item {i}: {e}")

    audio = result.get("audio")
    if audio and audio.get("url"):
        path = os.path.join(download_dir, "audio.m4a")
        try:
            r = requests.get(audio["url"], timeout=30)
            r.raise_for_status()
            if len(r.content) > 1000:
                with open(path, "wb") as f: f.write(r.content)
                audio["path"] = path
                err(f"Downloaded audio: {len(r.content)} bytes")
        except Exception as e:
            err(f"Audio download failed: {e}")

    return result


def login(cl, args):
    """Try all login methods in priority order."""
    if args.session and os.path.exists(args.session):
        try:
            cl.load_settings(args.session)
            cl.login(cl.username, cl.password)
            err(f"Session OK: {cl.username}")
            return True
        except Exception as e:
            err(f"Session failed: {e}")

    if args.sessionid:
        try:
            cl.login_by_sessionid(args.sessionid)
            err("Sessionid OK")
            if args.session: cl.dump_settings(args.session)
            return True
        except Exception as e:
            err(f"Sessionid failed: {e}")

    if args.username and args.password:
        try:
            cl.login(args.username, args.password)
            if args.session: cl.dump_settings(args.session)
            err(f"Login OK: {args.username}")
            return True
        except Exception as e:
            err(f"Login failed: {e}")

    return False


def out(obj):
    try:
        print(json.dumps(obj, ensure_ascii=False, default=str))
    except Exception as e:
        # Nuclear fallback — sanitize the whole thing
        err(f"JSON serialize error: {e}")
        print(json.dumps(sanitize(obj), ensure_ascii=False, default=str))

def sanitize(obj):
    """Recursively convert everything to JSON-safe types."""
    if isinstance(obj, dict):
        return {str(k): sanitize(v) for k, v in obj.items()}
    elif isinstance(obj, (list, tuple)):
        return [sanitize(v) for v in obj]
    elif isinstance(obj, (str, int, float, bool)) or obj is None:
        return obj
    else:
        return str(obj)

def err(msg):
    print(msg, file=sys.stderr)


if __name__ == "__main__":
    main()
