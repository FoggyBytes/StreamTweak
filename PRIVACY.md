# Privacy

StreamTweak has **no analytics, no telemetry, no accounts and no ads**. Nothing is ever sent to FoggyBytes: there is no FoggyBytes server.

This page lists every connection StreamTweak makes on its own, and every piece of data that leaves the PC.

## Your own devices

The bridge on TCP 47998 answers only clients **you approved** on this PC (see [SECURITY.md](SECURITY.md)). To an approved [StreamLight](https://github.com/FoggyBytes/StreamLight) client it can send:

- the host's live load during a stream — GPU, video encoder, CPU, VRAM, temperature, network throughput;
- the host's last finished session — when, how long, its quality grade, the games played and small cover pictures;
- the store each game comes from, the state of a game launch, whether the PC is locked, which power modes it supports, whether Windows updates are waiting;
- the host's Tailscale address, if Tailscale is installed;
- with **Shared clipboard** on (on by default on the host, off by default on the client — both sides must agree), clipboard text, encrypted.

The client sends back the statistics of each stream (frame rate, latency, jitter, bitrate, dropped frames), which StreamTweak keeps in its session history.

## Third-party services

| When | Where | What is sent | Can it be turned off |
|---|---|---|---|
| At every launch | `api.github.com` (GitHub) | A request for the latest StreamTweak release; the request carries your IP address and a `StreamTweak` user agent | No — it is how the update notice works. Nothing else is sent |
| When Settings opens | `api.github.com` (GitHub) | The same request for the latest StreamLight release, to show its version | No |
| When the **game library** syncs — when you press *Sync*, or at every launch if you turned on automatic sync (**off by default**) | Steam (`api.steampowered.com`, `store.steampowered.com`, Steam's image servers), GOG (`catalog.gog.com`, `api.gog.com`), Ubisoft's image server | The Steam app ids, GOG product ids and **names of the installed games**, to correct names and download cover art | **Yes** — leave the game library unsynced |
| When an approved client asks to scan for or install updates | Windows Update (Microsoft) | Whatever Windows Update itself sends | It only happens on request |

The privacy statements of [GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement), [Valve](https://store.steampowered.com/privacy_agreement/), [GOG](https://www.gog.com/en/privacy) and [Ubisoft](https://www.ubisoft.com/en-us/privacy-policy) apply to what they receive.

Links in Settings (releases, licence, donations, the streaming servers' pages) open your browser only when you click them.

## Data kept on this PC

Everything is in `%LOCALAPPDATA%\StreamTweak\`, for the current Windows user, and **never uploaded**:

- `config.json` — settings;
- `sessions.json` — the session history: games played, times, stream quality figures, and the host's power readings during each stream;
- `bridgeclients.json` — the clients you approved or denied: their name, certificate, and when they were approved and last seen;
- `managedapps.json`, `gamelibrarystate.json` and `covers\` — the *Apps* list, the game library and its cover art;
- `debug.log` — the diagnostic log. If you attach it to an issue, read it first: it contains client names, IP addresses and game names.

StreamTweak also **reads**, without changing them, the log files of the streaming server installed on this PC (Sunshine, Apollo, Vibeshine, Vibepollo) to know when a stream starts and ends, and the libraries of the game stores installed on it to build the game library.

Uninstalling removes the program; the `%LOCALAPPDATA%\StreamTweak` folder can be deleted by hand afterwards.
