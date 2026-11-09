# MiniDAW

![MiniDAW's banana logo](banana.png)

A small standalone Windows music program modelled on FL Studio's workflow. You build sounds in the **channel rack**, write them into **patterns**, place patterns in the **playlist**, and mix everything in the **mixer**.

All of it is one C# file, [MiniDAW.cs](MiniDAW.cs), with no dependencies. It uses only what ships with Windows: WinForms for the window and `winmm` for audio and MIDI.

## Download and install

1. Open the [latest release](https://github.com/grigorehunduu-droid/MiniDAW/releases/latest) and download **MiniDAW-Setup.exe**.
2. Run it and click **Install**. It installs for your Windows user only (no admin rights), into `%LOCALAPPDATA%\Programs\MiniDAW`, with a Start-menu shortcut and, if you leave the box ticked, a desktop one.
3. Windows may say **"Windows protected your PC"**. Click **More info → Run anyway**. This happens because the installer isn't signed with a paid code-signing certificate yet, not because anything is wrong with it; the whole source is here to check. You only see it once: updates through the app's **Update** button don't trigger it.

Needs Windows 10 or 11.

**Updates:** each time MiniDAW starts, it asks GitHub whether a newer release exists. If there is one, a green **⬆ Update to x.y** button appears in the toolbar: click it, save your project if asked, and MiniDAW downloads the new installer, closes, updates itself and opens again. Nothing is downloaded without that click. The check sends one ordinary web request to github.com and nothing else; with no internet it is silently skipped.

**Uninstall:** Windows **Settings → Apps → MiniDAW → Uninstall**. Your projects, WAV files and `%APPDATA%\MiniDAW` (recent list, autosave) are left alone.

## Run and build

- **Run:** double-click `MiniDAW.exe`. A banana splash shows for about a second while it starts (click it to dismiss it sooner). **F1**, or the banana at the right end of the toolbar, opens the About window.
- **Build:** run this in this folder (it uses the C# compiler that ships with Windows):
  ```
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:banana.ico /out:MiniDAW.exe MiniDAW.cs
  ```
- **Logo:** the banana is drawn in code, so it's sharp at every size: in the title bar, the taskbar, and on `MiniDAW.exe` itself. [banana.ico](banana.ico) and [banana.png](banana.png) come from `MiniDAW.exe --logo banana.ico`. To build from scratch without them, leave out `/win32icon:banana.ico`; the window still gets its banana.
- **Self-test:** `MiniDAW.exe --selftest result.txt` runs 147 checks and writes them to `result.txt`. The exit code is 0 when all of them pass.
- **Installer:** [Setup.cs](Setup.cs) builds `MiniDAW-Setup.exe`, with `MiniDAW.exe`, this README and the logo embedded in it. Its build line is at the top of the file.
- **Releasing a new version:**
  1. Raise `AboutForm.Version` in MiniDAW.cs, for example from 1.1 to 1.2, and commit.
  2. Run `powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "What changed"`.

  [release.ps1](release.ps1) builds the app and runs the self-test, stopping if anything fails. It then builds the installer, pushes, and publishes GitHub release `v1.2` with `MiniDAW-Setup.exe` attached. It needs the GitHub CLI, signed in (`gh auth login`). Installed copies pick up the release the next time they start.

## The screen

| Area | What it does | How to use it |
|---|---|---|
| **Toolbar** | Transport and files | Play / Stop · Mode (Pattern or Song) · BPM (type it, scroll the wheel over it, or press ↑/↓) · pattern list · + Pattern · + Sound · + Sample… · New · Open… · Save… · Export… · Mixer (F9) · MIDI device · ● Rec · metronome · 🍌 About |
| **Channel rack** | One row per sound | The dot mutes. Click a name to edit it in the piano roll; double-click it for **sound settings**; right-click it for **sound settings, a different sound, rename or delete**. Drag the volume and pan bars; right-click them to automate them. The number box picks the mixer insert: click = +1, right-click = −1, or drag up / down to go straight to any of the 125. Click the 16 steps to add drum hits. |
| **Piano roll** | Notes of the selected channel in the current pattern | Click an empty cell to add a note (drag right while adding to make it longer). Click a note to remove it. **Drag a note** to move it in time and pitch: if it's part of the selection, all selected notes move together, and you hear the new pitch while dragging. Moves stop at the pattern's edges, and a note dropped on another one replaces it. **Drag a note's right edge** to change its length. Drag in the **VEL** lane at the bottom to set **velocity** (how hard each note is played): 100 is the channel's normal level, 50 is half as loud, 127 is the loudest. A drag across several notes sets each one. |
| **Mixer** (F9 swaps it with the piano roll) | Master + 125 inserts | See below. |
| **Playlist** | 500 tracks, songs up to 999 bars | Click an empty spot to place the selected pattern (drag to keep placing); right-click a clip to erase it. **Drag a clip** to move it (**Shift**: copy); **click** it to edit its pattern. More under **Playlist** below. Plays in **Song** mode. |

**Graph editor:** click **graph** in the channel rack's header to open a strip under the rack for the selected channel. Click it again to cycle through **velocity → pan → pitch → off**; right-click goes backwards.
- Drag up and down across the steps to set each note's value. Right-click a step to put it back: velocity 100, centre, no detune.
- **Pan** is added to the channel's own pan.
- **Pitch** is fine tuning of up to ±100 cents (one semitone).
- The piano roll's notes keep these values. Copy, paste and saving keep them too.

**Colours and groups:** right-click a channel name.
- **Colour** tints its name tag, its lit steps and its notes in the piano roll.
- **Group** puts it in a group such as "Drums". Once any channel has a group, a **group** switch appears in the rack's header: click it to show only one group's channels, or all of them.
**Swing:** drag the **Swing** bar at the top right of the channel rack.
- It delays every off-beat 16th, meaning steps 2, 4, 6… counted from 1, for a shuffled groove. 0% is straight, 100% plays them half a 16th late (a 75/25 shuffle).
- It applies to the whole project, including exports, and is saved with it.
- The demo beat only uses on-beat steps, so put some hats on steps 2, 4, 6… to hear it.

**Piano roll:**
- **Getting around:** the mouse wheel scrolls up and down through all 128 keys, **Ctrl+wheel** zooms the keys, **Shift+wheel** scrolls across and **Ctrl+Shift+wheel** zooms across. **−** / **=** still jump to the computer keyboard's octave.
- **snap:** the grid that new notes, moves, lengths, recording and the tools use. Choices are beat, 1/8, 1/16, 1/32, 1/64, the 1/8, 1/16 and 1/32 triplets, or none (1/96 of a beat). New notes get the length of the last note you made or stretched.
- **Ghost notes:** the other channels' notes in this pattern show faintly behind yours.
- **scale:** pick a root and a scale (major, minor, harmonic minor, pentatonics, blues, dorian, mixolydian). Its notes light up, its root a little more, and the rest go dark.
- **stamp:** with a chord chosen (major, minor, 7th, maj7, min7, sus2, sus4, dim, aug, power, octave), one click adds the whole chord.
- **tools:** each works on the selected notes, or on all of them if none are selected:
  - **quantize** (also **Ctrl+Q**), or quantize lengths too
  - **chop** into snap-sized pieces
  - **strum**: each higher note of a chord starts 1/64 later and ends with the others
  - **arpeggiate** chords into a run at the snap
  - **slide notes**: instead of starting a note, a slide note bends the note already sounding to its pitch over its own length. It shows an arrow, and isn't written to MIDI files.
- **Bottom lane:** click its label to switch between **velocity**, **pan** and **fine pitch**. Drag to draw; right-click puts a value back to its default.
- **Glide (portamento):** in the sound settings window. With it on, the channel plays one note at a time and slides from each note into the next over up to 0.5 s.
**Pattern length:** right-click the **pattern list** in the toolbar to **rename / delete** a pattern or set its **length (1–4 bars)**.
- The rack shows every step, with a gap between bars, and the piano roll widens to match.
- Shortening a pattern drops notes past the new end, and you can undo that.

## Playlist

- **Getting around:** the mouse wheel scrolls through the 500 tracks, **Ctrl+wheel** zooms across, **Shift+wheel** scrolls across and **Ctrl+Shift+wheel** makes tracks taller or shorter.
- **Clips:**
  - Drag a clip's **right edge** to resize it. A clip longer than its pattern repeats the pattern.
  - Drag its **left edge** to trim off the front.
  - **Ctrl+click** splits a clip at the nearest grid line, and both halves play exactly what the whole did.
  - Clips show a small preview of their notes. A clip that hasn't been resized follows its pattern's length.
  - Clips on the same track may overlap; both play.
- **snap:** the grid for placing, moving and resizing clips: bar, beat or 1/16.
- **Tracks:** each track's tab has a light that mutes it. Right-click the tab to rename the track, colour it or mute it.
- **The ruler** (the bar numbers):
  - Drag along it to set a **loop region**; Song mode then plays just that part over and over. A click without a drag clears it.
  - Right-click it to add a **marker** with a name, or a **time signature** (2/4 to 7/4) from that point. The bar numbers, bar lines and the metronome's accents follow it.
## Sounds

**Built-in sounds:** add one with **+ Sound**, or change a channel's sound with right-click its name → **Sound**. They are all made by code, with no sample files. [sounds_demo.wav](sounds_demo.wav) plays each one for a bar.

| Sound | How it's made |
|---|---|
| Analog synth | Three oscillators (sine, saw, square, triangle or noise; each with its own octave or tuning and level), unison up to 7 voices spread left and right, a low/band/high-pass filter with its own envelope, an amp envelope and an LFO (on pitch, filter, volume or pan). 28 settings, all automatable; six presets. Saw and square are band-limited, so high notes don't alias |
| Saw lead | Saw wave through a soft low-pass |
| Square lead | Square wave, hollow and reedy |
| Pluck | Karplus-Strong string: a noise burst circulating in a one-period delay that softens on every pass |
| Pad | Three slightly detuned saws, slow to swell (0.3 s) and to fade (0.6 s) |
| Bass | Filtered square plus a sine an octave below |
| Electric piano | Two-operator FM: a sine bent by itself, the bend fading fast like a struck tine |
| Kick, Snare, Hat, Clap | Synthesized drums (pitch-swept sine, filtered noise bursts) |

**Sampler options** (a sample channel's sound settings):
- **Stretch:** make it up to twice as long or half as long without changing its pitch.
- **Reverse.**
- **Loop while held,** between a loop start and loop end; the note fades out 30 ms after you let go.

**Slices** (right-click the channel → **Slices**), like FL's Slicex:
- Cut a loop at its hits, or into 4, 8 or 16 equal parts. Then C5 plays the first slice, C#5 the second, and so on.
- **Put the slices in this pattern** lays them out as notes at the song's tempo, so the loop plays back as it was. Rearrange those notes to remix it.

**Browser** (**F8**, or **browser** in the channel rack's header): a panel on the left.
- **Synth presets:** click one to hear it, double-click to add it as a channel.
- **Folders:** shows a folder's WAV files, MIDI files, projects and subfolders. Click the folder line to pick another one; it's remembered.
  - Click a sample to hear it, and double-click it (or drag it onto the window) to add it.
  - Double-click a MIDI file to import it, or a project to open it.
**Samples:** **+ Sample…** loads a WAV file (16-bit, 24-bit or 32-bit float, mono or stereo). Stereo files keep their left and right sides, and the channel's pan works as a balance control. Note C5 plays a sample at its original pitch.

**Drag and drop:**
- Drop WAV files anywhere on the window: each becomes a sample channel named after its file, and one Ctrl+Z removes the whole drop.
- Drop a `.mdaw` project to open it; you are asked first if there are unsaved changes.
- You can also drop a project or WAV onto `MiniDAW.exe` itself, or use Windows' **Open with**.
- Other kinds of file are ignored.

**Sound settings:** double-click a channel name, or right-click it → **Sound settings…**. A small window opens showing the selected channel's settings and follows whichever channel you select. Drag a bar to change a setting; you hear the change right away, and each drag is one Ctrl+Z step.

| Setting | Range | Applies to |
|---|---|---|
| Attack | 0 to 2 s fade-in | the six synths |
| Release | 5 ms to 3 s fade-out after the note ends | the six synths |
| Tone | 25%–400% brightness (filter, FM depth, or string damping) | the six synths |
| Decay | 25%–400% of the drum's length | Kick, Snare, Hat, Clap |
| Pitch | ±24 semitones | everything except Hat and Clap, including samples |

**Arpeggiator:** the bottom half of the sound settings window (not for drums). With **Arpeggio** set to up, down, up & down or random, notes held together on that channel play one at a time instead of as a chord. This works both for chords in patterns and for keys held on a MIDI or computer keyboard, even while the song is stopped.
- **Range:** 1–4 octaves; each extra octave repeats the chord 12 semitones higher.
- **Time:** 1/32, 1/16, 1/8 or 1/4 notes.
- **Gate:** how much of each step a note lasts.
- Recording captures the keys you held, not the arpeggio. MIDI export writes the chord too.
- Every sound starts at its built-in setting, so 100% means unchanged.
- Switching a channel to another sound resets its settings.
- Settings are saved with the project.

## Mixer

The master stays on the left; the 125 inserts scroll beside it with the mouse wheel. Each insert has:
- a fader, pan, a mute dot, a level meter, and a list of the channels feeding it
- **8 effect slots**, processed top to bottom:
  - Click "+ effect" to add one.
  - Each insert shows one effect's settings at a time (▾). Click another effect's row (▸) to show its settings instead, then drag them.
  - Right-click a slot, or click the open one again, to change or remove its effect.
  - **Drag** an effect's row up or down to move it in the chain (an orange line shows where it will land). Its automation lanes move with it.
  - The small **bar** next to the dot is the effect's **wet/dry** mix: 100% is fully processed, 0% leaves the sound untouched, and anything in between blends the two (try a heavy Compressor at 40% for parallel compression). Drag it to set it, or right-click it to automate it. It works on every effect, separately from the Mix setting some effects have.
  - The **dot** at the right of each effect switches it on (green) or off (empty ring). An effect that is off is skipped completely, keeps its settings, and is saved that way.

| Effect | Settings |
|---|---|
| Delay | Time (20 ms–1 s), Feedback, Mix. The echoes bounce between left and right. |
| Reverb | Size, Damp, Mix (a Freeverb-style design) |
| Filter | Cutoff (40 Hz–18 kHz), Resonance, Mode (low-pass / band-pass / high-pass) |
| Distortion | Drive, Tone, Mix |
| EQ | Low / Mid / High, each ±12 dB (the middle of the bar = no change) |
| Compressor | Threshold (0 to −48 dB), Ratio (1:1–20:1), Gain (make-up, up to +24 dB) |
| Limiter | Ceiling (0 to −12 dB), Release (10–500 ms), Gain (drive into the ceiling, up to +24 dB). It looks 5 ms ahead, so no peak ever gets past the ceiling: put it last on the Master for a loud, safe mix |
| Chorus | Depth, Rate (0.05–5 Hz), Mix. Two slightly delayed copies sway a quarter-cycle apart left and right: wider and thicker |
| Flanger | Depth, Rate, Feedback. A very short swept delay fed back into itself: the classic jet-plane sweep |
| Phaser | Depth, Rate, Feedback. Six swept all-pass stages: a softer, swirling sweep |

- **Routing** (the **out** pill under the pan):
  - **To the master** switches whether the insert plays into the master.
  - Add any number of **sends** to other inserts, each with its own level. Sends go up or down the list, so this is how to share one reverb between tracks, or build a drum bus.
  - The bar beside **sc** shows the first send's level: drag it, or right-click it to automate it.
- **Sidechain** (**sc**): another insert's sound drives this insert's **Compressor** and **Vocoder**. For example, set Insert 4 (Synth) to sidechain from Insert 1 (Kick) and drag Threshold right: the synth ducks on every kick.
- Any route that would loop back on itself is never offered. The mixer works out the right order to process the inserts.

| More effects | Settings |
|---|---|
| Parametric EQ | 5 bands: low shelf, three peaks, high shelf. Its slot shows the curve; click it to open the editor. Drag a numbered handle for frequency (across) and gain (up and down), use the wheel over it for width (Q), and right-click it to flatten it. The curve is the EQ's true response. |
| Multiband | Depth, Speed, Gain. Splits the sound at 200 Hz and 4 kHz and compresses each band on its own, like FL's Maximus: dense, even loudness. At zero depth it's perfectly transparent |
| Beat FX | Mode (gate, stutter, half speed, reverse), Rate (1/4–1/32), Mix. Chops the sound in time with the song, after FL's Gross Beat. It only acts while the song plays |
| Vocoder | Release, Bright, Mix. Put a synth on the insert and a voice (or any sound) on another insert, then set this insert's **sc** to that one: the synth "speaks". With no sidechain it does nothing |
## Automation

**Right-click any control that can change during a song:**
- in the mixer: fader, pan, send level, an effect's setting or wet/dry
- in the channel rack: volume or pan
- in a sound settings window: Attack, Release, Tone/Decay or Pitch
- the **BPM** box

The menu offers:
- **Create automation clip:** an automation clip for that control appears on the first free playlist track, flat at the control's current value, and its editor opens.
  - Automation clips move, stretch, trim, split and copy like pattern clips. A clip only acts where it sits; before and after it, the control keeps its value.
  - Click a clip to open its editor.
  - Erasing a lane's last clip deletes the lane.
  - The **automation ▾** pill at the playlist's top right lists every lane, to edit it, add another clip of it, or delete it.
  - Clips play in **Song** mode and in exports, and the controls visibly move along with them.
- **Link to MIDI controller…:** then move a knob or slider on your MIDI controller. From then on that knob moves the control, live, like FL's "Link to controller".
  - **Esc** cancels the waiting.
  - **Unlink** in the same menu removes it.
  - Links are saved with the project.

**The automation editor:**
- Click to add a point, drag it to move, and right-click it to delete.
- Drag the small circle between two points to bend that stretch of curve.
- **LFO ▾** fills the lane with a sine, triangle, square or saw wave, repeating every 2 bars down to every 1/8.
- **Flatten** resets the lane to one level.
## Playing and recording notes

- **MIDI keyboard:** pick it in the toolbar's MIDI list. It plays the channel selected in the rack, and how hard you hit a key sets the note's velocity, both live and when recording.
- **Sustain pedal:** a pedal plugged into the MIDI keyboard works. While it's down, keys you let go keep sounding until the pedal comes up. When recording, those notes stay as long as you actually heard them.
- **Pitch wheel:** bends every note you're playing from the keyboard, including ones already sounding, by up to ±2 semitones. Notes from patterns aren't bent, and the bend isn't recorded.
- **Mod wheel:** adds vibrato to the notes you're playing from the keyboard. The pitch wobbles 5.5 times a second, by up to ±½ semitone at full wheel. Like the pitch wheel, it leaves pattern notes alone and isn't recorded.
- **Computer keyboard:**
  - **Z S X D C V G B H N J M** = C4 to B4
  - **Q 2 W 3 E R 5 T 6 Y 7 U I** = C5 to C6
  - **−** and **=** shift the keyboard down or up an octave, up to 3 either way (from C1 up to C9). The piano roll's view moves along, so it always shows the 3 octaves you're playing; its header shows the current range, e.g. "keys Z–M, Q–I play C3–C5".
- **Record:** turn on **● Rec**, use **Pattern** mode, and press Play. Notes go into the current pattern on the selected channel. They snap to 16ths, and the app allows for the audio delay so they land where you heard them. Stopping playback ends the take.
  - **Count-in:** recording starts with one bar of metronome clicks, then the pattern begins. Keys you press during the count-in aren't recorded. Right-click the metronome button to turn the count-in off.
  - **Loop recording:** the pattern keeps looping while you record, and every pass adds its notes on top of the earlier ones, so you can build a part up layer by layer.
- **Metronome:** the button next to **● Rec** (or **Ctrl+M**) clicks on every beat, higher on the first beat of each bar. It's only for you: exports never include it, and the master fader doesn't change its volume.

## Export

**Export… → Audio (WAV, FLAC, MP3)…** saves what Play would play: the whole arrangement plus its reverb/delay tail in **Song** mode, or the current pattern once in **Pattern** mode. A small window asks for:

| Format | Quality | Notes |
|---|---|---|
| WAV | 16-bit, 24-bit, 32-bit float | 32-bit float keeps peaks above full scale |
| FLAC | 16-bit, 24-bit | Lossless: sounds exactly like the WAV at roughly half the size. MiniDAW encodes it itself |
| MP3 | 128, 192, 256, 320 kbps | Uses the MP3 encoder built into Windows (on Windows "N" editions, install the Media Feature Pack) |

Everything is 44.1 kHz stereo. Tick **stems** to also save every mixer insert that carries sound as its own file next to the main one, named like `song - Insert 2 (Snare).flac`. Each stem is that insert's output after its effects and fader, and all of them line up with the main file, so you can mix them elsewhere.

The export window remembers your choices until MiniDAW closes.
## MIDI files

- **Export:** **Export… → MIDI file (.mid)…** saves the song (Song mode) or the current pattern (Pattern mode) as a standard MIDI file that other music programs open.
  - Each channel becomes its own track, named after it, with a matching General MIDI instrument.
  - Drums go on MIDI channel 10 as General MIDI drum notes.
  - A tempo lane becomes tempo changes.
  - Notes are written as you placed them, without swing. Muted channels are left out.
- **Import:** **Open… → Import MIDI file…**, or drop a `.mid` file on the window. The notes are added to the current project:
  - Every instrument in the file gets a new channel, with the closest built-in sound: drums by General MIDI drum note, everything else by its General MIDI instrument.
  - The notes go into new patterns of up to 4 bars, placed one after another on the first free playlist track.
  - A summary says what was added, and one Ctrl+Z takes it all back.
  - Timing is kept to 1/96 of a beat, so triplets and swung playing survive. A note that crosses a 4-bar boundary is cut there.
  - The project keeps its own tempo; the summary tells you the file's.

## Shortcuts

| Key | Action |
|---|---|
| Space | Play / Stop |
| F9 | Mixer ↔ piano roll |
| F8 | Browser |
| F1 | About MiniDAW |
| Ctrl+M | Metronome on / off |
| Ctrl+Z | Undo (each click or drag is one step, and a recording take is one step; up to 100 steps) |
| Ctrl+Y or Ctrl+Shift+Z | Redo |
| Right-drag in the piano roll | Select notes (right-click without dragging clears the selection) |
| Ctrl+A | Select all notes of the selected channel in this pattern |
| Ctrl+C / Ctrl+X | Copy / cut the selected notes (with nothing selected: the whole channel in this pattern) |
| Ctrl+V | Paste onto the selected channel and pattern, at the same steps. Notes that don't fit are dropped. If the copied notes start past a shorter pattern's end, they move back into bar 1. |
| Ctrl+B | Duplicate the selection right after itself, rounded up to whole beats (one bar → the next bar). The new copy stays selected, so pressing Ctrl+B again keeps going. |
| Delete | Delete the selected notes |
| Ctrl+Q | Quantize the selected notes (or all) to the snap |
| Wheel / Ctrl / Shift / Ctrl+Shift | Piano roll: scroll keys / zoom keys / scroll across / zoom across |

## Files

- **Autosave:** every minute, unsaved work is quietly copied to `%APPDATA%\MiniDAW\autosave.mdaw`. If MiniDAW closes without the usual "Save changes?" (a crash or power cut), the next start offers to recover it. Recovered work opens unsaved, marked "(recovered)", so save it to keep it. Saving, discarding or closing normally deletes the autosave. Running two MiniDAWs at once shares the one autosave file.
- **Recent projects:** **Open…** lists the last 8 projects you opened or saved, newest first, then **Browse…**. Hover an entry to see its full path. Files that were moved or deleted are left out. The list is kept per Windows user in `%APPDATA%\MiniDAW\recent.txt`.

- Projects are saved as `.mdaw` files: tab-separated text with one line per channel (including its sound settings), insert, effect, pattern note, playlist cell and automation lane.
- Samples are stored as file paths, so keep the WAVs where they are.
- Opening a project checks every value and refuses broken files instead of loading half of them.
- Files from older MiniDAW versions still open.

## What it learned from FL Studio

From looking at FL Studio's shipped files only, without decompiling anything:
- FL64.exe is a small launcher: it checks the program's signature and then loads `FLEngine_x64.dll`, which is the real app and exports `CreateFruityInstance`. The Delphi runtime markers in it suggest FL is written in Delphi.
- Instruments and effects are separate DLLs that each export `CreatePlugInstance`.
- It bundles a .NET part (likely the FL Studio Mobile engine) and Python 3.12, likely for MIDI scripting.
- **.flp project files** are an "FLhd" header followed by an "FLdt" chunk of small tagged events: tempo, channels, patterns, notes and playlist.

MiniDAW copies the *workflow* (rack → patterns → playlist → mixer inserts → automation), not FL's code or file format.

## License

MiniDAW is open source under the [MIT License](LICENSE): use it, change it, share it, sell it, as long as the license text stays with it. Contributions are welcome as pull requests.

## Limits

- Patterns are 1 to 4 bars long (stretch a clip to repeat one). Songs can be up to 999 bars.
- Time signatures change bar numbering, bar lines and metronome accents; patterns still count in 16ths.
- Velocity only changes loudness, not tone. The computer keyboard always plays at velocity 100.
- Swing is one setting for the whole project, not per channel. Recording doesn't take swing into account when snapping notes.
- The pitch wheel (±2 semitones) and the vibrato (5.5 Hz, ±½ semitone) can't be adjusted, and they don't affect Pluck or the drums. The computer keyboard has no sustain, pitch or mod controls.
- Sidechain drives the Compressor and the Vocoder. The Parametric EQ's settings can't be automated yet.
- Mixer controls, rack volume/pan, sound settings and tempo can be automated, and linked to MIDI controllers. Mixer changes take effect once per audio block (about 23 ms).
- No VST plugins. Export is 44.1 kHz only.
- Audio uses the classic Windows `waveOut` output with about 0.1 s of delay. No ASIO.
- Recording works only in Pattern mode and only adds notes (overdub).
- MIDI import keeps timing to 1/96 of a beat; a song is placed on the playlist up to its 999-bar limit.
