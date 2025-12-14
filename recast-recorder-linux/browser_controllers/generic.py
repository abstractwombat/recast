"""
Generic Browser Controller
Simple fallback controller that works with most video sites.
"""

import os
import time
import sys
import logging
import traceback

# Disable MouseInfo before importing pyautogui (requires tkinter which may not be installed)
sys.modules['mouseinfo'] = type(sys)('mouseinfo')
import pyautogui
pyautogui.FAILSAFE = False

from selenium import webdriver
from selenium.webdriver.chrome.options import Options
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
from selenium.common.exceptions import TimeoutException
from pathlib import Path

logging.basicConfig(level=logging.INFO, format='(Browser-Generic) %(levelname)s: %(message)s')

def run_browser_session(target_url, screen_width, screen_height, ready_flag_path):
    """
    Sets up a generic Selenium browser session, signals readiness, and runs until terminated.
    
    Args:
        target_url: URL to navigate to
        screen_width: Screen width in pixels
        screen_height: Screen height in pixels
        ready_flag_path: Path to create ready flag file
    """
    
    display_env = os.environ.get('DISPLAY')
    if not display_env:
        logging.error("ERROR: DISPLAY environment variable not set. Cannot run browser.")
        sys.exit(1)
    
    os.environ['DISPLAY'] = display_env
    logging.info(f"Browser controller starting on DISPLAY: {display_env}")
    
    chrome_options = Options()
    chrome_options.add_argument(f"--window-size={screen_width},{screen_height}")
    chrome_options.add_argument("--window-position=0,0")
    chrome_options.add_argument("--disable-gpu")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument('--disable-dev-shm-usage')
    chrome_options.add_argument('--disable-session-crashed-bubble')
    chrome_options.add_argument("--no-info-bars")
    chrome_options.add_argument("--start-maximized")
    chrome_options.add_argument(f"--display={display_env}")
    chrome_options.add_argument("--autoplay-policy=no-user-gesture-required")

    # Persistent user data dir to keep login cookies/sessions
    user_data_dir = os.environ.get('CHROME_USER_DATA_DIR')
    if not user_data_dir:
        # Fallback to /tmp if env var not set (avoid home directory which may not be writable)
        user_data_dir = '/tmp/recast-chrome'
    try:
        Path(user_data_dir).mkdir(parents=True, exist_ok=True)
    except Exception:
        pass
    chrome_options.add_argument(f"--user-data-dir={user_data_dir}")
    profile_dir = os.environ.get('CHROME_PROFILE_DIR', 'Default')
    chrome_options.add_argument(f"--profile-directory={profile_dir}")
    
    driver = None
    keep_open = os.environ.get('KEEP_OPEN_ON_ERROR', '0') == '1'
    should_quit = True
    try:
        logging.info("Starting Chrome WebDriver...")
        driver = webdriver.Chrome(options=chrome_options)
        
        # Navigate to URL
        driver.get(target_url)
        logging.info(f"Navigated to: {target_url}")
        
        # Wait for page to load
        time.sleep(5)
        
        # Try to find and click any play button
        try:
            play_button = WebDriverWait(driver, 10).until(
                EC.element_to_be_clickable((By.CSS_SELECTOR, 'button[title*="Play"], button[aria-label*="Play"], button.vjs-big-play-button, .ytp-play-button'))
            )
            play_button.click()
            logging.info("Clicked play button.")
        except TimeoutException:
            logging.info("No play button found, trying center click.")
            pyautogui.click(x=screen_width // 2, y=screen_height // 2)
        
        time.sleep(2)
        
        # Ensure video elements are unmuted and volume is up
        try:
            driver.execute_script("""
                (function(){
                    const vids = Array.from(document.querySelectorAll('video'));
                    vids.forEach(v => { try { v.muted = false; v.volume = 1.0; v.play().catch(()=>{}); } catch(e){} });
                    // Unmute common players
                    const ytMute = document.querySelector('.ytp-mute-button[aria-label*="Unmute"], .ytp-mute-button[aria-label*="Turn off mute"]');
                    if (ytMute) ytMute.click();
                })();
            """)
            logging.info("Ensured videos are unmuted and volume set to 100%.")
        except Exception as e:
            logging.warning(f"Failed to ensure unmuted state: {e}")

        # Try to go fullscreen via double-click
        try:
            pyautogui.doubleClick(x=screen_width // 2, y=screen_height // 2)
            logging.info("Attempted fullscreen via double-click.")
        except Exception as e:
            logging.warning(f"Fullscreen failed: {e}")
        
        time.sleep(3)
        
        # Signal readiness
        with open(ready_flag_path, 'w') as f:
            f.write("READY")
        logging.info(f"Synchronization flag '{ready_flag_path}' created. Recording should now start.")
        
        logging.info("Browser session active. Waiting for termination signal...")
        
        # Keep running until parent terminates
        while True:
            time.sleep(1)
    
    except Exception as e:
        logging.error(f"An error occurred in the browser session: {e}")
        logging.error(traceback.format_exc())
        if keep_open:
            try:
                err_path = os.environ.get('SESSION_ERROR_FLAG')
                if err_path:
                    from datetime import datetime
                    Path(err_path).write_text(f"{datetime.utcnow().isoformat()}Z Generic error: {e}\n")
            except Exception:
                pass
            should_quit = False
    finally:
        if driver:
            if should_quit:
                logging.info("Quitting Chrome driver.")
                driver.quit()
            else:
                logging.info("KEEP_OPEN_ON_ERROR active; leaving browser running for inspection.")
                while True:
                    time.sleep(1)

if __name__ == '__main__':
    # For standalone testing
    import sys
    if len(sys.argv) < 2:
        print("Usage: python generic.py <url>")
        sys.exit(1)
    
    run_browser_session(
        target_url=sys.argv[1],
        screen_width=1920,
        screen_height=1080,
        ready_flag_path="browser_ready.flag"
    )
