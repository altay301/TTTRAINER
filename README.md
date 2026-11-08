# TTTRAINER

A distracting-site blocker for Windows. It blocks sites **24/7** and only takes a 10-minute break
at the top of every hour: **07:00–07:10**, **08:00–08:10**, and so on, around the clock. It has a small terminal-style UI in PowerShell colors.

## How it works

- Blocked sites are written to the Windows `hosts` file, so they are blocked in every browser.
  DNS-over-HTTPS is turned off in Chrome, Edge, Brave and Firefox so browsers cannot bypass the block.
- A Windows service named **FocusLock** runs in the background (as SYSTEM, every 5 seconds):
  - If you delete the block from the `hosts` file, it writes it back within 5 seconds.
  - Restarting the computer does not lift the block.
  - During break hours it lifts the block by itself and puts it back when the break ends.
- The service permissions are locked down: even an administrator account cannot stop or delete it directly.
- **Adding sites** is always allowed. **Removing sites** and **uninstalling TTTRAINER** only work during break hours.

## Usage

Open `TTTRAINER.exe` (it asks for administrator permission).

| What | How |
|---|---|
| Add / remove a site | Type the site name on the `>` line and press Enter. If the site is on the list it is removed, otherwise it is added. The color of `>` shows the result: green = added, yellow = removed, red = not done. |
| Start | `START` (cyan = not installed, green = running) |
| Uninstall | Right-click `START` during break hours |
| ASCII art | Click the art and `<  -  +  >` appears below it: previous / delete (press twice) / add a new art by pasting / next |

ASCII art you add is stored in `%APPDATA%\TTTRAINER\arts`.

## Emergency exit

Steps to remove it from Safe Mode: [EMERGENCY-EXIT.txt](EMERGENCY-EXIT.txt).

## Building

Nothing extra to install. It uses the .NET Framework 4 compiler that ships with Windows:

```
build.cmd
```

Output: `TTTRAINER.exe`.

## Licenses

- Code: MIT, see [LICENSE](LICENSE).
- UI font: [IBM 3270](https://github.com/rbanffy/3270font), © Ricardo Banffy and contributors.
  BSD licensed; license text: [src/fonts/3270-LICENSE.txt](src/fonts/3270-LICENSE.txt).
  It is embedded in the exe and is not installed into Windows.
