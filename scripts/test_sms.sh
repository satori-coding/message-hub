#!/bin/bash

echo "🚀 Testing SMS API..."

# Test message
curl -X POST "http://localhost:5289/api/sms/send" \
     -H "Content-Type: application/json" \
     -d '{
       "PhoneNumber": "+491234567890",
       "Content": "Hello from REST API! This is a test SMS message via curl."
     }' \
     -v

echo -e "\n\n📊 Checking database..."
cd "$(dirname "$0")"
python3 view_db.py