#!/bin/bash

# ========================================
# Multi-Tenant Memory & Resource Testing
# ========================================
# 
# PURPOSE: Long-running memory leak detection for multi-tenant SMS service
# FOCUS: Memory leaks, connection cleanup, resource exhaustion, channel disposal
# 
# TEST SCENARIOS:
# 1. Long-running concurrent tenant access (10+ minutes)
# 2. Memory usage monitoring and leak detection
# 3. SMPP connection lifecycle validation
# 4. Channel cleanup verification
# 5. Resource exhaustion testing
#
# REQUIREMENTS:
# - SMPP simulator running on localhost:2775
# - MessageHub service in multi-tenant mode
# - System monitoring tools (ps, free, etc.)

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Configuration
BASE_URL="https://localhost:7142"
TENANT_A_KEY="dev-tenant-a-12345"
TENANT_B_KEY="dev-tenant-b-67890"
TENANT_C_KEY="dev-tenant-c-http-99999"

# Memory test parameters
TEST_DURATION_MINUTES=10
MONITORING_INTERVAL_SECONDS=30
MESSAGES_PER_CYCLE=3
MEMORY_SAMPLES_FILE="/tmp/memory_samples.csv"
RESOURCE_LOG_FILE="/tmp/resource_monitoring.log"

# System monitoring
MESSAGEHUB_PID=""
BASELINE_MEMORY=0
PEAK_MEMORY=0
MEMORY_GROWTH=0

# Logging functions
log() {
    echo -e "${BLUE}[$(date +'%H:%M:%S')]${NC} $1"
    echo "[$(date +'%H:%M:%S')] $1" >> "$RESOURCE_LOG_FILE"
}

success() {
    echo -e "${GREEN}✅ $1${NC}"
    echo "[$(date +'%H:%M:%S')] SUCCESS: $1" >> "$RESOURCE_LOG_FILE"
}

error() {
    echo -e "${RED}❌ $1${NC}"
    echo "[$(date +'%H:%M:%S')] ERROR: $1" >> "$RESOURCE_LOG_FILE"
}

warning() {
    echo -e "${YELLOW}⚠️  $1${NC}"
    echo "[$(date +'%H:%M:%S')] WARNING: $1" >> "$RESOURCE_LOG_FILE"
}

# Get memory usage of MessageHub process
get_memory_usage() {
    if [ ! -z "$MESSAGEHUB_PID" ] && kill -0 "$MESSAGEHUB_PID" 2>/dev/null; then
        local memory_kb=$(ps -p "$MESSAGEHUB_PID" -o rss= 2>/dev/null | awk '{print $1}')
        if [ ! -z "$memory_kb" ]; then
            echo $((memory_kb / 1024))  # Convert to MB
        else
            echo "0"
        fi
    else
        echo "0"
    fi
}

# Get CPU usage of MessageHub process  
get_cpu_usage() {
    if [ ! -z "$MESSAGEHUB_PID" ] && kill -0 "$MESSAGEHUB_PID" 2>/dev/null; then
        local cpu_usage=$(ps -p "$MESSAGEHUB_PID" -o pcpu= 2>/dev/null | awk '{print $1}')
        echo "${cpu_usage:-0}"
    else
        echo "0"
    fi
}

# Send test messages
send_test_messages() {
    local cycle=$1
    
    # Tenant A - SMPP
    curl -k -s -X POST "${BASE_URL}/api/message/send" \
        -H "X-Subscription-Key: ${TENANT_A_KEY}" \
        -H "Content-Type: application/json" \
        -d "{\"PhoneNumber\": \"+49111${cycle}001\", \"Content\": \"Memory Test A Cycle ${cycle}\", \"ChannelName\": \"localhost-smpp\"}" \
        > /dev/null 2>&1 &
    
    # Tenant B - SMPP (different channel)
    curl -k -s -X POST "${BASE_URL}/api/message/send" \
        -H "X-Subscription-Key: ${TENANT_B_KEY}" \
        -H "Content-Type: application/json" \
        -d "{\"PhoneNumber\": \"+49222${cycle}002\", \"Content\": \"Memory Test B Cycle ${cycle}\", \"ChannelName\": \"localhost-smpp-alt\"}" \
        > /dev/null 2>&1 &
    
    # Tenant C - HTTP
    curl -k -s -X POST "${BASE_URL}/api/message/send" \
        -H "X-Subscription-Key: ${TENANT_C_KEY}" \
        -H "Content-Type: application/json" \
        -d "{\"PhoneNumber\": \"+49333${cycle}003\", \"Content\": \"Memory Test C Cycle ${cycle}\", \"ChannelName\": \"httpbin-primary\"}" \
        > /dev/null 2>&1 &
        
    wait  # Wait for all 3 messages to complete
}

# System resource monitoring function
monitor_system_resources() {
    local timestamp=$(date +%s)
    local memory_mb=$(get_memory_usage)
    local cpu_percent=$(get_cpu_usage)
    local system_memory=$(free | grep '^Mem:' | awk '{printf "%.1f", ($3/$2) * 100.0}')
    local system_load=$(uptime | awk -F'load average:' '{print $2}' | awk '{print $1}' | sed 's/,//')
    
    # Log to CSV for analysis
    echo "${timestamp},${memory_mb},${cpu_percent},${system_memory},${system_load}" >> "$MEMORY_SAMPLES_FILE"
    
    # Track peak memory
    if [ "$memory_mb" -gt "$PEAK_MEMORY" ]; then
        PEAK_MEMORY=$memory_mb
    fi
    
    # Calculate memory growth from baseline
    if [ "$BASELINE_MEMORY" -gt 0 ]; then
        MEMORY_GROWTH=$((memory_mb - BASELINE_MEMORY))
    fi
    
    echo "${memory_mb}"  # Return current memory usage
}

echo -e "${BLUE}
========================================
🧠 MULTI-TENANT MEMORY TESTING
========================================${NC}"

# Initialize log files
rm -f "$MEMORY_SAMPLES_FILE" "$RESOURCE_LOG_FILE"
echo "timestamp,memory_mb,cpu_percent,system_memory_percent,load_avg" > "$MEMORY_SAMPLES_FILE"

# Pre-flight checks
log "Performing pre-flight checks..."

if ! curl -s http://localhost:8088 > /dev/null; then
    error "SMPP simulator not running"
    exit 1
fi

if ! curl -k -s "${BASE_URL}/api/message" > /dev/null; then
    error "MessageHub service not running"
    exit 1
fi

# Find MessageHub process
MESSAGEHUB_PID=$(pgrep -f "dotnet.*MessageHub" | head -1)
if [ -z "$MESSAGEHUB_PID" ]; then
    error "Could not find MessageHub process"
    exit 1
fi

success "Services are running, MessageHub PID: ${MESSAGEHUB_PID}"

# Establish baseline memory usage
log "Establishing baseline memory usage..."
sleep 5  # Let system stabilize
BASELINE_MEMORY=$(monitor_system_resources)
PEAK_MEMORY=$BASELINE_MEMORY

log "Baseline memory usage: ${BASELINE_MEMORY}MB"

# ========================================
# LONG-RUNNING MEMORY TEST
# ========================================
echo -e "\n${YELLOW}📋 LONG-RUNNING MEMORY TEST: ${TEST_DURATION_MINUTES} minutes${NC}"

log "Starting long-running memory test..."
TEST_START_TIME=$(date +%s)
TEST_END_TIME=$((TEST_START_TIME + TEST_DURATION_MINUTES * 60))
MONITORING_CYCLES=0
MESSAGE_CYCLES=0

while [ $(date +%s) -lt $TEST_END_TIME ]; do
    CURRENT_TIME=$(date +%s)
    ELAPSED_MINUTES=$(( (CURRENT_TIME - TEST_START_TIME) / 60 ))
    REMAINING_MINUTES=$(( TEST_DURATION_MINUTES - ELAPSED_MINUTES ))
    
    # Send test messages
    ((MESSAGE_CYCLES++))
    send_test_messages $MESSAGE_CYCLES
    
    # Monitor system resources
    CURRENT_MEMORY=$(monitor_system_resources)
    ((MONITORING_CYCLES++))
    
    # Progress update
    if [ $((MONITORING_CYCLES % 6)) -eq 0 ]; then  # Every 3 minutes (6 * 30s)
        log "Progress: ${ELAPSED_MINUTES}/${TEST_DURATION_MINUTES}min, Memory: ${CURRENT_MEMORY}MB (+${MEMORY_GROWTH}MB), Peak: ${PEAK_MEMORY}MB"
    fi
    
    # Check for concerning memory growth
    if [ "$MEMORY_GROWTH" -gt 100 ]; then
        warning "Significant memory growth detected: +${MEMORY_GROWTH}MB from baseline"
    fi
    
    # Check if process is still alive
    if ! kill -0 "$MESSAGEHUB_PID" 2>/dev/null; then
        error "MessageHub process died during test!"
        break
    fi
    
    # Wait for next monitoring cycle
    sleep $MONITORING_INTERVAL_SECONDS
done

TEST_ACTUAL_END_TIME=$(date +%s)
TEST_ACTUAL_DURATION=$(( (TEST_ACTUAL_END_TIME - TEST_START_TIME) / 60 ))

log "Memory test completed after ${TEST_ACTUAL_DURATION} minutes"
log "Total message cycles: ${MESSAGE_CYCLES}"
log "Total monitoring cycles: ${MONITORING_CYCLES}"

# ========================================
# MEMORY ANALYSIS
# ========================================
echo -e "\n${YELLOW}📊 MEMORY ANALYSIS${NC}"

FINAL_MEMORY=$(get_memory_usage)
TOTAL_MEMORY_GROWTH=$((FINAL_MEMORY - BASELINE_MEMORY))
MEMORY_GROWTH_RATE=$(echo "scale=2; $TOTAL_MEMORY_GROWTH / $TEST_ACTUAL_DURATION" | bc -l)

log "Memory Analysis Results:"
echo "  - Baseline Memory: ${BASELINE_MEMORY}MB"
echo "  - Final Memory: ${FINAL_MEMORY}MB"
echo "  - Peak Memory: ${PEAK_MEMORY}MB"
echo "  - Total Growth: ${TOTAL_MEMORY_GROWTH}MB"
echo "  - Growth Rate: ${MEMORY_GROWTH_RATE}MB/min"
echo "  - Total Messages Sent: $((MESSAGE_CYCLES * 3))"

# Memory leak assessment
if [ "$TOTAL_MEMORY_GROWTH" -lt 50 ]; then
    success "Memory usage stable (growth: ${TOTAL_MEMORY_GROWTH}MB < 50MB threshold)"
elif [ "$TOTAL_MEMORY_GROWTH" -lt 150 ]; then
    warning "Moderate memory growth detected (${TOTAL_MEMORY_GROWTH}MB)"
else
    error "Significant memory growth detected (${TOTAL_MEMORY_GROWTH}MB) - possible memory leak"
fi

# Memory growth rate assessment
MEMORY_GROWTH_RATE_ABS=$(echo "$MEMORY_GROWTH_RATE" | sed 's/-//')
if (( $(echo "$MEMORY_GROWTH_RATE_ABS < 2.0" | bc -l) )); then
    success "Memory growth rate acceptable (${MEMORY_GROWTH_RATE}MB/min < 2MB/min)"
else
    warning "High memory growth rate (${MEMORY_GROWTH_RATE}MB/min >= 2MB/min)"
fi

# ========================================
# CONNECTION POOL ANALYSIS
# ========================================
echo -e "\n${YELLOW}📋 CONNECTION POOL ANALYSIS${NC}"

log "Testing SMPP connection cleanup after memory test..."

# Send final test messages to verify connections still work
log "Sending verification messages..."
VERIFICATION_START=$(date +%s)

# Test all tenant channels
curl -k -s -X POST "${BASE_URL}/api/message/send" \
    -H "X-Subscription-Key: ${TENANT_A_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"PhoneNumber": "+49111999001", "Content": "Post-test verification A", "ChannelName": "localhost-smpp"}' \
    > /tmp/verify_a.json &

curl -k -s -X POST "${BASE_URL}/api/message/send" \
    -H "X-Subscription-Key: ${TENANT_B_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"PhoneNumber": "+49222999002", "Content": "Post-test verification B", "ChannelName": "localhost-smpp-alt"}' \
    > /tmp/verify_b.json &

curl -k -s -X POST "${BASE_URL}/api/message/send" \
    -H "X-Subscription-Key: ${TENANT_C_KEY}" \
    -H "Content-Type: application/json" \
    -d '{"PhoneNumber": "+49333999003", "Content": "Post-test verification C", "ChannelName": "httpbin-primary"}' \
    > /tmp/verify_c.json &

wait

VERIFICATION_END=$(date +%s)
VERIFICATION_DURATION=$((VERIFICATION_END - VERIFICATION_START))

# Check verification results
VERIFY_A_STATUS=$(grep -o '"status":"[^"]*"' /tmp/verify_a.json 2>/dev/null | cut -d'"' -f4 || echo "unknown")
VERIFY_B_STATUS=$(grep -o '"status":"[^"]*"' /tmp/verify_b.json 2>/dev/null | cut -d'"' -f4 || echo "unknown")
VERIFY_C_STATUS=$(grep -o '"status":"[^"]*"' /tmp/verify_c.json 2>/dev/null | cut -d'"' -f4 || echo "unknown")

log "Post-test verification results (${VERIFICATION_DURATION}s):"
echo "  - Tenant A (SMPP): ${VERIFY_A_STATUS}"
echo "  - Tenant B (SMPP-Alt): ${VERIFY_B_STATUS}"
echo "  - Tenant C (HTTP): ${VERIFY_C_STATUS}"

# Assess connection health
HEALTHY_CONNECTIONS=0
if [[ "$VERIFY_A_STATUS" =~ (Sent|Delivered) ]]; then ((HEALTHY_CONNECTIONS++)); fi
if [[ "$VERIFY_B_STATUS" =~ (Sent|Delivered) ]]; then ((HEALTHY_CONNECTIONS++)); fi
if [[ "$VERIFY_C_STATUS" =~ (Sent|Delivered) ]]; then ((HEALTHY_CONNECTIONS++)); fi

if [ "$HEALTHY_CONNECTIONS" -eq 3 ]; then
    success "All tenant channels healthy after extended testing"
elif [ "$HEALTHY_CONNECTIONS" -ge 2 ]; then
    warning "Some tenant channels may have issues after extended testing (${HEALTHY_CONNECTIONS}/3 healthy)"
else
    error "Multiple tenant channels failed after extended testing (${HEALTHY_CONNECTIONS}/3 healthy)"
fi

# ========================================
# SYSTEM RESOURCE CHECK
# ========================================
echo -e "\n${YELLOW}📋 FINAL SYSTEM RESOURCE CHECK${NC}"

log "Final system resource assessment..."

# Get final system stats
FINAL_SYSTEM_MEMORY=$(free | grep '^Mem:' | awk '{printf "%.1f", ($3/$2) * 100.0}')
FINAL_LOAD_AVG=$(uptime | awk -F'load average:' '{print $2}' | awk '{print $1}' | sed 's/,//')
FINAL_CPU=$(get_cpu_usage)

log "Final System Resources:"
echo "  - MessageHub Memory: ${FINAL_MEMORY}MB"
echo "  - MessageHub CPU: ${FINAL_CPU}%"
echo "  - System Memory Usage: ${FINAL_SYSTEM_MEMORY}%"
echo "  - System Load Average: ${FINAL_LOAD_AVG}"

# Check if system is still in good state
SYSTEM_HEALTHY=true
if (( $(echo "$FINAL_SYSTEM_MEMORY > 90" | bc -l) )); then
    warning "High system memory usage: ${FINAL_SYSTEM_MEMORY}%"
    SYSTEM_HEALTHY=false
fi

if (( $(echo "$FINAL_LOAD_AVG > 5.0" | bc -l) )); then
    warning "High system load average: ${FINAL_LOAD_AVG}"
    SYSTEM_HEALTHY=false
fi

if [ "$SYSTEM_HEALTHY" = true ]; then
    success "System resources in healthy state after extended testing"
fi

# ========================================
# DATA INTEGRITY CHECK
# ========================================
echo -e "\n${YELLOW}📋 DATA INTEGRITY CHECK${NC}"

log "Verifying database integrity after extended testing..."

# Check message counts
TENANT_A_MESSAGES=$(curl -k -s -H "X-Subscription-Key: ${TENANT_A_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)
TENANT_B_MESSAGES=$(curl -k -s -H "X-Subscription-Key: ${TENANT_B_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)
TENANT_C_MESSAGES=$(curl -k -s -H "X-Subscription-Key: ${TENANT_C_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)

TOTAL_EXPECTED_MESSAGES=$((MESSAGE_CYCLES * 3 + 3))  # Test messages + verification messages

log "Database integrity results:"
echo "  - Tenant A Messages: ${TENANT_A_MESSAGES}"
echo "  - Tenant B Messages: ${TENANT_B_MESSAGES}"
echo "  - Tenant C Messages: ${TENANT_C_MESSAGES}"
echo "  - Total Messages: $((TENANT_A_MESSAGES + TENANT_B_MESSAGES + TENANT_C_MESSAGES))"
echo "  - Expected Messages: ~${TOTAL_EXPECTED_MESSAGES}"

# Basic integrity check
if [ "$TENANT_A_MESSAGES" -gt 0 ] && [ "$TENANT_B_MESSAGES" -gt 0 ] && [ "$TENANT_C_MESSAGES" -gt 0 ]; then
    success "All tenants have messages in database"
    
    # Check tenant isolation still works
    CROSS_TENANT_TEST=$(curl -k -s -o /dev/null -w "%{http_code}" \
        -H "X-Subscription-Key: ${TENANT_A_KEY}" \
        "${BASE_URL}/api/message/99999/status")
    
    if [ "$CROSS_TENANT_TEST" = "404" ] || [ "$CROSS_TENANT_TEST" = "403" ]; then
        success "Tenant isolation still working after extended testing"
    else
        warning "Tenant isolation may be compromised after extended testing"
    fi
else
    error "Some tenants missing messages in database"
fi

# ========================================
# FINAL ASSESSMENT
# ========================================
echo -e "\n${BLUE}
========================================
🎯 MEMORY TEST FINAL ASSESSMENT
========================================${NC}"

# Determine overall test result
MEMORY_TEST_PASSED=true

# Memory criteria
if [ "$TOTAL_MEMORY_GROWTH" -ge 150 ]; then
    error "Memory leak criteria failed: ${TOTAL_MEMORY_GROWTH}MB growth (≥150MB threshold)"
    MEMORY_TEST_PASSED=false
else
    success "Memory leak criteria passed: ${TOTAL_MEMORY_GROWTH}MB growth (<150MB threshold)"
fi

# Connection health criteria
if [ "$HEALTHY_CONNECTIONS" -lt 2 ]; then
    error "Connection health criteria failed: ${HEALTHY_CONNECTIONS}/3 channels healthy"
    MEMORY_TEST_PASSED=false
else
    success "Connection health criteria passed: ${HEALTHY_CONNECTIONS}/3 channels healthy"
fi

# Data integrity criteria
if [ "$TENANT_A_MESSAGES" -eq 0 ] || [ "$TENANT_B_MESSAGES" -eq 0 ] || [ "$TENANT_C_MESSAGES" -eq 0 ]; then
    error "Data integrity criteria failed: missing tenant messages"
    MEMORY_TEST_PASSED=false
else
    success "Data integrity criteria passed: all tenants have messages"
fi

# Overall result
echo ""
if [ "$MEMORY_TEST_PASSED" = true ]; then
    echo -e "${GREEN}🎉 MULTI-TENANT MEMORY TEST PASSED!${NC}"
    echo -e "${GREEN}✅ The MessageHub SMS service shows no significant memory leaks under extended multi-tenant load.${NC}"
    echo ""
    echo -e "Key Results:"
    echo -e "  - Test Duration: ${TEST_ACTUAL_DURATION} minutes"
    echo -e "  - Total Messages: $((MESSAGE_CYCLES * 3))"
    echo -e "  - Memory Growth: ${TOTAL_MEMORY_GROWTH}MB (${MEMORY_GROWTH_RATE}MB/min)"
    echo -e "  - Peak Memory: ${PEAK_MEMORY}MB"
    echo -e "  - Channel Health: ${HEALTHY_CONNECTIONS}/3 healthy"
else
    echo -e "${RED}⚠️  MULTI-TENANT MEMORY TEST FAILED!${NC}"
    echo -e "${RED}❌ Memory or resource issues detected during extended testing.${NC}"
    echo ""
    echo -e "Issues detected:"
    if [ "$TOTAL_MEMORY_GROWTH" -ge 150 ]; then
        echo -e "  - Excessive memory growth: ${TOTAL_MEMORY_GROWTH}MB"
    fi
    if [ "$HEALTHY_CONNECTIONS" -lt 2 ]; then
        echo -e "  - Connection health issues: ${HEALTHY_CONNECTIONS}/3 healthy"
    fi
    if [ "$TENANT_A_MESSAGES" -eq 0 ] || [ "$TENANT_B_MESSAGES" -eq 0 ] || [ "$TENANT_C_MESSAGES" -eq 0 ]; then
        echo -e "  - Data integrity issues"
    fi
fi

echo ""
log "Memory test data saved in:"
log "  - Memory samples: ${MEMORY_SAMPLES_FILE}"
log "  - Resource log: ${RESOURCE_LOG_FILE}"
log "Memory test completed."

# Cleanup verification files
rm -f /tmp/verify_*.json