# How the stored values of Recommended were checked (2026-10-25)

> **Describes the 0.3 catalog.** 0.4 re-audited every entry and removed 103 of the 251 (see CHANGELOG.md); the numbers and lists below are those of 0.3. This document stays as the record of how those entries were built and checked.

Mesura's two main profiles apply only settings whose stored value is **documented** by Microsoft or **verified** as
described here. A setting known only from community sources (`community` in the catalog) can still be chosen one by one,
but is never part of a profile's automatic selection. This page says what "verified" means, what was checked for each
entry, and what was **not** checked.

## What was done

All checks were read-only. Nothing was installed, toggled, written or changed on the PC.

1. **The name check.** Every `.dll` and `.exe` under `C:\Windows\System32`, `SystemApps` and `ImmersiveControlPanel` (and
   the policy templates in `PolicyDefinitions`) was searched for the value name, as UTF-16 and as ASCII text. A name
   found in the component that implements the feature (the Settings handlers, the Start menu host, the lock-screen
   Spotlight component, the content-delivery components) shows that this Windows build reads or writes that value. A name
   found in no component is not used here.
2. **The toggle check.** The Windows Settings pages were opened (`ms-settings:` links) and every switch and checkbox on
   them was read through UI Automation, with its automation id and its state. The registry values were read at the same
   time. A switch whose id carries the value's name, or whose label is the setting's, and whose state agrees with the
   value, ties the value to the setting.
3. **A control.** The language-list value is documented by Microsoft (`HttpAcceptLanguageOptOut = 1` turns the sharing
   off). The Settings switch "Allow websites to access my language list" read Off with the value at 1, as documented, so
   the method gives the answer the documentation does.

## What it does not show

- The PC the checks ran on is a trimmed Windows 11 25H2 Pro image (build 26200) where these settings were already off.
  So only one direction was observed: **value 0 and the switch Off**. The On state (value 1) was observed for Game Mode
  only. Nothing was switched to the other state to watch the value change; that would have changed the PC.
- That the switch is backed by that value rather than another that is also 0 on this PC rests on the name check (the
  value is in the implementing component) and on the switch's automation id, not on watching a write.
- Whether the visible effect happens (fewer promotions in Start, no tips) was not measured.
- Other builds of Windows 11, and Windows 10, were not checked.

## Results

| Catalog entry | Value | Name found in | Settings control and state | Basis |
|---|---|---|---|---|
| `start.no-recommendations` | `Start_IrisRecommendations` = 0 | Settings, Start tile data, taskbar and shell components (10 files) | "Show recommendations for tips, shortcuts, new apps, and more": Off, value 0 | verified |
| `start.no-account-notifications` | `Start_AccountNotifications` = 0 | the Start menu host (StartDocked.dll), Settings components (9 files) | "Show account-related notifications": Off, value 0 | verified |
| `notifications.no-tips-and-suggestions` | `SubscribedContent-338389`, `-338388`, `-310093`, `-338393`, `-353694`, `-353696`, `SystemPaneSuggestionsEnabled`, `SoftLandingEnabled` | the content-delivery components (`ContentDeliveryManager.Utilities`, `Windows.Services.TargetedContent`, `ContentDeliveryManager.Background`), SoftLandingTask, Settings | "Get tips and suggestions when using Windows" and "Recommendations and offers in Settings": Off, values 0 | verified |
| (same) | `SilentInstalledAppsEnabled` | **no component** | none | **removed from the entry** |
| `notifications.no-setup-nags` | `ScoobeSystemSettingEnabled` = 0 | Settings handlers, IrisService, shell (3 files) | "Suggest ways to get the most out of Windows and finish setting up this device": Off, value 0 | verified |
| `lockscreen.no-spotlight-ads` | `RotatingLockScreenOverlayEnabled`, `SubscribedContent-338387` | the lock-screen Spotlight component, content delivery, IrisService | "Get fun facts, tips, tricks, and more on your lock screen": Off, value 0 | verified |
| `gaming.game-mode-on` | `AutoGameModeEnabled` = 1 | Game Mode settings handler, game panel, shell (4 files) | "Game Mode": **On**, value 1 | verified |
| `privacy.no-advertising-id` | `AdvertisingInfo\Enabled` = 0 | system and telemetry components, the first-run experience and the policy template (7 files); Microsoft documents the same name and data under HKLM | "Advertising ID": Off, value 0 | verified |
| `privacy.no-tailored-experiences` | `TailoredExperiencesWithDiagnosticDataEnabled` = 0 | the diagnostics settings handler, the telemetry service and content components (15 files) | "Personalized offers": Off, value 0 (the link from that switch to this value is inferred from the name) | verified |
| `notifications.off` (an unticked extra) | `ToastEnabled` = 0 | not searched (the switch check alone) | "Notifications" (the master switch): Off, value 0 | verified |
| `search.no-highlights` | `IsDynamicSearchBoxEnabled` = 0 | search components (2 files) | **no switch found** on this build | community |
| `notifications.no-sync-provider-ads` | `ShowSyncProviderNotifications` = 0 | `shell32.dll` | the File Explorer Options checkbox could not be read through UI Automation | community |
| `gaming.background-recording-off`, `gaming.background-capture-off` | `HistoricalCaptureEnabled`; `GameDVR_Enabled`, `AppCaptureEnabled` | not searched | "Record what happened": Off (value absent: default); no switch for the other two | community |

The two `community` entries that Recommended used to hold are therefore out of it; they stay in the *Quiet Windows* and
*Clean taskbar and Start* collections, with a label saying so in the app.

## When to redo it

Redo the name and toggle checks on a new Windows build before moving a `community` entry up, and after a Windows feature
update that changes the Start menu or Settings, because the pages and the components move between builds.
