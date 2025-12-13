"""
Configuration loader for Recast Server.
Loads settings from config.toml with sensible defaults.
"""

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
    defaults = {
        "server": {
            "host": "0.0.0.0",
            "port": 5000,
            "debug": False,
        },
        "paths": {
            "recordings_dir": "/opt/recast/recordings",
            "database": "/opt/recast/recordings.db",
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
server = _config["server"]
paths = _config["paths"]
