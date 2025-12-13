#!/bin/bash
# Recast Linux Recorder Setup Script
# Run on Wilma (recording recorder)

set -e  # Exit on error

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

# Get configuration
read -p "Enter management server hostname (e.g., kate): " MANAGER_HOSTNAME
read -p "Enter management server IP address (e.g., 192.168.0.150): " MANAGER_IP
read -p "Enter this recorder's hostname (e.g., wilma): " RECORDER_HOSTNAME
read -p "Enter recorder ID (e.g., recorder-wilma-01): " RECORDER_ID

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

echo ""
echo "Step 2: Installing system packages..."
sudo apt update
sudo apt install -y \
    python3 python3-pip python3-venv \
    ffmpeg \
    xvfb \
    xauth \
    x11vnc \
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
sudo -u recast python3 -m venv venv
sudo -u recast bash -c "source venv/bin/activate && pip install --upgrade pip && if [ -f /opt/recast/requirements.txt ]; then pip install -r /opt/recast/requirements.txt; else pip install Flask requests selenium pyautogui pyvirtualdisplay Pillow; fi"

echo ""
echo "Step 5: Skipping static PulseAudio null sink setup (recorder creates virtsink dynamically)"

echo ""
echo ""
echo "Step 6: Configuring firewall..."
if command -v ufw >/dev/null 2>&1; then
    sudo ufw allow 5001/tcp comment "Recast Recorder HLS Stream"
    sudo ufw allow 5900:5999/tcp comment "Recast Recorder VNC Ports"
else
    echo "ufw not found; skipping firewall rules for recorder"
fi

echo ""
echo "Step 7: Adding manager to /etc/hosts..."
if ! grep -q "$MANAGER_HOSTNAME" /etc/hosts; then
    echo "$MANAGER_IP $MANAGER_HOSTNAME" | sudo tee -a /etc/hosts
fi

echo ""
echo "Step 8: Creating configuration file..."
CONFIG_FILE="/opt/recast/config.toml"
EXAMPLE_FILE="/opt/recast/config.toml.example"

if [ -f "$CONFIG_FILE" ]; then
    echo "config.toml exists, updating values..."
    # Update existing config with sed
    sudo -u recast sed -i "s|^id = .*|id = \"$RECORDER_ID\"|" "$CONFIG_FILE"
    sudo -u recast sed -i "s|^hostname = .*|hostname = \"$RECORDER_HOSTNAME\"|" "$CONFIG_FILE"
    sudo -u recast sed -i "s|^management_server_url = .*|management_server_url = \"http://$MANAGER_HOSTNAME:5000\"|" "$CONFIG_FILE"
elif [ -f "$EXAMPLE_FILE" ]; then
    echo "Copying config.toml.example to config.toml..."
    sudo -u recast cp "$EXAMPLE_FILE" "$CONFIG_FILE"
    # Update with user values
    sudo -u recast sed -i "s|^id = .*|id = \"$RECORDER_ID\"|" "$CONFIG_FILE"
    sudo -u recast sed -i "s|^hostname = .*|hostname = \"$RECORDER_HOSTNAME\"|" "$CONFIG_FILE"
    sudo -u recast sed -i "s|^management_server_url = .*|management_server_url = \"http://$MANAGER_HOSTNAME:5000\"|" "$CONFIG_FILE"
else
    echo "Creating config.toml from scratch..."
    sudo -u recast tee "$CONFIG_FILE" > /dev/null << CONFIGEOF
# Recast Recorder Configuration

[recorder]
id = "$RECORDER_ID"
hostname = "$RECORDER_HOSTNAME"
management_server_url = "http://$MANAGER_HOSTNAME:5000"
poll_interval = 10
heartbeat_interval = 30

[recording]
screen_width = 1920
screen_height = 1080
framerate = 30
audio_source_name = "virtsink.monitor"

[video]
preset = "ultrafast"
crf = 28
maxrate_kbps = 2000
bufsize_kbps = 4000
threads = 2
pix_fmt = "yuv420p"
profile = "baseline"
gop_multiplier = 2
hls_time = 5

[audio]
bitrate_kbps = 192
sample_rate = 48000
channels = 2

[paths]
output_dir = "/opt/recast/recordings"
chrome_profiles_dir = "/home/recast/.recast-chrome"
CONFIGEOF
fi

# Ensure correct ownership
sudo chown recast:recast "$CONFIG_FILE"

echo ""
echo "Step 9: Installing systemd service..."
sudo ln -sf /opt/recast/recast-recorder.service /etc/systemd/system/recast-recorder.service
sudo systemctl daemon-reload
sudo systemctl enable recast-recorder

echo ""
echo "=========================================="
echo "Setup Complete!"
echo "=========================================="
echo ""
echo "Next steps:"
echo "1. Copy project files to /opt/recast/"
echo "   - recast_recorder.py"
echo "   - browser_controllers/"
echo ""
echo "2. Start the service:"
echo "   sudo systemctl start recast-recorder"
echo ""
echo "3. Check status:"
echo "   sudo systemctl status recast-recorder"
echo ""
echo "4. View logs:"
echo "   sudo journalctl -u recast-recorder -f"
echo ""

