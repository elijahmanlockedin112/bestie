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

Bestie adds itself to the Start menu the first time it runs. To pin it: press Start, type “bestie”, right-click it, and choose **Pin to taskbar**. Clicking it while Bestie is already running calls it over to your mouse.

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

**It thinks with Claude** (since Oct 30, 2026): Claude Haiku 5.5 answers everything, with Claude Sonnet 5.5 as the second try when Haiku fails. The Claude key is `ANTHROPIC_API_KEY`, from the environment or read at run time from Synaptic's `.env` (`learning-hub\apps\synaptic\.env`). Every Claude call is written to the shared spend ledger `C:\Users\elija\Synaptic\anthropic-spend.jsonl` (process `bestie`, filed by feature: chat, visual, memory, nosy peek…), failed calls too, and shows up on Synaptic's Claude usage page. No call goes out if the ledger can't be written, or once this month's ledger total (all apps) reaches $100. Code: `src/Claude.cs` (key, ledger) and `src/OpenAIClaude.cs` (Claude answers the same request shape the OpenAI code builds).

An OpenAI key in **settings** (right-click → settings…) is still used for the OpenAI voice and ears when those are chosen. The key is encrypted for your Windows account with DPAPI and stays on this computer. You can also set `OPENAI_API_KEY`. When OpenAI is out of credit, it hears through ElevenLabs instead.

- **Visual explainers:** ask how something works ("how does this tensor thing make training data?", "can a plane really hover like this?", "show me how to attach a camera to a raspberry pi") and it draws an animated diagram right on your screen while it talks you through it, step by step. It starts from what's on your screen: it can circle the real thing you're looking at and draw an arrow from it into the diagram. Things are drawn as objects (shaded shapes, 3D blocks and cylinders, parts grouped so a whole jet moves or a latch swings on its hinge), and they move, spin, turn and flow to show how they work; each spoken line shows as a caption. Claude Haiku 5.5 writes it (setting `visualModel`; `claude-sonnet-5-5` draws more carefully at ~28x the price): a short plan first, then it streams, so the first drawing appears in about 2.5 seconds and the rest draws while it talks. Shapes that land on top of each other slide apart on their own. Ask a follow-up ("explain more", "now show the engine") and it builds on the last one. Click the board or say "close it" to put it away; it puts itself away after 3 minutes. About $0.0006 each on Haiku (measured over 7, with screenshots); $0.017 on Sonnet. Turn it off with `showVisuals` in settings.json. Each one's drawing (never the screenshot) is kept in `.state\visuals\`. Code: `src/Visuals.cs` (prompt, call, timing, window) and `src/VisualScene.cs` (the drawing).

- **Chat:** double-click the creature, or press **Ctrl+Alt+B**. Replies show up in its speech bubble and in the chat window, which draws them instead of printing them (`src/ChatView.cs`):
  - code as a dark card with syntax colours, real line numbers, the broken line highlighted, and a copy button
  - a fix as a card with the file and line on top, old lines in red, new ones in green, and "copy fixed"
  - steps as numbered circles, shortcuts like `Ctrl+S` as key caps, a swatch next to a colour, tips, tables, and small bar charts for numbers
  - cards for anything with a shape (`src/ChatCards.cs`): weather with day tiles, your day as a timeline (done / now / next), comparisons with its pick, a recipe with a tickable ingredient list, a word's meaning, a song rated out of 7, progress bars, key facts. It writes a small ```` ```ui ```` JSON block and the chat draws it with line-drawn icons. Choice buttons send what you click as your next message; checklist items tick.
  - right-click a message to copy it. The voice skips code, charts and tables and says "it's in the chat" instead; a card is said through the sentence beside it.
  - the compose box grows as you type; its icons attach a screenshot, start the mic (it turns orange while listening) and open settings.
- **Its voice:** it reads replies aloud with OpenAI's cheap `gpt-4o-mini-tts`, using the *ballad* voice and a direction written for the character. That works out to about 1.5¢ per minute of speech, and it starts talking in under half a second because the audio streams in.
  - Without a key, or if anything goes wrong, it falls back to the free Windows voice.
  - You can pick the voice or turn speech off in settings.
  - It's played about 2.4× louder, with a limiter so it doesn't crackle.
  - **Everything else ducks:** while it talks or listens, Spotify, videos and every other app drop to 20% volume, then fade back. If you move an app's volume yourself in the meantime, it leaves that app alone.
- **It sees your screens when you ask something:** every question includes a fresh low-detail screenshot of each monitor. You can turn that off in settings. Only the newest message keeps its screenshots, so costs don't pile up.
- **Voice:** there are three ways to talk to it, and OpenAI transcribes what you say.
  - **The wake word:** say its name, with or without "hey": *"hey Mothball, open youtube"*, *"bestie, what's on my calendar"*, *"blorp, play something"*.
    - You can also say just the name, wait for *"yes?"*, and keep going.
    - A golden **halo** lights up the instant it hears its name, so you know it's listening.
    - Change its name with *rename…* and its extra names with *wake words…*, both in the right-click menu.
    - The listening for the wake phrase happens entirely offline with the Windows speech recognizer. Nothing is sent anywhere until it hears the phrase.
    - You can turn it off in settings.
  - Click and hold the creature, talk, and let go.
  - Press **Ctrl+Alt+V** and talk. It stops listening when you stop talking, or when you press the keys again.
  - Right after it answers out loud, it keeps listening for about 4 seconds (its halo glows), so you can reply without the wake word. If you say nothing, it stops quietly. Turn this off in settings.
- **While it talks,** it stands still and keeps what it's saying on screen until the voice finishes.
- **Reminders, calendar heads-ups and finished Claude or ChatGPT sessions are read out loud.** If it's already talking or listening, it waits its turn instead of cutting in.
- **Memory:** when you tell it things worth keeping, it writes them to `Documents\Bestie\memory\about-you.md`. That's a plain file you can read and edit. It can also keep notes in `notes\`, set reminders (one-time or repeating: every night, weekdays, mondays and thursdays…), search its memory and journal, open apps from your Start menu, open folders, and do tricks.
- **About me (private):** right-click it → **about me (private)…** and write whatever you want it to know: who you are, what you're into, how you like answers. It keeps this in mind with every answer. It's saved encrypted in `.state\about-me.dat` and never copied into its log, memory folder, notes or chats. There's no length limit, but it's sent with every question, so a very long one costs a little more per question (the box shows roughly how many tokens). To remove it, clear the box and save.
- **Point at things:** point at something with your mouse (or circle it) while you ask “what's this?” or “make this bigger”. It gets your pointer's path drawn on the screenshot, a sharp close-up of the spot, and what Windows says is there (the button, link or line of text), so it knows exactly what you mean. Password fields are never read.
- **Time blocks:** a widget for working in 90-minute focus blocks with 15-minute breaks, on repeat (or 50/10, 25/5). Start it from the widget or say “start my time blocks”. It warns you 5 minutes before a break. When the break starts, a bright, slowly shifting screen covers every monitor, and the Windows key and Alt+Tab are held back. It stays until the break ends or you type I NEED TO ACCESS MY COMPUTER. Ctrl+Shift+Esc and Ctrl+Alt+Del always still work.
- **YouTube callouts:** when YouTube comes to the front (the tab you're looking at, not one sitting in the background), it tells you to get off it, out loud and once. After that it waits 10 minutes before it says anything again. It stays silent during quiet time and focus-block breaks. YouTube that was already open when Bestie started doesn't count. On a real break? Say “youtube break, 20 minutes”, or right-click it → **youtube break (30 min)**. If you're still on YouTube when the break ends, you'll hear about it. Turn it off with right-click → **get mad when i go on youtube**. Nothing is ever blocked or closed.
- **Brain dump:** say “brain dump” (or right-click it → **brain dump…**) and just talk, for up to 8 minutes. Pauses to think are fine. It stops when you go quiet for about 7 seconds or click it. One AI call sorts everything into a page: a short summary, a “start here” step, to-dos (grouped, with checkboxes), what's on your mind, coming up, ideas, and questions to figure out. The page opens in your browser. Then it offers to put the to-dos on your to-do list; just say yes. Typing or dictating a long messy message works too. Pages live in `notes\brain dumps\`, and **my brain dumps** in the menu lists them all.
- **Costs:** it only calls the API when you talk to it, show it your screen or use voice. The creature's everyday life costs nothing. The model is set in settings and defaults to a cheap, fast one.

### Wispr Flow

If [Wispr Flow](https://wisprflow.ai) is installed, Bestie plugs into the real app. It doesn't use an API; it uses the app itself.

- **Wispr is its ears:** when you hold the creature, Bestie presses Wispr's push-to-talk for you. It reads the shortcut from Wispr's config; yours is Ctrl+Win. Wispr types into a little box by the creature, and Bestie sends that the moment you let go.
- **"hey Mothball" works in any dictation:** if a dictation anywhere starts with its name, Bestie acts on it.
- **It listens along:** while you dictate anywhere, the creature stops, leans in and cups an ear.
- **Optional, off by default:** the assistant can see your last few dictations as context.

All four switches are under **wispr flow** in the right-click menu. Bestie reads Wispr's local history (`flow.sqlite`) read-only, using the SQLite that's built into Windows. If a Wispr update changes that file, those features stop working quietly; nothing breaks.

### Widgets

Right-click → **widgets** puts little paper cards on your desktop:
- **to-do:** check things off, or right-click to delete. Its list is `Documents\Bestie\todo.md`, a plain checklist you can edit by hand.
- **calendar:** this week's events and your Bestie reminders, plus a box to add an event ("dentist fri 3pm").
- **now playing:** what's playing on Spotify, Mothball's rating out of 7, and play/skip buttons.
- **sticky notes:** as many as you want, in five colors. Each is saved to `notes\`.
- **tab switches:** a live count of how many times you switched tabs or windows today (since 4 am) and in the last hour. It counts a new tab in the browser (by the extension's tab id; without the extension, by the tab's title) or a different window in front. It checks every 2 seconds, so anything you flash past faster than that isn't counted. Bestie's own windows and coming back from being away don't count. The count survives a restart (`.state\switches.json`) and starts over at 4 am. If you switch 15 or more times in 5 minutes, Mothball tells you to pick one thing and the number turns pink. It says this once, then waits at least 15 minutes before saying it again. It stays silent during quiet time and focus-block breaks, and it only talks: nothing gets closed. To turn it off, right-click → **warn me when i'm tab-hopping**.

You can drag them by the header, resize them from the corner, and use the pin to keep one on top. The assistant can use them too: *"add milk to my to-do"*, *"put my calendar on the desktop"*, *"stick a note that says call abby"*.

The creature treats widgets like any other window: it'll stand on your to-do list (its "Clipboard") and nap on your sticky notes.

### Google Calendar

**Let it see your real calendar (30 seconds):** right-click → **let me see my calendar…**. Google Calendar's settings open in your browser:
1. Click your calendar on the left.
2. Scroll to **Integrate calendar**.
3. Copy the **secret address in iCal format** and paste it into Bestie.

The link is stored encrypted for your Windows account and can only read. With it:
- The calendar widget shows your week, including repeating events and the ones you moved or skipped.
- The assistant knows your next few hours, so you can ask "what's my day look like".
- It warns you about 10 minutes before each event.

To stop, right-click → **my calendar: connected (disconnect)**.

**Adding events** needs no setup at all: you can ask it to add an event ("add dentist friday 3pm"). It opens a prefilled Google Calendar event in a background tab of your browser, presses Save, and closes the tab. This needs the browser extension at version 1.2 (hit **reload** in `chrome://extensions`) and you signed in to Google in Chrome. Without the extension, it opens the prefilled event and you press Save yourself. It can also read your upcoming schedule from Google Calendar's agenda page.

**With the full connection**, it can also move and delete events (always asking you first with a yes/no pop-up), fill the calendar widget, and warn you about 10 minutes before each event. Setup takes about 5 minutes, once:

1. Go to [console.cloud.google.com](https://console.cloud.google.com) and create a project called "Bestie".
2. Under **APIs & Services**, enable **Google Calendar API**.
3. Set up the **OAuth consent screen**:
   - Choose External, name the app Bestie, and enter your email.
   - Add yourself as a test user.
   - Set **Publishing status → In production**. Otherwise Google expires the connection every 7 days.
4. Go to **Credentials → Create credentials → OAuth client ID → Desktop app**, and copy the ID and secret into Bestie's settings.
5. Click **connect google calendar…**.
   - Google will say it "hasn't verified this app". Click **Advanced → continue**; it's your own app.

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

### Working in your tabs

With the extension connected (version 1.3; hit **reload** in `chrome://extensions` after updating), the assistant can work in **any** tab, even ones you're not looking at:
- load a page, reload, go back or forward
- mute, pin, duplicate, move or group tabs
- read long pages part by part
- find the links, buttons and fields on a page, then click, type, pick from dropdowns and scroll

For example: *"in my gmail tab, search for the FBLA email"*, *"mute the youtube tab"*, *"group my school tabs"*.

Page content is treated as untrusted. Anything that looks like it sends, buys, deletes, posts, submits or signs out pops up a yes/no near the creature first; no answer in a minute means no. It never types into password fields or types anything that looks like a password, key or card number.

### Browser extension (optional)

With the extension, the assistant can see your open tabs and act on them: open, switch, close, read a page, read your selection, search.

1. Open `chrome://extensions` (also works as `edge://extensions` or `brave://extensions`).
2. Turn on **Developer mode**.
3. Click **Load unpacked** and pick the `extension` folder in this project.
4. With Bestie running, the extension's popup should say *connected to bestie ✓*.

The extension talks only to Bestie on `127.0.0.1:47823`. Bestie only accepts requests from the first extension that connects to it. If you reinstall the extension, use settings → *forget browser pairing*.

### What leaves your computer

Nothing, until you talk to it. When you do, Anthropic (Claude) receives the list below; OpenAI or ElevenLabs only get your voice (to hear it) and its replies (to say them). Claude receives:

- your message
- the contents of `memory\`
- your private "about me" text, if you wrote one
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
brain.md   a readable copy of everything it has learned (updated every couple of minutes)
memory\    what it knows about you: about-you.md, conversations.md, noticed.md (edit freely; it reads these)
journal\   what it noticed each day, written the moment it happens
notes\     notes you asked it to keep
chats\     transcripts of your conversations, written as you talk
.state\    its brain (saved every 15 seconds), settings, logs
```

It saves constantly. The journal and chat transcripts are appended as things happen, and the brain is saved every 15 seconds and right away when it learns something big.

About a minute after a conversation goes quiet, one small AI call reads it and writes down anything it learned about you (people, school, projects, events, what you're into, preferences) in `memory\about-you.md`, plus a one-line summary in `memory\conversations.md`. You'll see *"(wrote that down. i'll remember.)"* when it does. It reads both files before every conversation, so it remembers across days. Passwords, keys and card numbers are never stored. Anything it hasn't processed yet waits in `.state\memory-pending.txt`, so quitting doesn't lose it.

Once a day it also reads its own journal (what it saw you doing: apps, sites, music, what you asked for) and keeps `memory\noticed.md` up to date with patterns, like what you work on and when, and what you're into lately. That's one small AI call a day, and the journal text goes along with it.

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
