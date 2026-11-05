import sqlite3
c = sqlite3.connect("data/daams.db")
rows = c.execute(
    "select path, status, asset_name from protected_assets where asset_name like ?",
    ("%prototype test%",)
).fetchall()
for r in rows:
    print(r)
