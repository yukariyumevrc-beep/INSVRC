#!/usr/bin/env python3
"""
NEW GEN BLACKLIST V3 - list compiler for the INSVRC repo.

Takes the messy file you actually type into (profile links, bare usr_ ids, display
names, anything mixed together) and compiles the canonical list the VRChat world
downloads: deduped, sorted, commented, optionally signed.

With --resolve it turns usr_ ids into current display names by reading each
public profile page, because Udon cannot see user ids at all: the display name is
the only thing a world can match on. No login and no API token are needed; the
name is in the page's Open Graph tags, the same ones a Discord link preview uses.
Resolved names are cached in known_users.json so a page is fetched once per
account, not once per build.

With --refresh it re-reads every id even if the name is already known, which is
how a griefer who renamed gets put back on the list.

Standard library only. Python 3.8+.

  # compile sources/global_blacklist.src -> global_blacklist
  python3 build_blacklist.py --src sources/global_blacklist.src --out global_blacklist

  # fill in missing display names from public profile pages
  python3 build_blacklist.py \
      --src sources/global_blacklist.src --out global_blacklist --resolve

  # re-check every id, catching renames
  python3 build_blacklist.py \
      --src sources/global_blacklist.src --out global_blacklist --refresh

  # sign it so a hijacked host cannot poison the world (salt must match BlacklistCore)
  python3 build_blacklist.py --src ... --out ... --salt "$BLACKLIST_SALT"

  # CI check: fail if the committed output is stale
  python3 build_blacklist.py --src ... --out ... --check
"""

import argparse
import html
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

USER_ID_RE = re.compile(r"usr_[0-9A-Za-z_-]{4,}")

# The public profile page carries the display name in its Open Graph tags, the
# same ones Discord and Twitter read to build a link preview. No login, no API
# token, no session cookie: a plain GET of a public page.
#
#   <meta name="og:title" content="《ＳＡＩＮＴ》">
#   <meta name="og:description" content="VRChat user 《ＳＡＩＮＴ》">
#
# A deleted or wrong id still answers 200, but with VRChat's generic site tags,
# so the description check below is what tells a real profile from a miss.
PROFILE_URL = "https://vrchat.com/home/user/"

USER_AGENT = os.environ.get(
    "VRC_USER_AGENT", "ParadiseBlacklistBuilder/3.0 (+https://github.com/yukariyumevrc-beep/INSVRC)"
)

META_RE = re.compile(r"<meta\b[^>]*>", re.IGNORECASE)
META_NAME_RE = re.compile(r"(?:name|property)\s*=\s*(\"([^\"]*)\"|'([^']*)')", re.IGNORECASE)
META_CONTENT_RE = re.compile(r"content\s*=\s*(\"([^\"]*)\"|'([^']*)')", re.IGNORECASE)
PROFILE_DESCRIPTION_PREFIX = "VRChat user "

MIN_NAME_LENGTH = 3
MAX_NAME_LENGTH = 64
BAD_NAME_CHARS = set("*?%")

SECTION_BLACKLIST = "blacklist"
SECTION_IMMUNE = "immune"


class Entry:
    __slots__ = ("user_id", "name", "note", "section")

    def __init__(self, user_id="", name="", note="", section=SECTION_BLACKLIST):
        self.user_id = user_id
        self.name = name
        self.note = note
        self.section = section

    @property
    def sort_key(self):
        return (self.name or self.user_id).lower()

    def format(self):
        fields = [f for f in (self.user_id, self.name, self.note) if f]
        return " | ".join(fields)


# ---------------------------------------------------------------------------
# parsing
# ---------------------------------------------------------------------------


def parse_line(line):
    """Returns (entry, problem). Either may be None."""
    fields = [f.strip() for f in line.split("|")]
    entry = Entry()

    for field in fields:
        if not field:
            continue

        match = USER_ID_RE.search(field)
        if match:
            if not entry.user_id:
                entry.user_id = match.group(0)
            continue

        if "://" in field:
            return None, "link with no usr_ id in it: %s" % field

        if not entry.name:
            entry.name = field
        elif not entry.note:
            entry.note = field

    if not entry.user_id and not entry.name:
        return None, None

    if entry.name:
        if len(entry.name) < MIN_NAME_LENGTH:
            return None, "name %r is shorter than %d characters; too easy to hit an innocent player" % (
                entry.name,
                MIN_NAME_LENGTH,
            )
        if len(entry.name) > MAX_NAME_LENGTH:
            return None, "name %r is longer than %d characters" % (entry.name, MAX_NAME_LENGTH)
        if any(c in BAD_NAME_CHARS for c in entry.name):
            return None, "name %r contains a wildcard; matching is exact on purpose" % entry.name

    return entry, None


def parse_sources(paths):
    entries = []
    problems = []
    seen_ids = {}
    seen_names = {}

    for path in paths:
        section = SECTION_BLACKLIST
        with open(path, "r", encoding="utf-8-sig") as handle:
            for number, raw in enumerate(handle, start=1):
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue

                if line.startswith("[") and line.endswith("]"):
                    name = line[1:-1].strip().lower()
                    if name in ("immune", "allowlist", "whitelist"):
                        section = SECTION_IMMUNE
                    elif name in ("blacklist", "banned"):
                        section = SECTION_BLACKLIST
                    else:
                        problems.append("%s:%d unknown section %r" % (path, number, line))
                    continue

                entry, problem = parse_line(line)
                if problem:
                    problems.append("%s:%d %s" % (path, number, problem))
                    continue
                if entry is None:
                    continue

                entry.section = section

                # Dedupe on id first, then on name; keep the entry that carries more info.
                if entry.user_id and entry.user_id in seen_ids:
                    existing = seen_ids[entry.user_id]
                    if entry.name and not existing.name:
                        existing.name = entry.name
                    if entry.note and not existing.note:
                        existing.note = entry.note
                    continue

                name_key = entry.name.lower() if entry.name else None
                if name_key and name_key in seen_names:
                    existing = seen_names[name_key]
                    if entry.user_id and not existing.user_id:
                        existing.user_id = entry.user_id
                        seen_ids[entry.user_id] = existing

                        # A name carrying an id was written by a profile read; a
                        # bare name is whatever somebody typed once. So the id's
                        # spelling wins, even when the two differ only by case.
                        # Without this, a stale bare line keeps resurrecting the
                        # old spelling on every run that is not a --refresh.
                        existing.name = entry.name
                    continue

                entries.append(entry)
                if entry.user_id:
                    seen_ids[entry.user_id] = entry
                if name_key:
                    seen_names[name_key] = entry

    return entries, problems


# ---------------------------------------------------------------------------
# VRChat API resolution
# ---------------------------------------------------------------------------


def load_cache(path):
    if not path or not os.path.exists(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
        return data if isinstance(data, dict) else {}
    except (ValueError, OSError) as error:
        print("warning: could not read cache %s (%s)" % (path, error), file=sys.stderr)
        return {}


def save_cache(path, cache):
    if not path:
        return
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(cache, handle, indent=2, sort_keys=True, ensure_ascii=False)
        handle.write("\n")


def _meta_value(match):
    """Picks whichever quote style the attribute used."""
    return match.group(2) if match.group(2) is not None else match.group(3)


def parse_profile_page(page):
    """
    Pulls the display name out of a profile page's Open Graph tags.
    Returns None if the page is not a real user profile.

    Attribute order is not assumed, both quote styles are accepted, and HTML
    entities are decoded, so a name like `Bob's & Co "X"` survives the trip.
    """
    tags = {}

    for tag in META_RE.findall(page):
        name = META_NAME_RE.search(tag)
        content = META_CONTENT_RE.search(tag)
        if not name or not content:
            continue
        tags[html.unescape(_meta_value(name)).lower()] = html.unescape(_meta_value(content))

    title = tags.get("og:title", "").strip()
    description = tags.get("og:description", "").strip()

    # A wrong or deleted id serves VRChat's generic marketing tags with a 200,
    # so require the description to actually be about this name.
    if not title or description != PROFILE_DESCRIPTION_PREFIX + title:
        return None

    return title


def fetch_display_name(user_id, delay):
    """One plain GET of the public profile page. Returns the display name or None."""
    request = urllib.request.Request(PROFILE_URL + user_id)
    request.add_header("User-Agent", USER_AGENT)
    request.add_header("Accept", "text/html")

    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            page = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        if error.code == 429:
            print("  rate limited, waiting 30s", file=sys.stderr)
            time.sleep(30)
            return fetch_display_name(user_id, delay)
        print("  %s: HTTP %d" % (user_id, error.code), file=sys.stderr)
        return None
    except (urllib.error.URLError, ValueError) as error:
        print("  %s: %s" % (user_id, error), file=sys.stderr)
        return None

    name = parse_profile_page(page)
    if name is None:
        print("  %s: no profile found (deleted account, or wrong id)" % user_id, file=sys.stderr)
    return name


def apply_cache(entries, cache):
    """Fills in names we already know. Free, offline, always worth doing."""
    filled = 0

    for entry in entries:
        if not entry.user_id:
            continue

        if not entry.name:
            cached = cache.get(entry.user_id)
            if cached:
                entry.name = cached
                filled += 1
        else:
            # Keep the cache honest even when we did not need it.
            cache.setdefault(entry.user_id, entry.name)

    return filled


def resolve_names(entries, cache, delay, refresh):
    """Fills in names the cache could not, by reading each public profile page."""
    resolved = 0
    api_calls = 0

    for entry in entries:
        if not entry.user_id:
            continue

        if entry.name and not refresh:
            continue

        if api_calls:
            time.sleep(delay)
        api_calls += 1

        name = fetch_display_name(entry.user_id, delay)
        if not name:
            continue

        if entry.name and entry.name != name:
            print("  %s renamed: %r -> %r" % (entry.user_id, entry.name, name))
        entry.name = name
        cache[entry.user_id] = name
        resolved += 1

    return resolved, api_calls


# ---------------------------------------------------------------------------
# output
# ---------------------------------------------------------------------------


def merge_duplicate_names(entries):
    """
    Collapses entries that ended up with the same display name.

    Parsing dedupes on id and on name, but an id has no name yet at that point.
    So pasting the profile link for somebody already on the list by name gives
    two entries that only become identical once the name resolves. Keep one,
    and make sure it is the one carrying the id, since that is the half that
    survives a rename.
    """
    kept_by_key = {}
    merged = []
    removed = 0

    for entry in entries:
        if not entry.name:
            merged.append(entry)
            continue

        # Section is part of the key: a blacklist and an immune entry sharing a
        # name are a conflict to leave visible, not something to silently merge.
        key = (entry.section, entry.name.lower())
        existing = kept_by_key.get(key)

        if existing is None:
            kept_by_key[key] = entry
            merged.append(entry)
            continue

        if entry.user_id and not existing.user_id:
            existing.user_id = entry.user_id
        if entry.note and not existing.note:
            existing.note = entry.note
        removed += 1

    return merged, removed


def rewrite_sources(paths, names_by_id):
    """
    Writes resolved names back into the source files, in place.

    This is what makes the GitHub workflow feel right: you paste a bare id or a
    profile link into the file in GitHub's editor, commit, and a minute later the
    line reads `usr_... | TheirName`. On a --refresh run a renamed account gets
    its line corrected the same way.

    Comments, blank lines, section headers and lines it cannot improve are left
    byte for byte alone, so the file stays yours.
    """
    changed_files = []

    for path in paths:
        with open(path, "r", encoding="utf-8-sig") as handle:
            original = handle.read()

        rebuilt_lines = []
        changed = False

        for raw in original.splitlines():
            line = raw.strip()

            if not line or line.startswith("#") or (line.startswith("[") and line.endswith("]")):
                rebuilt_lines.append(raw)
                continue

            entry, _ = parse_line(line)
            if entry is None or not entry.user_id:
                rebuilt_lines.append(raw)
                continue

            # A freshly resolved name wins, so a rename corrects the line.
            name = names_by_id.get(entry.user_id) or entry.name
            if not name:
                rebuilt_lines.append(raw)
                continue

            rebuilt = Entry(entry.user_id, name, entry.note).format()
            if rebuilt == line:
                rebuilt_lines.append(raw)
            else:
                rebuilt_lines.append(rebuilt)
                changed = True

        if not changed:
            continue

        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(rebuilt_lines) + "\n")
        changed_files.append(path)

    return changed_files


def fnv1a_signature(salt, payload_lines):
    """FNV-1a 32: salt, then every payload line followed by '\\n'.
    Must stay identical to BlacklistCore.cs and BlacklistBuilderWindow.cs."""
    hash_value = 2166136261
    mask = 0xFFFFFFFF

    for char in salt or "":
        hash_value = ((hash_value ^ ord(char)) * 16777619) & mask

    for line in payload_lines:
        for char in line:
            hash_value = ((hash_value ^ ord(char)) * 16777619) & mask
        hash_value = ((hash_value ^ 10) * 16777619) & mask

    return "%08x" % hash_value


def fnv1a64_hex(text):
    """FNV-1a 64 of an already-lowercased name. Mirrors BlacklistCore.Fnv1a64Hex."""
    hash_value = 14695981039346656037
    mask = 0xFFFFFFFFFFFFFFFF

    for char in text:
        hash_value = ((hash_value ^ ord(char)) * 1099511628211) & mask

    return "%016x" % hash_value


def build_output(entries, salt, sort_entries=True, hash_mode=False):
    blacklist = [e for e in entries if e.section == SECTION_BLACKLIST]
    immune = [e for e in entries if e.section == SECTION_IMMUNE]

    if sort_entries:
        blacklist.sort(key=lambda e: e.sort_key)
        immune.sort(key=lambda e: e.sort_key)

    matchable = [e for e in blacklist if e.name]
    unresolved = [e for e in blacklist if not e.name]

    payload = ["[blacklist]"]
    if hash_mode:
        # Only the hash goes out. Ids, names and reasons stay in the source file,
        # so the published list is not a public accusation about named people.
        payload.extend(fnv1a64_hex(e.name.lower()) for e in matchable)
    else:
        payload.extend(e.format() for e in blacklist)

    if hash_mode:
        immune_named = [e for e in immune if e.name]
        if immune_named:
            payload.append("[immune]")
            payload.extend(fnv1a64_hex(e.name.lower()) for e in immune_named)
    elif immune:
        payload.append("[immune]")
        payload.extend(e.format() for e in immune)

    header = [
        "# NEW GEN BLACKLIST V3",
        "# generated %s by build_blacklist.py - do not edit by hand, edit the source file"
        % datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"),
    ]

    if hash_mode:
        header.append("#!mode=hash")
        header.append("# entries are FNV-1a 64 hashes of the lowercased display name")
        header.append("# the world hashes each player's name and compares; nothing here names anybody")
    else:
        header.append("# format: usr_id | DisplayName | note    (any field may be omitted, '#' starts a comment)")
        header.append("# only DisplayName can be matched in-world: Udon has no access to usr_ ids")

    header.append("#!count=%d" % len(matchable))

    lines = list(header)
    lines.extend(payload)
    if salt:
        lines.append("#!sig=" + fnv1a_signature(salt, payload))

    return "\n".join(lines) + "\n", matchable, unresolved, immune


# ---------------------------------------------------------------------------
# self test
# ---------------------------------------------------------------------------


def selftest():
    """
    Known-answer tests. The signature vectors were produced by an independent
    implementation and verified against both BlacklistCore.cs and
    BlacklistBuilderWindow.cs, so if this passes, all three agree and a list
    signed here will be accepted in-world.
    """
    failures = []

    def check(label, got, want):
        if got != want:
            failures.append("%s: got %r, want %r" % (label, got, want))

    check("sig empty", fnv1a_signature("", []), "811c9dc5")
    check("sig single line", fnv1a_signature("", ["a"]), "2524c6d2")
    check("sig two lines", fnv1a_signature("", ["[blacklist]", "GrieferOne"]), "f444b780")
    check("sig salted", fnv1a_signature("paradise", ["[blacklist]", "GrieferOne"]), "be6d8967")
    check(
        "sig full file",
        fnv1a_signature(
            "paradise",
            [
                "[blacklist]",
                "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469 | GrieferOne",
                "KIKIKIKI7397",
                "[immune]",
                "RinsChan",
            ],
        ),
        "793adad6",
    )

    # FNV-1a 64, used for hashed lists. The first three are the published
    # reference vectors for FNV-1a 64; the rest were cross-checked against the
    # C# implementation in BlacklistCore.
    check("hash empty", fnv1a64_hex(""), "cbf29ce484222325")
    check("hash a", fnv1a64_hex("a"), "af63dc4c8601ec8c")
    check("hash foobar", fnv1a64_hex("foobar"), "85944171f73967e8")
    check("hash spaced name", fnv1a64_hex("kikikiki7397 8390"), "3eed27e398d8a835")
    check("hash punct name", fnv1a64_hex("-vinzen-"), "825f1898f606b14d")
    check("hash plain name", fnv1a64_hex("nanoohho"), "9eb34a275c65a12d")
    check("hash length", len(fnv1a64_hex("anything")), 16)

    # Profile page parsing, against the real tag shapes vrchat.com serves.
    # These fixtures were copied from live responses, so if VRChat changes the
    # page this test is what tells you, instead of the list quietly going stale.
    real_profile = (
        '<meta name="og:title" content="《ＳＡＩＮＴ》">'
        '<meta name="og:type" content="website">'
        '<meta name="og:image" content="https://api.vrchat.cloud/api/1/image/file_7cc0cb25/1/256">'
        '<meta name="og:image:alt" content="《ＳＡＩＮＴ》">'
        '<meta name="og:description" content="VRChat user 《ＳＡＩＮＴ》">'
        '<meta name="og:siteName" content="VRChat">'
    )
    check("profile unicode name", parse_profile_page(real_profile), "《ＳＡＩＮＴ》")

    plain_profile = (
        '<meta name="og:title" content="tupper">'
        '<meta name="og:description" content="VRChat user tupper">'
    )
    check("profile plain name", parse_profile_page(plain_profile), "tupper")

    # A wrong or deleted id answers 200 with VRChat's generic marketing tags.
    missing_profile = (
        '<meta name="og:title" content="VRChat">'
        '<meta name="og:description" content="VRChat lets you create, publish, and explore '
        'virtual worlds with other people from around the world.">'
    )
    check("profile missing user", parse_profile_page(missing_profile), None)

    # Entities decoded, apostrophe inside a double-quoted attribute survives.
    tricky_profile = (
        '<meta name="og:title" content="Bob&#39;s &amp; Co &quot;X&quot;">'
        '<meta name="og:description" content="VRChat user Bob&#39;s &amp; Co &quot;X&quot;">'
    )
    check("profile escaped name", parse_profile_page(tricky_profile), 'Bob\'s & Co "X"')

    # Attribute order and quote style must not matter.
    reordered_profile = (
        "<meta content='Zed' property='og:title'>"
        "<meta content='VRChat user Zed' property='og:description'>"
    )
    check("profile reordered tags", parse_profile_page(reordered_profile), "Zed")

    check("profile empty page", parse_profile_page(""), None)

    # profile link -> id, no name
    entry, problem = parse_line("https://vrchat.com/home/user/usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9")
    check("link id", entry.user_id, "usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9")
    check("link name", entry.name, "")
    check("link problem", problem, None)

    # trailing slash and query string must not end up inside the id
    entry, _ = parse_line("https://vrchat.com/home/user/usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9/?ref=x")
    check("dirty link id", entry.user_id, "usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9")

    # full triple
    entry, _ = parse_line("usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469 | GrieferOne | crashed the bar")
    check("triple id", entry.user_id, "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469")
    check("triple name", entry.name, "GrieferOne")
    check("triple note", entry.note, "crashed the bar")
    check("triple format", entry.format(), "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469 | GrieferOne | crashed the bar")

    # plain display names, including ones with spaces and digits
    entry, _ = parse_line("KIKIKIKI7397 3d5d")
    check("spaced name", entry.name, "KIKIKIKI7397 3d5d")
    check("spaced name id", entry.user_id, "")

    # the rejections that protect innocent players
    check("short name", parse_line("ab")[0], None)
    check("wildcard name", parse_line("Bad*Wildcard")[0], None)
    check("foreign link", parse_line("https://example.com/not-a-profile")[0], None)
    check("truncated id", parse_line("usr_abc")[0] is None, False)  # 'usr_abc' is a name, not an id

    # dedupe and merge across lines
    entry_a = Entry(user_id="usr_aaaaaaaa-0000-0000-0000-000000000000")
    entry_b = Entry(name="Griefer")
    check("sort key id", entry_a.sort_key, "usr_aaaaaaaa-0000-0000-0000-000000000000")
    check("sort key name", entry_b.sort_key, "griefer")

    text, matchable, unresolved, immune = build_output(
        [
            Entry(user_id="usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469", name="GrieferOne"),
            Entry(name="KIKIKIKI7397"),
            Entry(user_id="usr_dddddddd-0000-0000-0000-000000000000"),
            Entry(name="RinsChan", section=SECTION_IMMUNE),
        ],
        "paradise",
    )
    check("matchable count", len(matchable), 2)
    check("unresolved count", len(unresolved), 1)
    check("immune count", len(immune), 1)
    check("count header", "#!count=2" in text, True)
    check("sig line present", "#!sig=" in text, True)
    check("sections", text.count("[blacklist]") == 1 and text.count("[immune]") == 1, True)

    # the emitted file must verify against the salt the world holds
    payload = [
        line
        for line in text.splitlines()
        if line.strip() and not line.startswith("#")
    ]
    emitted_sig = [line for line in text.splitlines() if line.startswith("#!sig=")][0][6:]
    check("round trip sig", fnv1a_signature("paradise", payload), emitted_sig)
    check("wrong salt rejected", fnv1a_signature("notparadise", payload) != emitted_sig, True)

    # hashed output: no names anywhere, one 16 hex entry per matchable name
    hashed_text, hashed_matchable, _, _ = build_output(
        [
            Entry(user_id="usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469", name="GrieferOne", note="crashed the bar"),
            Entry(name="NANOOHHO"),
            Entry(user_id="usr_dddddddd-0000-0000-0000-000000000000"),
            Entry(name="RinsChan", section=SECTION_IMMUNE),
        ],
        "",
        True,
        True,
    )
    check("hash mode directive", "#!mode=hash" in hashed_text, True)
    check("hash mode count", len(hashed_matchable), 2)
    check("hash mode hides names", "GrieferOne" in hashed_text or "NANOOHHO" in hashed_text, False)
    check("hash mode hides ids", "usr_" in hashed_text, False)
    check("hash mode hides notes", "crashed the bar" in hashed_text, False)
    check("hash mode entry", fnv1a64_hex("nanoohho") in hashed_text, True)
    check("hash mode immune", fnv1a64_hex("rinschan") in hashed_text, True)

    hashed_payload = [
        line for line in hashed_text.splitlines() if line.strip() and not line.startswith("#")
    ]
    check(
        "hash mode payload shape",
        all(len(line) == 16 or line in ("[blacklist]", "[immune]") for line in hashed_payload),
        True,
    )

    # A pasted link resolving onto somebody already listed by name must collapse
    # into one entry that keeps the id.
    name_only = Entry(name="Griefer")
    from_link = Entry(user_id="usr_eeeeeeee-0000-0000-0000-000000000000", name="griefer", note="pasted later")
    unrelated = Entry(name="SomebodyElse")
    id_only = Entry(user_id="usr_ffffffff-0000-0000-0000-000000000000")
    same_name_immune = Entry(name="Griefer", section=SECTION_IMMUNE)

    merged_entries, merged_count = merge_duplicate_names(
        [name_only, from_link, unrelated, id_only, same_name_immune]
    )
    check("merge count", merged_count, 1)
    check("merge keeps one", len(merged_entries), 4)
    check("merge adopts the id", name_only.user_id, "usr_eeeeeeee-0000-0000-0000-000000000000")
    check("merge adopts the note", name_only.note, "pasted later")
    check("merge keeps id-only entries", id_only in merged_entries, True)
    check("merge does not cross sections", same_name_immune in merged_entries, True)

    # In-place source annotation: the pasted-id-becomes-a-name behaviour.
    import tempfile

    source_text = (
        "# keep this comment\n"
        "\n"
        "[blacklist]\n"
        "usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9\n"
        "https://vrchat.com/home/user/usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469\n"
        "usr_dddddddd-0000-0000-0000-000000000000 | OldName | note kept\n"
        "PlainNameNoId\n"
        "[immune]\n"
        "# trailing comment\n"
    )
    names = {
        "usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9": "SaintTest",
        "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469": "tupper",
        "usr_dddddddd-0000-0000-0000-000000000000": "NewName",
    }

    handle, temp_path = tempfile.mkstemp(suffix=".src", text=True)
    os.close(handle)
    try:
        with open(temp_path, "w", encoding="utf-8", newline="\n") as out:
            out.write(source_text)

        changed = rewrite_sources([temp_path], names)
        check("rewrite reports change", changed, [temp_path])

        with open(temp_path, "r", encoding="utf-8") as out:
            result = out.read().splitlines()

        check("rewrite keeps comment", result[0], "# keep this comment")
        check("rewrite keeps blank line", result[1], "")
        check("rewrite keeps section", result[2], "[blacklist]")
        check("rewrite annotates bare id", result[3], "usr_1a0ea4fe-404a-4b46-be0e-4d0fe5aa9ce9 | SaintTest")
        check("rewrite annotates link", result[4], "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469 | tupper")
        check("rewrite applies rename, keeps note", result[5], "usr_dddddddd-0000-0000-0000-000000000000 | NewName | note kept")
        check("rewrite leaves nameless line", result[6], "PlainNameNoId")
        check("rewrite keeps second section", result[7], "[immune]")
        check("rewrite keeps trailing comment", result[8], "# trailing comment")

        # Running it again must be a no-op, or the bot would commit every run.
        check("rewrite is idempotent", rewrite_sources([temp_path], names), [])
    finally:
        os.unlink(temp_path)

    if failures:
        for failure in failures:
            print("FAIL " + failure, file=sys.stderr)
        raise SystemExit("%d self test failure(s)" % len(failures))

    print("self test passed")


# ---------------------------------------------------------------------------
# cli
# ---------------------------------------------------------------------------


def main():
    # Checked before argparse so --selftest works on its own, without --src/--out.
    if "--selftest" in sys.argv:
        selftest()
        return

    parser = argparse.ArgumentParser(description="Compile the NEW GEN BLACKLIST V3 list file.")
    parser.add_argument("--selftest", action="store_true", help="run known-answer tests and exit")
    parser.add_argument("--src", nargs="+", required=True, help="source file(s) you edit by hand")
    parser.add_argument("--out", required=True, help="canonical list file the world downloads")
    parser.add_argument("--also-write", nargs="*", default=[], help="extra copies of the output (e.g. global_blacklist.txt)")
    parser.add_argument("--cache", default="known_users.json", help="usr_ id -> display name cache")
    parser.add_argument("--salt", default=os.environ.get("BLACKLIST_SALT", ""), help="signature salt; must match BlacklistCore")
    parser.add_argument(
        "--hash",
        action="store_true",
        help="publish FNV-1a 64 hashes instead of display names, so the public file names nobody",
    )
    parser.add_argument("--resolve", action="store_true", help="look up missing display names from public profile pages")
    parser.add_argument("--refresh", action="store_true", help="re-check every id, catching griefers who renamed")
    parser.add_argument(
        "--write-src",
        action="store_true",
        help="write resolved names back into the source file, so a pasted id becomes 'usr_... | Name'",
    )
    parser.add_argument("--delay", type=float, default=1.5, help="seconds between requests (default 1.5)")
    parser.add_argument("--check", action="store_true", help="exit 1 if --out is not already up to date")
    parser.add_argument("--no-sort", action="store_true", help="keep source order instead of sorting")
    args = parser.parse_args()

    missing = [p for p in args.src if not os.path.exists(p)]
    if missing:
        raise SystemExit("source file(s) not found: " + ", ".join(missing))

    entries, problems = parse_sources(args.src)

    for problem in problems:
        print("skipped: " + problem, file=sys.stderr)

    cache = load_cache(args.cache)
    filled = apply_cache(entries, cache)
    if filled:
        print("filled %d name(s) from the local cache" % filled)

    if args.resolve or args.refresh:
        resolved, api_calls = resolve_names(entries, cache, args.delay, args.refresh)
        print("resolved %d name(s) from public profile pages (%d request(s))" % (resolved, api_calls))

    entries, merged = merge_duplicate_names(entries)
    if merged:
        print("merged %d entry/entries that resolved onto a name already listed" % merged)

    if not args.check:
        save_cache(args.cache, cache)

        if args.write_src:
            names_by_id = dict((e.user_id, e.name) for e in entries if e.user_id and e.name)
            rewritten = rewrite_sources(args.src, names_by_id)
            for path in rewritten:
                print("annotated " + path)

    text, matchable, unresolved, immune = build_output(entries, args.salt, not args.no_sort, args.hash)

    print(
        "%d matchable blacklist entries, %d id-only (inert in-world), %d immune%s"
        % (len(matchable), len(unresolved), len(immune), ", hashed" if args.hash else "")
    )

    if unresolved:
        print("  id-only entries, run with --resolve to fix:")
        for entry in unresolved[:20]:
            print("    " + entry.user_id)
        if len(unresolved) > 20:
            print("    ...and %d more" % (len(unresolved) - 20))

    if args.check:
        if not os.path.exists(args.out):
            raise SystemExit("check failed: %s does not exist" % args.out)
        with open(args.out, "r", encoding="utf-8") as handle:
            current = handle.read()
        # The generated-at comment always differs, so compare everything else.
        if strip_timestamp(current) != strip_timestamp(text):
            raise SystemExit("check failed: %s is stale, re-run without --check" % args.out)
        print("check passed: %s is up to date" % args.out)
        return

    for path in [args.out] + list(args.also_write):
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        # The '# generated' stamp changes on every run. Rewriting the file for
        # that alone would make the scheduled job commit every few hours with no
        # real change in it, so only write when the list itself actually moved.
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as handle:
                existing = handle.read()
            if strip_timestamp(existing) == strip_timestamp(text):
                print("unchanged " + path)
                continue

        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        print("wrote " + path)


def strip_timestamp(text):
    return "\n".join(line for line in text.splitlines() if not line.startswith("# generated "))


if __name__ == "__main__":
    main()
