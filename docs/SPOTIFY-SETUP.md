# Connect the Spotify library later

Windows playback is already connected. Play/pause and seeking have been tested with the installed Spotify app. The expanded panel adds search, Queue, Recent, Liked, Playlists, device transfer and device volume. Those account features become available after the setup below.

## One-time setup

1. Sign in to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) yourself and create an app, for example **Island Windows**. Select **Web API** when asked which API you will use. See Spotify's [app setup guide](https://developer.spotify.com/documentation/web-api/concepts/apps).
2. In the app's settings, register this exact redirect URI:

   ```text
   http://127.0.0.1:8888/spotify/callback
   ```

   Click **Add** beside the URI, then **Save**. Include `:8888`; Island now listens on that exact port for Spotify sign-in. Spotify documents both dynamic ports and explicit ports, but the dashboard rejected the earlier address without a port in this setup. HTTP is permitted for the explicit loopback IP; do not substitute `localhost` or change it to HTTPS. [Redirect URI rules](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri).
3. If required, add your Spotify account under the app's **User Management**. Development access requires an active Premium subscription for the app owner and an authorized account. [Development access requirements](https://developer.spotify.com/documentation/web-api/concepts/quota-modes).
4. Copy the app's **Client ID** (32 characters). Island uses PKCE and does **not** need a client secret.
5. Open Island Settings by launching Island again or using its tray icon. Paste the ID under **Spotify library**, then click **Connect Spotify library**. Complete the Spotify sign-in and consent in your browser yourself.
6. Return to Island's Music view and click its list button to open the library. Start a track in Spotify if it reports that no active device is available.

## Available controls

- Music view: play/pause, previous/next, seeking, shuffle and repeat when exposed by the Windows player; its volume strip controls Windows output volume.
- Library: track search with pages, queue viewing and adding tracks, recent tracks, Liked Songs and save/unsave.
- Playlists: browse your playlists, open their tracks, or request playlist playback.
- Devices: transfer playback and adjust a device's volume when Spotify permits it. The transfer preserves the playing/paused state.

The Music view controls the selected Windows media session. After transferring to another device, it is not a complete remote-device transport controller.

Spotify limits development apps to browsing playlist contents owned by the user or playlists they collaborate on. Some playback actions require Premium. This panel is not a replacement for every Spotify feature: lyrics, queue reordering/removal and playlist editing are not implemented. [Spotify development API changes](https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide), [playback requirements](https://developer.spotify.com/documentation/web-api/reference/transfer-a-users-playback).

## If connection fails

- Redirect mismatch: check the exact URI above in the app settings.
- Local port unavailable: close the other app using port 8888 and retry. Island will not silently switch to an unregistered callback port.
- Access denied / 403: check Premium, the app's permitted users, and browser consent. Reconnect if permissions were changed.
- No device / 404: open Spotify and start a track, then refresh the relevant tab.
- Rate limited / 429: wait for Island's displayed cooldown before retrying.

Library actions have contract tests, but live OAuth and account-backed actions still need verification after you supply a Client ID. No Client ID or account token is bundled with Island.
