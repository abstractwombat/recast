# Browser Controllers

This directory contains browser automation scripts for different video streaming sites. Each controller handles the specific requirements for playing videos at fullscreen on a particular site.

## Available Controllers

### videojs.py
- **Site**: VideoJS based sites
- **Features**:
  - Waits for video player to load
  - Handles loader overlay
  - Clicks play button
  - Enters fullscreen via double-click
  - Waits for video to stabilize

### youtube.py
- **Site**: YouTube
- **Features**:
  - Handles age verification overlays
  - Ensures video is playing
  - Enters fullscreen using 'f' key
  - Fallback to double-click if needed

### generic.py
- **Site**: Any video site (fallback)
- **Features**:
  - Simple play button detection
  - Center-screen click fallback
  - Double-click for fullscreen
  - Works with most video players

## Creating a New Controller

To add support for a new site, create a new Python file in this directory:

```python
"""
Browser Controller for MySite
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
import pyautogui 
from selenium.common.exceptions import TimeoutException

logging.basicConfig(level=logging.INFO, format='(Browser-MySite) %(levelname)s: %(message)s')

def run_browser_session(target_url, screen_width, screen_height, ready_flag_path):
    """
    Sets up a Selenium browser session for MySite.
    
    Args:
        target_url: URL to navigate to
        screen_width: Screen width in pixels
        screen_height: Screen height in pixels
        ready_flag_path: Path to create ready flag file
    """
    
    display_env = os.environ.get('DISPLAY')
    if not display_env:
        logging.error("ERROR: DISPLAY environment variable not set.")
        sys.exit(1)
    
    os.environ['DISPLAY'] = display_env
    logging.info(f"Browser controller starting on DISPLAY: {display_env}")
    
    # Setup Chrome Options
    chrome_options = Options()
    chrome_options.add_argument(f"--window-size={screen_width},{screen_height}")
    chrome_options.add_argument("--window-position=0,0")
    chrome_options.add_argument("--disable-gpu")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument("--disable-infobars")
    chrome_options.add_argument("--start-maximized")
    chrome_options.add_experimental_option("excludeSwitches", ["enable-automation"])
    chrome_options.add_argument(f"--display={display_env}")
    
    driver = None
    try:
        logging.info("Starting Chrome WebDriver...")
        driver = webdriver.Chrome(options=chrome_options)
        
        # Navigate to URL
        driver.get(target_url)
        logging.info(f"Navigated to: {target_url}")
        
        # YOUR SITE-SPECIFIC LOGIC HERE
        # 1. Wait for video player to load
        # 2. Click play button
        # 3. Enter fullscreen
        # 4. Wait for video to stabilize
        
        time.sleep(5)  # Example wait
        
        # Signal readiness
        with open(ready_flag_path, 'w') as f:
            f.write("READY")
        logging.info(f"Browser ready, recording should start.")
        
        # Keep running until terminated
        while True:
            time.sleep(1)
    
    except Exception as e:
        logging.error(f"Browser error: {e}")
        logging.error(traceback.format_exc())
    finally:
        if driver:
            logging.info("Quitting Chrome driver.")
            driver.quit()

if __name__ == '__main__':
    # For standalone testing
    import sys
    if len(sys.argv) < 2:
        print("Usage: python mysite.py <url>")
        sys.exit(1)
    
    run_browser_session(
        target_url=sys.argv[1],
        screen_width=1920,
        screen_height=1080,
        ready_flag_path="browser_ready.flag"
    )
```

## Testing a Controller

Test your controller standalone before using it in the recorder:

```bash
# Set DISPLAY variable
export DISPLAY=:99

# Start Xvfb
Xvfb :99 -screen 0 1920x1080x24 &

# Test the controller
python3 browser_controllers/mysite.py "https://example.com/video"
```

## Registering a New Controller

After creating a new controller, register it in the management server:

1. Edit `management_server.py`
2. Add your controller to the `get_browser_controllers()` function:

```python
@app.route('/api/browser_controllers')
def get_browser_controllers():
    controllers = [
        # ... existing controllers ...
        {
            'id': 'mysite',
            'name': 'MySite',
            'description': 'Optimized for MySite.com'
        }
    ]
    return jsonify({'controllers': controllers})
```

3. Add validation in `schedule_job()`:

```python
valid_controllers = ['videojs', 'youtube', 'generic', 'mysite']
```

## Tips for Site-Specific Controllers

### Finding Selectors

Use browser DevTools to inspect video player elements:

```python
# CSS Selectors
driver.find_element(By.CSS_SELECTOR, 'button.play-button')

# XPath
driver.find_element(By.XPATH, "//button[@aria-label='Play']")

# ID
driver.find_element(By.ID, 'video-player')
```

### Handling Overlays

Wait for overlays to disappear:

```python
WebDriverWait(driver, 10).until(
    EC.invisibility_of_element_located((By.CSS_SELECTOR, '.loading-overlay'))
)
```

### Fullscreen Methods

Different sites require different fullscreen approaches:

1. **Double-click** (most common):
```python
pyautogui.doubleClick(x=screen_width // 2, y=screen_height // 2)
```

2. **Keyboard shortcut**:
```python
from selenium.webdriver.common.keys import Keys
video_element.send_keys('f')  # YouTube
```

3. **Fullscreen button**:
```python
fullscreen_btn = driver.find_element(By.CSS_SELECTOR, 'button.fullscreen')
fullscreen_btn.click()
```

4. **JavaScript**:
```python
driver.execute_script("document.querySelector('video').requestFullscreen()")
```

### Waiting for Video to Load

Ensure video is actually playing:

```python
# Wait for video element to have data
WebDriverWait(driver, 20).until(
    lambda d: d.execute_script(
        "return document.querySelector('video').readyState > 0"
    )
)

# Check if video is playing
is_playing = driver.execute_script(
    "return !document.querySelector('video').paused"
)
```

### Handling Authentication

Some sites require login:

```python
# Fill login form
username_field = driver.find_element(By.ID, 'username')
username_field.send_keys(os.environ.get('SITE_USERNAME'))

password_field = driver.find_element(By.ID, 'password')
password_field.send_keys(os.environ.get('SITE_PASSWORD'))

login_button = driver.find_element(By.CSS_SELECTOR, 'button[type="submit"]')
login_button.click()

# Wait for redirect
time.sleep(3)
```

## Debugging

Enable verbose logging:

```python
logging.basicConfig(level=logging.DEBUG)
```

Take screenshots at key points:

```python
driver.save_screenshot(f'/tmp/step1_loaded.png')
```

Print page source:

```python
print(driver.page_source)
```

## Common Issues

### Element Not Found
- Wait longer for page to load
- Check if element is in an iframe
- Verify selector is correct

### Click Not Working
- Element might be covered by overlay
- Try JavaScript click: `driver.execute_script("arguments[0].click()", element)`
- Use PyAutoGUI as fallback

### Fullscreen Not Activating
- Try different methods (keyboard, double-click, button)
- Ensure video has focus
- Check if site blocks fullscreen in automation

### Video Not Playing
- Check autoplay policy
- Manually click play button
- Verify audio/video codecs are supported
