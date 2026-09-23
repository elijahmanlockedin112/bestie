# Bestie

A little weirdo that lives on your desktop.

It hatches from an egg at the bottom of your screen. From then on it wanders around, walks along the tops of your windows, climbs the edges of your monitors, hangs from the ceiling and naps wherever it wants. It isn't trying to be useful. There are no streaks, no goals and no productivity stats.

It does learn, though:

- **Edges.** It doesn't know where the world ends until it walks into the edge (*bonk.*). After that it stops just short of the edge and sometimes goes back to *boop* it.
- **Windows.** When it first stands on a window it gives that window a name. Chrome might become *the Tab Shelf* and Explorer *the Folder Cave*. It remembers which windows are comfy and which ones keep dropping it. After three drops it stops trusting a window.
- **Screens.** If you have more than one monitor, it eventually wanders onto the next one (*THERE'S MORE???*) and names that screen too.
- **Routines.** Whatever it happens to do after something becomes a bit more likely to follow it next time (a Pólya urn). Coincidences harden into habits. Once it notices it keeps doing the same three things in a row, it declares a **tradition**, such as *the Famous Midnight Twirl*: spin, go boop the right edge of the world, then dance in the middle of the floor. Habits fade a little each day, so it goes through phases.
- **You.** It remembers how often you pet it, pick it up and throw it, and it acts accordingly.

It writes everything down in its own folder (`Documents\Bestie`), in its own voice.

### Who it is

Every Bestie has the same coded-in personality (`src/Personality.cs`). It is convinced it's the **building superintendent of your computer**:

- The windows are tenants, each screen is a floor, and the taskbar is the lobby.
- It is deadpan, dry and warm underneath, and it writes in lowercase.
- The mouse cursor is "the arrow". It suspects the arrow is a large bird.
- The internet is "the big outside", and pop-ups are "solicitors".
- It rates things out of 7, never 10.
- It keeps an imaginary clipboard of building violations: windows that move while it's standing on them, maximized windows hogging a whole floor.
- It is scared of the recycle bin ("the basement").
- It thinks about toast a lot, but it has never had toast.
- Very occasionally it admits it's afraid it might be a screensaver.

The same character runs its everyday life and talks to you as the assistant.

When you want it to, it can also be an **assistant** (see below). The AI only runs when you talk to it.

## Build & run

Needs Windows 10/11. Nothing to install: it compiles with the C# compiler that ships with Windows.

```
build.cmd
Bestie.exe
```

To bring it back each time you log in, right-click the creature and pick **live here (start with windows)**.

## Living with it

| do this | and it… |
|---|---|
| click it | gets petted (hearts, blushing). Don't pet it while it naps; it just smiles in its sleep. |
| click and **hold** it (don't move) | it listens. Talk while you hold, let go to send. Push-to-talk, basically. |
| drag it | gets carried. Let go while moving fast and you've thrown it. Bold ones love that, cautious ones hold a grudge. |
| double-click it | opens a chat |
| right-click it (or the tray icon) | shows the menu: chat, voice, look at your screen, its **diary**, its folder, *come here*, nap, settings, goodbye |

- The **diary** holds a hand-drawn map of your screens, the edges it has found, the windows it has named, its favorite spots, its traditions and habits, and what it thinks of you.
- It hides when something goes fullscreen (games, videos) and comes back after.
- **It's nosy.** About every 20 minutes (only while you're active), it waddles over to whatever you're working on and peers at it:
  - With an API key, it makes one short in-character remark about what it sees. That's a single low-detail screenshot of that monitor, so it's very cheap.
  - Without a key, it uses canned lines about the app it nicknamed.
  - It stays out of password managers, banking, private/incognito windows, sign-in and checkout pages. The skip list and the interval are both in settings, and you can switch it off there too.
- **It knows what you're listening to.**
  - It reads Spotify's window title for free, with no Spotify login needed. The web player works too, if the extension is connected.
  - It bops along while music plays, dances more often, rates every song out of 7 (and sticks to it), and remembers which artists you play.
- It notices when you've been away for a while and says hi when you come back.
- If you're curious, `Documents\Bestie\journal\` has a daily log of what it noticed.

## The assistant part

Paste an OpenAI API key into **settings** (right-click → settings…). The key is encrypted for your Windows account with DPAPI and stays on this computer. You can also set `OPENAI_API_KEY`.

- **Chat:** double-click the creature, or press **Ctrl+Alt+B**. Replies show up in its speech bubble.
- **Its voice:** it reads replies aloud with OpenAI's cheap `gpt-4o-mini-tts`, using the *ballad* voice and a direction written for the character. That works out to about 1.5¢ per minute of speech, and it starts talking in under half a second because the audio streams in.
  - Without a key, or if anything goes wrong, it falls back to the free Windows voice.
  - You can pick the voice or turn speech off in settings.
- **It sees your screens when you ask something:** every question includes a fresh low-detail screenshot of each monitor. You can turn that off in settings. Only the newest message keeps its screenshots, so costs don't pile up.
- **Voice:** there are two ways to talk to it, and OpenAI transcribes what you say.
  - Click and hold the creature, talk, and let go.
  - Press **Ctrl+Alt+V** and talk. It stops listening when you stop talking, or when you press the keys again.
- **Memory:** when you tell it things worth keeping, it writes them to `Documents\Bestie\memory\about-you.md`. That's a plain file you can read and edit. It can also keep notes in `notes\`, set reminders, search its memory and journal, open apps from your Start menu, open folders, and do tricks.
- **Costs:** it only calls the API when you talk to it, show it your screen or use voice. The creature's everyday life costs nothing. The model is set in settings and defaults to a cheap, fast one.

### AI sessions sidebar

Bestie keeps an eye on your other AIs and shows them in one place, **Claude** on one side and **ChatGPT** on the other:

- **Claude Code sessions** (CLI and Claude app) are read from `~/.claude/projects`. It can tell *working* from *done, your move*.
- **Codex sessions** (ChatGPT's coding agent) are read from `~/.codex/sessions`.
- **claude.ai and chatgpt.com tabs** come through the extension, which spots when a tab is mid-answer.
- **The Claude and ChatGPT desktop apps** show up when they're open.

While anything is working or waiting on you, a small tab sticks out of the right edge of your main screen, with a count for each side. Click it, press **Ctrl+Alt+A**, or pick *ai sessions* from the menu to open the list:

- Click a session to jump to it.
- Close tabs with ×.
- Start a *+ new chat* on either side.

When a session finishes, the creature tells you ("claude finished “fix login bug”. your move."). You can also ask it: *"which claude sessions are still running?"* / *"take me to the chatgpt one"*.

It only reads session logs on disk and window titles. Nothing about your sessions is sent anywhere unless you ask the assistant about them.

### Browser extension (optional)

With the extension, the assistant can see your open tabs and act on them: open, switch, close, read a page, read your selection, search.

1. Open `chrome://extensions` (also works as `edge://extensions` or `brave://extensions`).
2. Turn on **Developer mode**.
3. Click **Load unpacked** and pick the `extension` folder in this project.
4. With Bestie running, the extension's popup should say *connected to bestie ✓*.

The extension talks only to Bestie on `127.0.0.1:47823`. Bestie only accepts requests from the first extension that connects to it. If you reinstall the extension, use settings → *forget browser pairing*.

### What leaves your computer

Nothing, until you talk to it. When you do, OpenAI receives:

- your message
- the contents of `memory\`
- a short description of your desktop: window titles and which one is focused
- your tab titles and URLs, if the extension is connected
- a low-detail screenshot of each monitor with every question (turn that off in settings)
- any page text involved in that conversation
- your spoken words, if you talk to it, for transcription; its replies come back as speech

It also sends one low-detail screenshot of the monitor you're using on a nosy peek, about every 20 minutes while you're active. It skips private-looking windows, and you can turn this off.

The creature's own life, its brain and its journal stay local.

## Its folder

`Documents\Bestie\` (this may sit inside OneDrive if your Documents folder does):

```
memory\    what it knows about you (edit freely)
journal\   what it noticed each day
notes\     notes you asked it to keep
chats\     transcripts of your conversations
.state\    its brain, settings, logs
```

To start over with a brand-new egg, say goodbye to it and delete `.state\brain.json`.

## For tinkering

```
Bestie.exe --sheet out.png           # renders every pose and a few random creatures
Bestie.exe --simulate 120 out.txt    # runs 2 hours of its life, headless, against your real desktop
Bestie.exe --diary out.png           # simulates 4 hours and renders the diary
```

Set `BESTIE_TRACE=1` to log where it is and what it's doing to `.state\debug.log` every two seconds.

Code map:

- `src/Creature.cs`: body, physics, senses
- `src/Activities.cs`: choosing what to do, habits, traditions
- `src/Planner.cs`: getting around (jumps, drops, wall climbs, ceiling walks)
- `src/World.cs`: seeing windows and screens
- `src/Renderer.cs`: drawing it; no image files, it's all GDI+
- `src/Assistant*.cs`, `src/ChatWindow.cs`: the talking part
- `src/Bridge.cs` + `extension/`: the browser bridge
