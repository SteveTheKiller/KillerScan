import csv

entries = {}
with open(r'C:\Users\steve\code\KillerScan\oui.csv', 'r', encoding='utf-8') as f:
    reader = csv.reader(f)
    header = next(reader)
    print(f"Header: {header}")
    for row in reader:
        if len(row) >= 3:
            registry = row[0]
            mac_prefix = row[1].strip()
            org_name = row[2].strip()
            if mac_prefix and org_name:
                # Normalize to XX:XX:XX format
                mac_clean = mac_prefix.upper().replace('-', ':')
                if len(mac_clean) == 6:
                    mac_clean = f"{mac_clean[:2]}:{mac_clean[2:4]}:{mac_clean[4:6]}"
                entries[mac_clean] = org_name

print(f"Total entries: {len(entries)}")
# Show first 10
for i, (k, v) in enumerate(list(entries.items())[:10]):
    print(f"  {k} -> {v}")

# Write as a simple tab-delimited file for the C# app to load
with open(r'C:\Users\steve\code\KillerScan\Resources\oui.txt', 'w', encoding='utf-8') as out:
    for mac, vendor in sorted(entries.items()):
        out.write(f"{mac}\t{vendor}\n")

print(f"Wrote oui.txt with {len(entries)} entries")
