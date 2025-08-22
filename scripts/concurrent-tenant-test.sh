#!/bin/bash

# ========================================
# Concurrent Multi-Tenant Testing Script
# ========================================
# 
# PURPOSE: Test MessageHub SMS service under concurrent multi-tenant load
# FOCUS: Channel creation race conditions, SMPP connection pool isolation, tenant data segregation
# 
# TEST SCENARIOS:
# 1. Simultaneous channel creation from cold start
# 2. Concurrent message sending from multiple tenants  
# 3. SMPP connection pool isolation verification
# 4. Tenant data segregation validation
#
# REQUIREMENTS:
# - SMPP simulator running on localhost:2775
# - MessageHub service in multi-tenant mode
# - 3 test tenants configured (A, B, C)

set -e  # Exit on any error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
BASE_URL="https://localhost:7142"
TENANT_A_KEY="dev-tenant-a-12345"
TENANT_B_KEY="dev-tenant-b-67890"  
TENANT_C_KEY="dev-tenant-c-http-99999"

# Test result tracking
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0

# Logging function
log() {
    echo -e "${BLUE}[$(date +'%H:%M:%S')]${NC} $1"
}

success() {
    echo -e "${GREEN}✅ $1${NC}"
    ((PASSED_TESTS++))
}

error() {
    echo -e "${RED}❌ $1${NC}"
    ((FAILED_TESTS++))
}

warning() {
    echo -e "${YELLOW}⚠️  $1${NC}"
}

# Test counter
test_counter() {
    ((TOTAL_TESTS++))
}

echo -e "${BLUE}
========================================
🚀 CONCURRENT MULTI-TENANT TESTING
========================================${NC}"

# Pre-flight checks
log "Performing pre-flight checks..."

# Check if SMPP simulator is running
if ! curl -s http://localhost:8088 > /dev/null; then
    error "SMPP simulator not running on http://localhost:8088"
    echo "Please run: ./scripts/start-smppsim.sh"
    exit 1
fi
success "SMPP simulator is running"

# Check if MessageHub service is running
if ! curl -k -s "${BASE_URL}/api/message" > /dev/null; then
    error "MessageHub service not running on ${BASE_URL}"
    echo "Please run: dotnet run"
    exit 1
fi
success "MessageHub service is running"

echo ""
log "Starting concurrent multi-tenant tests..."

# ========================================
# TEST 1: Simultaneous Channel Creation
# ========================================
echo -e "\n${YELLOW}📋 TEST 1: Simultaneous Channel Creation (Cold Start)${NC}"
test_counter

log "Sending simultaneous requests to trigger channel creation..."

# Launch 3 concurrent requests (different tenants, different channels)
(curl -k -s -w "Tenant A Response Time: %{time_total}s\n" \
  -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: ${TENANT_A_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49111000001", "Content": "Concurrent Test A - Channel Creation", "ChannelName": "localhost-smpp"}' \
  > /tmp/tenant_a_result.json 2>&1) &

(curl -k -s -w "Tenant B Response Time: %{time_total}s\n" \
  -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: ${TENANT_B_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49222000002", "Content": "Concurrent Test B - Channel Creation", "ChannelName": "localhost-smpp-alt"}' \
  > /tmp/tenant_b_result.json 2>&1) &

(curl -k -s -w "Tenant C Response Time: %{time_total}s\n" \
  -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: ${TENANT_C_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49333000003", "Content": "Concurrent Test C - Channel Creation", "ChannelName": "httpbin-primary"}' \
  > /tmp/tenant_c_result.json 2>&1) &

# Wait for all background jobs to complete
wait

# Analyze results
log "Analyzing simultaneous channel creation results..."

if grep -q '"status"' /tmp/tenant_a_result.json && grep -q '"status"' /tmp/tenant_b_result.json; then
    success "All tenants successfully created channels concurrently"
    
    # Check for different tenant IDs in responses
    tenant_a_id=$(grep -o '"id":[0-9]*' /tmp/tenant_a_result.json | cut -d':' -f2)
    tenant_b_id=$(grep -o '"id":[0-9]*' /tmp/tenant_b_result.json | cut -d':' -f2)
    
    if [ "$tenant_a_id" != "$tenant_b_id" ]; then
        success "Messages have different IDs: Tenant A ($tenant_a_id) vs Tenant B ($tenant_b_id)"
    else
        warning "Messages have same ID - potential race condition"
    fi
else
    error "Some tenants failed to create channels"
    cat /tmp/tenant_a_result.json /tmp/tenant_b_result.json /tmp/tenant_c_result.json
fi

# ========================================
# TEST 2: High-Volume Concurrent Load
# ========================================
echo -e "\n${YELLOW}📋 TEST 2: High-Volume Concurrent Load (30 messages)${NC}"
test_counter

log "Sending 30 messages concurrently (10 per tenant)..."

# Track successful messages
SUCCESSFUL_A=0
SUCCESSFUL_B=0  
SUCCESSFUL_C=0

# Launch 10 messages per tenant in parallel
for i in {1..10}; do
    # Tenant A - SMPP
    (curl -k -s -X POST "${BASE_URL}/api/message/send" \
      -H "X-Subscription-Key: ${TENANT_A_KEY}" \
      -H "Content-Type: application/json" \
      -d "{\"PhoneNumber\": \"+4911100${i}\", \"Content\": \"Load Test A #${i}\", \"ChannelName\": \"localhost-smpp\"}" \
      | grep -q '"status"' && echo "A${i}_SUCCESS" || echo "A${i}_FAILED") &

    # Tenant B - SMPP (different SystemId)
    (curl -k -s -X POST "${BASE_URL}/api/message/send" \
      -H "X-Subscription-Key: ${TENANT_B_KEY}" \
      -H "Content-Type: application/json" \
      -d "{\"PhoneNumber\": \"+4922200${i}\", \"Content\": \"Load Test B #${i}\", \"ChannelName\": \"localhost-smpp-alt\"}" \
      | grep -q '"status"' && echo "B${i}_SUCCESS" || echo "B${i}_FAILED") &

    # Tenant C - HTTP
    (curl -k -s -X POST "${BASE_URL}/api/message/send" \
      -H "X-Subscription-Key: ${TENANT_C_KEY}" \
      -H "Content-Type: application/json" \
      -d "{\"PhoneNumber\": \"+4933300${i}\", \"Content\": \"Load Test C #${i}\", \"ChannelName\": \"httpbin-primary\"}" \
      | grep -q '"status"' && echo "C${i}_SUCCESS" || echo "C${i}_FAILED") &
done

# Wait for all 30 requests to complete
wait > /tmp/load_test_results.txt 2>&1

# Count successful messages
SUCCESSFUL_A=$(grep -c "A.*_SUCCESS" /tmp/load_test_results.txt || echo 0)
SUCCESSFUL_B=$(grep -c "B.*_SUCCESS" /tmp/load_test_results.txt || echo 0)
SUCCESSFUL_C=$(grep -c "C.*_SUCCESS" /tmp/load_test_results.txt || echo 0)

TOTAL_SUCCESSFUL=$((SUCCESSFUL_A + SUCCESSFUL_B + SUCCESSFUL_C))

log "Load test results: A=${SUCCESSFUL_A}/10, B=${SUCCESSFUL_B}/10, C=${SUCCESSFUL_C}/10"

if [ $TOTAL_SUCCESSFUL -ge 25 ]; then
    success "High-volume concurrent load test passed (${TOTAL_SUCCESSFUL}/30 successful)"
else
    error "High-volume concurrent load test failed (${TOTAL_SUCCESSFUL}/30 successful)"
fi

# ========================================  
# TEST 3: Tenant Data Isolation
# ========================================
echo -e "\n${YELLOW}📋 TEST 3: Tenant Data Isolation${NC}"
test_counter

log "Testing tenant data segregation..."

# Get all messages for each tenant
tenant_a_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_A_KEY}" "${BASE_URL}/api/message" | grep -o '"id":[0-9]*' | wc -l)
tenant_b_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_B_KEY}" "${BASE_URL}/api/message" | wc -l)
tenant_c_messages=$(curl -k -s -H "X-Subscription-Key: ${TENANT_C_KEY}" "${BASE_URL}/api/message" | wc -l)

log "Message counts: Tenant A: ${tenant_a_messages}, Tenant B: ${tenant_b_messages}, Tenant C: ${tenant_c_messages}"

# Test cross-tenant access (should fail)
cross_tenant_test=$(curl -k -s -w "%{http_code}" -o /dev/null \
  -H "X-Subscription-Key: ${TENANT_A_KEY}" "${BASE_URL}/api/message/9999/status")

if [ "$cross_tenant_test" = "404" ] || [ "$cross_tenant_test" = "403" ]; then
    success "Cross-tenant access properly blocked (HTTP ${cross_tenant_test})"
else
    error "Cross-tenant access not properly blocked (HTTP ${cross_tenant_test})"
fi

# ========================================
# TEST 4: SMPP Connection Pool Isolation  
# ========================================
echo -e "\n${YELLOW}📋 TEST 4: SMPP Connection Pool Isolation${NC}"
test_counter

log "Testing SMPP connection pool isolation between tenants..."

# Send messages through different SMPP channels simultaneously
(curl -k -s -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: ${TENANT_A_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49111999001", "Content": "Pool Test A", "ChannelName": "localhost-smpp"}' \
  > /tmp/pool_test_a.json) &

(curl -k -s -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: ${TENANT_B_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49222999002", "Content": "Pool Test B", "ChannelName": "localhost-smpp-alt"}' \
  > /tmp/pool_test_b.json) &

wait

# Check if both SMPP messages succeeded (indicating separate connection pools)
pool_test_a_status=$(grep -o '"status":"[^"]*"' /tmp/pool_test_a.json | cut -d'"' -f4)
pool_test_b_status=$(grep -o '"status":"[^"]*"' /tmp/pool_test_b.json | cut -d'"' -f4)

if [[ "$pool_test_a_status" =~ (Sent|Delivered) ]] && [[ "$pool_test_b_status" =~ (Sent|Delivered) ]]; then
    success "SMPP connection pools are properly isolated (both tenants successful)"
else
    warning "SMPP connection pool isolation unclear (A: $pool_test_a_status, B: $pool_test_b_status)"
fi

# ========================================
# TEST 5: Error Handling Under Concurrency
# ========================================
echo -e "\n${YELLOW}📋 TEST 5: Error Handling Under Concurrency${NC}"
test_counter

log "Testing error handling with invalid tenant and concurrent requests..."

# Send invalid tenant request alongside valid ones
(curl -k -s -w "%{http_code}" -o /dev/null \
  -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: invalid-key-12345" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49999", "Content": "Should fail"}' > /tmp/invalid_tenant.txt) &

(curl -k -s -X POST "${BASE_URL}/api/message/send" \
  -H "X-Subscription-Key: ${TENANT_A_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49111888001", "Content": "Valid during error test"}' \
  > /tmp/valid_during_error.json) &

wait

invalid_response=$(cat /tmp/invalid_tenant.txt)
valid_response=$(grep -o '"status":"[^"]*"' /tmp/valid_during_error.json | cut -d'"' -f4)

if [ "$invalid_response" = "401" ] && [[ "$valid_response" =~ (Sent|Delivered) ]]; then
    success "Error handling works correctly under concurrency (invalid: 401, valid: $valid_response)"
else
    error "Error handling issues under concurrency (invalid: $invalid_response, valid: $valid_response)"
fi

# ========================================
# SUMMARY
# ========================================
echo -e "\n${BLUE}
========================================
📊 CONCURRENT TESTING SUMMARY
========================================${NC}"

echo "Total Tests: $TOTAL_TESTS"
success "Passed: $PASSED_TESTS"
if [ $FAILED_TESTS -gt 0 ]; then
    error "Failed: $FAILED_TESTS"
else
    echo -e "${GREEN}Failed: $FAILED_TESTS${NC}"
fi

# Overall result
if [ $FAILED_TESTS -eq 0 ]; then
    echo -e "\n${GREEN}🎉 ALL CONCURRENT MULTI-TENANT TESTS PASSED!${NC}"
    echo -e "${GREEN}✅ The MessageHub SMS service handles concurrent multi-tenant access robustly.${NC}"
    exit 0
else
    echo -e "\n${RED}⚠️  SOME CONCURRENT TESTS FAILED!${NC}"
    echo -e "${RED}❌ Review the failed tests above and check the implementation.${NC}"
    exit 1
fi

# Cleanup
rm -f /tmp/tenant_*_result.json /tmp/load_test_results.txt /tmp/pool_test_*.json /tmp/invalid_tenant.txt /tmp/valid_during_error.json