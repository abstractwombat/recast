#!/usr/bin/env python3
"""
Standalone test script for browser controllers.
Run this to test browser automation without the full recorder setup.

Usage:
    python3 test_browser_controller.py <url> [controller_name] [--vnc]
    
Examples:
    python3 test_browser_controller.py https://www.example.com/videos/test videojs
    python3 test_browser_controller.py https://www.youtube.com/watch?v=dQw4w9WgXcQ youtube
    python3 test_browser_controller.py https://example.com generic --vnc
"""

import sys
import os
import time
import subprocess
from pathlib import Path
from pyvirtualdisplay import Display

# Configuration
SCREEN_WIDTH = 1920
SCREEN_HEIGHT = 1080
TEST_DURATION = 30  # seconds

def test_browser_controller(url, controller_name='generic', enable_vnc=False, width=SCREEN_WIDTH, height=SCREEN_HEIGHT, duration=TEST_DURATION):
    """Test a browser controller with the given URL."""
    
    print(f"Testing browser controller: {controller_name}")
    print(f"URL: {url}")
    print(f"Screen: {width}x{height}")
    print(f"Test duration: {duration} seconds")
    print(f"VNC: {'Enabled' if enable_vnc else 'Disabled'}")
    print("-" * 60)
    
    # Start virtual display
    print("Starting virtual display...")
    display = Display(visible=False, size=(width, height), use_xauth=False)
    display.start()
    display_name = f":{display.display}"
    os.environ['DISPLAY'] = display_name
    
    # Create dummy .Xauthority file to prevent pyautogui import errors
    # pyautogui -> mouseinfo tries to read .Xauthority at import time
    xauth_file = Path.home() / '.Xauthority'
    if not xauth_file.exists():
        xauth_file.touch(mode=0o600)
        print(f"Created dummy .Xauthority file: {xauth_file}")
    
    print(f"Virtual display started: {display_name}")
    
    # Start VNC server if requested
    vnc_process = None
    if enable_vnc:
        try:
            vnc_process = subprocess.Popen(
                ['x11vnc', '-display', display_name, '-forever', '-nopw', '-shared', '-quiet'],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
            print(f"✓ VNC server started on port 5900")
            print(f"  Connect with: vncviewer {os.uname().nodename}:5900")
        except FileNotFoundError:
            print("⚠ x11vnc not found. Install with: sudo apt install x11vnc")
        except Exception as e:
            print(f"⚠ Failed to start VNC: {e}")
    
    # Import browser controller
    base_dir = Path(__file__).parent
    controllers_dir = base_dir / "browser_controllers"
    sys.path.insert(0, str(controllers_dir))
    
    try:
        controller_module = __import__(controller_name)
        print(f"Loaded controller module: {controller_name}")
    except ImportError as e:
        print(f"Error: Could not import controller '{controller_name}': {e}")
        print(f"Available controllers in {controllers_dir}:")
        for f in controllers_dir.glob("*.py"):
            if f.stem != "__init__":
                print(f"  - {f.stem}")
        display.stop()
        return False
    
    # Create ready flag
    ready_flag = Path("/tmp/browser_ready_test.flag")
    if ready_flag.exists():
        ready_flag.unlink()
    
    print(f"Starting browser controller...")
    print(f"Ready flag: {ready_flag}")
    print("-" * 60)
    
    try:
        # Run browser controller in a separate thread
        import threading
        
        def run_controller():
            try:
                controller_module.run_browser_session(
                    target_url=url,
                    screen_width=width,
                    screen_height=height,
                    ready_flag_path=str(ready_flag)
                )
            except Exception as e:
                print(f"Browser controller error: {e}")
                import traceback
                traceback.print_exc()
        
        thread = threading.Thread(target=run_controller, daemon=True)
        thread.start()
        
        # Wait for ready flag
        print("Waiting for browser to be ready...")
        timeout = 60
        start = time.time()
        
        while not ready_flag.exists():
            if time.time() - start > timeout:
                print(f"ERROR: Browser did not signal ready within {timeout} seconds")
                display.stop()
                return False
            time.sleep(0.5)
        
        print(f"✓ Browser ready! (took {time.time() - start:.1f} seconds)")
        print(f"Browser is now running. Waiting {duration} seconds...")
        print("You can connect to the display with VNC or x11vnc if needed.")
        print("-" * 60)
        
        # Let it run for test duration
        time.sleep(duration)
        
        print(f"✓ Test completed successfully!")
        print(f"Browser controller '{controller_name}' is working correctly.")
        
    except KeyboardInterrupt:
        print("\nTest interrupted by user")
    except Exception as e:
        print(f"ERROR: {e}")
        import traceback
        traceback.print_exc()
        return False
    finally:
        print("Cleaning up...")
        if vnc_process:
            vnc_process.terminate()
            print("VNC server stopped")
        display.stop()
        if ready_flag.exists():
            ready_flag.unlink()
    
    return True

def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    
    url = sys.argv[1]
    controller = 'generic'
    enable_vnc = False
    width = SCREEN_WIDTH
    height = SCREEN_HEIGHT
    duration = TEST_DURATION
    
    # Parse arguments
    args = sys.argv[2:]
    i = 0
    while i < len(args):
        arg = args[i]
        if arg == '--vnc':
            enable_vnc = True
            i += 1
        elif arg == '--width' and i + 1 < len(args):
            width = int(args[i+1]); i += 2
        elif arg == '--height' and i + 1 < len(args):
            height = int(args[i+1]); i += 2
        elif arg == '--duration' and i + 1 < len(args):
            duration = int(args[i+1]); i += 2
        elif not arg.startswith('--'):
            controller = arg; i += 1
        else:
            i += 1
    
    success = test_browser_controller(url, controller, enable_vnc, width, height, duration)
    sys.exit(0 if success else 1)

if __name__ == '__main__':
    main()
