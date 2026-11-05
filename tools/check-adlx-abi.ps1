# Offline check of the ADLX interface slot numbers Mesura's C# code uses (src\Mesura.Platform.Windows\Gpu\Adlx\AdlxObjects.cs, AdlxSession.cs) against the C interface structs of a copy of AMD's ADLX SDK
# that YOU downloaded (git clone --branch v1.5 https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git <folder>). The SDK is not part of this repository and is read, never copied.
#
#   powershell -ExecutionPolicy Bypass -File tools\check-adlx-abi.ps1 -Sdk <folder of the ADLX clone>
#
# Why: ADLX objects are called through their vtables, and the slot order is the order of the functions in the C struct (IADLXGPUVtbl and the like), which is not the order of the C++ class
# declarations. A wrong number calls the wrong function. This script fails if any slot below is not where Mesura says it is. Exit 0 when every slot matches.
param([Parameter(Mandatory = $true)][string]$Sdk)
$ErrorActionPreference = 'Stop'
$include = Join-Path $Sdk 'SDK\Include'
if (-not (Test-Path $include)) { throw "No SDK\Include folder in $Sdk" }
$text = (Get-ChildItem $include -Filter *.h | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
$text = $text -replace "`r", ''

# interface -> slot -> function name, as Mesura's code uses them.
$expected = [ordered]@{
  IADLXInterface = @{ 0 = 'Acquire'; 1 = 'Release'; 2 = 'QueryInterface' }
  IADLXSystem = @{ 0 = 'GetHybridGraphicsType'; 1 = 'GetGPUs'; 3 = 'GetDisplaysServices'; 2 = 'QueryInterface'; 7 = 'Get3DSettingsServices'; 8 = 'GetGPUTuningServices'; 9 = 'GetPerformanceMonitoringServices' }
  IADLXGPUList = @{ 3 = 'Size'; 11 = 'At_GPUList' }
  IADLXGPU = @{ 3 = 'VendorId'; 4 = 'ASICFamilyType'; 5 = 'Type'; 6 = 'IsExternal'; 7 = 'Name'; 8 = 'DriverPath'; 9 = 'PNPString'; 11 = 'TotalVRAM'; 12 = 'VRAMType'; 13 = 'BIOSInfo'; 14 = 'DeviceId'; 15 = 'RevisionId'; 16 = 'SubSystemId'; 18 = 'UniqueId' }
  IADLX3DSettingsServices = @{ 8 = 'GetWaitForVerticalRefresh'; 13 = 'GetTessellation'; 14 = 'GetRadeonSuperResolution'; 3 = 'GetAntiLag'; 4 = 'GetChill'; 5 = 'GetBoost'; 6 = 'GetImageSharpening'; 7 = 'GetEnhancedSync'; 9 = 'GetFrameRateTargetControl' }
  IADLX3DAntiLag = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 5 = 'SetEnabled' }
  IADLX3DAntiLag1 = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 5 = 'SetEnabled'; 6 = 'GetLevel' }
  IADLX3DChill = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 8 = 'SetEnabled' }
  IADLX3DBoost = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 7 = 'SetEnabled' }
  IADLX3DImageSharpening = @{ 3 = 'IsSupported'; 4 = 'IsEnabled' }
  IADLX3DEnhancedSync = @{ 3 = 'IsSupported'; 4 = 'IsEnabled' }
  IADLX3DFrameRateTargetControl = @{ 3 = 'IsSupported'; 4 = 'IsEnabled' }
  IADLXSmartAccessMemory = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 5 = 'SetEnabled' }
  IADLXGPUTuningServices = @{ 4 = 'IsAtFactory'; 6 = 'IsSupportedAutoTuning'; 7 = 'IsSupportedPresetTuning'; 8 = 'IsSupportedManualGFXTuning'; 9 = 'IsSupportedManualVRAMTuning'; 10 = 'IsSupportedManualFanTuning'; 11 = 'IsSupportedManualPowerTuning' }
  IADLXGPUTuningServices1 = @{ 4 = 'IsAtFactory'; 6 = 'IsSupportedAutoTuning'; 11 = 'IsSupportedManualPowerTuning'; 18 = 'GetSmartAccessMemory' }
  IADLXPerformanceMonitoringServices = @{ 18 = 'GetCurrentGPUMetrics' }
  IADLX3DWaitForVerticalRefresh = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 5 = 'GetMode' }
  IADLX3DTessellation = @{ 3 = 'IsSupported'; 4 = 'GetMode'; 5 = 'GetLevel' }
  IADLX3DRadeonSuperResolution = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 7 = 'GetSharpness' }
  IADLXDisplayServices = @{ 4 = 'GetDisplays'; 9 = 'GetFreeSync'; 10 = 'GetVirtualSuperResolution'; 11 = 'GetGPUScaling'; 12 = 'GetScalingMode'; 13 = 'GetIntegerScaling'; 14 = 'GetColorDepth'; 15 = 'GetPixelFormat' }
  IADLXDisplayList = @{ 3 = 'Size'; 11 = 'At_DisplayList' }
  IADLXDisplay = @{ 3 = 'ManufacturerID'; 4 = 'DisplayType'; 5 = 'ConnectorType'; 6 = 'Name'; 8 = 'NativeResolution'; 9 = 'RefreshRate'; 11 = 'ScanType'; 12 = 'GetGPU' }
  IADLXDisplayFreeSync = @{ 3 = 'IsSupported'; 4 = 'IsEnabled'; 5 = 'SetEnabled' }
  IADLXDisplayVSR = @{ 3 = 'IsSupported'; 4 = 'IsEnabled' }
  IADLXDisplayGPUScaling = @{ 3 = 'IsSupported'; 4 = 'IsEnabled' }
  IADLXDisplayIntegerScaling = @{ 3 = 'IsSupported'; 4 = 'IsEnabled' }
  IADLXDisplayScalingMode = @{ 3 = 'IsSupported'; 4 = 'GetMode' }
  IADLXDisplayColorDepth = @{ 3 = 'IsSupported'; 4 = 'GetValue' }
  IADLXDisplayPixelFormat = @{ 3 = 'IsSupported'; 4 = 'GetValue' }
  IADLXGPUMetrics = @{ 4 = 'GPUUsage'; 5 = 'GPUClockSpeed'; 6 = 'GPUVRAMClockSpeed'; 7 = 'GPUTemperature'; 8 = 'GPUHotspotTemperature'; 9 = 'GPUPower'; 10 = 'GPUTotalBoardPower'; 11 = 'GPUFanSpeed'; 13 = 'GPUVoltage' }
}
$failed = 0; $checked = 0
# The display-settings header names five of its vtable structs differently from their interfaces (IADLXFreeSyncVtbl for IADLXDisplayFreeSync, and so on); the slot order is what matters.
$structOf = @{ IADLXDisplayFreeSync = 'IADLXFreeSync'; IADLXDisplayVSR = 'IADLXVSR'; IADLXDisplayGPUScaling = 'IADLXGPUScaling'; IADLXDisplayIntegerScaling = 'IADLXIntegerScaling'; IADLXDisplayColorDepth = 'IADLX_COLOR_DEPTH'; IADLXDisplayPixelFormat = 'IADLX_PIXEL_FORMAT' }
foreach ($name in $expected.Keys) {
  $struct = if ($structOf.ContainsKey($name)) { $structOf[$name] } else { $name }
  $m = [regex]::Match($text, "typedef struct ${struct}Vtbl\s*\{(.*?)\}\s*${struct}Vtbl\s*;", 'Singleline')
  if (-not $m.Success) { Write-Host "FAIL  $name : no C vtable struct in this SDK"; $failed++; continue }
  $body = [regex]::Replace([regex]::Replace($m.Groups[1].Value, '/\*.*?\*/', '', 'Singleline'), '//[^\n]*', '')
  $slots = @([regex]::Matches($body, '\(\s*ADLX_STD_CALL\s*\*\s*(\w+)\s*\)') | ForEach-Object { $_.Groups[1].Value })
  foreach ($slot in $expected[$name].Keys) {
    $checked++
    $want = $expected[$name][$slot]
    if ($slot -ge $slots.Count -or $slots[$slot] -ne $want) { Write-Host ("FAIL  {0} slot {1}: Mesura expects {2}, the SDK has {3}" -f $name, $slot, $want, $(if ($slot -lt $slots.Count) { $slots[$slot] } else { '(none)' })); $failed++ }
  }
}
Write-Host "$checked slots checked in $($expected.Count) interfaces; $failed wrong."
exit $(if ($failed -eq 0) { 0 } else { 1 })
