"""
Recast Server - timeshift and record live streams

Provides web interface for scheduling recordings and serving completed videos.
"""

import sqlite3
import json
import datetime
from datetime import timezone
from flask import Flask, render_template, request, jsonify, send_from_directory, redirect, g
from pathlib import Path
import logging

import config

# Configuration
app = Flask(__name__, template_folder='templates')
logging.basicConfig(level=logging.INFO, format='%(asctime)s - [Recast-Server] - %(levelname)s - %(message)s')

# Paths from config
BASE_DIR = Path(__file__).parent
DB_PATH = Path(config.paths["database"])
RECORDINGS_DIR = Path(config.paths["recordings_dir"])
RECORDINGS_DIR.mkdir(exist_ok=True, parents=True)

RECORDING_CLIENTS = {}

def _track_recording_client(filename: str, ip: str):
    try:
        d = RECORDING_CLIENTS.get(filename) or {}
        d[ip or ''] = datetime.datetime.utcnow()
        RECORDING_CLIENTS[filename] = d
    except Exception:
        pass

def _get_recording_clients(filename: str, active_seconds: int = 10):
    try:
        d = RECORDING_CLIENTS.get(filename) or {}
        now = datetime.datetime.utcnow()
        out = []
        for ip, ts in d.items():
            try:
                active = (now - ts).total_seconds() <= active_seconds
                out.append({'ip': ip, 'active': active, 'last_seen': ts.strftime('%Y-%m-%d %H:%M:%S')})
            except Exception:
                pass
        return out
    except Exception:
        return []

def get_db():
    """Get a database connection from the application context."""
    db = getattr(g, '_database', None)
    if db is None:
        db = g._database = sqlite3.connect(DB_PATH)
        db.row_factory = sqlite3.Row
    return db

@app.teardown_appcontext
def close_db(exception):
    """Close the database connection at the end of the request."""
    db = getattr(g, '_database', None)
    if db is not None:
        db.close()

# Database initialization
def init_db():
    """Initialize SQLite database for job and recording management."""
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()
    
    # Jobs table
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS jobs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            url TEXT NOT NULL,
            title TEXT,
            start_time TEXT NOT NULL,
            end_time TEXT NOT NULL,
            duration_seconds INTEGER NOT NULL,
            browser_controller TEXT DEFAULT 'generic',
            status TEXT DEFAULT 'PENDING',
            recorder_id TEXT,
            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
            started_at TEXT,
            completed_at TEXT,
            error_message TEXT,
            conversion_progress INTEGER DEFAULT 0,
            conversion_start_time TEXT,
            estimated_completion_time TEXT
        )
    """)
    # Recordings table (filename required)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS recordings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id INTEGER NOT NULL,
            filename TEXT NOT NULL,
            file_size_bytes INTEGER,
            duration_seconds INTEGER,
            actual_start_time TEXT,
            actual_end_time TEXT,
            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
            FOREIGN KEY (job_id) REFERENCES jobs(id)
        )
    """)
    try:
        cursor.execute("PRAGMA table_info(recordings)")
        cols = [row[1] for row in cursor.fetchall()]
        if 'actual_start_time' not in cols:
            cursor.execute("ALTER TABLE recordings ADD COLUMN actual_start_time TEXT")
        if 'actual_end_time' not in cols:
            cursor.execute("ALTER TABLE recordings ADD COLUMN actual_end_time TEXT")
    except Exception:
        pass
    
    
    # Recorders table (for tracking active recorders)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS recorders (
            id TEXT PRIMARY KEY,
            hostname TEXT,
            ip_address TEXT,
            last_heartbeat TEXT,
            status TEXT DEFAULT 'IDLE',
            current_job_id INTEGER,
            capabilities TEXT
        )
    """)
    
    conn.commit()
    logging.info("Database initialized successfully")

init_db()

def format_time_fields(records, time_key):
    """
    Parses a time string from a list of dictionaries and adds a formatted version.

    Args:
        records (list[dict]): The list of job or recording dictionaries.
        time_key (str): The dictionary key containing the ISO time string.

    Returns:
        list[dict]: The updated list with a new '{time_key}_formatted' field.
    """
    OUTPUT_FORMAT = "%b %d %y %I:%M%p"

    formatted_key = f"{time_key}_formatted"

    for record in records:
        time_string = record.get(time_key)

        if not time_string:
            record[formatted_key] = 'N/A'
            continue

        dt_object = None
        try:
            dt_object = datetime.datetime.fromisoformat(time_string)
        except Exception:
            dt_object = None
        if dt_object is None:
            try:
                dt_object = datetime.datetime.strptime(time_string, "%Y-%m-%d %H:%M:%S")
            except Exception:
                dt_object = None

        if dt_object is None:
            record[formatted_key] = 'Format Error'
            continue

        if dt_object.tzinfo is None:
            dt_object = dt_object.replace(tzinfo=timezone.utc)

        # ISO 8601 UTC so browsers can convert to the viewer's local timezone
        try:
            record[f"{time_key}_iso"] = dt_object.astimezone(timezone.utc).isoformat()
        except Exception:
            pass

        try:
            local_dt = dt_object.astimezone()
            record[formatted_key] = local_dt.strftime(OUTPUT_FORMAT)
        except Exception:
            record[formatted_key] = 'Format Error'

    return records

def reconcile_jobs():
    conn = get_db()
    cursor = conn.cursor()
    now_iso = datetime.datetime.now(timezone.utc).isoformat()
    cursor.execute(
        """
        UPDATE jobs
        SET status = 'FAILED',
            error_message = CASE
                WHEN error_message IS NULL OR error_message = '' THEN '[reconciled] recorder offline or not running'
                ELSE error_message || ' [reconciled] recorder offline or not running'
            END,
            completed_at = ?
        WHERE id IN (
            SELECT j.id FROM jobs j
            LEFT JOIN recorders r ON j.recorder_id = r.id
            WHERE j.status IN ('ASSIGNED','STARTING','RECORDING','STOPPING','CONVERTING')
              AND (
                r.id IS NULL
                OR datetime(r.last_heartbeat) <= datetime('now','-2 minutes')
                OR (
                    j.status IN ('RECORDING','STOPPING','CONVERTING')
                    AND (r.status != 'RECORDING' OR r.current_job_id IS NULL OR r.current_job_id != j.id)
                )
              )
        )
        """,
        (now_iso,)
    )
    conn.commit()

# ============================================================================
# API Routes for Recorders
# ============================================================================

@app.route('/api/recorder/register', methods=['POST'])
def register_recorder():
    """Register a new recorder or update existing recorder info."""
    data = request.json
    recorder_id = data.get('recorder_id')
    hostname = data.get('hostname')
    # Derive the best client IP: prefer X-Forwarded-For (behind proxy),
    # then remote_addr. If loopback, fall back to payload-provided ip_address.
    fwd = request.headers.get('X-Forwarded-For')
    ip_address = (fwd.split(',')[0].strip() if fwd else request.remote_addr) or ''
    payload_ip = (data.get('ip_address') or '').strip()
    try:
        if not ip_address or ip_address.startswith('127.') or ip_address in ('::1', '0.0.0.0'):
            if payload_ip:
                ip_address = payload_ip
    except Exception:
        if payload_ip:
            ip_address = payload_ip
    capabilities = data.get('capabilities', [])

    conn = get_db()
    cursor = conn.cursor()
    # Use SQLite-friendly UTC format for datetime comparisons
    now_utc_str = datetime.datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')
    cursor.execute(
        "INSERT OR REPLACE INTO recorders (id, hostname, ip_address, last_heartbeat, status, capabilities) VALUES (?, ?, ?, ?, ?, ?)",
        (recorder_id, hostname, ip_address, now_utc_str, 'IDLE', json.dumps(capabilities))
    )
    conn.commit()

    logging.info(f"Recorder registered: {recorder_id} ({hostname} at {ip_address})")
    return jsonify({'status': 'success', 'recorder_id': recorder_id})
    
@app.route('/api/recorder/heartbeat', methods=['POST'])
def recorder_heartbeat():
    data = request.json
    recorder_id = data.get('recorder_id')
    status = data.get('status', 'IDLE')
    current_job_id = data.get('current_job_id')
    
    conn = get_db()
    cursor = conn.cursor()
    
    # Opportunistically capture client IP if missing or loopback
    fwd = request.headers.get('X-Forwarded-For')
    hb_ip = (fwd.split(',')[0].strip() if fwd else request.remote_addr) or ''
    cursor.execute("SELECT ip_address FROM recorders WHERE id = ?", (recorder_id,))
    row = cursor.fetchone()
    ip_to_set = None
    try:
        if row is None or not row['ip_address'] or row['ip_address'].startswith('127.') or row['ip_address'] in ('::1', '0.0.0.0'):
            if hb_ip and not hb_ip.startswith('127.'):
                ip_to_set = hb_ip
    except Exception:
        if hb_ip:
            ip_to_set = hb_ip
    now_utc_str = datetime.datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')
    if ip_to_set:
        cursor.execute("""
            UPDATE recorders 
            SET last_heartbeat = ?, status = ?, current_job_id = ?, ip_address = ?
            WHERE id = ?
        """, (now_utc_str, status, current_job_id, ip_to_set, recorder_id))
    else:
        cursor.execute("""
            UPDATE recorders 
            SET last_heartbeat = ?, status = ?, current_job_id = ?
            WHERE id = ?
        """, (now_utc_str, status, current_job_id, recorder_id))
    
    try:
        # If recorder reports actively recording a job, ensure the job reflects that
        if status == 'RECORDING' and current_job_id:
            cursor.execute(
                """
                UPDATE jobs
                SET status = 'RECORDING',
                    recorder_id = COALESCE(recorder_id, ?),
                    error_message = NULL,
                    started_at = COALESCE(started_at, ?)
                WHERE id = ?
                """,
                (recorder_id, datetime.datetime.now(timezone.utc).isoformat(), current_job_id)
            )
        cursor.execute(
            """
            UPDATE jobs
            SET status = 'FAILED',
                error_message = CASE
                    WHEN error_message IS NULL OR error_message = '' THEN '[reconciled] recorder heartbeat mismatch'
                    ELSE error_message || ' [reconciled] recorder heartbeat mismatch'
                END,
                completed_at = ?
            WHERE recorder_id = ?
              AND status IN ('RECORDING','STOPPING','CONVERTING')
              AND (
                    ? != 'RECORDING'
                    OR ? IS NULL
                    OR id != ?
                  )
            """,
            (now_utc_str, recorder_id, status, current_job_id, current_job_id)
        )
        conn.commit()
    except Exception:
        conn.commit()
    return jsonify({'status': 'success'})

@app.route('/api/jobs/<int:job_id>/title', methods=['POST'])
def set_job_title(job_id: int):
    data = request.json or {}
    title = (data.get('title') or '').strip()
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("SELECT id FROM jobs WHERE id = ?", (job_id,))
    row = cursor.fetchone()
    if not row:
        return jsonify({'status': 'error', 'message': 'Job not found'}), 404
    cursor.execute("UPDATE jobs SET title = ? WHERE id = ?", (title, job_id))
    conn.commit()
    return jsonify({'status': 'success'})

@app.route('/api/recording_clients/<path:filename>')
def get_recording_clients_api(filename):
    return jsonify({'clients': _get_recording_clients(filename)})

@app.route('/api/recorder/get_job', methods=['POST'])
def get_job_for_recorder():
    """Get next pending job for a recorder."""
    data = request.json
    recorder_id = data.get('recorder_id')
    
    conn = get_db()
    cursor = conn.cursor()

    # Find next job that should be started (start_time has passed, status is PENDING)
    cursor.execute("""
        SELECT * FROM jobs 
        WHERE status = 'PENDING' 
        AND datetime(start_time) <= datetime('now')
        ORDER BY datetime(start_time) ASC
        LIMIT 1
    """)
    
    job = cursor.fetchone()
    
    if job:
        job_id = job['id']
        # Assign job to recorder
        cursor.execute("""
            UPDATE jobs 
            SET status = 'ASSIGNED', recorder_id = ?, started_at = ?
            WHERE id = ?
        """, (recorder_id, datetime.datetime.now(timezone.utc).isoformat(), job_id))
        try:
            now_utc_str = datetime.datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')
            cursor.execute(
                """
                UPDATE recorders
                SET status = 'ASSIGNED', current_job_id = ?, last_heartbeat = ?
                WHERE id = ?
                """,
                (job_id, now_utc_str, recorder_id)
            )
        except Exception:
            pass
        conn.commit()
        
        job_dict = dict(job)
        
        logging.info(f"Job {job_id} assigned to recorder {recorder_id}")
        return jsonify({'status': 'success', 'job': job_dict})
    
    return jsonify({'status': 'no_jobs'})
@app.route('/api/recorder/update_job_status', methods=['POST'])
def update_job_status():
    """Update job status from recorder."""
    data = request.json
    status = data.get('status')
    error_message = data.get('error_message')
    conversion_progress = data.get('conversion_progress')
    estimated_completion_time = data.get('estimated_completion_time')
    job_id = data.get('job_id')
    should_retry = data.get('should_retry', False)
    
    conn = get_db()
    cursor = conn.cursor()
    
    if status == 'FAILED' and should_retry:
        # Reset job to PENDING to allow retry
        # Keep the original start_time, but clear recorder assignment and errors
        logging.info(f"Job {job_id} failed with retry requested. Resetting to PENDING. Error: {error_message}")
        cursor.execute("""
            UPDATE jobs 
            SET status = 'PENDING', 
                recorder_id = NULL,
                started_at = NULL,
                error_message = ? || ' (retrying)',
                conversion_progress = 0
            WHERE id = ?
        """, (error_message, job_id))
    elif status == 'CONVERTING':
        # Update conversion progress
        cursor.execute("""
            UPDATE jobs 
            SET status = ?, conversion_progress = ?, estimated_completion_time = ?,
                conversion_start_time = COALESCE(conversion_start_time, ?)
            WHERE id = ?
        """, (status, conversion_progress, estimated_completion_time, 
               datetime.datetime.now(timezone.utc).isoformat(), job_id))
    elif status == 'COMPLETED':
        cursor.execute("""
            UPDATE jobs 
            SET status = ?, completed_at = ?, error_message = ?, conversion_progress = 100
            WHERE id = ?
        """, (status, datetime.datetime.now(timezone.utc).isoformat(), error_message, job_id))
    else:
        cursor.execute("""
            UPDATE jobs 
            SET status = ?, error_message = ?
            WHERE id = ?
        """, (status, error_message, job_id))
    try:
        cursor.execute("SELECT recorder_id FROM jobs WHERE id = ?", (job_id,))
        jrow = cursor.fetchone()
        if jrow and jrow['recorder_id']:
            rid = jrow['recorder_id']
            now_utc_str = datetime.datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')
            if status in ('COMPLETED', 'FAILED', 'CANCELLED'):
                cursor.execute(
                    """
                    UPDATE recorders
                    SET status = 'IDLE', current_job_id = NULL, last_heartbeat = ?
                    WHERE id = ?
                    """,
                    (now_utc_str, rid)
                )
            else:
                cursor.execute(
                    """
                    UPDATE recorders
                    SET status = ?, current_job_id = ?, last_heartbeat = ?
                    WHERE id = ?
                    """,
                    (status, job_id, now_utc_str, rid)
                )
    except Exception:
        pass

    conn.commit()
    
    logging.info(f"Job {job_id} status updated to {status}")
    return jsonify({'status': 'success'})

@app.route('/api/recorder/upload_recording', methods=['POST'])
def upload_recording():
    """Receive recording metadata from recorder (file should be copied separately)."""
    data = request.json
    job_id = data.get('job_id')
    filename = data.get('filename')
    file_size_bytes = data.get('file_size_bytes')
    duration_seconds = data.get('duration_seconds')
    actual_start_time = data.get('actual_start_time')
    actual_end_time = data.get('actual_end_time')
    
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute(
        """
        INSERT INTO recordings (job_id, filename, file_size_bytes, duration_seconds, actual_start_time, actual_end_time)
        VALUES (?, ?, ?, ?, ?, ?)
        """,
        (job_id, filename, file_size_bytes, duration_seconds, actual_start_time, actual_end_time)
    )
    
    conn.commit()
    
    logging.info(f"Recording registered: {filename} for job {job_id}")
    return jsonify({'status': 'success'})

# ============================================================================
# Web UI Routes
# ============================================================================

@app.route('/')
def index():
    """Public home: show only live and recent recordings."""
    conn = get_db()
    cursor = conn.cursor()
    # Get recordings
    cursor.execute("""
        SELECT r.*, j.url, j.start_time, j.end_time, j.title, j.recorder_id, rec.hostname, rec.ip_address, rec.last_heartbeat
        FROM recordings r
        JOIN jobs j ON r.job_id = j.id
        LEFT JOIN recorders rec ON j.recorder_id = rec.id
        ORDER BY r.created_at DESC
        LIMIT 50
    """)
    recordings = [dict(row) for row in cursor.fetchall()]
    for rec in recordings:
        try:
            if rec.get('actual_start_time'):
                rec['start_time'] = rec.get('actual_start_time')
            if rec.get('actual_end_time'):
                rec['end_time'] = rec.get('actual_end_time')
            rec['clients'] = _get_recording_clients(rec.get('filename') or '')
            host = rec.get('ip_address') or rec.get('hostname')
            rec['recorder_host'] = host
            online = False
            hb = rec.get('last_heartbeat')
            if hb:
                try:
                    ts = datetime.datetime.strptime(hb, '%Y-%m-%d %H:%M:%S')
                    online = (datetime.datetime.utcnow() - ts).total_seconds() <= 120
                except Exception:
                    online = False
            rec['recorder_online'] = online
        except Exception:
            pass
    recordings = format_time_fields(recordings, 'start_time')
    recordings = format_time_fields(recordings, 'end_time')
    # Get upcoming jobs (Pending)
    cursor.execute(
        """
        SELECT id, url, title, start_time, end_time, status
        FROM jobs
        WHERE status = 'PENDING'
        ORDER BY datetime(start_time) ASC
        LIMIT 20
        """
    )
    upcoming_jobs = [dict(row) for row in cursor.fetchall()]
    upcoming_jobs = format_time_fields(upcoming_jobs, 'start_time')
    return render_template('home.html', recordings=recordings, upcoming_jobs=upcoming_jobs)

@app.route('/admin')
def admin_dashboard():
    """Admin dashboard shows jobs, recorders, recordings."""
    reconcile_jobs()
    conn = get_db()
    cursor = conn.cursor()
    # Get recent jobs
    cursor.execute("""
        SELECT * FROM jobs 
        ORDER BY created_at DESC 
        LIMIT 5
    """)
    jobs = [dict(row) for row in cursor.fetchall()]
    jobs = format_time_fields(jobs, 'start_time')
    jobs = format_time_fields(jobs, 'end_time')
    
    # Get recordings with recorder host and online status
    cursor.execute("""
        SELECT r.*, j.url, j.start_time, j.end_time, j.title, j.recorder_id, rec.hostname, rec.ip_address, rec.last_heartbeat
        FROM recordings r
        JOIN jobs j ON r.job_id = j.id
        LEFT JOIN recorders rec ON j.recorder_id = rec.id
        ORDER BY r.created_at DESC
        LIMIT 50
    """)
    recordings = [dict(row) for row in cursor.fetchall()]
    for rec in recordings:
        try:
            if rec.get('actual_start_time'):
                rec['start_time'] = rec.get('actual_start_time')
            if rec.get('actual_end_time'):
                rec['end_time'] = rec.get('actual_end_time')
            rec['clients'] = _get_recording_clients(rec.get('filename') or '')
            host = rec.get('ip_address') or rec.get('hostname')
            rec['recorder_host'] = host
            online = False
            hb = rec.get('last_heartbeat')
            if hb:
                try:
                    ts = datetime.datetime.strptime(hb, '%Y-%m-%d %H:%M:%S')
                    online = (datetime.datetime.utcnow() - ts).total_seconds() <= 120
                except Exception:
                    online = False
            rec['recorder_online'] = online
        except Exception:
            pass
    recordings = format_time_fields(recordings, 'start_time')
    recordings = format_time_fields(recordings, 'end_time')

    # Get active recorders
    cursor.execute("""
        SELECT * FROM recorders
        WHERE datetime(last_heartbeat) > datetime('now', '-2 minutes')
        ORDER BY last_heartbeat DESC
    """)
    recorders = [dict(row) for row in cursor.fetchall()]
    # Finalize recorder_online based on active set and ensure recorder_host
    active_recorder_ids = {r['id'] for r in recorders}
    recorder_host_map = {r['id']: (r.get('ip_address') or r.get('hostname')) for r in recorders}
    for rec in recordings:
        try:
            rid = rec.get('recorder_id')
            if rid in active_recorder_ids:
                rec['recorder_online'] = True
                if not rec.get('recorder_host'):
                    rec['recorder_host'] = recorder_host_map.get(rid)
        except Exception:
            pass

    return render_template('dashboard.html', jobs=jobs, recordings=recordings, recorders=recorders)
@app.route('/schedule')
def schedule_page():
    """Page for scheduling new recordings."""
    return render_template('schedule.html')

@app.route('/api/schedule_job', methods=['POST'])
def schedule_job():
    """API endpoint to schedule a new recording job."""
    data = request.json
    url = data.get('url')
    start_time = data.get('start_time')
    end_time = data.get('end_time')
    browser_controller = data.get('browser_controller', 'generic')
    title = (data.get('title') or '').strip()
    
    if not all([url, start_time, end_time]):
        return jsonify({'status': 'error', 'message': 'Missing required fields'}), 400
    
    # Validate browser controller
    valid_controllers = ['videojs', 'youtube', 'generic']
    if browser_controller not in valid_controllers:
        return jsonify({'status': 'error', 'message': f'Invalid browser controller. Must be one of: {valid_controllers}'}), 400
    
    try:
        user_start_dt = datetime.datetime.fromisoformat(start_time)
        user_end_dt = datetime.datetime.fromisoformat(end_time)

        local_tz = datetime.datetime.now().astimezone().tzinfo
        if user_start_dt.tzinfo is None:
            user_start_dt = user_start_dt.replace(tzinfo=local_tz)
        if user_end_dt.tzinfo is None:
            user_end_dt = user_end_dt.replace(tzinfo=local_tz)

        PRE_BUFFER_SECONDS = 120  # 2 minutes before for browser setup
        POST_BUFFER_SECONDS = 60  # 1 minute after for cleanup

        actual_start_local = user_start_dt - datetime.timedelta(seconds=PRE_BUFFER_SECONDS)
        actual_end_local = user_end_dt + datetime.timedelta(seconds=POST_BUFFER_SECONDS)

        actual_start_utc = actual_start_local.astimezone(timezone.utc)
        actual_end_utc = actual_end_local.astimezone(timezone.utc)

        duration_seconds = int((actual_end_utc - actual_start_utc).total_seconds())

        if duration_seconds <= 0:
            return jsonify({'status': 'error', 'message': 'End time must be after start time'}), 400

        conn = get_db()
        cursor = conn.cursor()

        start_str = actual_start_utc.strftime('%Y-%m-%d %H:%M:%S')
        end_str = actual_end_utc.strftime('%Y-%m-%d %H:%M:%S')
        cursor.execute(
            """
            INSERT INTO jobs (url, start_time, end_time, duration_seconds, browser_controller, status, title)
            VALUES (?, ?, ?, ?, ?, 'PENDING', ?)
            """,
            (url, start_str, end_str, duration_seconds, browser_controller, title)
        )

        job_id = cursor.lastrowid
        conn.commit()

        logging.info(f"New job scheduled: {job_id} for {url}")
        logging.info(f"  Actual recording time (UTC): {actual_start_utc.isoformat()} to {actual_end_utc.isoformat()}")

        return jsonify({
            'status': 'success',
            'job_id': job_id,
            'note': f'Recording will start {PRE_BUFFER_SECONDS//60} minutes early for browser setup'
        })

    except ValueError as e:
        return jsonify({'status': 'error', 'message': f'Invalid datetime format: {e}'}), 400

@app.route('/api/browser_controllers')
def get_browser_controllers():
    """Get list of available browser controllers."""
    controllers = [
        {
            'id': 'videojs',
            'name': 'VideoJS (Generic)',
            'description': 'Optimized for VideoJS streaming sites'
        },
        {
            'id': 'youtube',
            'name': 'YouTube',
            'description': 'Optimized for YouTube videos'
        },
        {
            'id': 'generic',
            'name': 'Generic',
            'description': 'Works with most video sites (fallback)'
        }
    ]
    return jsonify({'controllers': controllers})

@app.route('/api/live_streams')
def get_live_streams():
    """Get list of currently active recording streams."""
    reconcile_jobs()
    conn = get_db()
    cursor = conn.cursor()
    
    cursor.execute("""
        SELECT j.id, j.url, j.title, j.start_time, j.end_time, j.recorder_id, r.hostname, r.ip_address
        FROM jobs j
        JOIN recorders r ON j.recorder_id = r.id
        WHERE (
            j.status IN ('RECORDING', 'ASSIGNED', 'STARTING')
        ) OR (
            r.status = 'RECORDING'
            AND r.current_job_id = j.id
            AND datetime(r.last_heartbeat) > datetime('now', '-2 minutes')
        )
    """)
    
    live_jobs = [dict(row) for row in cursor.fetchall()]
    live_jobs = format_time_fields(live_jobs, 'start_time')
    live_jobs = format_time_fields(live_jobs, 'end_time')
    
    # Add stream URLs and readiness (recorders expose HLS streams on port 5001)
    import requests
    for job in live_jobs:
        recorder_ip = job['ip_address'] if job['ip_address'] else job['hostname']
        job['live_stream_url'] = f"http://{recorder_ip}:5001/live?job_id={job['id']}"
        ready = False
        try:
            r = requests.get(f"http://{recorder_ip}:5001/api/live_ready", timeout=2)
            if r.status_code == 200:
                jd = r.json()
                ready = bool(jd.get('ready'))
                job['live_start_time'] = jd.get('start_time')
        except Exception:
            ready = False
        job['live_ready'] = ready
        try:
            cr = requests.get(f"http://{recorder_ip}:5001/api/live_clients", timeout=2)
            if cr.status_code == 200:
                job['clients'] = cr.json().get('clients', [])
        except Exception:
            job['clients'] = []
    # Format live_start_time if present
    live_jobs = format_time_fields(live_jobs, 'live_start_time')
            
    return jsonify({'live_jobs': live_jobs})

@app.route('/api/job_status/<int:job_id>')
def get_job_status(job_id):
    """Get detailed status of a specific job."""
    conn = get_db()
    cursor = conn.cursor()
    
    cursor.execute("SELECT * FROM jobs WHERE id = ?", (job_id,))
    job = cursor.fetchone()
    
    if not job:
        return jsonify({'error': 'Job not found'}), 404
    
    job_dict = dict(job)
    
    # Normalize timestamps to ISO 8601 UTC; clients localize for display
    for time_field in ['start_time', 'end_time', 'created_at', 'started_at', 'completed_at']:
        time_string = job_dict.get(time_field)
        if time_string:
            try:
                dt_object = None
                try:
                    dt_object = datetime.datetime.fromisoformat(time_string)
                except Exception:
                    try:
                        dt_object = datetime.datetime.strptime(time_string, "%Y-%m-%d %H:%M:%S")
                    except Exception:
                        pass
                
                if dt_object:
                    if dt_object.tzinfo is None:
                        dt_object = dt_object.replace(tzinfo=timezone.utc)
                    # Emit ISO 8601 UTC; clients localize for display
                    job_dict[time_field] = dt_object.astimezone(timezone.utc).isoformat()
            except Exception:
                pass
    
    # Get associated recording if exists
    cursor.execute("SELECT * FROM recordings WHERE job_id = ?", (job_id,))
    recording = cursor.fetchone()
    if recording:
        job_dict['recording'] = dict(recording)
    
    return jsonify(job_dict)

@app.route('/api/cancel_job/<int:job_id>', methods=['POST'])
def cancel_job(job_id: int):
    """Cancel an active or pending job. Recorder will notice and stop early."""
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM jobs WHERE id = ?", (job_id,))
    job = cursor.fetchone()
    if not job:
        return jsonify({'status': 'error', 'message': 'Job not found'}), 404

    # Set status to CANCELLED; recorder polls this and will stop if recording
    cursor.execute(
        """
        UPDATE jobs
        SET status = 'CANCELLED', completed_at = ?
        WHERE id = ?
        """,
        (datetime.datetime.now(timezone.utc).isoformat(), job_id)
    )
    conn.commit()

    logging.info(f"Job {job_id} was cancelled via API")
    return jsonify({'status': 'success'})

@app.route('/api/stop_job/<int:job_id>', methods=['POST'])
def stop_job(job_id: int):
    """Request a graceful stop: recorder should finish current HLS and convert to MP4."""
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM jobs WHERE id = ?", (job_id,))
    job = cursor.fetchone()
    if not job:
        return jsonify({'status': 'error', 'message': 'Job not found'}), 404

    cursor.execute(
        """
        UPDATE jobs
        SET status = 'STOPPING', completed_at = ?
        WHERE id = ?
        """,
        (datetime.datetime.now(timezone.utc).isoformat(), job_id)
    )
    conn.commit()
    logging.info(f"Job {job_id} set to STOPPING via API")
    return jsonify({'status': 'success'})

@app.route('/api/jobs')
def get_jobs():
    """Get all jobs with filtering."""
    status_filter = request.args.get('status')
    
    conn = get_db()
    cursor = conn.cursor()
    
    if status_filter:
        cursor.execute("SELECT * FROM jobs WHERE status = ? ORDER BY created_at DESC", (status_filter,))
    else:
        cursor.execute("SELECT * FROM jobs ORDER BY created_at DESC LIMIT 100")
    
    jobs = [dict(row) for row in cursor.fetchall()]
    
    return jsonify({'jobs': jobs})

@app.route('/api/recordings')
def get_recordings():
    """Get all recordings."""
    conn = get_db()
    cursor = conn.cursor()
    
    cursor.execute("""
        SELECT r.*, j.url, j.start_time, j.end_time
        FROM recordings r
        JOIN jobs j ON r.job_id = j.id
        ORDER BY r.created_at DESC
    """)
    
    recordings = [dict(row) for row in cursor.fetchall()]
    
    return jsonify({'recordings': recordings})

@app.route('/api/recordings/<int:recording_id>/delete', methods=['POST'])
def delete_recording(recording_id: int):
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute(
        """
        SELECT r.id, r.filename, j.recorder_id
        FROM recordings r
        JOIN jobs j ON r.job_id = j.id
        WHERE r.id = ?
        """,
        (recording_id,)
    )
    rec = cursor.fetchone()
    if not rec:
        return jsonify({'status': 'error', 'message': 'Recording not found'}), 404

    filename = rec['filename']
    recorder_id = rec['recorder_id']
    if not recorder_id:
        return jsonify({'status': 'error', 'message': 'Recorder not found'}), 404

    cursor.execute("SELECT hostname, ip_address FROM recorders WHERE id = ?", (recorder_id,))
    recorder = cursor.fetchone()
    if not recorder:
        return jsonify({'status': 'error', 'message': 'Recorder offline'}), 503

    target_host = recorder['ip_address'] if recorder['ip_address'] else recorder['hostname']

    import requests
    try:
        resp = requests.post(f"http://{target_host}:5001/api/recordings/{filename}/delete", timeout=15)
        if resp.status_code == 404:
            # File already gone on the recorder — still remove the DB row
            logging.info(f"Recording file {filename} not found on recorder; removing DB entry anyway")
        elif resp.status_code != 200:
            try:
                data = resp.json()
                msg = data.get('message', 'Recorder deletion failed')
            except Exception:
                msg = 'Recorder deletion failed'
            return jsonify({'status': 'error', 'message': msg}), 502
    except Exception as e:
        logging.error(f"Error contacting recorder for delete: {e}")
        return jsonify({'status': 'error', 'message': 'Recorder not reachable'}), 502

    # Remove from DB
    cursor.execute("DELETE FROM recordings WHERE id = ?", (recording_id,))
    conn.commit()
    logging.info(f"Recording {recording_id} deleted (file {filename})")
    return jsonify({'status': 'success'})

@app.route('/playback/<int:job_id>')
def playback_page(job_id):
    """Playback page."""
    # Just pass the job_id, JavaScript will handle the rest
    return render_template('playback.html', job_id=job_id)

@app.route('/recordings/<path:filename>')
def serve_recording(filename):
    """Proxy MP4 files from recorder."""
    # Get the job/recording to find which recorder has it
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("""
        SELECT r.*, j.recorder_id 
        FROM recordings r 
        JOIN jobs j ON r.job_id = j.id 
        WHERE r.filename = ?
    """, (filename,))
    result = cursor.fetchone()
    
    if not result:
        return "Recording not found", 404
    
    # Get recorder hostname/IP
    recorder_id = result['recorder_id'] if result else None
    if not recorder_id:
        return "Recorder not found", 404
    
    # Get recorder info
    cursor.execute("SELECT hostname, ip_address FROM recorders WHERE id = ?", (recorder_id,))
    recorder = cursor.fetchone()
    if not recorder:
        return "Recorder offline", 503
    target_host = recorder['ip_address'] if recorder['ip_address'] else recorder['hostname']
    recorder_url = f"http://{target_host}:5001/recordings/{filename}"
    try:
        import requests
        # Forward Range for seeking support
        range_header = request.headers.get('Range')
        headers = {'Range': range_header} if range_header else {}
        response = requests.get(recorder_url, headers=headers, stream=True, timeout=30)
        try:
            ip = (request.headers.get('X-Forwarded-For') or '').split(',')[0].strip() or request.remote_addr
            _track_recording_client(filename, ip)
        except Exception:
            pass
        
        def generate():
            for chunk in response.iter_content(chunk_size=8192):
                try:
                    ip2 = (request.headers.get('X-Forwarded-For') or '').split(',')[0].strip() or request.remote_addr
                    _track_recording_client(filename, ip2)
                except Exception:
                    pass
                yield chunk
        # Build passthrough headers
        passthrough = {
            'Content-Disposition': f'inline; filename="{filename}"',
            'Accept-Ranges': response.headers.get('Accept-Ranges', 'bytes'),
        }
        if 'Content-Length' in response.headers:
            passthrough['Content-Length'] = response.headers['Content-Length']
        if 'Content-Range' in response.headers:
            passthrough['Content-Range'] = response.headers['Content-Range']
        mimetype = response.headers.get('Content-Type', 'video/mp4')
        return app.response_class(generate(), mimetype=mimetype, headers=passthrough, status=response.status_code)
    except Exception as e:
        logging.error(f"Error proxying recording: {e}")
        return "Error retrieving recording", 500

@app.route('/download/<int:recording_id>')
def download_recording(recording_id):
    """Download a recording as an attachment."""
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("""
        SELECT r.filename, j.recorder_id 
        FROM recordings r 
        JOIN jobs j ON r.job_id = j.id 
        WHERE r.id = ?
    """, (recording_id,))
    result = cursor.fetchone()
    
    if not result:
        return "Recording not found", 404
    
    filename = result['filename']
    recorder_id = result['recorder_id']
    
    # Get recorder hostname/IP
    cursor.execute("SELECT hostname, ip_address FROM recorders WHERE id = ?", (recorder_id,))
    recorder = cursor.fetchone()
    
    if not recorder:
        return "Recorder offline", 503
    target_host = recorder['ip_address'] if recorder['ip_address'] else recorder['hostname']
    recorder_url = f"http://{target_host}:5001/recordings/{filename}"
    try:
        import requests
        range_header = request.headers.get('Range')
        headers = {'Range': range_header} if range_header else {}
        response = requests.get(recorder_url, headers=headers, stream=True, timeout=30)
        
        def generate():
            for chunk in response.iter_content(chunk_size=8192):
                yield chunk
        headers_out = {
            'Content-Disposition': f'attachment; filename="{filename}"'
        }
        if 'Content-Length' in response.headers:
            headers_out['Content-Length'] = response.headers['Content-Length']
        if 'Content-Range' in response.headers:
            headers_out['Content-Range'] = response.headers['Content-Range']
        headers_out['Accept-Ranges'] = response.headers.get('Accept-Ranges', 'bytes')
        mimetype = response.headers.get('Content-Type', 'video/mp4')
        return app.response_class(generate(), mimetype=mimetype, headers=headers_out, status=response.status_code)
    except Exception as e:
        logging.error(f"Error downloading recording: {e}")
        return "Error downloading recording", 500

# ----------------------------------------------------------------------------
# CORS for media endpoints
# ----------------------------------------------------------------------------
@app.after_request
def add_cors_headers(resp):
    try:
        resp.headers['Access-Control-Allow-Origin'] = '*'
        resp.headers['Access-Control-Allow-Headers'] = 'Origin, Range, Content-Type, Accept'
        resp.headers['Access-Control-Allow-Methods'] = 'GET, OPTIONS'
    except Exception:
        pass
    return resp

# ----------------------------------------------------------------------------
# Proxy thumbnail VTT and images from recorder
# ----------------------------------------------------------------------------
@app.route('/recording_assets/<path:filename>/vtt')
def proxy_recording_vtt(filename):
    """Proxy WebVTT thumbnail track for a recording identified by its MP4 filename."""
    # Lookup recorder for this recording filename
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute(
        """
        SELECT r.filename, j.recorder_id
        FROM recordings r
        JOIN jobs j ON r.job_id = j.id
        WHERE r.filename = ?
        """,
        (filename,)
    )
    result = cursor.fetchone()
    if not result:
        return "Recording not found", 404
    recorder_id = result['recorder_id']
    cursor.execute("SELECT hostname, ip_address FROM recorders WHERE id = ?", (recorder_id,))
    recorder = cursor.fetchone()
    if not recorder:
        return "Recorder offline", 503
    target_host = recorder['ip_address'] if recorder['ip_address'] else recorder['hostname']
    recorder_url = f"http://{target_host}:5001/recordings/{filename}.vtt"
    try:
        import requests
        r = requests.get(recorder_url, stream=True, timeout=15)
        def generate():
            for chunk in r.iter_content(chunk_size=8192):
                yield chunk
        headers = {
            'Content-Type': r.headers.get('Content-Type', 'text/vtt; charset=utf-8')
        }
        if 'Content-Length' in r.headers:
            headers['Content-Length'] = r.headers['Content-Length']
        return app.response_class(generate(), headers=headers, status=r.status_code)
    except Exception as e:
        logging.error(f"Error proxying VTT for {filename}: {e}")
        return "Error retrieving VTT", 500

@app.route('/recording_assets/<path:filename>/thumbs/<path:image>')
def proxy_recording_thumb(filename, image):
    """Proxy thumbnail image for a recording identified by its MP4 filename."""
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute(
        """
        SELECT r.filename, j.recorder_id
        FROM recordings r
        JOIN jobs j ON r.job_id = j.id
        WHERE r.filename = ?
        """,
        (filename,)
    )
    result = cursor.fetchone()
    if not result:
        return "Recording not found", 404
    recorder_id = result['recorder_id']
    cursor.execute("SELECT hostname, ip_address FROM recorders WHERE id = ?", (recorder_id,))
    recorder = cursor.fetchone()
    if not recorder:
        return "Recorder offline", 503
    target_host = recorder['ip_address'] if recorder['ip_address'] else recorder['hostname']
    asset_path = f"{filename}.thumbs/{image}"
    recorder_url = f"http://{target_host}:5001/recordings/{asset_path}"
    try:
        import requests
        r = requests.get(recorder_url, stream=True, timeout=15)
        def generate():
            for chunk in r.iter_content(chunk_size=8192):
                yield chunk
        headers = {}
        if 'Content-Type' in r.headers:
            headers['Content-Type'] = r.headers['Content-Type']
        if 'Content-Length' in r.headers:
            headers['Content-Length'] = r.headers['Content-Length']
        return app.response_class(generate(), headers=headers, status=r.status_code)
    except Exception as e:
        logging.error(f"Error proxying thumbnail for {filename}: {e}")
        return "Error retrieving thumbnail", 500

# ============================================================================
# Main
# ============================================================================

if __name__ == '__main__':
    host = config.server["host"]
    port = config.server["port"]
    debug = config.server["debug"]
    app.run(host=host, port=port, threaded=True, debug=debug)
