# PodBattery

**An AirPods battery indicator for the Windows system tray.**

See the battery level of your left earbud, right earbud and case, and whether each is charging, right in the Windows notification area. No iPhone needed.

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
powershell -ExecutionPolicy Bypass -File .\install.ps1     # options: -NoStartup, -NoDesktopShortcut
```

This builds the app into `%LOCALAPPDATA%\Programs\PodBattery`, adds **PodBattery** shortcuts to the Start menu and desktop, starts it, and adds it to your login items. Run it again to update.

If you choose **Exit** by accident, start it again from either shortcut (or just sign in again). Opening it while it's already running shows a notification pointing to its icon.

## First run: "Find my AirPods"

**Open your AirPods case right next to the PC for a few seconds.** That's how PodBattery learns which AirPods are yours, which matters in an office full of identical ones. After that it follows your AirPods on its own while you wear them. If it ever loses track, for example after they've been in the case for a while, open the case next to the PC again. **Find my AirPods…** in the menu does the same thing, and also overrides any AirPods it's currently tracking.

## How it works

AirPods constantly broadcast a small Bluetooth LE message (Apple's *proximity pairing* message) containing the model, each earbud's battery level, the case battery and charging flags. iPhones use it for the battery popup. PodBattery only listens for these broadcasts; it never connects to or sends anything to the AirPods.

The broadcasts don't say whose AirPods they are, and each pair changes its Bluetooth address about every 15 minutes, so PodBattery:

1. **Ignores other models.** It only accepts the model IDs of AirPods paired with this PC.
2. **Locks on** when a case is opened right next to the PC. The broadcast says whether both earbuds are in the case with the lid open, and the signal must be very strong. AirPods in someone's ears never get picked up on their own, however close they are. **Find my AirPods…** also accepts AirPods held next to the PC in any state, as long as the signal stays strong over several broadcasts.
3. **Follows** them across address changes. A new address appears within seconds of the old one going quiet. If one appears at that moment with the same battery levels and lid state, is heard more than once, and has a similar signal strength, it's yours. Addresses that were already around can't be.

See [`PodTracker.cs`](src/PodBattery/PodTracker.cs) for the rules and [`ProximityPairing.cs`](src/PodBattery/ProximityPairing.cs) for the message format.

### Tests

`dotnet test` runs about 60 tests:

- **Decoder unit tests.** Each checks the decoder against a real AirPods broadcast.
- **Tracker unit tests.** One for each rule.
- **Replays of real recordings.** About 25 minutes of broadcasts from a busy office with 20+ AirPods nearby, with each address replaced by a made-up ID. One test checks that the tracker never shows a neighbour's battery. The *adversarial* scenarios come from a review that set out to break the tracker: walking away from your desk, coming back, reception gaps, the app starting with neighbours already around, and Windows' "out of range" reports.

## Limitations

- **10% steps.** The exact percentages are in an encrypted part of the broadcast that only Apple devices can read.
- **Closed case = no updates.** AirPods stop broadcasting in a closed case, so the icon turns grey and shows the last known levels until you open it.
- **Long absences need the case again.** If your AirPods are away for longer than an address change (a few minutes or more), wearing them won't be picked up again until you open the case next to the PC. In your ears, their signal isn't strong enough to tell them apart from a neighbour's.
- **Identifying your pair is a best guess.** One rare case is still ambiguous: you walk away wearing your AirPods, and within about 30 seconds a neighbour's pair with the same battery levels changes address at a similar signal strength. In the replay tests that happens in about 1% of timings. If it ever shows someone else's AirPods, open your case next to the PC; that always corrects it.

## Settings

`%APPDATA%\PodBattery\settings.json`:

| Setting | Default | Meaning |
|---|---|---|
| `LockRssi` | `-50` | Signal strength (dBm) that counts as "right next to the PC". Use a value closer to 0 (e.g. `-45`) if it ever locks on to a neighbour's AirPods, or further from 0 (e.g. `-55`) if it struggles to find yours. |
| `Log` | `false` | Write every AirPods broadcast and the decision made about it to `adverts.log` in the same folder. Run `PodBattery.exe --log` for the same effect once. |

## Uninstall

Untick **Start with Windows**, then choose **Exit** from the menu. (Alternatively, delete the `PodBattery` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.) Then delete `%LOCALAPPDATA%\Programs\PodBattery`, `%APPDATA%\PodBattery` and the two `PodBattery` shortcuts: one on the desktop, the other in `%APPDATA%\Microsoft\Windows\Start Menu\Programs`.

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
