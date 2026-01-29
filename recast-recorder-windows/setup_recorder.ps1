#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Recast Windows Recorder Setup Script
.DESCRIPTION
    Installs and configures the Recast Windows Recorder application.
    Run this script as Administrator on the target Windows 10/11 machine.
.NOTES
    Prerequisites that must be installed manually:
    - .NET 8.0 SDK or Runtime: https://dotnet.microsoft.com/download/dotnet/8.0
    - Google Chrome (stable)
    - TightVNC Server (optional, for remote control)
#>

param(
    [switch]$SkipBuild,
    [switch]$SkipFirewall,
    [switch]$SkipScheduledTask,
    [string]$InstallPath = "C:\Program Files\Recast\WindowsRecorder"
)

$ErrorActionPreference = "Stop"

# Script location
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Recast Windows Recorder Setup" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Check for Administrator privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "ERROR: This script must be run as Administrator." -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

# Load defaults from existing config if present
$ConfigFile = Join-Path $InstallPath "appsettings.json"
$DefaultRecorderId = ""
$DefaultRecorderHostname = ""
$DefaultManagerUrl = ""

if (Test-Path $ConfigFile) {
    Write-Host "Found existing config at $ConfigFile, loading defaults..." -ForegroundColor Green
    try {
        $existingConfig = Get-Content $ConfigFile -Raw | ConvertFrom-Json
        $DefaultRecorderId = $existingConfig.Recorder.RecorderId
        $DefaultRecorderHostname = $existingConfig.Recorder.RecorderHostname
        $DefaultManagerUrl = $existingConfig.Recorder.ManagementServerUrl
    } catch {
        Write-Host "Warning: Could not parse existing config" -ForegroundColor Yellow
    }
}

# Get configuration from user
Write-Host ""
Write-Host "Configuration" -ForegroundColor Yellow
Write-Host "-------------" -ForegroundColor Yellow

# Management Server URL
if ($DefaultManagerUrl) {
    $prompt = "Enter management server URL [$DefaultManagerUrl]"
} else {
    $prompt = "Enter management server URL (e.g., http://192.168.0.150:5000)"
}
$ManagementServerUrl = Read-Host $prompt
if ([string]::IsNullOrWhiteSpace($ManagementServerUrl)) {
    $ManagementServerUrl = $DefaultManagerUrl
}
if ([string]::IsNullOrWhiteSpace($ManagementServerUrl)) {
    Write-Host "ERROR: Management server URL is required" -ForegroundColor Red
    exit 1
}

# Recorder Hostname
$defaultHostname = if ($DefaultRecorderHostname) { $DefaultRecorderHostname } else { $env:COMPUTERNAME }
$RecorderHostname = Read-Host "Enter this recorder's hostname [$defaultHostname]"
if ([string]::IsNullOrWhiteSpace($RecorderHostname)) {
    $RecorderHostname = $defaultHostname
}

# Recorder ID
$defaultId = if ($DefaultRecorderId) { $DefaultRecorderId } else { "windows-$($RecorderHostname.ToLower())" }
$RecorderId = Read-Host "Enter recorder ID [$defaultId]"
if ([string]::IsNullOrWhiteSpace($RecorderId)) {
    $RecorderId = $defaultId
}

# VNC Port
$VncPort = Read-Host "Enter VNC port [5900]"
if ([string]::IsNullOrWhiteSpace($VncPort)) {
    $VncPort = "5900"
}

Write-Host ""
Write-Host "Configuration Summary:" -ForegroundColor Green
Write-Host "  Management Server: $ManagementServerUrl"
Write-Host "  Recorder Hostname: $RecorderHostname"
Write-Host "  Recorder ID: $RecorderId"
Write-Host "  VNC Port: $VncPort"
Write-Host "  Install Path: $InstallPath"
Write-Host ""

$confirm = Read-Host "Continue? (Y/n)"
if ($confirm -eq "n" -or $confirm -eq "N") {
    Write-Host "Setup cancelled." -ForegroundColor Yellow
    exit 0
}

# Step 1: Check prerequisites
Write-Host ""
Write-Host "Checking prerequisites..." -ForegroundColor Cyan

# Check .NET SDK/Runtime
$dotnetVersion = $null
try {
    $dotnetVersion = & dotnet --version 2>$null
} catch {}

if (-not $dotnetVersion) {
    Write-Host "ERROR: .NET SDK/Runtime not found." -ForegroundColor Red
    Write-Host "Please install .NET 8.0 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    exit 1
}
Write-Host "  .NET version: $dotnetVersion" -ForegroundColor Green

# Check if .NET 8.0 runtime is available
$runtimes = & dotnet --list-runtimes 2>$null
if ($runtimes -notmatch "Microsoft\.WindowsDesktop\.App 8\.") {
    Write-Host "NOTE: .NET 8.0 Windows Desktop Runtime not detected in current session." -ForegroundColor Yellow
    Write-Host "If you just installed .NET, you may need to reboot or open a new shell." -ForegroundColor Yellow
    Write-Host "Download from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    Write-Host "" 
    Write-Host "Continuing with setup - the runtime may already be installed." -ForegroundColor Cyan
}

# Check Chrome
$chromePaths = @(
    "${env:ProgramFiles}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:LocalAppData}\Google\Chrome\Application\chrome.exe"
)
$chromeFound = $false
foreach ($path in $chromePaths) {
    if (Test-Path $path) {
        $chromeFound = $true
        Write-Host "  Chrome found: $path" -ForegroundColor Green
        break
    }
}
if (-not $chromeFound) {
    Write-Host "WARNING: Google Chrome not found in standard locations." -ForegroundColor Yellow
    Write-Host "Please install Chrome from: https://www.google.com/chrome/" -ForegroundColor Yellow
}

# Check TightVNC (optional)
$vncPaths = @(
    "${env:ProgramFiles}\TightVNC\tvnserver.exe",
    "${env:ProgramFiles(x86)}\TightVNC\tvnserver.exe"
)
$vncFound = $false
foreach ($path in $vncPaths) {
    if (Test-Path $path) {
        $vncFound = $true
        Write-Host "  TightVNC found: $path" -ForegroundColor Green
        break
    }
}
if (-not $vncFound) {
    Write-Host "  TightVNC not found (optional - needed for remote control)" -ForegroundColor Yellow
}

# Step 2: Build the application
Write-Host ""
Write-Host "Building application..." -ForegroundColor Cyan

if (-not $SkipBuild) {
    $projectPath = Join-Path $ScriptDir "Recast.WindowsRecorder.csproj"
    if (-not (Test-Path $projectPath)) {
        Write-Host "ERROR: Project file not found at $projectPath" -ForegroundColor Red
        exit 1
    }
    
    Push-Location $ScriptDir
    try {
        Write-Host "  Restoring packages..."
        & dotnet restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
        
        Write-Host "  Building Release configuration..."
        & dotnet publish -c Release -o "$ScriptDir\publish" --self-contained false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
        
        Write-Host "  Build successful!" -ForegroundColor Green
    } catch {
        Write-Host "ERROR: Build failed: $_" -ForegroundColor Red
        exit 1
    } finally {
        Pop-Location
    }
} else {
    Write-Host "  Skipping build (--SkipBuild specified)" -ForegroundColor Yellow
}

# Step 3: Create installation directory
Write-Host ""
Write-Host "Creating installation directory..." -ForegroundColor Cyan

if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Write-Host "  Created: $InstallPath" -ForegroundColor Green
} else {
    Write-Host "  Directory exists: $InstallPath" -ForegroundColor Green
}

# Create recordings directory
$recordingsPath = Join-Path $InstallPath "recordings"
if (-not (Test-Path $recordingsPath)) {
    New-Item -ItemType Directory -Path $recordingsPath -Force | Out-Null
    Write-Host "  Created: $recordingsPath" -ForegroundColor Green
}

# Create logs directory
$logsPath = Join-Path $InstallPath "logs"
if (-not (Test-Path $logsPath)) {
    New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
    Write-Host "  Created: $logsPath" -ForegroundColor Green
}

# Grant Users group write access to recordings and logs directories
# This allows the app to run without admin privileges
Write-Host "  Setting directory permissions..." -ForegroundColor Cyan
try {
    $acl = Get-Acl $recordingsPath
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Users", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl $recordingsPath $acl
    Write-Host "  Granted Users write access to recordings directory" -ForegroundColor Green
    
    $acl = Get-Acl $logsPath
    $acl.SetAccessRule($rule)
    Set-Acl $logsPath $acl
    Write-Host "  Granted Users write access to logs directory" -ForegroundColor Green
    
    # Also grant write access to the main install directory for appsettings updates
    $acl = Get-Acl $InstallPath
    $acl.SetAccessRule($rule)
    Set-Acl $InstallPath $acl
    Write-Host "  Granted Users write access to install directory" -ForegroundColor Green
} catch {
    Write-Host "  WARNING: Could not set directory permissions: $_" -ForegroundColor Yellow
    Write-Host "  You may need to run the application as Administrator" -ForegroundColor Yellow
}

# Step 4: Copy files
Write-Host ""
Write-Host "Copying application files..." -ForegroundColor Cyan

$publishPath = Join-Path $ScriptDir "publish"
if (-not (Test-Path $publishPath)) {
    Write-Host "ERROR: Publish folder not found. Run without -SkipBuild first." -ForegroundColor Red
    exit 1
}

# Copy all files EXCEPT appsettings.json
Copy-Item -Path "$publishPath\*" -Destination $InstallPath -Recurse -Force -Exclude "appsettings.json"
Write-Host "  Copied application files to $InstallPath" -ForegroundColor Green

# Handle the JSON Merge logic
$sourceConfig = "$publishPath\appsettings.json"
$destConfig   = "$InstallPath\appsettings.json"
if (Test-Path $destConfig) {
    Write-Host "Merging appsettings.json..." -ForegroundColor Cyan

    # Read both files (preserve structure and order)
    # Note: removed -AsHashtable for compatibility with PowerShell 5.1
    $sourceJson = Get-Content $sourceConfig -Raw | ConvertFrom-Json
    $destJson   = Get-Content $destConfig -Raw | ConvertFrom-Json

    # Create an Ordered Dictionary to hold the final result to preserve Source order
    $mergedRecorder = [ordered]@{}

    # We iterate through the SOURCE keys to ensure the new file follows the Source's order
    # Using PSObject.Properties to iterate over PSCustomObject
    foreach ($prop in $sourceJson.Recorder.PSObject.Properties) {
        $key = $prop.Name
        # Check if destination has this property
        if ($destJson.Recorder.PSObject.Properties.Name -contains $key) {
            # Priority: Existing Destination Value
            $mergedRecorder[$key] = $destJson.Recorder.$key
        }
        else {
            # Missing in Destination: Use Source Value
            $mergedRecorder[$key] = $prop.Value
            Write-Host "  Adding new setting: $key" -ForegroundColor Green
        }
    }

    # Reconstruct the root object
    $finalObject = @{ Recorder = $mergedRecorder }

    # Save the file (Depth 10 ensures nested objects aren't cut off)
    $finalObject | ConvertTo-Json -Depth 10 | Set-Content $destConfig
}
else {
    # Destination doesn't exist? Just copy the source file.
    Write-Host "No existing config found. Copying fresh appsettings.json." -ForegroundColor Green
    Copy-Item -Path $sourceConfig -Destination $destConfig
}

# Step 6: Configure firewall
Write-Host ""
Write-Host "Configuring Windows Firewall..." -ForegroundColor Cyan

if (-not $SkipFirewall) {
    try {
        # Remove existing rules if they exist
        Get-NetFirewallRule -DisplayName "Recast Recorder*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        
        # Add inbound rule for the recorder web server (port 5001)
        New-NetFirewallRule -DisplayName "Recast Recorder Web Server" `
            -Direction Inbound -Protocol TCP -LocalPort 5001 `
            -Action Allow -Profile Any `
            -Description "Allow inbound connections to Recast Windows Recorder web server" | Out-Null
        Write-Host "  Added firewall rule for port 5001 (Web Server)" -ForegroundColor Green
        
        # Add inbound rule for VNC
        New-NetFirewallRule -DisplayName "Recast Recorder VNC" `
            -Direction Inbound -Protocol TCP -LocalPort 5900-5999 `
            -Action Allow -Profile Any `
            -Description "Allow inbound VNC connections for Recast Recorder" | Out-Null
        Write-Host "  Added firewall rule for ports 5900-5999 (VNC)" -ForegroundColor Green
    } catch {
        Write-Host "  WARNING: Could not configure firewall: $_" -ForegroundColor Yellow
    }
} else {
    Write-Host "  Skipping firewall configuration (--SkipFirewall specified)" -ForegroundColor Yellow
}

# Step 7: Create Scheduled Task (optional)
Write-Host ""
Write-Host "Task Scheduler setup..." -ForegroundColor Cyan
 
if (-not $SkipScheduledTask) {
    $taskName = "RecastWindowsRecorder"
    $exePath = Join-Path $InstallPath "Recast.WindowsRecorder.exe"
    $currentUser = "$env:USERDOMAIN\\$env:USERNAME"
 
    # Check if task already exists
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
 
    if ($existingTask) {
        Write-Host "  Scheduled Task '$taskName' already exists." -ForegroundColor Yellow
        $reinstall = Read-Host "  Reinstall Scheduled Task? (y/N)"
        if ($reinstall -eq "y" -or $reinstall -eq "Y") {
            Write-Host "  Removing existing Scheduled Task..."
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        } else {
            Write-Host "  Keeping existing Scheduled Task configuration." -ForegroundColor Green
            $SkipScheduledTask = $true
        }
    }
 
    if (-not $SkipScheduledTask) {
        try {
            Write-Host "  Creating Scheduled Task (at logon)..."

            $action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $InstallPath
            $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
            $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited            
            $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($logonTrigger) -Principal $principal -Settings $settings -Description "Recast Windows Recorder - Starts at logon" | Out-Null

            Write-Host "  Scheduled Task created successfully!" -ForegroundColor Green
            Write-Host "  Trigger: At logon" -ForegroundColor Green
 
            $startNow = Read-Host "  Start recorder now? (Y/n)"
            if ($startNow -ne "n" -and $startNow -ne "N") {
                Start-ScheduledTask -TaskName $taskName
                Write-Host "  Recorder started via Scheduled Task!" -ForegroundColor Green
            }
        } catch {
            Write-Host "  WARNING: Could not create Scheduled Task: $_" -ForegroundColor Yellow
            Write-Host "  You can run the application manually or add it to the Startup folder." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  Skipping Task Scheduler setup (--SkipScheduledTask specified)" -ForegroundColor Yellow
}

# Step 8: Create desktop shortcut
Write-Host ""
Write-Host "Creating shortcuts..." -ForegroundColor Cyan

$desktopPath = [Environment]::GetFolderPath("CommonDesktopDirectory")
$shortcutPath = Join-Path $desktopPath "Recast Recorder.lnk"
$exePath = Join-Path $InstallPath "Recast.WindowsRecorder.exe"

try {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = "Recast Windows Recorder"
    $shortcut.Save()
    Write-Host "  Created desktop shortcut: $shortcutPath" -ForegroundColor Green
} catch {
    Write-Host "  WARNING: Could not create desktop shortcut: $_" -ForegroundColor Yellow
}

# Create Start Menu shortcut
$startMenuPath = [Environment]::GetFolderPath("CommonPrograms")
$startMenuFolder = Join-Path $startMenuPath "Recast"
if (-not (Test-Path $startMenuFolder)) {
    New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null
}
$startMenuShortcut = Join-Path $startMenuFolder "Recast Recorder.lnk"

try {
    $shortcut = $shell.CreateShortcut($startMenuShortcut)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = "Recast Windows Recorder"
    $shortcut.Save()
    Write-Host "  Created Start Menu shortcut" -ForegroundColor Green
} catch {
    Write-Host "  WARNING: Could not create Start Menu shortcut: $_" -ForegroundColor Yellow
}

# Done!
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Setup Complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Installation Summary:" -ForegroundColor Yellow
Write-Host "  Install Path: $InstallPath"
Write-Host "  Config File: $appSettingsPath"
Write-Host "  Recordings: $recordingsPath"
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host ""
Write-Host "1. If running via Task Scheduler (recommended):"
Write-Host "   - Check status: Get-ScheduledTask -TaskName RecastWindowsRecorder"
Write-Host "   - Start now: Start-ScheduledTask -TaskName RecastWindowsRecorder"
Write-Host ""
Write-Host "2. If running manually:"
Write-Host "   - Double-click the desktop shortcut, or run:"
Write-Host "   - $exePath"
Write-Host ""
Write-Host "3. Access the control UI:"
Write-Host "   - http://localhost:5001/control"
Write-Host ""
Write-Host "4. Optional - Install additional components:"
Write-Host "   - FFmpeg: https://ffmpeg.org/download.html (add to PATH)"
Write-Host "   - VB-Audio Virtual Cable: https://vb-audio.com/Cable/"
Write-Host "   - TightVNC Server: https://www.tightvnc.com/download.php"
Write-Host ""
