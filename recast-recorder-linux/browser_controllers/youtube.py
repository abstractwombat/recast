"""
Browser Controller for YouTube
Handles video playback automation specific to YouTube.
"""

import os
import time
import sys
import logging
import traceback
from selenium import webdriver
from selenium.webdriver.chrome.options import Options
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
from selenium.webdriver.common.keys import Keys
import pyautogui 
from selenium.common.exceptions import TimeoutException, NoSuchElementException
from pathlib import Path
import tempfile
import shutil

logging.basicConfig(level=logging.INFO, format='(Browser-YouTube) %(levelname)s: %(message)s')

def run_browser_session(target_url, screen_width, screen_height, ready_flag_path):
    """
    Sets up a Selenium browser session for YouTube, signals readiness, and runs until terminated.
    
    Args:
        target_url: YouTube URL to navigate to
        screen_width: Screen width in pixels
        screen_height: Screen height in pixels
        ready_flag_path: Path to create ready flag file
    """
    
    display_env = os.environ.get('DISPLAY')
    if not display_env:
        logging.error("ERROR: DISPLAY environment variable not set. Cannot run browser.")
        sys.exit(1)
    
    os.environ['DISPLAY'] = display_env
    
    # Setup Chrome Options
    chrome_options = Options()
    chrome_options.add_argument(f"--window-size={screen_width},{screen_height}")
    chrome_options.add_argument("--window-position=0,0")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument("--no-info-bars")
    chrome_options.add_argument('--disable-dev-shm-usage')
    chrome_options.add_argument('--disable-session-crashed-bubble')
    chrome_options.add_argument('--hide-crash-restore-bubble')
    chrome_options.add_argument("--disable-infobars")
    chrome_options.add_argument("--start-maximized")
    chrome_options.add_experimental_option("excludeSwitches", ["enable-automation"])
    chrome_options.add_experimental_option('useAutomationExtension', False)
    chrome_options.add_argument(f"--display={display_env}")

    # Disable autoplay restrictions
    chrome_options.add_argument("--autoplay-policy=no-user-gesture-required")

    # Prefer GPU decode when available (safe no-ops if unsupported)
    chrome_options.add_argument("--ignore-gpu-blocklist")
    chrome_options.add_argument("--enable-gpu-rasterization")
    chrome_options.add_argument("--enable-zero-copy")
    chrome_options.add_argument("--enable-accelerated-video-decode")
    chrome_options.add_argument("--use-gl=egl")

    # Reduce obvious automation fingerprints
    chrome_options.add_argument("--disable-blink-features=AutomationControlled")
    chrome_options.add_argument("--disable-features=Translate,BackForwardCache,AudioServiceOutOfProcess")

    # Profile strategy: default to EPHEMERAL per session to avoid extensions/state issues.
    # Override with CHROME_USER_DATA_DIR to use a persistent profile.
    ephemeral_dir = None
    user_data_dir = os.environ.get('CHROME_USER_DATA_DIR')
    if user_data_dir:
        try:
            Path(user_data_dir).mkdir(parents=True, exist_ok=True)
        except Exception:
            pass
    else:
        ephemeral_dir = tempfile.mkdtemp(prefix='recast-youtube-')
        user_data_dir = ephemeral_dir
    chrome_options.add_argument(f"--user-data-dir={user_data_dir}")
    profile_dir = os.environ.get('CHROME_PROFILE_DIR', 'Default')
    chrome_options.add_argument(f"--profile-directory={profile_dir}")
    
    driver = None
    keep_open = os.environ.get('KEEP_OPEN_ON_ERROR', '0') == '1'
    should_quit = True
    try:
        try:
            logging.info("Chrome args: %s", getattr(chrome_options, 'arguments', []))
        except Exception:
            pass
        logging.info("Starting Chrome WebDriver...")
        driver = webdriver.Chrome(options=chrome_options)
        # Patch automation signals early
        try:
            driver.execute_cdp_cmd("Page.addScriptToEvaluateOnNewDocument", {
                "source": """
                Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
                try { window.chrome = window.chrome || { runtime: {} }; } catch(e){}
                Object.defineProperty(navigator, 'languages', {get: () => ['en-US', 'en']});
                Object.defineProperty(navigator, 'plugins', {get: () => [1,2,3,4,5]});
                """
            })
        except Exception:
            pass
        
        # Navigate to YouTube URL
        driver.get(target_url)
        logging.info(f"Navigated to: {target_url}")
        
        # Wait for video player to load
        try:
            video_element = WebDriverWait(driver, 15).until(
                EC.presence_of_element_located((By.CSS_SELECTOR, "video.html5-main-video"))
            )
            logging.info("YouTube video player found.")
        except TimeoutException:
            logging.error("YouTube video player not found within 15 seconds.")
            if keep_open:
                logging.info("KEEP_OPEN_ON_ERROR is set; keeping browser open for inspection.")
                should_quit = False
                raise RuntimeError("KEEP_OPEN_ON_ERROR: player not found")
            return
        
        # Wait a moment for page to fully load
        time.sleep(3)
        
        # Click on video to ensure it's focused and starts playing
        try:
            video_element.click()
            logging.info("Clicked on video element.")
            time.sleep(1)
        except Exception as e:
            logging.warning(f"Could not click video element: {e}")
        
        # Try to dismiss any overlays (age verification, etc.)
        try:
            # Look for "I understand and wish to proceed" button
            proceed_button = driver.find_element(By.CSS_SELECTOR, "button[aria-label*='proceed'], button[aria-label*='understand']")
            proceed_button.click()
            logging.info("Dismissed age verification overlay.")
            time.sleep(2)
        except NoSuchElementException:
            logging.info("No age verification overlay found.")
        
        # Ensure video is playing
        try:
            is_paused = driver.execute_script("return document.querySelector('video.html5-main-video').paused")
            if is_paused:
                logging.info("Video is paused, attempting to play...")
                # Try clicking play button
                try:
                    play_button = driver.find_element(By.CSS_SELECTOR, "button.ytp-play-button")
                    play_button.click()
                    logging.info("Clicked play button.")
                except NoSuchElementException:
                    # Fallback: use keyboard shortcut
                    video_element.send_keys(Keys.SPACE)
                    logging.info("Sent SPACE key to play video.")
                time.sleep(2)
        except Exception as e:
            logging.warning(f"Error checking/starting playback: {e}")
        
        # Ensure videos are unmuted and volume set to 100%
        try:
            driver.execute_script("""
                (function(){
                    const vids = Array.from(document.querySelectorAll('video'));
                    vids.forEach(v => { try { v.muted = false; v.volume = 1.0; v.play().catch(()=>{}); } catch(e){} });
                    const ytMute = document.querySelector('.ytp-mute-button[aria-label*="Unmute"], .ytp-mute-button[aria-label*="Turn off mute"]');
                    if (ytMute) ytMute.click();
                })();
            """)
            logging.info("Ensured videos are unmuted and volume set to 100%.")
        except Exception as e:
            logging.warning(f"Failed to ensure unmuted state: {e}")

        # Try to force a reasonable quality (to avoid heavy AV1/VP9 on CPU)
        def try_set_quality():
            try:
                # Open settings menu
                gear = WebDriverWait(driver, 5).until(EC.element_to_be_clickable((By.CSS_SELECTOR, '.ytp-settings-button')))
                gear.click()
                time.sleep(0.2)
                # Open "Quality"
                items = driver.find_elements(By.CSS_SELECTOR, '.ytp-menuitem')
                quality_item = None
                for it in items:
                    try:
                        label = it.find_element(By.CSS_SELECTOR, '.ytp-menuitem-label').text.strip()
                        if label.lower() == 'quality':
                            quality_item = it; break
                    except Exception:
                        continue
                if not quality_item:
                    return
                quality_item.click()
                time.sleep(0.2)
                # Prefer 720p then 480p then 360p
                for q in ('720p','480p','360p'):
                    opts = driver.find_elements(By.CSS_SELECTOR, '.ytp-menuitem')
                    for o in opts:
                        try:
                            t = o.text
                            if q in t:
                                o.click(); return
                        except Exception:
                            continue
            except Exception:
                pass

        try_set_quality()

        # Enter fullscreen mode using 'f' key
        logging.info("Attempting to enter fullscreen mode...")
        try:
            # Focus on video and press 'f' key
            video_element.send_keys('f')
            time.sleep(2)
        except Exception as e:
            logging.warning(f"Fullscreen via keyboard failed: {e}")
            # Fallback: try double-click
            try:
                pyautogui.doubleClick(x=screen_width // 2, y=screen_height // 2)
                logging.info("Attempted fullscreen via double-click.")
                time.sleep(2)
            except Exception as e2:
                logging.error(f"Fullscreen double-click failed: {e2}")
        
        # Wait for video to stabilize
        render_delay = 5
        logging.info(f"Waiting {render_delay} seconds for video playback to stabilize...")
        time.sleep(render_delay)
        
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
            # Write error flag so the recorder UI can show a toast
            try:
                err_path = os.environ.get('SESSION_ERROR_FLAG')
                if err_path:
                    from datetime import datetime
                    Path(err_path).write_text(f"{datetime.utcnow().isoformat()}Z YouTube error: {e}\n")
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
        # Cleanup ephemeral profile if used
        try:
            if ephemeral_dir:
                shutil.rmtree(ephemeral_dir, ignore_errors=True)
        except Exception:
            pass

if __name__ == '__main__':
    # For standalone testing
    import sys
    if len(sys.argv) < 2:
        print("Usage: python youtube.py <url>")
        sys.exit(1)
    
    run_browser_session(
        target_url=sys.argv[1],
        screen_width=1920,
        screen_height=1080,
        ready_flag_path="browser_ready.flag"
    )
