# Minou

A live status notch for Claude Code on Windows. Minou adds a small black notch at the top of your screen, in the
style of the MacBook notch, that shows what Claude Code is doing in every open session, so you can stay in your
browser or editor and still know the moment Claude needs you.

**Windows 10 and 11 only.** The plugin works in Claude Code; it does nothing on claude.ai or in Cowork, which don't
run plugin hooks.

## Install

```
/plugin install minou --marketplace 0xManusdev/minou
```

Run `/reload-plugins` in sessions that are already open. The notch shows up at the next Claude Code event.

## What you see

- **Idle**: a thin pill, out of the way.
- **Working**: the notch widens with an animated pixel grid and the current activity, such as
  `Terminal · npm run build` or `Edit · page.tsx`, plus the number of sessions and Minou's two little eyes, which
  blink at a natural, human pace and change with the state: wide open when an approval is waiting, "^ ^" when a task
  is done, "> <" on an error, sleepy when nothing is going on.
- **Needs approval**: a red grid and the tool waiting for your permission, with an optional Windows sound.
- **Done**: a green check for a few seconds, then back to the pill.

Hover the notch to expand it into a list of sessions with their project, activity and elapsed time. Click a session to
bring its terminal window to the front (Windows Terminal, classic console, VS Code, JetBrains IDEs). A tray icon mirrors
the state and holds the settings: screen, sounds, idle pill, full-screen behaviour.

## How it works

The plugin registers asynchronous Claude Code hooks that run a small native helper, `app/Minou.exe`, in hook mode. The
helper forwards each event over a named pipe that only your Windows account can write to, and the same executable
draws the notch. Minou appears when a Claude Code session starts and leaves when the last one closes. It never blocks
Claude, sends nothing over the network and stores only its settings and session list in `%LOCALAPPDATA%\Minou`.

The interface text is currently in French. Source code, build script and documentation:
<https://github.com/0xManusdev/minou>.

## Credits

Animation patterns and notch shape are adapted from NotchAgent for macOS by TeamNoSleepz (MIT). Bundled font: Inter
Display by The Inter Project Authors, under the SIL Open Font License 1.1. Minou itself is released under the MIT license.
