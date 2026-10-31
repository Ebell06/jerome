# TOOLS.md — every action Jerome can take

Every voice command Jerome recognizes is listed here, with a risk class.
If it's not on this list, Jerome should not be able to do it — if you find
code doing something not documented here, that's a bug, not a feature.

**Risk classes:**
- `SAFE` — reversible, low-impact, no real-world consequence beyond your own screen
- `RISKY` — closes running programs or windows; still local-only, still reversible
- `DENY` — not implemented, and should never be, without this file being
  updated *first* and a human deciding that on purpose

| Trigger phrase | Action | Risk | Notes |
|---|---|---|---|
| "hey jerome" | Opens Chrome (Claude tab + hub dashboards + hologram), speaks, plays Spotify, focuses Cursor | SAFE | Nothing destructive; only opens/starts things |
| "next song" / "skip song" | Sends Spotify's global media-skip key | SAFE | No file/process access |
| "play `<song>`" | Searches Spotify's public catalog, plays top result | SAFE | Read-only search + same playback path as above |
| "shut down" / "goodnight jerome" | Closes Spotify, Cursor, and the hub windows; Jerome keeps running | RISKY | Closes *by name/title match only* — never a blind "close everything" |
| "jerome power off" | Same as above, plus exits the Jerome process itself | RISKY | Only exits Jerome's own process — never touches unrelated programs |
| *(kill file)* | Immediate hard exit, no cleanup | RISKY | See KILLSWITCH.md — bypasses voice recognition entirely on purpose |

## Explicitly out of scope (DENY)
Jerome does not, and must not be extended to without updating this file first:
- Read, write, move, or delete any file outside its own project folder
- Send messages, emails, or make any purchase or payment
- Access webcam or take screenshots
- Modify Windows settings, registry, or other installed software
- Accept commands from any source other than the local microphone
  (no network-based remote control)
