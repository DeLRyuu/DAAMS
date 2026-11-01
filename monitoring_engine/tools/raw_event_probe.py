import sys, time
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

class Probe(FileSystemEventHandler):
    def on_any_event(self, e):
        dest = getattr(e, "dest_path", "")
        print(f"{time.strftime('%H:%M:%S')}  {e.event_type:<9} dir={e.is_directory!s:<5} {e.src_path} {('-> ' + dest) if dest else ''}")

folder = sys.argv[1]
obs = Observer(); obs.schedule(Probe(), folder, recursive=True); obs.start()
print("Watching", folder, "- Ctrl+C to stop")
try:
    while True: time.sleep(1)
except KeyboardInterrupt:
    obs.stop()
obs.join()