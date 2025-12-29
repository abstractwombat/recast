"""
Browser Controller for VideoJS based sites
Handles video playback automation specific to sites using the Video.js player.
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
from selenium.webdriver.common.keys import Keys
from selenium.webdriver import ActionChains
from selenium.common.exceptions import TimeoutException, NoSuchElementException, WebDriverException
from pathlib import Path

logging.basicConfig(level=logging.INFO, format='(Browser-VideoJS) %(levelname)s: %(message)s')

def run_browser_session(target_url, screen_width, screen_height, ready_flag_path):
    """
    Sets up a Selenium browser session for VideoJS sites, signals readiness, and runs until terminated.
    
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
    
    # Ensure pyautogui operates on the correct virtual display
    os.environ['DISPLAY'] = display_env
    
    # Disable X authority to avoid .Xauthority errors
    if 'XAUTHORITY' in os.environ:
        del os.environ['XAUTHORITY']
    
    logging.info(f"Browser controller starting on DISPLAY: {display_env}")
    
    chrome_options = Options()
    chrome_options.add_argument(f"--window-size={screen_width},{screen_height}")
    chrome_options.add_argument("--window-position=0,0")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument("--no-info-bars")
    chrome_options.add_argument("--start-maximized")
    chrome_options.add_argument('--disable-dev-shm-usage')
    chrome_options.add_argument('--disable-session-crashed-bubble')
    chrome_options.add_argument('--hide-crash-restore-bubble')
    chrome_options.add_experimental_option("excludeSwitches", ["enable-automation"])
    chrome_options.add_argument(f"--display={display_env}")
    chrome_options.add_argument("--disable-gpu-vsync")
    chrome_options.add_argument("--disable-frame-rate-limit")
    chrome_options.add_argument("--disable-background-timer-throttling")
    chrome_options.add_argument("--disable-backgrounding-occluded-windows")
    chrome_options.add_argument("--disable-renderer-backgrounding")
    
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
        try:
            logging.info("Chrome args: %s", getattr(chrome_options, 'arguments', []))
        except Exception:
            pass
        logging.info("Starting Chrome WebDriver in non-headless mode...")
        driver = webdriver.Chrome(options=chrome_options)
        
        # Navigate to URL
        driver.get(target_url)
        logging.info(f"Navigated to: {target_url}. Waiting for video player element...")
        try:
            WebDriverWait(driver, 10).until(lambda d: d.execute_script("return document.readyState==='complete'"))
        except Exception:
            pass
        
        # If the player is inside an iframe, switch to the iframe that actually contains a <video> or .video-js
        def _switch_to_frame_with_video(d):
            try:
                d.switch_to.default_content()
            except Exception:
                pass
            frames = d.find_elements(By.TAG_NAME, "iframe")
            for idx, fr in enumerate(frames):
                try:
                    d.switch_to.frame(fr)
                    has_video = d.execute_script("return !!document.querySelector('video, .video-js')")
                    if has_video:
                        logging.info(f"Switched to iframe {idx} that contains a video player.")
                        return True
                    d.switch_to.default_content()
                except Exception:
                    try:
                        d.switch_to.default_content()
                    except Exception:
                        pass
            return False

        _switch_to_frame_with_video(driver)

        # Helper to (re)start playback and enter fullscreen. Returns True on success.
        def _play_and_fullscreen():
            try:
                # Always reset to the frame that contains the player
                try:
                    driver.switch_to.default_content()
                except Exception:
                    pass
                _switch_to_frame_with_video(driver)

                # Wait for the video container / player
                PLAYER_ELEMENT_SELECTOR = ".video-js, .vjs-controls-enabled"
                try:
                    player_element = WebDriverWait(driver, 20).until(
                        EC.presence_of_element_located((By.CSS_SELECTOR, PLAYER_ELEMENT_SELECTOR))
                    )
                except TimeoutException:
                    logging.error("Player element not found after reload/attempt.")
                    return False

                try:
                    video_container_element = player_element.find_element(By.XPATH, "./ancestor-or-self::div[contains(@class,'video-js')]")
                except Exception:
                    video_container_element = player_element

                # Wait for any loader to clear
                try:
                    WebDriverWait(driver, 5).until(
                        EC.invisibility_of_element_located((By.CSS_SELECTOR, 'img[src*="loader-ftv"]'))
                    )
                except TimeoutException:
                    pass

                # Try clicking big play
                try:
                    play_button = WebDriverWait(driver, 5).until(
                        EC.element_to_be_clickable((By.CSS_SELECTOR, 'button.vjs-big-play-button[title="Play Video"]'))
                    )
                    play_button.click()
                except Exception:
                    try:
                        video_container_element.click()
                    except Exception:
                        pass

                # Wait for video readiness
                try:
                    WebDriverWait(driver, 15).until(
                        lambda d: d.execute_script("return document.querySelector('video') && document.querySelector('video').readyState > 0")
                    )
                except TimeoutException:
                    logging.warning("Video did not report ready > 0; continuing anyway.")

                # Try to enter fullscreen (hover to reveal controls)
                try:
                    try:
                        ActionChains(driver).move_to_element(video_container_element).perform()
                        time.sleep(0.2)
                    except Exception:
                        pass
                    FULLSCREEN_BUTTON_SELECTOR = "button.vjs-fullscreen-control, button[title*='Full'], button[aria-label*='Full']"
                    fs_btn = WebDriverWait(driver, 3).until(
                        EC.element_to_be_clickable((By.CSS_SELECTOR, FULLSCREEN_BUTTON_SELECTOR))
                    )
                    driver.execute_script("arguments[0].scrollIntoView({block:'center', inline:'center'});", fs_btn)
                    fs_btn.click()
                except Exception:
                    # Fallbacks: keyboard 'f' then Fullscreen API then double-click
                    try:
                        driver.execute_script("arguments[0].focus();", video_container_element)
                        time.sleep(0.05)
                        video_container_element.send_keys('f')
                    except Exception:
                        try:
                            driver.execute_script(
                                "(function(){var el=document.querySelector('.video-js')||document.querySelector('video');if(!el)return false;var t=el.closest('.video-js')||el;if(t.requestFullscreen){t.requestFullscreen();return true;}if(t.webkitRequestFullscreen){t.webkitRequestFullscreen();return true;}if(t.msRequestFullscreen){t.msRequestFullscreen();return true;}return false;})()"
                            )
                        except Exception:
                            try:
                                loc = video_container_element.location; sz = video_container_element.size
                                cx = int(loc['x'] + sz['width']//2); cy = int(loc['y'] + sz['height']//2)
                                pyautogui.moveTo(cx, cy); time.sleep(0.05); pyautogui.doubleClick(x=cx, y=cy)
                            except Exception:
                                pass

                # Unmute and set volume
                try:
                    driver.execute_script("(function(){Array.from(document.querySelectorAll('video')).forEach(v=>{try{v.muted=false;v.volume=1.0;v.play().catch(()=>{});}catch(e){}})})()")
                except Exception:
                    pass

                return True
            except Exception as e:
                logging.warning(f"_play_and_fullscreen failed: {e}")
                return False
        
        # Wait for the video container
        video_container_element = None
        try:
            PLAYER_ELEMENT_SELECTOR = ".video-js, .vjs-controls-enabled"
            player_element = WebDriverWait(driver, 20).until(
                EC.presence_of_element_located((By.CSS_SELECTOR, PLAYER_ELEMENT_SELECTOR))
            )
            try:
                video_container_element = player_element.find_element(By.XPATH, "./ancestor-or-self::div[contains(@class,'video-js')]")
                logging.info("Video container found.")
            except Exception:
                video_container_element = player_element
                logging.info("Using player element as click target (no explicit video container found).")
        except TimeoutException:
            logging.error("Video player/container not found within 15 seconds. Cannot proceed.")
            if keep_open:
                logging.info("KEEP_OPEN_ON_ERROR is set; keeping browser open for inspection.")
                should_quit = False
                raise RuntimeError("KEEP_OPEN_ON_ERROR: player not found")
            return
        
        # Wait for the loader to disappear
        LOADER_IMAGE_SELECTOR = 'img[src*="loader-ftv"]'
        logging.info("Waiting for video loader image to disappear...")
        try:
            WebDriverWait(driver, 10).until(
                EC.invisibility_of_element_located((By.CSS_SELECTOR, LOADER_IMAGE_SELECTOR))
            )
            logging.info("Video loader disappeared successfully.")
        except TimeoutException:
            logging.warning("Loader image did not disappear within 10 seconds. Attempting to click play anyway.")
        
        # Click the big play button
        BIG_PLAY_BUTTON_SELECTOR = 'button.vjs-big-play-button[title="Play Video"]'
        logging.info("Attempting to click the large Play button overlay...")
        
        try:
            play_button = WebDriverWait(driver, 15).until(
                EC.element_to_be_clickable((By.CSS_SELECTOR, BIG_PLAY_BUTTON_SELECTOR))
            )
            play_button.click()
            logging.info("Successfully clicked the large Play Video button.")
        except (TimeoutException, NoSuchElementException) as e:
            logging.warning(f"Large Play button not found or clickable ({e}). Falling back to center screen click.")
            pyautogui.click(x=screen_width // 2, y=screen_height // 2)
            logging.info("Performed fallback center screen click.")
        
        # Wait for video to start playing
        VIDEO_ELEMENT_SELECTOR = "video"
        logging.info("Waiting for video element to start playback (readyState > 0)...")
        
        try:
            WebDriverWait(driver, 20).until(
                lambda d: d.execute_script(
                    f"return document.querySelector('{VIDEO_ELEMENT_SELECTOR}') && document.querySelector('{VIDEO_ELEMENT_SELECTOR}').readyState > 0"
                )
            )
            logging.info("Video element found and has started loading/playing (readyState > 0).")
        except TimeoutException:
            logging.warning("Video failed to start playing or load within 20 seconds. Proceeding anyway.")
        
        # Enter fullscreen using robust multi-strategy approach
        if video_container_element:
            logging.info("Attempting to enter fullscreen using in-player control, with fallbacks.")
            try:
                # Make sure the control bar is visible
                try:
                    ActionChains(driver).move_to_element(video_container_element).perform()
                    time.sleep(0.2)
                except Exception:
                    pass

                FULLSCREEN_BUTTON_SELECTOR = "button.vjs-fullscreen-control, button[title*='Full'], button[aria-label*='Full']"
                fs_btn = WebDriverWait(driver, 5).until(
                    EC.element_to_be_clickable((By.CSS_SELECTOR, FULLSCREEN_BUTTON_SELECTOR))
                )
                driver.execute_script("arguments[0].scrollIntoView({block:'center', inline:'center'});", fs_btn)
                fs_btn.click()
                logging.info("Clicked the player's fullscreen button.")
            except Exception as e:
                logging.warning(f"Fullscreen button click failed: {e}. Trying keyboard fallback ('f').")
                try:
                    driver.execute_script("arguments[0].focus();", video_container_element)
                    time.sleep(0.1)
                    video_container_element.click()
                    time.sleep(0.1)
                    video_container_element.send_keys('f')
                    logging.info("Sent 'f' to toggle fullscreen.")
                except Exception as e2:
                    logging.warning(f"Keyboard fullscreen toggle failed: {e2}. Trying Fullscreen API.")
                    try:
                        driver.execute_script(
                            """
                            (function(){
                                var el = document.querySelector('.video-js') || document.querySelector('video');
                                if (!el) return false;
                                var target = el.closest('.video-js') || el;
                                if (target.requestFullscreen) { target.requestFullscreen(); return true; }
                                if (target.webkitRequestFullscreen) { target.webkitRequestFullscreen(); return true; }
                                if (target.msRequestFullscreen) { target.msRequestFullscreen(); return true; }
                                return false;
                            })();
                            """
                        )
                        logging.info("Attempted to enter fullscreen via the Fullscreen API.")
                    except Exception as e3:
                        logging.error(f"Fullscreen API attempt failed: {e3}. Falling back to OS-level double-click.")
                        try:
                            location = video_container_element.location
                            size = video_container_element.size
                            click_x = int(location['x'] + (size['width'] // 2))
                            click_y = int(location['y'] + (size['height'] // 2))
                            pyautogui.moveTo(click_x, click_y)
                            time.sleep(0.1)
                            pyautogui.doubleClick(x=click_x, y=click_y)
                            logging.info(f"Performed double-click at ({click_x}, {click_y}) as a last resort.")
                        except Exception as e4:
                            logging.error(f"All fullscreen attempts failed: {e4}")

            # Optionally wait to confirm fullscreen state
            try:
                WebDriverWait(driver, 5).until(lambda d: d.execute_script("return !!document.fullscreenElement"))
            except TimeoutException:
                logging.warning("No document.fullscreenElement detected; video may not have entered fullscreen.")
        else:
            logging.warning("Video container element not found. Skipping fullscreen attempt.")
        
        # Ensure videos are unmuted and at full volume
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

        # Wait for video playback to stabilize
        render_delay = 5
        logging.info(f"Waiting {render_delay} seconds for video playback to stabilize in fullscreen...")
        time.sleep(render_delay)
        
        # Signal readiness
        with open(ready_flag_path, 'w') as f:
            f.write("READY")
        logging.info(f"Synchronization flag '{ready_flag_path}' created. Recording should now start.")
        
        logging.info("Browser session active. Starting stall watchdog...")

        # Stall watchdog configuration
        stall_seconds = int(os.environ.get('VIDEOJS_STALL_SECONDS', '30'))
        poll_seconds = int(os.environ.get('VIDEOJS_WATCHDOG_POLL', '5'))
        max_reload_attempts = int(os.environ.get('VIDEOJS_MAX_RELOADS', '5'))
        reload_count = 0

        # Initialize progress tracking
        last_progress_ts = time.time()
        last_ct = -1.0

        def _get_video_metrics():
            try:
                # Ensure we are inside the frame that has the player
                if not driver.execute_script("return !!document.querySelector('video')"):
                    _switch_to_frame_with_video(driver)
                return driver.execute_script(
                    "return (function(){var v=document.querySelector('video'); if(!v) return {found:false}; return {found:true, ct:v.currentTime||0, rs:v.readyState||0, paused:!!v.paused, ended:!!v.ended};})()"
                )
            except WebDriverException as e:
                # Check for tab crash
                if 'tab crashed' in str(e).lower() or 'target closed' in str(e).lower():
                    return { 'found': False, 'crashed': True }
                try:
                    driver.switch_to.default_content()
                except Exception:
                    pass
                return { 'found': False }
            except Exception:
                try:
                    driver.switch_to.default_content()
                except Exception:
                    pass
                return { 'found': False }

        def _recover_with_reload(crashed=False):
            nonlocal last_progress_ts, last_ct, reload_count
            
            reload_count += 1
            if reload_count > max_reload_attempts:
                error_msg = f"Max reload attempts ({max_reload_attempts}) reached. Video playback unrecoverable."
                logging.error(error_msg)
                raise Exception(error_msg)
            
            if crashed:
                logging.error(f"Watchdog: Chrome tab crashed! Reload attempt {reload_count}/{max_reload_attempts}...")
            else:
                logging.warning(f"Watchdog: no progress for >{stall_seconds}s; reload attempt {reload_count}/{max_reload_attempts}...")
            
            try:
                driver.switch_to.default_content()
            except Exception:
                pass
            
            # Clear browser cache/memory before reload to prevent resource accumulation
            # try:
            #     driver.execute_script("window.localStorage.clear();")
            #     driver.execute_script("window.sessionStorage.clear();")
            # except Exception:
            #     pass
            
            # Add delay to allow cleanup
            time.sleep(2)
            
            try:
                driver.refresh()
                WebDriverWait(driver, 20).until(lambda d: d.execute_script("return document.readyState==='complete'"))
            except Exception as e:
                logging.error(f"Page refresh failed: {e}")
                # If refresh fails due to crash, try navigating to URL again
                try:
                    driver.get(target_url)
                    WebDriverWait(driver, 20).until(lambda d: d.execute_script("return document.readyState==='complete'"))
                except Exception as e2:
                    logging.error(f"Navigate to URL also failed: {e2}")
            
            # Re-run play + fullscreen sequence
            ok = _play_and_fullscreen()
            if not ok:
                logging.warning("Watchdog: reinit failed; will keep monitoring")
            else:
                # Reset reload counter on successful recovery
                reload_count = 0
                logging.info("Watchdog: recovery successful, reset reload counter")
            
            # Reset progress timers regardless
            last_progress_ts = time.time()
            try:
                m = _get_video_metrics()
                last_ct = float(m.get('ct', 0)) if m and m.get('found') else -1.0
            except Exception:
                last_ct = -1.0
        
        # Initial metrics baseline
        try:
            m0 = _get_video_metrics()
            if m0 and m0.get('found'):
                last_ct = float(m0.get('ct', 0))
                last_progress_ts = time.time()
        except Exception:
            pass

        # Watchdog loop
        while True:
            time.sleep(max(1, poll_seconds))
            now = time.time()
            m = _get_video_metrics()
            
            # Check for crash
            if m and m.get('crashed'):
                logging.error("Chrome tab crash detected!")
                _recover_with_reload(crashed=True)
                continue
            
            if not m or not m.get('found'):
                # If no video element, treat as stalled
                if now - last_progress_ts >= stall_seconds:
                    _recover_with_reload()
                continue

            ct = float(m.get('ct', 0))
            rs = int(m.get('rs', 0))

            if last_ct < 0 or ct > last_ct + 0.25:  # progress threshold
                last_ct = ct
                last_progress_ts = now
                continue

            # No progress this poll
            if now - last_progress_ts >= stall_seconds:
                _recover_with_reload()
    
    except Exception as e:
        logging.error(f"An error occurred in the browser session: {e}")
        logging.error(traceback.format_exc())
        if keep_open:
            # Write error flag so the recorder UI can display a toast
            try:
                err_path = os.environ.get('SESSION_ERROR_FLAG')
                if err_path:
                    from datetime import datetime
                    Path(err_path).write_text(f"{datetime.utcnow().isoformat()}Z VideoJS error: {e}\n")
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
        print("Usage: python videojs.py <url>")
        sys.exit(1)
    
    run_browser_session(
        target_url=sys.argv[1],
        screen_width=1920,
        screen_height=1080,
        ready_flag_path="browser_ready.flag"
    )
