# PodBattery

A tiny Windows tray app that shows your AirPods battery levels: left, right, case, and whether each is charging.

The tray icon shows the lower of your two earbuds' battery levels, colored green, amber or red. Hover over it for a summary. Click it to see:

```
Left      60%
Right     70%
Case      40%  ⚡ charging
AirPods Pro 2 (USB-C) · updated just now
──────────────
Find my AirPods…
Start with Windows
Exit
```

There's no account, no network access, no admin rights and no Microsoft Store. It's a few hundred lines of C# that use Windows' built-in Bluetooth API.

## Install

You need Windows 10 1809 or later (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/muradzalov/PodBattery.git
cd PodBattery
powershell -ExecutionPolicy Bypass -File .\install.ps1     # add -NoStartup to skip starting with Windows
```

This builds the app into `%LOCALAPPDATA%\Programs\PodBattery`, starts it, and adds it to your login items. Run it again to update.

## First run: "Find my AirPods"

On first launch, and whenever you choose **Find my AirPods…**, **open your AirPods case right next to the PC**. This tells PodBattery which AirPods are yours, which matters in an office full of identical ones. After that it follows your AirPods on its own. If it ever loses track, for example after they've been in the case for a while, opening the case near the PC picks them up again.

## How it works

AirPods constantly broadcast a small Bluetooth LE message (Apple's *proximity pairing* message) containing the model, each earbud's battery level, the case battery and charging flags. iPhones use it for the battery popup. PodBattery only listens for these broadcasts; it never connects to or sends anything to the AirPods.

The broadcasts don't say whose AirPods they are, and each pair changes its Bluetooth address about every 15 minutes, so PodBattery:

1. **Ignores other models.** It only accepts the model IDs of AirPods paired with this PC.
2. **Locks on** to AirPods with a very strong signal, meaning your case opened right next to the PC.
3. **Follows** them across address changes. When your current address goes quiet, a new address broadcasting the same battery levels and lid counter at a similar signal strength is yours.

See [`PodTracker.cs`](src/PodBattery/PodTracker.cs) for the rules and [`ProximityPairing.cs`](src/PodBattery/ProximityPairing.cs) for the message format.

## Limitations

- **10% steps.** The exact percentages are in an encrypted part of the broadcast that only Apple devices can read.
- **Closed case = no updates.** AirPods stop broadcasting in a closed case, so the icon turns grey and shows the last known levels until you open it.
- **Identifying your pair is a best guess.** If it ever shows someone else's AirPods, choose **Find my AirPods…** and open your case next to the PC.

## Settings

`%APPDATA%\PodBattery\settings.json`:

| Setting | Default | Meaning |
|---|---|---|
| `LockRssi` | `-50` | Signal strength (dBm) that counts as "right next to the PC". Use a value closer to 0 (e.g. `-45`) if it ever locks on to a neighbour's AirPods, or further from 0 (e.g. `-55`) if it struggles to find yours. |
| `Log` | `false` | Write every AirPods broadcast and the decision made about it to `adverts.log` in the same folder. Run `PodBattery.exe --log` for the same effect once. |

## Uninstall

Choose **Exit** from the menu and untick **Start with Windows** first (or delete the `PodBattery` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`). Then delete `%LOCALAPPDATA%\Programs\PodBattery` and `%APPDATA%\PodBattery`.

## Develop

```powershell
dotnet test                                # unit tests for the decoder and tracker
dotnet run --project src/PodBattery -- --log
```

## Credits

The message format was worked out by the [OpenPods](https://github.com/adolfintel/OpenPods) project and the research paper [*Discontinued Privacy: Personal Data Leaks in Apple Bluetooth-Low-Energy Continuity Protocols*](https://doi.org/10.2478/popets-2020-0003) (Celosia & Cunche, PETS 2020).

Not affiliated with Apple. AirPods is a trademark of Apple Inc.

## License

[MIT](LICENSE)
