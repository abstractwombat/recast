#!/bin/bash
# Recast Linux Recorder Setup Script
# Run on Wilma (recording recorder)

set -e  # Exit on error

# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=========================================="
echo "Recast Linux Recorder Setup"
echo "=========================================="
echo ""

# Check if running as root
if [ "$EUID" -eq 0 ]; then 
    echo "Warning: Running as root"
    echo "It's recommended to run as a regular user with sudo privileges"
    echo ""
    read -p "Create a regular user for running this script? (y/n): " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        read -p "Enter username to create: " NEW_USER
        adduser --disabled-password --gecos "" $NEW_USER
        usermod -aG sudo $NEW_USER
        echo ""
        echo "User $NEW_USER created with sudo privileges"
        echo "Please run this script again as that user:"
        echo "  su - $NEW_USER"
        echo "  ./setup_recorder.sh"
        exit 0
    else
        echo "Continuing as root (not recommended)..."
        echo ""
    fi
fi

# Load defaults from existing config if present
CONFIG_FILE="/opt/recast/config.toml"
DEFAULT_RECORDER_ID=""
DEFAULT_RECORDER_HOSTNAME=""
DEFAULT_MANAGER_URL=""
DEFAULT_MANAGER_HOSTNAME=""

if [ -f "$CONFIG_FILE" ]; then
    echo "Found existing config at $CONFIG_FILE, loading defaults..."
    DEFAULT_RECORDER_ID=$(grep -E '^id\s*=' "$CONFIG_FILE" | sed 's/.*=\s*"\([^"]*\)".*/\1/' | head -1)
    DEFAULT_RECORDER_HOSTNAME=$(grep -E '^hostname\s*=' "$CONFIG_FILE" | sed 's/.*=\s*"\([^"]*\)".*/\1/' | head -1)
    DEFAULT_MANAGER_URL=$(grep -E '^management_server_url\s*=' "$CONFIG_FILE" | sed 's/.*=\s*"\([^"]*\)".*/\1/' | head -1)
    # Extract hostname from URL (e.g., http://kate:5000 -> kate)
    DEFAULT_MANAGER_HOSTNAME=$(echo "$DEFAULT_MANAGER_URL" | sed 's|http://\([^:]*\):.*|\1|')
    echo ""
fi

# Get configuration with defaults
if [ -n "$DEFAULT_MANAGER_HOSTNAME" ]; then
    read -p "Enter management server hostname [$DEFAULT_MANAGER_HOSTNAME]: " MANAGER_HOSTNAME
    MANAGER_HOSTNAME=${MANAGER_HOSTNAME:-$DEFAULT_MANAGER_HOSTNAME}
else
    read -p "Enter management server hostname (e.g., kate): " MANAGER_HOSTNAME
fi

# Try to get current IP from /etc/hosts for the manager hostname
DEFAULT_MANAGER_IP=$(grep -E "\s$MANAGER_HOSTNAME\$" /etc/hosts 2>/dev/null | awk '{print $1}' | head -1)
if [ -n "$DEFAULT_MANAGER_IP" ]; then
    read -p "Enter management server IP address [$DEFAULT_MANAGER_IP]: " MANAGER_IP
    MANAGER_IP=${MANAGER_IP:-$DEFAULT_MANAGER_IP}
else
    read -p "Enter management server IP address (e.g., 192.168.0.150): " MANAGER_IP
fi

if [ -n "$DEFAULT_RECORDER_HOSTNAME" ]; then
    read -p "Enter this recorder's hostname [$DEFAULT_RECORDER_HOSTNAME]: " RECORDER_HOSTNAME
    RECORDER_HOSTNAME=${RECORDER_HOSTNAME:-$DEFAULT_RECORDER_HOSTNAME}
else
    read -p "Enter this recorder's hostname (e.g., wilma): " RECORDER_HOSTNAME
fi

if [ -n "$DEFAULT_RECORDER_ID" ]; then
    read -p "Enter recorder ID [$DEFAULT_RECORDER_ID]: " RECORDER_ID
    RECORDER_ID=${RECORDER_ID:-$DEFAULT_RECORDER_ID}
else
    read -p "Enter recorder ID (e.g., recorder-wilma-01): " RECORDER_ID
fi

echo ""
echo "Configuration:"
echo "  Management server: $MANAGER_HOSTNAME ($MANAGER_IP)"
echo "  Recorder hostname: $RECORDER_HOSTNAME"
echo "  Recorder ID: $RECORDER_ID"
echo ""
read -p "Continue? (y/n): " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    exit 1
fi

echo ""
echo "Step 1: Creating recast user..."
sudo useradd -r -s /bin/bash -d /opt/recast -m recast 2>/dev/null || echo "User already exists"
sudo usermod -aG video recast
echo "Added recast user to video group"

echo ""
echo "Step 2: Installing system packages..."
sudo apt update
sudo apt install -y \
    python3 python3-pip python3-venv \
    ffmpeg \
    xvfb \
    xauth \
    x11vnc \
    xserver-xorg-video-dummy \
    pipewire pipewire-pulse wireplumber pulseaudio-utils
if ! command -v chromium >/dev/null 2>&1 && ! command -v chromium-browser >/dev/null 2>&1; then
    sudo apt install -y chromium || sudo apt install -y chromium-browser || (command -v snap >/dev/null 2>&1 && sudo snap install chromium) || echo "Warning: Chromium installation failed; please install Chromium manually"
fi

echo ""
echo "Step 3: Creating directory structure..."
sudo -u recast mkdir -p /opt/recast/recordings
sudo -u recast mkdir -p /opt/recast/recorder_temp
sudo -u recast chmod 755 /opt/recast
cd /opt/recast || { echo "Failed to enter /opt/recast"; exit 1; }

echo ""
echo "Step 4: Installing Python dependencies..."
sudo cp "$SCRIPT_DIR/requirements.txt" /opt/recast/requirements.txt
sudo chown recast:recast /opt/recast/requirements.txt
sudo -u recast python3 -m venv venv
sudo -u recast bash -c "source venv/bin/activate && pip install --upgrade pip && pip install -r /opt/recast/requirements.txt"

echo ""
echo "Step 5: Configuring firewall..."
if command -v ufw >/dev/null 2>&1; then
    sudo ufw allow 5001/tcp comment "Recast Recorder HLS Stream"
    sudo ufw allow 5900:5999/tcp comment "Recast Recorder VNC Ports"
else
    echo "ufw not found; skipping firewall rules for recorder"
fi

echo ""
echo "Step 6: Adding manager to /etc/hosts..."
if ! grep -q "$MANAGER_HOSTNAME" /etc/hosts; then
    echo "$MANAGER_IP $MANAGER_HOSTNAME" | sudo tee -a /etc/hosts
fi

echo ""
echo "Step 7: Creating configuration file..."
EXAMPLE_FILE="$SCRIPT_DIR/config.toml.example"

if [ -f "$CONFIG_FILE" ]; then
    echo "config.toml exists, updating values..."
else
    if [ ! -f "$EXAMPLE_FILE" ]; then
        echo "ERROR: config.toml.example not found at $EXAMPLE_FILE"
        exit 1
    fi
    echo "Copying config.toml.example to config.toml..."
    sudo cp "$EXAMPLE_FILE" "$CONFIG_FILE"
    sudo chown recast:recast "$CONFIG_FILE"
fi

# Update config with user values
sudo sed -i "s|^id = .*|id = \"$RECORDER_ID\"|" "$CONFIG_FILE"
sudo sed -i "s|^hostname = .*|hostname = \"$RECORDER_HOSTNAME\"|" "$CONFIG_FILE"
sudo sed -i "s|^management_server_url = .*|management_server_url = \"http://$MANAGER_HOSTNAME:5000\"|" "$CONFIG_FILE"

echo ""
echo "Step 8: Configuring Xorg dummy driver for virtual display..."
sudo mkdir -p /etc/X11/xorg.conf.d

# Create the Xorg dummy driver configuration for proper 60Hz refresh rate
sudo tee /etc/X11/xorg.conf.d/10-dummy.conf > /dev/null << 'XORGCONF'
Section "Device"
    Identifier  "DummyDevice"
    Driver      "dummy"
    VideoRam    256000
EndSection

Section "Monitor"
    Identifier  "DummyMonitor"
    HorizSync   28.0-80.0
    VertRefresh 48.0-75.0
    # 1920x1080 @ 60Hz modeline
    Modeline "1920x1080_60" 148.50 1920 2008 2052 2200 1080 1084 1089 1125 +hsync +vsync
    # 1080x1920 @ 60Hz modeline (portrait mode for vertical video)
    Modeline "1080x1920_60" 148.50 1080 1168 1212 1344 1920 1924 1929 1965 +hsync +vsync
EndSection

Section "Screen"
    Identifier  "DummyScreen"
    Device      "DummyDevice"
    Monitor     "DummyMonitor"
    DefaultDepth 24
    SubSection "Display"
        Depth 24
        Modes "1920x1080_60" "1080x1920_60"
    EndSubSection
EndSection

Section "ServerLayout"
    Identifier  "DummyLayout"
    Screen      "DummyScreen"
EndSection
XORGCONF

echo "Xorg dummy driver configuration created at /etc/X11/xorg.conf.d/10-dummy.conf"

# Configure Xwrapper to allow non-root users to start Xorg
if [ -f /etc/X11/Xwrapper.config ]; then
    sudo sed -i 's/^allowed_users=.*/allowed_users=anybody/' /etc/X11/Xwrapper.config
else
    echo "allowed_users=anybody" | sudo tee /etc/X11/Xwrapper.config > /dev/null
fi
echo "Configured Xwrapper to allow recast user to start Xorg"

# Ensure HLS output directory exists with correct permissions
echo "Setting up directory permissions..."
sudo mkdir -p /opt/recast/recorder_temp/hls_stream
sudo chown -R recast:recast /opt/recast/recorder_temp
sudo chmod -R 755 /opt/recast/recorder_temp

echo ""
echo "Step 10: Installing project files and systemd service..."
sudo cp "$SCRIPT_DIR"/*.py /opt/recast/
sudo cp -r "$SCRIPT_DIR/browser_controllers" /opt/recast/
sudo cp -r "$SCRIPT_DIR/templates" /opt/recast/
sudo cp "$SCRIPT_DIR/recast-recorder.service" /opt/recast/recast-recorder.service

# Set ownership (exclude Chrome profile and cache directories to avoid permission errors)
sudo chown -R recast:recast /opt/recast/*.py /opt/recast/browser_controllers /opt/recast/templates /opt/recast/recorder_temp 2>/dev/null || true
sudo chown recast:recast /opt/recast/config.toml /opt/recast/recast-recorder.service 2>/dev/null || true

sudo ln -sf /opt/recast/recast-recorder.service /etc/systemd/system/recast-recorder.service
sudo systemctl daemon-reload
sudo systemctl enable recast-recorder

echo ""
echo "=========================================="
echo "Setup Complete!"
echo "=========================================="
echo ""
echo "Next steps:"
echo ""
echo "1. Start the service:"
echo "   sudo systemctl start recast-recorder"
echo ""
echo "2. Check status:"
echo "   sudo systemctl status recast-recorder"
echo ""
echo "3. View logs:"
echo "   sudo journalctl -u recast-recorder -f"
echo ""

