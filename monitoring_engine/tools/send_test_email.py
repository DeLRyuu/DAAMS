"""
send_test_email.py -- send a test security alert email to verify SMTP configuration.

Uses the existing services/email_notifier.py and existing environment variables:
- DAAMS_EMAIL_ENABLED
- DAAMS_SMTP_HOST
- DAAMS_SMTP_PORT
- DAAMS_SMTP_USERNAME
- DAAMS_SMTP_PASSWORD
- DAAMS_NOTIFICATION_EMAIL

Credentials are never displayed in plaintext.
"""

from datetime import datetime
import os
import sys

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from config.settings import (  # noqa: E402
    EMAIL_ENABLED,
    NOTIFICATION_EMAIL,
    SMTP_HOST,
    SMTP_PASSWORD,
    SMTP_PORT,
    SMTP_USERNAME,
)
from services import email_notifier  # noqa: E402


def main() -> int:
    print("DAAMS Email Notification Test")
    print("=" * 40)
    print(f"DAAMS_EMAIL_ENABLED      : {EMAIL_ENABLED}")
    print(f"DAAMS_SMTP_HOST          : {SMTP_HOST or '(not set)'}")
    print(f"DAAMS_SMTP_PORT          : {SMTP_PORT}")
    print(f"DAAMS_SMTP_USERNAME      : {SMTP_USERNAME or '(not set)'}")
    print(f"DAAMS_SMTP_PASSWORD      : {'********' if SMTP_PASSWORD else '(not set)'}")
    print(f"DAAMS_NOTIFICATION_EMAIL : {NOTIFICATION_EMAIL or '(not set)'}")
    print("=" * 40)

    now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    test_alert = {
        "alert_id": "TEST-ALERT-001",
        "activity_id": "TEST-ACT-001",
        "user": os.environ.get("USERNAME", "Administrator"),
        "device": os.environ.get("COMPUTERNAME", "DAAMS-DEMO"),
        "asset": "DAAMS Test Asset (Verification)",
        "asset_path": r"C:\DAAMS_Demo\test_asset.txt",
        "asset_classification": "Confidential",
        "action": "Open",
        "timestamp": now_str,
        "risk_score": 85,
        "risk_level": "Critical",
        "risk_factors": [
            "Test Alert Verification (+85)",
            "Classification: Confidential (+35)",
        ],
        "reason": "Test Email Notification sent by tools/send_test_email.py",
        "status": "Test",
    }
    test_incident = {
        "incident_id": "TEST-INC-001",
        "status": "Test Verification",
    }

    print("\nSending test security alert email...")
    success = email_notifier.send_alert_email(test_alert, test_incident)

    if success:
        print(f"\n[SUCCESS] Test alert email was successfully sent to {NOTIFICATION_EMAIL}.")
        return 0
    else:
        print("\n[FAILURE] Test alert email could not be sent.")
        if not EMAIL_ENABLED:
            print("Reason: DAAMS_EMAIL_ENABLED is false or unset.")
            print("Set $env:DAAMS_EMAIL_ENABLED='true' to enable email notifications.")
        elif not SMTP_HOST:
            print("Reason: DAAMS_SMTP_HOST is empty.")
        elif not NOTIFICATION_EMAIL:
            print("Reason: DAAMS_NOTIFICATION_EMAIL is empty.")
        else:
            print("Reason: SMTP connection or authentication failed. Check credentials, port, and network.")
        return 1


if __name__ == "__main__":
    sys.exit(main())
