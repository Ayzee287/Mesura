# Recommended and Maximum performance: what they hold and why

Review of 2026-10-25. This page is generated from the catalog's profile definitions plus the reasons below; the catalog
(`src/Mesura.Core/Windows/windows.catalog.json`) is the source, and tests pin the lists.

## What the two profiles are

- **Recommended** is everyday Windows with less promotion, ad profiling and nagging, and nothing you would miss. It holds
  settings that are *documented by Microsoft* or *verified* on a Windows build ([how](value-verification-2026-10.md)),
  that lose no function you would use, that are not tied to your hardware, and that Activity can undo exactly.
- **Maximum performance** is Recommended plus a more aggressive reduction of what runs or reports in the background, and
  the desktop power settings. Its additions are all documented by Microsoft, each names what it costs, and it is
  deliberately not a list of tweaks for their own sake. It does **not** claim a frame-rate or latency gain: no reputable
  measurement shows one from these settings on current hardware, and Mesura has none of its own.
- **Extras** are offered unticked beside Maximum performance, each with its consequence. Nothing in them is applied
  unless you tick it.

A setting known only from community sources, or a service or task switch Microsoft doesn't support, is never part of a
profile's automatic selection. Windows 10 and Windows 11 are covered; each entry says which builds and editions it
applies to, and an entry that can't apply to your PC reads "not applicable" instead of being written.

## Telemetry: what Mesura controls and what it can't

Windows sends diagnostic data in two levels, *Required* (always on) and *Optional*. Microsoft offers no setting that turns
Required data off on Pro or Home; the *Security* level that does exists only on Enterprise, Education and Server.
Mesura says so rather than promise a control it doesn't have.

| Mechanism | Official? | In | What it does | Cost |
|---|---|---|---|---|
| Diagnostic-data policy, level Required (`AllowTelemetry = 1`) | Yes, Microsoft policy (Pro and higher) | Recommended | Optional diagnostic data is not sent; Settings shows the switch as managed | Windows Insider builds need optional data; Home is not covered |
| Advertising ID, tailored experiences, feedback prompts, language list | Yes, Windows settings | Recommended | Apps and Microsoft stop using these for ads and offers | None you would notice |
| Windows Error Reporting off | Yes, documented value | Maximum | No crash reports are created or sent | No automatic crash reports or known solutions |
| Activity history, cross-device experiences | Yes, Microsoft policy (Pro and higher) | Maximum | Nothing recorded or published; PC not discoverable | No "continue on another device" |
| App-launch tracking, typing and inking personalisation | Yes, Windows settings | Maximum | Windows stops learning from what you start and type | No "most used apps" in Start or Run history; less personal typing suggestions |
| Delivery Optimization without peers | Yes, Microsoft policy (Pro and higher) | Maximum | No upload of updates to other PCs | None; downloads come from Microsoft |
| Telemetry service (DiagTrack) off | **No.** No Microsoft page documents it as a way to reduce diagnostic data (searched 2026-10-25), and a Microsoft Q&A answer (about Windows Server) advises against it | Maximum extra, unticked | Stops the service that sends diagnostic data | Windows may start it again after an update; Feedback Hub and some diagnostics stop reporting |
| Data-collection scheduled tasks off | **No.** | Maximum extra, unticked | Stops six CEIP, feedback and disk-diagnostic tasks | A feature update can turn some back on |

**What Windows keeps doing whatever you choose here:** sending the Required diagnostic data; Windows Update, activation,
certificate and connectivity checks; Microsoft Defender cloud protection; and everything Edge, Microsoft 365, OneDrive, the
Microsoft Store apps and third-party software send under their own settings. Mesura does not touch those.

## The lists

### Recommended (14)

| Setting | Writes | Kind | Value basis | Admin | Needs |
|---|---|---|---|---|---|
| Turn off the Sticky Keys and other accessibility shortcuts (`input.accessibility-shortcuts-off`) | `HKCU\StickyKeys\Flags=506; HKCU\Keyboard Response\Flags=122; HKCU\ToggleKeys\Flags=58; HKCU\MouseKeys\Flags=58` | Windows setting | documented | no | - |
| No Windows feedback prompts (`notifications.no-feedback-prompts`) | `HKCU\Rules\NumberOfSIUFInPeriod=0; HKCU\Rules\PeriodInNanoSeconds=0` | Windows setting | documented | no | - |
| Don't share the language list with websites (`privacy.no-language-list`) | `HKCU\User Profile\HttpAcceptLanguageOptOut=1` | Windows setting | documented | no | - |
| Turn the internet connectivity check back on (`repair.ncsi-active-probing`) | `HKLM\Internet\EnableActiveProbing=1` | Windows setting | documented | yes | - |
| Turn threaded DPCs back on (`repair.threaded-dpc`) | `HKLM\kernel\ThreadDpcEnable=1` | Windows setting | documented | yes | - |
| Send required diagnostic data only (policy) (`privacy.diagnostic-data-required`) | `HKLM\DataCollection\AllowTelemetry=1` | Microsoft policy | documented | yes | proEdition |
| Turn off the advertising ID (`privacy.no-advertising-id`) | `HKCU\AdvertisingInfo\Enabled=0` | Windows setting | verified | no | - |
| Turn off tailored experiences (`privacy.no-tailored-experiences`) | `HKCU\Privacy\TailoredExperiencesWithDiagnosticDataEnabled=0` | Windows setting | verified | no | - |
| No tips, suggestions and promoted apps (`notifications.no-tips-and-suggestions`) | `HKCU\ContentDeliveryManager\SubscribedContent-338389Enabled=0; HKCU\ContentDeliveryManager\SubscribedContent-338388Enabled=0; HKCU\ContentDeliveryManager\SubscribedContent-310093Enabled=0; HKCU\ContentDeliveryManager\SubscribedContent-338393Enabled=0; HKCU\ContentDeliveryManager\SubscribedContent-353694Enabled=0; HKCU\ContentDeliveryManager\SubscribedContent-353696Enabled=0; HKCU\ContentDeliveryManager\SystemPaneSuggestionsEnabled=0; HKCU\ContentDeliveryManager\SoftLandingEnabled=0` | Windows setting | verified | no | - |
| No 'finish setting up your device' screens (`notifications.no-setup-nags`) | `HKCU\UserProfileEngagement\ScoobeSystemSettingEnabled=0` | Windows setting | verified | no | - |
| No lock-screen tips and ads (`lockscreen.no-spotlight-ads`) | `HKCU\ContentDeliveryManager\RotatingLockScreenOverlayEnabled=0; HKCU\ContentDeliveryManager\SubscribedContent-338387Enabled=0` | Windows setting | verified | no | - |
| No tips and app recommendations in Start (`start.no-recommendations`) | `HKCU\Advanced\Start_IrisRecommendations=0` | Windows setting | verified | no | build>=22621 |
| No account prompts in Start (`start.no-account-notifications`) | `HKCU\Advanced\Start_AccountNotifications=0` | Windows setting | verified | no | build>=22621 |
| Turn on Game Mode (`gaming.game-mode-on`) | `HKCU\GameBar\AutoGameModeEnabled=1` | Windows setting | verified | no | - |

### Added by Maximum performance (10)

| Setting | Writes | Kind | Value basis | Admin | Needs |
|---|---|---|---|---|---|
| Turn off Windows Error Reporting (`privacy.no-error-reporting`) | `HKLM\Windows Error Reporting\Disabled=1` | Windows setting | documented | yes | - |
| Turn off activity history (`privacy.no-activity-history`) | `HKLM\System\EnableActivityFeed=0; HKLM\System\PublishUserActivities=0; HKLM\System\UploadUserActivities=0` | Microsoft policy | documented | yes | proEdition |
| Turn off cross-device experiences (policy) (`privacy.no-cross-device-experiences`) | `HKLM\System\EnableCdp=0` | Microsoft policy | documented | yes | proEdition, build>=19041 |
| Turn off app-launch tracking (`privacy.no-app-launch-tracking`) | `HKCU\Advanced\Start_TrackProgs=0` | Windows setting | documented | no | - |
| Turn off typing and inking personalization (`privacy.no-typing-personalization`) | `HKCU\InputPersonalization\RestrictImplicitTextCollection=1; HKCU\InputPersonalization\RestrictImplicitInkCollection=1` | Windows setting | documented | no | - |
| Get updates from Microsoft only (no peer sharing) (`updates.no-p2p-delivery`) | `HKLM\DeliveryOptimization\DODownloadMode=0` | Microsoft policy | documented | yes | proEdition |
| Turn off Widgets (policy) (`taskbar.widgets-policy-off`) | `HKLM\Dsh\AllowNewsAndInterests=0` | Microsoft policy | documented | yes | proEdition, build>=22000 |
| High performance power plan (`power.high-performance`) | `power plan High performance` | Windows setting (power/network) | documented | yes | noBattery, noModernStandby |
| No PCI Express link power saving (on AC) (`power.pcie-aspm-off`) | `power PCI Express link state power management=0 (AC)` | Windows setting (power/network) | documented | yes | noBattery |
| Never turn off drives (on AC) (`power.disk-never-off`) | `power Turn off hard disk after=0 (AC)` | Windows setting (power/network) | documented | yes | - |

### Maximum performance extras (unticked) (7)

| Setting | Writes | Kind | Value basis | Admin | Needs |
|---|---|---|---|---|---|
| Disable the telemetry service (unofficial) (`services.telemetry-off`) | `service DiagTrack=disabled` | unofficial switch | - | yes | - |
| Turn off data-collection scheduled tasks (`tasks.telemetry-off`) | `task Consolidator=off; task UsbCeip=off; task Proxy=off; task Microsoft-Windows-DiskDiagnosticDataCollector=off; task DmClient=off; task DmClientOnScenarioDownload=off` | unofficial switch | - | yes | - |
| Turn off online speech recognition (`privacy.no-online-speech`) | `HKCU\OnlineSpeechPrivacy\HasAccepted=0` | Windows setting | documented | no | - |
| Turn off app notifications (`notifications.off`) | `HKCU\PushNotifications\ToastEnabled=0` | Windows setting | verified | no | - |
| Turn off USB selective suspend (on AC) (`power.usb-keep-awake`) | `power USB selective suspend=0 (AC)` | Windows setting (power/network) | documented | yes | - |
| Turn on hardware-accelerated GPU scheduling (`gpu.hardware-scheduling-on`) | `HKLM\GraphicsDrivers\HwSchMode=2` | Windows setting | community | yes | build>=19041 |
| Turn off NTFS last-access time updates (`system.ntfs-last-access-off`) | `HKLM\FileSystem\NtfsDisableLastAccessUpdate=1` | Windows setting | documented | yes | - |


## Left out of both profiles, and why

- **Xbox services, printing, offline maps, search indexing:** each removes a feature some PC needs; the benefit is trivial or
  unmeasured. They stay selectable one by one.
- **Blocking driver updates, removing Windows features, taskbar and Explorer looks:** a risk, a component removal or a
  preference, not an optimisation.
- **`DisableOneSettingsDownloads`:** Microsoft warns that apps using the service "may stop working".
- **Entries known only from community sources:** search highlights, OneDrive/sync-provider notices, Game Bar capture and
  background recording. They stay in the *Quiet Windows* and *Clean taskbar and Start* collections, with a label saying so.
- **Anything undocumented:** "Undocumented" entries are never in a profile.

## Limits of this review

- The value checks ran on one PC, a trimmed Windows 11 25H2 Pro image; see [the verification notes](value-verification-2026-10.md)
  for what they do and don't show. No setting was switched during the review, so none has been seen to *change* the
  behaviour it names.
- Policies are written to the registry as Group Policy would; Windows Home ignores them, so Mesura marks them
  not applicable there.
- The profiles were previewed (read-only) on that PC, where most settings already read as done; they have not been applied
  to a clean PC by the author.
