#!/bin/bash

# MessageHub Local Development Startup Script
# Starts RabbitMQ and runs the application with Local configuration

set -e

echo "🚀 Starting MessageHub Local Development Environment..."

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
RABBITMQ_CONTAINER_NAME="messagequeue-rabbitmq"
RABBITMQ_PORT=5672
RABBITMQ_MGMT_PORT=15672
MAX_WAIT_TIME=30

# Function to stop existing MessageHub services
stop_messagehub() {
    echo -e "${YELLOW}🛑 Stopping any running MessageHub services...${NC}"
    
    # Find and kill dotnet run processes (MessageHub)
    local dotnet_pids=$(pgrep -f "dotnet run" || true)
    if [ ! -z "$dotnet_pids" ]; then
        echo -e "${YELLOW}📋 Found dotnet run processes: $dotnet_pids${NC}"
        for pid in $dotnet_pids; do
            echo -e "${YELLOW}🔨 Stopping process $pid...${NC}"
            kill $pid 2>/dev/null || true
            sleep 1
            
            # Force kill if still running
            if kill -0 $pid 2>/dev/null; then
                echo -e "${RED}💀 Force killing process $pid...${NC}"
                kill -9 $pid 2>/dev/null || true
            fi
        done
    fi
    
    # Find and kill any MessageHub related processes
    local messagehub_pids=$(pgrep -f "MessageHub" || true)
    if [ ! -z "$messagehub_pids" ]; then
        echo -e "${YELLOW}📋 Found MessageHub processes: $messagehub_pids${NC}"
        for pid in $messagehub_pids; do
            echo -e "${YELLOW}🔨 Stopping MessageHub process $pid...${NC}"
            kill $pid 2>/dev/null || true
            sleep 1
            
            # Force kill if still running
            if kill -0 $pid 2>/dev/null; then
                echo -e "${RED}💀 Force killing MessageHub process $pid...${NC}"
                kill -9 $pid 2>/dev/null || true
            fi
        done
    fi
    
    # Wait a moment for cleanup
    sleep 2
    
    # Verify cleanup
    local remaining_pids=$(pgrep -f "dotnet run\|MessageHub" || true)
    if [ -z "$remaining_pids" ]; then
        echo -e "${GREEN}✅ All MessageHub services stopped successfully${NC}"
    else
        echo -e "${RED}⚠️ Some processes may still be running: $remaining_pids${NC}"
        echo -e "${YELLOW}💡 You may need to manually kill them: kill -9 $remaining_pids${NC}"
    fi
}

# Function to validate system requirements
validate_system() {
    echo -e "${BLUE}🔍 Validating system requirements...${NC}"
    
    # Check if Docker is running
    if ! docker info >/dev/null 2>&1; then
        echo -e "${RED}❌ Docker is not running or not accessible${NC}"
        echo "Please start Docker Desktop or Docker daemon"
        exit 1
    fi
    echo -e "${GREEN}✅ Docker is running${NC}"
    
    # Check for any RabbitMQ container using the ports
    local existing_rabbit=$(docker ps --format "{{.Names}}" | grep -E "(rabbit|message)" | head -1)
    if [ ! -z "$existing_rabbit" ]; then
        echo -e "${YELLOW}💡 Found existing RabbitMQ-like container: ${existing_rabbit}${NC}"
        # Check if it's using our ports
        if docker port "$existing_rabbit" 2>/dev/null | grep -q "${RABBITMQ_PORT}"; then
            echo -e "${GREEN}✅ Container ${existing_rabbit} is using RabbitMQ ports - will reuse it${NC}"
            RABBITMQ_CONTAINER_NAME="$existing_rabbit"
        fi
    fi
}

# Function to get RabbitMQ container status
get_rabbitmq_status() {
    if docker ps --format "{{.Names}}" | grep -q "^${RABBITMQ_CONTAINER_NAME}$"; then
        echo "running"
    elif docker ps -a --format "{{.Names}}" | grep -q "^${RABBITMQ_CONTAINER_NAME}$"; then
        echo "stopped"
    else
        echo "not_found"
    fi
}

# Function to manage RabbitMQ container
manage_rabbitmq() {
    local status=$(get_rabbitmq_status)
    
    case $status in
        "running")
            echo -e "${GREEN}✅ RabbitMQ is already running${NC}"
            ;;
        "stopped")
            echo -e "${YELLOW}🔄 Starting existing RabbitMQ container...${NC}"
            if docker start ${RABBITMQ_CONTAINER_NAME}; then
                echo -e "${GREEN}✅ RabbitMQ container started${NC}"
            else
                echo -e "${RED}❌ Failed to start existing container${NC}"
                exit 1
            fi
            ;;
        "not_found")
            echo -e "${YELLOW}🆕 Creating new RabbitMQ container...${NC}"
            if docker run -d \
                --name ${RABBITMQ_CONTAINER_NAME} \
                -p ${RABBITMQ_PORT}:5672 \
                -p ${RABBITMQ_MGMT_PORT}:15672 \
                -e RABBITMQ_DEFAULT_USER=guest \
                -e RABBITMQ_DEFAULT_PASS=guest \
                rabbitmq:3-management; then
                echo -e "${GREEN}✅ RabbitMQ container created and started${NC}"
            else
                echo -e "${RED}❌ Failed to create RabbitMQ container${NC}"
                exit 1
            fi
            ;;
    esac
    
    echo -e "${YELLOW}📊 Management UI: http://localhost:${RABBITMQ_MGMT_PORT} (guest/guest)${NC}"
}

# Function to wait for RabbitMQ to be ready
wait_for_rabbitmq() {
    echo -e "${BLUE}⏳ Waiting for RabbitMQ to be ready...${NC}"
    
    local count=0
    local max_attempts=$((MAX_WAIT_TIME / 2))
    
    while [ $count -lt $max_attempts ]; do
        # Check if management API is responding
        if curl -s -u guest:guest "http://localhost:${RABBITMQ_MGMT_PORT}/api/overview" >/dev/null 2>&1; then
            echo -e "${GREEN}✅ RabbitMQ is ready and responding${NC}"
            return 0
        fi
        
        echo -n "."
        sleep 2
        count=$((count + 1))
    done
    
    echo ""
    echo -e "${RED}❌ RabbitMQ failed to become ready within ${MAX_WAIT_TIME} seconds${NC}"
    echo -e "${YELLOW}💡 You can check container logs with: docker logs ${RABBITMQ_CONTAINER_NAME}${NC}"
    exit 1
}

# Function to check Local configuration
check_local_config() {
    echo -e "${YELLOW}⚙️ Checking Local configuration...${NC}"
    
    if [ -f "appsettings.Local.json" ]; then
        echo -e "${GREEN}✅ appsettings.Local.json found${NC}"
    else
        echo -e "${RED}❌ appsettings.Local.json not found!${NC}"
        echo "Please ensure appsettings.Local.json exists in the project root."
        exit 1
    fi
}

# Function to cleanup on script exit
cleanup() {
    echo ""
    echo -e "${YELLOW}🧹 Script interrupted. Cleaning up...${NC}"
    # Note: We don't stop RabbitMQ as it might be used by other processes
    echo -e "${BLUE}💡 RabbitMQ container left running for reuse${NC}"
    echo -e "${BLUE}💡 To stop RabbitMQ: docker stop ${RABBITMQ_CONTAINER_NAME}${NC}"
    exit 0
}

# Function to start the application
start_application() {
    echo -e "${YELLOW}🌐 Starting MessageHub application with Local environment...${NC}"
    echo -e "${YELLOW}📝 API Tests available at: scripts/api-tests.http${NC}"
    echo -e "${YELLOW}📖 Swagger UI will be available at: https://localhost:7142/swagger${NC}"
    echo ""
    echo -e "${GREEN}Press Ctrl+C to stop the application${NC}"
    echo ""
    
    # Set trap for cleanup on script exit
    trap cleanup INT TERM
    
    # Start the application with Local environment
    ASPNETCORE_ENVIRONMENT=Local dotnet run
}

# Main execution
main() {
    echo "==========================================="
    echo "🏠 MessageHub Local Development Startup"
    echo "==========================================="
    
    # Step 1: Stop existing MessageHub services
    stop_messagehub
    
    # Step 2: System validation
    validate_system
    
    # Step 3: Check Local configuration
    check_local_config
    
    # Step 4: Manage RabbitMQ container
    manage_rabbitmq
    
    # Step 5: Wait for RabbitMQ to be ready (only if not already running)
    local status=$(get_rabbitmq_status)
    if [ "$status" != "running" ] || ! curl -s -u guest:guest "http://localhost:${RABBITMQ_MGMT_PORT}/api/overview" >/dev/null 2>&1; then
        wait_for_rabbitmq
    else
        echo -e "${GREEN}✅ RabbitMQ is already healthy${NC}"
    fi
    
    echo ""
    echo -e "${GREEN}🎉 All systems ready!${NC}"
    echo ""
    
    # Step 6: Start the application
    start_application
}

# Run main function
main "$@"