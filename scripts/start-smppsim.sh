#!/bin/bash

# SMPP Simulator Starter Script
# Manages SMPP Simulator Docker Container for Development and Testing

echo "🚀 SMPP Simulator Starter"
echo "========================="

# Check if Docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Error: Docker is not running. Please start Docker first."
    exit 1
fi

# Check if container exists and is running
if docker ps --format 'table {{.Names}}' | grep -q "smppsim"; then
    echo "✅ SMPP Simulator is already running"
    echo ""
    echo "📊 Container Status:"
    docker ps --filter "name=smppsim" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
    echo ""
    echo "🌐 Web Interface: http://localhost:8088"
    echo "📡 SMPP Port: localhost:2775"
    echo "👤 Default credentials: smppclient1 / password"
    exit 0
fi

# Check if container exists but is stopped
if docker ps -a --format 'table {{.Names}}' | grep -q "smppsim"; then
    echo "🔄 Starting existing SMPP Simulator container..."
    docker start smppsim
    
    if [ $? -eq 0 ]; then
        echo "✅ SMPP Simulator started successfully!"
    else
        echo "❌ Failed to start existing container. Removing and recreating..."
        docker rm smppsim
        create_new_container=true
    fi
else
    create_new_container=true
fi

# Create new container if needed
if [ "$create_new_container" = true ]; then
    echo "🏗️  Creating new SMPP Simulator container..."
    
    docker run -p 2775:2775 -p 8088:88 -d --name smppsim eagafonov/smppsim
    
    if [ $? -eq 0 ]; then
        echo "✅ SMPP Simulator container created and started!"
        
        # Wait a moment for the container to fully start
        echo "⏳ Waiting for simulator to initialize..."
        sleep 3
        
        # Check if it's actually running
        if docker ps --format 'table {{.Names}}' | grep -q "smppsim"; then
            echo "✅ Container is running successfully!"
        else
            echo "⚠️  Container started but may still be initializing..."
        fi
    else
        echo "❌ Failed to create SMPP Simulator container"
        echo "💡 Try: docker pull eagafonov/smppsim"
        exit 1
    fi
fi

echo ""
echo "🎉 SMPP Simulator is ready!"
echo "========================="
echo "📡 SMPP Server: localhost:2775"
echo "🌐 Web Interface: http://localhost:8088"
echo "👤 Default credentials:"
echo "   - System ID: smppclient1" 
echo "   - Password: password"
echo ""
echo "🔧 Test Configuration (appsettings.Development.json):"
echo '   "SmppSettings": {'
echo '     "Host": "localhost",'
echo '     "Port": 2775,'
echo '     "SystemId": "smppclient1",'
echo '     "Password": "password",'
echo '     "MaxConnections": 3'
echo '   }'
echo ""
echo "🛑 To stop: docker stop smppsim"
echo "🗑️  To remove: docker rm smppsim"