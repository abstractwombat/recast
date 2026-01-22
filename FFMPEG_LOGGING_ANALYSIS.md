# FFmpeg Logging Analysis and Improvements

## Problem Identified

The recast-recorder-windows application failed to start FFmpeg jobs repeatedly. Analysis of the logs revealed:

### Current State
- **FFmpeg logs only contain version header** (~3KB files)
- **No actual error messages** are being captured
- **Process exits immediately** after printing version information
- **No diagnostic output** to determine why FFmpeg is failing

### Log Evidence
All FFmpeg job logs (jobs 21 and 22) show identical pattern:
1. Header with command and configuration ✓
2. FFmpeg version information ✓
3. **Then nothing** - process appears to exit
4. No error messages, no warnings, no failure details

### Root Cause
FFmpeg is failing **during initialization** before it can produce meaningful error output. This could be:
- Invalid command-line arguments
- Missing DLL dependencies
- Hardware acceleration issues (NVENC/ddagrab)
- Audio device problems (VB-Audio Virtual Cable)
- Process crash during startup

## Improvements Implemented

Enhanced `RecordingManager.cs` with comprehensive logging:

### 1. **Stdout Capture**
- Added parallel stdout stream reader
- FFmpeg might write errors to stdout instead of stderr
- Both streams now logged with `[STDOUT]` and `[STDERR]` prefixes

### 2. **Process Exit Monitoring**
- Dedicated task to monitor process exit
- Captures exact exit code and runtime
- Logs to both application log and FFmpeg log file
- Detects early exits (< 2 seconds) as configuration errors

### 3. **Enhanced Error Reporting**
- Logs both stderr AND stdout in error messages
- Tracks last 20 lines from each stream
- Reports if no output captured (indicates crash)
- Provides runtime and exit code in diagnostics

### 4. **Better Diagnostics**
- Timestamps for process start/exit
- Runtime calculation to detect immediate failures
- Explicit warnings when process exits early
- Guidance messages for troubleshooting

## What You'll See Now

### In FFmpeg Log Files
```
========== FFmpeg Recording Log ==========
Job ID: 21
Start Time: 2026-01-17 01:58:40 UTC
FFmpeg Path: ffmpeg.exe
Full Command: [full command]
Working Directory: [path]
==========================================

[STDERR] ffmpeg version 2026-01-07...
[STDERR] built with gcc 15.2.0...
[STDERR] [any error messages]
[STDOUT] [any stdout messages]

========== Process Exit Info ==========
Exit Code: 1
Runtime: 0.15 seconds
Exit Time: 2026-01-17 01:58:40 UTC
=======================================
```

### In Application Logs
- FFmpeg process exit warnings with runtime and exit code
- Recent stderr output (last 20 lines)
- Recent stdout output (last 20 lines)
- Explicit error if process exits within 2 seconds
- Warning if no output captured at all

## Next Steps to Diagnose

1. **Run the application again** - The enhanced logging will now capture:
   - Actual FFmpeg error messages (if any)
   - Exit codes to identify failure type
   - Timing to confirm immediate exit
   - Both stdout and stderr streams

2. **Check the new log output** for:
   - Exit code (common codes: 1=error, -1073741819=crash/access violation)
   - Any error messages before exit
   - Runtime (< 1s suggests argument/config issue)

3. **Common FFmpeg Issues to Check**:
   - **NVENC**: Ensure NVIDIA drivers are up to date
   - **ddagrab**: Requires Windows 10/11 with Desktop Duplication API
   - **Audio device**: Verify "CABLE Output (VB-Audio Virtual Cable)" exists
   - **DLL dependencies**: Run `ffmpeg.exe` manually to check for missing DLLs

4. **Manual Test Command**:
   ```powershell
   # Test if ffmpeg runs at all
   ffmpeg.exe -version
   
   # Test ddagrab capture
   ffmpeg.exe -f lavfi -i "ddagrab=output_idx=0:draw_mouse=1:framerate=60" -t 5 test.mp4
   
   # Test audio device
   ffmpeg.exe -f dshow -list_devices true -i dummy
   ```

## Files Modified

- `c:\Users\micha\Documents\recast\recast-recorder-windows\Services\RecordingManager.cs`
  - Added stdout capture task
  - Added process exit monitoring task
  - Enhanced error reporting with both streams
  - Added early exit detection and diagnostics
