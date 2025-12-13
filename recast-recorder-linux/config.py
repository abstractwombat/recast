"""
Configuration loader for Recast Recorder.
Loads settings from config.toml with sensible defaults.
"""

import socket
from pathlib import Path

# Use tomllib (Python 3.11+) or fall back to tomli
try:
    import tomllib
except ImportError:
    import tomli as tomllib


def load_config(config_path: Path = None) -> dict:
    """
    Load configuration from TOML file.
    
    Search order:
    1. Explicit config_path if provided
    2. /opt/recast/config.toml (production)
    3. ./config.toml (development)
    
    Returns dict with all config values, using defaults for missing keys.
    """
    if config_path is None:
        candidates = [
            Path("/opt/recast/config.toml"),
            Path(__file__).parent / "config.toml",
        ]
        for candidate in candidates:
            if candidate.exists():
                config_path = candidate
                break
    
    config = {}
    if config_path and config_path.exists():
        with open(config_path, "rb") as f:
            config = tomllib.load(f)
    
    return _apply_defaults(config)


def _apply_defaults(config: dict) -> dict:
    """Apply default values for missing configuration keys."""
    hostname = socket.gethostname()
    
    defaults = {
        "recorder": {
            "id": f"recorder-{hostname}",
            "hostname": hostname,
            "management_server_url": "http://localhost:5000",
            "poll_interval": 10,
            "heartbeat_interval": 30,
        },
        "recording": {
            "screen_width": 1920,
            "screen_height": 1080,
            "framerate": 30,
            "audio_source_name": "virtsink.monitor",
        },
        "video": {
            "preset": "ultrafast",
            "crf": 28,
            "maxrate_kbps": 2000,
            "bufsize_kbps": 4000,
            "threads": 2,
            "pix_fmt": "yuv420p",
            "profile": "baseline",
            "gop_multiplier": 2,
            "hls_time": 5,
        },
        "audio": {
            "bitrate_kbps": 192,
            "sample_rate": 48000,
            "channels": 2,
        },
        "paths": {
            "output_dir": "/opt/recast/recordings",
            "chrome_profiles_dir": str(Path.home() / ".recast-chrome"),
        },
    }
    
    result = {}
    for section, section_defaults in defaults.items():
        result[section] = {}
        config_section = config.get(section, {})
        for key, default_value in section_defaults.items():
            result[section][key] = config_section.get(key, default_value)
    
    return result


# Load config at module import time
_config = load_config()

# Expose config sections as module-level attributes for easy access
recorder = _config["recorder"]
recording = _config["recording"]
video = _config["video"]
audio = _config["audio"]
paths = _config["paths"]
