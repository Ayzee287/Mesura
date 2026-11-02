# Inside Windows Sandbox: what the installer's wizard SHOWS when Mesura is already installed for the other scope. It opens the real wizard (msiexec /i, no /qn), reads every page's text with
# UI Automation, takes a screenshot of each, and cancels: nothing is installed by the wizard itself (the installed copies are put there silently first). Sandbox only: the wizard is never
# driven on the PC in use.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Say "== installer-wizard $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"

function Shot([string]$name) {
  try {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height); [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size); $bmp.Save("C:\Host\out\$name.png")
  } catch { Info "no screenshot $name" $_.Exception.Message }
}
# The wizard's windows: top-level windows of any msiexec process; returns the title and every text/button name on it.
function Wizard-Page {
  $ids = @(Get-Process msiexec -ErrorAction SilentlyContinue | ForEach-Object Id)
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($ids -contains $w.Current.ProcessId -and $w.Current.Name -and $w.Current.Name -ne 'Windows Installer') {
      $texts = @(); foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -and $e.Current.ControlType.ProgrammaticName -match 'Text|Button|RadioButton|CheckBox') { $texts += "[$($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.','')] $($e.Current.Name)" }
      }
      [pscustomobject]@{ Title = $w.Current.Name; Elements = $texts; Window = $w }
    }
  }
}
function Wait-Page([int]$seconds = 180) {
  $t = Get-Date
  for ($i = 0; $i -lt $seconds; $i++) {
    try { $p = @(Wizard-Page) } catch { Info 'Wizard-Page threw' $_.Exception.Message; $p = @() }
    if ($p.Count) { Info 'a wizard page appeared' "after $([int]((Get-Date) - $t).TotalSeconds) s: $(($p | ForEach-Object Title) -join ', ')"; return $p }
    Start-Sleep 1
  }
  @()
}
function Press([object]$page, [string]$name) {
  foreach ($e in $page.Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
    if ($e.Current.Name -replace '&', '' -eq $name) { ([System.Windows.Automation.InvokePattern]$e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true }
  }
  $false
}
function Show-Page([string]$what, [string]$shot) {
  $pages = @(Wait-Page)
  if (-not $pages.Count) {
    Shot "$shot-nowindow"
    $all = foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) { "$($w.Current.ProcessId):'$($w.Current.Name)'" }
    Info $what ("no wizard window appeared; msiexec processes: " + ((Get-Process msiexec -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Id) s$($_.SessionId)" }) -join ', ') + "; top-level windows: " + ($all -join ' '))
    return $null
  }
  Start-Sleep 2; $pages = @(Wizard-Page); Shot $shot
  if (-not $pages.Count) { return $null }
  foreach ($p in $pages) { Info "$what - window '$($p.Title)'" ($p.Elements -join ' | ') }
  $pages[0]
}
function Wizard([string]$which, [string]$name, [string[]]$walk) {
  $proc = Start-Process msiexec -ArgumentList @('/i', "`"$(Package $which)`"") -PassThru
  Start-Sleep 3
  $page = Show-Page "$name (first page)" "$name-1"
  $n = 2
  foreach ($button in $walk) {
    if (-not $page) { break }
    if (-not (Press $page $button)) { Info "$name" "no '$button' button on '$($page.Title)'"; break }
    Start-Sleep 3
    $page = Show-Page "$name (after $button)" "$name-$n"; $n++
  }
  Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 -and $_.MainWindowHandle -ne 0 } | Stop-Process -Force -ErrorAction SilentlyContinue
  try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
  Start-Sleep 2
}

Remove-All
# 1. nothing installed: the pages as a new user sees them (welcome, scope)
Wizard 'older' 'fresh' @('Next')
Remove-All
# 2. the SAME package is installed for me only; the user opens the same package again
Check 'older installed for me only (silent)' ((Install 'older' 'user') -in 0, 3010)
Wizard 'older' 'same-version-installed-for-me' @('Next', 'Next')
# 3. the same package is installed for all users
Remove-All
Check 'older installed for all users (silent)' ((Install 'older' 'machine') -in 0, 3010)
Wizard 'older' 'same-version-installed-for-all' @('Next', 'Next')
# 4. a different (newer) package while the older is installed for me only: this is the case where a second copy is possible
Remove-All
Check 'older installed for me only again' ((Install 'older' 'user') -in 0, 3010)
Wizard 'newer' 'newer-over-per-user' @('Next')
Remove-All
Say "== done $(Get-Date -Format s)"
