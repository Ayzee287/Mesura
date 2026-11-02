# Inside Windows Sandbox: does a local QoS policy mark outgoing packets on this Windows, and does the legacy "Do not use NLA" value change that? (docs/dscp-research-0.5.md)
# It creates a policy for powershell.exe's UDP traffic to a documentation address (192.0.2.1, TEST-NET-1: nothing answers), captures the packets leaving the virtual adapter
# with pktmon, and records the ToS byte of each. It removes the policy and the registry value it created. The sandbox's adapter is virtual and its host NAT is not your router:
# this shows what Windows puts in the packet, not what a router or an ISP does with it.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== dscp $(Get-Date -Format s) on $env:COMPUTERNAME ($((Get-CimInstance Win32_OperatingSystem).Caption) $((Get-CimInstance Win32_OperatingSystem).BuildNumber))"
Check 'pktmon is available' ($null -ne (Get-Command pktmon -ErrorAction SilentlyContinue))
Info 'network profile' ((Get-NetConnectionProfile | ForEach-Object { "$($_.Name): $($_.NetworkCategory)" }) -join '; ')
$work = 'C:\mesura-dscp'; New-Item -ItemType Directory -Force $work | Out-Null
$nlaKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\QoS'
$policy = 'MesuraDscpTest'

function Send-Packets {
  $udp = New-Object System.Net.Sockets.UdpClient
  $bytes = [Text.Encoding]::ASCII.GetBytes('mesura-dscp-test')
  $script:sendErrors = @()
  foreach ($n in 1..8) {
    try { [void]$udp.Send($bytes, $bytes.Length, '192.0.2.1', 40000) } catch { $script:sendErrors += $_.Exception.InnerException.Message }
    Start-Sleep -Milliseconds 150
  }
  $udp.Close()
}

function Capture([string]$label) {
  $etl = Join-Path $work "$label.etl"; $txt = Join-Path $work "$label.txt"
  Remove-Item $etl, $txt -ErrorAction SilentlyContinue
  pktmon filter remove | Out-Null
  pktmon filter add test -i 192.0.2.1 -p 40000 | Out-Null
  pktmon start --capture --pkt-size 0 --file-name $etl | Out-Null
  Start-Sleep -Seconds 1
  Send-Packets
  Start-Sleep -Seconds 1
  pktmon stop | Out-Null
  pktmon etl2txt $etl --out $txt --verbose 3 --hex | Out-Null
  $text = if (Test-Path $txt) { Get-Content $txt -Raw } else { '' }
  # The ToS byte is the second byte of the IPv4 header; pktmon prints it as "tos 0x.." in the header line; the hex dump is kept in the file for reading.
  $tos = [regex]::Matches($text, '(?i)\btos\s+(0x[0-9a-f]+)') | ForEach-Object { $_.Groups[1].Value } | Group-Object | ForEach-Object { "$($_.Name) x$($_.Count)" }
  Copy-Item $txt "C:\Host\out\dscp-$label.txt" -ErrorAction SilentlyContinue
  Info "send errors for '$label'" "$($script:sendErrors.Count) of 8 sends failed$(if ($script:sendErrors.Count) { ': ' + ($script:sendErrors | Select-Object -Unique) -join ' ' })"
  Info "capture '$label'" "packets mentioning 192.0.2.1: $([regex]::Matches($text, '192\.0\.2\.1').Count); ToS values: $(if ($tos) { $tos -join ', ' } else { 'none parsed (read dscp-' + $label + '.txt)' })"
}

try {
  Capture '1-baseline'
  New-NetQosPolicy -Name $policy -AppPathNameMatchCondition 'powershell.exe' -IPProtocolMatchCondition UDP -IPDstPrefixMatchCondition '192.0.2.1' -DSCPAction 46 -PolicyStore ActiveStore | Out-Null
  Info 'policy' ((Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object Name -eq $policy | Format-List Name, Owner, NetworkProfile, AppPathName, DSCPValue | Out-String).Trim())
  Capture '2-policy-no-nla-key'
  New-Item $nlaKey -Force | Out-Null
  New-ItemProperty $nlaKey -Name 'Do not use NLA' -Value '1' -PropertyType String -Force | Out-Null
  Capture '3-policy-with-nla-key-string-no-restart'
  Info 'note' 'Windows reads the NLA value at start-up; if capture 3 still shows ToS 0x0 the key may need a restart (not done here, so record "unknown" for that case).'
}
finally {
  Remove-NetQosPolicy -Name $policy -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
  Remove-Item $nlaKey -Recurse -ErrorAction SilentlyContinue
  pktmon filter remove | Out-Null
}
Check 'the policy was removed' (-not (Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object Name -eq $policy))
Check 'the registry value was removed' (-not (Test-Path $nlaKey))
Capture '4-after-removal'
Say "== done $(Get-Date -Format s)"

