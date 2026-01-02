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
    [switch]$SkipService,
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
Write-Host "Step 1: Checking prerequisites..." -ForegroundColor Cyan

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
    Write-Host "WARNING: .NET 8.0 Windows Desktop Runtime not detected." -ForegroundColor Yellow
    Write-Host "The application requires .NET 8.0 Windows Desktop Runtime." -ForegroundColor Yellow
    Write-Host "Download from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    $continue = Read-Host "Continue anyway? (y/N)"
    if ($continue -ne "y" -and $continue -ne "Y") {
        exit 1
    }
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
Write-Host "Step 2: Building application..." -ForegroundColor Cyan

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
Write-Host "Step 3: Creating installation directory..." -ForegroundColor Cyan

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
Write-Host "Step 4: Copying application files..." -ForegroundColor Cyan

$publishPath = Join-Path $ScriptDir "publish"
if (-not (Test-Path $publishPath)) {
    Write-Host "ERROR: Publish folder not found. Run without -SkipBuild first." -ForegroundColor Red
    exit 1
}

# Copy all published files
Copy-Item -Path "$publishPath\*" -Destination $InstallPath -Recurse -Force
Write-Host "  Copied application files to $InstallPath" -ForegroundColor Green

# Step 5: Configure appsettings.json
Write-Host ""
Write-Host "Step 5: Configuring application settings..." -ForegroundColor Cyan

$appSettingsPath = Join-Path $InstallPath "appsettings.json"
$appSettings = @{
    Recorder = @{
        ManagementServerUrl = $ManagementServerUrl
        RecorderId = $RecorderId
        RecorderHostname = $RecorderHostname
        VncPort = [int]$VncPort
        FfmpegPath = "ffmpeg.exe"
        AudioApi = "dshow"
        AudioDevice = "CABLE Output (VB-Audio Virtual Cable)"
        Width = 1920
        Height = 1080
        Framerate = 60
        ForceCfr = $true
        VideoPreset = "medium"
        VideoCrf = 18
        VideoProfile = "high"
        VideoPixFmt = "yuv420p"
        HlsTime = 2
        GopMult = 2
        AudioBitrateK = 192
        AudioSampleRate = 44100
        AudioChannels = 2
        VideoEncoder = "h264_nvenc"
        HwPreset = "p5"
        HwRc = "cbr"
        VideoBitrateK = 9000
        VideoMaxrateK = 9000
        VideoBufsizeK = 18000
        OutputDirectory = "recordings"
        FinalizeHardCapMinutes = 120
        FinalizeStallCapMinutes = 5
        FinalizeLogIntervalSeconds = 20
    }
}

$appSettings | ConvertTo-Json -Depth 10 | Set-Content $appSettingsPath -Encoding UTF8
Write-Host "  Configuration saved to $appSettingsPath" -ForegroundColor Green

# Step 6: Configure firewall
Write-Host ""
Write-Host "Step 6: Configuring Windows Firewall..." -ForegroundColor Cyan

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

# Step 7: Create Windows Service (optional)
Write-Host ""
Write-Host "Step 7: Windows Service setup..." -ForegroundColor Cyan

if (-not $SkipService) {
    $serviceName = "RecastWindowsRecorder"
    $exePath = Join-Path $InstallPath "Recast.WindowsRecorder.exe"
    
    # Check if service already exists
    $existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    
    if ($existingService) {
        Write-Host "  Service '$serviceName' already exists." -ForegroundColor Yellow
        $reinstall = Read-Host "  Reinstall service? (y/N)"
        if ($reinstall -eq "y" -or $reinstall -eq "Y") {
            Write-Host "  Stopping and removing existing service..."
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            & sc.exe delete $serviceName | Out-Null
            Start-Sleep -Seconds 2
        } else {
            Write-Host "  Keeping existing service configuration." -ForegroundColor Green
            $SkipService = $true
        }
    }
    
    if (-not $SkipService) {
        Write-Host "  Creating Windows service..."
        
        # Create the service using sc.exe
        $scArgs = "create `"$serviceName`" binPath= `"$exePath`" start= auto DisplayName= `"Recast Windows Recorder`""
        $result = & cmd.exe /c "sc.exe $scArgs" 2>&1
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Service created successfully!" -ForegroundColor Green
            
            # Set service description
            & sc.exe description $serviceName "Recast Windows Recorder - Records browser sessions and streams via VNC" | Out-Null
            
            # Configure service recovery options (restart on failure)
            & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
            
            Write-Host "  Service configured with auto-restart on failure" -ForegroundColor Green
            
            $startNow = Read-Host "  Start service now? (Y/n)"
            if ($startNow -ne "n" -and $startNow -ne "N") {
                Start-Service -Name $serviceName
                Write-Host "  Service started!" -ForegroundColor Green
            }
        } else {
            Write-Host "  WARNING: Could not create service: $result" -ForegroundColor Yellow
            Write-Host "  You can run the application manually or use NSSM for service management." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  Skipping service setup (--SkipService specified)" -ForegroundColor Yellow
}

# Step 8: Create desktop shortcut
Write-Host ""
Write-Host "Step 8: Creating shortcuts..." -ForegroundColor Cyan

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
Write-Host "1. If running as a service:"
Write-Host "   - Check status: Get-Service RecastWindowsRecorder"
Write-Host "   - View logs: Get-EventLog -LogName Application -Source RecastWindowsRecorder"
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
