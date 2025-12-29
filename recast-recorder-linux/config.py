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

# For writing TOML files
try:
    import tomli_w
except ImportError:
    tomli_w = None


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
            try:
                config = tomllib.load(f)
            except Exception as e:
                raise RuntimeError(f"Failed to parse TOML config at {config_path}: {e}")
    
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
            "video_thread_queue_size": 8192,
            "audio_thread_queue_size": 4096,
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
            # If > 0, overrides gop_multiplier and sets GOP to (framerate * gop_seconds)
            "gop_seconds": 0,
            "hls_time": 5,
            "hls_list_size": 0,
            "hls_flags": "independent_segments+append_list",
            "hls_playlist_type": "event",
            # Video sync mode (replaces deprecated -vsync usage). Common values: cfr, vfr, passthrough
            "fps_mode": "cfr",
            "hw_accel": "none",  # Options: none, vaapi, nvenc, qsv

            # NVENC-specific options (used when hw_accel == 'nvenc')
            "nvenc_preset": "p4",
            "nvenc_rc": "vbr",
            "nvenc_tune": "",
            "nvenc_cq": 0,
            "nvenc_profile": "",
            "nvenc_bframes": 0,
            "nvenc_lookahead": 0,
            "nvenc_spatial_aq": 0,
            "nvenc_temporal_aq": 0,
            "nvenc_aq_strength": 0,
        },
        "audio": {
            "bitrate_kbps": 192,
            "sample_rate": 48000,
            "channels": 2,
        },
        "paths": {
            "output_dir": "/opt/recast/recordings",
            "chrome_profiles_dir": "/opt/recast/chrome_profiles",
        },
    }
    
    result = {}
    for section, section_defaults in defaults.items():
        result[section] = {}
        config_section = config.get(section, {})
        for key, default_value in section_defaults.items():
            result[section][key] = config_section.get(key, default_value)
    
    return result


# Track the config file path for saving
_config_path = None


def _find_config_path() -> Path:
    """Find the config file path."""
    candidates = [
        Path("/opt/recast/config.toml"),
        Path(__file__).parent / "config.toml",
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    # Default to local config.toml if none exists
    return Path(__file__).parent / "config.toml"


def get_config_path() -> Path:
    """Get the current config file path."""
    global _config_path
    if _config_path is None:
        _config_path = _find_config_path()
    return _config_path


def save_config(config_data: dict, config_path: Path = None) -> bool:
    """
    Save configuration to TOML file.
    
    Args:
        config_data: Dict with config sections (recording, video, audio, etc.)
        config_path: Optional path to save to. Uses current config path if not specified.
    
    Returns:
        True if save succeeded, False otherwise.
    """
    if tomli_w is None:
        raise ImportError("tomli_w is required for saving config. Install with: pip install tomli-w")
    
    if config_path is None:
        config_path = get_config_path()
    
    try:
        with open(config_path, "wb") as f:
            tomli_w.dump(config_data, f)
        return True
    except Exception as e:
        raise IOError(f"Failed to save config: {e}")


def reload_config():
    """Reload configuration from disk and update module-level attributes."""
    global _config, recorder, recording, video, audio, paths
    _config = load_config(get_config_path())
    recorder = _config["recorder"]
    recording = _config["recording"]
    video = _config["video"]
    audio = _config["audio"]
    paths = _config["paths"]
    return _config


def get_full_config() -> dict:
    """Get the full current configuration as a dict."""
    return {
        "recorder": dict(recorder),
        "recording": dict(recording),
        "video": dict(video),
        "audio": dict(audio),
        "paths": dict(paths),
    }


def update_section(section: str, updates: dict) -> dict:
    """
    Update a specific config section and save to disk.
    
    Args:
        section: Section name (recording, video, audio)
        updates: Dict of key-value pairs to update
    
    Returns:
        The updated full config dict.
    """
    full_config = get_full_config()
    if section not in full_config:
        raise ValueError(f"Unknown config section: {section}")
    
    full_config[section].update(updates)
    save_config(full_config)
    return reload_config()


# Load config at module import time
_config = load_config()
_config_path = _find_config_path()

# Expose config sections as module-level attributes for easy access
recorder = _config["recorder"]
recording = _config["recording"]
video = _config["video"]
audio = _config["audio"]
paths = _config["paths"]
