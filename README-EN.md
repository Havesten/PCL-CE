[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# PCLCE-NS

A third-party fork of PCL Community Edition

[Releases](https://github.com/Havesten/PCL-CE/releases) |
[Community Edition upstream](https://github.com/PCL-Community/PCL-CE) |
[Original author](https://github.com/Meloong-Git/PCL)

[Submit an issue](https://github.com/Havesten/PCL-CE/issues/new/choose) |
[Changelog](https://github.com/Havesten/PCL-CE/releases)

</div>

## What this is

PCL (Plain Craft Launcher) is a Minecraft launcher by 龙腾猫跃 (Meloong). PCL CE is a community edition built on its open-source code.

This repository is a **third-party fork of PCL CE**, maintained independently by one person. It is not an official build of PCL or PCL CE, and it does not represent upstream in any way.

## Differences from upstream PCL CE

### New: download node selection

Upstream offers a binary choice between official and mirror sources. This build measures each candidate node's TCP and TLS handshake time, takes the median across several rounds to damp jitter, then pins the fastest one for subsequent requests. The URL, SNI and certificate validation all stay exactly as they were — only the IP the connection lands on changes.

Sixteen hosts are covered, in four groups:

| Group | Hosts |
|---|---|
| Mojang | piston-meta, launchermeta, launcher, resources and two more |
| Mod loaders | Forge, NeoForge and Fabric mavens and list endpoints, five in total |
| CurseForge | api, edge, mediafilez |
| Modrinth | api, cdn |

Cloudflare-fronted hosts additionally switch to a shared preferred-IP pool (resolved from `cloudflare.182682.xyz`). The pool is probed once and reused by every CF host, since Cloudflare is anycast and one set of preferred IPs serves every domain behind it. Non-CF hosts (AWS CloudFront, Fastly, Azure) are detected and keep their own DNS answers.

Available under Settings → Download → Node selection.

### Changed: update source

- Removed the MirrorChyan beta channel and its CDK field
- The update endpoint now points at a private server speaking the Minio-compatible protocol
- No community announcement popup when online services are disabled

### Deleted & rebuilt: multiplayer

The old Lobby entry and the Natayark account login it depended on are gone — multiplayer was removed wholesale and rebuilt. It now sits in the title bar next to Launch, Download, Settings and Tools, and currently offers relay multiplayer.

- **Relay multiplayer**: the host's local port is mapped to the public internet by a relay server, so no public IP is needed and two players behind NAT can still connect. Sign-in uses email + verification code (first sign-in registers, with a CAPTCHA); afterwards you can see monthly traffic, speed limit and port quota. Those limits are pushed and enforced server-side — editing local config cannot bypass them. The tunnel protocol is a custom PTP v1, built into both client and server, with no external binaries required
- **P2P direct connect has been pulled**: the page implementation stays at `Pages/PageLink/PageLinkP2P` but no longer appears in navigation — pure direct connect needs a public IP on at least one side, so it always fails when both peers are behind NAT, too poor an experience for a default entry. Uncomment `ItemP2P` in `PageLinkLeft.xaml` to bring it back

### Changed: accounts

- Creating a profile now only offers "Microsoft account" and "third-party auth"; **the offline login entry is gone**. Existing offline profiles are unaffected and still launch fine

### Changed: interface

- Removed the persistent community notice card on the home page
- Added a `NS` mark after the title bar logo to distinguish this build from upstream
- Default game window title is now `玩家：{user} 登录方式：{login} 版本：{version} 时间：{date} {time}`
- The multiplayer entry inside Tools is gone — multiplayer lives only in the title-bar Multiplayer page
- The P2P multiplayer page is temporarily hidden: navigation lists only "Relay multiplayer", and the multiplayer page opens straight onto the relay panel. The P2P page code and its room-code / peer-address / three-way protocol-stack logic are all kept as-is — uncomment `ItemP2P` in `PageLinkLeft.xaml` to restore it
- The "Settings → Multiplayer (EasyTier)" page is deleted entirely: username, protocol preference, latency-first, symmetric-NAT port guessing and CLI debug output only took effect for EasyTier direct connect, and with that entry hidden there is no way to reach them. The matching checkboxes under Tools and Hidden features were removed with it

## Features

All of these come from upstream and are kept as-is:

- Version install, modpack import, mod / resource pack / world management
- Mod and resource downloads (CurseForge, Modrinth)
- **Multiplayer**: EasyTier tunnel, public relay nodes and self-hosted relay server configuration (this fork rebuilt multiplayer on top of these — see above)
- Microsoft account sign-in, skin and cape management, multi-account switching
- Automatic Java detection and install, crash analysis, log export

## Download

Grab the latest build from [Releases](https://github.com/Havesten/PCL-CE/releases/latest). Single exe, no installer.

Same requirements as upstream: Windows 10 1809 (17763) or newer, plus [.NET 10 Desktop Runtime](https://get.dot.net/10).

## Building from source

Requires .NET 10 SDK on Windows:

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -p:Platform=x64 -o ./artifact --no-self-contained
```

### Optional: build-time secrets

A self-built copy ships with **no embedded keys**. Nothing breaks, but two features degrade:

| Variable | Behaviour when missing |
|---|---|
| `PCL_CURSEFORGE_API_KEY` | CurseForge's official API returns 403 and falls back to the mcim mirror (download source must be set to Auto or Prefer mirror) |
| `PCL_MS_CLIENT_ID` | Microsoft sign-in unavailable, offline login only |
| `PCL_NAID_CLIENT_ID` / `_SECRET` | Natayark ID login unavailable (the Lobby is gone, so this is usually no longer needed) |

```bash
PCL_WRITE_SECRET=1 \
PCL_CURSEFORGE_API_KEY='$2a$10$...' \
PCL_MS_CLIENT_ID='xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx' \
  dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -p:Platform=x64 -o ./artifact --no-self-contained
```

Note that `$` expands inside bash double quotes — wrap the key in single quotes.

## Known issues

- **The update endpoint still points at a dead address**, so the launcher skips update checks entirely
- The first node-selection run takes 4–5 minutes depending on your network; results are cached for an hour
- Without an embedded CurseForge key, search ordering and some fields differ slightly from the official API

## Feedback

**Do not report issues with this fork to PCL or PCL CE upstream.** They cannot reproduce changes made here, and it only adds work for them.

Open an issue [in this repository](https://github.com/Havesten/PCL-CE/issues/new/choose) and attach the launcher log from `PCL/Log/`.

## Credits and licensing

This fork exists only because Meloong released PCL's source and because the PCL CE community keeps maintaining it. If it's useful to you, consider supporting the original author: [afdian.com/a/LTCat](https://afdian.com/a/LTCat).

Licensing:

- The `Plain Craft Launcher 2/` directory is covered by the *PCL Distribution Limited License* and the *PCL Repository Fair Use Guidelines*, see [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE). This fork follows the "heavy use" terms:
  - It declares itself a third-party derivative work, not an official build
  - Its name starts with PCL and carries a suffix indicating third-party modification
  - Author credit and the donation link remain first on the About page, and this repository is public
  - The Minecraft purchase prompt (`McLaunchPrecheck`) and donation prompt are preserved
  - No donation-unlock-like features are offered, and the primary hue stays blue
- All other directories use the [Apache License 2.0](LICENSE)

This fork is distributed free of charge, with no paid tier and no donation gate of any kind.

## Star history

<a href="https://www.star-history.com/?repos=Havesten%2FPCL-CE&type=date&legend=top-left">
  <img src="https://api.star-history.com/chart?repos=Havesten/PCL-CE&type=date&legend=top-left" alt="Star History" width="600">
</a>
