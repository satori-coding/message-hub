#!/usr/bin/env python3
import sqlite3
import json

db_path = "sms_database.db"

try:
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    print("=== Latest Multi-Part Message Analysis ===")
    
    # Get the latest message with parts
    cursor.execute("""
        SELECT Id, Recipient, Status, MessageParts, ProviderMessageId, ChannelData 
        FROM Messages 
        WHERE MessageParts > 1 
        ORDER BY CreatedAt DESC 
        LIMIT 1
    """)
    message = cursor.fetchone()
    
    if message:
        msg_id, recipient, status, parts_count, provider_id, channel_data = message
        print(f"Message ID: {msg_id}")
        print(f"Recipient: {recipient}")
        print(f"Status: {status}")
        print(f"Parts Count: {parts_count}")
        print(f"Primary Provider ID: {provider_id}")
        
        # Parse channel data
        if channel_data:
            try:
                data = json.loads(channel_data)
                print(f"All Provider IDs: {data.get('SmppMessageIds', [])}")
                print(f"Channel Type: {data.get('ChannelType', 'Unknown')}")
            except:
                print("Channel Data: (unparseable)")
    
    print("\n=== MessageParts Details ===")
    
    # Get all message parts for the latest message
    cursor.execute("""
        SELECT mp.Id, mp.PartNumber, mp.TotalParts, mp.ProviderMessageId, 
               mp.Status, mp.DeliveredAt, mp.DeliveryStatus, mp.ErrorCode
        FROM MessageParts mp
        INNER JOIN Messages m ON mp.MessageId = m.Id
        WHERE mp.MessageId = ?
        ORDER BY mp.PartNumber
    """, (msg_id if message else 0,))
    
    parts = cursor.fetchall()
    
    if parts:
        print(f"{'Part':>4} | {'Provider ID':>12} | {'Status':>12} | {'Delivered At':>20} | {'DLR Status':>12}")
        print("-" * 80)
        for part in parts:
            part_id, part_num, total, prov_id, status, delivered_at, dlr_status, error_code = part
            delivered_str = delivered_at[:19] if delivered_at else "Not delivered"
            dlr_str = dlr_status or "N/A"
            print(f"{part_num:>4} | {prov_id:>12} | {status:>12} | {delivered_str:>20} | {dlr_str:>12}")
    
    print("\n=== DLR Processing Success Summary ===")
    
    # Count delivered vs pending parts
    cursor.execute("""
        SELECT Status, COUNT(*) as Count
        FROM MessageParts
        WHERE MessageId = ?
        GROUP BY Status
    """, (msg_id if message else 0,))
    
    status_counts = cursor.fetchall()
    for status, count in status_counts:
        print(f"  {status}: {count} parts")
    
    # Check if all parts are delivered
    cursor.execute("""
        SELECT COUNT(*) as Total, 
               SUM(CASE WHEN Status = 'Delivered' THEN 1 ELSE 0 END) as Delivered
        FROM MessageParts
        WHERE MessageId = ?
    """, (msg_id if message else 0,))
    
    total, delivered = cursor.fetchone()
    if total > 0:
        success_rate = (delivered / total) * 100
        print(f"\n🎯 DLR Success Rate: {delivered}/{total} ({success_rate:.1f}%)")
        
        if success_rate == 100:
            print("✅ PERFECT: All SMS parts received delivery receipts!")
        else:
            print(f"⚠️  PARTIAL: {total - delivered} parts still pending")
    
    conn.close()
    
except Exception as e:
    print(f"❌ Error: {e}")