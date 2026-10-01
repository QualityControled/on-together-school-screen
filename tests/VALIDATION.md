# Version 0.1.10 validation

Checked on Windows on 2026-10-01.

## Passed

- Plugin and browser Release builds: zero warnings and errors.
- Upstream r2modman 3.2.20 installer code, commit `a1897e3d6f7c1946dc51312252db1cc25f1dace0`, using its bundled On-Together `OnTogetherVirtualCoWorking` routing rules and real files in a fresh directory.
- Reproduced both 0.1.9 defects: `www/index.html` installed as `index.html`, and the two native DLL paths collided at one installed filename.
- Uninstalled the old package and installed 0.1.10: `www/index.html` retained its path; the helper and dependencies stayed together; the installed payload matched ZIP file hashes; one native loader was packaged; no copy destinations collided.
- Disabled and enabled the installed package with the same upstream installer.
- Launched the installed helper using the single x64 native loader and a separate WebView2 data folder. Its local page loaded, named-pipe communication worked, and it captured a valid 768 by 384 JPEG. The captured waiting page was visually inspected.
- Loaded video `pqsgpG_OeM8` while muted and received `STATE|0.0|1|This is not a Wii remote...|pqsgpG_OeM8|1` from the installed helper.

## Still untested

- Local ZIP import through Thunderstore Mod Manager or r2modman's actual UI. The desktop-control tool failed to initialize with `failed to write kernel assets: The system cannot find the path specified. (os error 3)`, including retries after reset.
- Launching On-Together through the manager and checking Unity integration, distance-based audio, automatic queue progression, skip votes, and multiplayer synchronization in a live lobby.

The automated installer check exercises production installer functions with filesystem/provider adapters. It is not a claim that the manager UI or the full game was tested. See [instructions](README.md) to reproduce the checks.
