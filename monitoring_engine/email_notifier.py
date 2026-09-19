"""
email_notifier.py

Phase 7: Email notifications for Security Alerts / Incident Reports.

Sends a plain-text summary email via SMTP when a new High/Critical
Security Alert (and its associated Incident Report) is created. Uses
only Python's standard library (smtplib + email.message) -- no new pip
dependency was needed for this phase.

Credentials and server settings come ONLY from environment variables --
never hardcoded in this file, never read from a file this project owns.
See config.py for the exact variable names and README.md "Email Setup"
for how to set them.

Design goals, matching the rest of this project's established patterns:
- NEVER crash the Monitoring Engine. Any SMTP failure (bad credentials,
  no network, connection refused, timeout, misconfiguration) is caught
  and reported as a warning. By the time this module runs, the alert and
  incident records are already saved -- a failed email can never erase a
  security event.
- Respect EMAIL_ENABLED. When disabled (the default), this module is a
  fast no-op -- the event itself is still fully recorded either way.
- No new alerting is invented here. This module only sends a
  notification for a security event that alert_manager.py /
  incident_manager.py already decided was real; it never decides for
  itself whether something is alert-worthy.
- No duplicate emails: since this is only ever called once per newly
  created Security Alert (alert_manager.py's existing duplicate-alert
  guard already prevents a second alert -- and therefore a second
  email -- for the exact same activity), no separate email-specific
  dedupe bookkeeping is needed here.
"""

import smtplib
from email.message import EmailMessage

from config import (
    EMAIL_ENABLED,
    NOTIFICATION_EMAIL,
    SMTP_HOST,
    SMTP_PASSWORD,
    SMTP_PORT,
    SMTP_USERNAME,
)


def _build_email(alert: dict, incident: dict) -> EmailMessage:
    asset_label = alert.get("asset") or "Protected Asset"
    risk_level = alert.get("risk_level", "Unknown")
    subject = f"[DAAMS] {risk_level}-Risk Security Alert — {asset_label}"

    reason = " + ".join(
        factor.split(" (+")[0] for factor in (alert.get("risk_factors") or [])
    ) or "Not specified"

    incident_id = incident.get("incident_id") if incident else "N/A"
    incident_status = incident.get("status") if incident else "N/A"

    body = (
        "DAAMS SECURITY ALERT\n"
        "\n"
        f"Alert ID: {alert.get('alert_id')}\n"
        f"Incident ID: {incident_id}\n"
        "\n"
        f"User: {alert.get('user')}\n"
        f"Device: {alert.get('device')}\n"
        f"Asset: {alert.get('asset')}\n"
        f"Classification: {alert.get('asset_classification')}\n"
        f"Action: {alert.get('action')}\n"
        f"Date/Time: {alert.get('timestamp')}\n"
        "\n"
        f"Risk Score: {alert.get('risk_score')}\n"
        f"Risk Level: {risk_level}\n"
        f"Reason: {reason}\n"
        "\n"
        f"Status: {alert.get('status')} / {incident_status}\n"
        "\n"
        "Action Required:\n"
        "Please review this activity in the DAAMS Control Center.\n"
    )

    message = EmailMessage()
    message["Subject"] = subject
    message["From"] = SMTP_USERNAME or "daams@localhost"
    message["To"] = NOTIFICATION_EMAIL
    message.set_content(body)
    return message


def send_alert_email(alert: dict, incident: dict = None) -> bool:
    """
    Send a notification email for a newly-created Security Alert (and,
    if available, its associated Incident Report).

    Returns True if the email was sent successfully, False otherwise
    (disabled, not fully configured, or a send failure). NEVER raises.
    """
    if not EMAIL_ENABLED:
        print("[EMAIL] Notifications disabled (DAAMS_EMAIL_ENABLED is not 'true'); skipping.")
        return False

    if not SMTP_HOST or not NOTIFICATION_EMAIL:
        print(
            "[EMAIL WARNING] Email is enabled but SMTP_HOST and/or "
            "NOTIFICATION_EMAIL are not configured; skipping. "
            "See README.md 'Email Setup'."
        )
        return False

    alert_id = alert.get("alert_id", "unknown")

    try:
        message = _build_email(alert, incident or {})
        print(f"[EMAIL] Attempting to send notification for {alert_id}...")

        if SMTP_PORT == 465:
            # Implicit TLS from the first byte (e.g. many providers' port 465).
            with smtplib.SMTP_SSL(SMTP_HOST, SMTP_PORT, timeout=10) as server:
                if SMTP_USERNAME and SMTP_PASSWORD:
                    server.login(SMTP_USERNAME, SMTP_PASSWORD)
                server.send_message(message)
        else:
            # STARTTLS (e.g. port 587) -- the common default for most providers.
            with smtplib.SMTP(SMTP_HOST, SMTP_PORT, timeout=10) as server:
                server.starttls()
                if SMTP_USERNAME and SMTP_PASSWORD:
                    server.login(SMTP_USERNAME, SMTP_PASSWORD)
                server.send_message(message)

        print(f"[EMAIL] Notification sent for {alert_id}.")
        return True

    except Exception as e:
        # Covers auth failures, connection refused, DNS failure, timeout,
        # and anything else SMTP-related. The alert/incident were already
        # saved before this function was ever called, so nothing is lost.
        print(f"[EMAIL WARNING] Failed to send notification for {alert_id}: {e}")
        print("The security alert and incident record were still saved.")
        return False
