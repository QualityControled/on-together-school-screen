# On-Together School Screen

**Press F9 to open the menu.**

Windows mod that plays shared YouTube videos on the whiteboard inside the school building.

## Features

- Uses a purple library-style F9 menu with **Now playing**, **Lobby queue**, **Playlists**, and **Access** tabs.
- Finds the school's `DrawingBoard` and displays a local Chromium-based Microsoft Edge WebView2 player on it.
- Lets any player with the mod add videos and start the queue when the board is idle. The game host does not need the mod.
- Automatically plays the next queued video when the current video finishes, in queue order.
- Sends playback and queue updates through the school's networked whiteboard RPC. It does not send synchronization text through chat.
- Displays the playing video's title in the panel and resolves queue entries to video titles.
- Shows video thumbnails, titles, available durations, and the person who added each queued video.
- Saves private playlists on your computer so you can queue saved videos directly from F9, individually or as a selection.
- Limits additions to three waiting videos per person and ten seconds between accepted additions by default. A modded game host can adjust these limits in Access.
- Lets modded players vote to skip; the vote passes at 30% of active modded players, rounded up with at least one yes vote. The game host can skip immediately if they have the mod.
- Gives a modded game host an Access tab to block specific players from adding videos to the queue.
- Clears playback, queue, votes, and queue blocks when the lobby changes.
- Keeps the hidden browser helper out of Alt+Tab while it renders the whiteboard video.
- Gives each player an independent volume control with distance falloff, a 0–200% slider, and mute.
- Loads video and audio locally for each player; video frames are not sent over the game network.
- Targets a 30 FPS whiteboard refresh while the game can keep up.
- Shows a color-changing, bouncing DVD-style logo behind the F9 prompt while the board is waiting for a video.
- Celebrates a DVD corner hit with a brief visual effect.

## Install

Download the ZIP from [GitHub Releases](https://github.com/QualityControled/on-together-school-screen/releases/latest).

### Mod manager

Install through Thunderstore Mod Manager or r2modman. For a local ZIP, use the manager's **Import local mod** option and choose the package. The manager installs the mod files together under its profile's `BepInEx/plugins` folder.

### Manual installation

1. Close On-Together before replacing the mod files.
2. Install BepInEx for On-Together and the Microsoft Edge WebView2 Runtime.
3. Extract the ZIP and copy its **BepInEx** folder into the game folder or the manager's profile folder. The package places the plugin, browser helper, WebView2 DLLs, and `www` folder together inside `BepInEx/plugins`. For manual updates, replace your previous School Screen installation with these files so only one copy of the plugin is loaded. The `www` folder must stay beside `SchoolScreenBrowser.exe`.
4. Everyone who wants to watch together should install version 0.1.12 and enter the school. Press `F9` to open the controls. The host does not need the mod for playback, queueing, or skip voting. Adding a video starts playback when the board and queue are empty; otherwise it adds to the end of the queue. Videos advance automatically when they finish. If the board is stopped with videos still queued, anyone can press **Play Next** in the Queue tab to start the first entry.

### Updating from an older version

Upgrade to **0.1.12** and replace your previous School Screen files. All modded viewers in the lobby should update together so queue confirmations and selected-playlist requests work consistently.

Version 0.1.11 removes a player-discovery message that older versions sent through the game's report-player system. That message could repeatedly open a **Kick Player?** dialog on a host without the mod. All School Screen users in the lobby should update to 0.1.12; clients older than 0.1.11 can still cause these dialogs.

If a host already has these dialogs, everyone using an older version should leave the lobby to stop new requests. The host can repeatedly click **No** until the queued dialogs are dismissed, keeping the lobby open. An updated client cannot clear dialogs already stored on the host's computer. Avoid clicking **Yes**, which changes the host's ban list.

## Host and queue moderation

Videos play in queue order. Viewers can add videos, pause or resume playback, and vote to skip. They cannot choose a later queue entry, stop the board, or seek past a video.

When the game host has the mod, they can skip immediately, choose a specific queued video, stop or seek playback, remove queue entries, and block players from adding videos in the Access tab. When the host does not have the mod, one modded player coordinates automatic playback, queue updates, and skip votes. Coordination transfers if that player leaves. In that case there is no host-side block list; viewers still use queue order and skip voting. Joining viewers receive the current video, queue, modded-player roster, and existing votes. Players without the mod are excluded from skip-vote counts.

## Saved playlists and queue limits

Open **Playlists** in F9 to create a collection such as Music or Study. Add YouTube links once, then select saved videos and use **Queue selected**, or queue one video directly. **Save queue** copies the lobby's waiting videos into a local playlist. Collections support up to 30 videos each, with up to 20 collections. Removal, replacement, and deletion offer Undo.

Your collections are stored only in your own `BepInEx/config/OnTogetherSchoolScreen.playlists.xml` file. They remain available when you change lobbies. Videos you submit to the lobby queue are shared with the modded viewers there.

By default, each person may have three videos waiting and must wait ten seconds between accepted additions. The currently playing video does not count. A selected group uses one cooldown and adds only the videos that fit your remaining allowance; the menu reports how many were added and leaves the remaining saved tracks selected. An updated modded player enforces these defaults even when the game host does not have the mod. A modded game host can adjust the limits under **Access**.

Everyone using the mod should install **0.1.12**: older builds do not understand queue confirmations or selected-playlist requests. After the queue coordinator leaves, waiting-video ownership transfers but the addition cooldown clock can reset. A video's duration may be unavailable; live videos show no fixed duration.

## Volume behavior

Each player's volume is local. It is full near the board, falls with distance, and reaches silence 18 Unity units from the board. The 0–200% control compensates for that falloff; YouTube's embedded player accepts volume only from 0 to 100, so the extra range lets a player farther away raise quiet playback back up to the player's normal maximum. It does not amplify beyond YouTube's 100% level. The volume setting is not saved between game sessions.

The cutoff is based on distance from the board rather than the school's room boundary, so a player just outside a wall may still hear it if they are within range.

## Limits

- Everyone watching together should use 0.1.12. It includes the 0.1.11 whiteboard compatibility fixes and the new queue confirmations and selected-playlist requests. Clients older than 0.1.11 can still send the faulty player-discovery requests.
- Players without the mod still process each message as a small eraser dab, clearing one drawing cell at the school whiteboard's top-right corner. They do not see or hear the mod's videos.
- The mod and browser helper target Windows only. Each player needs WebView2 Runtime installed.
- The local WebView2 player targets up to 30 board-image updates per second. Actual smoothness depends on game performance and WebView2 capture speed.
- YouTube must allow the video to be embedded. Some private, age-restricted, or region-restricted videos may not play.

The game and its assets are not included. WebView2 is provided by Microsoft and is not bundled with this mod.

## Build from source

Requirements: Windows, the .NET SDK, the .NET Framework 4.8 developer pack, On-Together's game-managed assemblies, and BepInEx 5's `core` folder.

Build the plugin by passing the paths on the command line:

```powershell
dotnet build .\src\SchoolScreenMod\SchoolScreenMod.csproj `
  -p:GameManagedDir="C:\path\to\OnTogether_Data\Managed" `
  -p:BepInExCoreDir="C:\path\to\BepInEx\core"
```

`GameManagedDir` must contain `Assembly-CSharp.dll`; `BepInExCoreDir` must contain `BepInEx.dll` and `0Harmony.dll`. You can set `ON_TOGETHER_MANAGED_DIR` and `BEPINEX_CORE_DIR` environment variables instead of passing the MSBuild properties. Build the browser helper separately with:

```powershell
dotnet build .\src\SchoolScreenBrowser\SchoolScreenBrowser.csproj --configuration Release
```

The browser helper uses Microsoft's WebView2 NuGet package. Players also need the WebView2 Runtime installed. Do not commit game assemblies, BepInEx files, generated `bin`/`obj` folders, or packaged release binaries to the source repository.

Build a mod-manager package with:

```powershell
.\scripts\Build-Package.ps1 `
  -GameManagedDir="C:\path\to\OnTogether_Data\Managed" `
  -BepInExCoreDir="C:\path\to\BepInEx\core"
```

The package builder keeps metadata at the ZIP root and puts the runtime files inside `BepInEx/plugins`. See [test instructions](tests/README.md) and [recorded validation](tests/VALIDATION.md) for installation checks and their limits.

## Repository

GitHub: [QualityControled/on-together-school-screen](https://github.com/QualityControled/on-together-school-screen)

The source code and build instructions are available here. Packaged Windows downloads are available under [Releases](https://github.com/QualityControled/on-together-school-screen/releases).
