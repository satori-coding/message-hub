#!/bin/bash

# Start RabbitMQ Docker container for MessageHub development
# Similar to start-smppsim.sh pattern

CONTAINER_NAME="messagehub-rabbitmq"
RABBITMQ_IMAGE="masstransit/rabbitmq"

echo "🐰 Starting RabbitMQ for MessageHub development..."

# Check if container already exists
if docker ps -a --format 'table {{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
    echo "📦 Container '$CONTAINER_NAME' already exists"
    
    # Check if it's running
    if docker ps --format 'table {{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
        echo "✅ RabbitMQ is already running!"
    else
        echo "🔄 Starting existing container..."
        docker start $CONTAINER_NAME
        echo "✅ RabbitMQ started!"
    fi
else
    echo "🆕 Creating new RabbitMQ container..."
    docker run -d \
        --name $CONTAINER_NAME \
        -p 15672:15672 \
        -p 5672:5672 \
        $RABBITMQ_IMAGE
    
    echo "✅ RabbitMQ container created and started!"
fi

echo ""
echo "🌐 RabbitMQ Management UI: http://localhost:15672"
echo "👤 Username: guest"
echo "🔑 Password: guest"
echo "🔌 AMQP Port: 5672"
echo ""
echo "To stop RabbitMQ:"
echo "  docker stop $CONTAINER_NAME"
echo ""
echo "To remove RabbitMQ container:"
echo "  docker rm $CONTAINER_NAME"