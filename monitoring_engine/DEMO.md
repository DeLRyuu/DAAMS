# DAAMS Demonstration Runbook (5-Minute Script)

This runbook provides a concise, step-by-step guide for demonstrating the Digital Asset Access Monitoring and Risk Assessment System (DAAMS).

---

## 1. Pre-Demo Checklist

Ensure all prerequisites are met before starting the presentation:

- [ ] **Administrator Privileges**: Open an elevated PowerShell terminal (`Run as Administrator`). Administrative rights are required for Windows Security Audit Log ingestion (Event ID 4663).
- [ ] **Python Environment**: Confirm Python 3.10+ virtual environment is active (`monitoring_engine\venv\Scripts\Activate.ps1`).
- [ ] **Clean Demo State**: Ensure no previous DAAMS processes are running, then run:
  ```powershell
  python tools/prepare_demo.py --yes
  ```
  *This provisions `C:\DAAMS_Demo`, sets its classification to `Confidential`, populates demo files, and clears prior activity/alert logs while strictly preserving security PIN settings.*
- [ ] **Open File Auditing Enabled**:
  ```powershell
  powershell -ExecutionPolicy Bypass -File tools/enable_open_audit.ps1 -Path C:\DAAMS_Demo
  ```
- [ ] **Start Monitoring Engine**: In an elevated terminal:
  ```powershell
  python main.py
  ```
  *Verify console outputs: `N protected asset(s) being monitored` and confirm that `Open detection is off` does NOT appear.*
- [ ] **Start Control Center**: In a separate terminal or Visual Studio:
  ```powershell
  dotnet run --project ..\DAAMS.ControlCenter\DAAMS.ControlCenter\DAAMS.ControlCenter.csproj
  ```
  *Verify Dashboard loads cleanly with 0 active alerts and offline-first SQLite connection active.*

---

## 2. 5-Minute Demonstration Script

### Minute 1: System Overview & Baseline Setup
- **Action**: Open the Control Center. Walk through the **Dashboard** and navigate to **Protected Assets**.
- **What Appears**:
  - Protected Assets table lists `C:\DAAMS_Demo` with the **Confidential** classification chip and monitored status.
  - Dashboard indicates a clean baseline: 0 open alerts, empty risk mix bar, and idle activity feed.
- **Talking Point**: DAAMS operates on an offline-first architecture using local SQLite storage with background Firestore synchronization. Assets are categorized into four tiers: Public, Sensitive, Private, and Confidential.

### Minute 2: Normal Read & Low/Medium Modification
- **Action**:
  1. Navigate to `C:\DAAMS_Demo` in File Explorer.
  2. Open `financial_records_2026.txt` in Notepad, review the contents, and close it.
  3. Reopen `financial_records_2026.txt`, append a short note, and save (`Ctrl+S`).
- **What Appears**:
  - In **Activity Logs**:
    - **Open**: Captured via Windows Security Event 4663. Risk score: **37 (Medium)**.
      *Factors: Confidential asset (+35), Open action (+2).*
    - **Modify**: Captured via Win32 directory change notifications. Risk score: **43 (Medium)**.
      *Factors: Confidential asset (+35), Modify action (+8).*
  - No disruptive alerts are triggered because scores remain below the alert threshold (score < 50).
- **Talking Point**: Routine day-to-day operations on confidential files are recorded for full auditability without spamming security administrators with false positives.

### Minute 3: Exfiltration Indicators (Copy & Move)
- **Action**:
  1. Select `employee_payroll_q4.txt` and press `Ctrl+C` (Copy).
  2. Move `confidential_merger_notes.txt` into `C:\DAAMS_Demo\Subfolder_for_testing` (or Cut with `Ctrl+X`).
- **What Appears**:
  - In **Activity Logs**:
    - **Copy**: Intercepted via Windows Clipboard drop effects (`DROPEFFECT_COPY`). Risk score: **53 (High)**.
      *Factors: Confidential asset (+35), Copy action (+18).*
    - **Move**: Intercepted via directory move / clipboard cut. Risk score: **49 (Medium)**.
      *Factors: Confidential asset (+35), Move action (+14).*
  - **Security Alerts**: The Copy event crosses the high-risk threshold ($\ge 50$), generating a **High** alert.
  - The Control Center sidebar shows the badge count incrementing, and the Dashboard "Needs review" card lists the new case.
- **Talking Point**: Clipboard monitoring detects potential data exfiltration attempts the instant sensitive files are placed on the clipboard.

### Minute 4: High-Risk Action & Rapid Burst Activity
- **Action**:
  Trigger a burst of 3 rapid actions within 60 seconds:
  1. Rename `security_audit_plan.txt` to `audit_plan_archive.txt`.
  2. Delete `confidential_merger_notes.txt`.
  3. Perform a 3rd action (e.g., delete or modify another file in `C:\DAAMS_Demo`) within the same minute.
- **What Appears**:
  - **Rename**: Score **41 (Medium)** [*Confidential (+35), Rename (+6)*].
  - **Delete**: Score **57 (High)** [*Confidential (+35), Delete (+22)*]. Triggers a High Security Alert.
  - **3rd Action (Burst)**: The engine detects 3 actions by the same user within 60 seconds and applies the **Burst Rule (+20 points)**:
    - If the 3rd action is Delete: Score = $35 + 22 + 20 =$ **77 (Critical)**.
    - An immediate **Critical** alert and Incident Report are raised.
    - If email alerting is enabled (`DAAMS_EMAIL_ENABLED=true`), an incident notification is immediately dispatched to the security team.
  - Dashboard updates: Risk mix bar reflects orange (High) and red (Critical) segments; the latest activities table shows the burst penalty in the factor list.
- **Talking Point**: Behavioral heuristics detect rapid, bulk automated activity or panic deletions through real-time velocity tracking.

### Minute 5: Off-Hours Policy & Incident Resolution
- **Action**:
  - Explain the **Working Hours Policy**: Operations outside configured working hours (default: 07:00–19:00) incur an automatic **+25 points** after-hours penalty.
  - In Control Center: Click on the generated alert in **Security Alerts** or the case under "Needs review". Update incident status to *In Progress* or *Resolved* (enter administrator PIN if prompted).
- **What Appears**:
  - Incident details display full contextual timeline, user, device, and contributing risk factors.
  - Status updates persist locally to SQLite and sync to Firestore.
- **Talking Point**: DAAMS provides security operators with immediate, explainable forensic context without black-box machine learning confusion.

---

## 3. Expected Risk Scores Reference Table

### Confidential Asset (Base Weight: +35)

| Action | Action Weight | Work Hours Score (Risk Level) | After-Hours Score (+25) (Risk Level) | Burst (+20) Work Hours | Burst (+20) After-Hours |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Open** | +2 | **37** (Medium) | **62** (High) | **57** (High) | **82** (Critical) |
| **Modify** | +8 | **43** (Medium) | **68** (High) | **63** (High) | **88** (Critical) |
| **Rename** | +6 | **41** (Medium) | **66** (High) | **61** (High) | **86** (Critical) |
| **Move** | +14 | **49** (Medium) | **74** (High) | **69** (High) | **94** (Critical) |
| **Copy** | +18 | **53** (High) | **78** (Critical) | **73** (High) | **98** (Critical) |
| **Delete** | +22 | **57** (High) | **82** (Critical) | **77** (Critical) | **100** (Critical, capped) |

*Note: Risk level bands: Low (0–24), Medium (25–49), High (50–74), Critical (75–100).*
*Burst Rule: 3 or more actions by the same user on the same device within 60 seconds add **+20 points**.*

---

## 4. Fallback Plan

If unexpected issues occur during live demonstration:

1. **Open Detection Inactive / Missing Event 4663**:
   - *Symptom*: Opening a file does not log an activity.
   - *Cause*: Console was not launched as Administrator, or Windows Audit Policy is disabled.
   - *Fallback*: Explain that Open detection uses Windows Security Audit Log Event 4663 which requires Administrator privilege. Continue demonstrating Modify, Copy, Rename, and Delete actions, which rely on filesystem watchers and clipboard hooks that do not depend on the security log.
2. **Cloud Sync Offline**:
   - *Symptom*: Footer displays `Cloud sync: Offline`.
   - *Cause*: No internet connectivity or missing Firestore credentials.
   - *Fallback*: Highlight this as a key system strength—DAAMS is built with an **Offline-First Architecture**. Show that all alerts, logs, and incidents continue recording seamlessly into local SQLite without data loss.
3. **Dirty Demo State**:
   - *Fallback*: Stop the engine (`Ctrl+C`), close the Control Center, and run `python tools/prepare_demo.py --yes`. Relaunch both applications for an immediate fresh state in under 5 seconds.

---

## 5. Known Limitations

The following architectural boundaries are intentional for this prototype phase:

1. **Cut Detection Timing**:
   - When a user cuts a file (`Ctrl+X`), the action is detected and recorded as `Move` at the moment the cut is placed on the Windows clipboard (`DROPEFFECT_MOVE`), before the user pastes it into the destination.
2. **Administrator Privilege for Open Detection**:
   - File read auditing (`Open`) relies on Windows Event ID 4663. Querying the Windows Security log requires elevated administrator privileges. Standard non-elevated user accounts monitor Create, Modify, Rename, Delete, Copy, and Move, but not Open.
3. **Single Protected Demo Root**:
   - The demo environment monitors `C:\DAAMS_Demo` and its subdirectories. Files manipulated outside monitored asset directories are ignored by design to prevent performance degradation.
4. **Authorization / Session Status Unscored**:
   - While `session_status` is accepted in the activity data schema, it currently does not alter the risk score because standalone Windows environments lack authoritative identity-provider session claims.
5. **In-Memory Burst Window**:
   - Rapid-action burst counters are maintained in memory per user session. Restarting the monitoring engine process resets the 60-second velocity history.
