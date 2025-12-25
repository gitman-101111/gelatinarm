# Gelatinarm — Privacy Policy

Last updated: 2025-12-25

## Summary
Gelatinarm is a Jellyfin client for Xbox. The app communicates only with the Jellyfin server URL you configure in the app. It does not send data to third‑party servers, analytics services, or external endpoints.

## What we send to your Jellyfin server
- Authentication requests (username/password) and Quick Connect authentication — sent to the server you configure.
- Playback and session reporting (play start, progress, stopped) and media playback requests so your Jellyfin server can manage sessions and streaming.
- User actions that change server state (favorites, watched/unwatched toggles, queued items).
- Requests to fetch images, metadata, and media streams from your server. When necessary (for example, when the platform API does not allow custom HTTP headers for images), the app may append the configured access token (API key) to image URLs so it can retrieve resources from your Jellyfin server.

## What we store locally
- App preferences, playback positions, and caches are stored locally on your device.
- Authentication tokens are stored using the platform's secure storage mechanisms (for example, the Windows Credential Vault on Windows/Xbox).
- Images and other cached content may be stored locally to improve performance.

## What we do NOT do
- We do not collect or transmit usage analytics, crash reports, or telemetry to any third‑party analytics or monitoring services.
- We do not share your data with third parties.
- We do not transmit data to any remote servers other than the Jellyfin server you configured.

## Quick Connect note
Quick Connect uses the Jellyfin Quick Connect flow (code/secret) to authorize access with your Jellyfin server. Those requests and any returned tokens are exchanged with the configured Jellyfin server only.

## Security
- Network requests are made only to the server URL you provide. Use HTTPS whenever possible.
- The app includes an option to allow untrusted/self‑signed certificates for development or self‑hosted servers; enabling that option reduces the protection normally provided by TLS — only enable it if you understand the risks.
- Keep your Jellyfin server, account credentials, and network protected.

## Changes to this policy
This is an app‑level privacy statement. The policy may be updated; check the project repository or release notes for updates.

## Contact / Maintainer
For questions or privacy/data requests, contact: development@spamfolder.xyz
