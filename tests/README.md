# Package checks

These checks run the upstream r2modman installer against actual ZIP contents using a filesystem provider pointed at a fresh test directory. They also launch the installed Windows browser helper and read its named-pipe output. They do not control a manager UI or test Unity or a multiplayer lobby.

## Installer regression

Requirements: Node.js 24 or newer, Git, and the old 0.1.9 ZIP plus the corrected ZIP.

```powershell
git clone --depth 1 https://github.com/ebkr/r2modmanPlus.git .\work\r2modman-installer
npm install --prefix tests --ignore-scripts
node --experimental-vm-modules tests/manager-install.mjs `
  .\work\r2modman-installer `
  .\OnTogetherSchoolScreen-0.1.9.zip `
  .\OnTogetherSchoolScreen-0.1.11.zip `
  .\work\manager-check
```

Use a fresh output directory each run. The check reads On-Together's rules from the upstream ecosystem schema and executes `InstallRulePluginInstaller`, `FileTree`, `InstallationRules`, and `FileUtils` from that checkout. Type-only module bindings and unused provider dependencies are supplied by the harness; routing and installation functions are unchanged. File operations use real package bytes and are restricted to the test output directory.

The check reproduces the old flattened HTML path and native-loader collision, then checks uninstall/upgrade, file hashes, the preserved `www/index.html` path, one native loader, and disable/enable. It leaves the new installation available for the browser check and writes `installer-report.json`.

## Installed browser helper

Requirements: Windows, .NET 8 or newer, and Microsoft Edge WebView2 Runtime.

Use the `browserPath` returned by the installer check:

```powershell
dotnet run --project tests/BrowserSmoke --configuration Release -- `
  .\work\manager-check\profile\BepInEx\plugins\QualityControled-OnTogetherSchoolScreen\SchoolScreenBrowser.exe `
  .\work\manager-check\browser
```

An optional third argument is a YouTube video ID. Playback is muted and the check requires a playing-state report for that ID and load. Without it, the check verifies helper startup, successful local page navigation, IPC, and a valid captured JPEG frame. Each run uses a separate WebView2 data folder, closes its own helper afterward, and writes `browser-report.json` and `browser-frame.jpg`.

To complete an end-to-end check, import the ZIP through the manager UI in a separate profile, launch the game modded, and check the board and multiplayer controls in a lobby.

## Reporting transport regression

The dependency-free .NET check scans the compiled plugin's references, IL strings, and metadata without loading Unity or the game. It rejects reporting, ban, and kick APIs and the removed reporting protocol marker. Include the old 0.1.10 DLL as a positive control:

```powershell
dotnet run --project tests/ReportTransportCheck --configuration Release -- `
  .\OnTogetherSchoolScreen-0.1.11\BepInEx\plugins\OnTogetherSchoolScreen.dll `
  .\OnTogetherSchoolScreen-0.1.10\BepInEx\plugins\OnTogetherSchoolScreen.dll
```

## Whiteboard packet envelope

This check links the same envelope helper used by the plugin. It checks every 24-bit integer payload for exact round trips and ensures the encoded previous X is negative, which avoids the vanilla drawing code's stroke interpolation. It also checks the small-erasure flags and legacy positive payload decoding.

```powershell
dotnet run --project tests/BoardPacketEnvelopeCheck --configuration Release
```

These checks do not test a live lobby. For the 0.1.11 fix, also check with an unmodded host, confirm no kick dialog or paint-color errors appear, add a second modded player after a queue exists, and exercise names, host queue blocking, skip voting, and lobby changes. Vanilla recipients still process each packet as a small eraser dab at a corner of the board.
