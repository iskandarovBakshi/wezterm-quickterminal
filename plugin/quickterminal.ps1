# wezterm-quickterminal helper launcher. Started hidden by plugin/init.lua (or at login when autostart = true):
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File quickterminal.ps1 -Version <v> [-Quit]
#
# Compiles QuickTerminalHelper.cs once per version into %LOCALAPPDATA%\wezterm-quickterminal and runs it.
# Uses only Windows PowerShell 5.1 and .NET Framework, which ship with Windows.

param(
    [string]$Version = "dev",
    [switch]$Quit
)

$ErrorActionPreference = "Stop"

$cacheDir = Join-Path $env:LOCALAPPDATA "wezterm-quickterminal"
$logDir = Join-Path $env:APPDATA "wezterm-quickterminal"
$source = Join-Path $PSScriptRoot "QuickTerminalHelper.cs"
$safeVersion = $Version -replace '[^\w.-]', '_'
$dll = Join-Path $cacheDir "QuickTerminalHelper-$safeVersion.dll"

try {
    New-Item -ItemType Directory -Force -Path $cacheDir, $logDir | Out-Null

    # "dev" builds always recompile so edits to QuickTerminalHelper.cs are picked up.
    if ($safeVersion -eq "dev" -or -not (Test-Path $dll)) {
        Get-ChildItem $cacheDir -Filter "QuickTerminalHelper-*.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne $dll -and $_.Name -notlike "*-tmp-*" } |
            Remove-Item -Force -ErrorAction SilentlyContinue
        # Compile to a unique file first: two WezTerm windows loading the config at once may both
        # get here, and compiling straight to $dll would make one of them fail.
        $tmp = Join-Path $cacheDir ("QuickTerminalHelper-tmp-" + [guid]::NewGuid().ToString("N") + ".dll")
        Add-Type -Path $source -OutputAssembly $tmp -OutputType Library `
            -ReferencedAssemblies System.Windows.Forms, System.Web.Extensions
        if ($safeVersion -eq "dev") {
            $dll = $tmp
        } else {
            try { Move-Item -Path $tmp -Destination $dll -ErrorAction Stop }
            catch { Remove-Item $tmp -Force -ErrorAction SilentlyContinue } # the other launch won
        }
    }

    Add-Type -Path $dll
    [WeztermQuickTerminal.Helper]::Start($Version, $Quit.IsPresent)
}
catch {
    $line = "{0} ERROR quickterminal.ps1: {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $_
    Add-Content -Path (Join-Path $logDir "wezterm-quickterminal.log") -Value $line -ErrorAction SilentlyContinue
    exit 1
}
