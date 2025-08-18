#!/bin/bash

echo "🔍 Searching for MessageHub processes..."

# Find and kill MessageHub processes
PIDS=$(ps aux | grep -E "(dotnet.*MessageHub|MessageHub)" | grep -v grep | awk '{print $2}')

if [ -z "$PIDS" ]; then
    echo "✅ No MessageHub processes found"
else
    echo "🛑 Found processes to kill:"
    ps aux | grep -E "(dotnet.*MessageHub|MessageHub)" | grep -v grep
    echo ""
    echo "💀 Killing processes..."
    echo "$PIDS" | xargs kill -9 2>/dev/null || true
    echo "✅ Processes killed"
fi

# Check ports
echo ""
echo "🔍 Checking if ports 5289 and 7142 are free..."
PORT_5289=$(netstat -tlnp 2>/dev/null | grep ":5289 " || true)
PORT_7142=$(netstat -tlnp 2>/dev/null | grep ":7142 " || true)

if [ -z "$PORT_5289" ] && [ -z "$PORT_7142" ]; then
    echo "✅ Ports 5289 and 7142 are free"
else
    if [ ! -z "$PORT_5289" ]; then
        echo "⚠️  Port 5289 still in use: $PORT_5289"
    fi
    if [ ! -z "$PORT_7142" ]; then
        echo "⚠️  Port 7142 still in use: $PORT_7142"
    fi
fi

echo ""
echo "🚀 Ready for debugging!"