"""
Recast Linux Recorder - timeshift and record live streams
Polls management server for jobs, executes recordings, and reports status.
Designed to run on Wilma (GPU-enabled) or other recording machines.
"""

import os
import time
import subprocess
import signal
import socket
import logging
import json
import shutil
import atexit
import requests
from pathlib import Path
from datetime import datetime, timedelta, timezone
from flask import Flask, render_template, request, jsonify, send_from_directory
import threading

import config

# Configuration from config.toml
RECORDER_ID = config.recorder["id"]
MANAGEMENT_SERVER_URL = config.recorder["management_server_url"]
RECORDER_HOSTNAME = config.recorder["hostname"]
POLL_INTERVAL = config.recorder["poll_interval"]
HEARTBEAT_INTERVAL = config.recorder["heartbeat_interval"]

# Recording settings
SCREEN_WIDTH = config.recording["screen_width"]
SCREEN_HEIGHT = config.recording["screen_height"]
FRAMERATE = config.recording["framerate"]
AUDIO_SOURCE_NAME = config.recording["audio_source_name"]

# Video encoding settings
VIDEO_PRESET = config.video["preset"]
VIDEO_CRF = config.video["crf"]
VIDEO_MAXRATE_K = str(config.video["maxrate_kbps"])
VIDEO_BUFSIZE_K = str(config.video["bufsize_kbps"])
VIDEO_THREADS = str(config.video["threads"])
VIDEO_PIX_FMT = config.video["pix_fmt"]
VIDEO_PROFILE = config.video["profile"]
GOP_MULT = config.video["gop_multiplier"]
HLS_TIME = str(config.video["hls_time"])
VIDEO_HW_ACCEL = config.video.get("hw_accel", "none")

# Audio encoding settings
AUDIO_BITRATE_K = str(config.audio["bitrate_kbps"])
AUDIO_SAMPLE_RATE = str(config.audio["sample_rate"])
AUDIO_CHANNELS = str(config.audio["channels"])

# Paths
CHROME_PROFILES_BASE_DIR = config.paths["chrome_profiles_dir"]
BASE_DIR = Path(__file__).parent
BROWSER_CONTROLLERS_DIR = BASE_DIR / "browser_controllers"
TEMP_DIR = BASE_DIR / "recorder_temp"
TEMP_DIR.mkdir(exist_ok=True)
HLS_DIR = TEMP_DIR / "hls_stream"
READY_FLAG = TEMP_DIR / 'browser_ready.flag'
BROWSER_LOG = TEMP_DIR / 'browser.log'
AUDIO_FIFO = TEMP_DIR / 'audio_fifo'

# Output directory for final MP4 files
OUTPUT_DIR = Path(config.paths["output_dir"])
OUTPUT_DIR.mkdir(exist_ok=True, parents=True)

# Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - [%(name)s] - %(levelname)s - %(message)s'
)
logger = logging.getLogger('Recast-Recorder')

def log_effective_config():
    try:
        logger.info("Config: RECORDER_ID=%s HOSTNAME=%s MGMT_URL=%s", RECORDER_ID, RECORDER_HOSTNAME, MANAGEMENT_SERVER_URL)
        logger.info("Config: SCREEN=%sx%s FRAMERATE=%s AUDIO_SOURCE=%s", SCREEN_WIDTH, SCREEN_HEIGHT, FRAMERATE, AUDIO_SOURCE_NAME)
        logger.info("Config: OUTPUT_DIR=%s PROFILES_BASE=%s", OUTPUT_DIR, CHROME_PROFILES_BASE_DIR)
    except Exception:
        pass

log_effective_config()

# Global state
current_job = None
browser_process = None
ffmpeg_process = None
vnc_process = None
parec_process = None
flask_app = None
flask_thread = None
running = True
controller_vnc = {}
controller_sessions = {}
browser_sessions = {}  # Named browser sessions independent of controllers

# Flask app for serving live HLS stream
stream_app = Flask(__name__)
LIVE_CLIENTS = {}
LIVE_START_TIME = None

def _client_ip():
    try:
        fwd = request.headers.get('X-Forwarded-For')
        ip = (fwd.split(',')[0].strip() if fwd else request.remote_addr) or ''
        return ip
    except Exception:
        return ''

def _track_live_client():
    try:
        ip = _client_ip()
        if ip:
            LIVE_CLIENTS[ip] = datetime.utcnow()
    except Exception:
        pass

def get_local_ip():
    """Best-effort detection of this machine's primary IPv4 address."""
    ip = '127.0.0.1'
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(0.2)
        try:
            # Doesn't need to be reachable; we just use the routing decision
            s.connect(('8.8.8.8', 80))
            ip = s.getsockname()[0]
        finally:
            s.close()
    except Exception:
        try:
            ip = socket.gethostbyname(socket.gethostname())
        except Exception:
            pass
    return ip

@stream_app.route('/live_hls/<path:filename>')
def serve_hls(filename):
    """Serve HLS segments for live streaming."""
    try:
        _track_live_client()
        return send_from_directory(HLS_DIR, filename)
    except FileNotFoundError:
        return "", 404

@stream_app.route('/api/live_ready')
def live_ready():
    """Report if HLS playlist exists and has any segments ready."""
    try:
        playlist = HLS_DIR / 'stream.m3u8'
        if not playlist.exists():
            return jsonify({'ready': False, 'segments': 0, 'start_time': LIVE_START_TIME})
        text = playlist.read_text(errors='ignore')
        # Count EXTINF occurrences as segments
        segs = text.count('#EXTINF')
        return jsonify({'ready': segs > 0, 'segments': segs, 'start_time': LIVE_START_TIME})
    except Exception:
        return jsonify({'ready': False, 'segments': 0, 'start_time': LIVE_START_TIME})

@stream_app.route('/api/live_clients')
def live_clients():
    try:
        now = datetime.utcnow()
        clients = []
        for ip, ts in list(LIVE_CLIENTS.items()):
            try:
                active = (now - ts).total_seconds() <= 10
                clients.append({'ip': ip, 'active': active, 'last_seen': ts.strftime('%Y-%m-%d %H:%M:%S')})
            except Exception:
                pass
        return jsonify({'clients': clients})
    except Exception:
        return jsonify({'clients': []})

@stream_app.after_request
def add_cors_headers(resp):
    try:
        resp.headers['Access-Control-Allow-Origin'] = '*'
        resp.headers['Access-Control-Allow-Headers'] = 'Origin, Range, Content-Type, Accept'
        resp.headers['Access-Control-Allow-Methods'] = 'GET, OPTIONS'
    except Exception:
        pass
    return resp

@stream_app.route('/status')
def recorder_status():
    """Recorder status endpoint."""
    return {
        'recorder_id': RECORDER_ID,
        'status': 'RECORDING' if current_job else 'IDLE',
        'current_job': current_job['id'] if current_job else None
    }

@stream_app.route('/live')
def live_player():
    """Serve the HLS live player page."""
    return render_template('live_player.html')

@stream_app.route('/control')
def control_page():
    """Render controller VNC control page."""
    return render_template('control.html')

@stream_app.route('/api/config', methods=['GET'])
def get_config():
    """Get current recorder configuration (recording, video, audio sections)."""
    try:
        full_config = config.get_full_config()
        return jsonify({
            'status': 'success',
            'recording': full_config['recording'],
            'video': full_config['video'],
            'audio': full_config['audio'],
        })
    except Exception as e:
        logger.error(f"Failed to get config: {e}")
        return jsonify({'status': 'error', 'message': str(e)}), 500

@stream_app.route('/api/config', methods=['POST'])
def update_config():
    """Update recorder configuration and save to config.toml."""
    global SCREEN_WIDTH, SCREEN_HEIGHT, FRAMERATE, AUDIO_SOURCE_NAME
    global VIDEO_PRESET, VIDEO_CRF, VIDEO_MAXRATE_K, VIDEO_BUFSIZE_K
    global VIDEO_THREADS, VIDEO_PIX_FMT, VIDEO_PROFILE, GOP_MULT, HLS_TIME
    global VIDEO_HW_ACCEL
    global AUDIO_BITRATE_K, AUDIO_SAMPLE_RATE, AUDIO_CHANNELS
    
    try:
        payload = request.get_json(silent=True) or {}
        full_config = config.get_full_config()
        
        # Update recording section
        if 'recording' in payload:
            rec = payload['recording']
            for key in ['screen_width', 'screen_height', 'framerate', 'audio_source_name']:
                if key in rec:
                    if key in ['screen_width', 'screen_height', 'framerate']:
                        full_config['recording'][key] = int(rec[key])
                    else:
                        full_config['recording'][key] = str(rec[key])
        
        # Update video section
        if 'video' in payload:
            vid = payload['video']
            for key in ['preset', 'crf', 'maxrate_kbps', 'bufsize_kbps', 'threads', 
                        'pix_fmt', 'profile', 'gop_multiplier', 'hls_time', 'hw_accel']:
                if key in vid:
                    if key in ['crf', 'maxrate_kbps', 'bufsize_kbps', 'threads', 'gop_multiplier', 'hls_time']:
                        full_config['video'][key] = int(vid[key])
                    else:
                        full_config['video'][key] = str(vid[key])
        
        # Update audio section
        if 'audio' in payload:
            aud = payload['audio']
            for key in ['bitrate_kbps', 'sample_rate', 'channels']:
                if key in aud:
                    full_config['audio'][key] = int(aud[key])
        
        # Save to disk
        config.save_config(full_config)
        
        # Reload config module
        config.reload_config()
        
        # Update module-level globals for upcoming recordings
        SCREEN_WIDTH = config.recording["screen_width"]
        SCREEN_HEIGHT = config.recording["screen_height"]
        FRAMERATE = config.recording["framerate"]
        AUDIO_SOURCE_NAME = config.recording["audio_source_name"]
        
        VIDEO_PRESET = config.video["preset"]
        VIDEO_CRF = config.video["crf"]
        VIDEO_MAXRATE_K = str(config.video["maxrate_kbps"])
        VIDEO_BUFSIZE_K = str(config.video["bufsize_kbps"])
        VIDEO_THREADS = str(config.video["threads"])
        VIDEO_PIX_FMT = config.video["pix_fmt"]
        VIDEO_PROFILE = config.video["profile"]
        GOP_MULT = config.video["gop_multiplier"]
        HLS_TIME = str(config.video["hls_time"])
        VIDEO_HW_ACCEL = config.video.get("hw_accel", "none")
        
        AUDIO_BITRATE_K = str(config.audio["bitrate_kbps"])
        AUDIO_SAMPLE_RATE = str(config.audio["sample_rate"])
        AUDIO_CHANNELS = str(config.audio["channels"])
        
        logger.info("Config updated and saved to disk")
        
        return jsonify({
            'status': 'success',
            'recording': config.recording,
            'video': config.video,
            'audio': config.audio,
        })
    except ImportError as e:
        logger.error(f"Failed to save config (missing tomli-w): {e}")
        return jsonify({'status': 'error', 'message': 'tomli-w package required for saving config'}), 500
    except Exception as e:
        logger.error(f"Failed to update config: {e}")
        return jsonify({'status': 'error', 'message': str(e)}), 500

# Test recording state
test_recording_active = False
test_recording_controller = None
test_recording_start_time = None

@stream_app.route('/api/test_recording/status')
def test_recording_status():
    """Get status of test recording."""
    global test_recording_active, test_recording_controller, test_recording_start_time
    
    is_running = False
    if test_recording_active and ffmpeg_process:
        is_running = ffmpeg_process.poll() is None
        if not is_running:
            # FFmpeg stopped unexpectedly
            test_recording_active = False
    
    elapsed = 0
    if test_recording_active and test_recording_start_time:
        elapsed = int(time.time() - test_recording_start_time)
    
    return jsonify({
        'active': test_recording_active and is_running,
        'controller': test_recording_controller,
        'elapsed_seconds': elapsed,
        'ffmpeg_running': ffmpeg_process is not None and ffmpeg_process.poll() is None if ffmpeg_process else False,
    })

@stream_app.route('/api/test_recording/start', methods=['POST'])
def test_recording_start():
    """Start a test recording using an existing session's display."""
    global test_recording_active, test_recording_controller, test_recording_start_time
    global LIVE_START_TIME
    
    if test_recording_active:
        return jsonify({'status': 'error', 'message': 'Test recording already active'}), 400
    
    if current_job:
        return jsonify({'status': 'error', 'message': 'A real recording job is in progress'}), 400
    
    payload = request.get_json(silent=True) or {}
    session_name = payload.get('controller')  # UI sends session name as 'controller' for compatibility
    
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session required'}), 400
    
    # Check browser_sessions first (new API), then fall back to controller_sessions (legacy)
    sess = browser_sessions.get(session_name)
    if not sess or not sess.get('display'):
        sess = controller_sessions.get(session_name)
    if not sess or not sess.get('display'):
        return jsonify({'status': 'error', 'message': f'No active session "{session_name}". Create a browser session first.'}), 400
    
    display = sess.get('display')
    
    try:
        # Set DISPLAY env for FFmpeg
        os.environ['DISPLAY'] = display
        
        # Clear old HLS files
        if HLS_DIR.exists():
            import shutil
            shutil.rmtree(HLS_DIR, ignore_errors=True)
        HLS_DIR.mkdir(exist_ok=True)
        
        # Start FFmpeg recording
        result = start_ffmpeg_recording(display)
        if not result:
            return jsonify({'status': 'error', 'message': 'Failed to start FFmpeg recording'}), 500
        
        test_recording_active = True
        test_recording_controller = controller
        test_recording_start_time = time.time()
        LIVE_START_TIME = datetime.now(timezone.utc).isoformat()
        
        logger.info(f"Test recording started on display {display} for controller {controller}")
        
        return jsonify({
            'status': 'success',
            'message': 'Test recording started',
            'display': display,
            'controller': controller,
        })
    except Exception as e:
        logger.error(f"Failed to start test recording: {e}")
        return jsonify({'status': 'error', 'message': str(e)}), 500

@stream_app.route('/api/test_recording/stop', methods=['POST'])
def test_recording_stop():
    """Stop the test recording and optionally save to MP4."""
    global test_recording_active, test_recording_controller, test_recording_start_time
    global ffmpeg_process, parec_process
    
    if not test_recording_active:
        return jsonify({'status': 'error', 'message': 'No test recording active'}), 400
    
    payload = request.get_json(silent=True) or {}
    save_mp4 = payload.get('save_mp4', False)
    
    try:
        # Stop FFmpeg gracefully
        if ffmpeg_process and ffmpeg_process.poll() is None:
            ffmpeg_process.send_signal(signal.SIGINT)
            try:
                ffmpeg_process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                ffmpeg_process.kill()
        
        # Stop parec if running
        if parec_process and parec_process.poll() is None:
            try:
                parec_process.terminate()
                parec_process.wait(timeout=5)
            except Exception:
                pass
        
        elapsed = int(time.time() - test_recording_start_time) if test_recording_start_time else 0
        controller = test_recording_controller
        
        output_file = None
        if save_mp4:
            # Convert HLS to MP4
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            filename = f"test_recording_{timestamp}.mp4"
            output_path = convert_hls_to_mp4(filename, job_id=None)
            if output_path:
                output_file = filename
                logger.info(f"Test recording saved to: {output_path}")
        
        # Reset state
        test_recording_active = False
        test_recording_controller = None
        test_recording_start_time = None
        
        logger.info(f"Test recording stopped after {elapsed}s")
        
        return jsonify({
            'status': 'success',
            'message': 'Test recording stopped',
            'elapsed_seconds': elapsed,
            'controller': controller,
            'saved_file': output_file,
        })
    except Exception as e:
        logger.error(f"Failed to stop test recording: {e}")
        # Reset state anyway
        test_recording_active = False
        test_recording_controller = None
        test_recording_start_time = None
        return jsonify({'status': 'error', 'message': str(e)}), 500

def list_controllers():
    ctrls = []
    try:
        for p in BROWSER_CONTROLLERS_DIR.glob('*.py'):
            name = p.stem
            if name.startswith('_'):
                continue
            ctrls.append(name)
    except Exception:
        pass
    return sorted(ctrls)

@stream_app.route('/api/vnc/status')
def vnc_status():
    data = {}
    for name in list_controllers():
        mode = 'none'
        display = None
        vnc_running = False
        error = False
        error_msg = None
        # Prefer recording context
        info_rec = controller_vnc.get(name)
        if info_rec:
            display = info_rec.get('display')
            try:
                vnc_running = info_rec.get('process') is not None and info_rec.get('process').poll() is None
            except Exception:
                vnc_running = False
            mode = 'recording'
        # If no recording display, check session
        if not display:
            sess = controller_sessions.get(name)
            if sess:
                display = sess.get('display')
                try:
                    vnc_running = sess.get('vnc_proc') is not None and sess.get('vnc_proc').poll() is None
                except Exception:
                    vnc_running = False
                mode = 'session'
                # Check error flag
                try:
                    eflag = sess.get('error_flag')
                    if eflag and Path(eflag).exists():
                        error = True
                        try:
                            error_msg = (Path(eflag).read_text() or '').strip()[:500]
                        except Exception:
                            error_msg = 'Session error'
                except Exception:
                    pass
        # Prefer session port if available
        vnc_port = 5900
        try:
            sess = controller_sessions.get(name)
            if sess and sess.get('vnc_port'):
                vnc_port = int(sess.get('vnc_port'))
        except Exception:
            pass
        data[name] = {'display': display, 'mode': mode, 'vnc_running': vnc_running, 'error': error, 'error_message': error_msg, 'vnc_port': vnc_port}
    return jsonify({'controllers': data})

@stream_app.route('/api/vnc/start', methods=['POST'])
def vnc_start():
    payload = request.get_json(silent=True) or {}
    name = payload.get('controller')
    if not name:
        return jsonify({'status': 'error', 'message': 'controller required'}), 400
    # Try recording context first
    info = controller_vnc.get(name)
    display = None
    if info:
        proc = info.get('process')
        display = info.get('display')
        if proc and proc.poll() is None:
            return jsonify({'status': 'success', 'message': 'already running', 'display': display})
        v = start_vnc_server(display)
        controller_vnc[name] = {'display': display, 'process': v}
        ok = v is not None and v.poll() is None
        return jsonify({'status': 'success' if ok else 'error', 'display': display})
    # Next, try session context
    sess = controller_sessions.get(name)
    if not sess:
        return jsonify({'status': 'error', 'message': 'No active recording or session. Launch a session first.'}), 400
    display = sess.get('display')
    vnc_proc = sess.get('vnc_proc')
    if vnc_proc and vnc_proc.poll() is None:
        return jsonify({'status': 'success', 'message': 'already running', 'display': display})
    # Use existing session port or find a new free one
    vnc_port = sess.get('vnc_port') or find_free_port(5900, 5999)
    v = start_vnc_server(display, port=vnc_port)
    sess['vnc_proc'] = v
    sess['vnc_port'] = vnc_port
    ok = v is not None and v.poll() is None
    return jsonify({'status': 'success' if ok else 'error', 'display': display, 'port': vnc_port})

@stream_app.route('/api/vnc/stop', methods=['POST'])
def vnc_stop():
    payload = request.get_json(silent=True) or {}
    name = payload.get('controller')
    if not name:
        return jsonify({'status': 'error', 'message': 'controller required'}), 400
    # Try recording VNC
    info = controller_vnc.get(name)
    if info:
        proc = info.get('process')
        if proc and proc.poll() is None:
            try:
                proc.terminate()
                proc.wait(timeout=5)
            except Exception:
                pass
        controller_vnc[name]['process'] = None
        return jsonify({'status': 'success'})
    # Try session VNC
    sess = controller_sessions.get(name)
    if not sess:
        return jsonify({'status': 'success', 'message': 'not running'})
    vnc_proc = sess.get('vnc_proc')
    if vnc_proc and vnc_proc.poll() is None:
        try:
            vnc_proc.terminate()
            vnc_proc.wait(timeout=5)
        except Exception:
            pass
    sess['vnc_proc'] = None
    return jsonify({'status': 'success'})

@stream_app.route('/api/session/launch', methods=['POST'])
def session_launch():
    payload = request.get_json(silent=True) or {}
    controller = payload.get('controller')
    url = payload.get('url') or 'https://www.google.com/'
    mode = (payload.get('mode') or 'manual').lower()
    keep_open_on_error = bool(payload.get('keep_open_on_error'))
    # Normalize URL
    try:
        if url and not (url.startswith('http://') or url.startswith('https://')):
            url = 'https://' + url
    except Exception:
        pass
    if not controller:
        return jsonify({'status': 'error', 'message': 'controller required'}), 400
    if controller in controller_sessions:
        sess = controller_sessions[controller]
        # If session exists and display alive, just ensure VNC running
        disp = sess.get('display')
        vnc_proc = sess.get('vnc_proc')
        if not vnc_proc or vnc_proc.poll() is not None:
            vnc_proc = start_vnc_server(disp)
            sess['vnc_proc'] = vnc_proc
        return jsonify({'status': 'success', 'display': disp})

    # Start new session: Xvfb + Browser + VNC
    try:
        from pyvirtualdisplay import Display
        display = Display(size=(SCREEN_WIDTH, SCREEN_HEIGHT), use_xauth=False)
        display.start()
        display_name = f":{display.display}"
        # Ensure this process (and children) have DISPLAY set for any utilities that may inherit
        os.environ['DISPLAY'] = display_name
        # Build environment similar to start_browser
        xauth_file = Path.home() / '.Xauthority'
        if not xauth_file.exists():
            try:
                xauth_file.touch(mode=0o600)
            except Exception:
                pass
        # Create wrapper for the controller session
        if mode == 'automated':
            wrapper_script = f"""
import os, sys, logging
print('[session] automated controller starting', flush=True)
print('[session] DISPLAY=' + str(os.environ.get('DISPLAY')), flush=True)
print('[session] CHROME_USER_DATA_DIR=' + str(os.environ.get('CHROME_USER_DATA_DIR')), flush=True)
print('[session] CHROME_PROFILE_DIR=' + str(os.environ.get('CHROME_PROFILE_DIR')), flush=True)
# Route Python logs (incl. selenium) to stdout at DEBUG
root = logging.getLogger()
root.setLevel(logging.DEBUG)
_h = logging.StreamHandler(sys.stdout)
_h.setLevel(logging.DEBUG)
root.addHandler(_h)
sys.path.insert(0, '{BROWSER_CONTROLLERS_DIR}')

import {controller}

print('[session] invoking controller.run_browser_session target_url=' + {url!r}, flush=True)
{controller}.run_browser_session(
    target_url={url!r},
    screen_width={SCREEN_WIDTH},
    screen_height={SCREEN_HEIGHT},
    ready_flag_path='{(TEMP_DIR / (controller + '_session_ready.flag'))}'
)
"""
        else:
            # Manual session: launch bare Chrome via Selenium and keep it running
            wrapper_script = f"""
import os, sys, time, logging
from selenium import webdriver
from selenium.webdriver.chrome.options import Options
try:
    from selenium.webdriver.chrome.service import Service as ChromeService
except Exception:
    ChromeService = None

display_env = os.environ.get('DISPLAY')
user_data_dir = os.environ.get('CHROME_USER_DATA_DIR') or ''
profile_dir = os.environ.get('CHROME_PROFILE_DIR') or 'Default'
cd_log = os.environ.get('CHROMEDRIVER_VERBOSE_LOG_PATH') or ''
opts = Options()
opts.add_argument(f"--window-size={SCREEN_WIDTH},{SCREEN_HEIGHT}")
opts.add_argument("--window-position=0,0")
opts.add_argument("--disable-gpu")
opts.add_argument("--no-sandbox")
opts.add_argument("--disable-dev-shm-usage")
opts.add_argument("--no-default-browser-check")
opts.add_argument("--no-first-run")
if display_env:
    opts.add_argument("--display=" + str(display_env))
opts.add_argument("--autoplay-policy=no-user-gesture-required")
if user_data_dir:
    opts.add_argument("--user-data-dir=" + user_data_dir)
if profile_dir:
    opts.add_argument("--profile-directory=" + profile_dir)
# Increase Chrome logging where supported
opts.add_argument("--enable-logging=stderr")
opts.add_argument("--v=1")

print('[session] manual Chrome starting', flush=True)
print('[session] DISPLAY=' + str(display_env), flush=True)
print('[session] CHROME_USER_DATA_DIR=' + user_data_dir, flush=True)
print('[session] CHROME_PROFILE_DIR=' + profile_dir, flush=True)
# Route Python logs (incl. selenium) to stdout at DEBUG
root = logging.getLogger()
root.setLevel(logging.DEBUG)
_h = logging.StreamHandler(sys.stdout)
_h.setLevel(logging.DEBUG)
root.addHandler(_h)

if ChromeService is not None:
    svc = None
    try:
        # Selenium 4.11+: pass log_output to stdout so logs are tee'd
        svc = ChromeService(log_output=sys.stdout)
    except TypeError:
        try:
            # Older Selenium: pass log_path
            svc = ChromeService(log_path=cd_log or None)
        except Exception:
            svc = ChromeService()
    driver = webdriver.Chrome(options=opts, service=svc)
else:
    driver = webdriver.Chrome(options=opts)
driver.get({url!r})
# Touch a ready flag for visibility
try:
    with open({str((TEMP_DIR / (controller + '_session_ready.flag')))!r}, 'w') as f:
        f.write('READY')
except Exception:
    pass
while True:
    time.sleep(1)
"""
        wrapper_path = TEMP_DIR / f'session_{controller}.py'
        with open(wrapper_path, 'w') as f:
            f.write(wrapper_script)

        # Python executable
        venv_python = BASE_DIR / 'venv' / 'bin' / 'python3'
        python_executable = str(venv_python) if venv_python.exists() else sys.executable

        # Env with per-controller Chrome profile and DISPLAY
        browser_env = os.environ.copy()
        browser_env['DISPLAY'] = display_name
        try:
            profiles_base = Path(CHROME_PROFILES_BASE_DIR)
            profiles_base.mkdir(parents=True, exist_ok=True)
            controller_profile = profiles_base / controller
            controller_profile.mkdir(parents=True, exist_ok=True)
            browser_env['CHROME_USER_DATA_DIR'] = str(controller_profile)
            browser_env['CHROME_PROFILE_DIR'] = browser_env.get('CHROME_PROFILE_DIR', 'Default')
        except Exception:
            pass
        # Keep-open flag propagated to controller
        browser_env['KEEP_OPEN_ON_ERROR'] = '1' if keep_open_on_error else '0'
        # Error flag path for controller to write
        error_flag_path = str(TEMP_DIR / f'{controller}_session_error.flag')
        try:
            efp = Path(error_flag_path)
            if efp.exists():
                efp.unlink()
        except Exception:
            pass
        browser_env['SESSION_ERROR_FLAG'] = error_flag_path

        # Enhance logs: also capture Chrome/ChromeDriver logs for automated sessions if controller uses Selenium
        try:
            browser_env['CHROMEDRIVER_VERBOSE_LOG_PATH'] = str(TEMP_DIR / f'{controller}_chromedriver.log')
            browser_env['CHROME_LOG_FILE'] = str(TEMP_DIR / f'{controller}_chrome.log')
        except Exception:
            pass

        # Launch controller browser and tee its stdout to both file and journal
        log_path = TEMP_DIR / f'session_{controller}.log'
        log_fp = open(log_path, 'a', buffering=1, encoding='utf-8')
        bproc = subprocess.Popen(
            [python_executable, str(wrapper_path)],
            env=browser_env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            preexec_fn=os.setsid,
            text=True
        )

        def _tee_stream(proc, out_file):
            try:
                for line in iter(proc.stdout.readline, ''):
                    try:
                        out_file.write(line)
                    except Exception:
                        pass
                    try:
                        logger.info(line.rstrip())
                    except Exception:
                        pass
            finally:
                try:
                    out_file.close()
                except Exception:
                    pass

        threading.Thread(target=_tee_stream, args=(bproc, log_fp), daemon=True).start()

        # Give Chrome a moment to draw its first frame before starting VNC
        time.sleep(1)
        # Pick a free VNC port for this session
        vnc_port = find_free_port(5900, 5999)
        # Start VNC on the selected port
        vnc_proc = start_vnc_server(display_name, port=vnc_port)
        try:
            safe_disp = display_name.replace(':', '').replace('/', '_')
            vnc_log_hint = str(TEMP_DIR / f"x11vnc_{safe_disp}.log")
            with open(log_path, 'a', encoding='utf-8') as _lf:
                _lf.write(f"[recorder] VNC started display={display_name} port={vnc_port} log={vnc_log_hint}\n")
        except Exception:
            pass
        logger.info(f"Session launched for controller '{controller}' on display {display_name}; VNC port {vnc_port}; URL={url}")

        controller_sessions[controller] = {
            'display': display_name,
            'display_obj': display,
            'browser_proc': bproc,
            'vnc_proc': vnc_proc,
            'vnc_port': vnc_port,
            'ready_flag': str(TEMP_DIR / f'{controller}_session_ready.flag'),
            'error_flag': error_flag_path
        }

        return jsonify({'status': 'success', 'display': display_name})
    except Exception as e:
        logger.error(f"Failed to launch session for {controller}: {e}")
        return jsonify({'status': 'error', 'message': str(e)}), 500

@stream_app.route('/api/session/stop', methods=['POST'])
def session_stop():
    payload = request.get_json(silent=True) or {}
    controller = payload.get('controller')
    if not controller:
        return jsonify({'status': 'error', 'message': 'controller required'}), 400
    sess = controller_sessions.get(controller)
    if not sess:
        return jsonify({'status': 'success'})
    # Stop VNC
    vnc_proc = sess.get('vnc_proc')
    if vnc_proc and vnc_proc.poll() is None:
        try:
            vnc_proc.terminate()
            vnc_proc.wait(timeout=5)
        except Exception:
            pass
    # Stop browser
    bproc = sess.get('browser_proc')
    if bproc and bproc.poll() is None:
        try:
            os.killpg(os.getpgid(bproc.pid), signal.SIGTERM)
            bproc.wait(timeout=5)
        except Exception:
            pass
    # Stop display
    try:
        disp = sess.get('display_obj')
        if disp:
            disp.stop()
    except Exception:
        pass
    # Cleanup
    try:
        rf = sess.get('ready_flag')
        if rf and Path(rf).exists():
            Path(rf).unlink()
    except Exception:
        pass
    try:
        ef = sess.get('error_flag')
        if ef and Path(ef).exists():
            Path(ef).unlink()
    except Exception:
        pass
    controller_sessions.pop(controller, None)
    return jsonify({'status': 'success'})

# ============================================================================
# Browser Sessions API - Named sessions independent of controllers
# ============================================================================

@stream_app.route('/api/browser_sessions/list')
def browser_sessions_list():
    """List all browser sessions with their status."""
    sessions = {}
    for name, sess in browser_sessions.items():
        vnc_running = False
        vnc_proc = sess.get('vnc_proc')
        if vnc_proc:
            try:
                vnc_running = vnc_proc.poll() is None
            except Exception:
                pass
        browser_running = False
        browser_proc = sess.get('browser_proc')
        if browser_proc:
            try:
                browser_running = browser_proc.poll() is None
            except Exception:
                pass
        sessions[name] = {
            'display': sess.get('display'),
            'vnc_running': vnc_running,
            'vnc_port': sess.get('vnc_port'),
            'browser_running': browser_running,
            'controller': sess.get('controller'),  # If a controller is using this session
        }
    return jsonify({'sessions': sessions})

@stream_app.route('/api/browser_sessions/create', methods=['POST'])
def browser_sessions_create():
    """Create a new named browser session with Xvfb and Chrome."""
    payload = request.get_json(silent=True) or {}
    session_name = payload.get('name')
    url = payload.get('url') or 'https://www.google.com/'
    
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session name required'}), 400
    
    # Normalize URL
    try:
        if url and not (url.startswith('http://') or url.startswith('https://')):
            url = 'https://' + url
    except Exception:
        pass
    
    if session_name in browser_sessions:
        sess = browser_sessions[session_name]
        # Check if session is still alive
        browser_proc = sess.get('browser_proc')
        if browser_proc and browser_proc.poll() is None:
            return jsonify({
                'status': 'success',
                'message': 'session already exists',
                'display': sess.get('display'),
                'vnc_port': sess.get('vnc_port')
            })
        # Session died, clean it up
        _cleanup_browser_session(session_name)
    
    try:
        from pyvirtualdisplay import Display
        display = Display(size=(SCREEN_WIDTH, SCREEN_HEIGHT), use_xauth=False)
        display.start()
        display_name = f":{display.display}"
        os.environ['DISPLAY'] = display_name
        
        # Create manual browser session script
        wrapper_script = f"""
import os, sys, time, logging
from selenium import webdriver
from selenium.webdriver.chrome.options import Options
try:
    from selenium.webdriver.chrome.service import Service as ChromeService
except Exception:
    ChromeService = None

display_env = os.environ.get('DISPLAY')
user_data_dir = os.environ.get('CHROME_USER_DATA_DIR') or ''
profile_dir = os.environ.get('CHROME_PROFILE_DIR') or 'Default'

opts = Options()
opts.add_argument(f"--window-size={SCREEN_WIDTH},{SCREEN_HEIGHT}")
opts.add_argument("--window-position=0,0")
opts.add_argument("--disable-gpu")
opts.add_argument("--no-sandbox")
opts.add_argument("--disable-dev-shm-usage")
opts.add_argument("--no-default-browser-check")
opts.add_argument("--no-first-run")
if display_env:
    opts.add_argument("--display=" + str(display_env))
opts.add_argument("--autoplay-policy=no-user-gesture-required")
if user_data_dir:
    opts.add_argument("--user-data-dir=" + user_data_dir)
if profile_dir:
    opts.add_argument("--profile-directory=" + profile_dir)

print('[session] browser starting for session {session_name}', flush=True)
print('[session] DISPLAY=' + str(display_env), flush=True)

if ChromeService is not None:
    svc = ChromeService()
    driver = webdriver.Chrome(options=opts, service=svc)
else:
    driver = webdriver.Chrome(options=opts)
driver.get({url!r})

# Touch ready flag
try:
    with open({str(TEMP_DIR / f'session_{session_name}_ready.flag')!r}, 'w') as f:
        f.write('READY')
except Exception:
    pass

while True:
    time.sleep(1)
"""
        wrapper_path = TEMP_DIR / f'browser_session_{session_name}.py'
        with open(wrapper_path, 'w') as f:
            f.write(wrapper_script)
        
        # Python executable
        venv_python = BASE_DIR / 'venv' / 'bin' / 'python3'
        python_executable = str(venv_python) if venv_python.exists() else sys.executable
        
        # Environment with session-specific Chrome profile
        browser_env = os.environ.copy()
        browser_env['DISPLAY'] = display_name
        try:
            profiles_base = Path(CHROME_PROFILES_BASE_DIR)
            profiles_base.mkdir(parents=True, exist_ok=True)
            session_profile = profiles_base / f'session_{session_name}'
            session_profile.mkdir(parents=True, exist_ok=True)
            browser_env['CHROME_USER_DATA_DIR'] = str(session_profile)
            browser_env['CHROME_PROFILE_DIR'] = 'Default'
        except Exception:
            pass
        
        # Launch browser
        log_path = TEMP_DIR / f'browser_session_{session_name}.log'
        log_fp = open(log_path, 'a', buffering=1, encoding='utf-8')
        bproc = subprocess.Popen(
            [python_executable, str(wrapper_path)],
            env=browser_env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            preexec_fn=os.setsid,
            text=True
        )
        
        def _tee_stream(proc, out_file):
            try:
                for line in iter(proc.stdout.readline, ''):
                    try:
                        out_file.write(line)
                    except Exception:
                        pass
                    try:
                        logger.info(line.rstrip())
                    except Exception:
                        pass
            finally:
                try:
                    out_file.close()
                except Exception:
                    pass
        
        threading.Thread(target=_tee_stream, args=(bproc, log_fp), daemon=True).start()
        
        # Give Chrome a moment to start
        time.sleep(1)
        
        browser_sessions[session_name] = {
            'display': display_name,
            'display_obj': display,
            'browser_proc': bproc,
            'vnc_proc': None,
            'vnc_port': None,
            'controller': None,
        }
        
        logger.info(f"Browser session '{session_name}' created on display {display_name}")
        return jsonify({
            'status': 'success',
            'display': display_name,
            'session_name': session_name
        })
    except Exception as e:
        logger.error(f"Failed to create browser session '{session_name}': {e}")
        return jsonify({'status': 'error', 'message': str(e)}), 500

@stream_app.route('/api/browser_sessions/start_vnc', methods=['POST'])
def browser_sessions_start_vnc():
    """Start VNC server for a browser session."""
    payload = request.get_json(silent=True) or {}
    session_name = payload.get('name')
    
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session name required'}), 400
    
    sess = browser_sessions.get(session_name)
    if not sess:
        return jsonify({'status': 'error', 'message': f'session {session_name} not found'}), 404
    
    vnc_proc = sess.get('vnc_proc')
    if vnc_proc and vnc_proc.poll() is None:
        return jsonify({
            'status': 'success',
            'message': 'VNC already running',
            'port': sess.get('vnc_port')
        })
    
    display = sess.get('display')
    vnc_port = find_free_port(5900, 5999)
    vnc_proc = start_vnc_server(display, port=vnc_port)
    sess['vnc_proc'] = vnc_proc
    sess['vnc_port'] = vnc_port
    
    ok = vnc_proc is not None and vnc_proc.poll() is None
    logger.info(f"VNC started for session '{session_name}' on port {vnc_port}")
    return jsonify({
        'status': 'success' if ok else 'error',
        'port': vnc_port,
        'display': display
    })

@stream_app.route('/api/browser_sessions/stop_vnc', methods=['POST'])
def browser_sessions_stop_vnc():
    """Stop VNC server for a browser session."""
    payload = request.get_json(silent=True) or {}
    session_name = payload.get('name')
    
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session name required'}), 400
    
    sess = browser_sessions.get(session_name)
    if not sess:
        return jsonify({'status': 'success', 'message': 'session not found'})
    
    vnc_proc = sess.get('vnc_proc')
    if vnc_proc and vnc_proc.poll() is None:
        try:
            vnc_proc.terminate()
            vnc_proc.wait(timeout=5)
        except Exception:
            pass
    sess['vnc_proc'] = None
    sess['vnc_port'] = None
    
    logger.info(f"VNC stopped for session '{session_name}'")
    return jsonify({'status': 'success'})

@stream_app.route('/api/browser_sessions/delete', methods=['POST'])
def browser_sessions_delete():
    """Delete a browser session (stops browser and VNC)."""
    payload = request.get_json(silent=True) or {}
    session_name = payload.get('name')
    
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session name required'}), 400
    
    _cleanup_browser_session(session_name)
    return jsonify({'status': 'success'})

def _cleanup_browser_session(session_name):
    """Clean up a browser session."""
    sess = browser_sessions.get(session_name)
    if not sess:
        return
    
    # Stop VNC
    vnc_proc = sess.get('vnc_proc')
    if vnc_proc and vnc_proc.poll() is None:
        try:
            vnc_proc.terminate()
            vnc_proc.wait(timeout=5)
        except Exception:
            pass
    
    # Stop browser
    bproc = sess.get('browser_proc')
    if bproc and bproc.poll() is None:
        try:
            os.killpg(os.getpgid(bproc.pid), signal.SIGTERM)
            bproc.wait(timeout=5)
        except Exception:
            pass
    
    # Stop display
    try:
        disp = sess.get('display_obj')
        if disp:
            disp.stop()
    except Exception:
        pass
    
    browser_sessions.pop(session_name, None)
    logger.info(f"Browser session '{session_name}' cleaned up")

# ============================================================================
# Browser Controller API - Run automated controller on a session
# ============================================================================

@stream_app.route('/api/controller/run', methods=['POST'])
def controller_run():
    """Run an automated controller on an existing browser session."""
    payload = request.get_json(silent=True) or {}
    controller = payload.get('controller')
    session_name = payload.get('session')
    url = payload.get('url') or 'https://www.google.com/'
    keep_open_on_error = bool(payload.get('keep_open_on_error'))
    
    if not controller:
        return jsonify({'status': 'error', 'message': 'controller required'}), 400
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session required'}), 400
    
    sess = browser_sessions.get(session_name)
    if not sess:
        return jsonify({'status': 'error', 'message': f'session {session_name} not found'}), 404
    
    # Check if session already has a controller running
    if sess.get('controller'):
        return jsonify({'status': 'error', 'message': f'session already has controller {sess.get("controller")} running'}), 400
    
    # Normalize URL
    try:
        if url and not (url.startswith('http://') or url.startswith('https://')):
            url = 'https://' + url
    except Exception:
        pass
    
    display_name = sess.get('display')
    
    try:
        # Create automated controller script
        wrapper_script = f"""
import os, sys, logging
print('[controller] automated controller starting', flush=True)
print('[controller] DISPLAY=' + str(os.environ.get('DISPLAY')), flush=True)

root = logging.getLogger()
root.setLevel(logging.DEBUG)
_h = logging.StreamHandler(sys.stdout)
_h.setLevel(logging.DEBUG)
root.addHandler(_h)
sys.path.insert(0, '{BROWSER_CONTROLLERS_DIR}')

import {controller}

print('[controller] invoking controller.run_browser_session target_url=' + {url!r}, flush=True)
{controller}.run_browser_session(
    target_url={url!r},
    screen_width={SCREEN_WIDTH},
    screen_height={SCREEN_HEIGHT},
    ready_flag_path='{(TEMP_DIR / (controller + '_controller_ready.flag'))}'
)
"""
        wrapper_path = TEMP_DIR / f'controller_{controller}_{session_name}.py'
        with open(wrapper_path, 'w') as f:
            f.write(wrapper_script)
        
        # Python executable
        venv_python = BASE_DIR / 'venv' / 'bin' / 'python3'
        python_executable = str(venv_python) if venv_python.exists() else sys.executable
        
        # Environment
        ctrl_env = os.environ.copy()
        ctrl_env['DISPLAY'] = display_name
        ctrl_env['KEEP_OPEN_ON_ERROR'] = '1' if keep_open_on_error else '0'
        error_flag_path = str(TEMP_DIR / f'{controller}_{session_name}_error.flag')
        try:
            efp = Path(error_flag_path)
            if efp.exists():
                efp.unlink()
        except Exception:
            pass
        ctrl_env['SESSION_ERROR_FLAG'] = error_flag_path
        
        # Use session's Chrome profile
        try:
            profiles_base = Path(CHROME_PROFILES_BASE_DIR)
            session_profile = profiles_base / f'session_{session_name}'
            ctrl_env['CHROME_USER_DATA_DIR'] = str(session_profile)
            ctrl_env['CHROME_PROFILE_DIR'] = 'Default'
        except Exception:
            pass
        
        # Launch controller
        log_path = TEMP_DIR / f'controller_{controller}_{session_name}.log'
        log_fp = open(log_path, 'a', buffering=1, encoding='utf-8')
        cproc = subprocess.Popen(
            [python_executable, str(wrapper_path)],
            env=ctrl_env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            preexec_fn=os.setsid,
            text=True
        )
        
        def _tee_stream(proc, out_file):
            try:
                for line in iter(proc.stdout.readline, ''):
                    try:
                        out_file.write(line)
                    except Exception:
                        pass
                    try:
                        logger.info(line.rstrip())
                    except Exception:
                        pass
            finally:
                try:
                    out_file.close()
                except Exception:
                    pass
        
        threading.Thread(target=_tee_stream, args=(cproc, log_fp), daemon=True).start()
        
        # Start VNC if not already running
        vnc_proc = sess.get('vnc_proc')
        vnc_port = sess.get('vnc_port')
        if not vnc_proc or vnc_proc.poll() is not None:
            vnc_port = find_free_port(5900, 5999)
            vnc_proc = start_vnc_server(display_name, port=vnc_port)
            sess['vnc_proc'] = vnc_proc
            sess['vnc_port'] = vnc_port
        
        # Update session with controller info
        sess['controller'] = controller
        sess['controller_proc'] = cproc
        sess['error_flag'] = error_flag_path
        
        logger.info(f"Controller '{controller}' started on session '{session_name}', display {display_name}, VNC port {vnc_port}")
        return jsonify({
            'status': 'success',
            'display': display_name,
            'vnc_port': vnc_port,
            'controller': controller,
            'session': session_name
        })
    except Exception as e:
        logger.error(f"Failed to run controller '{controller}' on session '{session_name}': {e}")
        return jsonify({'status': 'error', 'message': str(e)}), 500

@stream_app.route('/api/controller/stop', methods=['POST'])
def controller_stop():
    """Stop a running controller on a session."""
    payload = request.get_json(silent=True) or {}
    session_name = payload.get('session')
    
    if not session_name:
        return jsonify({'status': 'error', 'message': 'session required'}), 400
    
    sess = browser_sessions.get(session_name)
    if not sess:
        return jsonify({'status': 'success', 'message': 'session not found'})
    
    controller = sess.get('controller')
    cproc = sess.get('controller_proc')
    
    if cproc and cproc.poll() is None:
        try:
            os.killpg(os.getpgid(cproc.pid), signal.SIGTERM)
            cproc.wait(timeout=5)
        except Exception:
            pass
    
    # Clean up error flag
    try:
        ef = sess.get('error_flag')
        if ef and Path(ef).exists():
            Path(ef).unlink()
    except Exception:
        pass
    
    sess['controller'] = None
    sess['controller_proc'] = None
    sess['error_flag'] = None
    
    logger.info(f"Controller '{controller}' stopped on session '{session_name}'")
    return jsonify({'status': 'success', 'controller': controller})

@stream_app.route('/recordings/<path:filename>')
def serve_recording(filename):
    """Serve completed MP4 recordings."""
    try:
        path = OUTPUT_DIR / filename
        if not path.exists() or not path.is_file():
            logger.warning(f"Recording not found: {filename}")
            return "Recording not found", 404
        file_size = path.stat().st_size
        range_header = request.headers.get('Range', None)
        if not range_header:
            # Full content
            resp = send_from_directory(OUTPUT_DIR, filename)
            try:
                resp.headers['Accept-Ranges'] = 'bytes'
            except Exception:
                pass
            return resp
        # Parse Range: bytes=start-end
        try:
            units, rng = range_header.split('=')
            if units.strip().lower() != 'bytes':
                raise ValueError('Unsupported units')
            start_str, end_str = (rng or '').split('-')
            start = int(start_str) if start_str else 0
            end = int(end_str) if end_str else file_size - 1
            start = max(0, start)
            end = min(end, file_size - 1)
            if start > end:
                start, end = 0, file_size - 1
        except Exception:
            start, end = 0, file_size - 1

        length = end - start + 1
        def generate():
            with open(path, 'rb') as f:
                f.seek(start)
                remaining = length
                chunk = 1024 * 1024
                while remaining > 0:
                    read_len = min(chunk, remaining)
                    data = f.read(read_len)
                    if not data:
                        break
                    remaining -= len(data)
                    yield data

        headers = {
            'Content-Range': f'bytes {start}-{end}/{file_size}',
            'Accept-Ranges': 'bytes',
            'Content-Length': str(length),
        }
        return stream_app.response_class(generate(), status=206, headers=headers, mimetype='video/mp4')
    except Exception as e:
        logger.error(f"Error serving recording {filename}: {e}")
        return "Error", 500

@stream_app.route('/api/recordings/<path:filename>/delete', methods=['POST'])
def delete_recording_file(filename):
    try:
        target = (OUTPUT_DIR / filename).resolve()
        base = OUTPUT_DIR.resolve()
        if not str(target).startswith(str(base) + os.sep):
            return jsonify({'status': 'error', 'message': 'Invalid path'}), 400
        if not target.exists() or not target.is_file():
            return jsonify({'status': 'error', 'message': 'Not found'}), 404
        try:
            target.unlink()
        except Exception as e:
            logger.error(f"Failed to delete {target}: {e}")
            return jsonify({'status': 'error', 'message': 'Delete failed'}), 500
        return jsonify({'status': 'success'})
    except Exception as e:
        logger.error(f"Delete error for {filename}: {e}")
        return jsonify({'status': 'error'}), 500

# ============================================================================
# Recorder Registration and Communication
# ============================================================================

def register_recorder():
    """Register this recorder with the management server."""
    try:
        response = requests.post(
            f"{MANAGEMENT_SERVER_URL}/api/recorder/register",
            json={
                'recorder_id': RECORDER_ID,
                'hostname': RECORDER_HOSTNAME,
                'ip_address': get_local_ip(),
                'capabilities': {
                    'screen_width': SCREEN_WIDTH,
                    'screen_height': SCREEN_HEIGHT,
                    'framerate': FRAMERATE,
                    'has_gpu': True  # Set based on actual hardware
                }
            },
            timeout=10
        )
        
        if response.status_code == 200:
            logger.info(f"Recorder registered successfully: {RECORDER_ID}")
            return True
        else:
            logger.error(f"Failed to register recorder: {response.status_code}")
            return False
            
    except Exception as e:
        logger.error(f"Error registering recorder: {e}")
        return False

def send_heartbeat(status='IDLE', current_job_id=None):
    """Send heartbeat to management server."""
    try:
        requests.post(
            f"{MANAGEMENT_SERVER_URL}/api/recorder/heartbeat",
            json={
                'recorder_id': RECORDER_ID,
                'status': status,
                'current_job_id': current_job_id
            },
            timeout=5
        )
    except Exception as e:
        logger.warning(f"Failed to send heartbeat: {e}")

def get_next_job():
    """Poll management server for next job."""
    try:
        response = requests.post(
            f"{MANAGEMENT_SERVER_URL}/api/recorder/get_job",
            json={'recorder_id': RECORDER_ID},
            timeout=10
        )
        
        if response.status_code == 200:
            data = response.json()
            if data['status'] == 'success':
                return data['job']
        
        return None
        
    except Exception as e:
        logger.warning(f"Error getting next job: {e}")
        return None

def update_job_status(job_id, status, error_message=None, conversion_progress=None, estimated_completion_time=None):
    """Update job status on management server."""
    try:
        payload = {
            'job_id': job_id,
            'status': status,
        }
        
        if conversion_progress is not None:
            payload['conversion_progress'] = conversion_progress
        if error_message:
            payload['error_message'] = error_message
        if estimated_completion_time is not None:
            payload['estimated_completion_time'] = estimated_completion_time

        requests.post(
            f"{MANAGEMENT_SERVER_URL}/api/recorder/update_job_status",
            json=payload,
            timeout=10
        )
        logger.info(f"Job {job_id} status updated to {status}")
    except Exception as e:
        logger.error(f"Failed to update job status: {e}")

def upload_recording_metadata(job_id, filename, file_size, duration, actual_start_time=None, actual_end_time=None):
    """Upload recording metadata to management server."""
    try:
        requests.post(
            f"{MANAGEMENT_SERVER_URL}/api/recorder/upload_recording",
            json={
                'job_id': job_id,
                'filename': filename,
                'file_size_bytes': file_size,
                'duration_seconds': duration,
                'actual_start_time': actual_start_time,
                'actual_end_time': actual_end_time
            },
            timeout=10
        )
        logger.info(f"Recording metadata uploaded for job {job_id}")
    except Exception as e:
        logger.error(f"Failed to upload recording metadata: {e}")

# ============================================================================
# Recording Logic
# ============================================================================

def start_browser(url, display, browser_controller='generic'):
    """Start browser controller process using specified controller.
    
    Args:
        url: Target URL to record
        display: X display name (e.g., ':99')
        browser_controller: Name of browser controller to use (videojs, youtube, generic)
    """
    global browser_process
    
    # Find the browser controller module
    controller_path = BROWSER_CONTROLLERS_DIR / f"{browser_controller}.py"
    
    if not controller_path.exists():
        logger.warning(f"Browser controller '{browser_controller}' not found, falling back to generic")
        controller_path = BROWSER_CONTROLLERS_DIR / "generic.py"
    
    if not controller_path.exists():
        logger.error("No browser controllers found!")
        return None
    
    logger.info(f"Using browser controller: {browser_controller}")
    
    # Create a wrapper script that imports and runs the controller
    wrapper_script = f"""
import sys
sys.path.insert(0, '{BROWSER_CONTROLLERS_DIR}')

import {browser_controller}

{browser_controller}.run_browser_session(
    target_url='{url}',
    screen_width={SCREEN_WIDTH},
    screen_height={SCREEN_HEIGHT},
    ready_flag_path='{READY_FLAG}'
)
"""
    
    wrapper_path = TEMP_DIR / 'browser_wrapper.py'
    with open(wrapper_path, 'w') as f:
        f.write(wrapper_script)
    
    # Start browser process using venv's Python
    venv_python = BASE_DIR / 'venv' / 'bin' / 'python3'
    python_executable = str(venv_python) if venv_python.exists() else sys.executable
    
    # Prepare environment with X authority
    # Create dummy .Xauthority file if it doesn't exist
    # pyautogui -> mouseinfo tries to read it at import time
    xauth_file = Path.home() / '.Xauthority'
    if not xauth_file.exists():
        xauth_file.touch(mode=0o600)
        logger.info(f"Created dummy .Xauthority file: {xauth_file}")
    
    browser_env = os.environ.copy()
    # Per-controller Chrome profile dir
    try:
        profiles_base = Path(CHROME_PROFILES_BASE_DIR)
        profiles_base.mkdir(parents=True, exist_ok=True)
        controller_profile = profiles_base / browser_controller
        controller_profile.mkdir(parents=True, exist_ok=True)
        browser_env['CHROME_USER_DATA_DIR'] = str(controller_profile)
        browser_env['CHROME_PROFILE_DIR'] = browser_env.get('CHROME_PROFILE_DIR', 'Default')
        logger.info(f"Using Chrome user data dir for controller '{browser_controller}': {controller_profile}")
    except Exception as e:
        logger.warning(f"Failed to ensure profile dir for {browser_controller}: {e}")
    
    try:
        with open(BROWSER_LOG, 'w') as log_file:
            browser_process = subprocess.Popen(
                [python_executable, str(wrapper_path)],
                env=browser_env,
                stdout=log_file,
                stderr=subprocess.STDOUT,
                preexec_fn=os.setsid
            )
        logger.info(f"Browser process started with PID: {browser_process.pid} using {browser_controller} controller")
        logger.info(f"Browser logs: {BROWSER_LOG}")
        return browser_process
    except Exception as e:
        logger.error(f"Failed to start browser: {e}")
        return None

def get_audio_source():
    """Get available audio source, auto-detecting if needed."""
    try:
        # Set up environment for pactl to work with PipeWire
        pactl_env = os.environ.copy()
        # Try to find PipeWire runtime directory
        if 'XDG_RUNTIME_DIR' not in pactl_env:
            pactl_env['XDG_RUNTIME_DIR'] = f'/run/user/{os.getuid()}'
        # Ensure we pass PULSE_SERVER through for pactl/ffmpeg
        if 'PULSE_SERVER' not in pactl_env:
            runtime_dir = pactl_env['XDG_RUNTIME_DIR']
            native = Path(runtime_dir) / 'pulse' / 'native'
            if native.exists():
                pactl_env['PULSE_SERVER'] = f'unix:{native}'
        
        # Try the configured source first
        result = subprocess.run(
            ['pactl', 'list', 'sources', 'short'],
            capture_output=True,
            text=True,
            timeout=5,
            env=pactl_env
        )
        
        if result.returncode != 0:
            logger.warning(f"pactl command failed with code {result.returncode}")
            logger.warning(f"pactl stderr: {result.stderr}")
            logger.warning(f"Using configured audio source: {AUDIO_SOURCE_NAME}")
            return AUDIO_SOURCE_NAME
        
        sources = result.stdout.strip().split('\n')
        logger.info(f"pactl stdout: {result.stdout}")
        logger.info(f"Available audio sources: {sources}")
        # Parse to names only
        source_names = []
        for line in sources:
            parts = line.split()
            if len(parts) >= 2:
                source_names.append(parts[1])

        # Prefer real monitor devices that are not auto_null and not loopbacks
        for name in source_names:
            lname = name.lower()
            if 'monitor' in lname and 'loopback' not in lname and 'auto_null' not in lname:
                logger.info(f"Found hardware monitor: {name}")
                return name

        # If only auto_null.monitor exists, try to create and select a virtual sink
        if any(n.endswith('.monitor') and 'auto_null' in n for n in source_names):
            # If virtsink already exists, just set as default and use it
            if 'virtsink.monitor' in source_names:
                logger.info("virtsink.monitor already present; selecting as default")
                pactl_env2 = pactl_env
                try:
                    subprocess.run(['pactl','set-default-source','virtsink.monitor'], env=pactl_env2, timeout=5)
                except Exception:
                    pass
                return 'default'
            logger.warning("Only auto_null.monitor found; attempting to create 'virtsink' and use its monitor")

            def run_pactl(args):
                return subprocess.run(['pactl'] + args, capture_output=True, text=True, timeout=5, env=pactl_env)

            # Load null sink (ignore error if already loaded)
            run_pactl(['load-module', 'module-null-sink', 'sink_name=virtsink', 'sink_properties=device.description=Virtual_Sink'])
            # Set as default sink so Chromium outputs to it
            run_pactl(['set-default-sink', 'virtsink'])
            # Move any current sink inputs to virtsink (best-effort)
            proc_inputs = run_pactl(['list', 'short', 'sink-inputs'])
            if proc_inputs.returncode == 0:
                for line in proc_inputs.stdout.strip().split('\n'):
                    parts = line.split() if line else []
                    if parts:
                        input_id = parts[0]
                        run_pactl(['move-sink-input', input_id, 'virtsink'])
            # Re-list sources to find virtsink.monitor
            recheck = run_pactl(['list', 'sources', 'short'])
            if recheck.returncode == 0:
                for line in recheck.stdout.strip().split('\n'):
                    parts = line.split()
                    if len(parts) >= 2 and parts[1] == 'virtsink.monitor':
                        # Set virtsink.monitor as default source so 'default' resolves correctly
                        run_pactl(['set-default-source', 'virtsink.monitor'])
                        logger.info("Using virtsink.monitor as audio source (default)")
                        return 'default'
            # If still not found, fall back to silent
            logger.warning("virtsink.monitor not found after creation; falling back to silent audio")
            return None

        # If no monitor found, use first available source if present
        if source_names:
            source_name = source_names[0]
            logger.warning(f"No monitor source found, using first available: {source_name}")
            return source_name

        # Last resort fallback
        logger.error(f"No audio sources found, using configured value: {AUDIO_SOURCE_NAME}")
        return AUDIO_SOURCE_NAME
        
    except Exception as e:
        logger.warning(f"Failed to detect audio source: {e}, using configured value")
        return AUDIO_SOURCE_NAME

def _get_video_encoder_opts():
    """Build video encoder options based on hardware acceleration setting."""
    if VIDEO_HW_ACCEL == 'vaapi':
        return [
            '-vaapi_device', '/dev/dri/renderD128',
            '-vf', 'format=nv12,hwupload',
            '-c:v', 'h264_vaapi',
            '-qp', str(VIDEO_CRF),
            '-g', str(FRAMERATE * GOP_MULT),
            '-maxrate', f'{VIDEO_MAXRATE_K}k',
            '-bufsize', f'{VIDEO_BUFSIZE_K}k',
        ]
    elif VIDEO_HW_ACCEL == 'nvenc':
        return [
            '-c:v', 'h264_nvenc',
            '-pix_fmt', VIDEO_PIX_FMT,
            '-preset', 'p4',
            '-rc', 'vbr',
            '-cq', str(VIDEO_CRF),
            '-g', str(FRAMERATE * GOP_MULT),
            '-maxrate', f'{VIDEO_MAXRATE_K}k',
            '-bufsize', f'{VIDEO_BUFSIZE_K}k',
        ]
    elif VIDEO_HW_ACCEL == 'qsv':
        return [
            '-c:v', 'h264_qsv',
            '-pix_fmt', VIDEO_PIX_FMT,
            '-preset', VIDEO_PRESET,
            '-global_quality', str(VIDEO_CRF),
            '-g', str(FRAMERATE * GOP_MULT),
            '-maxrate', f'{VIDEO_MAXRATE_K}k',
            '-bufsize', f'{VIDEO_BUFSIZE_K}k',
        ]
    else:
        # Software encoding (libx264)
        return [
            '-c:v', 'libx264',
            '-pix_fmt', VIDEO_PIX_FMT,
            '-profile:v', VIDEO_PROFILE,
            '-preset', VIDEO_PRESET,
            '-crf', str(VIDEO_CRF),
            '-g', str(FRAMERATE * GOP_MULT),
            '-threads', VIDEO_THREADS,
            '-maxrate', f'{VIDEO_MAXRATE_K}k',
            '-bufsize', f'{VIDEO_BUFSIZE_K}k',
        ]

def start_ffmpeg_recording(display):
    """Start FFmpeg HLS recording."""
    global ffmpeg_process, parec_process
    
    HLS_DIR.mkdir(exist_ok=True)
    
    # Create FFmpeg log file
    ffmpeg_log = TEMP_DIR / 'ffmpeg.log'
    
    # Get the audio source dynamically
    audio_source = get_audio_source()

    # Build FFmpeg command with audio and video
    command = [
        'ffmpeg',
        '-y',

        # --- VIDEO INPUT ---
        '-f', 'x11grab',
        '-thread_queue_size', '1024',
        '-video_size', f'{SCREEN_WIDTH}x{SCREEN_HEIGHT}',
        '-framerate', str(FRAMERATE),
        '-i', display,
    ]

    used_pulse_audio = False
    if audio_source:
        # Pulse/pipewire audio source
        command += [
            '-f', 'pulse',
            '-thread_queue_size', '4096',
            '-i', audio_source,
        ]
        used_pulse_audio = True
    else:
        # Fallback to a silent audio source to ensure HLS playback continues
        command += [
            '-f', 'lavfi',
            '-i', 'anullsrc=r=48000:cl=stereo',
        ]

    # --- ENCODING AND OUTPUT ---
    video_enc_opts = _get_video_encoder_opts()
    command += ['-vsync', '2'] + video_enc_opts + [
        '-c:a', 'aac',
        '-ar', AUDIO_SAMPLE_RATE,
        '-b:a', f'{AUDIO_BITRATE_K}k',
        '-ac', AUDIO_CHANNELS,
        '-af', 'aresample=async=1:min_hard_comp=0.100000:first_pts=0',

        '-hls_time', HLS_TIME,
        '-hls_list_size', '0',
        '-hls_flags', 'independent_segments+append_list',
        '-hls_playlist_type', 'event',
        '-f', 'hls',
        str(HLS_DIR / 'stream.m3u8')
    ]
    
    logger.info(f"Recording with audio source: {audio_source}")
    
    try:
        # Log FFmpeg output to file for debugging
        with open(ffmpeg_log, 'w') as log_file:
            ffmpeg_process = subprocess.Popen(
                command,
                stdout=log_file,
                stderr=subprocess.STDOUT,
                env=os.environ.copy()
            )
        logger.info(f"FFmpeg command: {' '.join(command)}")
        logger.info(f"FFmpeg recording started with PID: {ffmpeg_process.pid}")
        logger.info(f"FFmpeg logs: {ffmpeg_log}")

        # Wait a moment and check if it's still running
        time.sleep(2)
        if ffmpeg_process.poll() is not None:
            # First attempt failed. If we tried PulseAudio, retry with lavfi anullsrc
            first_rc = ffmpeg_process.poll()
            logger.error(f"FFmpeg exited immediately with code: {first_rc}")
            if ffmpeg_log.exists():
                with open(ffmpeg_log, 'r') as f:
                    logger.error(f"FFmpeg output (attempt 1):\n{f.read()}")

            if used_pulse_audio:
                # If we used a named Pulse source, try 'default' first
                if audio_source and audio_source != 'default':
                    logger.warning("Retrying FFmpeg using PulseAudio 'default' source")
                    default_cmd = [
                        'ffmpeg', '-y',
                        '-f', 'x11grab',
                        '-thread_queue_size', '1024',
                        '-video_size', f'{SCREEN_WIDTH}x{SCREEN_HEIGHT}',
                        '-framerate', str(FRAMERATE),
                        '-i', display,
                        '-f', 'pulse', '-thread_queue_size', '4096', '-i', 'default',
                        '-vsync', '2',
                    ] + _get_video_encoder_opts() + [
                        '-c:a', 'aac', '-ar', AUDIO_SAMPLE_RATE, '-b:a', f'{AUDIO_BITRATE_K}k', '-ac', AUDIO_CHANNELS, '-af', 'aresample=async=1:min_hard_comp=0.100000:first_pts=0',
                        '-hls_time', HLS_TIME, '-hls_list_size', '0', '-hls_flags', 'independent_segments+append_list', '-hls_playlist_type', 'event', '-f', 'hls', str(HLS_DIR / 'stream.m3u8')
                    ]
                    with open(ffmpeg_log, 'w') as log_file:
                        ffmpeg_process = subprocess.Popen(
                            default_cmd,
                            stdout=log_file,
                            stderr=subprocess.STDOUT,
                            env=os.environ.copy()
                        )
                    time.sleep(2)
                    if ffmpeg_process.poll() is None:
                        return ffmpeg_process

                # Next, try PipeWire/Pulse capture via parec -> FIFO -> ffmpeg raw audio
                try:
                    import shutil as _shutil
                    if _shutil.which('parec'):
                        # Prepare FIFO
                        try:
                            if AUDIO_FIFO.exists():
                                AUDIO_FIFO.unlink()
                        except Exception:
                            pass
                        os.mkfifo(AUDIO_FIFO)

                        logger.warning("Retrying using parec -> FIFO pipeline")

                        # Build ffmpeg command to read raw PCM from FIFO
                        fifo_cmd = [
                            'ffmpeg', '-y',
                            '-f', 'x11grab', '-thread_queue_size', '1024',
                            '-video_size', f'{SCREEN_WIDTH}x{SCREEN_HEIGHT}',
                            '-framerate', str(FRAMERATE),
                            '-i', display,
                            '-f', 's16le', '-ar', '48000', '-ac', '2', '-i', str(AUDIO_FIFO),
                            '-vsync', '2',
                        ] + _get_video_encoder_opts() + [
                            '-c:a', 'aac', '-ar', AUDIO_SAMPLE_RATE, '-b:a', f'{AUDIO_BITRATE_K}k', '-ac', AUDIO_CHANNELS, '-af', 'aresample=async=1:min_hard_comp=0.100000:first_pts=0',
                            '-hls_time', HLS_TIME, '-hls_list_size', '0', '-hls_flags', 'independent_segments+append_list', '-hls_playlist_type', 'event', '-f', 'hls', str(HLS_DIR / 'stream.m3u8')
                        ]

                        with open(ffmpeg_log, 'w') as log_file:
                            ffmpeg_process = subprocess.Popen(
                                fifo_cmd,
                                stdout=log_file,
                                stderr=subprocess.STDOUT,
                                env=os.environ.copy()
                            )

                        # Open FIFO for write and start parec to feed it
                        fifo_w = open(AUDIO_FIFO, 'wb', buffering=0)
                        parec_dev = []
                        if audio_source:
                            # If we returned 'default', don't pass --device
                            if audio_source != 'default':
                                parec_dev = ['--device', audio_source]
                        else:
                            # No source known; rely on Pulse default
                            parec_dev = []

                        parec_cmd = ['parec', '--rate=48000', '--format=s16le', '--channels=2'] + parec_dev
                        parec_process = subprocess.Popen(
                            parec_cmd,
                            stdout=fifo_w,
                            stderr=subprocess.DEVNULL,
                            env=os.environ.copy()
                        )

                        time.sleep(2)
                        if ffmpeg_process.poll() is None:
                            logger.info("FFmpeg running with parec audio pipeline")
                            return ffmpeg_process
                        else:
                            logger.error("FFmpeg still failed with parec pipeline")
                            try:
                                if parec_process and parec_process.poll() is None:
                                    parec_process.terminate()
                            except Exception:
                                pass
                    else:
                        logger.warning("parec not found; skipping FIFO pipeline fallback")
                except Exception as e:
                    logger.warning(f"Failed to start parec pipeline: {e}")

                logger.warning("Retrying FFmpeg without PulseAudio using a silent lavfi source")
                fallback_cmd = [
                    'ffmpeg', '-y',
                    '-f', 'x11grab',
                    '-thread_queue_size', '1024',
                    '-video_size', f'{SCREEN_WIDTH}x{SCREEN_HEIGHT}',
                    '-framerate', str(FRAMERATE),
                    '-i', display,
                    '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo',
                    '-vsync', '2',
                ] + _get_video_encoder_opts() + [
                    '-c:a', 'aac', '-ar', AUDIO_SAMPLE_RATE, '-b:a', f'{AUDIO_BITRATE_K}k', '-ac', AUDIO_CHANNELS, '-af', 'aresample=async=1:min_hard_comp=0.100000:first_pts=0',
                    '-hls_time', HLS_TIME, '-hls_list_size', '0', '-hls_flags', 'independent_segments+append_list', '-hls_playlist_type', 'event', '-f', 'hls', str(HLS_DIR / 'stream.m3u8')
                ]
                with open(ffmpeg_log, 'w') as log_file:
                    ffmpeg_process = subprocess.Popen(
                        fallback_cmd,
                        stdout=log_file,
                        stderr=subprocess.STDOUT,
                        env=os.environ.copy()
                    )
                logger.info(f"FFmpeg fallback command: {' '.join(fallback_cmd)}")
                time.sleep(2)
                if ffmpeg_process.poll() is not None:
                    logger.error(f"FFmpeg fallback exited immediately with code: {ffmpeg_process.poll()}")
                    if ffmpeg_log.exists():
                        with open(ffmpeg_log, 'r') as f:
                            logger.error(f"FFmpeg output (fallback):\n{f.read()}")
                    return None
            else:
                return None

        return ffmpeg_process
    except Exception as e:
        logger.error(f"Failed to start FFmpeg: {e}")
        return None

def _get_playlist_total_ms(playlist_path):
    """Best-effort parse of HLS playlist duration in milliseconds.
    Returns 0 if parsing fails.
    """
    try:
        text = Path(playlist_path).read_text(errors='ignore')
        total = 0.0
        for line in text.splitlines():
            line = line.strip()
            if line.startswith('#EXTINF:'):
                try:
                    dur = float(line.split(':', 1)[1].split(',')[0])
                    total += dur
                except Exception:
                    pass
        return int(total * 1000)
    except Exception:
        return 0

def convert_hls_to_mp4(output_filename, job_id):
    """Convert HLS segments to MP4 with progress reporting."""
    hls_playlist = HLS_DIR / 'stream.m3u8'
    output_path = OUTPUT_DIR / output_filename
    
    if not hls_playlist.exists():
        logger.error("HLS playlist not found")
        return None
    
    logger.info(f"Converting HLS to MP4: {output_filename}")
    
    # Update status to CONVERTING
    update_job_status(job_id, 'CONVERTING', conversion_progress=0)
    
    command = [
        'ffmpeg',
        '-hide_banner', '-loglevel', 'error',
        '-y',
        '-i', str(hls_playlist),
        '-c', 'copy',
        '-movflags', 'faststart',
        '-bsf:a', 'aac_adtstoasc',
        '-progress', 'pipe:1',
        str(output_path)
    ]
    
    try:
        start_time = time.time()
        total_ms = _get_playlist_total_ms(hls_playlist)
        
        # Start FFmpeg with progress monitoring; merge stderr to avoid pipe blocking
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            universal_newlines=True
        )
        
        # Monitor progress
        last_update = 0.0
        last_output = time.time()
        for line in process.stdout:
            line = (line or '').strip()
            if not line:
                continue
            last_output = time.time()
            if line.startswith('out_time_ms='):
                try:
                    out_ms = int(line.split('=', 1)[1])
                except Exception:
                    out_ms = 0
                # Compute progress from playlist duration if available
                if total_ms and total_ms > 0:
                    progress = min(99, max(0, int(out_ms * 100 / total_ms)))
                    remaining = max(0, int((total_ms - out_ms) / 1000))
                else:
                    # Fallback to time-based heuristic
                    elapsed = time.time() - start_time
                    progress = min(99, int((elapsed / 60.0) * 100))
                    remaining = max(0, int(60 - elapsed))
                if time.time() - last_update > 5:
                    eta_time = (datetime.now(timezone.utc) + timedelta(seconds=remaining)).isoformat()
                    update_job_status(
                        job_id,
                        'CONVERTING',
                        conversion_progress=progress,
                        estimated_completion_time=eta_time
                    )
                    try:
                        send_heartbeat('CONVERTING', job_id)
                    except Exception:
                        pass
                    last_update = time.time()
                    logger.info(f"Conversion progress: {progress}%, ETA: {remaining}s")
            elif line.startswith('progress=') and line.split('=', 1)[1].strip() == 'end':
                # ffmpeg reports completion
                break

            # Watchdog: abort if ffmpeg stops emitting for too long
            if time.time() - last_output > 300:
                logger.error("Conversion stalled: no progress output for >300s; aborting")
                try:
                    process.terminate()
                except Exception:
                    pass
                try:
                    process.wait(timeout=10)
                except Exception:
                    pass
                return None
        
        process.wait()
        
        if process.returncode == 0 and output_path.exists():
            file_size = output_path.stat().st_size
            logger.info(f"MP4 created: {output_filename} ({file_size / 1024 / 1024:.1f} MB)")
            
            # Final progress update
            update_job_status(job_id, 'CONVERTING', conversion_progress=100)
            try:
                # Generate thumbnails and VTT for this MP4
                generate_thumbnails_and_vtt(output_path, interval_seconds=10)
            except Exception as _thumb_e:
                logger.warning(f"Thumbnail generation failed for {output_filename}: {_thumb_e}")
            return output_path
        else:
            try:
                # Read any remaining combined output for diagnostics
                tail = ''
                try:
                    tail = ''.join(process.stdout.readlines()[-50:])
                except Exception:
                    pass
                logger.error(f"FFmpeg conversion failed (rc={process.returncode}). Output tail:\n{tail}")
            except Exception:
                logger.error("FFmpeg conversion failed")
            return None
            
    except Exception as e:
        logger.error(f"Error during conversion: {e}")
        return None

def find_free_port(start=5900, end=5999):
    try:
        import socket as _s
        for p in range(start, end + 1):
            with _s.socket(_s.AF_INET, _s.SOCK_STREAM) as s:
                s.setsockopt(_s.SOL_SOCKET, _s.SO_REUSEADDR, 1)
                try:
                    s.bind(('0.0.0.0', p))
                    return p
                except Exception:
                    continue
    except Exception:
        pass
    return 5900

def start_vnc_server(display_name, port=None):
    """Start VNC server for remote viewing/control."""
    global vnc_process
    
    try:
        # Log file for x11vnc diagnostics
        safe_disp = display_name.replace(':', '').replace('/', '_')
        vnc_log_path = TEMP_DIR / f"x11vnc_{safe_disp}.log"
        # Choose port
        port = port or 5900
        # Start x11vnc and bind explicitly to all interfaces
        cmd = ['x11vnc', '-display', display_name, '-rfbport', str(port), '-listen', '0.0.0.0',
               '-forever', '-nopw', '-shared', '-quiet', '-noxdamage', '-noshm']
        try:
            logger.info(f"Starting x11vnc: {' '.join(cmd)}; log={vnc_log_path}")
        except Exception:
            pass
        with open(vnc_log_path, 'w') as vnc_log:
            vnc_process = subprocess.Popen(
                cmd,
                stdout=vnc_log,
                stderr=subprocess.STDOUT
            )
        # Give it a moment; if it dies immediately, report failure
        time.sleep(0.6)
        if vnc_process.poll() is not None:
            try:
                last = vnc_log_path.read_text()[-2000:]
            except Exception:
                last = ''
            logger.warning(f"x11vnc exited immediately for display {display_name} on port {port}. Log tail: {last}")
            return None
        logger.info(f"VNC server started on port {port} for display {display_name}")
        logger.info(f"Connect with: vncviewer {RECORDER_HOSTNAME}:{port}")
        return vnc_process
    except FileNotFoundError:
        logger.warning("x11vnc not found. Install with: sudo apt install x11vnc")
        return None
    except Exception as e:
        logger.warning(f"Failed to start VNC server: {e}")
        return None

def cleanup_recording():
    """Clean up recording processes and temp files."""
    global browser_process, ffmpeg_process, vnc_process
    
    # Stop VNC server
    if vnc_process and vnc_process.poll() is None:
        try:
            vnc_process.terminate()
            vnc_process.wait(timeout=5)
            logger.info("VNC server stopped")
        except Exception as e:
            logger.warning(f"Error stopping VNC server: {e}")
    # Clear controller VNC map
    try:
        controller_vnc.clear()
    except Exception:
        pass
    
    # Stop browser
    if browser_process and browser_process.poll() is None:
        try:
            os.killpg(os.getpgid(browser_process.pid), signal.SIGTERM)
            browser_process.wait(timeout=5)
        except Exception as e:
            logger.warning(f"Error stopping browser: {e}")
    
    # Stop FFmpeg
    if ffmpeg_process and ffmpeg_process.poll() is None:
        try:
            ffmpeg_process.send_signal(signal.SIGINT)
            ffmpeg_process.wait(timeout=10)
        except Exception as e:
            logger.warning(f"Error stopping FFmpeg: {e}")

    # Stop parec if running
    if 'parec_process' in globals() and parec_process and parec_process.poll() is None:
        try:
            parec_process.terminate()
        except Exception:
            pass
    # Remove FIFO if present
    try:
        if AUDIO_FIFO.exists():
            AUDIO_FIFO.unlink()
    except Exception:
        pass
    
    # Clean up temp files
    if HLS_DIR.exists():
        try:
            shutil.rmtree(HLS_DIR)
        except Exception as e:
            logger.warning(f"Error removing HLS directory: {e}")
    
    if READY_FLAG.exists():
        READY_FLAG.unlink()
    try:
        globals()['LIVE_START_TIME'] = None
    except Exception:
        pass

def execute_recording_job(job):
    """Execute a recording job."""
    global current_job
    current_job = job
    
    job_id = job['id']
    url = job['url']
    duration_seconds = job['duration_seconds']
    browser_controller = job.get('browser_controller', 'generic')
    
    logger.info(f"Starting job {job_id}: {url} ({duration_seconds}s) with {browser_controller} controller")
    update_job_status(job_id, 'RECORDING')
    try:
        send_heartbeat('RECORDING', job_id)
    except Exception:
        pass
    
    try:
        # Start virtual display
        from pyvirtualdisplay import Display
        display = Display(size=(SCREEN_WIDTH, SCREEN_HEIGHT), use_xauth=False)
        display.start()
        display_name = f":{display.display}"
        os.environ['DISPLAY'] = display_name
        logger.info(f"Virtual display started: {display_name}")
        
        # Start VNC server for remote viewing/control
        v = start_vnc_server(display_name)
        controller_vnc[browser_controller] = {'display': display_name, 'process': v}
        
        # Start browser with specified controller
        if not start_browser(url, display_name, browser_controller):
            raise Exception("Failed to start browser")
        
        # Wait for browser ready signal
        timeout = 60
        start_wait = time.time()
        
        while not READY_FLAG.exists():
            if time.time() - start_wait > timeout:
                # Log browser output for debugging
                if BROWSER_LOG.exists():
                    with open(BROWSER_LOG, 'r') as f:
                        browser_output = f.read()
                        logger.error(f"Browser output:\n{browser_output}")
                
                # Check if browser process is still running
                if browser_process and browser_process.poll() is not None:
                    logger.error(f"Browser process exited with code: {browser_process.poll()}")
                
                raise Exception("Browser failed to signal ready")
            time.sleep(0.5)
        
        logger.info("Browser ready, starting FFmpeg")
        
        # Start FFmpeg
        if not start_ffmpeg_recording(display_name):
            raise Exception("Failed to start FFmpeg")
        # Determine actual start time as when the first HLS segment appears
        actual_start_time = None
        try:
            playlist = HLS_DIR / 'stream.m3u8'
            start_wait = time.time()
            while time.time() - start_wait < 30:
                if playlist.exists():
                    try:
                        txt = playlist.read_text(errors='ignore')
                        if '#EXTINF' in txt:
                            actual_start_time = datetime.now(timezone.utc).isoformat()
                            try:
                                globals()['LIVE_START_TIME'] = actual_start_time
                            except Exception:
                                pass
                            break
                    except Exception:
                        pass
                time.sleep(0.5)
            if actual_start_time is None:
                actual_start_time = datetime.now(timezone.utc).isoformat()
                try:
                    globals()['LIVE_START_TIME'] = actual_start_time
                except Exception:
                    pass
        except Exception:
            actual_start_time = datetime.now(timezone.utc).isoformat()
            try:
                globals()['LIVE_START_TIME'] = actual_start_time
            except Exception:
                pass
        
        # Pre-check for cancellation before recording
        try:
            status_info = requests.get(f"{MANAGEMENT_SERVER_URL}/api/job_status/{job_id}", timeout=5).json()
            if status_info and status_info.get('status') == 'CANCELLED':
                logger.info(f"Job {job_id} already cancelled; aborting")
                raise Exception("Job cancelled")
        except Exception:
            pass

        # Record for specified duration, but poll for cancellation
        logger.info(f"Recording for {duration_seconds} seconds...")
        end_time = time.time() + duration_seconds
        cancelled = False
        stop_requested = False
        last_hb = 0
        while time.time() < end_time:
            time.sleep(1)
            try:
                resp = requests.get(f"{MANAGEMENT_SERVER_URL}/api/job_status/{job_id}", timeout=5)
                if resp.status_code == 200:
                    st = resp.json().get('status')
                    if st == 'CANCELLED':
                        cancelled = True
                        logger.info(f"Cancellation received for job {job_id}")
                        break
                    if st == 'STOPPING':
                        stop_requested = True
                        logger.info(f"Stop requested for job {job_id}; finalizing early")
                        break
            except Exception:
                # Transient network errors are ignored
                pass
            # Heartbeat while recording
            if time.time() - last_hb > HEARTBEAT_INTERVAL:
                try:
                    send_heartbeat('RECORDING', job_id)
                except Exception:
                    pass
                last_hb = time.time()
        
        # Stop FFmpeg gracefully
        if ffmpeg_process and ffmpeg_process.poll() is None:
            ffmpeg_process.send_signal(signal.SIGINT)
            ffmpeg_process.wait(timeout=10)
        actual_end_time = datetime.now(timezone.utc).isoformat()
        
        if cancelled:
            update_job_status(job_id, 'CANCELLED')
            logger.info(f"Job {job_id} cancelled; skipping MP4 conversion")
            # Cleanup & exit early
            display.stop()
            cleanup_recording()
            return

        logger.info("Recording complete, converting to MP4...")
        if stop_requested:
            logger.info("Job was stopped early; converting partial recording to MP4")
        
        # Convert to MP4 with progress reporting
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        filename = f"recording_{job_id}_{timestamp}.mp4"
        output_path = convert_hls_to_mp4(filename, job_id)
        
        if not output_path:
            raise Exception("MP4 conversion failed")
        
        # Register recording metadata with management server
        # File is stored in OUTPUT_DIR; management server proxies downloads from the recorder
        file_size = output_path.stat().st_size
        upload_recording_metadata(job_id, filename, file_size, duration_seconds, actual_start_time=actual_start_time, actual_end_time=actual_end_time)
        
        logger.info(f"Recording saved to: {output_path}")
        logger.info(f"File available via management server download endpoint")
        
        update_job_status(job_id, 'COMPLETED')
        logger.info(f"Job {job_id} completed successfully")
        
        # Cleanup
        display.stop()
        cleanup_recording()
        
    except Exception as e:
        logger.error(f"Job {job_id} failed: {e}")
        update_job_status(job_id, 'FAILED', str(e))
        cleanup_recording()
    
    finally:
        current_job = None

def generate_thumbnails_and_vtt(mp4_path: Path, interval_seconds: int = 10):
    """Generate thumbnail images and a WebVTT file next to the MP4 for UI previews.
    Thumbnails are written to OUTPUT_DIR / f"{filename}.thumbs" as JPEGs.
    VTT is written to OUTPUT_DIR / f"{filename}.vtt" and references manager-proxied paths
    like /recording_assets/{filename}/thumbs/<image>.
    """
    try:
        filename = mp4_path.name
        thumbs_dir = OUTPUT_DIR / f"{filename}.thumbs"
        thumbs_dir.mkdir(exist_ok=True)
        # Create thumbnails at fixed interval, scaled width to 320px (keep aspect)
        # Example: thumb_00001.jpg, thumb_00002.jpg, ...
        img_pattern = str(thumbs_dir / 'thumb_%05d.jpg')
        cmd = [
            'ffmpeg', '-hide_banner', '-loglevel', 'error',
            '-y', '-i', str(mp4_path),
            '-vf', f"fps=1/{interval_seconds},scale=320:-1", '-q:v', '7', img_pattern
        ]
        logger.info(f"Generating thumbnails for {filename} every {interval_seconds}s")
        try:
            subprocess.run(cmd, check=True)
        except subprocess.CalledProcessError as e:
            logger.warning(f"ffmpeg thumbnails failed: {e}")
            return
        # Determine duration for cue generation
        duration = 0.0
        try:
            import json as _json
            prob = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'json', str(mp4_path)], capture_output=True, text=True)
            if prob.returncode == 0:
                info = _json.loads(prob.stdout or '{}')
                duration = float(((info or {}).get('format') or {}).get('duration') or 0.0)
        except Exception:
            duration = 0.0
        # Build VTT
        vtt_path = OUTPUT_DIR / f"{filename}.vtt"
        try:
            imgs = sorted([p.name for p in thumbs_dir.glob('thumb_*.jpg')])
            if not imgs:
                return
            with open(vtt_path, 'w', encoding='utf-8') as f:
                f.write('WEBVTT\n\n')
                t = 0.0
                for idx, img in enumerate(imgs):
                    start = t
                    end = min(duration or (t + interval_seconds), t + interval_seconds)
                    def fmt(ts):
                        h = int(ts // 3600); m = int((ts % 3600) // 60); s = int(ts % 60); ms = int((ts - int(ts)) * 1000)
                        return f"{h:02d}:{m:02d}:{s:02d}.{ms:03d}"
                    f.write(f"{fmt(start)} --> {fmt(end)}\n")
                    # Use management_server proxy path for portability
                    f.write(f"/recording_assets/{filename}/thumbs/{img}\n\n")
                    t += interval_seconds
                    if duration and t >= duration:
                        break
            logger.info(f"Generated VTT: {vtt_path}")
        except Exception as e:
            logger.warning(f"Failed to write VTT for {filename}: {e}")
    except Exception as e:
        logger.warning(f"Thumbnail generation error: {e}")

# ============================================================================
# Main Recorder Loop
# ============================================================================

def recorder_main_loop():
    """Main recorder loop - poll for jobs and execute them."""
    global running
    
    last_heartbeat = 0
    
    while running:
        try:
            # Send heartbeat
            if time.time() - last_heartbeat > HEARTBEAT_INTERVAL:
                status = 'RECORDING' if current_job else 'IDLE'
                job_id = current_job['id'] if current_job else None
                send_heartbeat(status, job_id)
                last_heartbeat = time.time()
            
            # If idle, check for new jobs
            if not current_job:
                job = get_next_job()
                if job:
                    execute_recording_job(job)
            
            time.sleep(POLL_INTERVAL)
            
        except KeyboardInterrupt:
            logger.info("Received shutdown signal")
            running = False
            break
        except Exception as e:
            logger.error(f"Error in main loop: {e}")
            time.sleep(POLL_INTERVAL)

def cleanup_on_exit():
    """Cleanup on recorder exit."""
    global running
    running = False
    cleanup_recording()
    logger.info("Recorder shutdown complete")

atexit.register(cleanup_on_exit)

# ============================================================================
# Main Entry Point
# ============================================================================

def start_pipewire():
    """Start PipeWire and PipeWire-Pulse for audio."""
    try:
        # Set up runtime directory
        runtime_dir = Path(f"/run/user/{os.getuid()}")
        # Ensure runtime dir exists with correct permissions
        try:
            runtime_dir.mkdir(parents=True, exist_ok=True)
            os.chmod(runtime_dir, 0o700)
        except Exception as e:
            logger.warning(f"Failed to create XDG_RUNTIME_DIR {runtime_dir}: {e}")
        os.environ['XDG_RUNTIME_DIR'] = str(runtime_dir)
        
        # Start PipeWire daemon
        pipewire_proc = subprocess.Popen(
            ['pipewire'],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL
        )
        time.sleep(1)  # Give it time to start
        
        # Start PipeWire-Pulse (PulseAudio compatibility)
        pipewire_pulse_proc = subprocess.Popen(
            ['pipewire-pulse'],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL
        )
        # Start WirePlumber (session manager)
        try:
            wireplumber_proc = subprocess.Popen(
                ['wireplumber'],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
        except FileNotFoundError:
            wireplumber_proc = None
            logger.warning("WirePlumber not found; routing may be limited. Install with: sudo apt install wireplumber")

        # Wait for Pulse native socket to appear
        pulse_socket = runtime_dir / 'pulse' / 'native'
        for _ in range(20):  # ~10s
            if pulse_socket.exists():
                break
            time.sleep(0.5)

        if pulse_socket.exists():
            os.environ['PULSE_SERVER'] = f"unix:{pulse_socket}"
            logger.info(f"Using PulseAudio server at {os.environ['PULSE_SERVER']}")
        else:
            logger.warning("pipewire-pulse did not create pulse/native socket; trying legacy pulseaudio daemon")
            # Fallback to pulseaudio if available
            try:
                subprocess.Popen(
                    ['pulseaudio', '--start', '--exit-idle-time=-1', '--log-target=stderr'],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL
                )
            except FileNotFoundError:
                logger.warning("pulseaudio not installed; install pulseaudio and pulseaudio-utils for fallback path")
            # Wait a bit more for socket
            for _ in range(10):
                if pulse_socket.exists():
                    break
                time.sleep(0.5)
            if pulse_socket.exists():
                os.environ['PULSE_SERVER'] = f"unix:{pulse_socket}"
                logger.info(f"Using PulseAudio server at {os.environ['PULSE_SERVER']} (legacy daemon)")
            else:
                logger.warning("No Pulse server socket available; audio capture may fail")

        # Verify pactl connectivity using the same environment
        try:
            env = os.environ.copy()
            check = subprocess.run(['pactl', 'info'], capture_output=True, text=True, timeout=5, env=env)
            if check.returncode != 0:
                logger.warning(f"pactl info failed (rc={check.returncode}): {check.stderr.strip()}")
            else:
                logger.info("pactl is connected to Pulse server")
        except Exception as e:
            logger.warning(f"pactl connectivity check failed: {e}")

        logger.info("PipeWire audio system started")
        return True
    except FileNotFoundError:
        logger.warning("PipeWire not found - audio may not work")
        return False
        logger.warning(f"Failed to start PipeWire: {e}")
        return False

if __name__ == '__main__':
    import threading
    
    logger.info(f"Starting Recast Recorder: {RECORDER_ID}")
    logger.info(f"Management Server: {MANAGEMENT_SERVER_URL}")
    logger.info(f"Hostname: {RECORDER_HOSTNAME}")
    
    # Start PipeWire for audio
    start_pipewire()
    
    # Register with management server
    if not register_recorder():
        logger.error("Failed to register with management server, exiting")
        sys.exit(1)
    
    # Start Flask app for HLS streaming in background
    def _run_stream_app():
        try:
            port = int(os.environ.get('STREAM_PORT', '5001'))
            cert_file = os.environ.get('SSL_CERT_FILE')
            key_file = os.environ.get('SSL_KEY_FILE')
            ssl_ctx = None
            if cert_file and key_file:
                try:
                    ssl_ctx = (cert_file, key_file)
                    logger.info('Starting recorder stream_app with HTTPS enabled')
                except Exception as e:
                    logger.error(f'Failed to enable HTTPS for recorder: {e}')
                    ssl_ctx = None
            stream_app.run(host='0.0.0.0', port=port, threaded=True, use_reloader=False, ssl_context=ssl_ctx)
        except Exception as e:
            logger.error(f"stream_app run failed: {e}")

    flask_thread = threading.Thread(target=_run_stream_app, daemon=True)
    flask_thread.start()
    logger.info("HLS streaming server started on port 5001")
    
    # Start main recorder loop
    try:
        recorder_main_loop()
    except KeyboardInterrupt:
        logger.info("Recorder stopped by user")
    finally:
        cleanup_on_exit()
