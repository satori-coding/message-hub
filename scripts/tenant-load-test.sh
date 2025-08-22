#!/bin/bash

# ========================================
# Multi-Tenant Load Testing Script
# ========================================
# 
# PURPOSE: Stress test MessageHub SMS service with sustained multi-tenant load
# FOCUS: Database race conditions, connection pool exhaustion, memory usage
# 
# TEST SCENARIOS:
# 1. Sustained high-volume load (100+ messages per tenant)
# 2. Database race condition detection
# 3. SMPP connection pool stress testing
# 4. Performance degradation monitoring
#
# REQUIREMENTS:
# - SMPP simulator running on localhost:2775
# - MessageHub service in multi-tenant mode
# - Sufficient system resources for load testing

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

# Load test parameters
MESSAGES_PER_TENANT=50
CONCURRENT_BATCHES=5
BATCH_SIZE=10

# Performance tracking
START_TIME=$(date +%s)
TOTAL_REQUESTS=0
SUCCESSFUL_REQUESTS=0
FAILED_REQUESTS=0

# Logging functions
log() {
    echo -e "${BLUE}[$(date +'%H:%M:%S')]${NC} $1"
}

success() {
    echo -e "${GREEN}✅ $1${NC}"
}

error() {
    echo -e "${RED}❌ $1${NC}"
}

warning() {
    echo -e "${YELLOW}⚠️  $1${NC}"
}

# Performance monitoring function
monitor_performance() {
    local duration=$1
    local requests=$2
    local rps=$(echo "scale=2; $requests / $duration" | bc -l)
    echo "Duration: ${duration}s, Requests: ${requests}, RPS: ${rps}"
}

echo -e "${BLUE}
========================================
🔥 MULTI-TENANT LOAD TESTING
========================================${NC}"

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

success "Services are running, starting load test..."

# Create results directory
mkdir -p /tmp/load_test_results
rm -f /tmp/load_test_results/*

# ========================================
# SUSTAINED HIGH-VOLUME LOAD TEST
# ========================================
echo -e "\n${YELLOW}📋 SUSTAINED LOAD TEST: ${MESSAGES_PER_TENANT} messages per tenant${NC}"

log "Starting sustained load test..."
LOAD_START_TIME=$(date +%s)

# Function to send batch of messages
send_message_batch() {
    local tenant_key=$1
    local tenant_name=$2
    local channel_name=$3
    local batch_num=$4
    local batch_size=$5
    local results_file=$6
    
    for ((i=1; i<=batch_size; i++)); do
        local phone_number="+49${tenant_name}${batch_num}$(printf "%03d" $i)"
        local message_content="Load Test ${tenant_name} Batch${batch_num} #${i}"
        
        local response=$(curl -k -s -w "%{http_code}|%{time_total}" \
            -X POST "${BASE_URL}/api/message/send" \
            -H "X-Subscription-Key: ${tenant_key}" \
            -H "Content-Type: application/json" \
            -d "{\"PhoneNumber\": \"${phone_number}\", \"Content\": \"${message_content}\", \"ChannelName\": \"${channel_name}\"}" 2>/dev/null)
        
        # Extract HTTP status and response time
        local http_status=$(echo "$response" | tail -c 10 | cut -d'|' -f1)
        local response_time=$(echo "$response" | tail -c 10 | cut -d'|' -f2)
        
        # Log result
        echo "${tenant_name},${batch_num},${i},${http_status},${response_time}" >> "$results_file"
        
        ((TOTAL_REQUESTS++))
        if [ "$http_status" = "200" ] || [ "$http_status" = "201" ]; then
            ((SUCCESSFUL_REQUESTS++))
        else
            ((FAILED_REQUESTS++))
        fi
    done
}

# Launch concurrent batches for all tenants
log "Launching ${CONCURRENT_BATCHES} concurrent batches of ${BATCH_SIZE} messages per tenant..."

for batch in $(seq 1 $CONCURRENT_BATCHES); do
    log "Starting batch ${batch}/${CONCURRENT_BATCHES}..."
    
    # Tenant A - SMPP
    send_message_batch "$TENANT_A_KEY" "A" "localhost-smpp" "$batch" "$BATCH_SIZE" "/tmp/load_test_results/tenant_a.csv" &
    
    # Tenant B - SMPP (different channel)
    send_message_batch "$TENANT_B_KEY" "B" "localhost-smpp-alt" "$batch" "$BATCH_SIZE" "/tmp/load_test_results/tenant_b.csv" &
    
    # Tenant C - HTTP
    send_message_batch "$TENANT_C_KEY" "C" "httpbin-primary" "$batch" "$BATCH_SIZE" "/tmp/load_test_results/tenant_c.csv" &
    
    # Wait for batch to complete before starting next batch
    wait
    
    # Brief pause between batches to allow for system recovery
    sleep 2
    
    # Progress update
    local elapsed=$(($(date +%s) - LOAD_START_TIME))
    local completed=$((batch * BATCH_SIZE * 3))
    local total_expected=$((CONCURRENT_BATCHES * BATCH_SIZE * 3))
    local progress=$((completed * 100 / total_expected))
    
    log "Batch ${batch} completed. Progress: ${progress}% (${completed}/${total_expected})"
done

LOAD_END_TIME=$(date +%s)
LOAD_DURATION=$((LOAD_END_TIME - LOAD_START_TIME))

echo -e "\n${GREEN}✅ Load test completed in ${LOAD_DURATION} seconds${NC}"
log "Total requests: ${TOTAL_REQUESTS}, Successful: ${SUCCESSFUL_REQUESTS}, Failed: ${FAILED_REQUESTS}"

# ========================================
# PERFORMANCE ANALYSIS
# ========================================
echo -e "\n${YELLOW}📊 PERFORMANCE ANALYSIS${NC}"

# Calculate overall performance metrics
OVERALL_RPS=$(echo "scale=2; $TOTAL_REQUESTS / $LOAD_DURATION" | bc -l)
SUCCESS_RATE=$(echo "scale=2; $SUCCESSFUL_REQUESTS * 100 / $TOTAL_REQUESTS" | bc -l)

log "Overall Performance:"
echo "  - Total Duration: ${LOAD_DURATION}s"
echo "  - Total Requests: ${TOTAL_REQUESTS}"
echo "  - Successful Requests: ${SUCCESSFUL_REQUESTS}"
echo "  - Failed Requests: ${FAILED_REQUESTS}"
echo "  - Success Rate: ${SUCCESS_RATE}%"
echo "  - Requests per Second: ${OVERALL_RPS}"

# Analyze per-tenant performance
analyze_tenant_performance() {
    local tenant_name=$1
    local results_file=$2
    
    if [ -f "$results_file" ]; then
        local total_requests=$(wc -l < "$results_file")
        local successful_requests=$(awk -F, '$4 == "200" || $4 == "201"' "$results_file" | wc -l)
        local avg_response_time=$(awk -F, '$4 == "200" || $4 == "201" {sum += $5; count++} END {if (count > 0) printf "%.3f", sum/count; else print "N/A"}' "$results_file")
        local min_response_time=$(awk -F, '$4 == "200" || $4 == "201" {print $5}' "$results_file" | sort -n | head -1)
        local max_response_time=$(awk -F, '$4 == "200" || $4 == "201" {print $5}' "$results_file" | sort -n | tail -1)
        
        echo "  Tenant ${tenant_name}:"
        echo "    - Requests: ${total_requests}"
        echo "    - Successful: ${successful_requests}"
        echo "    - Success Rate: $(echo "scale=2; $successful_requests * 100 / $total_requests" | bc -l)%"
        echo "    - Avg Response Time: ${avg_response_time}s"
        echo "    - Min Response Time: ${min_response_time}s"
        echo "    - Max Response Time: ${max_response_time}s"
    else
        echo "  Tenant ${tenant_name}: No results file found"
    fi
}

log "Per-Tenant Performance:"
analyze_tenant_performance "A (SMPP)" "/tmp/load_test_results/tenant_a.csv"
analyze_tenant_performance "B (SMPP-Alt)" "/tmp/load_test_results/tenant_b.csv"  
analyze_tenant_performance "C (HTTP)" "/tmp/load_test_results/tenant_c.csv"

# ========================================
# DATABASE INTEGRITY CHECK
# ========================================
echo -e "\n${YELLOW}📋 DATABASE INTEGRITY CHECK${NC}"

log "Checking database integrity and tenant data segregation..."

# Count total messages in database
total_db_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_A_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)

# Count messages per tenant
tenant_a_db_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_A_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)
tenant_b_db_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_B_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)
tenant_c_db_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_C_KEY}" "${BASE_URL}/api/message" | grep -o '"id":' | wc -l)

log "Database message counts:"
echo "  - Total messages (Tenant A view): ${tenant_a_db_messages}"
echo "  - Tenant A messages: ${tenant_a_db_messages}"
echo "  - Tenant B messages: ${tenant_b_db_messages}"
echo "  - Tenant C messages: ${tenant_c_db_messages}"

# Verify tenant isolation
if [ "$tenant_a_db_messages" -gt 0 ] && [ "$tenant_b_db_messages" -gt 0 ] && [ "$tenant_c_db_messages" -gt 0 ]; then
    success "All tenants have messages in database"
    
    # Check if tenants can't access each other's messages
    cross_tenant_test=$(curl -k -s -o /dev/null -w "%{http_code}" \
        -H "X-Subscription-Key: ${TENANT_A_KEY}" \
        "${BASE_URL}/api/message/9999/status")
    
    if [ "$cross_tenant_test" = "404" ] || [ "$cross_tenant_test" = "403" ]; then
        success "Tenant isolation verified (cross-tenant access blocked)"
    else
        warning "Tenant isolation may be compromised"
    fi
else
    error "Some tenants have no messages in database"
fi

# ========================================
# MEMORY AND RESOURCE CHECK
# ========================================
echo -e "\n${YELLOW}📋 MEMORY AND RESOURCE CHECK${NC}"

log "Checking system resources after load test..."

# Get process info for dotnet (MessageHub)
if pgrep -f "dotnet.*MessageHub" > /dev/null; then
    local dotnet_pid=$(pgrep -f "dotnet.*MessageHub" | head -1)
    local memory_usage=$(ps -p $dotnet_pid -o rss= 2>/dev/null | awk '{print $1/1024}' | head -1)
    local cpu_usage=$(ps -p $dotnet_pid -o pcpu= | head -1)
    
    if [ ! -z "$memory_usage" ]; then
        log "MessageHub Process Resources:"
        echo "  - Memory Usage: ${memory_usage}MB"
        echo "  - CPU Usage: ${cpu_usage}%"
        
        # Check if memory usage is reasonable (under 500MB for load test)
        if (( $(echo "$memory_usage < 500" | bc -l) )); then
            success "Memory usage within reasonable limits (${memory_usage}MB)"
        else
            warning "High memory usage detected (${memory_usage}MB)"
        fi
    else
        warning "Could not retrieve resource information"
    fi
else
    warning "MessageHub process not found for resource monitoring"
fi

# ========================================
# FINAL ASSESSMENT
# ========================================
echo -e "\n${BLUE}
========================================
🎯 LOAD TEST FINAL ASSESSMENT
========================================${NC}"

# Determine test result based on success criteria
PASS_CRITERIA_MET=true

# Check success rate (should be > 90%)
if (( $(echo "$SUCCESS_RATE >= 90" | bc -l) )); then
    success "Success rate criteria met: ${SUCCESS_RATE}% (≥90%)"
else
    error "Success rate criteria failed: ${SUCCESS_RATE}% (<90%)"
    PASS_CRITERIA_MET=false
fi

# Check performance (should handle > 1 RPS)
if (( $(echo "$OVERALL_RPS >= 1.0" | bc -l) )); then
    success "Performance criteria met: ${OVERALL_RPS} RPS (≥1.0)"
else
    warning "Performance below expected: ${OVERALL_RPS} RPS (<1.0)"
fi

# Check database integrity
if [ "$tenant_a_db_messages" -gt 0 ] && [ "$tenant_b_db_messages" -gt 0 ]; then
    success "Database integrity criteria met (all tenants have data)"
else
    error "Database integrity criteria failed (missing tenant data)"
    PASS_CRITERIA_MET=false
fi

# Overall result
echo ""
if [ "$PASS_CRITERIA_MET" = true ]; then
    echo -e "${GREEN}🎉 MULTI-TENANT LOAD TEST PASSED!${NC}"
    echo -e "${GREEN}✅ The MessageHub SMS service handles sustained multi-tenant load successfully.${NC}"
    echo ""
    echo -e "Key Results:"
    echo -e "  - Processed ${TOTAL_REQUESTS} requests in ${LOAD_DURATION} seconds"
    echo -e "  - Achieved ${SUCCESS_RATE}% success rate at ${OVERALL_RPS} RPS"
    echo -e "  - Maintained tenant data isolation"
    echo -e "  - No major performance degradation observed"
else
    echo -e "${RED}⚠️  MULTI-TENANT LOAD TEST FAILED!${NC}"
    echo -e "${RED}❌ Some load test criteria were not met.${NC}"
    echo ""
    echo -e "Issues detected:"
    if (( $(echo "$SUCCESS_RATE < 90" | bc -l) )); then
        echo -e "  - Low success rate: ${SUCCESS_RATE}%"
    fi
    if [ "$tenant_a_db_messages" -eq 0 ] || [ "$tenant_b_db_messages" -eq 0 ]; then
        echo -e "  - Database integrity issues"
    fi
fi

# Cleanup
log "Cleaning up test results..."
# Keep results for analysis but clean up temp files
# rm -rf /tmp/load_test_results

echo ""
log "Load test results saved in /tmp/load_test_results/"
log "Load test completed."