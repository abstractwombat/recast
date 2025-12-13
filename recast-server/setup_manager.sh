#!/bin/bash
# Recast Server Setup Script
# Run on Kate (management server)

set -e  # Exit on error

echo "=========================================="
echo "Recast Server Setup"
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
        echo "  ./setup_manager.sh"
        exit 0
    else
        echo "Continuing as root (not recommended)..."
        echo ""
    fi
fi

# Get configuration
read -p "Enter recorder hostname (e.g., wilma): " RECORDER_HOSTNAME
read -p "Enter recorder IP address (e.g., 192.168.0.151): " RECORDER_IP

echo ""
echo "Configuration:"
echo "  Recorder hostname: $RECORDER_HOSTNAME"
echo "  Recorder IP: $RECORDER_IP"
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
sudo apt install -y python3 python3-pip python3-venv

echo ""
echo "Step 3: Creating directory structure..."
sudo mkdir -p /opt/recast
sudo chown recast:recast /opt/recast
sudo chmod 755 /opt/recast
cd /opt/recast || { echo "Failed to enter /opt/recast"; exit 1; }

echo ""
echo "Step 4: Installing Python dependencies..."
sudo -u recast python3 -m venv venv
sudo -u recast bash -c "source venv/bin/activate && pip install --upgrade pip && if [ -f /opt/recast/requirements.txt ]; then pip install -r /opt/recast/requirements.txt; else pip install Flask requests; fi"

echo ""
echo "Step 5: Configuring hostname resolution and local recordings directory..."
sudo mkdir -p /opt/recast/recordings
sudo chown recast:recast /opt/recast/recordings

# Add to /etc/hosts if not already there
if ! grep -q "$RECORDER_HOSTNAME" /etc/hosts; then
    echo "$RECORDER_IP $RECORDER_HOSTNAME" | sudo tee -a /etc/hosts
fi

echo ""
echo "Step 6: Creating configuration file..."
CONFIG_FILE="/opt/recast/config.toml"
EXAMPLE_FILE="/opt/recast/config.toml.example"

if [ -f "$CONFIG_FILE" ]; then
    echo "config.toml exists, updating values..."
elif [ -f "$EXAMPLE_FILE" ]; then
    echo "Copying config.toml.example to config.toml..."
    sudo -u recast cp "$EXAMPLE_FILE" "$CONFIG_FILE"
else
    echo "Creating config.toml from scratch..."
    sudo -u recast tee "$CONFIG_FILE" > /dev/null << 'CONFIGEOF'
# Recast Server Configuration

[server]
host = "0.0.0.0"
port = 5000
debug = false

[paths]
recordings_dir = "/opt/recast/recordings"
database = "/opt/recast/recordings.db"
CONFIGEOF
fi

# Ensure correct ownership
sudo chown recast:recast "$CONFIG_FILE"

echo ""
echo "Step 7: Configuring firewall..."
if command -v ufw >/dev/null 2>&1; then
    sudo ufw allow 5000/tcp comment "Recast Server"
else
    echo "ufw not found; skipping firewall rule for port 5000"
fi

echo ""
echo "Step 8: Installing systemd service..."
sudo ln -sf /opt/recast/recast-server.service /etc/systemd/system/recast-server.service
sudo systemctl daemon-reload
sudo systemctl enable recast-server

echo ""
echo "=========================================="
echo "Setup Complete!"
echo "=========================================="
echo ""
echo "Next steps:"
echo "1. Copy project files to /opt/recast/"
echo "   - recast_server.py"
echo "   - templates/"
echo ""
echo "2. Start the service:"
echo "   sudo systemctl start recast-server"
echo ""
echo "3. Check status:"
echo "   sudo systemctl status recast-server"
echo ""
echo "4. Access web interface:"
echo "   http://$(hostname):5000"
echo ""
echo "5. View logs:"
echo "   sudo journalctl -u recast-server -f"
echo ""
