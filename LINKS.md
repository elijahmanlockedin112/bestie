# Bestie links

## Run it
- Start: double-click `C:\Users\elija\Downloads\Bestie\Bestie.exe`
- Build: `cmd /c "C:\Users\elija\Downloads\Bestie\build.cmd"` (asks a running Bestie to step out first)

## On the PC
- Second brain (dashboard): http://127.0.0.1:47830/brain (or right-click Mothball → second brain…)
- Phone app pairing code: http://127.0.0.1:47830/pair

## On the phone (Tailscale)
- Phone app: https://elijahman.taileb0bc0.ts.net:8445/
- Second brain: https://elijahman.taileb0bc0.ts.net:8445/brain
- Locked In screen (timer, Mothball, song on the PC): https://elijahman.taileb0bc0.ts.net:8445/lockin?k=<pairing key>&go=1 (`go=1` plays the intro; the key is kept and dropped from the address). Sound: /lockin.wav
- Locked In (NFC tag Shortcut): POST https://elijahman.taileb0bc0.ts.net:8445/api/control, header `X-Bestie-Key: <pairing key>`, body `{"action":"lockin"}` (`"value":"off"` stops the block, `"preview"` only says what it would do)

## Starts with Windows
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `Bestie`. Started from a Claude shell, Bestie's own "live here" toggle writes into Claude's sandboxed registry and Windows never sees it, so launch it with `Start-Process explorer.exe C:\Users\elija\Downloads\Bestie\Bestie.exe`.

## Code
- Repo: https://github.com/elijahmanlockedin112/bestie
