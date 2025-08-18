#!/bin/bash

# Performance Test Script for MessageHub SMPP Service
# Tests connection pooling, concurrent requests, and service behavior

echo "🚀 MessageHub SMPP Service Performance Test"
echo "=========================================="
echo "Test Date: $(date)"
echo "Local SMPP Simulator: localhost:2775"
echo ""

# Configuration
API_URL="https://localhost:7142/api/sms/send"
BASE_PHONE="+4366412345"
RESULTS_FILE="performance-results-$(date +%Y%m%d-%H%M%S).txt"

# Function to send SMS and measure time
send_sms() {
    local phone="$1"
    local message="$2" 
    local test_name="$3"
    
    echo "[$test_name] Sending SMS to $phone..."
    
    start_time=$(date +%s.%3N)
    
    response=$(curl -k -s -w "\n%{http_code}\n%{time_total}" -X POST "$API_URL" \
        -H "Content-Type: application/json" \
        -d "{\"PhoneNumber\": \"$phone\", \"Content\": \"$message\"}")
    
    end_time=$(date +%s.%3N)
    
    # Parse response
    body=$(echo "$response" | head -n 1)
    http_code=$(echo "$response" | sed -n '2p')
    curl_time=$(echo "$response" | sed -n '3p')
    
    # Calculate total time
    total_time=$(echo "$end_time - $start_time" | bc)
    
    # Extract status and ID from JSON response
    if command -v jq &> /dev/null; then
        status=$(echo "$body" | jq -r '.status // "unknown"')
        sms_id=$(echo "$body" | jq -r '.id // "unknown"')
    else
        # Fallback parsing without jq
        status=$(echo "$body" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)
        sms_id=$(echo "$body" | grep -o '"id":[^,}]*' | cut -d':' -f2)
    fi
    
    # Log results
    result="[$test_name] SMS ID: $sms_id | Status: $status | HTTP: $http_code | Time: ${total_time}s | Curl: ${curl_time}s"
    echo "$result"
    echo "$result" >> "$RESULTS_FILE"
    
    return 0
}

# Function to check if service is running
check_service() {
    echo "🔍 Checking if MessageHub service is running..."
    
    if curl -k -s "$API_URL" > /dev/null 2>&1; then
        echo "❌ Service check: API endpoint not responding"
        echo "Please start the MessageHub service first:"
        echo "  cd /home/matthias/projects/message-hub-server"
        echo "  dotnet run"
        return 1
    else
        echo "✅ Service appears to be running"
        return 0
    fi
}

# Function to run concurrent tests
run_concurrent_test() {
    local count=$1
    echo ""
    echo "🔄 Running $count concurrent SMS requests..."
    echo "Testing connection pooling under load..."
    
    pids=()
    
    for i in $(seq 1 $count); do
        phone="${BASE_PHONE}$(printf "%02d" $i)"
        message="Concurrent test #$i - $(date +%H:%M:%S.%3N)"
        (send_sms "$phone" "$message" "CONCURRENT-$i") &
        pids+=($!)
        sleep 0.1  # Small delay to avoid overwhelming
    done
    
    echo "Waiting for all concurrent requests to complete..."
    for pid in "${pids[@]}"; do
        wait "$pid"
    done
    
    echo "✅ All concurrent requests completed"
}

# Main test execution
main() {
    echo "Starting performance tests..." > "$RESULTS_FILE"
    echo "Test timestamp: $(date)" >> "$RESULTS_FILE"
    echo "==============================" >> "$RESULTS_FILE"
    
    # Test 1: Single SMS (cold start - first connection)
    echo ""
    echo "📋 Test 1: Cold Start (First Connection)"
    echo "This should take ~250-300ms for connection setup + binding"
    send_sms "${BASE_PHONE}01" "Cold start test - first connection to SMPP pool" "COLD-START"
    
    sleep 2
    
    # Test 2: Warm connection (should reuse pool)
    echo ""
    echo "📋 Test 2: Warm Connection (Pool Reuse)"
    echo "This should take ~15-25ms using existing connection"
    send_sms "${BASE_PHONE}02" "Warm connection test - reusing pooled connection" "WARM-CONNECTION"
    
    sleep 1
    
    # Test 3: Sequential requests (testing pool efficiency)
    echo ""
    echo "📋 Test 3: Sequential Requests (Pool Efficiency)"
    for i in {3..7}; do
        phone="${BASE_PHONE}$(printf "%02d" $i)"
        message="Sequential test #$i - pool efficiency test"
        send_sms "$phone" "$message" "SEQUENTIAL-$i"
        sleep 0.5
    done
    
    sleep 2
    
    # Test 4: Burst test (rapid sequential)
    echo ""
    echo "📋 Test 4: Burst Test (Rapid Sequential)"
    echo "Testing rapid-fire requests without delays"
    for i in {8..12}; do
        phone="${BASE_PHONE}$(printf "%02d" $i)"
        message="Burst test #$i - rapid fire"
        send_sms "$phone" "$message" "BURST-$i"
    done
    
    sleep 3
    
    # Test 5: Concurrent requests
    echo ""
    echo "📋 Test 5: Concurrent Load Test"
    run_concurrent_test 5
    
    sleep 3
    
    # Test 6: Long message test
    echo ""
    echo "📋 Test 6: Long Message Test"
    long_message="Long message performance test: $(date) - This message is intentionally longer to test how the SMPP service handles messages with more content. Testing character limits and encoding. The SMS service should handle this efficiently through the connection pool. Message contains: äöü special characters and numbers 12345."
    send_sms "${BASE_PHONE}99" "$long_message" "LONG-MESSAGE"
    
    # Test 7: Final connection test
    echo ""
    echo "📋 Test 7: Final Connection Test"
    echo "Verifying pool is still healthy after all tests"
    send_sms "${BASE_PHONE}00" "Final test - connection pool health check after load testing" "FINAL-TEST"
    
    # Summary
    echo ""
    echo "📊 TEST SUMMARY"
    echo "==============="
    echo "Total tests completed: All test scenarios executed"
    echo "Results saved to: $RESULTS_FILE"
    echo ""
    echo "Expected results:"
    echo "  • First SMS: ~250-300ms (connection setup)"
    echo "  • Subsequent SMS: ~15-25ms (pool reuse)" 
    echo "  • All SMS should have status 'Sent'"
    echo "  • HTTP status should be 200"
    echo ""
    echo "🔍 Quick analysis:"
    if [ -f "$RESULTS_FILE" ]; then
        echo "SMS with 'Sent' status: $(grep -c 'Status: Sent' "$RESULTS_FILE")"
        echo "SMS with 'Failed' status: $(grep -c 'Status: Failed' "$RESULTS_FILE")"
        echo "Average response times can be calculated from the results file."
    fi
    
    echo ""
    echo "📁 Full results available in: $RESULTS_FILE"
    echo "✅ Performance test completed!"
}

# Check dependencies
if ! command -v bc &> /dev/null; then
    echo "⚠️  Warning: 'bc' not found. Time calculations may not work."
    echo "Install with: sudo apt-get install bc"
fi

if ! command -v jq &> /dev/null; then
    echo "⚠️  Warning: 'jq' not found. Using fallback JSON parsing."
    echo "Install with: sudo apt-get install jq"
fi

# Run main test
main

echo ""
echo "🎯 To analyze results further:"
echo "  cat $RESULTS_FILE"
echo "  grep 'Time:' $RESULTS_FILE"
echo "  grep 'Status: Sent' $RESULTS_FILE | wc -l"