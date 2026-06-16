# ui-tests.ps1 — automated UI tests for My Notebook (WinUI 3)
# Usage:  .\ui-tests.ps1 -AppPid <PID>     (app must already be running)
# Requires the WinApp CLI (run /winui-setup if `winapp` is not found).
param([Parameter(Mandatory)][int]$AppPid)

$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $results = @()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) { $script:pass++; $script:results += @{ name = $Name; status = "PASS" } }
        else { $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" } }
    } catch { $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" } }
}

New-Item -ItemType Directory -Force -Path "screenshots" | Out-Null

# ─── Shell / sidebar exist ───
# NOTE: the folder/note TreeView is not surfaced by UIA inspect (a winapp/WinUI
# limitation), so it can't be asserted here — it is verified via screenshot review
# (screenshots/01-initial.png shows folders with notes nested).
Test-UI "Search box exists"      { winapp ui wait-for "SearchBox" -a $AppPid -t 4000 }
Test-UI "New note button"        { winapp ui wait-for "NewNoteButton" -a $AppPid -t 3000 }
Test-UI "New thread button"      { winapp ui wait-for "NewThreadButton" -a $AppPid -t 3000 }
Test-UI "Settings button"        { winapp ui wait-for "SettingsButton" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots/01-initial.png" 2>$null

# ─── Create a note via toolbar, then type a title ───
Test-UI "Click New note"         { winapp ui invoke "NewNoteButton" -a $AppPid }
Test-UI "Editor title appears"   { winapp ui wait-for "TitleBox" -a $AppPid -t 3000 }
Test-UI "Set note title"         { winapp ui set-value "TitleBox" "UI Test Note" -a $AppPid }
Test-UI "Editor body appears"    { winapp ui wait-for "Editor" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots/02-new-note.png" 2>$null

# ─── Create a screenshot thread ───
Test-UI "Click New thread"       { winapp ui invoke "NewThreadButton" -a $AppPid }
Test-UI "Thread title appears"   { winapp ui wait-for "ThreadTitleBox" -a $AppPid -t 3000 }
Test-UI "Paste hint visible"     { winapp ui wait-for "PasteHint" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots/03-new-thread.png" 2>$null

# ─── Search (AutoSuggestBox is a Group wrapper — verify it exists & focuses) ───
Test-UI "Focus search box"       { winapp ui focus "SearchBox" -a $AppPid }
winapp ui screenshot -a $AppPid -o "screenshots/04-search.png" 2>$null

# ─── Settings navigation + defaults ───
Test-UI "Open Settings"          { winapp ui invoke "SettingsButton" -a $AppPid }
Test-UI "Theme combo exists"     { winapp ui wait-for "ThemeCombo" -a $AppPid -t 3000 }
Test-UI "Theme default = Follow Windows" { winapp ui wait-for "ThemeCombo" -a $AppPid --value "Follow Windows" -t 2000 }
Test-UI "Confirm-delete default On"      { winapp ui wait-for "ConfirmDeleteToggle" -a $AppPid --value "On" -t 2000 }
Test-UI "Spell-check default On"         { winapp ui wait-for "SpellCheckToggle" -a $AppPid --value "On" -t 2000 }
Test-UI "Close-to-tray default On"       { winapp ui wait-for "CloseToTrayToggle" -a $AppPid --value "On" -t 2000 }
Test-UI "Quick-note default Off"         { winapp ui wait-for "QuickNoteToggle" -a $AppPid --value "Off" -t 2000 }
Test-UI "Stats text shows notes"         { winapp ui wait-for "StatsText" -a $AppPid --value "notes" --contains -t 2000 }
Test-UI "OCR status shown"               { winapp ui wait-for "OcrStatusText" -a $AppPid --value "OCR" --contains -t 2000 }
Test-UI "Data path shown"                { winapp ui wait-for "DataPathText" -a $AppPid --value "Data" --contains -t 2000 }
winapp ui screenshot -a $AppPid -o "screenshots/05-settings-light.png" 2>$null

# ─── Dark mode ───
# Driving the Theme ComboBox dropdown via UIA is unreliable (items duplicate across
# the popup window). Dark mode is verified visually in screenshots/dark-verify.png.
# Here we just confirm the combo reads its selected value.
Test-UI "Theme combo readable"   { winapp ui wait-for "ThemeCombo" -a $AppPid --value "Follow Windows" -t 2000 }
winapp ui screenshot -a $AppPid -o "screenshots/06-settings.png" 2>$null

# ─── Toggle a behavior setting, confirm it sticks in the UI AND on disk ───
Test-UI "Turn confirm-delete Off" { winapp ui invoke "ConfirmDeleteToggle" -a $AppPid }
Test-UI "Confirm-delete is Off"   { winapp ui wait-for "ConfirmDeleteToggle" -a $AppPid --value "Off" -t 2000 }

# ─── Accessibility audit (interactive controls in the main window) ───
$allElements = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).elements
$appElements = @($allElements | Where-Object {
    $_.type -match 'Button|TextBox|ComboBox|ToggleSwitch|Edit|Tree' -and
    $_.name -notmatch 'Minimize|Maximize|Close|System' -and
    $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
})
$missingId = @($appElements | Where-Object { -not $_.automationId })
if ($missingId.Count -eq 0) {
    $pass++; $results += @{ name = "Interactive controls have AutomationId"; status = "PASS" }
} else {
    $fail++
    $names = ($missingId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", "
    $results += @{ name = "AutomationId coverage"; status = "FAIL"; detail = "Missing: $names" }
}

# ─── Verify the toggle persisted to disk ───
$settingsFile = Join-Path (Split-Path (Get-Process -Id $AppPid).Path) "Data\settings.json"
Test-UI "Toggle persisted to settings.json" {
    if (Test-Path $settingsFile) {
        $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if ($s.ConfirmBeforeDelete -eq $false) { $global:LASTEXITCODE = 0 }
        else { throw "ConfirmBeforeDelete not persisted: $($s.ConfirmBeforeDelete)" }
    } else { throw "settings.json missing at $settingsFile" }
}

winapp ui screenshot -a $AppPid -o "test-screenshot.png" 2>$null

# ─── Results ───
Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
    Write-Host "  FAIL: $($_.name) — $($_.detail)" -ForegroundColor Red
}
$results | ConvertTo-Json | Out-File "test-results.json"
if ($fail -gt 0) { exit 1 } else { exit 0 }
