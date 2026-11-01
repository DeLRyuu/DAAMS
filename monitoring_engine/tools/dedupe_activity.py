import shutil, sqlite3
shutil.copy("data/daams.db", "data/daams.db.bak")
c = sqlite3.connect("data/daams.db")
before = c.execute("SELECT COUNT(*) FROM activity_logs").fetchone()[0]
c.execute("""
DELETE FROM activity_logs
WHERE activity_id LIKE 'ACT-MIG-%'
AND EXISTS (SELECT 1 FROM activity_logs a
            WHERE a.activity_id NOT LIKE 'ACT-MIG-%'
              AND a.user = activity_logs.user AND a.device = activity_logs.device
              AND a.asset = activity_logs.asset AND a.action = activity_logs.action
              AND a.timestamp = activity_logs.timestamp)""")
c.commit()
print(before, "->", c.execute("SELECT COUNT(*) FROM activity_logs").fetchone()[0])