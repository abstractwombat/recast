#!/bin/bash
# Recast Server Setup Script
# Run as root on the recast server

set -e  # Exit on error

# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=========================================="
echo "Recast Server Setup"
echo "=========================================="
echo ""

if [ "$EUID" -ne 0 ]; then
    echo "Error: This script must be run as root" >&2
    exit 1
fi

echo "Step 1: Creating recast user..."
if id -u recast >/dev/null 2>&1; then
    echo "User already exists, updating to system account settings..."
    usermod -L -d /opt/recast -s /usr/sbin/nologin recast
else
    useradd -r -d /opt/recast -m -s /usr/sbin/nologin recast
fi

echo ""
echo "Step 2: Installing system packages..."
apt update
apt install -y python3 python3-pip python3-venv

echo ""
echo "Step 3: Creating directory structure..."
mkdir -p /opt/recast
chown recast:recast /opt/recast
chmod 755 /opt/recast
cd /opt/recast || { echo "Failed to enter /opt/recast"; exit 1; }

echo ""
echo "Step 4: Installing Python dependencies..."
cp "$SCRIPT_DIR/requirements.txt" /opt/recast/requirements.txt
chown recast:recast /opt/recast/requirements.txt
runuser -u recast -- python3 -m venv venv
runuser -u recast -- bash -c "source venv/bin/activate && pip install --upgrade pip && pip install -r /opt/recast/requirements.txt"

echo ""
echo "Step 5: Creating recordings directory..."
mkdir -p /opt/recast/recordings
chown recast:recast /opt/recast/recordings

echo ""
echo "Step 6: Creating configuration file..."
CONFIG_FILE="/opt/recast/config.toml"
EXAMPLE_FILE="$SCRIPT_DIR/config.toml.example"

if [ -f "$CONFIG_FILE" ]; then
    echo "config.toml already exists, skipping..."
else
    echo "Copying config.toml.example to config.toml..."
    cp "$EXAMPLE_FILE" "$CONFIG_FILE"
fi

# Ensure correct ownership
chown recast:recast "$CONFIG_FILE"

echo ""
echo "Step 7: Configuring firewall..."
if command -v ufw >/dev/null 2>&1; then
    ufw allow 5000/tcp comment "Recast Server"
else
    echo "ufw not found; skipping firewall rule for port 5000"
fi

echo ""
echo "Step 8: Installing systemd service..."

cp "$SCRIPT_DIR"/*.py /opt/recast/
cp -r "$SCRIPT_DIR/templates" /opt/recast/
cp "$SCRIPT_DIR/recast-server.service" /opt/recast/recast-server.service

chown -R recast:recast /opt/recast/

ln -sf /opt/recast/recast-server.service /etc/systemd/system/recast-server.service
systemctl daemon-reload
systemctl enable recast-server

echo ""
echo "=========================================="
echo "Setup Complete!"
echo "=========================================="
echo ""
echo "Next steps:"
echo ""
echo "1. Start the service:"
echo "   systemctl start recast-server"
echo ""
echo "2. Check status:"
echo "   systemctl status recast-server"
echo ""
echo "3. Access web interface:"
echo "   http://$(hostname):5000"
echo ""
echo "4. View logs:"
echo "   journalctl -u recast-server -f"
echo ""
