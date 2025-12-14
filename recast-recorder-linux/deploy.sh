#!/bin/bash
# Deploy recast-recorder files to /opt/recast/
# Run as root or with sudo

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST_DIR="/opt/recast"

echo "Deploying from $SCRIPT_DIR to $DEST_DIR..."

# Create destination if needed
mkdir -p "$DEST_DIR"
mkdir -p "$DEST_DIR/browser_controllers"
mkdir -p "$DEST_DIR/templates"

# Copy Python files
cp "$SCRIPT_DIR"/*.py "$DEST_DIR/"

# Copy browser_controllers directory
cp -r "$SCRIPT_DIR/browser_controllers/"* "$DEST_DIR/browser_controllers/"

# Copy templates directory
cp -r "$SCRIPT_DIR/templates/"* "$DEST_DIR/templates/"

# Copy service file
cp "$SCRIPT_DIR/recast-recorder.service" "$DEST_DIR/"

# Set ownership (exclude problematic cache/fuse directories)
find "$DEST_DIR" -path "$DEST_DIR/.cache" -prune -o -print0 | xargs -0 chown recast:recast 2>/dev/null || true

echo "Deployment complete. Files copied to $DEST_DIR"
ls -la "$DEST_DIR"

echo "Restarting service..."
systemctl daemon-reload
systemctl restart recast-recorder.service
