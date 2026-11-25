# Vocal FX prototype

During a song, open **Pause → Settings → Audio Settings**. Select a singer's Vocal FX row to cycle through Auto, Standard, Reverb, Delay, Distortion, Auto-Tune and Talkbox. Each live singer with an assigned microphone gets a row. The choice lasts for this song session. Use headphones and the existing Monitoring control to hear the live mic.

## Live gamepad controls

Assign a standard dual-stick gamepad and microphone to the same vocalist profile. During gameplay, point the left stick toward an effect to select and hear it immediately. Up selects Auto. Clockwise from Auto are Standard, Reverb, Delay, Distortion, Auto-Tune and Talkbox. Release the stick to keep your selection. The wheel fades after 1.25 seconds of inactivity.

The right stick adjusts the selected manual effect. Push horizontally or vertically to increase or decrease a value, or diagonally to adjust both. Release to hold. Each singer remembers their settings for each manual effect for the current song. Auto uses the song's cues with preset defaults.

| Effect | Horizontal | Vertical |
| --- | --- | --- |
| Reverb | Room size | Wet level |
| Delay | Repeat time (50–550 ms) | Feedback (0–64%) |
| Distortion | Drive | Mix |
| Auto-Tune | Correction strength | Retune speed (slow to fast) |
| Talkbox | Synth carrier pitch (C2–C4) | Dry-to-vocoder mix |

Auto-Tune corrects toward the nearest chromatic semitone using each microphone's detected pitch. The native pitch shifter adds about 35 ms of monitor delay while selected. Talkbox is a 12-band software vocoder with a sawtooth carrier. For a first test, speak into the mic with Talkbox selected and raise the mix. The pitch control moves the synth while your mouth shapes its sound.

## Song automation

Auto starts with YARG's usual monitor sound. For an unpacked song folder, save the following as **vocal-fx.json** alongside the chart, then start the song. The first list controls the first singer in vocal-player order. An empty list can reserve a slot. All microphones assigned to one singer receive that singer's preset.

```json
[
  [
    { "time": 5, "preset": "Reverb" },
    { "time": 10, "preset": "Delay" },
    { "time": 15, "preset": "Distortion" },
    { "time": 20, "preset": "Standard" }
  ],
  [
    { "time": 10, "preset": "Reverb" },
    { "time": 20, "preset": "Standard" }
  ]
]
```

Use increasing chart times in seconds. A cue takes effect at its timestamp and lasts until the next cue. A missing list, absent file or position before the first cue uses Standard. An invalid file falls back to Standard in Auto. Packaged songs support manual presets. Only folder-based INI entries load a sidecar.

## Audio and timing

Shared capture, WASAPI and ASIO feed per-microphone BASS split streams. Vocal FX changes the live monitor split registered with the output router. The analyzer still reads the existing analysis split, including its input EQ, and recordings keep their existing dry/processed routing.

Standard uses the existing monitor EQ, dynamics, short echo and configured reverb level. Reverb raises the native reverb wet mix. Delay changes the existing echo to a 300 ms repeat. Distortion attaches a BASS FX drive before the final monitor limiter. Auto-Tune attaches the native pitch shifter only while selected. Talkbox processes the mono monitor buffer in place with preallocated filters. Neither opens another capture stream. Use the preset names `AutoTune` and `Talkbox` in song cues.

The game loads the cue lists once per song with a live singer. Each gameplay update resolves the most recent cue against SongRunner's calibrated SongTime. Absolute lookup supports practice loops, pause rewinds, skips and speed changes. Cue resolution is frame-granular. Returning to Auto from a manual preset resolves the current position immediately.

## Listening check

1. Load the example with two microphone profiles. Sing into each separately at 12 seconds. Singer one should have a distinct repeat and singer two should have a reverb tail.
2. Set singer one to Distortion in Audio Settings. Resume and cross 20 seconds. Singer one's manual choice should persist while singer two returns to Standard.
3. Return singer one to Auto and seek backwards across 10 seconds in practice. Listen for the changes at the same chart positions at half speed.
4. Exit the song with a manual effect active, then start a song without a sidecar. Both monitors should return to the standard sound.
