# FyteClub

P2P mod sync for FFXIV thats kinda like Mare but its serverless.

Every Mare fork still runs a server, meaning some guy you don't know is paying for bandwidth and logging god knows what, and whoever's running it is also exactly who Square Enix goes after. FyteClub skips that entirely, two clients just find each other and talk directly, nothing to shut down and nobody to trust. Not claiming I'll personally run this forever either, it's open source, PRs welcome, the whole point is nobody has to trust one guy long term.

[WebRTC](https://webrtc.org/) does the actual data transfer, same tech video calls run on to push data around fast. [Nostr](https://nostr.com/) (public relays, not mine, not anybody's) is just pub/sub so two clients can find each other without either one standing up infrastructure. Syncshells are password-derived group keys, invite people and they're in, nothing routes through anything I control because there's nothing I control. Tried a lightweight phonebook server once to speed up peer discovery, killed it fast once I realized everyone syncing through it meant everyone had my IP, exact same problem I was trying to avoid. If it helps: mods are the neurotransmitter, the clients are neurons, Nostr's the signal that something's nearby, WebRTC's the synapse it crosses.

Runs on [Dalamud](https://github.com/goatcorp/Dalamud), talks to [Penumbra](https://github.com/xivdev/Penumbra), [Glamourer](https://github.com/Ottermandias/Glamourer), [Customize+](https://github.com/Aether-Tools/CustomizePlus), [SimpleHeels](https://github.com/Caraxi/SimpleHeels), and [Honorific](https://github.com/Caraxi/Honorific). Auto-syncs with nearby group members (~50m), manual sync button if you don't want to wait.

## security, honestly

WebRTC connections are encrypted (DTLS) once two clients are actually talking to each other, so mod data in transit isn't sitting in the clear. Syncshell membership is a password-derived key, meaning it's only as strong as the password you picked and who you handed it to, same as any group chat invite link.

The part I'll flag myself: the WebRTC offer/answer exchange that happens *before* two clients connect goes out over public Nostr relays as plain JSON right now, not encrypted with the group key. Relay operators or anyone watching that relay can see connection metadata (who's trying to connect, roughly when) even though they can't get your mods. That's a real gap, not a secret, just where it's at today.

There's also no member removal / key rotation yet, so kicking someone out of a syncshell isn't a solved problem currently. And once you've sent someone a mod, they have the file, permanently, there's no DRM and there never will be, client-side file protection is theater and I'm not pretending otherwise. Don't sync mods with people you wouldn't hand a USB stick to.

Compared to Mare or a Mare clone: their whole security model runs through a server, meaning there's a login system, a database, and a box somewhere that knows who you are, what group you're in, and probably your IP, all sitting in one place waiting to be breached, subpoenaed, or just misconfigured by whoever's running it. Take that server down and every single user is locked out at once, no warning, no recourse. FyteClub doesn't have that box. There's no account database to leak, no central log of who's synced with who, and no single point that going down takes everyone else with it. Worse security in some specific spots (see above, I'm not going to pretend it's flawless), but a fundamentally different failure mode, one person's bad day doesn't end the project for everyone else.

I've been building this quietly since the day Mare got taken down, on purpose. Wasn't trying to make noise about it or attract attention while it wasn't ready, the entire point was to not repeat what happened to Mare. Hard to take down infrastructure that doesn't exist.

## install

XIVLauncher + Dalamud, then add the repo `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`, install FyteClub from the plugin installer, `/fyteclub` in game to make or join a syncshell.

## build from source

```bash
cd plugin
dotnet build -c Release
```

## license

MIT, see LICENSE. Not affiliated with Square Enix or FFXIV, obviously.
