# Drives the installer's wizard for real, as a throwaway Windows user, so the PC's own Mesura is never seen, let alone touched (docs/testing-lifecycle.md):
# welcome page, the page with the Install button, progress, finish; then checks what was installed (files, Start menu shortcut, the registration in the
# user's own registry hive), starts the installer again over the installed copy, and uninstalls. Every page is saved as a PNG.
#
#   Run from an ELEVATED terminal (it creates and deletes a local user):
#   powershell -ExecutionPolicy Bypass -File tools\e2e-installer-wizard.ps1 -Msi <Mesura-x.y.z-x64.msi> -Out <folder> [-Report <file>]
#
# The user's name is MesuraWizard<random>; its password is random, held in memory only. The user, its profile folder and the copied package are removed at
# the end (a loaded registry hive can keep the profile folder until the next restart; the script says so).
param(
  [Parameter(Mandatory = $true)][string]$Msi,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated terminal.' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WZ { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l); }
"@
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
New-Item -ItemType Directory -Force $Out | Out-Null
$Msi = (Resolve-Path $Msi).Path
$results = New-Object System.Collections.Generic.List[string]
function Say([string]$l) { $results.Add($l); Write-Host $l; if ($Report) { $l | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

$name = 'MesuraWizard' + (Get-Random -Maximum 9999)
$pw = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ }); $pw += 'aA1!'
$share = 'C:\Users\Public\mesura-wizard'
New-Item -ItemType Directory -Force $share | Out-Null
$pkg = Join-Path $share (Split-Path $Msi -Leaf); Copy-Item $Msi $pkg -Force
New-LocalUser -Name $name -Password (ConvertTo-SecureString $pw -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires | Out-Null
Add-LocalGroupMember -Group 'Users' -Member $name
$cred = New-Object PSCredential($name, (ConvertTo-SecureString $pw -AsPlainText -Force))
$home_ = "C:\Users\$name"
$proc = $null

function Wizard-Window {
  foreach ($w in $A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    $n = $w.Current.Name
    if ($n -match 'Mesura' -and $w.Current.ControlType -eq $CT::Window -and $w.Current.ProcessId -ne $PID -and $w.Current.BoundingRectangle.Width -gt 200 -and $n -notmatch 'Visual Studio|Claude|Code') { return $w }
  }
  $null
}
function Shot($w, [string]$file) {
  $r = $w.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
function Press($w, [string[]]$names) {
  # A click on the button's own window (BM_CLICK): Windows Installer's buttons are plain Win32 buttons, and this works across accounts.
  foreach ($n in $names) {
    $b = @($w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n)))) | Where-Object { $_.Current.NativeWindowHandle -ne 0 -and $_.Current.IsEnabled } | Select-Object -First 1
    if ($b) { [WZ]::SendMessage([IntPtr]$b.Current.NativeWindowHandle, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; return $n }
  }
  $null
}
function Texts($w) { (@($w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ' }
function Wait-Page([string]$pattern, [int]$seconds = 60) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { $w = Wizard-Window; if ($w -and (Texts $w) -match $pattern) { return $w }; Start-Sleep -Milliseconds 500 }
  $null
}
function Tree([string]$root) { if (Test-Path $root) { @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue).Count } else { 0 } }

try {
  Say "== a throwaway user $name; package $(Split-Path $Msi -Leaf)"
  $proc = Start-Process msiexec -ArgumentList "/i `"$pkg`"" -Credential $cred -WorkingDirectory 'C:\Users\Public' -LoadUserProfile -PassThru
  # 1. Welcome
  $w = Wait-Page 'Welcome to the Mesura Setup Wizard|Вас приветствует' 40
  Check 'the wizard opens on a welcome page' ([bool]$w)
  if ($w) {
    Shot $w (Join-Path $Out '1-welcome.png'); Say ('      page 1: ' + (Texts $w))
    $pressed = Press $w @('Next', 'Далее', '&Next')
    Check 'Next is pressed on the welcome page' ([bool]$pressed) $pressed
  }
  # 2. The page with the Install button (not the licence, features or folder page)
  $w = Wait-Page 'Ready to install|Все готово к установке|Всё готово к установке' 20
  Check 'the second page offers Install' ([bool]$w)
  if ($w) {
    $t = Texts $w; Shot $w (Join-Path $Out '2-install.png'); Say ('      page 2: ' + $t)
    Check 'there is no folder, licence or feature choice on it' ($t -notmatch 'Browse|Change\.\.\.|Обзор|license agreement|Feature') $t
    $pressed = Press $w @('Install', 'Установить', '&Install')
    Check 'Install is pressed' ([bool]$pressed) $pressed
  }
  # 3. Progress, then finish
  $w = Wait-Page 'Completed the Mesura Setup Wizard|Завершение|Finish|Готово|Завершить' 120
  Check 'the install reaches the finish page' ([bool]$w)
  if ($w) {
    Shot $w (Join-Path $Out '3-finish.png'); Say ('      page 3: ' + (Texts $w))
    $box = @($w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::CheckBox)))) | Select-Object -First 1
    if ($box) { try { $tp = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); if ($tp.Current.ToggleState -eq 'On') { $tp.Toggle() } } catch { } }   # do not start Mesura
    $null = Press $w @('Finish', 'Готово', 'Завершить', '&Finish')
  }
  Start-Sleep 3
  if ($proc -and -not $proc.HasExited) { $null = $proc.WaitForExit(20000) }

  $dir = "$home_\AppData\Local\Programs\Mesura"
  Check 'Mesura.exe is in the default per-user folder, with no administrator rights' (Test-Path "$dir\Mesura.exe") $dir
  Check 'the command line is installed' (Test-Path "$dir\cli\mesura.exe")
  Check 'a Start menu shortcut exists' (Test-Path "$home_\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Mesura.lnk")
  Check 'there is no desktop shortcut (the optional feature is off)' (-not (Test-Path "$home_\Desktop\Mesura.lnk"))
  $files = Tree $dir; Say "      $files files installed"
  $sid = (New-Object System.Security.Principal.NTAccount($name)).Translate([System.Security.Principal.SecurityIdentifier]).Value
  $prod = @(Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products" -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty "$($_.PSPath)\InstallProperties" -ErrorAction SilentlyContinue).DisplayName -eq 'Mesura' })
  Check 'Windows Installer lists exactly one Mesura product for this user' ($prod.Count -eq 1) "$($prod.Count)"

  # 4. The installer started again over the installed copy
  $proc2 = Start-Process msiexec -ArgumentList "/i `"$pkg`"" -Credential $cred -WorkingDirectory 'C:\Users\Public' -LoadUserProfile -PassThru
  $w = Wait-Page 'Mesura' 30
  Check 'a second run opens the installer again' ([bool]$w)
  if ($w) { Shot $w (Join-Path $Out '4-again.png'); Say ('      again: ' + (Texts $w)); $null = Press $w @('Cancel', 'Отмена', '&Cancel'); Start-Sleep 2; $w = Wizard-Window; if ($w) { $null = Press $w @('Yes', 'Да', '&Yes') } }
  Start-Sleep 3

  # 5. Uninstall (silent): files and registration go
  $x = Start-Process msiexec -ArgumentList "/x `"$pkg`" /qn" -Credential $cred -WorkingDirectory 'C:\Users\Public' -LoadUserProfile -PassThru -Wait
  Check 'the uninstall finishes' ($x.ExitCode -in 0, 3010) "exit $($x.ExitCode)"
  Check 'the program files are gone' (-not (Test-Path "$dir\Mesura.exe"))
  Check 'the shortcut is gone' (-not (Test-Path "$home_\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Mesura.lnk"))
}
finally {
  Get-Process msiexec -ErrorAction SilentlyContinue | ForEach-Object { try { if ((Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").GetOwner().User -eq $name) { Stop-Process -Id $_.Id -Force } } catch { } }
  try { Remove-LocalUser -Name $name } catch { Say "could not remove the user: $_" }
  try { Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -eq $home_ } | Remove-CimInstance } catch { Say "profile left behind: $home_ (a loaded hive; restart, then delete it)" }
  Remove-Item $share -Recurse -Force -ErrorAction SilentlyContinue
  Say ("user removed; profile folder {0}" -f $(if (Test-Path $home_) { 'still there (restart, then delete it)' } else { 'removed' }))
  $failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
  Say ("{0} lines, {1} failed" -f $results.Count, $failed)
}
