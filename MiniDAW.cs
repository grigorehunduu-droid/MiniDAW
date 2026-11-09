// MiniDAW - a small standalone pattern-based DAW modelled on FL Studio's workflow:
// channel rack -> patterns (notes per channel) -> playlist (patterns placed on bars).
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:banana.ico /out:MiniDAW.exe MiniDAW.cs
//        (banana.ico comes from MiniDAW.exe --logo banana.ico; to make the very first build, leave /win32icon out)
// Test:  MiniDAW.exe --selftest result.txt   (exit code 0 = pass)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("MiniDAW")]
[assembly: System.Reflection.AssemblyProduct("MiniDAW")]
[assembly: System.Reflection.AssemblyVersion(AboutForm.Version)]       // the installer reads it; Explorer shows the file version
[assembly: System.Reflection.AssemblyFileVersion(AboutForm.Version)]

// ---------------------------------------------------------------- model
class Note   // Step/Len in 16th notes (fractions for triplets and finer snaps), Key = MIDI note (60 = C5 in FL naming), Vel 1..127 (100 = the channel's normal level)
{
    public double Step, Len;
    public int Key, Vel = 100;
    public float Pan;   // -1..1, added to the channel's pan (the rack's graph editor)
    public int Fine;    // -100..100 cents of fine pitch
    public bool Slide;  // a slide note: bends the channel's sounding note to this pitch over its length instead of starting a new one
    public Note Clone() { return (Note)MemberwiseClone(); }
}

class Channel
{
    public string Name, Kind, SamplePath;     // Kind: kick | snare | hat | synth | sample
    public float Vol = 0.8f, Pan = 0f;
    public bool Mute;
    public int Insert;                        // mixer insert this channel feeds (0 = master)
    public int Color;                         // ARGB of its colour in the rack and piano roll; 0 = the default orange
    public string Group;                      // rack filter group, like FL's channel groups; null = none
    public int ArpDir, ArpRange = 1, ArpTime = 1; public float ArpGate = 0.5f;   // arpeggiator: 0 off, 1 up, 2 down, 3 up & down, 4 random; octaves; ArpTimes index; note length
    public float Glide;                                                           // portamento: 0 off, 1 = 500 ms; when on, the channel plays one note at a time and slides between them
    public int GlideSamples() { return (int)(Glide * 0.5 * Engine.SR); }
    public static readonly double[] ArpTimes = { 0.5, 1, 2, 4 };                  // in 16ths: 1/32, 1/16, 1/8, 1/4
    public static readonly string[] ArpDirs = { "off", "up", "down", "up & down", "random" }, ArpTimeNames = { "1/32", "1/16", "1/8", "1/4" };
    public float[] Sample, SampleR; public int SampleRate = 44100;   // SampleR: the right side of a stereo sample (null = mono)
    public static readonly string[] Kinds = { "kick", "snare", "hat", "synth", "sample", "square", "pluck", "pad", "bass", "keys", "clap", "analog" };
    public static readonly string[] Sounds = { "analog", "synth", "square", "pluck", "pad", "bass", "keys", "kick", "snare", "hat", "clap" };   // built-in sounds, in menu order
    static readonly string[] Titles = { "Analog synth", "Saw lead", "Square lead", "Pluck", "Pad", "Bass", "Electric piano", "Kick", "Snare", "Hat", "Clap" };

    // Sampler options (Kind "sample"): loop between two points while the note is held, play backwards, time-stretch (longer or
    // shorter at the same pitch), and slices (notes from C5 up play slice 1, 2, … like FL's Slicex).
    public bool Loop, Reverse;
    public float LoopStart, LoopEnd = 1, Stretch = 1;   // loop points as fractions of the sample; Stretch 0.5 .. 2 = half .. twice as long
    public int[] Slices;                                // sample positions where slices start (the first at 0); null = no slicing

    // Analog synth (Kind "analog"), after FL's 3xOsc and friends: 3 oscillators with unison, a filter with its own envelope, an
    // amp envelope and an LFO. All settings 0..1; null = the first preset. Automation lanes reach them as Param LaneSyn + i.
    public float[] Syn;
    public const int LaneSyn = 6;
    public static readonly string[] SynNames = { "Osc 1 wave", "Osc 1 octave", "Osc 1 level", "Osc 2 wave", "Osc 2 semitones", "Osc 2 detune", "Osc 2 level", "Osc 3 wave", "Osc 3 octave", "Osc 3 level",
        "Filter type", "Cutoff", "Resonance", "Filter envelope", "Amp attack", "Amp decay", "Amp sustain", "Amp release", "Filter attack", "Filter decay", "Filter sustain", "Filter release",
        "LFO rate", "LFO amount", "LFO target", "Unison voices", "Unison spread", "Volume" };
    public static readonly string[] Waves = { "sine", "saw", "square", "triangle", "noise" }, FilterTypes = { "low-pass", "band-pass", "high-pass" }, LfoTargets = { "pitch", "filter", "volume", "pan" };
    public static readonly string[] PresetNames = { "Bright saw lead", "Warm pad", "Sub bass", "Pluck", "Wobble bass", "Supersaw chords" };
    public static readonly float[][] SynPresets = {
        new[] { .25f, .5f, .8f, .25f, .5f, .57f, .6f, .45f, .25f, .2f, 0f, .62f, .25f, .8f, .05f, .4f, .8f, .25f, .02f, .35f, .3f, .3f, .6f, 0f, 0f, .17f, .3f, .6f },
        new[] { .25f, .5f, .7f, .25f, .5f, .62f, .7f, .65f, .75f, .3f, 0f, .5f, .1f, .6f, .6f, .5f, .85f, .6f, .6f, .6f, .6f, .6f, .3f, .15f, .3f, .5f, .5f, .55f },
        new[] { 0f, .25f, 1f, .45f, .5f, .5f, .15f, 0f, .5f, 0f, 0f, .4f, .1f, .55f, .02f, .3f, .9f, .15f, .01f, .25f, .4f, .2f, 0f, 0f, 0f, 0f, 0f, .75f },
        new[] { .25f, .5f, .8f, .45f, 1f, .5f, .3f, 0f, .5f, 0f, 0f, .35f, .35f, .95f, .01f, .35f, 0f, .3f, 0f, .3f, 0f, .2f, 0f, 0f, 0f, 0f, 0f, .65f },
        new[] { .25f, .25f, .8f, .45f, .5f, .55f, .7f, 0f, .25f, .5f, 0f, .3f, .6f, .5f, .01f, .3f, 1f, .1f, .01f, .3f, .5f, .2f, .55f, .8f, .3f, 0f, 0f, .6f },
        new[] { .25f, .5f, .8f, .25f, .5f, .6f, .7f, .25f, .75f, .3f, 0f, .72f, .15f, .6f, .08f, .5f, .75f, .4f, .05f, .5f, .5f, .4f, 0f, 0f, 0f, 1f, .55f, .45f } };
    public float S(int i) { return (Syn ?? SynPresets[0])[i]; }

    // Where the hits in a sample start, like FL's Slicex: a 512-sample window whose energy jumps to 4× the windows before it,
    // at least 100 ms after the last start. The first slice always starts at 0.
    public static int[] Transients(float[] x, int rate)
    {
        const int Hop = 512; var e = new List<double>();
        for (int i = 0; i + Hop <= x.Length; i += Hop) { double sum = 0; for (int k = i; k < i + Hop; k++) sum += x[k] * x[k]; e.Add(sum / Hop); }
        var starts = new List<int> { 0 }; double floor = e.Count > 0 ? e.Max() * 1e-3 : 0;
        for (int f = 1; f < e.Count; f++)
        {
            double before = 0; int n = 0;
            for (int k = Math.Max(0, f - 4); k < f; k++) { before += e[k]; n++; }
            if (!(e[f] > floor && e[f] > 4 * before / n)) continue;
            int lo = Math.Max(0, (f - 1) * Hop), hi = Math.Min(x.Length, (f + 1) * Hop); float peak = 0;
            for (int k = lo; k < hi; k++) peak = Math.Max(peak, Math.Abs(x[k]));
            int at = lo; while (at < hi && Math.Abs(x[at]) < 0.1f * peak) at++;   // the hit's first loud sample,
            at = Math.Max(0, at - 32);                                             // just before it
            if (at - starts[starts.Count - 1] >= rate / 10) starts.Add(at);
        }
        return starts.ToArray();
    }

    public string SynLabel(int i)   // a synth setting with its value in words
    {
        float v = S(i);
        string val = i == 0 || i == 3 || i == 7 ? Waves[Wave(v)] : i == 1 || i == 8 ? Signed(Oct(v)) + " oct" : i == 4 ? Signed(Semis(v)) + " st" : i == 5 ? Signed((int)Math.Round((v - .5f) * 100)) + " cents"
            : i == 10 ? FilterTypes[Math.Min(2, (int)(v * 3))] : i == 11 ? (int)Math.Round(CutHz(v)) + " Hz" : i == 13 ? Signed((int)Math.Round((v - .5f) * 200)) + "%"
            : i >= 14 && i <= 21 && i != 16 && i != 20 ? Secs((int)(EnvSec(v) * Engine.SR)) : i == 22 ? (0.05 * Math.Pow(200, v)).ToString("0.00", CultureInfo.InvariantCulture) + " Hz"
            : i == 24 ? LfoTargets[Math.Min(3, (int)(v * 4))] : i == 25 ? (1 + (int)Math.Round(v * 6)).ToString() : (int)Math.Round(v * 100) + "%";
        return SynNames[i] + "  " + val;
    }
    static string Signed(int x) { return (x > 0 ? "+" : "") + x; }
    public static int Wave(float v) { return Math.Min(4, (int)(v * 5)); }
    public static int Oct(float v) { return (int)Math.Round(v * 4) - 2; }
    public static int Semis(float v) { return (int)Math.Round(v * 24) - 12; }
    public static double CutHz(float v) { return 20 * Math.Pow(1000, v); }
    public static double EnvSec(float v) { return 0.001 + 4 * v * v * v; }
    public static string Title(string kind) { int i = Array.IndexOf(Sounds, kind); return i < 0 ? "Sample" : Titles[i]; }

    // Sound settings, all 0..1: attack, release, tone (decay for drums), pitch. null = the sound's built-in settings.
    public float[] Knob;
    public const int KAttack = 0, KRelease = 1, KTone = 2, KPitch = 3, LaneVol = 4, LanePan = 5;   // LaneVol/LanePan: automation targets only
    static readonly Dictionary<string, float[]> builtIn = Kinds.ToDictionary(k => k, k => BuiltIn(k));   // made once; the audio thread only reads it

    public static float[] BuiltIn(string kind)   // knob positions that reproduce each sound's original shape
    {
        double a = 220.0 / Engine.SR, r = 3500.0 / Engine.SR;   // saw and square leads
        if (kind == "pad") { a = 0.3; r = 0.6; }
        else if (kind == "bass") { a = 60.0 / Engine.SR; r = 1500.0 / Engine.SR; }
        else if (kind == "keys") { a = 30.0 / Engine.SR; r = 2500.0 / Engine.SR; }
        else if (kind == "pluck") { a = 1.0 / Engine.SR; r = 2500.0 / Engine.SR; }
        return new[] { (float)Math.Pow(a / 2, 1 / 3.0), (float)Math.Pow((r - 0.005) / 3, 1 / 3.0), 0.5f, 0.5f };
    }

    public float K(int i) { return (Knob ?? builtIn[Kind])[i]; }
    public Channel Copy() { var c = (Channel)MemberwiseClone(); if (Knob != null) c.Knob = (float[])Knob.Clone(); if (Syn != null) c.Syn = (float[])Syn.Clone(); return c; }   // sample data stays shared
    public int AttackSamples() { double k = K(KAttack); return Math.Max(1, (int)Math.Round(2 * k * k * k * Engine.SR)); }                 // 0 .. 2 s
    public int ReleaseSamples() { double k = K(KRelease); return Math.Max(1, (int)Math.Round((0.005 + 3 * k * k * k) * Engine.SR)); }   // 5 ms .. 3 s
    public double Bright() { double k = K(KTone); return k < 0.5 ? 0.25 + 1.5 * k : 1 + 6 * (k - 0.5); }   // tone or decay: 0.25x .. 4x, 1x in the middle
    public int Semitones() { return (int)Math.Round((K(KPitch) - 0.5) * 48); }                              // -24 .. +24
    public bool IsDrum { get { return Kind == "kick" || Kind == "snare" || Kind == "hat" || Kind == "clap"; } }

    public int[] KnobsShown()   // the settings that mean something for this sound
    {
        if (Kind == "sample" || Kind == "analog") return new[] { KPitch };
        if (Kind == "hat" || Kind == "clap") return new[] { KTone };
        if (IsDrum) return new[] { KTone, KPitch };
        return new[] { KAttack, KRelease, KTone, KPitch };
    }

    public string KnobLabel(int i)
    {
        if (i == KAttack) return "Attack  " + Secs(AttackSamples());
        if (i == KRelease) return "Release  " + Secs(ReleaseSamples());
        if (i == KTone) return (IsDrum ? "Decay  " : "Tone  ") + (int)Math.Round(Bright() * 100) + "%";
        int st = Semitones();
        return "Pitch  " + (st > 0 ? "+" : "") + st + " semitones";
    }

    static string Secs(int samples) { double s = samples / (double)Engine.SR; return s < 1 ? (int)Math.Round(s * 1000) + " ms" : s.ToString("0.00", CultureInfo.InvariantCulture) + " s"; }
}

class Pattern
{
    public const int MaxBars = 4;
    public string Name;
    public int Length = Project.Steps;                         // in 16ths: 1 to MaxBars bars
    public int Bars { get { return Length / Project.Steps; } }
    public List<List<Note>> Notes = new List<List<Note>>();   // indexed by channel
    public List<Note> For(int ch) { while (Notes.Count <= ch) Notes.Add(new List<Note>()); return Notes[ch]; }

    // Copies of notes land at step + offset on channel ch: ones past the end are dropped, ones on an existing note (same step and key) replace it.
    public List<Note> Paste(int ch, IEnumerable<Note> notes, double offset)
    {
        var list = For(ch); var added = new List<Note>();
        foreach (var n in notes.ToList())   // snapshot: the source may be this very list
        {
            double s = n.Step + offset;
            if (s < 0 || s >= Length) continue;
            list.RemoveAll(x => x.Step == s && x.Key == n.Key);
            var c = n.Clone(); c.Step = s; c.Len = Math.Min(n.Len, Length - s);
            list.Add(c); added.Add(c);
        }
        return added;
    }

    // How far a group of notes may move (ds steps, dk keys) and all still fit in a pattern this long; from = each note as it was before the drag
    public static void ClampMove(Dictionary<Note, Note> from, int length, ref double ds, ref int dk)
    {
        foreach (var o in from.Values)   // each clamp keeps the earlier ones satisfied, since every note's range contains 0
        {
            ds = Math.Max(-o.Step, Math.Min(length - o.Len - o.Step, ds));
            dk = Math.Max(-o.Key, Math.Min(127 - o.Key, dk));
        }
    }

    public static double DupOffset(IEnumerable<Note> notes)   // Ctrl+B: the notes' span rounded up to whole beats, so the copy follows right after
    {
        double a = notes.Min(n => n.Step), b = notes.Max(n => n.Step + n.Len);
        return Math.Ceiling((b - a) / 4 - 1e-9) * 4;
    }

    public void SetBars(int bars)   // shortening drops notes past the new end and trims ones that run over it
    {
        Length = bars * Project.Steps;
        foreach (var list in Notes)
        {
            list.RemoveAll(n => n.Step >= Length);
            foreach (var n in list) n.Len = Math.Min(n.Len, Length - n.Step);
        }
    }
}

class Route { public int To; public float Level = 0.5f; }   // a send: a post-fader copy of an insert's output into another insert, at a level

class Insert   // one mixer track: effects run in slot order, then fader and pan; its output goes to the master (unless switched off) and to any sends
{
    public const int SlotCount = 8;   // like FL's insert slots (FL has 10)
    public float Vol = 1f, Pan = 0f;
    public bool Mute, ToMaster = true;
    public int Sidechain;             // 0 = none: another insert whose output drives this one's Compressor and Vocoder
    public List<Route> Sends = new List<Route>();   // any number; the routing never loops back (Project.CanRoute)
    public Effect[] Slots = new Effect[SlotCount];
    // The first send, as a single "send to N at level" (the mixer strip's send bar, its automation lane, older files)
    public int SendTo { get { return Sends.Count > 0 ? Sends[0].To : 0; } set { if (value <= 0) { if (Sends.Count > 0) Sends.RemoveAt(0); } else if (Sends.Count > 0) Sends[0].To = value; else Sends.Add(new Route { To = value }); } }
    public float SendLevel { get { return Sends.Count > 0 ? Sends[0].Level : 0.5f; } set { if (Sends.Count > 0) Sends[0].Level = value; } }
}

class LanePoint { public double At; public float Value, Tension; }   // an automation point: at a 16th, a value 0..1, and the curve's bend to the next point (-1..1)

// Automation: one control's curve, stored as a value (0..1) per 16th and edited as points with bendable curves. Automation clips
// on the playlist (Clip.Lane) play stretches of it; a lane that's on no clip at all (made in code) plays from bar 1.
class Lane
{
    public int Ins, Slot = -1, Param;   // Slot -1 = the strip itself (Param 0 volume, 1 pan, 2 send level); else an effect slot and its parameter (or FxMix)
    public const int FxMix = 3;         // an effect lane's Param for the effect's wet/dry mix
    public int Ch = -1;                 // >= 0: a channel's sound setting instead (Param = Channel.KAttack .. KPitch) or its rack volume / pan (LaneVol, LanePan); Ins and Slot unused
    public bool Tempo;                  // true: the song tempo (0..1 = MinBpm..MaxBpm); every other field unused
    public float[] Values = new float[Project.Bars * Project.Steps];
    public List<LanePoint> Points;   // null until edited as points; Render() turns them into Values

    public float At(double pos)   // the value at a position in 16ths, between the stored 16ths; past the end it holds the last
    {
        if (pos <= 0) return Values[0];
        int k = (int)pos; if (k >= Values.Length - 1) return Values[Values.Length - 1];
        return Values[k] + (Values[k + 1] - Values[k]) * (float)(pos - k);
    }

    public static float Bend(float t, float tension)   // 0..1 along a segment, curved: tension > 0 starts slow, < 0 starts fast
    {
        return tension == 0 ? t : tension > 0 ? (float)Math.Pow(t, 1 + 4 * tension) : 1 - (float)Math.Pow(1 - t, 1 - 4 * tension);
    }

    public void Render()   // Points -> Values (at least the old 32 bars; longer if a point is further)
    {
        if (Points == null || Points.Count == 0) return;
        var pts = Points.OrderBy(q => q.At).ToList();
        int len = Math.Max(Values.Length, (int)Math.Ceiling(pts[pts.Count - 1].At) + 1);
        var v = new float[len];
        for (int k = 0, j = 0; k < len; k++)
        {
            while (j < pts.Count - 1 && pts[j + 1].At <= k) j++;
            if (k <= pts[0].At) v[k] = pts[0].Value;
            else if (j >= pts.Count - 1) v[k] = pts[pts.Count - 1].Value;
            else { var a = pts[j]; var b = pts[j + 1]; float t = (float)((k - a.At) / Math.Max(1e-9, b.At - a.At)); v[k] = a.Value + (b.Value - a.Value) * Bend(t, a.Tension); }
        }
        Values = v;   // one reference swap: the audio thread sees the old curve or the new one
    }

    public void MakePoints()   // Values -> Points, keeping only the corners (a drawn ramp becomes two points)
    {
        if (Points != null) return;
        Points = new List<LanePoint>();
        for (int k = 0; k < Values.Length; k++)
        {
            bool corner = k == 0 || k == Values.Length - 1 || Math.Abs(Values[k] - (Values[k - 1] + Values[k + 1]) / 2) > 1e-4;
            if (corner) Points.Add(new LanePoint { At = k, Value = Values[k] });
        }
    }

    public bool SameTarget(Lane o) { return Tempo == o.Tempo && (Tempo || Ch == o.Ch && Param == o.Param && (Ch >= 0 || Ins == o.Ins && Slot == o.Slot)); }

    public float Get(Project p)
    {
        if (Tempo) return (p.Bpm - Project.MinBpm) / (float)(Project.MaxBpm - Project.MinBpm);
        if (Ch >= 0)
        {
            if (Ch >= p.Channels.Count) return 0.5f;
            var ch = p.Channels[Ch];
            return Param == Channel.LaneVol ? ch.Vol : Param == Channel.LanePan ? (ch.Pan + 1) / 2 : Param >= Channel.LaneSyn ? ch.S(Param - Channel.LaneSyn) : ch.K(Param);
        }
        var ins = p.Mixer[Ins];
        if (Slot < 0) return Param == 0 ? ins.Vol : Param == 1 ? (ins.Pan + 1) / 2 : ins.SendLevel;
        var fx = ins.Slots[Slot];
        return fx == null ? 0.5f : Param == FxMix ? fx.Mix : fx.P[Param];
    }

    public void Set(Project p, float v)
    {
        if (Tempo) { p.Bpm = (int)Math.Round(Project.MinBpm + v * (Project.MaxBpm - Project.MinBpm)); return; }   // takes effect from the next audio block
        if (Ch >= 0)
        {
            if (Ch >= p.Channels.Count) return;
            var c = p.Channels[Ch];
            if (Param == Channel.LaneVol) { c.Vol = v; return; }
            if (Param == Channel.LanePan) { c.Pan = v * 2 - 1; return; }
            if (Param >= Channel.LaneSyn) { var sy = (float[])(c.Syn ?? Channel.SynPresets[0]).Clone(); sy[Param - Channel.LaneSyn] = v; c.Syn = sy; return; }   // a whole new array: never half-written
            var k = c.Knob ?? (float[])Channel.BuiltIn(c.Kind).Clone();   // fill the array before publishing it, so no one sees a half-made one
            k[Param] = v; c.Knob = k;
            return;
        }
        var ins = p.Mixer[Ins];
        if (Slot < 0) { if (Param == 0) ins.Vol = v; else if (Param == 1) ins.Pan = v * 2 - 1; else ins.SendLevel = v; }
        else { var fx = ins.Slots[Slot]; if (fx != null) { if (Param == FxMix) fx.Mix = v; else fx.P[Param] = v; } }   // follows whatever effect is in the slot now
    }

    public string Name(Project p)
    {
        if (Tempo) return "Tempo (BPM)";
        if (Ch >= 0)
        {
            if (Ch >= p.Channels.Count) return "missing channel";
            var c = p.Channels[Ch];
            return c.Name + " · " + (Param >= Channel.LaneSyn ? Channel.SynNames[Param - Channel.LaneSyn] : new[] { "Attack", "Release", c.IsDrum ? "Decay" : "Tone", "Pitch", "Volume", "Pan" }[Param]);
        }
        string where = Ins == 0 ? "Master" : "Insert " + Ins;
        if (Slot < 0) return where + " · " + new[] { "Volume", "Pan", "Send" }[Param];
        var fx = p.Mixer[Ins].Slots[Slot];
        return where + " · " + (fx == null ? "empty slot " + (Slot + 1) : fx.Type + " " + (Param == FxMix ? "Wet/dry" : fx.ParamName(Param)));
    }
}

class Effect
{
    public static readonly string[] Types = { "Delay", "Reverb", "Filter", "Distortion", "EQ", "Compressor", "Limiter", "Chorus", "Flanger", "Phaser",
        "Parametric EQ", "Multiband", "Beat FX", "Vocoder" };
    static readonly string[] BandNames = { "Low shelf", "Low mid", "Mid", "High mid", "High shelf" };
    static readonly string[][] Names = { new[] { "Time", "Feedback", "Mix" }, new[] { "Size", "Damp", "Mix" }, new[] { "Cutoff", "Reso", "Mode" }, new[] { "Drive", "Tone", "Mix" },
        new[] { "Low", "Mid", "High" }, new[] { "Threshold", "Ratio", "Gain" }, new[] { "Ceiling", "Release", "Gain" },
        new[] { "Depth", "Rate", "Mix" }, new[] { "Depth", "Rate", "Feedback" }, new[] { "Depth", "Rate", "Feedback" },
        BandNames.SelectMany(n => new[] { n + " freq", n + " gain", n + " Q" }).ToArray(), new[] { "Depth", "Speed", "Gain" }, new[] { "Mode", "Rate", "Mix" }, new[] { "Release", "Bright", "Mix" } };
    static readonly float[][] Defaults = { new[] { 0.35f, 0.4f, 0.35f }, new[] { 0.6f, 0.4f, 0.3f }, new[] { 0.6f, 0.2f, 0f }, new[] { 0.4f, 0.6f, 1f },
        new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.25f, 3f / 19, 0.25f },   // EQ flat; compressor -12 dB, 4:1, +6 dB
        new[] { 0.025f, 0.35f, 0f }, new[] { 0.5f, 0.45f, 1f }, new[] { 0.6f, 0.3f, 0.5f }, new[] { 0.6f, 0.35f, 0.4f },   // limiter -0.3 dB, ~40 ms, +0 dB
        new[] { 0.159f, 0.5f, 0.245f, 0.366f, 0.5f, 0.347f, 0.566f, 0.5f, 0.347f, 0.767f, 0.5f, 0.347f, 0.926f, 0.5f, 0.245f },   // flat; 60 Hz, 250 Hz, 1 kHz, 4 kHz, 12 kHz
        new[] { 0.4f, 0.5f, 0.2f }, new[] { 0f, 0.5f, 1f }, new[] { 0.3f, 0.3f, 1f } };
    static readonly int[] CombLen = { 1116, 1188, 1277, 1356 }, ApLen = { 556, 441 };   // Freeverb tunings at 44.1 kHz

    public readonly string Type;
    public readonly float[] P;   // its settings, all 0..1 (3 for most effects, 15 for the Parametric EQ); the UI writes them while audio reads (float writes are atomic)
    public double Sps, Pos; public bool Playing;   // set before each block: samples per 16th, song position (16ths), playing; Beat FX follows them
    public bool Bypass;          // switched off: skipped in the chain, settings kept (the on/off dot on its mixer slot)
    public float Mix = 1;        // wet/dry: 1 = fully processed, 0 = untouched (the small bar on its mixer slot, like FL's slot mix knob)

    // Process with the wet/dry mix applied; dry is scratch space, grown when needed.
    public void Run(float[] b, int n2, float[] key, ref float[] dry)
    {
        float m = Mix;   // read once: the UI may move it mid-block
        if (m >= 1) { Process(b, n2, key); return; }
        if (dry == null || dry.Length < n2) dry = new float[n2];
        Array.Copy(b, dry, n2);
        Process(b, n2, key);
        for (int k = 0; k < n2; k++) b[k] = dry[k] + (b[k] - dry[k]) * m;
    }
    readonly int t;
    // DSP state, created on first use
    float[] dl; int wp;
    float[][] comb, ap; readonly float[] store = new float[8]; readonly int[] ci = new int[8], ai = new int[4];
    readonly double[] s1 = new double[2], s2 = new double[2];
    double lfo; float[] peaks;   // modulation phase (chorus, flanger, phaser); the limiter's look-ahead window of peak levels
    readonly double[] apx = new double[12], apy = new double[12];   // phaser: 6 all-pass stages per side

    public Effect(string type)
    {
        t = Array.IndexOf(Types, type);
        if (t < 0) throw new InvalidDataException("Unknown effect: " + type);
        Type = type; P = (float[])Defaults[t].Clone();
    }

    public string ParamName(int i) { return Names[t][i]; }

    public string Label(int i)
    {
        string v;
        if (t == 0 && i == 0) v = (int)(DelaySec() * 1000) + " ms";
        else if (t == 2 && i == 0) v = (int)CutoffHz() + " Hz";
        else if (t == 2 && i == 2) v = new[] { "Low-pass", "Band-pass", "High-pass" }[Mode()];
        else if (t == 4) v = EqDb(i).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
        else if (t == 5 && i == 0) v = ThreshDb().ToString("0", CultureInfo.InvariantCulture) + " dB";
        else if (t == 5 && i == 1) v = Ratio().ToString("0.0", CultureInfo.InvariantCulture) + ":1";
        else if (t == 5) v = "+" + MakeupDb().ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        else if (t == 6 && i == 0) v = CeilingDb().ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        else if (t == 6 && i == 1) v = (int)Math.Round(ReleaseMs()) + " ms";
        else if (t == 6) v = "+" + LimitGainDb().ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        else if (t >= 7 && t <= 9 && i == 1) v = RateHz().ToString("0.00", CultureInfo.InvariantCulture) + " Hz";
        else if (t == 10) v = i % 3 == 0 ? Hz(BandHz(P[i])) : i % 3 == 1 ? BandDb(P[i]).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB" : "Q " + BandQ(P[i]).ToString("0.0", CultureInfo.InvariantCulture);
        else if (t == 11 && i == 2) v = "+" + (P[2] * 18).ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        else if (t == 12 && i == 0) v = BeatModes[BeatMode()];
        else if (t == 12 && i == 1) v = new[] { "1/4", "1/8", "1/16", "1/32" }[Math.Min(3, (int)(P[1] * 4))];
        else if (t == 13 && i == 0) v = (int)Math.Round(10 + 190 * P[0]) + " ms";
        else v = (int)Math.Round(P[i] * 100) + "%";
        return Names[t][i] + "  " + v;
    }

    double DelaySec() { return 0.02 + P[0] * 0.98; }
    double CutoffHz() { return 40 * Math.Pow(450, P[0]); }   // 40 Hz .. 18 kHz, exponential like a real knob
    int Mode() { return Math.Min(2, (int)(P[2] * 3)); }
    double EqDb(int i) { return (P[i] - 0.5) * 24; }          // ±12 dB, middle = flat
    double ThreshDb() { return -48 * P[0]; }                  // 0 .. -48 dB: drag right to compress more
    double Ratio() { return 1 + P[1] * 19; }                  // 1:1 .. 20:1
    double MakeupDb() { return P[2] * 24; }                   // 0 .. +24 dB (the faders can't boost, this can)
    double CeilingDb() { return -12 * P[0]; }                 // limiter: 0 .. -12 dB
    double ReleaseMs() { return 10 * Math.Pow(50, P[1]); }    // 10 .. 500 ms
    double LimitGainDb() { return P[2] * 24; }                // drive into the ceiling: 0 .. +24 dB
    double RateHz() { return 0.05 * Math.Pow(100, P[1]); }    // modulation: 0.05 .. 5 Hz
    static string Hz(double f) { return f >= 1000 ? (f / 1000).ToString("0.0#", CultureInfo.InvariantCulture) + " kHz" : (int)Math.Round(f) + " Hz"; }
    public static readonly string[] BeatModes = { "Gate", "Stutter", "Half speed", "Reverse" };
    int BeatMode() { return Math.Min(3, (int)(P[0] * 4)); }

    // ---- Parametric EQ: 5 bands (low shelf, 3 peaks, high shelf), RBJ biquads; its window draws the true curve from ResponseDb
    public const int Bands = 5;
    public static double BandHz(float v) { return 20 * Math.Pow(1000, v); }   // 20 Hz .. 20 kHz
    public static double BandDb(float v) { return (v - 0.5) * 36; }           // ±18 dB, middle = flat
    public static double BandQ(float v) { return 0.3 * Math.Pow(32, v); }     // 0.3 .. 9.6
    readonly double[] bq = new double[Bands * 4];                              // per band and side: two state values

    double[] Coefs(int band)   // b0, b1, b2, a1, a2, normalised
    {
        double f = Math.Min(BandHz(P[band * 3]), 0.45 * Engine.SR), A = Math.Pow(10, BandDb(P[band * 3 + 1]) / 40), w = 2 * Math.PI * f / Engine.SR;
        double cs = Math.Cos(w), al = Math.Sin(w) / (2 * BandQ(P[band * 3 + 2])), sq = 2 * Math.Sqrt(A) * al, b0, b1, b2, a0, a1, a2;
        if (band == 0) { b0 = A * ((A + 1) - (A - 1) * cs + sq); b1 = 2 * A * ((A - 1) - (A + 1) * cs); b2 = A * ((A + 1) - (A - 1) * cs - sq); a0 = (A + 1) + (A - 1) * cs + sq; a1 = -2 * ((A - 1) + (A + 1) * cs); a2 = (A + 1) + (A - 1) * cs - sq; }
        else if (band == Bands - 1) { b0 = A * ((A + 1) + (A - 1) * cs + sq); b1 = -2 * A * ((A - 1) + (A + 1) * cs); b2 = A * ((A + 1) + (A - 1) * cs - sq); a0 = (A + 1) - (A - 1) * cs + sq; a1 = 2 * ((A - 1) - (A + 1) * cs); a2 = (A + 1) - (A - 1) * cs - sq; }
        else { b0 = 1 + al * A; b1 = -2 * cs; b2 = 1 - al * A; a0 = 1 + al / A; a1 = -2 * cs; a2 = 1 - al / A; }
        return new[] { b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0 };
    }

    public double ResponseDb(double hz)   // the curve: every band's gain at this frequency, added
    {
        double w = 2 * Math.PI * hz / Engine.SR, db = 0;
        for (int band = 0; band < Bands; band++)
        {
            var c = Coefs(band);
            double nr = c[0] + c[1] * Math.Cos(w) + c[2] * Math.Cos(2 * w), ni = -(c[1] * Math.Sin(w) + c[2] * Math.Sin(2 * w));
            double dr = 1 + c[3] * Math.Cos(w) + c[4] * Math.Cos(2 * w), di = -(c[3] * Math.Sin(w) + c[4] * Math.Sin(2 * w));
            db += 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
        }
        return db;
    }

    void Peq(float[] b, int n2)
    {
        for (int band = 0; band < Bands; band++)
        {
            if (Math.Abs(BandDb(P[band * 3 + 1])) < 0.05) continue;   // flat: skip
            var c = Coefs(band);
            for (int i = 0; i < n2; i++)   // transposed direct form II
            {
                int z = (band * 2 + (i & 1)) * 2; double x = b[i], y = c[0] * x + bq[z];
                bq[z] = c[1] * x - c[3] * y + bq[z + 1]; bq[z + 1] = c[2] * x - c[4] * y;
                b[i] = (float)y;
            }
        }
    }

    // ---- Multiband: three bands (split at 200 Hz and 4 kHz, summing back exactly), each compressed on its own, then made up
    readonly double[] mbEnv = new double[3], mbX = new double[6];
    void Multi(float[] b, int n2)
    {
        double a1 = 1 - Math.Exp(-2 * Math.PI * 200 / Engine.SR), a2 = 1 - Math.Exp(-2 * Math.PI * 4000 / Engine.SR);
        double thr = -30 * P[0], slope = 1 - 1 / (1 + 7 * P[0]), makeup = P[2] * 18;
        double att = Math.Exp(-1 / ((0.03 - 0.029 * P[1]) * Engine.SR)), rel = Math.Exp(-1 / ((0.5 - 0.45 * P[1]) * Engine.SR));
        for (int k = 0; k < n2; k += 2)
        {
            for (int sd = 0; sd < 2; sd++)
            {
                s1[sd] += a1 * (b[k + sd] - s1[sd]); s2[sd] += a2 * (b[k + sd] - s2[sd]);
                mbX[sd] = s1[sd]; mbX[2 + sd] = s2[sd] - s1[sd]; mbX[4 + sd] = b[k + sd] - s2[sd];   // low, mid, high (no allocation: this runs per sample)
            }
            double outL = 0, outR = 0;
            for (int band = 0; band < 3; band++)
            {
                double xl = mbX[band * 2], xr = mbX[band * 2 + 1];
                double peak = Math.Max(Math.Abs(xl), Math.Abs(xr)), want = Math.Max(0, 20 * Math.Log10(Math.Max(peak, 1e-6)) - thr) * slope;
                mbEnv[band] = want + (want > mbEnv[band] ? att : rel) * (mbEnv[band] - want);
                double g = Math.Pow(10, -mbEnv[band] / 20);
                outL += xl * g; outR += xr * g;
            }
            double mk = Math.Pow(10, makeup / 20);
            b[k] = (float)(outL * mk); b[k + 1] = (float)(outR * mk);
        }
    }

    // ---- Beat FX, after Gross Beat: chops locked to the song's tempo. Gate: the first half of each slice; Stutter: each beat's
    // first slice repeated; Half speed: each beat played at half speed; Reverse: each slice plays the one before it backwards.
    float[] bfBuf; int bfLen;
    void Beat(float[] b, int n2)
    {
        if (!Playing || Sps <= 0) return;   // only while the song plays: stopped, the sound passes untouched
        if (bfBuf == null) { bfLen = Engine.SR * 4; bfBuf = new float[bfLen * 2]; }
        int mode = BeatMode(); double slice = new[] { 4, 2, 1, 0.5 }[Math.Min(3, (int)(P[1] * 4))] * Sps, beat = 4 * Sps, ramp = 0.002 * Engine.SR;
        float mix = P[2];
        for (int k = 0; k < n2; k += 2)
        {
            double pos = Pos * Sps + k / 2;   // samples since the song started
            bfBuf[2 * wp] = b[k]; bfBuf[2 * wp + 1] = b[k + 1];
            double back = 0, gain = 1;   // how far back to read, and a gate
            if (mode == 0) { double ph = pos % slice; gain = ph < slice / 2 ? Math.Min(1, Math.Min(ph, slice / 2 - ph) / ramp) : 0; }
            else if (mode == 1) { double ph = pos % beat; back = ph - ph % slice; }
            else if (mode == 2) back = (pos % beat) / 2;
            else { double ph = pos % slice; back = 2 * ph + 1; }
            int d = (int)Math.Min(bfLen - 1, back), rp = (wp - d + bfLen) % bfLen;
            for (int sd = 0; sd < 2; sd++) { float o = (float)(bfBuf[2 * rp + sd] * gain); b[k + sd] += (o - b[k + sd]) * mix; }
            wp = (wp + 1) % bfLen;
        }
    }

    // ---- Vocoder: the insert's own sound (the carrier, e.g. a synth) shaped by its sidechain's (the voice): 16 bands from
    // 120 Hz to 7 kHz, each carrier band as loud as the voice is in it. With no sidechain the sound passes untouched.
    const int VocBands = 16;
    double[] vocState, vocEnv, vocCoef;
    void Vocode(float[] b, int n2, float[] key)
    {
        if (key == null || key == b) return;
        if (vocState == null)
        {
            vocState = new double[VocBands * 3 * 2]; vocEnv = new double[VocBands]; vocCoef = new double[VocBands * 5];
            for (int j = 0; j < VocBands; j++)
            {
                double f = 120 * Math.Pow(7000 / 120.0, j / (double)(VocBands - 1)), w = 2 * Math.PI * f / Engine.SR, al = Math.Sin(w) / (2 * 5), a0 = 1 + al;
                vocCoef[j * 5] = al / a0; vocCoef[j * 5 + 1] = 0; vocCoef[j * 5 + 2] = -al / a0; vocCoef[j * 5 + 3] = -2 * Math.Cos(w) / a0; vocCoef[j * 5 + 4] = (1 - al) / a0;   // band-pass
            }
        }
        double rel = Math.Exp(-1 / ((0.01 + 0.19 * P[0]) * Engine.SR)), att = Math.Exp(-1 / (0.003 * Engine.SR)); float mix = P[2];
        for (int k = 0; k < n2; k += 2)
        {
            double m = (key[k] + key[k + 1]) * 0.5, outL = 0, outR = 0;
            for (int j = 0; j < VocBands; j++)
            {
                double mb = Bp(j, 0, m), e = Math.Abs(mb);
                vocEnv[j] = e + (e > vocEnv[j] ? att : rel) * (vocEnv[j] - e);
                double g = vocEnv[j] * (1 + P[1] * 3 * j / (VocBands - 1.0)) * 6;
                outL += Bp(j, 1, b[k]) * g; outR += Bp(j, 2, b[k + 1]) * g;
            }
            b[k] = (float)(b[k] * (1 - mix) + outL * mix); b[k + 1] = (float)(b[k + 1] * (1 - mix) + outR * mix);
        }
    }

    double Bp(int band, int which, double x)   // one band-pass step: which = 0 the voice, 1 / 2 the carrier's left / right
    {
        int z = (band * 3 + which) * 2, c = band * 5;
        double y = vocCoef[c] * x + vocState[z];
        vocState[z] = vocCoef[c + 1] * x - vocCoef[c + 3] * y + vocState[z + 1]; vocState[z + 1] = vocCoef[c + 2] * x - vocCoef[c + 4] * y;
        return y;
    }

    public void Process(float[] b, int n2, float[] key = null)   // b = interleaved stereo, n2 = float count; key = sidechain input (Compressor only)
    {
        switch (t)
        {
            case 0: Delay(b, n2); break;
            case 1: Reverb(b, n2); break;
            case 2: Filter(b, n2); break;
            case 3: Distort(b, n2); break;
            case 4: Eq(b, n2); break;
            case 5: Compress(b, n2, key ?? b); break;
            case 6: Limit(b, n2); break;
            case 7: Chorus(b, n2); break;
            case 8: Flange(b, n2); break;
            case 9: Phase(b, n2); break;
            case 10: Peq(b, n2); break;
            case 11: Multi(b, n2); break;
            case 12: Beat(b, n2); break;
            default: Vocode(b, n2, key); break;
        }
    }

    // Look-ahead brickwall limiter: the output runs 5 ms behind the input, so the gain is already down when a peak arrives.
    // Gain = ceiling / the loudest sample in that 5 ms window, falling fast and recovering over Release; a final clamp
    // catches whatever the smoothing leaves.
    void Limit(float[] b, int n2)
    {
        int look = (int)(0.005 * Engine.SR), len = look + 1;
        if (dl == null) { dl = new float[len * 2]; peaks = new float[len]; s1[0] = 1; }
        double ceil = Math.Pow(10, CeilingDb() / 20), drive = Math.Pow(10, LimitGainDb() / 20);
        double att = Math.Exp(-5.0 / look), rel = Math.Exp(-1 / (ReleaseMs() * 0.001 * Engine.SR));
        for (int k = 0; k < n2; k += 2)
        {
            float l = (float)(b[k] * drive), r = (float)(b[k + 1] * drive);
            dl[2 * wp] = l; dl[2 * wp + 1] = r; peaks[wp] = Math.Max(Math.Abs(l), Math.Abs(r));
            float max = 0; foreach (var pk in peaks) max = Math.Max(max, pk);   // the window: every sample between the one going out and the newest
            double want = max > ceil ? ceil / max : 1;
            s1[0] = want < s1[0] ? want + att * (s1[0] - want) : want + rel * (s1[0] - want);
            int rp = (wp + 1) % len;   // the oldest sample: 5 ms ago
            b[k] = (float)Math.Max(-ceil, Math.Min(ceil, dl[2 * rp] * s1[0]));
            b[k + 1] = (float)Math.Max(-ceil, Math.Min(ceil, dl[2 * rp + 1] * s1[0]));
            wp = rp;
        }
    }

    float Tap(int side, double delay, int len)   // the delay line `delay` samples back, linearly interpolated
    {
        double rp = wp - delay; while (rp < 0) rp += len;
        int i0 = (int)rp % len, i1 = (i0 + 1) % len; double f = rp - Math.Floor(rp);
        return (float)(dl[2 * i0 + side] * (1 - f) + dl[2 * i1 + side] * f);
    }

    void Chorus(float[] b, int n2)   // two copies 8-22 ms late, swaying a quarter-cycle apart left and right: a wider, thicker sound
    {
        int len = (int)(0.05 * Engine.SR);
        if (dl == null) dl = new float[len * 2];
        double step = 2 * Math.PI * RateHz() / Engine.SR, centre = 0.015 * Engine.SR, depth = P[0] * 0.007 * Engine.SR; float mix = P[2] * 0.5f;
        for (int k = 0; k < n2; k += 2)
        {
            dl[2 * wp] = b[k]; dl[2 * wp + 1] = b[k + 1];
            for (int sd = 0; sd < 2; sd++) b[k + sd] += (Tap(sd, centre + depth * Math.Sin(lfo + sd * Math.PI / 2), len) - b[k + sd]) * mix;
            lfo += step; if (lfo > 2 * Math.PI) lfo -= 2 * Math.PI;
            wp = (wp + 1) % len;
        }
    }

    void Flange(float[] b, int n2)   // a 0.5-4.5 ms swept delay fed back into itself, mixed 50/50: the jet-plane comb sweep
    {
        int len = (int)(0.01 * Engine.SR);
        if (dl == null) dl = new float[len * 2];
        double step = 2 * Math.PI * RateHz() / Engine.SR, centre = 0.0025 * Engine.SR, depth = P[0] * 0.002 * Engine.SR; float fb = P[2] * 0.9f;
        for (int k = 0; k < n2; k += 2)
        {
            for (int sd = 0; sd < 2; sd++)
            {
                float wet = Tap(sd, Math.Max(1, centre + depth * Math.Sin(lfo + sd * Math.PI / 2)), len);
                dl[2 * wp + sd] = b[k + sd] + wet * fb;
                b[k + sd] = (b[k + sd] + wet) * 0.5f;
            }
            lfo += step; if (lfo > 2 * Math.PI) lfo -= 2 * Math.PI;
            wp = (wp + 1) % len;
        }
    }

    void Phase(float[] b, int n2)   // six first-order all-passes swept between 300 Hz and up to 4.8 kHz, with feedback, mixed 50/50
    {
        double step = 2 * Math.PI * RateHz() / Engine.SR, fb = P[2] * 0.85, span = P[0] * 4;
        for (int k = 0; k < n2; k += 2)
        {
            for (int sd = 0; sd < 2; sd++)
            {
                double f = 300 * Math.Pow(2, span * (0.5 + 0.5 * Math.Sin(lfo + sd * Math.PI / 2)));
                double tn = Math.Tan(Math.PI * f / Engine.SR), a = (tn - 1) / (tn + 1);
                double x = b[k + sd] + fb * s2[sd];
                for (int st = sd * 6; st < sd * 6 + 6; st++) { double y = a * x + apx[st] - a * apy[st]; apx[st] = x; apy[st] = y; x = y; }
                s2[sd] = x;
                b[k + sd] = (float)((b[k + sd] + x) * 0.5);
            }
            lfo += step; if (lfo > 2 * Math.PI) lfo -= 2 * Math.PI;
        }
    }

    void Eq(float[] b, int n2)   // 3-band split by one-pole low-passes at 200 Hz and 5 kHz; the bands sum back to the input exactly when flat
    {
        double a1 = 1 - Math.Exp(-2 * Math.PI * 200 / Engine.SR), a2 = 1 - Math.Exp(-2 * Math.PI * 5000 / Engine.SR);
        double gl = Math.Pow(10, EqDb(0) / 20), gm = Math.Pow(10, EqDb(1) / 20), gh = Math.Pow(10, EqDb(2) / 20);
        for (int i = 0; i < n2; i++)
        {
            int s = i & 1;
            s1[s] += a1 * (b[i] - s1[s]); s2[s] += a2 * (b[i] - s2[s]);
            b[i] = (float)(s1[s] * gl + (s2[s] - s1[s]) * gm + (b[i] - s2[s]) * gh);
        }
    }

    void Compress(float[] b, int n2, float[] key)   // feed-forward, stereo-linked peak detector on key (b itself unless sidechained); 5 ms attack, 120 ms release
    {
        double thr = ThreshDb(), slope = 1 - 1 / Ratio(), makeup = MakeupDb();
        double att = Math.Exp(-1 / (0.005 * Engine.SR)), rel = Math.Exp(-1 / (0.12 * Engine.SR));
        for (int k = 0; k < n2; k += 2)
        {
            double peak = Math.Max(Math.Abs(key[k]), Math.Abs(key[k + 1]));
            double want = Math.Max(0, 20 * Math.Log10(Math.Max(peak, 1e-6)) - thr) * slope;   // dB of gain reduction this sample asks for
            s1[0] = want + (want > s1[0] ? att : rel) * (s1[0] - want);
            float g = (float)Math.Pow(10, (makeup - s1[0]) / 20);
            b[k] *= g; b[k + 1] *= g;
        }
    }

    void Delay(float[] b, int n2)
    {
        int len = Engine.SR + 1;   // 1 s maximum delay
        if (dl == null) dl = new float[len * 2];
        int d = (int)(DelaySec() * Engine.SR);
        float fb = P[1] * 0.9f, mix = P[2];
        for (int k = 0; k < n2; k += 2)
        {
            int rp = (wp - d + len) % len;
            float yl = dl[2 * rp], yr = dl[2 * rp + 1];
            dl[2 * wp] = b[k] + yr * fb;   // cross feedback = ping-pong
            dl[2 * wp + 1] = b[k + 1] + yl * fb;
            b[k] += yl * mix; b[k + 1] += yr * mix;
            wp = (wp + 1) % len;
        }
    }

    void Reverb(float[] b, int n2)   // Freeverb-style: 4 damped combs + 2 allpasses per side
    {
        if (comb == null)
        {
            comb = new float[8][]; ap = new float[4][];
            for (int s = 0; s < 2; s++)
            {
                for (int j = 0; j < 4; j++) comb[s * 4 + j] = new float[CombLen[j] + 23 * s];   // +23 samples on the right = stereo width
                for (int j = 0; j < 2; j++) ap[s * 2 + j] = new float[ApLen[j] + 23 * s];
            }
        }
        float fb = 0.7f + P[0] * 0.28f, damp = P[1] * 0.4f, mix = P[2];
        for (int k = 0; k < n2; k += 2)
        {
            float input = (b[k] + b[k + 1]) * 0.015f;
            for (int s = 0; s < 2; s++)
            {
                float o = 0;
                for (int j = s * 4; j < s * 4 + 4; j++)
                {
                    var c = comb[j]; float y = c[ci[j]];
                    store[j] = y * (1 - damp) + store[j] * damp;
                    c[ci[j]] = input + store[j] * fb;
                    if (++ci[j] == c.Length) ci[j] = 0;
                    o += y;
                }
                for (int j = s * 2; j < s * 2 + 2; j++)
                {
                    var a = ap[j]; float bo = a[ai[j]];
                    a[ai[j]] = o + bo * 0.5f;
                    o = bo - o;
                    if (++ai[j] == a.Length) ai[j] = 0;
                }
                b[k + s] = b[k + s] * (1 - mix * 0.5f) + o * mix * 3;
            }
        }
    }

    void Filter(float[] b, int n2)   // trapezoidal state-variable filter: stable at any cutoff
    {
        double g = Math.Tan(Math.PI * Math.Min(CutoffHz(), 0.45 * Engine.SR) / Engine.SR), k = 1 / (0.5 + P[1] * 9.5);
        double a1 = 1 / (1 + g * (g + k)), a2 = g * a1, a3 = g * a2;
        int mode = Mode();
        for (int i = 0; i < n2; i++)
        {
            int s = i & 1;
            double v0 = b[i], v3 = v0 - s2[s], v1 = a1 * s1[s] + a2 * v3, v2 = s2[s] + a2 * s1[s] + a3 * v3;
            s1[s] = 2 * v1 - s1[s]; s2[s] = 2 * v2 - s2[s];
            b[i] = (float)(mode == 0 ? v2 : mode == 1 ? v1 : v0 - k * v1 - v2);
        }
    }

    void Distort(float[] b, int n2)   // tanh waveshaper into a one-pole tone filter
    {
        double gain = 1 + P[0] * 49, a = 1 - Math.Exp(-2 * Math.PI * 500 * Math.Pow(24, P[1]) / Engine.SR);
        float mix = P[2];
        for (int i = 0; i < n2; i++)
        {
            int s = i & 1;
            s1[s] += a * (Math.Tanh(b[i] * gain) * 0.5 - s1[s]);
            b[i] = (float)(b[i] * (1 - mix) + s1[s] * mix);
        }
    }
}

// A pattern placed on the playlist. It plays from Start for its length; a clip longer than its pattern repeats it, and a
// trimmed clip skips the pattern's first Offset 16ths.
class Clip
{
    public int Track, Pattern;
    public int Lane = -1;          // >= 0: an automation clip playing that lane (Project.Automation) instead of a pattern
    public double Start, Offset;   // in 16ths
    public double Len = -1;        // in 16ths; -1 = as long as its pattern (and it follows the pattern's length until resized)
    public double Length(Project p) { return Len > 0 ? Len : Lane < 0 && Pattern >= 0 && Pattern < p.Patterns.Count ? p.Patterns[Pattern].Length : Project.Steps; }
    public Clip Copy() { return (Clip)MemberwiseClone(); }
}

class Track { public string Name; public int Color; public bool Mute; }
class Link { public int Cc; public Lane Target; }   // a MIDI controller knob (CC number) moving a control, like FL's "Link to controller"   // a playlist track's name, colour and mute; null name = "Track N"
class Marker { public double At; public string Name; public int Beats; }   // at a 16th in the song; Beats > 0: the time signature becomes Beats/4 from here

// The playlist seen as a [track, bar] grid of clips that start on a bar and follow their pattern's length: how the playlist
// worked before clips could be trimmed and resized. Code and tests that place whole patterns use it.
class PlaylistGrid
{
    readonly Project p;
    public PlaylistGrid(Project p) { this.p = p; }
    public int this[int t, int b]
    {
        get { foreach (var c in p.Clips) if (c.Track == t && c.Start == b * Project.Steps) return c.Pattern; return -1; }
        set { p.Clips.RemoveAll(c => c.Track == t && c.Start == b * Project.Steps); if (value >= 0) p.Clips.Add(new Clip { Track = t, Start = b * Project.Steps, Pattern = value }); }
    }
}

class Project
{
    public const int Steps = 16, Tracks = 500, Bars = 32, Inserts = 126;   // Steps = 16ths per bar; Bars: the span of automation lanes; insert 0 = master, then 125 like FL
    public const int MaxBars = 999;                                      // the longest song
    public const int MinBpm = 40, MaxBpm = 300;
    public int Bpm = 128;
    public float Swing;   // 0 = straight; 1 = every off-beat 16th is played half a 16th late (a 75/25 shuffle), like FL's swing knob
    public Insert[] Mixer = new Insert[Inserts];
    public List<Channel> Channels = new List<Channel>();
    public List<Pattern> Patterns = new List<Pattern>();
    public List<Clip> Clips = new List<Clip>();
    public readonly PlaylistGrid Playlist;
    public readonly Track[] TrackInfo = new Track[Tracks];   // null = a default track
    public List<Marker> Markers = new List<Marker>();
    public double LoopFrom, LoopTo;                          // in 16ths; a loop region for Song mode when LoopTo > LoopFrom
    public List<Lane> Automation = new List<Lane>();
    public List<string> Warnings = new List<string>();

    public Project()
    {
        Playlist = new PlaylistGrid(this);
        for (int i = 0; i < Inserts; i++) Mixer[i] = new Insert { Vol = i == 0 ? 0.8f : 1f };
    }

    public List<Link> Links = new List<Link>();   // MIDI controller knobs linked to controls

    // A lane's value at song position pos (16ths): from the automation clip playing there, or (a lane on no clip) its own curve
    // from bar 1; null when it has clips but none plays here (the control keeps its value).
    public float? LaneValue(int k, double pos)
    {
        var l = Automation[k]; bool any = false;
        foreach (var c in Clips)
        {
            if (c.Lane != k) continue;
            any = true;
            if (TrackMuted(c.Track) || pos < c.Start - 1e-9 || pos >= c.Start + c.Length(this) - 1e-9) continue;
            return l.At(pos - c.Start + c.Offset);
        }
        return any ? (float?)null : l.At(pos);
    }

    public void RemoveLane(int k)   // with its clips; later lanes' clips renumbered
    {
        Automation.RemoveAt(k);
        Clips.RemoveAll(c => c.Lane == k);
        foreach (var c in Clips) if (c.Lane > k) c.Lane--;
    }

    public int FreeTrack()   // the first playlist track with nothing on it
    {
        for (int t = 0; t < Tracks; t++) if (!Clips.Any(c => c.Track == t)) return t;
        return Tracks - 1;
    }

    public Track TrackAt(int t) { return TrackInfo[t] ?? (TrackInfo[t] = new Track()); }
    public bool TrackMuted(int t) { return t >= 0 && t < Tracks && TrackInfo[t] != null && TrackInfo[t].Mute; }
    public string TrackName(int t) { return TrackInfo[t] != null && TrackInfo[t].Name != null ? TrackInfo[t].Name : "Track " + (t + 1); }

    // Every pattern sounding at song step abs, with the step inside it, as Play hears it (muted tracks left out).
    public List<KeyValuePair<Pattern, int>> SoundingAt(int abs)
    {
        var hits = new List<KeyValuePair<Pattern, int>>();
        foreach (var c in Clips)
        {
            double rel = abs - c.Start;
            if (c.Lane >= 0 || rel < -1e-9 || rel >= c.Length(this) - 1e-9 || c.Pattern < 0 || c.Pattern >= Patterns.Count || TrackMuted(c.Track)) continue;
            var pat = Patterns[c.Pattern];
            hits.Add(new KeyValuePair<Pattern, int>(pat, (int)Math.Floor((rel + c.Offset) % pat.Length + 1e-6) % pat.Length));
        }
        return hits;
    }

    // Time signature at a song step: bar starts follow the markers (4/4 before the first). Returns whether step starts a bar,
    // and its bar number (from 1) and beats per bar.
    public bool BarAt(double step, out int bar, out int beats)
    {
        double from = 0; bar = 1; beats = 4;
        foreach (var m in Markers.Where(x => x.Beats > 0).OrderBy(x => x.At))
        {
            if (m.At > step + 1e-9) break;
            int n = (int)Math.Ceiling((m.At - from) / (beats * 4) - 1e-9); bar += n; from = m.At; beats = m.Beats;   // a change starts a new bar
        }
        double into = step - from; bar += (int)Math.Floor(into / (beats * 4) + 1e-9);
        return Math.Abs(into % (beats * 4)) < 1e-9 || Math.Abs(into % (beats * 4) - beats * 4) < 1e-9;
    }

    public static Project Demo()
    {
        var p = new Project();
        foreach (var k in new[] { "kick", "snare", "hat", "synth" })
            p.Channels.Add(new Channel { Kind = k, Name = char.ToUpper(k[0]) + k.Substring(1) });
        var a = new Pattern { Name = "Pattern 1" };
        var b = new Pattern { Name = "Pattern 2" };
        p.Patterns.Add(a); p.Patterns.Add(b);
        for (int s = 0; s < 16; s += 4) a.For(0).Add(new Note { Step = s, Key = 60, Len = 1 });
        a.For(1).Add(new Note { Step = 4, Key = 60, Len = 1 });
        a.For(1).Add(new Note { Step = 12, Key = 60, Len = 1 });
        for (int s = 2; s < 16; s += 4) { a.For(2).Add(new Note { Step = s, Key = 60, Len = 1 }); b.For(2).Add(new Note { Step = s, Key = 60, Len = 1 }); }
        int[] keys = { 57, 60, 64, 67 };
        for (int i = 0; i < 4; i++) b.For(3).Add(new Note { Step = i * 4, Key = keys[i], Len = 3 });
        for (int bar = 0; bar < 4; bar++) p.Playlist[0, bar] = 0;
        for (int bar = 2; bar < 6; bar++) p.Playlist[1, bar] = 1;
        for (int i = 0; i < 4; i++) p.Channels[i].Insert = i + 1;   // like FL templates: one insert per channel
        p.Mixer[2].Slots[0] = new Effect("Reverb");
        p.Mixer[4].Slots[0] = new Effect("Delay");
        return p;
    }

    // Copy for offline export: each effect gets fresh DSP state so the live audio thread's delay/reverb buffers aren't touched.
    // Channels are copied too (their sound settings can be automated); their samples, the patterns and the playlist are shared,
    // which is safe because export runs on the UI thread, their only writer.
    public Project ExportCopy()
    {
        var p = new Project { Bpm = Bpm, Swing = Swing, Channels = Channels.Select(c => c.Copy()).ToList(), Patterns = Patterns, Clips = Clips, Markers = Markers,
                              Automation = Automation };   // lanes target by index, so they drive the copy
        Array.Copy(TrackInfo, p.TrackInfo, Tracks);
        for (int i = 0; i < Inserts; i++)
        {
            Insert m = Mixer[i], c = p.Mixer[i];
            c.Vol = m.Vol; c.Pan = m.Pan; c.Mute = m.Mute; c.ToMaster = m.ToMaster; c.Sidechain = m.Sidechain;
            c.Sends = m.Sends.Select(r => new Route { To = r.To, Level = r.Level }).ToList();   // own copies: send-level lanes drive the copy, not the live mixer
            for (int s = 0; s < Insert.SlotCount; s++)
                if (m.Slots[s] != null) { c.Slots[s] = new Effect(m.Slots[s].Type); Array.Copy(m.Slots[s].P, c.Slots[s].P, c.Slots[s].P.Length); c.Slots[s].Bypass = m.Slots[s].Bypass; c.Slots[s].Mix = m.Slots[s].Mix; }
        }
        return p;
    }

    // Routing: a send from -> to, or a sidechain (to listens to from), is allowed if it can't loop back: to must not already
    // feed from. The master (0) only receives.
    public bool CanRoute(int from, int to)
    {
        if (from < 1 || to < 0 || from == to || from >= Inserts || to >= Inserts) return false;
        return to == 0 || !Feeds(to, from);
    }

    bool Feeds(int a, int b)   // does a's sound reach b through sends and sidechains?
    {
        var seen = new bool[Inserts]; var todo = new Stack<int>(); todo.Push(a);
        while (todo.Count > 0)
        {
            int x = todo.Pop();
            if (x == b) return true;
            if (x <= 0 || seen[x]) continue;
            seen[x] = true;
            foreach (var r in Mixer[x].Sends) todo.Push(r.To);
            for (int y = 1; y < Inserts; y++) if (Mixer[y].Sidechain == x) todo.Push(y);
        }
        return false;
    }

    // An order to process the inserts in: each after everything that sends to it or sidechains it (the master last).
    public int[] ProcessOrder()
    {
        var indeg = new int[Inserts];
        for (int i = 1; i < Inserts; i++)
        {
            foreach (var r in Mixer[i].Sends) if (r.To > 0 && r.To < Inserts && r.To != i) indeg[r.To]++;
            int sc = Mixer[i].Sidechain; if (sc > 0 && sc < Inserts && sc != i) indeg[i]++;
        }
        var order = new List<int>(Inserts); var ready = new Queue<int>();
        for (int i = 1; i < Inserts; i++) if (indeg[i] == 0) ready.Enqueue(i);
        while (ready.Count > 0)
        {
            int i = ready.Dequeue(); order.Add(i);
            foreach (var r in Mixer[i].Sends) if (r.To > 0 && r.To < Inserts && r.To != i && --indeg[r.To] == 0) ready.Enqueue(r.To);
            for (int y = 1; y < Inserts; y++) if (Mixer[y].Sidechain == i && y != i && --indeg[y] == 0) ready.Enqueue(y);
        }
        for (int i = 1; i < Inserts; i++) if (!order.Contains(i)) order.Add(i);   // a loop in a damaged file: still play everything
        return order.ToArray();
    }

    public void RemoveChannel(int i)   // patterns and sound-setting lanes refer to channels by index, so later ones shift down with them
    {
        Channels.RemoveAt(i);
        foreach (var pat in Patterns) if (i < pat.Notes.Count) pat.Notes.RemoveAt(i);
        for (int k = Automation.Count - 1; k >= 0; k--) if (Automation[k].Ch == i) RemoveLane(k);   // its lanes go, with their clips
        foreach (var l in Automation) if (l.Ch > i) l.Ch--;
        Links.RemoveAll(lk => lk.Target.Ch == i);
        foreach (var lk in Links) if (lk.Target.Ch > i) lk.Target.Ch--;
    }

    // Move an insert's effect from one slot to another; the ones in between shift by one. Automation lanes on that insert's
    // effects follow them. Returns each old slot's new index.
    public int[] MoveEffect(int ins, int from, int to)
    {
        var where = Enumerable.Range(0, Insert.SlotCount)
            .Select(k => k == from ? to : from < to && k > from && k <= to ? k - 1 : from > to && k >= to && k < from ? k + 1 : k).ToArray();
        var moved = new Effect[Insert.SlotCount];
        for (int k = 0; k < Insert.SlotCount; k++) moved[where[k]] = Mixer[ins].Slots[k];
        Mixer[ins].Slots = moved;   // one reference swap: the audio thread sees the old chain or the new one, never half of each
        foreach (var l in Automation) if (!l.Tempo && l.Ch < 0 && l.Ins == ins && l.Slot >= 0) l.Slot = where[l.Slot];
        return where;
    }

    public void RemovePattern(int k)   // the playlist stores pattern indices: clear this one's cells, shift later ones down
    {
        Patterns.RemoveAt(k);
        Clips.RemoveAll(c => c.Lane < 0 && c.Pattern == k);
        foreach (var c in Clips) if (c.Lane < 0 && c.Pattern > k) c.Pattern--;
    }

    public int SongBars()   // whole bars to the end of the last clip
    {
        double end = Steps;
        foreach (var c in Clips) end = Math.Max(end, c.Start + c.Length(this));
        return Math.Max(1, Math.Min(MaxBars, (int)Math.Ceiling(end / Steps - 1e-9)));
    }

    public void MoveClip(int t0, int b0, int t1, int b1, bool copy)   // the clip starting at track t0, bar b0 goes to t1, b1; copy keeps the original
    {
        var c = Clips.FirstOrDefault(x => x.Track == t0 && x.Start == b0 * Steps);
        if (c == null || (t0 == t1 && b0 == b1)) return;
        Clips.RemoveAll(x => x != c && x.Track == t1 && x.Start == b1 * Steps);   // replaces a clip that started there, like placing a pattern does
        if (copy) { c = c.Copy(); Clips.Add(c); }
        c.Track = t1; c.Start = b1 * Steps;
    }

    public int ClipAt(int t, int b)   // start bar of a clip on track t that covers bar b, or -1
    {
        foreach (var c in Clips) if (c.Track == t && c.Start < (b + 1) * Steps - 1e-9 && c.Start + c.Length(this) > b * Steps + 1e-9) return (int)(c.Start / Steps);
        return -1;
    }
    public void Save(string path) { File.WriteAllLines(path, Lines()); }

    public List<string> Lines()   // the whole project as text: the file format, and the undo snapshots
    {
        var w = new List<string> { "MINIDAW\t1", "bpm\t" + Bpm, "swing\t" + Swing.ToString("R") };
        foreach (var c in Channels)
            w.Add(string.Join("\t", "ch", c.Kind, c.Vol.ToString("R"), c.Pan.ToString("R"), c.Mute ? "1" : "0", Clean(c.Name), c.SamplePath ?? "", c.Insert,
                              string.Join("\t", Enumerable.Range(0, 4).Select(i => c.K(i).ToString("R"))), c.Color, Clean(c.Group ?? ""), c.ArpDir, c.ArpRange, c.ArpTime, c.ArpGate.ToString("R"), c.Glide.ToString("R"),
                              c.Loop ? "1" : "0", c.Reverse ? "1" : "0", c.LoopStart.ToString("R"), c.LoopEnd.ToString("R"), c.Stretch.ToString("R"),
                              c.Slices == null ? "" : string.Join(",", c.Slices), c.Syn == null ? "" : string.Join(",", c.Syn.Select(v => v.ToString("R")))));
        for (int i = 0; i < Inserts; i++)
        {
            var m = Mixer[i];
            w.Add(string.Join("\t", "ins", i, m.Vol.ToString("R"), m.Pan.ToString("R"), m.Mute ? "1" : "0", m.SendTo, m.SendLevel.ToString("R"), m.Sidechain, m.ToMaster ? "1" : "0",
                              string.Join(",", m.Sends.Skip(1).Select(r => r.To + ":" + r.Level.ToString("R")))));   // the first send in its old place; more sends after
            for (int s = 0; s < Insert.SlotCount; s++)
                if (m.Slots[s] != null) w.Add(string.Join("\t", "fx", i, s, m.Slots[s].Type, string.Join("\t", m.Slots[s].P.Select(v => v.ToString("R"))), m.Slots[s].Bypass ? "1" : "0", m.Slots[s].Mix.ToString("R")));   // as many settings as the effect has
        }
        foreach (var pat in Patterns)
        {
            w.Add("pat\t" + Clean(pat.Name) + "\t" + pat.Bars);
            for (int ch = 0; ch < pat.Notes.Count; ch++)
                foreach (var n in pat.Notes[ch]) w.Add(string.Join("\t", "n", ch, n.Step.ToString("R"), n.Key, n.Len.ToString("R"), n.Vel, n.Pan.ToString("R"), n.Fine, n.Slide ? "1" : "0"));
        }
        foreach (var c in Clips) w.Add(string.Join("\t", "clip", c.Track, c.Start.ToString("R"), c.Pattern, c.Len.ToString("R"), c.Offset.ToString("R"), c.Lane));

        foreach (var lk in Links) w.Add(string.Join("\t", "link", lk.Cc, lk.Target.Tempo ? "1" : "0", lk.Target.Ch, lk.Target.Ins, lk.Target.Slot, lk.Target.Param));
        for (int t = 0; t < Tracks; t++)
        {
            var tr = TrackInfo[t];
            if (tr != null && (tr.Name != null || tr.Color != 0 || tr.Mute)) w.Add(string.Join("\t", "track", t, Clean(tr.Name ?? ""), tr.Color, tr.Mute ? "1" : "0"));
        }
        foreach (var m in Markers) w.Add(string.Join("\t", "marker", m.At.ToString("R"), m.Beats, Clean(m.Name ?? "")));
        if (LoopTo > LoopFrom) w.Add(string.Join("\t", "loop", LoopFrom.ToString("R"), LoopTo.ToString("R")));
        foreach (var l in Automation)   // "autotempo"; "autoch" = a channel's sound setting; "auto" = a mixer control (the older line kept as it was)
            w.Add(l.Tempo ? "autotempo\t" + string.Join("\t", l.Values.Select(v => v.ToString("R")))
                : l.Ch >= 0 ? string.Join("\t", "autoch", l.Ch, l.Param, string.Join("\t", l.Values.Select(v => v.ToString("R"))))
                            : string.Join("\t", "auto", l.Ins, l.Slot, l.Param, string.Join("\t", l.Values.Select(v => v.ToString("R")))));
        for (int k = 0; k < Automation.Count; k++)   // automation points, after the lanes they belong to (the lane lines hold the curve they make)
            if (Automation[k].Points != null) w.Add("pts\t" + k + "\t" + string.Join(";", Automation[k].Points.Select(q => q.At.ToString("R") + ":" + q.Value.ToString("R") + ":" + q.Tension.ToString("R"))));
        return w;
    }

    static string Clean(string s) { return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

    static int Int(string s, int lo, int hi)
    {
        int v = int.Parse(s, CultureInfo.InvariantCulture);
        if (v < lo || v > hi) throw new InvalidDataException("Value " + v + " out of range " + lo + ".." + hi);
        return v;
    }

    static double Dbl(string s, double lo, double hi)   // a note position or length: out of range means a damaged file
    {
        double v = double.Parse(s, CultureInfo.InvariantCulture);
        if (double.IsNaN(v) || v < lo || v > hi) throw new InvalidDataException("Value " + s + " out of range");
        return v;
    }

    static float Num(string s, float lo, float hi)
    {
        float v = float.Parse(s, CultureInfo.InvariantCulture);
        if (float.IsNaN(v)) throw new InvalidDataException("NaN in project file.");   // a NaN would silence the whole mix
        return Math.Max(lo, Math.Min(hi, v));
    }

    public static Project Load(string path) { return Parse(File.ReadAllLines(path), null); }

    // samplesFrom: reuse audio already in memory for the same file (undo/redo) instead of reading it from disk again
    public static Project Parse(string[] lines, Project samplesFrom)
    {
        if (lines.Length == 0 || !lines[0].StartsWith("MINIDAW\t")) throw new InvalidDataException("Not a MiniDAW project.");
        var p = new Project();
        Pattern cur = null;
        for (int li = 1; li < lines.Length; li++)
        {
            var f = lines[li].Split('\t');
            if (f[0] == "bpm") p.Bpm = Int(f[1], 40, 300);
            else if (f[0] == "swing") p.Swing = Num(f[1], 0, 1);   // older files have none: straight
            else if (f[0] == "ch")
            {
                if (f.Length < 6 || Array.IndexOf(Channel.Kinds, f[1]) < 0) throw new InvalidDataException("Bad channel on line " + (li + 1));
                var c = new Channel
                {
                    Kind = f[1], Name = f[5], Mute = f[4] == "1",
                    Vol = Num(f[2], 0, 1), Pan = Num(f[3], -1, 1),
                    SamplePath = f.Length > 6 && f[6] != "" ? f[6] : null,
                    Insert = f.Length > 7 ? Int(f[7], 0, Inserts - 1) : 0,
                    Knob = f.Length > 11 ? new[] { Num(f[8], 0, 1), Num(f[9], 0, 1), Num(f[10], 0, 1), Num(f[11], 0, 1) } : null   // older files: built-in settings
                };
                if (f.Length > 17)   // older files: no colour, group or arpeggiator
                {
                    c.Color = int.Parse(f[12], CultureInfo.InvariantCulture); c.Group = f[13] == "" ? null : f[13];
                    c.ArpDir = Int(f[14], 0, Channel.ArpDirs.Length - 1); c.ArpRange = Int(f[15], 1, 4); c.ArpTime = Int(f[16], 0, Channel.ArpTimes.Length - 1); c.ArpGate = Num(f[17], 0.05f, 1);
                }
                if (f.Length > 18) c.Glide = Num(f[18], 0, 1);
                if (f.Length > 25)   // older files: no sampler options or analog synth settings
                {
                    c.Loop = f[19] == "1"; c.Reverse = f[20] == "1"; c.LoopStart = Num(f[21], 0, 1); c.LoopEnd = Num(f[22], 0, 1); c.Stretch = Num(f[23], 0.5f, 2);
                    c.Slices = f[24] == "" ? null : f[24].Split(',').Select(x => Int(x, 0, int.MaxValue)).ToArray();
                    if (f[25] != "") { var sy = f[25].Split(',').Select(x => Num(x, 0, 1)).ToArray(); if (sy.Length != Channel.SynNames.Length) throw new InvalidDataException("Bad synth settings on line " + (li + 1)); c.Syn = sy; }
                }
                var mem = c.Kind == "sample" && c.SamplePath != null && samplesFrom != null ? samplesFrom.Channels.Find(x => x.SamplePath == c.SamplePath && x.Sample != null) : null;
                if (mem != null) { c.Sample = mem.Sample; c.SampleR = mem.SampleR; c.SampleRate = mem.SampleRate; }   // arrays are never modified, so sharing is safe
                else if (c.Kind == "sample" && c.SamplePath != null)
                {
                    try { Wav.Load(c.SamplePath, c); }
                    catch (Exception e) { p.Warnings.Add(c.Name + ": " + e.Message); }
                }
                p.Channels.Add(c);
            }
            else if (f[0] == "ins")
            {
                if (f.Length < 5) throw new InvalidDataException("Bad mixer insert on line " + (li + 1));
                int idx = Int(f[1], 0, Inserts - 1); var m = p.Mixer[idx];
                m.Vol = Num(f[2], 0, 1); m.Pan = Num(f[3], -1, 1); m.Mute = f[4] == "1";
                if (f.Length > 7)   // older files stop at mute
                {
                    int to = Int(f[5], 0, Inserts - 1), sc = Int(f[7], 0, Inserts - 1);
                    if (to != 0 && !p.CanRoute(idx, to) || sc != 0 && !p.CanRoute(sc, idx))
                        throw new InvalidDataException("A send or sidechain would loop back on itself (line " + (li + 1) + ")");
                    m.SendTo = to; m.SendLevel = Num(f[6], 0, 1); m.Sidechain = sc;
                }
                if (f.Length > 8) m.ToMaster = f[8] != "0";
                if (f.Length > 9 && f[9] != "")
                    foreach (var part in f[9].Split(','))
                    {
                        var kv = part.Split(':'); int to = Int(kv[0], 1, Inserts - 1);
                        if (kv.Length != 2 || !p.CanRoute(idx, to)) throw new InvalidDataException("Bad send on line " + (li + 1));
                        m.Sends.Add(new Route { To = to, Level = Num(kv[1], 0, 1) });
                    }
            }
            else if (f[0] == "fx")
            {
                if (f.Length < 4) throw new InvalidDataException("Bad effect on line " + (li + 1));
                var fx = new Effect(f[3]); int np = fx.P.Length;
                if (f.Length < 4 + np) throw new InvalidDataException("Bad effect on line " + (li + 1));
                for (int j = 0; j < np; j++) fx.P[j] = Num(f[4 + j], 0, 1);
                fx.Bypass = f.Length > 4 + np && f[4 + np] == "1";   // older files have no bypass field: on
                fx.Mix = f.Length > 5 + np ? Num(f[5 + np], 0, 1) : 1;  // ... and no mix field: fully wet
                p.Mixer[Int(f[1], 0, Inserts - 1)].Slots[Int(f[2], 0, Insert.SlotCount - 1)] = fx;
            }
            else if (f[0] == "pat")   // older files have no length: one bar
            {
                cur = new Pattern { Name = f.Length > 1 ? f[1] : "Pattern", Length = (f.Length > 2 ? Int(f[2], 1, Pattern.MaxBars) : 1) * Steps };
                p.Patterns.Add(cur);
            }
            else if (f[0] == "n")
            {
                if (cur == null || f.Length < 5) throw new InvalidDataException("Note outside a pattern on line " + (li + 1));
                double step = Dbl(f[2], 0, cur.Length - 1e-6);
                cur.For(Int(f[1], 0, p.Channels.Count - 1)).Add(new Note { Step = step, Key = Int(f[3], 0, 127), Len = Dbl(f[4], 1e-6, cur.Length - step + 1e-6),
                                                                          Vel = f.Length > 5 ? Int(f[5], 1, 127) : 100,   // older files have no velocity,
                                                                          Pan = f.Length > 7 ? Num(f[6], -1, 1) : 0, Fine = f.Length > 7 ? Int(f[7], -100, 100) : 0,   // pan or fine pitch,
                                                                          Slide = f.Length > 8 && f[8] == "1" });                                                     // or slide flag
            }
            else if (f[0] == "pl")
            {
                if (f.Length < 4) throw new InvalidDataException("Bad playlist item on line " + (li + 1));
                p.Playlist[Int(f[1], 0, Tracks - 1), Int(f[2], 0, MaxBars - 1)] = Int(f[3], 0, int.MaxValue);   // older files: whole patterns on bars
            }
            else if (f[0] == "clip")
            {
                if (f.Length < 6) throw new InvalidDataException("Bad playlist clip on line " + (li + 1));
                double len = double.Parse(f[4], CultureInfo.InvariantCulture);
                p.Clips.Add(new Clip { Track = Int(f[1], 0, Tracks - 1), Start = Dbl(f[2], 0, MaxBars * Steps), Pattern = Int(f[3], -1, int.MaxValue),
                                       Len = len < 0 ? -1 : Dbl(f[4], 1e-6, MaxBars * Steps), Offset = Dbl(f[5], 0, MaxBars * Steps), Lane = f.Length > 6 ? Int(f[6], -1, int.MaxValue) : -1 });
            }
            else if (f[0] == "pts")
            {
                if (f.Length < 3) throw new InvalidDataException("Bad automation points on line " + (li + 1));
                var l = p.Automation[Int(f[1], 0, p.Automation.Count - 1)]; l.Points = new List<LanePoint>();
                foreach (var q in f[2].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = q.Split(':'); if (v.Length != 3) throw new InvalidDataException("Bad automation point on line " + (li + 1));
                    l.Points.Add(new LanePoint { At = Dbl(v[0], 0, MaxBars * Steps), Value = Num(v[1], 0, 1), Tension = Num(v[2], -1, 1) });
                }
                l.Render();
            }
            else if (f[0] == "link")
            {
                if (f.Length < 7) throw new InvalidDataException("Bad MIDI link on line " + (li + 1));
                p.Links.Add(new Link { Cc = Int(f[1], 0, 127), Target = new Lane { Tempo = f[2] == "1", Ch = Int(f[3], -1, int.MaxValue), Ins = Int(f[4], 0, Inserts - 1), Slot = Int(f[5], -1, Insert.SlotCount - 1), Param = Int(f[6], 0, Channel.LaneSyn + Channel.SynNames.Length - 1) } });
            }
            else if (f[0] == "track")
            {
                if (f.Length < 5) throw new InvalidDataException("Bad track on line " + (li + 1));
                var tr = p.TrackAt(Int(f[1], 0, Tracks - 1));
                tr.Name = f[2] == "" ? null : f[2]; tr.Color = int.Parse(f[3], CultureInfo.InvariantCulture); tr.Mute = f[4] == "1";
            }
            else if (f[0] == "marker")
            {
                if (f.Length < 4) throw new InvalidDataException("Bad marker on line " + (li + 1));
                p.Markers.Add(new Marker { At = Dbl(f[1], 0, MaxBars * Steps), Beats = Int(f[2], 0, 16), Name = f[3] });
            }
            else if (f[0] == "loop" && f.Length >= 3) { p.LoopFrom = Dbl(f[1], 0, MaxBars * Steps); p.LoopTo = Dbl(f[2], 0, MaxBars * Steps); }
            else if (f[0] == "auto")
            {
                var l = new Lane();
                if (f.Length != 4 + l.Values.Length) throw new InvalidDataException("Bad automation lane on line " + (li + 1));
                l.Ins = Int(f[1], 0, Inserts - 1); l.Slot = Int(f[2], -1, Insert.SlotCount - 1); l.Param = Int(f[3], 0, l.Slot < 0 ? 2 : Lane.FxMix);
                for (int k = 0; k < l.Values.Length; k++) l.Values[k] = Num(f[4 + k], 0, 1);
                p.Automation.Add(l);
            }
            else if (f[0] == "autotempo")
            {
                var l = new Lane { Tempo = true };
                if (f.Length != 1 + l.Values.Length) throw new InvalidDataException("Bad tempo lane on line " + (li + 1));
                for (int k = 0; k < l.Values.Length; k++) l.Values[k] = Num(f[1 + k], 0, 1);
                p.Automation.Add(l);
            }
            else if (f[0] == "autoch")   // channel lines come first in the file, so the channel must already exist
            {
                var l = new Lane();
                if (f.Length != 3 + l.Values.Length) throw new InvalidDataException("Bad automation lane on line " + (li + 1));
                l.Ch = Int(f[1], 0, p.Channels.Count - 1); l.Param = Int(f[2], 0, Channel.LaneSyn + Channel.SynNames.Length - 1);
                for (int k = 0; k < l.Values.Length; k++) l.Values[k] = Num(f[3 + k], 0, 1);
                p.Automation.Add(l);
            }
        }
        if (p.Patterns.Count == 0) p.Patterns.Add(new Pattern { Name = "Pattern 1" });
        if (p.Clips.Any(c => c.Lane < 0 && (c.Pattern < 0 || c.Pattern >= p.Patterns.Count))) throw new InvalidDataException("Playlist refers to a missing pattern.");
        if (p.Clips.Any(c => c.Lane >= p.Automation.Count || c.Lane >= 0 && c.Len <= 0)) throw new InvalidDataException("Playlist refers to a missing automation lane.");
        return p;
    }
}

// ---------------------------------------------------------------- WAV import
static class Wav
{
    public static void Load(string path, Channel c)
    {
        if (new FileInfo(path).Length > 256L << 20) throw new InvalidDataException("WAV larger than 256 MB.");
        var b = File.ReadAllBytes(path);
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a WAV file.");
        int fmt = 0, chans = 0, rate = 0, bits = 0, dataOff = -1, dataLen = 0;
        for (int p = 12; p + 8 <= b.Length; )
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int len = BitConverter.ToInt32(b, p + 4), body = p + 8;
            if (len < 0 || body + len < body) break;
            if (id == "fmt " && body + 16 <= b.Length)
            {
                fmt = BitConverter.ToUInt16(b, body); chans = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToUInt16(b, body + 14);
                if (fmt == 0xFFFE && len >= 26 && body + 26 <= b.Length) fmt = BitConverter.ToUInt16(b, body + 24); // WAVE_FORMAT_EXTENSIBLE subformat
            }
            else if (id == "data") { dataOff = body; dataLen = Math.Min(len, b.Length - body); }
            p = body + len + (len & 1);
        }
        if (dataOff < 0 || chans < 1 || rate < 1000 || rate > 384000) throw new InvalidDataException("WAV has no usable fmt/data chunk.");
        if (!((fmt == 1 && (bits == 16 || bits == 24)) || (fmt == 3 && bits == 32)))
            throw new InvalidDataException("Only 16/24-bit PCM or 32-bit float WAV is supported.");
        int bps = bits / 8, frames = dataLen / (bps * chans);
        Func<int, float> read = o => bits == 16 ? BitConverter.ToInt16(b, o) / 32768f : bits == 24 ? (b[o] | b[o + 1] << 8 | (sbyte)b[o + 2] << 16) / 8388608f : BitConverter.ToSingle(b, o);
        var left = new float[frames];
        var right = chans >= 2 ? new float[frames] : null;   // stereo keeps both sides; files with more channels use their front left/right
        for (int i = 0; i < frames; i++)
        {
            int o = dataOff + i * chans * bps;
            left[i] = read(o);
            if (right != null) right[i] = read(o + bps);
        }
        c.Sample = left; c.SampleR = right; c.SampleRate = rate; c.SamplePath = path;
    }

    public static byte[] Make16(float[] data, int rate, int chans) { return Make(data, rate, chans, 16); }

    public static byte[] Make(float[] data, int rate, int chans, int bits)   // 16 or 24-bit PCM, or 32-bit float; data is interleaved when chans > 1
    {
        int bps = bits / 8;
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length * bps); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)(bits == 32 ? 3 : 1)); w.Write((short)chans); w.Write(rate); w.Write(rate * bps * chans); w.Write((short)(bps * chans)); w.Write((short)bits);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length * bps);
        foreach (var x in data)
        {
            if (bits == 32) { w.Write(x); continue; }   // float keeps peaks above full scale
            int v = Sample(x, bits);                     // clamped: an overflowing integer would wrap into a click
            if (bits == 16) w.Write((short)v); else { w.Write((byte)v); w.Write((byte)(v >> 8)); w.Write((byte)(v >> 16)); }
        }
        return m.ToArray();
    }

    public static int Sample(float x, int bits) { int max = (1 << (bits - 1)) - 1; return (int)Math.Round(Math.Max(-1f, Math.Min(1f, x)) * max); }
}

// FLAC encoder (lossless): fixed-predictor subframes (orders 0-4) with Rice-coded residuals, the cheapest stereo decorrelation
// per frame, and the STREAMINFO MD5 so players and `flac -t` can verify it. Plain C#, no dependency.
static class Flac
{
    const int Block = 4096;

    class Bits   // MSB-first bit writer
    {
        public readonly List<byte> Out = new List<byte>(); ulong acc; int n;
        public void Put(long v, int bits)   // bits <= 32
        {
            if (bits == 0) return;
            acc = acc << bits | ((ulong)v & ((1UL << bits) - 1)); n += bits;
            while (n >= 8) { n -= 8; Out.Add((byte)(acc >> n)); }
        }
        public void Zeros(long count) { while (count > 0) { int k = (int)Math.Min(32, count); Put(0, k); count -= k; } }
        public void Align() { if (n > 0) Put(0, 8 - n); }
    }

    public static byte[] Encode(float[] lr, int rate, int bits)   // interleaved stereo; bits 16 or 24
    {
        int frames = lr.Length / 2;
        var L = new int[frames]; var R = new int[frames];
        for (int i = 0; i < frames; i++) { L[i] = Wav.Sample(lr[2 * i], bits); R[i] = Wav.Sample(lr[2 * i + 1], bits); }
        var file = new Bits();
        foreach (var c in "fLaC") file.Put(c, 8);
        file.Put(1, 1); file.Put(0, 7); file.Put(34, 24);   // last metadata block: STREAMINFO, 34 bytes
        file.Put(Block, 16); file.Put(Block, 16); file.Put(0, 24); file.Put(0, 24);
        file.Put(rate, 20); file.Put(1, 3); file.Put(bits - 1, 5); file.Put(frames >> 4, 32); file.Put(frames & 15, 4);
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var raw = new byte[frames * 2 * bits / 8]; int o = 0;
            for (int i = 0; i < frames; i++) foreach (int v in new[] { L[i], R[i] }) for (int b = 0; b < bits / 8; b++) raw[o++] = (byte)(v >> 8 * b);
            foreach (var b in md5.ComputeHash(raw)) file.Put(b, 8);
        }
        for (int start = 0, no = 0; start < frames; start += Block, no++) Frame(file, L, R, start, Math.Min(Block, frames - start), no, rate, bits);
        return file.Out.ToArray();
    }

    static void Frame(Bits file, int[] L, int[] R, int start, int n, int no, int rate, int bits)
    {
        var l = new int[n]; var r = new int[n]; var mid = new int[n]; var side = new int[n];
        for (int i = 0; i < n; i++) { l[i] = L[start + i]; r[i] = R[start + i]; mid[i] = (l[i] + r[i]) >> 1; side[i] = l[i] - r[i]; }
        long cl = Cost(l), cr = Cost(r), cm = Cost(mid), cs = Cost(side);
        long[] costs = { cl + cr, cl + cs, cr + cs, cm + cs };   // independent, left/side, side/right, mid/side
        int mode = Array.IndexOf(costs, costs.Min());
        var h = new Bits();
        h.Put(0x3FFE, 14); h.Put(0, 1); h.Put(0, 1);              // sync, reserved, fixed block size
        h.Put(n == Block ? 12 : 7, 4);                             // 4096, or "16-bit size at the end of the header"
        int rc = Array.IndexOf(new[] { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 }, rate);
        h.Put(Math.Max(0, rc), 4);                                 // 0 = read it from STREAMINFO
        h.Put(new[] { 1, 8, 9, 10 }[mode], 4);
        h.Put(bits == 16 ? 4 : 6, 3); h.Put(0, 1);
        Utf8(h, no);
        if (n != Block) h.Put(n - 1, 16);
        h.Put(Crc8(h.Out), 8);
        int[] a = mode == 2 ? side : mode == 3 ? mid : l, b = mode == 0 ? r : mode == 2 ? r : side;
        Subframe(h, a, bits + (mode == 2 ? 1 : 0));
        Subframe(h, b, bits + (mode == 1 || mode == 3 ? 1 : 0));   // the side channel needs one more bit
        h.Align();
        h.Put(Crc16(h.Out), 16);
        file.Out.AddRange(h.Out);
    }

    static int[] Residual(int[] x, int order)
    {
        var e = new int[x.Length - order];
        for (int i = order; i < x.Length; i++)
            e[i - order] = order == 0 ? x[i] : order == 1 ? x[i] - x[i - 1] : order == 2 ? x[i] - 2 * x[i - 1] + x[i - 2]
                : order == 3 ? x[i] - 3 * x[i - 1] + 3 * x[i - 2] - x[i - 3] : x[i] - 4 * x[i - 1] + 6 * x[i - 2] - 4 * x[i - 3] + x[i - 4];
        return e;
    }

    static int BestOrder(int[] x, out long cost)
    {
        int best = 0; cost = long.MaxValue;
        for (int k = 0; k <= Math.Min(4, x.Length - 1); k++)
        {
            long s = 0; foreach (var v in Residual(x, k)) s += Math.Abs((long)v);
            if (s < cost) { cost = s; best = k; }
        }
        return best;
    }

    static long Cost(int[] x) { long c; BestOrder(x, out c); return c; }

    static void Subframe(Bits o, int[] x, int bps)
    {
        if (x.All(v => v == x[0])) { o.Put(0, 1); o.Put(0, 6); o.Put(0, 1); o.Put(x[0], bps); return; }   // CONSTANT (silence)
        long ignore; int order = BestOrder(x, out ignore);
        var res = Residual(x, order);
        var body = new Bits();
        Rice(body, res, x.Length, order);
        if (body.Out.Count * 8L > (long)(x.Length - order) * bps)   // noise: verbatim is smaller
        {
            o.Put(0, 1); o.Put(1, 6); o.Put(0, 1);
            foreach (var v in x) o.Put(v, bps);
            return;
        }
        o.Put(0, 1); o.Put(8 | order, 6); o.Put(0, 1);   // FIXED predictor
        for (int i = 0; i < order; i++) o.Put(x[i], bps);   // warm-up samples
        Rice(o, res, x.Length, order);
    }

    // Partitioned Rice coding: the partition order and each partition's parameter are chosen from the residuals' sums
    // (bits ≈ count·(k+1) + sum >> k), as the reference encoder estimates.
    static void Rice(Bits o, int[] res, int n, int order)
    {
        var u = res.Select(v => (long)v << 1 ^ (long)v >> 63).ToArray();   // zigzag: 0, -1, 1, -2… → 0, 1, 2, 3…
        int bestP = 0; long bestBits = long.MaxValue; int[] bestK = null;
        for (int p = 0; p <= 8; p++)
        {
            if (n % (1 << p) != 0 || (n >> p) <= order) break;
            int parts = 1 << p, size = n >> p; var ks = new int[parts]; long total = 0;
            for (int q = 0, at = 0; q < parts; q++)
            {
                int cnt = q == 0 ? size - order : size; long sum = 0;
                for (int i = 0; i < cnt; i++) sum += u[at + i];
                at += cnt;
                long bestHere = long.MaxValue;
                for (int k = 0; k <= 30; k++) { long b = cnt * (k + 1L) + (sum >> k); if (b < bestHere) { bestHere = b; ks[q] = k; } }
                total += bestHere + 5;
            }
            if (total < bestBits) { bestBits = total; bestP = p; bestK = ks; }
        }
        bool wide = bestK.Max() > 14;   // RICE2 (5-bit parameters) when a parameter won't fit in 4 bits
        o.Put(wide ? 1 : 0, 2); o.Put(bestP, 4);
        for (int q = 0, at = 0; q < bestK.Length; q++)
        {
            int k = bestK[q], cnt = q == 0 ? (n >> bestP) - order : n >> bestP;
            o.Put(k, wide ? 5 : 4);
            for (int i = 0; i < cnt; i++) { long v = u[at + i]; o.Zeros(v >> k); o.Put(1, 1); o.Put(v, k); }
            at += cnt;
        }
    }

    static void Utf8(Bits b, long v)   // frame number, UTF-8 style
    {
        if (v < 0x80) { b.Put(v, 8); return; }
        int extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : 5;
        b.Put((0xFF << (7 - extra) & 0xFF) | (int)(v >> 6 * extra), 8);
        for (int i = extra - 1; i >= 0; i--) b.Put(0x80 | (v >> 6 * i & 0x3F), 8);
    }

    static int Crc8(List<byte> d) { int c = 0; foreach (var x in d) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? (c << 1 ^ 0x07) & 0xFF : c << 1 & 0xFF; } return c; }
    static int Crc16(List<byte> d) { int c = 0; foreach (var x in d) { c ^= x << 8; for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? (c << 1 ^ 0x8005) & 0xFFFF : c << 1 & 0xFFFF; } return c; }
}

// Windows' own Media Foundation: its MP3 encoder (Windows 8 and later) for export, and its decoders, which the self-test uses
// to check exported WAV, FLAC and MP3 files with a reader that isn't MiniDAW's own.
static class MediaFoundation
{
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateMediaType(out IMFMediaType t);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateSample(out IMFSample s);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer b);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr stream, IntPtr attrs, out IMFSinkWriter w);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attrs, out IMFSourceReader r);

    static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SubType = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        Audio = new Guid("73647561-0000-0010-8000-00aa00389b71"), Pcm = new Guid("00000001-0000-0010-8000-00aa00389b71"), Mp3Type = new Guid("00000055-0000-0010-8000-00aa00389b71"),
        Channels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), Rate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), BitsPer = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"),
        BlockAlign = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d"), AvgBytes = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    // Only the vtable slots up to the last method used are declared; the placeholders keep later methods in their places.
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void A01(); void A02(); void A03(); void A04();
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int v);
        void A06(); void A07(); void A08(); void A09(); void A10(); void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18();
        [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int v);
        void A20(); void A21();
        [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid v);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void A01(); void A02(); void A03(); void A04(); void A05(); void A06(); void A07(); void A08(); void A09(); void A10();
        void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19(); void A20();
        void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29(); void A30();   // IMFAttributes
        void S01(); void S02(); void S03();
        [PreserveSig] int SetSampleTime(long t);
        void S05();
        [PreserveSig] int SetSampleDuration(long d);
        void S07(); void S08();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer b);
        [PreserveSig] int AddBuffer(IMFMediaBuffer b);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buf, out int max, out int cur);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int len);
        [PreserveSig] int SetCurrentLength(int len);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType t, out int index);
        [PreserveSig] int SetInputMediaType(int index, IMFMediaType t, IntPtr encodingParams);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int index, IMFSample s);
        void W05(); void W06(); void W07(); void W08();
        [PreserveSig] int Finish();   // IMFSinkWriter::Finalize: writes the file's end and closes it
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void R01();
        [PreserveSig] int SetStreamSelection(int index, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void R03();
        [PreserveSig] int GetCurrentMediaType(int index, out IMFMediaType t);
        [PreserveSig] int SetCurrentMediaType(int index, IntPtr reserved, IMFMediaType t);
        void R06();
        [PreserveSig] int ReadSample(int index, int flags, out int actual, out int streamFlags, out long time, out IMFSample s);
    }

    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException(what + " failed (Media Foundation error 0x" + hr.ToString("X8") + ")."); }

    static void Start()
    {
        int hr;
        try { hr = MFStartup(0x00020070, 0); }
        catch (DllNotFoundException) { throw new InvalidOperationException("This Windows has no Media Foundation (on Windows \"N\" editions, install the Media Feature Pack)."); }
        Check(hr, "Starting Media Foundation");
    }

    static IMFMediaType PcmType(int rate, int chans, int bits)
    {
        IMFMediaType t; Check(MFCreateMediaType(out t), "MFCreateMediaType");
        t.SetGUID(MajorType, Audio); t.SetGUID(SubType, Pcm); t.SetUINT32(Channels, chans); t.SetUINT32(Rate, rate);
        t.SetUINT32(BitsPer, bits); t.SetUINT32(BlockAlign, chans * bits / 8); t.SetUINT32(AvgBytes, rate * chans * bits / 8);
        return t;
    }

    public static void Mp3(string path, float[] lr, int rate, int kbps)   // interleaved stereo
    {
        Start();
        IMFSinkWriter w = null;
        try
        {
            if (File.Exists(path)) File.Delete(path);   // the sink writer won't overwrite
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out w), "Creating the MP3 file");
            IMFMediaType outType; Check(MFCreateMediaType(out outType), "MFCreateMediaType");
            outType.SetGUID(MajorType, Audio); outType.SetGUID(SubType, Mp3Type); outType.SetUINT32(Channels, 2); outType.SetUINT32(Rate, rate);
            outType.SetUINT32(AvgBytes, kbps * 1000 / 8); outType.SetUINT32(BlockAlign, 1);
            int idx; Check(w.AddStream(outType, out idx), "Windows' MP3 encoder");
            Check(w.SetInputMediaType(idx, PcmType(rate, 2, 16), IntPtr.Zero), "Feeding the MP3 encoder");
            Check(w.BeginWriting(), "Starting the MP3 file");
            const int Chunk = 4096;
            for (int at = 0; at < lr.Length / 2; at += Chunk)
            {
                int n = Math.Min(Chunk, lr.Length / 2 - at);
                IMFMediaBuffer buf; Check(MFCreateMemoryBuffer(n * 4, out buf), "MFCreateMemoryBuffer");
                IntPtr p; int max, cur; Check(buf.Lock(out p, out max, out cur), "Lock");
                var pcm = new short[n * 2];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Wav.Sample(lr[2 * at + i], 16);
                Marshal.Copy(pcm, 0, p, pcm.Length);
                buf.Unlock(); buf.SetCurrentLength(n * 4);
                IMFSample s; Check(MFCreateSample(out s), "MFCreateSample");
                s.AddBuffer(buf); s.SetSampleTime(at * 10000000L / rate); s.SetSampleDuration(n * 10000000L / rate);
                Check(w.WriteSample(idx, s), "Encoding MP3");
                Marshal.ReleaseComObject(s); Marshal.ReleaseComObject(buf);
            }
            Check(w.Finish(), "Finishing the MP3 file");
        }
        finally { if (w != null) Marshal.ReleaseComObject(w); MFShutdown(); }
    }

    public static float[] Decode(string path, out int rate, out int chans)   // any file Windows can play, as interleaved floats
    {
        Start();
        IMFSourceReader r = null;
        try
        {
            Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out r), "Opening " + Path.GetFileName(path));
            const int FirstAudio = -3, All = -2;
            r.SetStreamSelection(All, false); r.SetStreamSelection(FirstAudio, true);
            IMFMediaType want; Check(MFCreateMediaType(out want), "MFCreateMediaType");
            want.SetGUID(MajorType, Audio); want.SetGUID(SubType, Pcm);
            Check(r.SetCurrentMediaType(FirstAudio, IntPtr.Zero, want), "Choosing PCM output");
            IMFMediaType got; Check(r.GetCurrentMediaType(FirstAudio, out got), "Reading the format");
            int bits; got.GetUINT32(Channels, out chans); got.GetUINT32(Rate, out rate); got.GetUINT32(BitsPer, out bits);
            var bytes = new List<byte>();
            while (true)
            {
                int actual, flags; long t; IMFSample s;
                Check(r.ReadSample(FirstAudio, 0, out actual, out flags, out t, out s), "Decoding");
                if (s != null)
                {
                    IMFMediaBuffer buf; s.ConvertToContiguousBuffer(out buf);
                    IntPtr p; int max, cur; buf.Lock(out p, out max, out cur);
                    var chunk = new byte[cur]; Marshal.Copy(p, chunk, 0, cur); bytes.AddRange(chunk);
                    buf.Unlock(); Marshal.ReleaseComObject(buf); Marshal.ReleaseComObject(s);
                }
                if ((flags & 2) != 0) break;   // end of stream
            }
            int bps = bits / 8; var outp = new float[bytes.Count / bps]; var b = bytes.ToArray();
            for (int i = 0; i < outp.Length; i++)
                outp[i] = bits == 16 ? BitConverter.ToInt16(b, i * 2) / 32768f : bits == 24 ? (b[i * 3] | b[i * 3 + 1] << 8 | (sbyte)b[i * 3 + 2] << 16) / 8388608f : BitConverter.ToInt32(b, i * 4) / 2147483648f;
            return outp;
        }
        finally { if (r != null) Marshal.ReleaseComObject(r); MFShutdown(); }
    }
}

// ---------------------------------------------------------------- audio engine
[StructLayout(LayoutKind.Sequential)]
struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }

[StructLayout(LayoutKind.Sequential)]
struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }

// Standard MIDI files (.mid). Export writes what Play would start, one track per channel (drums on MIDI channel 10 as General
// MIDI drum notes). Import turns each MIDI track/channel into a new rack channel (drums by GM note, others by GM program) and
// the notes into new patterns of up to Pattern.MaxBars bars, placed one after another on a free playlist track.
static class Midi
{
    const int Ppq = 96, PerStep = Ppq / 4;   // ticks per quarter note, and per 16th
    static readonly Dictionary<string, int> DrumNote = new Dictionary<string, int> { { "kick", 36 }, { "snare", 38 }, { "hat", 42 }, { "clap", 39 } };
    static readonly Dictionary<string, int> Program = new Dictionary<string, int> { { "synth", 81 }, { "square", 80 }, { "pluck", 24 }, { "pad", 89 }, { "bass", 38 }, { "keys", 4 }, { "sample", 0 }, { "analog", 81 } };   // GM, 0-based

    public static byte[] Export(Project p, bool song, int pattern)
    {
        var starts = new List<Tuple<double, int, Note>>();   // (song position in 16ths, channel, note)
        var tempo = new List<Tuple<int, int, byte[]>>();
        lock (p)
        {
            if (song)
            {
                int len = p.SongBars() * Project.Steps;
                int laneK = p.Automation.FindIndex(l => l.Tempo);
                for (int abs = 0, last = -1; abs < len; abs++)
                {
                    foreach (var hit in p.SoundingAt(abs))   // the same clips Engine.Trigger plays at this step
                    {
                        var pat = hit.Key; int s = hit.Value;
                        for (int ch = 0; ch < Math.Min(pat.Notes.Count, p.Channels.Count); ch++)
                            if (!p.Channels[ch].Mute) foreach (var n in pat.Notes[ch]) if (!n.Slide && n.Step >= s && n.Step < s + 1) starts.Add(Tuple.Create(abs + n.Step - s, ch, n));
                    }
                    float? tv = laneK < 0 ? null : p.LaneValue(laneK, abs);
                    int bpm = tv.HasValue ? (int)Math.Round(Project.MinBpm + tv.Value * (Project.MaxBpm - Project.MinBpm)) : last > 0 ? last : p.Bpm;
                    if (bpm != last) { tempo.Add(Tuple.Create(abs * PerStep, 2, Tempo(bpm))); last = bpm; }   // a tempo lane: one change per 16th it moves
                }
            }
            else
            {
                var pat = p.Patterns[Math.Min(pattern, p.Patterns.Count - 1)];
                for (int ch = 0; ch < Math.Min(pat.Notes.Count, p.Channels.Count); ch++)
                    if (!p.Channels[ch].Mute) foreach (var n in pat.Notes[ch]) if (!n.Slide) starts.Add(Tuple.Create(n.Step, ch, n));   // slide notes are bends, not notes
                tempo.Add(Tuple.Create(0, 2, Tempo(p.Bpm)));
            }
        }
        tempo.Add(Tuple.Create(0, 0, Meta(0x03, Encoding.UTF8.GetBytes("MiniDAW"))));
        tempo.Add(Tuple.Create(0, 1, Meta(0x58, new byte[] { 4, 2, 24, 8 })));   // 4/4
        var tracks = new List<byte[]> { Chunk(tempo) };
        int nextChan = 0;
        foreach (var group in starts.GroupBy(x => x.Item2).OrderBy(gr => gr.Key))
        {
            var c = p.Channels[group.Key]; bool drum = c.IsDrum;
            int mc = drum ? 9 : nextChan++ % 15; if (mc >= 9 && !drum) mc++;   // channel 10 is kept for drums
            var ev = new List<Tuple<int, int, byte[]>> { Tuple.Create(0, 0, Meta(0x03, Encoding.UTF8.GetBytes(c.Name ?? ""))) };
            if (!drum) ev.Add(Tuple.Create(0, 1, new byte[] { (byte)(0xC0 | mc), (byte)Program[c.Kind] }));
            foreach (var x in group)
            {
                int key = drum ? DrumNote[c.Kind] : Math.Max(0, Math.Min(127, x.Item3.Key)), vel = Math.Max(1, Math.Min(127, x.Item3.Vel));
                int on = (int)Math.Round(x.Item1 * PerStep), off = Math.Max(on + 1, (int)Math.Round((x.Item1 + x.Item3.Len) * PerStep));
                ev.Add(Tuple.Create(on, 3, new byte[] { (byte)(0x90 | mc), (byte)key, (byte)vel }));
                ev.Add(Tuple.Create(off, 2, new byte[] { (byte)(0x80 | mc), (byte)key, 0 }));   // offs sort before ons at the same tick
            }
            tracks.Add(Chunk(ev));
        }
        var file = new List<byte>(Encoding.ASCII.GetBytes("MThd"));
        BE(file, 6, 4); BE(file, 1, 2); BE(file, tracks.Count, 2); BE(file, Ppq, 2);
        foreach (var t in tracks) file.AddRange(t);
        return file.ToArray();
    }

    static byte[] Tempo(int bpm) { int us = 60000000 / Math.Max(1, bpm); return Meta(0x51, new[] { (byte)(us >> 16), (byte)(us >> 8), (byte)us }); }
    static byte[] Meta(int type, byte[] data) { var m = new List<byte> { 0xFF, (byte)type }; Var(m, data.Length); m.AddRange(data); return m.ToArray(); }
    static void BE(List<byte> o, int v, int n) { for (int i = n - 1; i >= 0; i--) o.Add((byte)(v >> 8 * i)); }
    static void Var(List<byte> o, int v) { var b = new List<byte> { (byte)(v & 0x7F) }; while ((v >>= 7) > 0) b.Insert(0, (byte)(0x80 | v & 0x7F)); o.AddRange(b); }

    static byte[] Chunk(List<Tuple<int, int, byte[]>> ev)   // (tick, order within the tick, event) → an MTrk chunk with delta times
    {
        var body = new List<byte>(); int at = 0;
        foreach (var e in ev.OrderBy(x => x.Item1).ThenBy(x => x.Item2)) { Var(body, e.Item1 - at); at = e.Item1; body.AddRange(e.Item3); }
        body.AddRange(new byte[] { 0, 0xFF, 0x2F, 0 });   // end of track
        var c = new List<byte>(Encoding.ASCII.GetBytes("MTrk")); BE(c, body.Count, 4); c.AddRange(body);
        return c.ToArray();
    }

    // ---- import
    class Reader   // bounds-checked: a damaged file stops with InvalidDataException, never an index error
    {
        readonly byte[] d; readonly int end; public int Pos;
        public Reader(byte[] d, int pos, int end) { this.d = d; Pos = pos; this.end = end; }
        public bool More { get { return Pos < end; } }
        public int U8() { if (Pos >= end) throw Bad(); return d[Pos++]; }
        public int BE(int n) { int v = 0; for (int i = 0; i < n; i++) v = v << 8 | U8(); return v; }
        public int Var() { int v = 0; for (int i = 0; i < 4; i++) { int b = U8(); v = v << 7 | b & 0x7F; if (b < 0x80) return v; } throw Bad(); }
        public int Skip(int n) { if (n < 0 || n > end - Pos) throw Bad(); int at = Pos; Pos += n; return at; }
    }
    static Exception Bad() { return new InvalidDataException("This MIDI file is damaged: it ends early or has an event that doesn't make sense."); }

    class N { public int Track, Chan, Key, On, Off, Vel; }

    static string KindFor(int program)   // General MIDI program → the closest built-in sound (no program change = piano, the GM default)
    {
        if (program < 8) return "keys";
        if (program >= 24 && program < 32) return "pluck";
        if (program >= 32 && program < 40) return "bass";
        if (program == 80) return "square";
        if (program >= 48 && program < 56 || program >= 88 && program < 96) return "pad";
        return "synth";
    }

    static string DrumKind(int n)   // GM drum note → the closest built-in drum: toms and low notes → kick, cymbals and percussion → hat
    {
        if (n == 39) return "clap";
        if (n == 37 || n == 38 || n == 40) return "snare";
        if (n == 42 || n == 44 || n == 46 || n >= 49) return "hat";
        return "kick";
    }

    // Adds the file's notes to p as new channels and patterns; returns a summary for the user.
    public static string Import(Project p, byte[] data, string title)
    {
        var r = new Reader(data, 0, data.Length);
        if (data.Length < 14 || r.BE(4) != 0x4D546864) throw new InvalidDataException("This isn't a MIDI file.");   // "MThd"
        int hl = r.BE(4); if (hl < 6) throw Bad();
        r.BE(2); int ntr = r.BE(2), div = r.BE(2); r.Skip(hl - 6);
        if (div == 0 || (div & 0x8000) != 0) throw new InvalidDataException("MIDI files timed in SMPTE frames aren't supported.");
        var notes = new List<N>(); var names = new Dictionary<int, string>(); var programs = new Dictionary<int, int>(); double fileBpm = 0;
        for (int t = 0; r.More && t < ntr; )
        {
            int id = r.BE(4), len = r.BE(4), start = r.Skip(len);
            if (id != 0x4D54726B) continue;   // not "MTrk": skip unknown chunks
            var tr = new Reader(data, start, start + len);
            int tick = 0, status = 0; var open = new Dictionary<int, Queue<int[]>>();
            while (tr.More)
            {
                tick += tr.Var();
                int b = tr.U8();
                if (b == 0xFF)   // meta: name, tempo, end
                {
                    int type = tr.U8(), n = tr.Var(), at = tr.Skip(n);
                    if (type == 0x03 && n > 0 && !names.ContainsKey(t)) names[t] = Encoding.GetEncoding(28591).GetString(data, at, n).Trim();
                    if (type == 0x51 && n == 3 && fileBpm == 0) fileBpm = 60e6 / Math.Max(1, data[at] << 16 | data[at + 1] << 8 | data[at + 2]);
                    if (type == 0x2F) break;
                    continue;
                }
                if (b == 0xF0 || b == 0xF7) { tr.Skip(tr.Var()); continue; }   // system exclusive
                if (b > 0xF0) throw Bad();
                int d1;
                if (b >= 0x80) { status = b; d1 = tr.U8(); } else if (status != 0) d1 = b; else throw Bad();   // running status
                int hi = status & 0xF0, ch = status & 0x0F, d2 = hi == 0xC0 || hi == 0xD0 ? 0 : tr.U8();
                if ((d1 | d2) >= 0x80) throw Bad();
                int k = ch << 7 | d1;
                if (hi == 0xC0 && !programs.ContainsKey(t << 4 | ch)) programs[t << 4 | ch] = d1;
                else if (hi == 0x90 && d2 > 0) { if (!open.ContainsKey(k)) open[k] = new Queue<int[]>(); open[k].Enqueue(new[] { tick, d2 }); }
                else if ((hi == 0x80 || hi == 0x90) && open.ContainsKey(k) && open[k].Count > 0)
                {
                    var on = open[k].Dequeue();
                    notes.Add(new N { Track = t, Chan = ch, Key = d1, On = on[0], Off = tick, Vel = on[1] });
                }
            }
            foreach (var kv in open) foreach (var on in kv.Value)   // never released: ends with the track
                notes.Add(new N { Track = t, Chan = kv.Key >> 7, Key = kv.Key & 0x7F, On = on[0], Off = tick, Vel = on[1] });
            t++;
        }
        if (notes.Count == 0) throw new InvalidDataException("This MIDI file has no notes.");

        Func<int, double> step = tk => Math.Round(tk * 96.0 / div) / 24;   // to 16ths, kept to 1/96 of a beat: triplets and swung timing survive
        int span = Pattern.MaxBars * Project.Steps; double endStep = notes.Max(n => Math.Max(step(n.On) + 0.25, step(n.Off)));
        int patCount = (int)Math.Ceiling(endStep / span - 1e-9), firstPat;
        var made = new Dictionary<string, int>();   // group → new channel index
        lock (p)
        {
            firstPat = p.Patterns.Count;
            for (int i = 0; i < patCount; i++)
            {
                var pat = new Pattern { Name = patCount == 1 ? title : title + " " + (i + 1) };
                pat.SetBars(Math.Max(1, Math.Min(Pattern.MaxBars, (int)Math.Ceiling((endStep - i * span) / Project.Steps - 1e-9))));
                p.Patterns.Add(pat);
            }
            foreach (var n in notes.OrderBy(x => x.On))
            {
                bool drum = n.Chan == 9;
                string kind = drum ? DrumKind(n.Key) : KindFor(programs.ContainsKey(n.Track << 4 | n.Chan) ? programs[n.Track << 4 | n.Chan] : 0);
                string group = drum ? kind : n.Track + "/" + n.Chan;
                if (!made.ContainsKey(group))
                {
                    string name = !drum && names.ContainsKey(n.Track) && names[n.Track] != "" ? names[n.Track] : Channel.Title(kind);
                    if (name.Length > 40) name = name.Substring(0, 40);
                    string free = name;
                    for (int k = 2; p.Channels.Any(c => c.Name == free); k++) free = name + " " + k;
                    p.Channels.Add(new Channel { Kind = kind, Name = free, Insert = Math.Min(p.Channels.Count + 1, Project.Inserts - 1) });
                    made[group] = p.Channels.Count - 1;
                }
                double s = step(n.On); int pi = Math.Min(patCount - 1, (int)(s / span)); var pt = p.Patterns[firstPat + pi]; double at = s - pi * span;
                if (at >= pt.Length) continue;
                var list = pt.For(made[group]); int key = drum ? 60 : n.Key;
                list.RemoveAll(x => x.Step == at && x.Key == key);   // two notes at the same moment and key: the later one wins, as in the piano roll
                list.Add(new Note { Step = at, Key = key, Len = Math.Max(1 / 24.0, Math.Min(Math.Max(drum ? 1 : 0, step(n.Off) - s), pt.Length - at)), Vel = Math.Max(1, Math.Min(127, n.Vel)) });
            }
            // place the patterns in order on the first playlist track that's empty for the whole length
            int bars = Math.Min(Project.MaxBars, (int)Math.Ceiling(endStep / Project.Steps - 1e-9)), track = -1;
            for (int t = 0; t < Project.Tracks && track < 0; t++)
                if (!p.Clips.Any(c => c.Track == t && c.Start < bars * Project.Steps)) track = t;   // a track that's empty for the whole length
            int placed = 0;
            if (track >= 0) for (int i = 0; i < patCount && i * Pattern.MaxBars < Project.MaxBars; i++, placed++) p.Playlist[track, i * Pattern.MaxBars] = firstPat + i;
            int total = p.Patterns.Skip(firstPat).Sum(x => x.Notes.Sum(l => l.Count));
            string where = track < 0 ? "No playlist track was free for it, so place the patterns yourself."
                : placed < patCount ? "Patterns 1–" + placed + " are on playlist track " + (track + 1) + "; the rest go past the playlist's end."
                : "They're on playlist track " + (track + 1) + (bars == 1 ? ", bar 1." : ", bars 1–" + bars + ".");
            return "Imported " + total + " notes into " + made.Count + " new channel" + (made.Count == 1 ? "" : "s") + " and " + patCount + " pattern" + (patCount == 1 ? "" : "s") + ". " + where
                + (fileBpm > 0 && Math.Abs(fileBpm - p.Bpm) > 0.5 ? "\nThe file's tempo is " + Math.Round(fileBpm) + " BPM; the project stays at " + p.Bpm + " BPM." : "");
        }
    }
}

class Engine
{
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

    public const int SR = 44100;
    const int Frames = 1024, NBuf = 4, MaxVoices = 64;   // ~93 ms of buffered output

    public class Voice { public double[] Ph; public double Z3; public Channel Ch; public int Key, Age, Len, Wait, GlideAge, GlideLen; public float Gain = 1, NotePan; public double Phase, Pos, Rate, Z1, Z2, Detune = 1, Shift, ShiftFrom, ShiftTo; public float[] Buf; public float Right; public bool Done, Live, Stereo; }   // Shift: semitones it's currently bent by (slides, glide), moving from ShiftFrom to ShiftTo over GlideLen samples   // Right/Stereo: a stereo sample's right side; Gain = velocity / 100; NotePan/Detune: the note's own pan and fine pitch; Wait = samples of silence before it starts (swing); Buf = pluck string; Live = played from a keyboard (follows the pitch wheel)

    // Arpeggiator: a channel's held notes (from patterns, or keys held on a keyboard) play one at a time on a clock instead of together.
    class Held { public int Key, Vel, Fine; public float Pan; public long End; public bool Live; }
    class Arp { public readonly List<Held> Notes = new List<Held>(); public int Index; public double Wait; }
    readonly Dictionary<Channel, Arp> arps = new Dictionary<Channel, Arp>();
    long now;   // the sample being rendered, counted from the engine's start
    int lastAbs; // song step last played (Song mode): automation reads its lanes there
    Arp ArpOf(Channel c) { Arp a; if (!arps.TryGetValue(c, out a)) arps[c] = a = new Arp(); return a; }

    public volatile Project Proj;
    public volatile bool Playing, SongMode;
    public volatile bool Metronome, CountIn;   // click on every beat; one bar of clicks before playback starts (set while recording)
    public volatile int CurPattern, PlayStep = -1;
    public volatile string LastError;
    public int StepLimit = int.MaxValue;   // offline export: no notes start at or after this step, so the song doesn't loop
    long rendered, songEnd = -1;           // frames rendered so far; the frame where step StepLimit would have started (-1 = not yet)
    public readonly float[] Peaks = new float[Project.Inserts];   // per-insert meter levels for the mixer view
    public readonly float[] EverPeak = new float[Project.Inserts]; // loudest each insert has been: export skips stems that stayed silent
    public int Tap;                                                // export of one stem: Render outputs this insert (post-fader) instead of the master
    readonly float[][] ib = new float[Project.Inserts][];          // per-insert stereo bus buffers
    float[] dryBuf;                                                 // scratch for effects' wet/dry mix
    public readonly ConcurrentQueue<Voice> Preview = new ConcurrentQueue<Voice>();
    public readonly ConcurrentQueue<Channel> Hush = new ConcurrentQueue<Channel>();   // stop every voice of this channel (the browser's previews)
    public readonly ConcurrentQueue<int> MidiIn = new ConcurrentQueue<int>();   // raw MIDI messages from the keyboard
    public readonly ConcurrentQueue<KeyValuePair<int, double>> MidiRec = new ConcurrentQueue<KeyValuePair<int, double>>();   // (message, pattern step as heard) for recording
    public volatile Channel MidiTarget;                                          // the channel the keyboard plays, like FL's selected channel
    public volatile Lane LearnTarget;                                            // waiting for a controller to link to this control
    public readonly ConcurrentQueue<int> Learned = new ConcurrentQueue<int>();   // the controller number that answered, for the UI
    readonly Voice[] held = new Voice[128];                                      // sounding keyboard notes, by key
    bool pedal; readonly bool[] sustained = new bool[128];                       // sustain pedal down; keys let go while it was down
    const double BendRange = 2;                                                  // semitones at full wheel, the usual default
    double bendMul = 1;                                                          // pitch wheel as a frequency factor for live notes
    const double VibHz = 5.5, VibSemis = 0.5;                                    // vibrato speed, and its depth at full mod wheel (± semitones)
    double modDepth;                                                             // mod wheel, 0..1

    readonly List<Voice> voices = new List<Voice>();
    double toNext; int step; bool wasPlaying; uint rng = 2463534242;
    int countLeft;                    // 16ths of count-in still to go; the song waits until it reaches 0
    int clickAge = -1; double clickHz; float[] clickBuf;   // the metronome's current click (-1 = none), mixed in after the master fader
    IntPtr hwo; Thread thread; volatile bool running;

    // Render interleaved stereo floats. Runs on the audio thread (or the self-test).
    public void Render(float[] lr, int frames)
    {
        var proj = Proj;
        now = rendered;
        if (arps.Count > 0 && arps.Keys.Any(c => !proj.Channels.Contains(c)))   // the project was replaced (open, undo): forget its arpeggiators
            foreach (var gone in arps.Keys.Where(c => !proj.Channels.Contains(c)).ToList()) arps.Remove(gone);
        Channel hush; while (Hush.TryDequeue(out hush)) foreach (var v in voices) if (v.Ch == hush) v.Done = true;   // a browser preview cut short
        Voice pv; while (Preview.TryDequeue(out pv)) AddVoice(pv);
        bool playing = Playing;
        if (playing && !wasPlaying) { step = 0; toNext = 0; countLeft = CountIn ? Project.Steps : 0; }
        if (!playing && wasPlaying) PlayStep = -1;
        wasPlaying = playing;
        double sps = 60.0 / proj.Bpm / 4 * SR;   // samples per 16th
        if (playing && SongMode && proj.Automation.Count > 0) { Automate(proj, sps); sps = 60.0 / proj.Bpm / 4 * SR; }   // a tempo lane may have just changed the BPM
        int msg; while (MidiIn.TryDequeue(out msg)) MidiEvent(proj, msg, playing, sps);
        int n2 = frames * 2;
        double blockPos = step == 0 ? 0 : step - 1 + Math.Max(0, Math.Min(1, (sps - toNext) / sps));   // where the block starts, in 16ths of the song or pattern
        for (int i = 0; i < ib.Length; i++)
            if (ib[i] == null || ib[i].Length < n2) ib[i] = new float[n2]; else Array.Clear(ib[i], 0, n2);
        if (clickBuf == null || clickBuf.Length < n2) clickBuf = new float[n2]; else Array.Clear(clickBuf, 0, n2);
        for (int f = 0; f < frames; f++)
        {
            if (playing && countLeft > 0)   // count-in: a bar of beats, accent first, then the song starts on the next 16th
            {
                if (toNext <= 0) { if (countLeft % 4 == 0) Click(countLeft == Project.Steps); countLeft--; toNext += sps; }
                toNext--;
            }
            else if (playing)
            {
                now = rendered + f;
                if (toNext <= 0 && step < StepLimit) { Trigger(proj, step++, sps); toNext += sps; }
                else if (toNext <= 0 && songEnd < 0) songEnd = rendered + f;   // export: the moment the step after the last would have started
                toNext--;
            }
            now = rendered + f;
            if (arps.Count > 0) RunArps(sps);
            if (clickAge >= 0)   // a short sine blip: 1.6 kHz on the bar, 1 kHz on other beats
            {
                double ct = clickAge++ / (double)SR;
                float c = (float)(Math.Sin(2 * Math.PI * clickHz * ct) * 0.45 * Math.Exp(-ct * 70));
                clickBuf[2 * f] += c; clickBuf[2 * f + 1] += c;
                if (ct > 0.08) clickAge = -1;
            }
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var v = voices[i];
                if (v.Wait > 0) { v.Wait--; continue; }
                float s = Voice1(v);
                if (v.Done) { voices.RemoveAt(i); continue; }
                if (v.Ch.Mute) continue;
                float g = v.Ch.Vol * v.Gain, pan = Math.Max(-1, Math.Min(1, v.Ch.Pan + v.NotePan));
                var bus = ib[(uint)v.Ch.Insert < (uint)ib.Length ? v.Ch.Insert : 0];
                bus[2 * f] += s * g * Math.Min(1, 1 - pan); bus[2 * f + 1] += (v.Stereo ? v.Right : s) * g * Math.Min(1, 1 + pan);   // pan acts as balance on stereo
            }
        }
        var master = ib[0];
        blockSps = sps; blockPos0 = blockPos; blockPlaying = playing;   // tempo-synced effects (Beat FX) follow the song
        int[] order; lock (proj) order = proj.ProcessOrder();
        foreach (int i in order)   // each insert after everything that feeds it
        {
            var ins = proj.Mixer[i]; int sc = ins.Sidechain;   // read once: the UI may change it mid-block
            Strip(ins, ib[i], n2, i, sc > 0 && sc < ib.Length && sc != i ? ib[sc] : null);   // ib[sc] is already that insert's post-fader output
            if (ins.ToMaster) for (int k = 0; k < n2; k++) master[k] += ib[i][k];
            var sends = ins.Sends;
            for (int r = 0; r < sends.Count; r++) { int to = sends[r].To; float lvl = sends[r].Level; if (to >= 0 && to < ib.Length && to != i) for (int k = 0; k < n2; k++) ib[to][k] += ib[i][k] * lvl; }
        }
        Strip(proj.Mixer[0], master, n2, 0, null);
        if (Tap > 0) Array.Copy(ib[Tap], lr, n2);   // a stem: the insert as it leaves for the master, unclipped
        else for (int k = 0; k < n2; k++) lr[k] = (float)Math.Tanh(master[k] + clickBuf[k]);   // soft clip instead of hard wrap-around; the click skips the master fader
        rendered += frames;
    }

    // Offline bounce, like FL's File > Export: the whole song (or one pattern), then the reverb/delay tail until it goes quiet.
    // Pass a Project.ExportCopy() so the live engine's effect state isn't shared.
    public static float[] Bounce(Project proj, bool song, int pattern) { float[] ever; return Bounce(proj, song, pattern, out ever); }

    public static float[] Bounce(Project proj, bool song, int pattern, out float[] everPeak)   // everPeak: how loud each insert got
    {
        int steps = song ? proj.SongBars() * Project.Steps : proj.Patterns[Math.Min(pattern, proj.Patterns.Count - 1)].Length;
        var e = new Engine { Proj = proj, Playing = true, SongMode = song, CurPattern = pattern, StepLimit = steps };
        everPeak = e.EverPeak;
        int maxTail = 10 * SR, maxSong = 10 * 60 * SR;   // the slowest possible song (32 bars at 40 BPM) takes 3.2 minutes
        int quietNeeded = (int)(1.2 * SR);   // longer than the 1 s max delay time, so a gap before an echo isn't taken for the end
        const float Silent = 1e-4f;          // -80 dBFS, below 16-bit's last few bits
        var outp = new List<float>(); var buf = new float[Frames * 2];
        int quiet = 0;
        while (outp.Count / 2 < (e.songEnd < 0 ? maxSong : e.songEnd + maxTail))   // the engine marks the song's end, which a tempo lane can move
        {
            e.Render(buf, Frames);
            quiet = buf.All(x => Math.Abs(x) < Silent) ? quiet + Frames : 0;
            outp.AddRange(buf);
            if (e.songEnd >= 0 && quiet >= quietNeeded) break;
        }
        int body = (int)Math.Max(0, e.songEnd);
        int last = outp.FindLastIndex(x => Math.Abs(x) >= Silent) / 2 + 1;   // cut the silent end, but keep the full song length
        return outp.Take(2 * Math.Max(body, last)).ToArray();
    }

    // One insert's output (post-fader, as it goes to the master) over exactly `frames` frames: a stem lined up with the master export.
    // Rendered on its own pass so only one stem is ever in memory. Pass a fresh Project.ExportCopy().
    public static float[] BounceStem(Project proj, bool song, int pattern, int insert, int frames)
    {
        int steps = song ? proj.SongBars() * Project.Steps : proj.Patterns[Math.Min(pattern, proj.Patterns.Count - 1)].Length;
        var e = new Engine { Proj = proj, Playing = true, SongMode = song, CurPattern = pattern, StepLimit = steps, Tap = insert };
        var outp = new float[frames * 2]; var buf = new float[Frames * 2];
        for (int at = 0; at < frames; at += Frames) { e.Render(buf, Frames); Array.Copy(buf, 0, outp, at * 2, Math.Min(Frames, frames - at) * 2); }
        return outp;
    }

    double blockSps, blockPos0; bool blockPlaying;   // this block's tempo and song position, for tempo-synced effects

    void Strip(Insert ins, float[] b, int n2, int idx, float[] key)
    {
        foreach (var fx in ins.Slots) if (fx != null && !fx.Bypass) { fx.Sps = blockSps; fx.Pos = blockPos0; fx.Playing = blockPlaying; fx.Run(b, n2, key, ref dryBuf); }
        float g = ins.Mute ? 0 : ins.Vol, gl = g * Math.Min(1, 1 - ins.Pan), gr = g * Math.Min(1, 1 + ins.Pan), pk = 0;
        for (int k = 0; k < n2; k += 2)
        {
            b[k] *= gl; b[k + 1] *= gr;
            pk = Math.Max(pk, Math.Max(Math.Abs(b[k]), Math.Abs(b[k + 1])));
        }
        Peaks[idx] = Math.Max(pk, Peaks[idx] * 0.85f);   // meter falls back gradually
        EverPeak[idx] = Math.Max(EverPeak[idx], pk);
    }

    // Set automated controls from their lanes at the current song position, once per block (~23 ms),
    // interpolating between 16th-note values so sweeps and fades move smoothly.
    void Automate(Project proj, double sps)
    {
        lock (proj)
        {
            double frac = step == 0 ? 0 : Math.Max(0, Math.Min(1, (sps - toNext) / sps)), pos = Math.Max(0, lastAbs) + frac;   // how far we are into the current 16th
            for (int k = 0; k < proj.Automation.Count; k++) { var v = proj.LaneValue(k, pos); if (v.HasValue) proj.Automation[k].Set(proj, v.Value); }
        }
    }

    // Keyboard note-on starts a voice that holds until its note-off. The sustain pedal holds note-offs back until it comes up,
    // and the pitch wheel bends every live note by up to ±BendRange semitones. Everything else (other controllers, clock) is ignored.
    void MidiEvent(Project proj, int msg, bool playing, double sps)
    {
        int status = msg & 0xF0, key = (msg >> 8) & 0x7F, vel = (msg >> 16) & 0x7F;
        if (status == 0xE0)   // pitch wheel: 14 bits, 8192 = centre
        {
            bendMul = Math.Pow(2, ((key | vel << 7) - 8192) / 8192.0 * BendRange / 12);
            return;
        }
        if (status == 0xB0)   // a controller: MIDI learn takes it; linked controls follow it
        {
            var learn = LearnTarget;
            if (learn != null) { LearnTarget = null; Learned.Enqueue(key); return; }
            lock (proj) foreach (var lk in proj.Links) if (lk.Cc == key) lk.Target.Set(proj, vel / 127f);
        }
        if (status == 0xB0 && key == 1) { modDepth = vel / 127.0; return; }   // controller 1 = mod wheel: vibrato depth
        if (status == 0xB0 && key == 64)   // controller 64 = sustain pedal: 64..127 down, 0..63 up
        {
            pedal = vel >= 64;
            if (!pedal)   // let go: every key released meanwhile gets its note-off now (it also ends that note when recording)
                for (int k = 0; k < 128; k++) if (sustained[k]) { sustained[k] = false; MidiEvent(proj, 0x80 | k << 8, playing, sps); }
            return;
        }
        bool on = status == 0x90 && vel > 0;
        if (!on && status != 0x80 && status != 0x90) return;   // note-on with velocity 0 means note-off
        if (!on && pedal) { sustained[key] = true; return; }   // pedal down: keep sounding
        sustained[key] = false;
        if (held[key] != null) { held[key].Len = held[key].Age; held[key] = null; }   // release: the synth fades out from here
        foreach (var a in arps.Values) foreach (var h in a.Notes) if (h.Live && h.Key == key && h.End == long.MaxValue) h.End = now;   // ... or leaves the arpeggio
        var ch = MidiTarget;
        if (on && ch != null && ch.ArpDir != 0) ArpOf(ch).Notes.Add(new Held { Key = key, Vel = vel, End = long.MaxValue, Live = true });
        else if (on && ch != null) { held[key] = new Voice { Ch = ch, Key = key, Len = int.MaxValue / 2, Gain = vel / 100f, Live = true }; AddVoice(held[key]); }
        if (playing && !SongMode && countLeft == 0)   // tell the UI where in the pattern the player heard this, for recording (not during the count-in)
        {
            double pos = step == 0 ? 0 : step - 1 + Math.Max(0, Math.Min(1, (sps - toNext) / sps));
            pos -= (NBuf + 0.5) * Frames / sps;   // audio is heard about NBuf blocks after it is rendered (+ half a block of event wait)
            int len; lock (proj) len = proj.Patterns[Math.Min(CurPattern, proj.Patterns.Count - 1)].Length;
            MidiRec.Enqueue(new KeyValuePair<int, double>(msg, (pos % len + len) % len));
        }
    }

    void AddVoice(Voice v) { if (voices.Count >= MaxVoices) voices.RemoveAt(0); voices.Add(v); }

    void Click(bool bar) { clickAge = 0; clickHz = bar ? 1600 : 1000; }

    // A new note. With glide (portamento) on, the channel plays one note at a time: the one sounding is let go, and the new one
    // starts at its pitch and slides to its own.
    void Start(Voice v)
    {
        int glide = v.Ch.GlideSamples();
        if (glide > 0)
        {
            Voice prev = null;
            foreach (var o in voices) if (o.Ch == v.Ch && !o.Done && o.Age <= o.Len) prev = o;   // the newest one still held
            if (prev != null)
            {
                v.ShiftFrom = v.Shift = prev.Key + prev.Shift - v.Key; v.ShiftTo = 0; v.GlideLen = glide; v.GlideAge = -v.Wait;
                foreach (var o in voices) if (o.Ch == v.Ch && o.Age <= o.Len) o.Len = o.Age + v.Wait;   // release the others as this one starts
            }
        }
        AddVoice(v);
    }

    void Bend(Channel c, int key, int delay, int samples)   // slide note: every held note on the channel slides to `key` over `samples`
    {
        foreach (var v in voices)
            if (v.Ch == c && !v.Done && v.Age <= v.Len)
            {
                v.ShiftFrom = v.Shift; v.ShiftTo = key - v.Key; v.GlideLen = Math.Max(1, samples); v.GlideAge = delay;
                v.Len = Math.Max(v.Len, v.Age - delay + samples);   // keep sounding at least to the slide's end
            }
    }

    void RunArps(double sps)   // once per sample: drop notes that ended, and start the next arpeggio note when its time comes
    {
        foreach (var kv in arps)
        {
            Channel c = kv.Key; var a = kv.Value; var held = a.Notes;
            for (int i = held.Count - 1; i >= 0; i--) if (held[i].End <= now) held.RemoveAt(i);
            if (held.Count == 0 || c.ArpDir == 0) { a.Index = 0; a.Wait = 0; continue; }   // the next chord starts its arpeggio at once
            if (a.Wait <= 0)
            {
                var order = held.OrderBy(x => x.Key).ToList(); int n = order.Count * Math.Max(1, c.ArpRange), k;   // the chord's notes, repeated an octave higher per extra octave of range
                if (c.ArpDir == 1) k = a.Index % n;
                else if (c.ArpDir == 2) k = n - 1 - a.Index % n;
                else if (c.ArpDir == 3) { int period = Math.Max(1, 2 * n - 2); k = a.Index % period; if (k >= n) k = period - k; }
                else k = (int)((Noise() * 0.5 + 0.5) * n) % n;
                a.Index++;
                var h = order[k % order.Count]; double len = Channel.ArpTimes[Math.Max(0, Math.Min(Channel.ArpTimes.Length - 1, c.ArpTime))] * sps;
                if (!c.Mute) AddVoice(new Voice { Ch = c, Key = Math.Min(127, h.Key + 12 * (k / order.Count)), Len = Math.Max(1, (int)(len * c.ArpGate)), Gain = h.Vel / 100f,
                                                  NotePan = h.Pan, Detune = Math.Pow(2, h.Fine / 1200.0), Live = h.Live });
                a.Wait += len;
            }
            a.Wait--;
        }
    }

    void Trigger(Project proj, int st, double sps)
    {
        lock (proj)
        {
            var hits = new List<KeyValuePair<Pattern, int>>();   // each pattern sounding now, and the step within it
            if (SongMode)
            {
                int songLen = proj.SongBars() * Project.Steps, loopLen = (int)(proj.LoopTo - proj.LoopFrom);
                int abs = loopLen > 0 && StepLimit == int.MaxValue ? (int)proj.LoopFrom + st % loopLen : st % songLen;   // the loop region, while playing live (exports play the whole song)
                PlayStep = lastAbs = abs;
                hits = proj.SoundingAt(abs);
            }
            else
            {
                var cur = proj.Patterns[Math.Min(CurPattern, proj.Patterns.Count - 1)];
                PlayStep = st % cur.Length;
                hits.Add(new KeyValuePair<Pattern, int>(cur, st % cur.Length));
            }
            int barNo, beats;
            if (Metronome && PlayStep % 4 == 0) Click(SongMode ? proj.BarAt(PlayStep, out barNo, out beats) : PlayStep % Project.Steps == 0);   // every beat; the first of each bar higher
            foreach (var hit in hits)
                for (int ch = 0, s = hit.Value; ch < Math.Min(hit.Key.Notes.Count, proj.Channels.Count); ch++)
                {
                    var c = proj.Channels[ch];
                    if (c.Mute) continue;
                    foreach (var n in hit.Key.Notes[ch])
                    {
                        if (n.Step < s || n.Step >= s + 1) continue;   // notes starting within this 16th; an off-grid one waits for its moment
                        int wait = (int)Math.Round((n.Step - s) * sps) + (s % 2 == 1 ? (int)Math.Round(proj.Swing * 0.5 * sps) : 0);   // swing delays off-beat 16ths
                        if (n.Slide) { Bend(c, n.Key, -wait, (int)(n.Len * sps)); continue; }   // a slide note bends what's sounding; it starts nothing
                        if (c.ArpDir != 0) ArpOf(c).Notes.Add(new Held { Key = n.Key, Vel = n.Vel, Pan = n.Pan, Fine = n.Fine, End = now + wait + (long)(n.Len * sps) });   // the arpeggiator plays it
                        else Start(new Voice { Ch = c, Key = n.Key, Len = Math.Max(1, (int)(n.Len * sps)), Gain = n.Vel / 100f, NotePan = n.Pan, Detune = Math.Pow(2, n.Fine / 1200.0), Wait = wait });
                    }
                }
        }
    }

    static double Hz(int key) { return 440 * Math.Pow(2, (key - 69) / 12.0); }   // MIDI 69 = A, 440 Hz
    static double Wrap(double phase) { return phase >= 1 ? phase - 1 : phase; }    // oscillator phase in 0..1 (rates stay below 1)

    static double Env(Voice v, int attack, int release)   // rise over attack samples, hold for the note, fade over release samples; ends the voice
    {
        double e = Math.Min(1, v.Age / (double)attack);
        if (v.Age > v.Len) { e *= Math.Max(0, 1 - (v.Age - v.Len) / (double)release); if (v.Age >= v.Len + release) v.Done = true; }
        return e;
    }

    double Noise() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng / 2147483648.0 - 1; }

    float Voice1(Voice v)
    {
        double t = v.Age / (double)SR, br = 1;   // br scales every oscillator's speed: pitch wheel and vibrato, for keyboard notes only
        if (v.Live) br = modDepth == 0 ? bendMul : bendMul * Math.Exp(modDepth * VibSemis * Math.Sin(2 * Math.PI * VibHz * t) * Math.Log(2) / 12);
        if (v.GlideLen > 0)   // a slide or glide in progress
        {
            if (v.GlideAge >= 0) v.Shift = v.GlideAge >= v.GlideLen ? v.ShiftTo : v.ShiftFrom + (v.ShiftTo - v.ShiftFrom) * v.GlideAge / v.GlideLen;
            if (v.GlideAge++ >= v.GlideLen) v.GlideLen = 0;
        }
        if (v.Shift != 0) br *= Math.Pow(2, v.Shift / 12);
        v.Age++;
        var c = v.Ch; double b = c.Bright();   // tone knob: filter brightness for synths, decay length for drums (1 = built-in)
        switch (c.Kind)
        {
            case "kick":
            {
                if (v.Rate == 0) v.Rate = Math.Pow(2, c.Semitones() / 12.0) * v.Detune;   // pitch knob as a frequency factor
                v.Phase += 2 * Math.PI * (45 + 110 * Math.Exp(-t * 28)) * v.Rate / SR;   // pitch sweep 155 -> 45 Hz
                if (t > 0.5 * b) v.Done = true;
                return (float)(Math.Sin(v.Phase) * Math.Exp(-t * 8 / b));
            }
            case "snare":
            {
                if (v.Rate == 0) v.Rate = Math.Pow(2, c.Semitones() / 12.0) * v.Detune;
                double n = Noise(), hp = n - v.Z1; v.Z1 = n;   // first difference = crude high-pass
                if (t > 0.3 * b) v.Done = true;
                return (float)(hp * 0.35 * Math.Exp(-t * 18 / b) + Math.Sin(2 * Math.PI * 190 * v.Rate * t) * 0.5 * Math.Exp(-t * 30 / b));
            }
            case "hat":
            {
                double n = Noise(), hp = n - 2 * v.Z1 + v.Z2; v.Z2 = v.Z1; v.Z1 = n;   // second difference = steeper high-pass
                if (t > 0.1 * b) v.Done = true;
                return (float)(hp * 0.2 * Math.Exp(-t * 55 / b));
            }
            case "clap":   // three quick noise bursts 10 ms apart, then a short tail
            {
                double n = Noise(), hp = n - v.Z1; v.Z1 = n;
                if (t > 0.35 * b) v.Done = true;
                return (float)(hp * 0.3 * (t < 0.03 ? Math.Exp(-(t % 0.01) * 300) : Math.Exp(-(t - 0.03) * 25 / b)));
            }
            case "synth":   // saw through a one-pole low-pass
            {
                if (v.Rate == 0) v.Rate = Hz(v.Key + c.Semitones()) * v.Detune / SR;
                v.Phase = Wrap(v.Phase + v.Rate * br);
                v.Z1 += Math.Min(1, 0.15 * b) * ((2 * v.Phase - 1) - v.Z1);
                return (float)(v.Z1 * 0.35 * Env(v, c.AttackSamples(), c.ReleaseSamples()));
            }
            case "square":   // hollow square lead
            {
                if (v.Rate == 0) v.Rate = Hz(v.Key + c.Semitones()) * v.Detune / SR;
                v.Phase = Wrap(v.Phase + v.Rate * br);
                v.Z1 += Math.Min(1, 0.25 * b) * ((v.Phase < 0.5 ? 1 : -1) - v.Z1);
                return (float)(v.Z1 * 0.25 * Env(v, c.AttackSamples(), c.ReleaseSamples()));
            }
            case "pad":   // three saws detuned ±0.7%, slow to swell and to fade
            {
                if (v.Rate == 0) v.Rate = Hz(v.Key + c.Semitones()) * v.Detune / SR;
                v.Phase = Wrap(v.Phase + v.Rate * br); v.Pos = Wrap(v.Pos + v.Rate * br * 1.007); v.Z2 = Wrap(v.Z2 + v.Rate * br * 0.993);
                v.Z1 += Math.Min(1, 0.06 * b) * ((v.Phase + v.Pos + v.Z2) * 2 / 3 - 1 - v.Z1);
                return (float)(v.Z1 * 0.45 * Env(v, c.AttackSamples(), c.ReleaseSamples()));
            }
            case "bass":   // filtered square plus a sine an octave below
            {
                if (v.Rate == 0) v.Rate = Hz(v.Key + c.Semitones()) * v.Detune / SR;
                v.Phase = Wrap(v.Phase + v.Rate * br); v.Pos = Wrap(v.Pos + v.Rate * br / 2);
                v.Z1 += Math.Min(1, 0.12 * b) * ((v.Phase < 0.5 ? 1 : -1) - v.Z1);
                return (float)((v.Z1 * 0.2 + Math.Sin(2 * Math.PI * v.Pos) * 0.3) * Env(v, c.AttackSamples(), c.ReleaseSamples()));
            }
            case "keys":   // two-operator FM: a sine bent by itself, the bend fading fast, like an electric piano
            {
                if (v.Rate == 0) v.Rate = Hz(v.Key + c.Semitones()) * v.Detune / SR;
                v.Phase = Wrap(v.Phase + v.Rate * br);
                double bend = 2.2 * b * Math.Exp(-t * 4);
                return (float)(Math.Sin(2 * Math.PI * v.Phase + bend * Math.Sin(2 * Math.PI * v.Phase)) * 0.3 * Math.Exp(-t * 1.5) * Env(v, c.AttackSamples(), c.ReleaseSamples()));
            }
            case "pluck":   // Karplus-Strong: a burst of noise circulating in a one-period delay, softened on every pass
            {
                if (v.Buf == null)
                {
                    v.Buf = new float[Math.Max(2, (int)Math.Round(SR / (Hz(v.Key + c.Semitones()) * v.Detune)))];
                    for (int i = 0; i < v.Buf.Length; i++) v.Buf[i] = (float)Noise();
                }
                int p = (int)v.Pos, q = (p + 1) % v.Buf.Length;
                float o = v.Buf[p];
                double s = Math.Min(0.5, 0.5 / b), keep = 1 - 0.004 / Math.Min(1, b);   // brighter: less smoothing; darker: more loss per pass
                v.Buf[p] = (float)((v.Buf[p] * (1 - s) + v.Buf[q] * s) * keep);   // built-in: average with the next sample and lose 0.4%
                v.Pos = q;
                return (float)(o * 0.35 * Env(v, c.AttackSamples(), c.ReleaseSamples()));
            }
            case "sample": return Sampler(v, c, br);
            case "analog": return Analog(v, c, br, t);
        }
        v.Done = true;
        return 0;
    }

    // ---- sampler: one-shot by default; loop, reverse, time-stretch and slices on request
    static float Read(float[] a, double i) { int k = (int)i; if (k < 0 || k + 1 >= a.Length) return 0; return (float)(a[k] + (a[k + 1] - a[k]) * (i - k)); }

    float Sampler(Voice v, Channel c, double br)   // runs per sample: no allocations
    {
        var smp = c.Sample;
        if (smp == null) { v.Done = true; return 0; }
        int start = 0, end = smp.Length; var sl = c.Slices;
        if (sl != null)   // a slice: C5 plays the first, C#5 the second…
        {
            int si = v.Key - 60;
            if (si < 0 || si >= sl.Length) { v.Done = true; return 0; }
            start = Math.Min(sl[si], smp.Length - 1); end = si + 1 < sl.Length ? Math.Min(sl[si + 1], smp.Length) : smp.Length;
        }
        if (v.Rate == 0) v.Rate = c.SampleRate / (double)SR * Math.Pow(2, ((sl != null ? 0 : v.Key - 60) + c.Semitones()) / 12.0) * v.Detune;   // key 60 (C5) = original pitch
        double pitch = v.Rate * br, len = end - start, lo = c.Loop ? c.LoopStart * len : 0, hi = c.Loop ? Math.Max(lo + 64, c.LoopEnd * len) : len;
        if (c.Loop && v.Age > v.Len + SR / 30) { v.Done = true; return 0; }   // a looped note fades out over 30 ms after it ends
        double gain = c.Loop && v.Age > v.Len ? 1 - (v.Age - v.Len) / (SR / 30.0) : 1;
        var right = c.SampleR; v.Stereo = right != null;
        float l, r;
        if (!c.Loop && v.Pos >= len - 1) { v.Done = true; return 0; }
        if (Math.Abs(c.Stretch - 1) < 1e-3)   // plain playback
        {
            l = SampleAt(smp, c, v.Pos, start, end, len, lo, hi); r = v.Stereo ? SampleAt(right, c, v.Pos, start, end, len, lo, hi) : l;
            v.Pos += pitch;
        }
        else   // time-stretch: two overlapping 50 ms grains read at the note's pitch, while where they start moves Stretch times slower
        {
            const int G = SR / 20;
            l = r = 0;
            for (int j = 0; j < 2; j++)
            {
                int age = (v.Age + j * G / 2) % G;
                if (age == 0) { if (j == 0) v.Z1 = v.Pos; else v.Z2 = v.Pos; }   // a new grain starts where the stretched timeline is now
                double from = j == 0 ? v.Z1 : v.Z2, w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * age / G), x = from + age * pitch;
                float sl2 = SampleAt(smp, c, x, start, end, len, lo, hi);
                l += (float)(sl2 * w); r += (float)((v.Stereo ? SampleAt(right, c, x, start, end, len, lo, hi) : sl2) * w);
            }
            v.Pos += pitch / c.Stretch;
        }
        v.Right = (float)(r * gain);
        return (float)(l * gain);
    }

    static float SampleAt(float[] a, Channel c, double x, int start, int end, double len, double lo, double hi)   // x along the (slice of the) sample: looped, reversed
    {
        if (c.Loop && x >= hi) x = lo + (x - lo) % (hi - lo);
        if (x < 0 || x >= len - 1) return 0;
        return Read(a, c.Reverse ? end - 1 - x : start + x);
    }

    // ---- analog synth: 3 oscillators (with unison, spread left and right), a state-variable filter swept by its own envelope,
    // an amp envelope and an LFO. Saw and square are band-limited with PolyBLEP so high notes don't alias. No allocations per sample.
    static double Blep(double t, double dt) { if (t < dt) { t /= dt; return t + t - t * t - 1; } if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; } return 0; }
    double Osc(int wave, double ph, double dt)
    {
        switch (wave)
        {
            case 0: return Math.Sin(2 * Math.PI * ph);
            case 1: return 2 * ph - 1 - Blep(ph, dt);
            case 2: return (ph < 0.5 ? 1 : -1) + Blep(ph, dt) - Blep((ph + 0.5) % 1, dt);
            case 3: return 1 - 4 * Math.Abs(ph - 0.5);
            default: return Noise();
        }
    }
    static double Adsr(int age, int len, double a, double d, double sus, double rel, out bool done)   // 0..1; the release fades from wherever the note ended
    {
        double ta = age / (double)SR, tl = len / (double)SR; done = false;
        if (ta <= tl) return EnvHeld(ta, a, d, sus);
        double r = (ta - tl) / rel; if (r >= 1) { done = true; return 0; }
        return EnvHeld(tl, a, d, sus) * (1 - r);
    }
    static double EnvHeld(double t, double a, double d, double sus) { return t < a ? t / a : t < a + d ? 1 - (1 - sus) * (t - a) / d : sus; }

    float Analog(Voice v, Channel c, double br, double t)
    {
        var sy = c.Syn ?? Channel.SynPresets[0];
        int uni = 1 + (int)Math.Round(sy[25] * 6);
        if (v.Ph == null || v.Ph.Length != 3 * uni) { v.Ph = new double[3 * uni]; for (int k = 0; k < v.Ph.Length; k++) v.Ph[k] = (k * 0.37) % 1; v.Rate = Hz(v.Key + c.Semitones()) * v.Detune; }   // once per note
        bool done, fdone;
        double amp = Adsr(v.Age, v.Len, Channel.EnvSec(sy[14]), Channel.EnvSec(sy[15]), sy[16], Channel.EnvSec(sy[17]), out done);
        if (done) { v.Done = true; return 0; }
        double fenv = Adsr(v.Age, v.Len, Channel.EnvSec(sy[18]), Channel.EnvSec(sy[19]), sy[20], Channel.EnvSec(sy[21]), out fdone);
        double lfo = Math.Sin(2 * Math.PI * 0.05 * Math.Pow(200, sy[22]) * t) * sy[23]; int lt = Math.Min(3, (int)(sy[24] * 4));
        double pitch = v.Rate * br * (lt == 0 ? Math.Pow(2, lfo / 12) : 1);   // LFO on pitch: up to a semitone either way
        double l = 0, r = 0;
        for (int o = 0; o < 3; o++)
        {
            float lvl = o == 0 ? sy[2] : o == 1 ? sy[6] : sy[9];
            if (lvl <= 0) continue;
            int wav = Channel.Wave(o == 0 ? sy[0] : o == 1 ? sy[3] : sy[7]);
            double mult = o == 0 ? Math.Pow(2, Channel.Oct(sy[1])) : o == 1 ? Math.Pow(2, (Channel.Semis(sy[4]) + (sy[5] - 0.5)) / 12) : Math.Pow(2, Channel.Oct(sy[8]));
            for (int u = 0; u < uni; u++)
            {
                double spread = uni == 1 ? 0 : (u / (double)(uni - 1) * 2 - 1) * sy[26] * 0.5;   // unison: up to ±25 cents; alternate voices lean left and right
                double dt = pitch * mult * Math.Pow(2, spread / 12) / SR; int k = o * uni + u;
                v.Ph[k] += dt; if (v.Ph[k] >= 1) v.Ph[k] -= Math.Floor(v.Ph[k]);
                double x = Osc(wav, v.Ph[k], dt) * lvl;
                if (uni == 1) { l += x; r += x; } else if (u % 2 == 0) { l += x * 1.4; r += x * 0.6; } else { l += x * 0.6; r += x * 1.4; }
            }
        }
        l /= uni; r /= uni;
        double cut = Channel.CutHz(sy[11]) * Math.Pow(2, (sy[13] - 0.5) * 2 * 6 * fenv + (lt == 1 ? lfo * 2 : 0));   // filter envelope: up to ±6 octaves; LFO: ±2
        double g = Math.Tan(Math.PI * Math.Min(cut, 0.45 * SR) / SR), kq = 2 - 1.95 * sy[12], a1 = 1 / (1 + g * (g + kq)), a2 = g * a1, a3 = g * a2;
        int ft = Math.Min(2, (int)(sy[10] * 3));
        double v3 = l - v.Z2, v1 = a1 * v.Z1 + a2 * v3, v2 = v.Z2 + a2 * v.Z1 + a3 * v3;   // TPT state-variable filter, left (Z1, Z2) and right (Z3, Pos)
        v.Z1 = 2 * v1 - v.Z1; v.Z2 = 2 * v2 - v.Z2; l = ft == 0 ? v2 : ft == 1 ? v1 : l - kq * v1 - v2;
        v3 = r - v.Pos; v1 = a1 * v.Z3 + a2 * v3; v2 = v.Pos + a2 * v.Z3 + a3 * v3;
        v.Z3 = 2 * v1 - v.Z3; v.Pos = 2 * v2 - v.Pos; r = ft == 0 ? v2 : ft == 1 ? v1 : r - kq * v1 - v2;
        double vol = amp * sy[27] * 0.5 * (lt == 2 ? 1 - Math.Abs(lfo) * 0.5 : 1), pan = lt == 3 ? lfo : 0;
        v.Stereo = true; v.Right = (float)(r * vol * Math.Min(1, 1 + pan));
        return (float)(l * vol * Math.Min(1, 1 - pan));
    }
    public string Start()
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = SR, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = SR * 4 };
        int err = waveOutOpen(out hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);   // -1 = WAVE_MAPPER (default device)
        if (err != 0) return "Could not open the audio device (waveOutOpen error " + err + ").";
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "audio" };
        thread.Start();
        return null;
    }

    public void Stop()
    {
        if (thread == null) return;
        running = false; thread.Join(1000); thread = null;
    }

    void Loop()
    {
        timeBeginPeriod(1);
        int hsz = Marshal.SizeOf(typeof(WAVEHDR)), flagsOff = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
        var hdrs = new IntPtr[NBuf]; var data = new IntPtr[NBuf];
        var lr = new float[Frames * 2]; var pcm = new short[Frames * 2];
        for (int i = 0; i < NBuf; i++)
        {
            data[i] = Marshal.AllocHGlobal(Frames * 4); hdrs[i] = Marshal.AllocHGlobal(hsz);
            Marshal.StructureToPtr(new WAVEHDR { lpData = data[i], dwBufferLength = Frames * 4 }, hdrs[i], false);
            waveOutPrepareHeader(hwo, hdrs[i], hsz);
            Marshal.WriteInt32(hdrs[i], flagsOff, Marshal.ReadInt32(hdrs[i], flagsOff) | 1);   // mark WHDR_DONE so it gets filled first
        }
        while (running)
        {
            bool wrote = false;
            for (int i = 0; i < NBuf; i++)
            {
                int fl = Marshal.ReadInt32(hdrs[i], flagsOff);
                if ((fl & 1) == 0) continue;   // still queued in the driver
                try { Render(lr, Frames); }
                catch (Exception e) { LastError = e.Message; Array.Clear(lr, 0, lr.Length); }   // keep the app alive; UI shows the error
                for (int k = 0; k < pcm.Length; k++) pcm[k] = (short)(lr[k] * 32767);
                Marshal.Copy(pcm, 0, data[i], pcm.Length);
                Marshal.WriteInt32(hdrs[i], flagsOff, fl & ~1);
                waveOutWrite(hwo, hdrs[i], hsz);
                wrote = true;
            }
            if (!wrote) Thread.Sleep(2);
        }
        waveOutReset(hwo);
        for (int i = 0; i < NBuf; i++) { waveOutUnprepareHeader(hwo, hdrs[i], hsz); Marshal.FreeHGlobal(hdrs[i]); Marshal.FreeHGlobal(data[i]); }
        waveOutClose(hwo);
        timeEndPeriod(1);
    }
}

// The app's logo: a banana, drawn in code so it's sharp at every size (window, taskbar, and the .exe via --logo + /win32icon).
static class Logo
{
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var body = new System.Drawing.Drawing2D.GraphicsPath())
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 100f, size / 100f);   // drawn on a 100 x 100 grid
            body.AddBezier(8, 46, 10, 98, 80, 100, 86, 26);   // the outer (lower) curve, tip to stem
            body.AddBezier(86, 26, 76, 62, 32, 66, 8, 46);    // the inner (upper) curve, back to the tip: a plump middle
            body.CloseFigure();
            using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0, 34), new PointF(0, 94), Color.FromArgb(255, 238, 120), Color.FromArgb(236, 176, 28)))
                g.FillPath(fill, body);
            g.SetClip(body);   // keep the shine inside the peel
            using (var shine = new Pen(Color.FromArgb(150, 255, 255, 228), 6) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawBezier(shine, 20, 66, 32, 82, 58, 84, 72, 68);   // light along the belly
            g.ResetClip();
            using (var edge = new Pen(Color.FromArgb(150, 98, 18), Math.Max(4f, 100f / size)))   // at least one real pixel wide at 16 px
                g.DrawPath(edge, body);
            using (var stem = new Pen(Color.FromArgb(104, 72, 26), 9) { StartCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawLine(stem, 87, 26, 92, 10);
            using (var tip = new SolidBrush(Color.FromArgb(70, 44, 14))) g.FillEllipse(tip, 3, 42, 9, 9);
        }
        return bmp;
    }

    public static Icon Icon() { using (var b = Draw(32)) return System.Drawing.Icon.FromHandle(b.GetHicon()); }

    public static byte[] Ico()   // a Windows .ico with the banana at 16, 24, 32, 48 and 256 px (PNG-compressed entries, Vista and later)
    {
        int[] sizes = { 16, 24, 32, 48, 256 };
        var pngs = sizes.Select(n => { using (var b = Draw(n)) using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); } }).ToList();
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);   // ICONDIR: reserved, type 1 = icon, count
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)   // ICONDIRENTRY: width, height (0 = 256), colours, reserved, planes, bits, bytes, offset
        {
            w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        return m.ToArray();
    }
}

// Recently opened or saved projects, newest first, kept per Windows user in %APPDATA%\MiniDAW\recent.txt.
static class Recent
{
    public const int Max = 8;
    public static string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniDAW", "recent.txt");

    public static List<string> Load()
    {
        try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Trim() != "").Distinct(StringComparer.OrdinalIgnoreCase).Take(Max).ToList() : new List<string>(); }
        catch (Exception) { return new List<string>(); }   // an unreadable list just means none
    }

    public static void Add(string path)   // to the top; an existing entry moves up instead of repeating
    {
        var list = Load(); string full = Path.GetFullPath(path);
        list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, full);
        Write(list);
    }

    public static void Remove(string path) { var list = Load(); list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); Write(list); }

    static void Write(List<string> list)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllLines(FilePath, list.Take(Max)); }
        catch (Exception) { }   // a read-only profile only costs the recent list, never the project
    }
}

// The start-up splash (splash = true) and the About window (F1, or the banana at the end of the toolbar).
class AboutForm : Form
{
    public const string Version = "1.1";   // bump for every release: running copies compare it with the latest GitHub release
    static readonly Color Bg = MainForm.PanelBg, Dim = MainForm.Dim;
    readonly bool splash;
    readonly Font big = new Font("Segoe UI", 26, FontStyle.Bold), text = new Font("Segoe UI", 9.5f);
    public readonly Panel view = new Canvas();

    public AboutForm(bool splash)
    {
        this.splash = splash;
        Text = "About MiniDAW"; Icon = Logo.Icon(); BackColor = view.BackColor = Bg;
        FormBorderStyle = splash ? FormBorderStyle.None : FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = splash ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        ClientSize = splash ? new Size(440, 180) : new Size(580, 330);
        view.Dock = DockStyle.Fill; view.Paint += PaintView; Controls.Add(view);
        if (splash) { view.Click += (s, e) => Close(); return; }   // a click dismisses the splash early
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 28), Location = new Point(580 - 84 - 16, 330 - 28 - 14), FlatStyle = FlatStyle.Flat,
            BackColor = MainForm.Raised, ForeColor = MainForm.Txt, Cursor = Cursors.Hand };
        ok.FlatAppearance.BorderColor = MainForm.Line;
        Controls.Add(ok); ok.BringToFront(); AcceptButton = CancelButton = ok;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); MainForm.DarkTitle(Handle); }

    void PaintView(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(MainForm.On)) g.FillRectangle(b, 0, 0, view.Width, 3);   // accent stripe
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var logo = Logo.Draw(splash ? 120 : 104)) g.DrawImage(logo, 20, splash ? 28 : 22);
        float x = splash ? 156 : 140;
        g.DrawString("MiniDAW", big, Brushes.White, x, splash ? 40 : 28);
        using (var dim = new SolidBrush(Dim))
        {
            g.DrawString("a tiny pattern-based music studio", text, dim, x + 4, splash ? 92 : 80);
            if (splash) { g.DrawString("starting up…", text, dim, x + 4, 124); return; }
            string built = File.GetLastWriteTime(typeof(AboutForm).Assembly.Location).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            g.DrawString("Version " + Version + "  ·  built " + built, text, Brushes.Gainsboro, x + 4, 104);
            string[] lines =
            {
                "Channel rack, patterns, playlist, mixer with 8 effects per insert, automation,",
                "MIDI keyboard, recording and WAV export, all in one small program.",
                "",
                "Space  play / stop     F9  mixer     F1  this window     Ctrl+Z / Ctrl+Y  undo / redo",
                "The full guide is README.md, next to MiniDAW.exe.",
                "",
                "Workflow inspired by FL Studio. Not affiliated with or endorsed by Image-Line.",
            };
            for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], text, i == lines.Length - 1 ? dim : Brushes.Gainsboro, 24, 146 + i * 19);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) { big.Dispose(); text.Dispose(); } base.Dispose(disposing); }
}

// Updates are the GitHub repository's releases, each carrying MiniDAW-Setup.exe. github.com/<repo>/releases/latest redirects to
// .../releases/tag/<tag>, so the newest version is read from that redirect: no API token, no JSON.
static class Updates
{
    public const string Repo = "grigorehunduu-droid/MiniDAW";   // "owner/name" on GitHub; empty = no update check
    public const string SetupName = "MiniDAW-Setup.exe";

    static Updates() { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; }   // TLS 1.2, which GitHub requires

    public static string LatestTag()   // e.g. "v1.2"; null when offline, blocked, or nothing is released yet
    {
        if (string.IsNullOrEmpty(Repo)) return null;
        try
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://github.com/" + Repo + "/releases/latest");
            req.Method = "HEAD"; req.AllowAutoRedirect = false; req.Timeout = 15000; req.UserAgent = "MiniDAW/" + AboutForm.Version;
            using (var resp = req.GetResponse())
            {
                var m = System.Text.RegularExpressions.Regex.Match(resp.Headers["Location"] ?? "", @"/releases/tag/([^/?#]+)$");
                return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
            }
        }
        catch (System.Net.WebException) { return null; }
        catch (IOException) { return null; }
    }

    public static bool Newer(string tag, string current) { var a = Parse(tag); var b = Parse(current); return a != null && b != null && a > b; }

    static Version Parse(string s)   // "v1.2" and "1.2.0" are the same version (System.Version alone ranks 1.2 below 1.2.0)
    {
        Version v;
        if (s == null || !Version.TryParse(s.TrimStart('v', 'V'), out v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    public static string SetupUrl(string tag) { return "https://github.com/" + Repo + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + SetupName; }
}

// MIDI keyboard input through winmm. The driver calls back on its own thread, so the callback only queues the raw message.
class MidiInput
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MIDIINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwSupport; }
    delegate void MidiProc(IntPtr h, uint msg, IntPtr inst, IntPtr p1, IntPtr p2);
    [DllImport("winmm.dll")] static extern int midiInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW")] static extern int midiInGetDevCaps(IntPtr id, out MIDIINCAPS caps, int size);
    [DllImport("winmm.dll")] static extern int midiInOpen(out IntPtr h, int id, MidiProc cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int midiInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInClose(IntPtr h);

    readonly MidiProc proc;   // kept in a field: the driver holds the callback, so the GC must not collect it
    readonly ConcurrentQueue<int> sink;
    IntPtr h;

    public MidiInput(ConcurrentQueue<int> sink) { this.sink = sink; proc = OnMidi; }

    public static List<string> Devices()
    {
        var names = new List<string>();
        for (int i = 0; i < midiInGetNumDevs(); i++)
        {
            MIDIINCAPS c;
            names.Add(midiInGetDevCaps((IntPtr)i, out c, Marshal.SizeOf(typeof(MIDIINCAPS))) == 0 ? c.szPname : "MIDI device " + (i + 1));
        }
        return names;
    }

    public string Open(int id)
    {
        Close();
        int err = midiInOpen(out h, id, proc, IntPtr.Zero, 0x30000);   // CALLBACK_FUNCTION
        if (err != 0) { h = IntPtr.Zero; return "MIDI device is busy or unavailable (midiInOpen error " + err + ")"; }
        midiInStart(h);
        return null;
    }

    public void Close()
    {
        if (h == IntPtr.Zero) return;
        midiInStop(h); midiInReset(h); midiInClose(h); h = IntPtr.Zero;
    }

    void OnMidi(IntPtr hm, uint msg, IntPtr inst, IntPtr p1, IntPtr p2)
    {
        if (msg == 0x3C3) sink.Enqueue((int)((long)p1 & 0xFFFFFF));   // MIM_DATA: status | data1 << 8 | data2 << 16
    }
}

// Snapshot undo/redo: the whole project as text before each edit gesture (a click or drag, a toolbar action, a recording take).
// A gesture that changed nothing leaves no step.
class History
{
    const int Max = 100;
    readonly List<string> undo = new List<string>(), redo = new List<string>();
    string pending;

    public void Begin(Func<string> snap) { if (pending == null) pending = snap(); }

    public void End(Func<string> snap)
    {
        if (pending == null) return;
        if (pending != snap()) { undo.Add(pending); if (undo.Count > Max) undo.RemoveAt(0); redo.Clear(); }
        pending = null;
    }

    public string Undo(Func<string> snap) { End(snap); return Move(undo, redo, snap); }
    public string Redo(Func<string> snap) { End(snap); return Move(redo, undo, snap); }
    public void Clear() { undo.Clear(); redo.Clear(); pending = null; }

    static string Move(List<string> from, List<string> to, Func<string> snap)   // returns the state to restore, or null
    {
        if (from.Count == 0) return null;
        to.Add(snap());
        string s = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
        return s;
    }
}

// ---------------------------------------------------------------- UI
class Canvas : Panel
{
    public Canvas() { DoubleBuffered = true; ResizeRedraw = true; BackColor = MainForm.PanelBg; }
}

// A drop-down list in the app's colours. Windows draws a flat combo's border white and its arrow black, so both are painted over
// after each paint (and each WM_PRINT, which is what DrawToBitmap uses).
class DarkCombo : ComboBox
{
    public DarkCombo() { DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; BackColor = MainForm.Raised; ForeColor = MainForm.Txt; }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F) using (var g = Graphics.FromHwnd(Handle)) Draw(g);
        else if (m.Msg == 0x0317) using (var g = Graphics.FromHdc(m.WParam)) Draw(g);
    }

    void Draw(Graphics g)
    {
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, r);
        using (var pen = new Pen(Focused || DroppedDown ? MainForm.On : MainForm.Line)) g.DrawRectangle(pen, r);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(6, 0, Width - 28, Height), ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        float ax = Width - 13, ay = Height / 2f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(MainForm.Dim, 1.6f)) g.DrawLines(pen, new[] { new PointF(ax - 4, ay - 2), new PointF(ax, ay + 2), new PointF(ax + 4, ay - 2) });
    }
}

// Right-click menus in the app's colours (every ContextMenuStrip uses the manager's renderer).
class DarkMenus : ToolStripProfessionalRenderer
{
    public DarkMenus() : base(new Colors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? MainForm.Txt : Color.FromArgb(100, 107, 120); base.OnRenderItemText(e); }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = MainForm.Dim; base.OnRenderArrow(e); }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)   // an accent dot: the stock black tick vanishes on a dark menu
    {
        var r = e.ImageRectangle; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(MainForm.On)) e.Graphics.FillEllipse(b, r.X + r.Width / 2f - 4, r.Y + r.Height / 2f - 4, 8, 8);
    }
    class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return MainForm.Raised; } }
        public override Color ImageMarginGradientBegin { get { return MainForm.Raised; } }
        public override Color ImageMarginGradientMiddle { get { return MainForm.Raised; } }
        public override Color ImageMarginGradientEnd { get { return MainForm.Raised; } }
        public override Color MenuBorder { get { return MainForm.Line; } }
        public override Color MenuItemBorder { get { return MainForm.Sel; } }
        public override Color MenuItemSelected { get { return MainForm.Sel; } }
        public override Color SeparatorDark { get { return MainForm.Line; } }
        public override Color SeparatorLight { get { return MainForm.Raised; } }
    }
}

class MainForm : Form
{
    readonly Engine eng = new Engine();
    Project P { get { return eng.Proj; } }
    readonly Canvas rack = new Canvas(), roll = new Canvas(), playlist = new Canvas(), mixer = new Canvas(), browser = new Canvas { Dock = DockStyle.Left, Width = 250, Visible = false };
    readonly ComboBox patBox = new DarkCombo { Width = 124 };
    readonly NumericUpDown bpm = new NumericUpDown { Minimum = Project.MinBpm, Maximum = Project.MaxBpm, Width = 62, BackColor = Well, ForeColor = Txt,
        BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    readonly Button playBtn, modeBtn, mixBtn, recBtn;
    readonly ComboBox midiBox = new DarkCombo { Width = 126 };
    readonly MidiInput midi;
    bool recording; readonly Note[] recNotes = new Note[128];   // notes being recorded, by key, until their note-off
    readonly History hist = new History();
    bool syncingBpm;   // the BPM box is being updated from the project, not by the user
    static bool screenshotMode;   // --screenshot: built off-screen, never activated; no recovery prompt, no MIDI device, the autosave left alone
    protected override bool ShowWithoutActivation { get { return screenshotMode; } }
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 33 };
    int selCh = 3; string dragMode; int dragCh; string filePath; bool dirty; int lastStep = -2;
    Effect dragFx; int dragParam; float dragX0, dragW;
    List<Note> sel = new List<Note>(), clip = new List<Note>();   // piano roll selection, and the copy buffer
    Point? boxFrom, boxTo;                                        // right-drag selection box in the piano roll
    List<Note> moving; Dictionary<Note, Note> moveFrom; Note moveHit; bool moved, resizing;   // notes being dragged: each one as it was before, and the grabbed one

    // Theme: one dark palette for every panel and window. Shapes keep the layout coordinates, so clicking works as before.
    internal static readonly Color Txt = Color.FromArgb(230, 233, 238), Dim = Color.FromArgb(138, 147, 163),
        AppBg = Color.FromArgb(21, 24, 29), PanelBg = Color.FromArgb(28, 32, 38), HeadBg = Color.FromArgb(35, 40, 49),
        Raised = Color.FromArgb(44, 50, 61), Line = Color.FromArgb(58, 66, 79), Well = Color.FromArgb(16, 18, 22), Sel = Color.FromArgb(58, 72, 96),
        Cell1 = Color.FromArgb(42, 47, 57), Cell2 = Color.FromArgb(54, 60, 72), On = Color.FromArgb(255, 159, 58),
        Blue = Color.FromArgb(90, 169, 230), DeepBlue = Color.FromArgb(56, 102, 168), Green = Color.FromArgb(110, 220, 140);
    static readonly Color[] PatColors = { Color.FromArgb(70, 168, 112), Color.FromArgb(78, 132, 224), Color.FromArgb(222, 92, 108),
        Color.FromArgb(214, 150, 56), Color.FromArgb(150, 104, 224), Color.FromArgb(52, 176, 188) };
    const int Row = 26, RackX = 284, Cell = 26, Top0 = 26, KeyW = 44, PlLabel = 64, HeadH = 19, ParamsH = 3 * 14 + 4, StripHead = 86, AutoBoxX = 444, AutoBoxW = 360, VelH = 56, SwingW = 150;
    static readonly Font Small = new Font("Segoe UI", 8), Bold = new Font("Segoe UI", 9, FontStyle.Bold);
    static readonly SolidBrush TxtB = new SolidBrush(Txt), DimB = new SolidBrush(Dim);
    static readonly StringFormat Mid = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center },
        Fit = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

    static GraphicsPath RoundPath(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    internal static void Round(Graphics g, Color c, float x, float y, float w, float h, float rad)   // anti-aliased rounded box; grid lines stay pixel-crisp
    {
        if (w <= 0 || h <= 0) return;
        var keep = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = RoundPath(new RectangleF(x, y, w, h), rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        g.SmoothingMode = keep;
    }

    static void RoundEdge(Graphics g, Color c, float width, RectangleF r, float rad)
    {
        var keep = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = RoundPath(r, rad)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        g.SmoothingMode = keep;
    }

    internal static void ValueBar(Graphics g, float x, float y, float w, float h, float from, float to, Color c)   // rounded track, filled between two fractions of its width
    {
        float rad = Math.Min(h / 2, 4);
        Round(g, Well, x, y, w, h, rad);
        if (to <= from) return;
        var st = g.Save();
        g.SetClip(new RectangleF(x + w * from, y, w * (to - from), h), CombineMode.Intersect);
        Round(g, c, x, y, w, h, rad);
        g.Restore(st);
    }

    static void Led(Graphics g, float x, float y, float d, bool lit)   // status dot: lit green with a glow, or an empty ring
    {
        var keep = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (lit)
        {
            using (var b = new SolidBrush(Color.FromArgb(45, Green))) g.FillEllipse(b, x - 2, y - 2, d + 4, d + 4);
            using (var b = new SolidBrush(Green)) g.FillEllipse(b, x, y, d, d);
        }
        else using (var pen = new Pen(Dim, 1.5f)) g.DrawEllipse(pen, x + 0.5f, y + 0.5f, d - 1, d - 1);
        g.SmoothingMode = keep;
    }

    static void Header(Graphics g, Control c, string title)   // the strip across the top of each panel
    {
        using (var b = new SolidBrush(HeadBg)) g.FillRectangle(b, 0, 0, c.Width, 22);
        using (var b = new SolidBrush(On)) g.FillRectangle(b, 0, 0, 3, 22);
        using (var pen = new Pen(AppBg)) g.DrawLine(pen, 0, 22, c.Width, 22);
        g.DrawString(title, Bold, TxtB, 9, 4);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    internal static void DarkTitle(IntPtr h)   // dark title bar (attribute 20) on Windows 10 20H1+ and 11; elsewhere the call fails and the bar stays light
    {
        int on = 1;
        try { DwmSetWindowAttribute(h, 20, ref on, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); DarkTitle(Handle); }

    public MainForm()
    {
        Text = "MiniDAW"; ClientSize = new Size(1380, 800); MinimumSize = new Size(800, 600); Icon = Logo.Icon();
        BackColor = AppBg; ForeColor = Txt; Font = new Font("Segoe UI", 9);
        ToolStripManager.Renderer = new DarkMenus();
        eng.Proj = Project.Demo();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 7, 8, 7) };
        playBtn = Btn(bar, "▶ Play", (s, e) => TogglePlay());
        playBtn.BackColor = playBtn.FlatAppearance.BorderColor = On; playBtn.ForeColor = Color.FromArgb(28, 22, 14); playBtn.Font = Bold; playBtn.MinimumSize = new Size(76, 28);
        playBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 180, 100); playBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(230, 135, 40);
        modeBtn = Btn(bar, "Mode: Pattern", (s, e) => { eng.SongMode = !eng.SongMode; modeBtn.Text = eng.SongMode ? "Mode: Song" : "Mode: Pattern"; });
        modeBtn.MinimumSize = new Size(98, 28);   // same width in both modes, so the toolbar doesn't shift
        bar.Controls.Add(Gap(Lbl("BPM"))); bar.Controls.Add(bpm);
        bpm.Margin = new Padding(3, 5, 3, 3);
        bpm.Controls[0].Visible = false;   // Windows draws the spin arrows light; the wheel and ↑/↓ still change the tempo
        new ToolTip().SetToolTip(bpm, "Tempo: type it, scroll the wheel or press ↑/↓ · right-click: automate it");
        bpm.ValueChanged += (s, e) => { if (!syncingBpm) Edit(() => { lock (P) P.Bpm = (int)bpm.Value; dirty = true; }); };
        bpm.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) { var l = new Lane { Tempo = true }; ControlMenu(l, bpm, e.Location); } };   // a tempo lane, like FL's tempo automation clip
        patBox.Margin = midiBox.Margin = new Padding(3, 5, 3, 3);
        bar.Controls.Add(Gap(patBox));   // the list shows "Pattern 1"…, so it needs no label
        patBox.SelectedIndexChanged += (s, e) => { eng.CurPattern = Math.Max(0, patBox.SelectedIndex); Changed(false); };
        patBox.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) PatternMenu(); };
        Btn(bar, "+ Pattern", (s, e) => Edit(() => { lock (P) P.Patterns.Add(new Pattern { Name = "Pattern " + (P.Patterns.Count + 1) }); RefreshAll(P.Patterns.Count - 1); dirty = true; }));
        Button soundBtn = null;
        soundBtn = Btn(bar, "+ Sound", (s, e) => SoundMenu(kind => AddChannel(new Channel { Kind = kind, Name = FreeName(Channel.Title(kind)) })).Show(soundBtn, 0, soundBtn.Height));
        Gap(soundBtn);
        Btn(bar, "+ Sample…", (s, e) => AddSample());
        Gap(Btn(bar, "New", (s, e) => { if (ConfirmDiscard()) { ClearAutosave(); eng.Proj = Project.Demo(); filePath = null; hist.Clear(); RefreshAll(0); dirty = false; } }));
        Button openBtn = null;
        openBtn = Btn(bar, "Open…", (s, e) => OpenProject(openBtn));
        Btn(bar, "Save…", (s, e) => SaveProject());
        Button exportBtn = null;
        exportBtn = Btn(bar, "Export…", (s, e) =>
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Audio (WAV, FLAC, MP3)…", null, (s2, e2) => ExportAudio());
            m.Items.Add("MIDI file (.mid)…", null, (s2, e2) => ExportMidi());
            m.Show(exportBtn, 0, exportBtn.Height);
        });
        mixBtn = Gap(Btn(bar, "Mixer (F9)", (s, e) => ToggleMixer()));
        mixBtn.MinimumSize = new Size(108, 28);   // fits "Piano roll (F9)" too, so the toolbar doesn't shift
        midi = new MidiInput(eng.MidiIn);
        bar.Controls.Add(Gap(midiBox));   // the keyboard plays the selected channel, like FL
        midiBox.DropDown += (s, e) => FillMidi();   // re-list on open, so a keyboard plugged in later shows up
        midiBox.SelectedIndexChanged += (s, e) => OpenMidi();
        recBtn = Btn(bar, "● Rec", (s, e) => { EndEdit(); recording = !recording; recBtn.BackColor = recording ? Color.FromArgb(200, 52, 60) : Raised; });
        metroBtn = Btn(bar, "", (s, e) => ToggleMetronome());   // an icon button: a metronome drawn in code
        metroBtn.MinimumSize = new Size(30, 28); metroBtn.Paint += PaintMetronome;
        new ToolTip().SetToolTip(metroBtn, "Metronome (Ctrl+M) · right-click: count-in before recording");
        metroBtn.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var m = new ContextMenuStrip();
            var item = (ToolStripMenuItem)m.Items.Add("Count in one bar before recording", null, (s2, e2) => countIn = !countIn);
            item.Checked = countIn;
            m.Show(metroBtn, 0, metroBtn.Height);
        };
        updateBtn = Gap(Btn(bar, "Update", (s, e) => InstallUpdate()));   // shown only when GitHub has a newer release
        updateBtn.BackColor = updateBtn.FlatAppearance.BorderColor = Color.FromArgb(46, 140, 82); updateBtn.Font = Bold; updateBtn.Visible = false;
        updateBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 165, 100);
        var about = new PictureBox { Image = Logo.Draw(24), Size = new Size(28, 28), SizeMode = PictureBoxSizeMode.CenterImage, Cursor = Cursors.Hand, Margin = new Padding(8, 2, 0, 0) };
        about.Click += (s, e) => ShowAbout();   // the banana at the end of the toolbar
        new ToolTip().SetToolTip(about, "About MiniDAW (F1)");
        bar.Controls.Add(about);

        playlist.Dock = DockStyle.Bottom; playlist.Height = 220;
        rack.Dock = DockStyle.Top;
        roll.Dock = mixer.Dock = DockStyle.Fill;
        mixer.Visible = false;   // mixer and piano roll share the middle area; F9 swaps them
        Controls.Add(roll); Controls.Add(mixer); Controls.Add(playlist); Controls.Add(rack); Controls.Add(browser); Controls.Add(bar);   // dock order: last added docks first
        browser.Paint += PaintBrowser; browser.MouseDown += BrowserDown; browser.MouseMove += BrowserMove; browser.MouseDoubleClick += BrowserOpen;
        browser.MouseWheel += (s, e) => { browserScroll = Math.Max(0, browserScroll - Math.Sign(e.Delta) * 3); browser.Invalidate(); };

        // every click or drag is one undo step: the snapshot is taken before the panel's own MouseDown and closed after its own MouseUp
        foreach (var c in new[] { rack, roll, playlist, mixer }) c.MouseDown += (s, e) => BeginEdit();
        rack.Paint += PaintRack; rack.MouseDown += RackDown; rack.MouseMove += RackMove;
        rack.MouseUp += (s, e) =>
        {
            if (dragMode == "insert" && !insDragged && dragCh < P.Channels.Count)   // a click on the insert box: one up (right-click: one down), wrapping around
            { var c = P.Channels[dragCh]; c.Insert = (c.Insert + insDir + Project.Inserts) % Project.Inserts; dirty = true; Changed(); }
            dragMode = null;
        };
        rack.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left && e.X >= 20 && e.X < 134 && RackRow(e.Y) >= 0) ShowSoundSettings(); };
        roll.Paint += PaintRoll; roll.MouseDown += RollDown; roll.MouseMove += RollMove;
        roll.MouseUp += (s, e) => { dragNotes = null; dragMode = null; if (boxFrom.HasValue) FinishBox(); if (moving != null) FinishMove(); };
        roll.MouseWheel += RollWheel;
        playlist.Paint += PaintPlaylist; playlist.MouseDown += PlaylistDown; playlist.MouseMove += PlaylistMouse; playlist.MouseUp += (s, e) => FinishClipDrag();
        playlist.MouseWheel += PlaylistWheel;
        mixer.Paint += PaintMixer; mixer.MouseDown += MixerDown; mixer.MouseMove += MixerMove; mixer.MouseWheel += MixerWheel;
        mixer.MouseUp += (s, e) => { dragMode = null; dragFx = null; if (slotDragIns >= 0) FinishSlotDrag(e); };
        foreach (var c in new[] { rack, roll, playlist, mixer }) c.MouseUp += (s, e) => EndEdit();

        timer.Tick += (s, e) =>
        {
            int st = eng.PlayStep;
            if (st != lastStep) { lastStep = st; rack.Invalidate(); roll.Invalidate(); playlist.Invalidate(); }
            if (mixer.Visible) mixer.Invalidate();   // meters move every frame
            string err = eng.LastError;
            if (err != null) { eng.LastError = null; Text = "MiniDAW — audio error: " + err; }
            eng.MidiTarget = selCh < P.Channels.Count ? P.Channels[selCh] : null;
            if (!bpm.Focused && bpm.Value != P.Bpm)   // a tempo lane moved the tempo: show it, without that counting as an edit
            {
                syncingBpm = true; bpm.Value = Math.Max(bpm.Minimum, Math.Min(bpm.Maximum, P.Bpm)); syncingBpm = false;
            }
            KeyValuePair<int, double> ev;
            while (eng.MidiRec.TryDequeue(out ev)) RecordMidi(ev.Key, ev.Value);
            if (!eng.Learned.IsEmpty) LearnArrived();
        };
        timer.Start();
        autoTimer.Tick += (s, e) => Autosave(); autoTimer.Start();
        Shown += (s, e) => { OfferRecovery(); CheckForUpdate(); };
        foreach (var c in Descendants(this))   // drag-and-drop: every panel takes files, since a drop goes to the control under the mouse
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => e.Effect = Droppable(e.Data.GetData(DataFormats.FileDrop) as string[]) ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) BeginInvoke((Action)(() => OpenFiles(files)));   // after the drop returns, so Explorer isn't held up by any dialog
            };
        }
        FormClosing += (s, e) => { if (!ConfirmDiscard()) { e.Cancel = true; return; } if (!screenshotMode) ClearAutosave(); midi.Close(); eng.Stop(); };   // saved or knowingly discarded: nothing to recover
        KeyPreview = true;   // the form sees letter keys before the focused control
        KeyDown += (s, e) => PianoKey(e, true);
        KeyUp += (s, e) => PianoKey(e, false);
        Deactivate += (s, e) => ReleasePiano();
        RefreshAll(0);
        dirty = false;
        string startErr = eng.Start();
        if (startErr != null) MessageBox.Show(this, startErr + "\nThe editor works, but nothing will be heard.", "MiniDAW");
        FillMidi();
    }

    readonly Button updateBtn; string updateTag;
    readonly Button metroBtn; bool countIn = true;   // count-in: on by default, as most DAWs do when recording

    void ToggleMetronome() { eng.Metronome = !eng.Metronome; metroBtn.BackColor = eng.Metronome ? Sel : Raised; metroBtn.Invalidate(); }

    void PaintMetronome(object sender, PaintEventArgs e)   // a pyramid body with its pendulum, lit orange when on
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = metroBtn.Width / 2f, top = 6, bot = metroBtn.Height - 6;
        using (var pen = new Pen(eng.Metronome ? On : Dim, 1.6f))
        {
            g.DrawPolygon(pen, new[] { new PointF(cx - 3, top), new PointF(cx + 3, top), new PointF(cx + 7, bot), new PointF(cx - 7, bot) });
            g.DrawLine(pen, cx, bot - 4, cx + 6, top + 1);
        }
    }

    void CheckForUpdate()   // once per start, in the background; offline or no release just means no Update button
    {
        if (screenshotMode || Updates.Repo == "") return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string tag = Updates.LatestTag();
            if (tag == null || !Updates.Newer(tag, AboutForm.Version)) return;
            try { BeginInvoke((Action)(() => { updateTag = tag; updateBtn.Text = "⬆ Update to " + tag.TrimStart('v', 'V'); updateBtn.Visible = true; })); }
            catch (InvalidOperationException) { }   // the window closed while we were asking
        });
    }

    void InstallUpdate()   // download the release's installer, then let it replace this copy and start the new one
    {
        string tag = updateTag, ver = tag.TrimStart('v', 'V');
        if (MessageBox.Show(this, "MiniDAW " + ver + " is available (you have " + AboutForm.Version + ").\n\nDownload and install it now? MiniDAW closes for the install and opens again afterwards.",
                "MiniDAW update", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK || !ConfirmDiscard()) return;
        string file = Path.Combine(Path.GetTempPath(), "MiniDAW-Setup-" + ver + ".exe");
        var wc = new System.Net.WebClient();
        wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "MiniDAW/" + AboutForm.Version;
        updateBtn.Enabled = false; updateBtn.Text = "Downloading…";
        wc.DownloadProgressChanged += (s, e) => updateBtn.Text = "Downloading " + e.ProgressPercentage + "%";
        wc.DownloadFileCompleted += (s, e) =>
        {
            wc.Dispose();
            if (e.Error != null || e.Cancelled)
            {
                updateBtn.Enabled = true; updateBtn.Text = "⬆ Update to " + ver;
                MessageBox.Show(this, "The download didn't finish" + (e.Error != null ? ": " + e.Error.Message : ".") + "\nNothing was changed; try again later.", "MiniDAW update");
                return;
            }
            System.Diagnostics.Process.Start(file, "/update");   // it waits for this window to close
            dirty = false; Close();                              // the project was saved or knowingly discarded above
        };
        wc.DownloadFileAsync(new Uri(Updates.SetupUrl(tag)), file);
    }

    void FillMidi()   // (re)list MIDI inputs, keeping the current one; the first time, pick the first keyboard found
    {
        string keep = midiBox.SelectedIndex > 0 ? (string)midiBox.SelectedItem : null;
        bool first = midiBox.Items.Count == 0;
        midiBox.Items.Clear();
        midiBox.Items.Add("(no MIDI input)");
        foreach (var d in MidiInput.Devices()) midiBox.Items.Add(d);
        int k = keep != null ? midiBox.Items.IndexOf(keep) : first && midiBox.Items.Count > 1 ? 1 : 0;
        midiBox.SelectedIndex = Math.Max(0, k);
    }

    void OpenMidi()
    {
        midi.Close();
        if (screenshotMode) return;   // a screenshot never takes a MIDI device away from a running MiniDAW
        eng.MidiIn.Enqueue(0xB0 | 64 << 8);   // pedal up first, or the note-offs below would be held back
        eng.MidiIn.Enqueue(0xE0 | 64 << 16);  // pitch wheel back to centre (8192)
        eng.MidiIn.Enqueue(0xB0 | 1 << 8);    // mod wheel down: no vibrato
        for (int k = 0; k < 128; k++) eng.MidiIn.Enqueue(0x80 | k << 8);   // release notes still held from the old device
        if (midiBox.SelectedIndex <= 0) return;
        string err = midi.Open(midiBox.SelectedIndex - 1);
        if (err != null) { Text = "MiniDAW — " + err; midiBox.SelectedIndex = 0; }
    }

    // Rec on + playing in Pattern mode: keyboard notes land in the current pattern on the selected channel, snapped to the piano roll's grid.
    void RecordMidi(int msg, double pos)
    {
        int key = (msg >> 8) & 0x7F; double len = CurPat.Length, step = SnapNear(pos) % len;
        var open = recNotes[key]; recNotes[key] = null;
        if (open != null) open.Len = Math.Max(Grid(), Math.Min(len - open.Step, ((step - open.Step) % len + len) % len));   // note-off sets the length
        bool on = (msg & 0xF0) == 0x90 && (msg >> 16 & 0x7F) > 0;
        if (on && recording && selCh < P.Channels.Count)
        {
            BeginEdit();   // a whole take is one undo step; it ends when playback or Rec stops
            Note n;
            lock (P)
            {
                var list = CurPat.For(selCh);
                n = list.Find(x => Math.Abs(x.Step - step) < 1e-6 && x.Key == key);
                if (n == null) list.Add(n = new Note { Step = step, Key = key, Len = Grid() });
                n.Vel = msg >> 16 & 0x7F;   // how hard the key was hit
            }
            recNotes[key] = n;
        }
        if (open != null || on && recording) Changed();
    }

    static Label Lbl(string t) { return new Label { Text = t, AutoSize = true, Font = Small, ForeColor = Dim, Padding = new Padding(0, 9, 0, 0) }; }

    static T Gap<T>(T c) where T : Control { c.Margin = new Padding(c.Margin.Left + 7, c.Margin.Top, c.Margin.Right, c.Margin.Bottom); return c; }   // space before a toolbar group

    static Button Btn(Control parent, string t, EventHandler click)
    {
        var b = new Button { Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Raised, ForeColor = Txt, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 28), Cursor = Cursors.Hand, Padding = new Padding(3, 0, 3, 0) };
        b.FlatAppearance.BorderColor = Line; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 64, 78); b.FlatAppearance.MouseDownBackColor = Sel;
        b.Click += click; parent.Controls.Add(b); return b;
    }

    // Computer keyboard as a piano, like FL's typing keyboard. Notes go through the MIDI path, so Rec records them too.
    static readonly Keys[] PianoKeys = { Keys.Z, Keys.S, Keys.X, Keys.D, Keys.C, Keys.V, Keys.G, Keys.B, Keys.H, Keys.N, Keys.J, Keys.M,   // C4..B4
        Keys.Q, Keys.D2, Keys.W, Keys.D3, Keys.E, Keys.R, Keys.D5, Keys.T, Keys.D6, Keys.Y, Keys.D7, Keys.U, Keys.I };                     // C5..C6
    readonly int[] pianoHeld = Enumerable.Repeat(-1, PianoKeys.Length).ToArray();   // the note each held key started, -1 = up
    const int MinOctave = -3, MaxOctave = 3;
    int pianoOctave;   // - and = move the keyboard down and up; 0: Z = C4, Q = C5

    static int PianoMsg(Keys k, bool down, int[] held, int octave)   // MIDI message for a key press/release, or 0
    {
        int i = Array.IndexOf(PianoKeys, k);
        if (i < 0 || (held[i] >= 0) == down) return 0;   // not a piano key, or Windows' auto-repeat of a key already held
        int key = down ? 48 + 12 * octave + i : held[i];   // a release ends the note that press started, even if the octave moved meanwhile
        held[i] = down ? key : -1;
        return down ? 0x90 | key << 8 | 100 << 16 : 0x80 | key << 8;
    }

    static string NoteName(int key) { return new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[key % 12] + key / 12; }   // FL naming: 60 = C5

    void PianoKey(KeyEventArgs e, bool down)
    {
        bool octaveKey = e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Oemplus;
        if (Array.IndexOf(PianoKeys, e.KeyCode) < 0 && !octaveKey) return;
        if (down && (e.Modifiers != Keys.None || ActiveControl is NumericUpDown)) return;   // shortcuts and typing a BPM aren't notes
        e.SuppressKeyPress = true;   // stops a focused drop-down from jumping to an item that starts with this letter
        if (octaveKey)
        {
            if (down) { pianoOctave = Math.Max(MinOctave, Math.Min(MaxOctave, pianoOctave + (e.KeyCode == Keys.Oemplus ? 1 : -1))); ShowKeys(48 + 12 * pianoOctave); roll.Invalidate(); }   // the roll scrolls to the keyboard's range
            return;
        }
        int m = PianoMsg(e.KeyCode, down, pianoHeld, pianoOctave);
        if (m != 0) eng.MidiIn.Enqueue(m);
    }

    void ReleasePiano()   // the window lost focus, so key-ups will never arrive
    {
        foreach (var k in PianoKeys) { int m = PianoMsg(k, false, pianoHeld, pianoOctave); if (m != 0) eng.MidiIn.Enqueue(m); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space && !(ActiveControl is NumericUpDown)) { TogglePlay(); return true; }   // space = play/stop, like FL
        if (keyData == Keys.F9) { ToggleMixer(); return true; }
        if (keyData == Keys.F8) { ToggleBrowser(); return true; }
        if (keyData == Keys.F1) { ShowAbout(); return true; }
        if (keyData == Keys.Escape && learning != null) { eng.LearnTarget = null; learning = null; learnFor = null; Text = "MiniDAW" + (filePath != null ? " — " + Path.GetFileName(filePath) : ""); return true; }
        if (keyData == (Keys.Control | Keys.M)) { ToggleMetronome(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { UndoRedo(false); return true; }
        if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { UndoRedo(true); return true; }
        if (!(ActiveControl is NumericUpDown) && P.Channels.Count > 0)   // note editing; the BPM box keeps its own text keys
        {
            if (keyData == (Keys.Control | Keys.A)) { sel = CurPat.For(selCh).ToList(); roll.Invalidate(); return true; }
            if (keyData == (Keys.Control | Keys.C) || keyData == (Keys.Control | Keys.X)) { CopyNotes(keyData == (Keys.Control | Keys.X)); return true; }
            if (keyData == (Keys.Control | Keys.V) || keyData == (Keys.Control | Keys.B)) { PasteNotes(keyData == (Keys.Control | Keys.B)); return true; }
            if (keyData == Keys.Delete) { DeleteNotes(); return true; }
            if (keyData == (Keys.Control | Keys.Q)) { Tool("quantize"); return true; }   // quick quantize, like FL
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void TogglePlay()   // ends a recording take's undo step
    {
        EndEdit();
        eng.CountIn = countIn && recording && !eng.SongMode;   // read by the engine when playback starts
        eng.Playing = !eng.Playing; playBtn.Text = eng.Playing ? "■ Stop" : "▶ Play";
    }

    void ShowAbout() { using (var a = new AboutForm(false)) a.ShowDialog(this); }

    // ---- autosave: every minute, unsaved work is copied to %APPDATA%\MiniDAW\autosave.mdaw. Saving, discarding or closing
    // normally deletes it, so finding one at start-up means the last session ended abruptly, and it's offered back.
    public static string AutosavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniDAW", "autosave.mdaw");
    readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
    string lastAutosave;   // what was last written, so an unchanged project isn't written again

    void Autosave()
    {
        if (!dirty) return;
        var lines = P.Lines();
        lines.Add("origin\t" + (filePath ?? ""));   // where it came from; Project.Parse skips line types it doesn't know
        string text = string.Join("\r\n", lines);
        if (text == lastAutosave) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutosavePath));
            string tmp = AutosavePath + ".tmp";   // write aside, then swap in, so a crash mid-write can't spoil the last good autosave
            File.WriteAllText(tmp, text);
            if (File.Exists(AutosavePath)) File.Replace(tmp, AutosavePath, null); else File.Move(tmp, AutosavePath);
            lastAutosave = text;
        }
        catch (Exception) { }   // a safety net: a failed autosave must never interrupt the music
    }

    void ClearAutosave() { lastAutosave = null; try { File.Delete(AutosavePath); } catch (Exception) { } }

    void OfferRecovery()
    {
        if (screenshotMode || !File.Exists(AutosavePath)) return;
        string when = File.GetLastWriteTime(AutosavePath).ToString("HH:mm 'on' d MMM", CultureInfo.InvariantCulture);
        var r = MessageBox.Show(this, "MiniDAW didn't close normally last time.\n\nRecover the unsaved work autosaved at " + when + "?",
                                "MiniDAW", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) { ClearAutosave(); return; }
        try { LoadRecovered(); }
        catch (Exception e) { MessageBox.Show(this, "The autosave couldn't be read:\n" + e.Message, "MiniDAW"); ClearAutosave(); }
    }

    void LoadRecovered()   // the autosave becomes the current, unsaved project; the file stays until that work is saved or discarded
    {
        var lines = File.ReadAllLines(AutosavePath);
        var p = Project.Parse(lines, null);
        string origin = lines.Where(l => l.StartsWith("origin\t")).Select(l => l.Substring(7)).FirstOrDefault();
        eng.Proj = p; filePath = string.IsNullOrEmpty(origin) ? null : origin; hist.Clear();
        RefreshAll(0); dirty = true;
        Text = "MiniDAW — " + (filePath != null ? Path.GetFileName(filePath) : "untitled") + " (recovered)";
    }

    string Snap() { return string.Join("\n", P.Lines()); }
    void BeginEdit() { hist.Begin(Snap); }
    void EndEdit() { hist.End(Snap); }
    void Edit(Action a) { BeginEdit(); a(); EndEdit(); }

    void UndoRedo(bool redo)
    {
        string s = redo ? hist.Redo(Snap) : hist.Undo(Snap);
        if (s == null) return;
        eng.Proj = Project.Parse(s.Split('\n'), P);   // our own snapshot, so it parses; samples come from memory
        RefreshAll(patBox.SelectedIndex);
        dirty = true;
    }

    void Changed(bool edit = true)
    {
        if (edit) dirty = true;
        rack.Invalidate(); roll.Invalidate(); playlist.Invalidate(); mixer.Invalidate();
        if (soundWin != null && !soundWin.IsDisposed) soundWin.view.Invalidate();   // it follows the selected channel, and undo
    }

    // ---- sound settings window: the selected channel's knobs, like FL's channel settings. Double-click a channel name to open it.
    SoundWindow soundWin;

    void ShowSoundSettings()
    {
        if (P.Channels.Count == 0) return;
        if (soundWin == null || soundWin.IsDisposed) { soundWin = new SoundWindow(this); soundWin.Show(this); }
        else soundWin.Activate();
        soundWin.view.Invalidate();
    }

    // A list of rows (section headings and bars), laid out in columns: the sound's own settings, the analog synth's 28, the sampler's
    // options, then glide and the arpeggiator. Right-click a bar that can move with the song: automation clip or MIDI link.
    class SoundWindow : Form
    {
        const int RowH = 28, ColW = 300, X0 = 12, RowsTop = 30, HeadH = 22, MaxColH = 560;
        readonly MainForm m;
        public readonly Canvas view = new Canvas();
        Row dragRow;

        class Row
        {
            public string Head;                                     // a section heading instead of a bar
            public Func<Channel, float> Get; public Action<Channel, float> Set; public Func<Channel, string> Label;
            public int Lane = -1;                                   // the channel-lane Param it can be automated as; -1 = none
            public bool Toggle;                                     // on / off: a click flips it
            public Color Col; public Func<Channel, bool> Live;      // greyed out while it doesn't matter
            public RectangleF At;
        }

        public SoundWindow(MainForm main)
        {
            m = main;
            FormBorderStyle = FormBorderStyle.FixedToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            Font = main.Font; view.Dock = DockStyle.Fill; Controls.Add(view);
            view.Paint += PaintView;
            view.MouseDown += (s, e) =>
            {
                var c = Ch(); if (c == null) return;
                if (e.Y < RowsTop - 4) { if (c.Kind == "analog" && e.X > view.Width - 130) PresetMenu(c); return; }
                var row = Place(c).FirstOrDefault(r => r.Head == null && r.At.Contains(e.Location));
                if (row == null) return;
                if (e.Button == MouseButtons.Right) { if (row.Lane >= 0) m.ControlMenu(new Lane { Ch = m.P.Channels.IndexOf(c), Param = row.Lane }, view, e.Location); return; }
                if (e.Button != MouseButtons.Left) return;
                if (row.Toggle) { m.Edit(() => row.Set(c, row.Get(c) > 0.5f ? 0 : 1)); m.dirty = true; view.Invalidate(); return; }
                m.BeginEdit(); dragRow = row; SetFrom(e);   // one drag = one undo step
            };
            view.MouseMove += (s, e) => { if (dragRow != null && e.Button == MouseButtons.Left) SetFrom(e); };
            view.MouseUp += (s, e) => { if (dragRow != null) { dragRow = null; m.EndEdit(); } };
            HandleCreated += (s, e) => DarkTitle(Handle);
            KeyPreview = true;   // keep the computer-keyboard piano playing while this window has focus
            KeyDown += (s, e) => m.PianoKey(e, true);
            KeyUp += (s, e) => m.PianoKey(e, false);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { return m.ProcessCmdKey(ref msg, keyData); }   // Space, F9, Ctrl+Z… work here too

        Channel Ch() { var ch = m.P.Channels; return ch.Count == 0 ? null : ch[Math.Max(0, Math.Min(m.selCh, ch.Count - 1))]; }

        static void SetKnob(Channel c, int k, float v) { var a = (float[])(c.Knob ?? Channel.BuiltIn(c.Kind)).Clone(); a[k] = v; c.Knob = a; }        // a new array: the audio thread never sees half a change
        static void SetSyn(Channel c, int k, float v) { var a = (float[])(c.Syn ?? Channel.SynPresets[0]).Clone(); a[k] = v; c.Syn = a; }

        static Row Bar(Func<Channel, float> get, Action<Channel, float> set, Func<Channel, string> label, Color col, Func<Channel, bool> live = null) { return new Row { Get = get, Set = set, Label = label, Col = col, Live = live }; }

        static List<Row> Rows(Channel c)
        {
            var rows = new List<Row> { new Row { Head = "SOUND  ·  " + Channel.Title(c.Kind) } };
            foreach (int k in c.KnobsShown()) { int kk = k; var r = Bar(ch => ch.K(kk), (ch, v) => SetKnob(ch, kk, v), ch => ch.KnobLabel(kk), DeepBlue); r.Lane = kk; rows.Add(r); }
            if (c.Kind == "analog")
            {
                var heads = new Dictionary<int, string> { { 0, "OSCILLATORS" }, { 10, "FILTER" }, { 14, "AMP ENVELOPE" }, { 18, "FILTER ENVELOPE" }, { 22, "LFO" }, { 25, "UNISON AND VOLUME" } };
                for (int i = 0; i < Channel.SynNames.Length; i++)
                {
                    if (heads.ContainsKey(i)) rows.Add(new Row { Head = heads[i] });
                    int ii = i; var r = Bar(ch => ch.S(ii), (ch, v) => SetSyn(ch, ii, v), ch => ch.SynLabel(ii), Color.FromArgb(196, 116, 56)); r.Lane = Channel.LaneSyn + ii; rows.Add(r);
                }
            }
            if (c.Kind == "sample")
            {
                var col = Color.FromArgb(52, 150, 132);
                rows.Add(new Row { Head = "SAMPLE" + (c.Slices != null ? "  ·  " + c.Slices.Length + " slices from C5 up" : "") });
                rows.Add(Bar(ch => (ch.Stretch - 0.5f) / 1.5f, (ch, v) => ch.Stretch = Math.Abs(v - 1 / 3f) < 0.02f ? 1 : 0.5f + v * 1.5f,
                    ch => "Stretch  " + (Math.Abs(ch.Stretch - 1) < 1e-3 ? "off" : ch.Stretch.ToString("0.00", CultureInfo.InvariantCulture) + "× as long, same pitch"), col));
                rows.Add(new Row { Get = ch => ch.Reverse ? 1 : 0, Set = (ch, v) => ch.Reverse = v > 0.5f, Label = ch => "Reverse  " + (ch.Reverse ? "on" : "off"), Col = col, Toggle = true });
                rows.Add(new Row { Get = ch => ch.Loop ? 1 : 0, Set = (ch, v) => ch.Loop = v > 0.5f, Label = ch => "Loop while held  " + (ch.Loop ? "on" : "off"), Col = col, Toggle = true });
                rows.Add(Bar(ch => ch.LoopStart, (ch, v) => ch.LoopStart = Math.Min(v, ch.LoopEnd - 0.01f), ch => "Loop start  " + (int)Math.Round(ch.LoopStart * 100) + "%", col, ch => ch.Loop));
                rows.Add(Bar(ch => ch.LoopEnd, (ch, v) => ch.LoopEnd = Math.Max(v, ch.LoopStart + 0.01f), ch => "Loop end  " + (int)Math.Round(ch.LoopEnd * 100) + "%", col, ch => ch.Loop));
            }
            if (!c.IsDrum)
            {
                rows.Add(new Row { Head = "PLAYING  ·  glide and arpeggiator" });
                rows.Add(Bar(ch => ch.Glide, (ch, v) => ch.Glide = v < 0.02f ? 0 : v, ch => "Glide  " + (ch.Glide == 0 ? "off" : (int)Math.Round(ch.Glide * 500) + " ms, one note at a time"), Color.FromArgb(46, 140, 120), ch => ch.Glide > 0));
                var arp = Color.FromArgb(150, 104, 224);
                rows.Add(Bar(ch => ch.ArpDir / (float)(Channel.ArpDirs.Length - 1), (ch, v) => ch.ArpDir = (int)Math.Round(v * (Channel.ArpDirs.Length - 1)), ch => "Arpeggio  " + Channel.ArpDirs[ch.ArpDir], arp));
                rows.Add(Bar(ch => (ch.ArpRange - 1) / 3f, (ch, v) => ch.ArpRange = 1 + (int)Math.Round(v * 3), ch => "Range  " + ch.ArpRange + (ch.ArpRange == 1 ? " octave" : " octaves"), arp, ch => ch.ArpDir != 0));
                rows.Add(Bar(ch => ch.ArpTime / (float)(Channel.ArpTimes.Length - 1), (ch, v) => ch.ArpTime = (int)Math.Round(v * (Channel.ArpTimes.Length - 1)), ch => "Time  " + Channel.ArpTimeNames[ch.ArpTime] + " notes", arp, ch => ch.ArpDir != 0));
                rows.Add(Bar(ch => ch.ArpGate, (ch, v) => ch.ArpGate = Math.Max(0.05f, v), ch => "Gate  " + (int)Math.Round(ch.ArpGate * 100) + "% of each note", arp, ch => ch.ArpDir != 0));
            }
            return rows;
        }

        List<Row> Place(Channel c)   // rows placed down a column, starting a new one when it gets too tall
        {
            var rows = Rows(c); float x = X0, y = RowsTop;
            foreach (var r in rows)
            {
                float h = r.Head != null ? HeadH : RowH;
                if (y + h > RowsTop + MaxColH && r.Head == null && y > RowsTop) { x += ColW + X0; y = RowsTop; }
                r.At = new RectangleF(x, y, ColW, h - (r.Head != null ? 0 : 6)); y += h;
            }
            return rows;
        }

        void SetFrom(MouseEventArgs e)
        {
            var c = Ch(); if (c == null || dragRow == null) return;
            dragRow.Set(c, Clamp01((e.X - dragRow.At.X) / dragRow.At.Width));   // the audio thread reads it on its next sample
            m.dirty = true; view.Invalidate();
        }

        void PresetMenu(Channel c)   // the analog synth's presets
        {
            var menu = new ContextMenuStrip();
            for (int k = 0; k < Channel.PresetNames.Length; k++) { int kk = k; menu.Items.Add(Channel.PresetNames[k], null, (s, e) => { m.Edit(() => c.Syn = (float[])Channel.SynPresets[kk].Clone()); m.dirty = true; view.Invalidate(); }); }
            menu.Show(view, view.Width - 130, RowsTop - 6);
        }

        void PaintView(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; var c = Ch();
            if (c == null) return;
            Text = c.Name + " — sound settings";
            var rows = Place(c);
            var size = new Size((int)rows.Max(r => r.At.Right) + X0, (int)rows.Max(r => r.At.Bottom) + 12);
            if (ClientSize != size) ClientSize = size;   // the window fits this channel's rows
            g.DrawString("drag a bar · click on / off · right-click: automation clip or MIDI link", Small, DimB, X0, 9);
            if (c.Kind == "analog") RouteBox(g, view.Width - 130, 7, 118, "preset  ▾", false);
            foreach (var r in rows)
            {
                if (r.Head != null) { g.DrawString(r.Head, Small, DimB, r.At.X, r.At.Y + 6); continue; }
                bool live = r.Live == null || r.Live(c);
                ValueBar(g, r.At.X, r.At.Y, r.At.Width, r.At.Height, 0, r.Toggle ? (r.Get(c) > 0.5f ? 1 : 0) : r.Get(c), live ? r.Col : Raised);
                g.DrawString(r.Label(c), Font, live ? TxtB : DimB, new RectangleF(r.At.X + 8, r.At.Y, r.At.Width - 12, r.At.Height), Fit);
            }
        }
    }
    void RefreshAll(int pattern)
    {
        patBox.Items.Clear();
        foreach (var p in P.Patterns) patBox.Items.Add(p.Name + (p.Bars > 1 ? "  (" + p.Bars + " bars)" : ""));
        patBox.SelectedIndex = Math.Min(pattern, P.Patterns.Count - 1);
        bpm.Value = Math.Max(bpm.Minimum, Math.Min(bpm.Maximum, P.Bpm));
        selCh = Math.Max(0, Math.Min(selCh, P.Channels.Count - 1));
        if (groupFilter != null && !P.Channels.Any(c => c.Group == groupFilter)) groupFilter = null;   // that group is gone (deleted, undone, another project)
        RackHeight();
        Changed(false);
    }

    Pattern CurPat { get { return P.Patterns[Math.Max(0, Math.Min(patBox.SelectedIndex, P.Patterns.Count - 1))]; } }

    void AddChannel(Channel c) { Edit(() => AddChannel1(c)); }

    void AddChannel1(Channel c)
    {
        c.Insert = Math.Min(P.Channels.Count + 1, Project.Inserts - 1);
        lock (P) P.Channels.Add(c);
        selCh = P.Channels.Count - 1;
        RefreshAll(patBox.SelectedIndex);
        dirty = true;
    }

    static IEnumerable<Control> Descendants(Control root) { yield return root; foreach (Control c in root.Controls) foreach (var d in Descendants(c)) yield return d; }

    static bool IsExt(string path, string ext) { return path.EndsWith(ext, StringComparison.OrdinalIgnoreCase); }
    static bool IsMidi(string path) { return IsExt(path, ".mid") || IsExt(path, ".midi"); }
    static bool Droppable(string[] files) { return files != null && files.Any(f => IsExt(f, ".wav") || IsExt(f, ".mdaw") || IsMidi(f)); }

    // Files dropped on the window or handed to MiniDAW.exe: a project opens (the first one, if several); otherwise each WAV
    // becomes a sample channel, all in one undo step. Other files are ignored.
    void OpenFiles(string[] files)
    {
        var project = files.FirstOrDefault(f => IsExt(f, ".mdaw"));
        if (project != null) { if (ConfirmDiscard()) OpenPath(project); return; }
        foreach (var mid in files.Where(IsMidi)) ImportMidi(mid);
        var failed = new List<string>();
        Edit(() =>
        {
            foreach (var path in files.Where(f => IsExt(f, ".wav")))
            {
                var c = new Channel { Kind = "sample", Name = FreeName(Path.GetFileNameWithoutExtension(path)) };
                try { Wav.Load(path, c); }
                catch (Exception e) { failed.Add(Path.GetFileName(path) + ": " + e.Message); continue; }
                AddChannel1(c);
            }
        });
        if (failed.Count > 0) MessageBox.Show(this, "Could not load:\n" + string.Join("\n", failed), "MiniDAW");
    }

    void AddSample()
    {
        using (var d = new OpenFileDialog { Filter = "WAV audio (*.wav)|*.wav" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var c = new Channel { Kind = "sample", Name = Path.GetFileNameWithoutExtension(d.FileName) };
            try { Wav.Load(d.FileName, c); }
            catch (Exception e) { MessageBox.Show(this, "Could not load " + d.FileName + ":\n" + e.Message, "MiniDAW"); return; }
            AddChannel(c);
        }
    }

    bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var r = MessageBox.Show(this, "Save changes to the current project?", "MiniDAW", MessageBoxButtons.YesNoCancel);
        if (r == DialogResult.Cancel) return false;
        return r == DialogResult.No || SaveProject();
    }

    bool SaveProject()
    {
        using (var d = new SaveFileDialog { Filter = "MiniDAW project (*.mdaw)|*.mdaw", FileName = filePath ?? "song.mdaw" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return false;
            try { lock (P) P.Save(d.FileName); }
            catch (Exception e) { MessageBox.Show(this, "Save failed:\n" + e.Message, "MiniDAW"); return false; }
            filePath = d.FileName; dirty = false; Text = "MiniDAW — " + Path.GetFileName(filePath);
            Recent.Add(filePath); ClearAutosave();
            return true;
        }
    }

    // ---- audio export, like FL's render window: format and quality, plus optional stems (each mixer insert as its own file)
    static readonly string[] Formats = { "WAV", "FLAC (lossless, about half the size)", "MP3" }, Exts = { ".wav", ".flac", ".mp3" };
    static string[] Qualities(int f) { return f == 0 ? new[] { "16-bit", "24-bit", "32-bit float" } : f == 1 ? new[] { "16-bit", "24-bit" } : new[] { "128 kbps", "192 kbps", "256 kbps", "320 kbps" }; }
    static int exportFormat, exportQuality; static bool exportStems;   // remembered while MiniDAW runs

    bool AskExport()
    {
        using (var f = new Form { Text = "Export audio", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                                  StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(380, 178), BackColor = PanelBg, ForeColor = Txt, Font = Font })
        {
            f.HandleCreated += (s, e) => DarkTitle(f.Handle);
            var fmt = new DarkCombo { Left = 92, Top = 14, Width = 272 }; var q = new DarkCombo { Left = 92, Top = 46, Width = 272 };
            fmt.Items.AddRange(Formats);
            fmt.SelectedIndexChanged += (s, e) => { q.Items.Clear(); q.Items.AddRange(Qualities(fmt.SelectedIndex)); q.SelectedIndex = fmt.SelectedIndex == 2 ? 3 : 0; };
            fmt.SelectedIndex = exportFormat; q.SelectedIndex = Math.Min(exportQuality, q.Items.Count - 1);
            var stems = new CheckBox { Text = "Also save each mixer insert as its own file (stems)", Left = 14, Top = 82, AutoSize = true, Checked = exportStems };
            var what = new Label { Text = eng.SongMode ? "Exports the whole song (Song mode), 44.1 kHz stereo." : "Exports the current pattern (Pattern mode), 44.1 kHz stereo.", Left = 14, Top = 108, AutoSize = true, ForeColor = Dim };
            f.Controls.AddRange(new Control[] { new Label { Text = "Format", Left = 14, Top = 18, AutoSize = true }, fmt, new Label { Text = "Quality", Left = 14, Top = 50, AutoSize = true }, q, stems, what });
            var ok = Btn(f, "Export…", null); ok.DialogResult = DialogResult.OK; ok.AutoSize = false; ok.SetBounds(196, 138, 84, 28);
            var cancel = Btn(f, "Cancel", null); cancel.DialogResult = DialogResult.Cancel; cancel.AutoSize = false; cancel.SetBounds(286, 138, 78, 28);
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog(this) != DialogResult.OK) return false;
            exportFormat = fmt.SelectedIndex; exportQuality = q.SelectedIndex; exportStems = stems.Checked;
            return true;
        }
    }

    void ExportAudio()   // exports what Play would play: the whole song in Song mode, the current pattern in Pattern mode
    {
        if (!AskExport()) return;
        string ext = Exts[exportFormat];
        using (var d = new SaveFileDialog { Filter = Formats[exportFormat] + "|*" + ext, FileName = Path.GetFileNameWithoutExtension(filePath ?? "song") + ext })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                bool song = eng.SongMode; int pat = eng.CurPattern; float[] ever;
                var lr = Engine.Bounce(P.ExportCopy(), song, pat, out ever);
                WriteAudio(d.FileName, lr, exportFormat, exportQuality);
                var stems = new List<string>();
                if (exportStems)
                    for (int i = 1; i < Project.Inserts; i++)
                    {
                        if (ever[i] < 1e-4f) continue;   // nothing came through this insert
                        string stem = Path.Combine(Path.GetDirectoryName(d.FileName), Path.GetFileNameWithoutExtension(d.FileName) + " - " + SafeName(InsertName(i)) + ext);
                        WriteAudio(stem, Engine.BounceStem(P.ExportCopy(), song, pat, i, lr.Length / 2), exportFormat, exportQuality);
                        stems.Add(Path.GetFileName(stem));
                    }
                MessageBox.Show(this, "Exported " + (song ? "the song" : "the pattern") + " (" + (lr.Length / 2.0 / Engine.SR).ToString("0.0") + " s, " + Exts[exportFormat].Substring(1).ToUpper()
                    + " " + Qualities(exportFormat)[exportQuality] + ") to\n" + d.FileName + (stems.Count > 0 ? "\n\nplus " + stems.Count + " stems next to it:\n" + string.Join("\n", stems) : ""), "MiniDAW");
            }
            catch (Exception ex) { MessageBox.Show(this, "Export failed:\n" + ex.Message, "MiniDAW"); }
            finally { Cursor = Cursors.Default; }
        }
    }

    internal static void WriteAudio(string path, float[] lr, int format, int quality)
    {
        if (format == 0) File.WriteAllBytes(path, Wav.Make(lr, Engine.SR, 2, new[] { 16, 24, 32 }[quality]));
        else if (format == 1) File.WriteAllBytes(path, Flac.Encode(lr, Engine.SR, new[] { 16, 24 }[quality]));
        else MediaFoundation.Mp3(path, lr, Engine.SR, new[] { 128, 192, 256, 320 }[quality]);
    }

    string InsertName(int i)   // "Insert 3 (Snare)", from the channels that feed it
    {
        string from = string.Join(", ", P.Channels.Where(c => c.Insert == i).Select(c => c.Name));
        if (from.Length > 40) from = from.Substring(0, 40) + "…";
        return "Insert " + i + (from == "" ? "" : " (" + from + ")");
    }

    static string SafeName(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

    // A MIDI file's notes join this project as new channels and patterns (one undo step); the summary says where they went.
    void ImportMidi(string path)
    {
        string summary = null, error = null; int firstNew = P.Patterns.Count, chans = P.Channels.Count;
        try
        {
            var data = File.ReadAllBytes(path);
            Edit(() => { summary = Midi.Import(P, data, Path.GetFileNameWithoutExtension(path)); selCh = Math.Min(chans, P.Channels.Count - 1); RefreshAll(firstNew); dirty = true; });
        }
        catch (InvalidDataException e) { error = e.Message; }
        catch (IOException e) { error = e.Message; }
        catch (UnauthorizedAccessException e) { error = e.Message; }
        MessageBox.Show(this, error == null ? summary : "Could not import " + Path.GetFileName(path) + ":\n" + error, "MiniDAW");
    }

    void ExportMidi()   // like Export WAV: the song in Song mode, the current pattern in Pattern mode
    {
        using (var d = new SaveFileDialog { Filter = "MIDI file (*.mid)|*.mid", FileName = Path.GetFileNameWithoutExtension(filePath ?? "song") + ".mid" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllBytes(d.FileName, Midi.Export(P, eng.SongMode, eng.CurPattern)); }
            catch (Exception ex) { MessageBox.Show(this, "Export failed:\n" + ex.Message, "MiniDAW"); return; }
            MessageBox.Show(this, "Exported " + (eng.SongMode ? "the song" : "the pattern") + " as MIDI to\n" + d.FileName, "MiniDAW");
        }
    }

    void OpenProject(Control anchor)   // the Open… button: recent projects first (if any), then Browse… and Import MIDI…
    {
        var recent = Recent.Load().Where(File.Exists).ToList();   // files moved or deleted since are left out
        var m = new ContextMenuStrip { ShowItemToolTips = true };
        foreach (var path in recent)
        {
            string pth = path;   // foreach variables are shared between iterations in C# 5
            var item = m.Items.Add(Path.GetFileNameWithoutExtension(pth) + "   —   " + Path.GetFileName(Path.GetDirectoryName(pth)), null, (s, e) => { if (ConfirmDiscard()) OpenPath(pth); });
            item.ToolTipText = pth;
        }
        if (recent.Count > 0) m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Browse…", null, (s, e) => BrowseProject());
        m.Items.Add("Import MIDI file…", null, (s, e) =>
        {
            using (var d = new OpenFileDialog { Filter = "MIDI file (*.mid, *.midi)|*.mid;*.midi" })
                if (d.ShowDialog(this) == DialogResult.OK) ImportMidi(d.FileName);
        });
        m.Show(anchor, 0, anchor.Height);
    }

    void BrowseProject()
    {
        if (!ConfirmDiscard()) return;
        using (var d = new OpenFileDialog { Filter = "MiniDAW project (*.mdaw)|*.mdaw" })
            if (d.ShowDialog(this) == DialogResult.OK) OpenPath(d.FileName);
    }

    void OpenPath(string path)   // unsaved changes were already confirmed by the caller
    {
        Project p;
        try { p = Project.Load(path); }
        catch (Exception e)
        {
            MessageBox.Show(this, "Could not open project:\n" + e.Message, "MiniDAW");
            if (!File.Exists(path)) Recent.Remove(path);   // gone: stop offering it
            return;
        }
        eng.Proj = p; filePath = path; Text = "MiniDAW — " + Path.GetFileName(filePath); hist.Clear();
        RefreshAll(0); dirty = false; ClearAutosave();
        Recent.Add(path);
        if (p.Warnings.Count > 0) MessageBox.Show(this, "Some samples could not be loaded:\n" + string.Join("\n", p.Warnings), "MiniDAW");
    }

    // ---- channel rack: [mute] [name] [vol] [pan] [16 steps]
    // Rack extras: a graph editor for the selected channel's per-step velocity / pan / pitch (under the rows), and a group
    // filter that shows only one group's channels, like FL's channel groups.
    int graphMode; string groupFilter;
    int insDownY, insDownVal, insDir; bool insDragged;   // the insert box being dragged
    const int GraphH = 74, GraphBoxW = 128, GroupBoxW = 140;
    static readonly string[] GraphNames = { "off", "velocity", "pan", "pitch" };
    static readonly Color[] Swatches = { Color.FromArgb(232, 84, 96), Color.FromArgb(242, 170, 60), Color.FromArgb(226, 212, 86), Color.FromArgb(140, 212, 92), Color.FromArgb(72, 192, 124),
        Color.FromArgb(62, 182, 194), Color.FromArgb(84, 142, 232), Color.FromArgb(152, 112, 232), Color.FromArgb(222, 104, 182), Color.FromArgb(152, 158, 168) };

    List<int> RackRows() { var l = new List<int>(); for (int i = 0; i < P.Channels.Count; i++) if (groupFilter == null || P.Channels[i].Group == groupFilter) l.Add(i); return l; }
    int RackRow(int y) { var sh = RackRows(); int r = (y - Top0) / Row; return y >= Top0 && r < sh.Count ? sh[r] : -1; }   // the channel on the rack row at height y, or -1
    float GraphTop() { return Top0 + Math.Max(1, RackRows().Count) * Row + 4; }
    void RackHeight() { rack.Height = Top0 + Math.Max(1, RackRows().Count) * Row + 6 + (graphMode > 0 ? GraphH : 0); }
    static Color ChColor(Channel c) { return c.Color != 0 ? Color.FromArgb(c.Color) : On; }
    List<string> Groups() { return P.Channels.Where(c => c.Group != null).Select(c => c.Group).Distinct().OrderBy(x => x).ToList(); }
    float GraphBoxX() { return rack.Width - SwingW - 18 - GraphBoxW; }
    float GroupBoxX() { return GraphBoxX() - 8 - GroupBoxW; }

    void PaintRack(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Header(g, rack, "CHANNEL RACK");
        RouteBox(g, 120, 4, 86, browser.Visible ? "browser ◂" : "browser ▸", browser.Visible);
        g.DrawString("dot: mute · name: click = edit, double-click = settings, right-click = menu · bars: volume / pan · box: mixer insert · click steps", Small, DimB, 214, 5);
        float swx = rack.Width - SwingW - 10;   // swing bar, top right, like FL's swing knob on the channel rack
        ValueBar(g, swx, 4, SwingW, 15, 0, P.Swing, DeepBlue);
        g.DrawString("Swing  " + (int)Math.Round(P.Swing * 100) + "%", Small, TxtB, swx + 6, 5);
        RouteBox(g, GraphBoxX(), 4, GraphBoxW, "graph: " + GraphNames[graphMode], graphMode > 0);
        if (groupFilter != null || Groups().Count > 0) RouteBox(g, GroupBoxX(), 4, GroupBoxW, "group: " + (groupFilter ?? "all"), groupFilter != null);
        var pat = CurPat; int ps = eng.PlayStep; var shown = RackRows();
        for (int r = 0; r < shown.Count; r++)
        {
            int i = shown[r]; var c = P.Channels[i]; int y = Top0 + r * Row;
            Led(g, 6, y + 8, 10, !c.Mute);
            Round(g, i == selCh ? Sel : Raised, 22, y + 2, 110, Row - 4, 4);
            if (i == selCh || c.Color != 0) Round(g, ChColor(c), 22, y + 2, 3, Row - 4, 1.5f);   // its colour; the default shows only on the channel the piano roll edits
            g.DrawString(c.Name, Font, TxtB, new RectangleF(30, y + 2, 100, Row - 4), Fit);
            ValueBar(g, 136, y + 10, 60, 6, 0, c.Vol, On);
            float pan = 0.5f + c.Pan / 2;
            ValueBar(g, 202, y + 10, 40, 6, Math.Min(0.5f, pan), Math.Max(0.5f, pan), Blue);
            using (var pen = new Pen(Dim)) g.DrawLine(pen, 222, y + 7, 222, y + 18);   // centre mark
            Round(g, Raised, 248, y + 4, 28, Row - 8, 4);
            g.DrawString(c.Insert == 0 ? "M" : c.Insert.ToString(), Small, c.Insert == 0 ? DimB : TxtB, new RectangleF(248, y + 4, 28, Row - 8), Mid);
            var notes = pat.For(i);
            float sw = StepW();
            for (int s = 0; s < pat.Length; s++)
            {
                bool on = notes.Any(n => (int)n.Step == s), now = ps >= 0 && ps % pat.Length == s;
                var col = on ? (now ? ControlPaint.Light(ChColor(c), 0.6f) : ChColor(c)) : now ? Color.FromArgb(84, 94, 112) : s / 4 % 2 == 0 ? Cell2 : Cell1;
                float gap = s % Project.Steps == Project.Steps - 1 && s < pat.Length - 1 ? sw * 0.4f : Math.Max(2, sw * 0.15f);   // wider gap between bars
                float px = RackX + s * sw, pw = sw - gap;
                Round(g, col, px, y + 2, pw, Row - 4, 3);
                if (on) Round(g, Color.FromArgb(70, 255, 255, 255), px + 2, y + 3, pw - 4, 5, 2);   // a little shine on lit pads
            }
        }
        if (graphMode > 0 && selCh < P.Channels.Count) PaintGraph(g);
    }

    void PaintGraph(Graphics g)
    {
        var c = P.Channels[selCh]; float gy = GraphTop(), gh = GraphH - 16, top = gy + 4;
        Round(g, Color.FromArgb(23, 26, 32), 4, gy, rack.Width - 8, GraphH - 6, 5);
        g.DrawString(GraphNames[graphMode].ToUpper() + " — " + c.Name, Bold, TxtB, new RectangleF(12, gy + 6, 262, 16), Fit);
        g.DrawString(graphMode == 1 ? "drag across the steps · right-click: back to 100" : graphMode == 2 ? "up = right, down = left · right-click: centre" : "up to ±100 cents (a semitone) · right-click: none",
            Small, DimB, new RectangleF(12, gy + 26, 262, 32));
        var notes = CurPat.For(selCh); float sw = StepW(); var col = ChColor(c);
        for (int s = 0; s < CurPat.Length; s++)
        {
            float gap = s % Project.Steps == Project.Steps - 1 && s < CurPat.Length - 1 ? sw * 0.4f : Math.Max(2, sw * 0.15f), x = RackX + s * sw, pw = sw - gap;
            Round(g, Well, x, top, pw, gh, 2);
            var n = notes.FirstOrDefault(nn => (int)nn.Step == s);
            if (n == null) continue;   // only steps with a note have a value
            if (graphMode == 1) { float v = n.Vel / 127f; Round(g, col, x, top + gh * (1 - v), pw, gh * v, 2); continue; }
            float u = graphMode == 2 ? n.Pan : n.Fine / 100f, mid = top + gh / 2;
            Round(g, col, x, u >= 0 ? mid - gh / 2 * u : mid, pw, Math.Max(2, Math.Abs(u) * gh / 2), 1);
            using (var pen = new Pen(Dim)) g.DrawLine(pen, x, mid, x + pw, mid);   // the centre: no change
        }
    }

    void GraphSet(MouseEventArgs e)   // drag in the graph: set the value of the selected channel's note on the step under the mouse
    {
        int s = (int)Math.Floor((e.X - RackX) / StepW());
        if (s < 0 || s >= CurPat.Length || selCh >= P.Channels.Count) return;
        float v = Clamp01(1 - (e.Y - GraphTop() - 4) / (GraphH - 16)); bool reset = e.Button == MouseButtons.Right;
        foreach (var n in CurPat.For(selCh).Where(nn => (int)nn.Step == s))
        {
            if (graphMode == 1) n.Vel = reset ? 100 : Math.Max(1, (int)Math.Round(v * 127));
            else if (graphMode == 2) n.Pan = reset || Math.Abs(v - 0.5f) < 0.03f ? 0 : v * 2 - 1;   // a small dead zone snaps to the centre
            else n.Fine = reset || Math.Abs(v - 0.5f) < 0.03f ? 0 : (int)Math.Round((v * 2 - 1) * 100);
        }
        dirty = true; Changed();
    }

    float StepW() { return Math.Min(Cell, (rack.Width - RackX - 6) / (float)CurPat.Length); }   // 1-bar patterns keep full-size steps; longer ones shrink to fit

    void RackMove(object sender, MouseEventArgs e)
    {
        if (dragMode == "swing" && e.Button == MouseButtons.Left) { P.Swing = Clamp01((e.X - (rack.Width - SwingW - 10)) / (float)SwingW); Changed(); return; }
        if (dragMode == "graph" && e.Button != MouseButtons.None) { GraphSet(e); return; }
        if (dragMode == "insert" && e.Button != MouseButtons.None && dragCh < P.Channels.Count)
        {
            int to = Math.Max(0, Math.Min(Project.Inserts - 1, insDownVal + (insDownY - e.Y) / 5));   // 5 pixels per insert
            if (to != insDownVal || insDragged) { insDragged = true; if (P.Channels[dragCh].Insert != to) { P.Channels[dragCh].Insert = to; dirty = true; Changed(); } }
            return;
        }
        if (dragMode == null || e.Button != MouseButtons.Left || dragCh >= P.Channels.Count) return;
        var c = P.Channels[dragCh];
        if (dragMode == "vol") c.Vol = Math.Max(0, Math.Min(1, (e.X - 136) / 60f));
        else c.Pan = Math.Max(-1, Math.Min(1, (e.X - 222) / 20f));
        Changed();
    }

    void RackDown(object sender, MouseEventArgs e)
    {
        int dir = e.Button == MouseButtons.Right ? -1 : 1;
        if (e.Y < Top0)   // header: browser switch, swing bar, graph switch, group filter
        {
            if (e.X >= 120 && e.X < 206) ToggleBrowser();
            else if (e.Button == MouseButtons.Left && e.X >= rack.Width - SwingW - 10) { dragMode = "swing"; RackMove(sender, e); }
            else if (e.X >= GraphBoxX() && e.X < GraphBoxX() + GraphBoxW) { graphMode = (graphMode + dir + GraphNames.Length) % GraphNames.Length; RackHeight(); rack.Invalidate(); }
            else if (e.X >= GroupBoxX() && e.X < GroupBoxX() + GroupBoxW && (groupFilter != null || Groups().Count > 0))
            {
                var options = new List<string> { null }; options.AddRange(Groups());
                groupFilter = options[(Math.Max(0, options.IndexOf(groupFilter)) + dir + options.Count) % options.Count];
                RackHeight(); rack.Invalidate();
            }
            return;
        }
        if (graphMode > 0 && e.Y >= GraphTop())   // the graph editor
        {
            if (e.X >= RackX) { dragMode = "graph"; GraphSet(e); }
            return;
        }
        int i = RackRow(e.Y);
        if (i < 0) return;
        var c = P.Channels[i];
        if (e.X >= 246 && e.X < RackX)   // insert box: drag up / down to pick any insert; a click is +1, a right-click -1 (decided on mouse-up)
        {
            dragMode = "insert"; dragCh = i; insDownY = e.Y; insDownVal = c.Insert; insDragged = false; insDir = e.Button == MouseButtons.Right ? -1 : 1;
            return;
        }
        if (e.Button == MouseButtons.Right && e.X >= 20 && e.X < 134) { ChannelMenu(c, e.Location); return; }
        if (e.Button == MouseButtons.Right && e.X >= 134 && e.X < 246) { AutomateKnob(i, e.X < 198 ? Channel.LaneVol : Channel.LanePan); return; }   // volume / pan bar: a lane for it
        if (e.Button != MouseButtons.Left) return;
        if (e.X < 20) c.Mute = !c.Mute;
        else if (e.X < 134) { selCh = i; if (mixer.Visible) ToggleMixer(); }
        else if (e.X < 198) { dragMode = "vol"; dragCh = i; c.Vol = Math.Max(0, Math.Min(1, (e.X - 136) / 60f)); }
        else if (e.X < 246) { dragMode = "pan"; dragCh = i; c.Pan = Math.Max(-1, Math.Min(1, (e.X - 222) / 20f)); }
        else
        {
            int s = (int)Math.Floor((e.X - RackX) / StepW());
            if (s < 0 || s >= CurPat.Length) return;
            lock (P)
            {
                var notes = CurPat.For(i);
                if (notes.RemoveAll(n => (int)n.Step == s) == 0) notes.Add(new Note { Step = s, Key = 60, Len = 1 });
            }
        }
        Changed();
    }

    static ContextMenuStrip SoundMenu(Action<string> pick)   // the built-in sounds: synths, a separator, then drums
    {
        var m = new ContextMenuStrip();
        foreach (var kind in Channel.Sounds)
        {
            if (kind == "kick") m.Items.Add(new ToolStripSeparator());
            string k = kind;   // foreach variables are shared between iterations in C# 5
            m.Items.Add(Channel.Title(k), null, (s, e) => pick(k));
        }
        return m;
    }

    void SlicesToPattern(Channel c)   // each slice as a note where it falls at the song's tempo, so the loop plays back as it was (replaces this channel's notes)
    {
        int i = P.Channels.IndexOf(c); if (i < 0 || c.Slices == null) return;
        double sixteenth = 60.0 / P.Bpm / 4 * c.SampleRate;   // samples per 16th at the song's tempo
        lock (P)
        {
            var list = CurPat.For(i); list.Clear();
            for (int k = 0; k < c.Slices.Length && k < 68; k++)
            {
                double at = c.Slices[k] / sixteenth, end = (k + 1 < c.Slices.Length ? c.Slices[k + 1] : c.Sample.Length) / sixteenth;
                if (at >= CurPat.Length) break;
                list.Add(new Note { Step = Math.Round(at * 24) / 24, Key = 60 + k, Len = Math.Max(1 / 24.0, Math.Min(CurPat.Length - at, end - at)) });
            }
        }
    }

    // ---- browser (F8), like FL's: synth presets, and a folder of samples, MIDI files and projects. Click to hear, double-click to
    // add (or open), drag a sample onto the window to add it. The folder is remembered between runs.
    internal static string BrowserFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniDAW", "browser.txt");
    string browserDir; int browserScroll; Channel previewCh; Point browseDown; string dragFile;
    const int BrowseRow = 20, BrowseTop = 26;

    void ToggleBrowser() { browser.Visible = !browser.Visible; browserCache = null; rack.Invalidate(); }

    string BrowserDir()
    {
        if (browserDir == null)
        {
            try { browserDir = File.Exists(BrowserFile) ? File.ReadAllText(BrowserFile).Trim() : null; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (browserDir == null || !Directory.Exists(browserDir)) browserDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
        return browserDir;
    }

    class Item { public string Text, Path; public int Preset = -1; public bool Folder, Heading; }

    List<Item> browserCache;   // the rows, read once per folder (and again each time the panel opens)

    List<Item> BrowserItems()   // the rows, top to bottom
    {
        if (browserCache != null) return browserCache;
        var items = browserCache = new List<Item> { new Item { Text = "SYNTH PRESETS", Heading = true } };
        for (int k = 0; k < Channel.PresetNames.Length; k++) items.Add(new Item { Text = Channel.PresetNames[k], Preset = k });
        string dir = BrowserDir();
        items.Add(new Item { Text = "FOLDER  ·  click to change", Heading = true });
        items.Add(new Item { Text = "⌂ " + (Path.GetFileName(dir.TrimEnd('\\')) == "" ? dir : Path.GetFileName(dir.TrimEnd('\\'))), Path = dir, Folder = true });
        try
        {
            var parent = Directory.GetParent(dir);
            if (parent != null) items.Add(new Item { Text = "  ..", Path = parent.FullName, Folder = true });
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(x => x)) items.Add(new Item { Text = "  ▸ " + Path.GetFileName(sub), Path = sub, Folder = true });
            foreach (var f in Directory.GetFiles(dir).Where(f => IsExt(f, ".wav") || IsMidi(f) || IsExt(f, ".mdaw")).OrderBy(x => x))
                items.Add(new Item { Text = "  " + (IsExt(f, ".wav") ? "♪ " : IsMidi(f) ? "♫ " : "▣ ") + Path.GetFileName(f), Path = f });
        }
        catch (UnauthorizedAccessException) { items.Add(new Item { Text = "  (can't read this folder)", Heading = true }); }
        catch (IOException) { items.Add(new Item { Text = "  (can't read this folder)", Heading = true }); }
        return items;
    }

    Item BrowserAt(int y) { var items = BrowserItems(); int i = (y - BrowseTop) / BrowseRow + browserScroll; return y >= BrowseTop && i >= 0 && i < items.Count ? items[i] : null; }

    void PaintBrowser(object sender, PaintEventArgs e)
    {
        var g = e.Graphics; Header(g, browser, "BROWSER  (F8)");
        var items = BrowserItems(); browserScroll = Math.Min(browserScroll, Math.Max(0, items.Count - 5));
        for (int i = browserScroll, r = 0; i < items.Count; i++, r++)
        {
            float y = BrowseTop + r * BrowseRow; if (y > browser.Height) break;
            var it = items[i];
            if (it.Heading) { g.DrawString(it.Text, Small, DimB, 8, y + 4); continue; }
            if (it.Path != null && it.Path == dragFile) Round(g, Sel, 4, y, browser.Width - 8, BrowseRow - 2, 3);
            g.DrawString(it.Text, Font, it.Folder ? TxtB : it.Preset >= 0 ? new SolidBrush(Color.FromArgb(232, 160, 96)) : TxtB, new RectangleF(8, y, browser.Width - 12, BrowseRow), Fit);
        }
    }

    void BrowserDown(object sender, MouseEventArgs e)   // click: hear it, or go into a folder
    {
        var it = BrowserAt(e.Y); browseDown = e.Location; dragFile = null;
        if (it == null || it.Heading || e.Button != MouseButtons.Left) return;
        if (it.Folder && it.Text.StartsWith("⌂"))   // the folder line: pick another
        {
            using (var d = new FolderBrowserDialog { SelectedPath = BrowserDir() })
                if (d.ShowDialog(this) == DialogResult.OK) SetBrowserDir(d.SelectedPath);
            return;
        }
        if (it.Folder) { SetBrowserDir(it.Path); return; }
        if (previewCh != null) eng.Hush.Enqueue(previewCh);   // one preview at a time
        previewCh = null;
        if (it.Preset >= 0) previewCh = new Channel { Kind = "analog", Name = "preview", Syn = (float[])Channel.SynPresets[it.Preset].Clone() };
        else if (IsExt(it.Path, ".wav")) { var c = new Channel { Kind = "sample", Name = "preview" }; try { Wav.Load(it.Path, c); previewCh = c; } catch (InvalidDataException) { } catch (IOException) { } }
        if (previewCh != null) eng.Preview.Enqueue(new Engine.Voice { Ch = previewCh, Key = 60, Len = it.Preset >= 0 ? Engine.SR / 2 : int.MaxValue / 2 });
        dragFile = it.Path; browser.Invalidate();
    }

    void BrowserMove(object sender, MouseEventArgs e)   // drag a sample out: the window's own drop handler adds it
    {
        if (e.Button != MouseButtons.Left || dragFile == null || !IsExt(dragFile, ".wav")) return;
        if (Math.Abs(e.X - browseDown.X) + Math.Abs(e.Y - browseDown.Y) < 6) return;
        string f = dragFile; dragFile = null;
        browser.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { f }), DragDropEffects.Copy);
    }

    void BrowserOpen(object sender, MouseEventArgs e)   // double-click: add the preset or sample as a channel; import a MIDI file; open a project
    {
        var it = BrowserAt(e.Y);
        if (it == null || it.Heading || it.Folder) return;
        if (it.Preset >= 0) { AddChannel(new Channel { Kind = "analog", Name = FreeName(Channel.PresetNames[it.Preset]), Syn = (float[])Channel.SynPresets[it.Preset].Clone() }); return; }
        if (IsExt(it.Path, ".mdaw")) { if (ConfirmDiscard()) OpenPath(it.Path); return; }
        OpenFiles(new[] { it.Path });   // a WAV becomes a sample channel; a MIDI file is imported
    }

    void SetBrowserDir(string dir)
    {
        browserDir = dir; browserScroll = 0; browserCache = null; browser.Invalidate();
        try { Directory.CreateDirectory(Path.GetDirectoryName(BrowserFile)); File.WriteAllText(BrowserFile, dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // remembering it is a nicety
    }

    static Bitmap Swatch(Color c)   // a colour chip for the Colour menu
    {
        var b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b)) Round(g, c, 1, 1, 14, 14, 3);
        return b;
    }

    string FreeName(string title)   // "Pluck", then "Pluck 2", "Pluck 3"...
    {
        string n = title;
        for (int i = 2; P.Channels.Any(c => c.Name == n); i++) n = title + " " + i;
        return n;
    }

    void SetSound(Channel c, string kind)   // switch a channel to another built-in sound; a name that was just the old sound's title follows along
    {
        if (!P.Channels.Contains(c) || c.Kind == kind) return;
        Edit(() =>
        {
            if (c.Name == Channel.Title(c.Kind)) c.Name = FreeName(Channel.Title(kind));
            c.Kind = kind; c.Sample = null; c.SampleR = null; c.SamplePath = null; c.Knob = null;   // the new sound starts from its own built-in settings
            dirty = true; Changed();
        });
    }

    void ChannelMenu(Channel c, Point at)   // right-click a channel name, like FL's channel menu
    {
        var m = new ContextMenuStrip();
        var sound = new ToolStripMenuItem("Sound");
        foreach (ToolStripItem it in SoundMenu(kind => SetSound(c, kind)).Items.Cast<ToolStripItem>().ToList())
        {
            var mi = it as ToolStripMenuItem;
            if (mi != null) mi.Checked = mi.Text == Channel.Title(c.Kind);
            sound.DropDownItems.Add(it);
        }
        m.Items.Add(sound);
        m.Items.Add("Sound settings…", null, (s, e) => { int i = P.Channels.IndexOf(c); if (i >= 0) { selCh = i; Changed(false); ShowSoundSettings(); } });
        var colour = new ToolStripMenuItem("Colour");
        Action<int> paint = argb => { if (P.Channels.Contains(c)) Edit(() => { c.Color = argb; dirty = true; Changed(); }); };
        ((ToolStripMenuItem)colour.DropDownItems.Add("Default (orange)", Swatch(On), (s, e) => paint(0))).Checked = c.Color == 0;
        foreach (var sw in Swatches) { int argb = sw.ToArgb(); ((ToolStripMenuItem)colour.DropDownItems.Add("", Swatch(sw), (s, e) => paint(argb))).Checked = c.Color == argb; }
        m.Items.Add(colour);
        if (c.Kind == "sample" && c.Sample != null)   // slicing, like FL's Slicex
        {
            var slice = new ToolStripMenuItem("Slices" + (c.Slices != null ? " (" + c.Slices.Length + ")" : ""));
            Action<int[]> cut = starts => Edit(() => { c.Slices = starts; dirty = true; Changed(); });
            slice.DropDownItems.Add("Slice at the hits (transients)", null, (s, e) => cut(Channel.Transients(c.Sample, c.SampleRate)));
            foreach (int n in new[] { 4, 8, 16 }) { int parts = n; slice.DropDownItems.Add("Slice into " + n + " equal parts", null, (s, e) => cut(Enumerable.Range(0, parts).Select(k => (int)((long)c.Sample.Length * k / parts)).ToArray())); }
            if (c.Slices != null)
            {
                slice.DropDownItems.Add(new ToolStripSeparator());
                slice.DropDownItems.Add("Put the slices in this pattern (C5 up, at the song's tempo)", null, (s, e) => Edit(() => { SlicesToPattern(c); dirty = true; Changed(); }));
                slice.DropDownItems.Add("No slices (play the whole sample)", null, (s, e) => cut(null));
            }
            m.Items.Add(slice);
        }
        var group = new ToolStripMenuItem("Group");
        Action<string> join = name => { if (P.Channels.Contains(c)) Edit(() => { c.Group = name; dirty = true; RefreshAll(patBox.SelectedIndex); }); };
        ((ToolStripMenuItem)group.DropDownItems.Add("None", null, (s, e) => join(null))).Checked = c.Group == null;
        foreach (var gname in Groups()) { string gn = gname; ((ToolStripMenuItem)group.DropDownItems.Add(gn, null, (s, e) => join(gn))).Checked = c.Group == gn; }
        group.DropDownItems.Add(new ToolStripSeparator());
        group.DropDownItems.Add("New group…", null, (s, e) => { string n = Prompt("New group", c.IsDrum ? "Drums" : "Synths"); if (n != null) join(n); });
        m.Items.Add(group);
        m.Items.Add("Rename…", null, (s, e) =>
        {
            string n = Prompt("Rename channel", c.Name);
            if (n != null && P.Channels.Contains(c)) Edit(() => { c.Name = n; dirty = true; Changed(); });
        });
        m.Items.Add("Delete channel", null, (s, e) =>
        {
            int i = P.Channels.IndexOf(c);
            if (i < 0) return;   // the project was replaced (undo, open) since the menu opened
            Edit(() => { lock (P) P.RemoveChannel(i); RefreshAll(patBox.SelectedIndex); dirty = true; });
        }).Enabled = P.Channels.Count > 1;   // keep one channel so the piano roll always has something to edit
        m.Show(rack, at);
    }

    void PatternMenu()   // right-click the pattern list
    {
        var m = new ContextMenuStrip(); int k = Math.Max(0, patBox.SelectedIndex);
        m.Items.Add("Rename pattern…", null, (s, e) =>
        {
            string n = Prompt("Rename pattern", P.Patterns[k].Name);
            if (n != null && k < P.Patterns.Count) Edit(() => { P.Patterns[k].Name = n; RefreshAll(k); dirty = true; });
        });
        m.Items.Add("Delete pattern", null, (s, e) =>
        {
            if (k < P.Patterns.Count) Edit(() => { lock (P) P.RemovePattern(k); RefreshAll(Math.Min(k, P.Patterns.Count - 1)); dirty = true; });
        }).Enabled = P.Patterns.Count > 1;
        m.Items.Add(new ToolStripSeparator());
        for (int bars = 1; bars <= Pattern.MaxBars; bars++)   // pattern length
        {
            int nb = bars;   // C# 5 shares a for variable between iterations, so copy it for the lambda
            var item = (ToolStripMenuItem)m.Items.Add(nb == 1 ? "1 bar" : nb + " bars", null, (s, e) =>
            {
                if (k < P.Patterns.Count && P.Patterns[k].Bars != nb) Edit(() => { lock (P) P.Patterns[k].SetBars(nb); RefreshAll(k); dirty = true; });
            });
            item.Checked = P.Patterns[k].Bars == nb;
        }
        m.Show(patBox, 0, patBox.Height);
    }

    string Prompt(string title, string value)   // small text-input dialog; null = cancelled or empty
    {
        using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                                  StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(300, 72), ShowInTaskbar = false })
        {
            f.BackColor = PanelBg; f.ForeColor = Txt; f.HandleCreated += (s, e) => DarkTitle(f.Handle);
            var t = new TextBox { Text = value, Left = 10, Top = 10, Width = 280, BackColor = Well, ForeColor = Txt, BorderStyle = BorderStyle.FixedSingle };
            var ok = Btn(f, "OK", null); ok.DialogResult = DialogResult.OK; ok.AutoSize = false; ok.SetBounds(134, 40, 75, 26);
            var cancel = Btn(f, "Cancel", null); cancel.DialogResult = DialogResult.Cancel; cancel.AutoSize = false; cancel.SetBounds(215, 40, 75, 26);
            f.Controls.Add(t);
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog(this) == DialogResult.OK && t.Text.Trim() != "" ? t.Text.Trim() : null;
        }
    }

    // ---- piano roll for the selected channel
    // ---- piano roll view: scrolled and zoomed on its own; − and = still bring the computer keyboard's range into view
    int rollTop = 83, rollRows = 36;   // top key shown, and how many rows (12..128)
    double rollFrom, rollSpan;          // first 16th shown, and how many (0 = the whole pattern)
    double snap = 1, lastLen = 1;       // grid in 16ths (4 = a beat, 2/3 = 1/16 triplet, 0 = none); the length new notes get
    int laneMode, scaleRoot = -1, scaleType, stamp;   // bottom lane: 0 velocity, 1 pan, 2 fine pitch; scale highlighting (root -1 = off); chord stamp (0 = off)
    List<Note> dragNotes;               // notes just added, still being stretched by the drag
    double moveAt; int moveKey;         // where a note drag grabbed (16ths, key)
    const int SnapBoxX = 250, SnapBoxW = 118, ScaleBoxW = 136, StampBoxW = 108, ToolsBoxW = 64;
    static readonly string[] SnapNames = { "beat", "1/8", "1/16", "1/32", "1/64", "1/8 triplet", "1/16 triplet", "1/32 triplet", "none" };
    static readonly double[] SnapValues = { 4, 2, 1, 0.5, 0.25, 4 / 3.0, 2 / 3.0, 1 / 3.0, 0 };
    static readonly string[] ScaleNames = { "major", "minor", "harmonic minor", "major pentatonic", "minor pentatonic", "blues", "dorian", "mixolydian" };
    static readonly int[][] ScaleSteps = { new[] { 0, 2, 4, 5, 7, 9, 11 }, new[] { 0, 2, 3, 5, 7, 8, 10 }, new[] { 0, 2, 3, 5, 7, 8, 11 }, new[] { 0, 2, 4, 7, 9 },
        new[] { 0, 3, 5, 7, 10 }, new[] { 0, 3, 5, 6, 7, 10 }, new[] { 0, 2, 3, 5, 7, 9, 10 }, new[] { 0, 2, 4, 5, 7, 9, 10 } };
    static readonly string[] StampNames = { "off", "major", "minor", "7th", "maj7", "min7", "sus2", "sus4", "dim", "aug", "power", "octave" };
    static readonly int[][] StampSteps = { new[] { 0 }, new[] { 0, 4, 7 }, new[] { 0, 3, 7 }, new[] { 0, 4, 7, 10 }, new[] { 0, 4, 7, 11 }, new[] { 0, 3, 7, 10 },
        new[] { 0, 2, 7 }, new[] { 0, 5, 7 }, new[] { 0, 3, 6 }, new[] { 0, 4, 8 }, new[] { 0, 7 }, new[] { 0, 12 } };
    static readonly string[] KeyNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    int Lo { get { return rollTop - rollRows + 1; } }   // lowest key shown
    int Hi { get { return rollTop + 1; } }              // one above the highest
    double Span() { return rollSpan > 0 ? Math.Min(rollSpan, CurPat.Length) : CurPat.Length; }
    double From() { return Math.Max(0, Math.Min(rollFrom, CurPat.Length - Span())); }
    float RollCw() { return (float)((roll.Width - KeyW) / Span()); }
    float RollRh() { return (roll.Height - Top0 - VelH) / (float)rollRows; }
    float StepX(double s) { return (float)(KeyW + (s - From()) * RollCw()); }
    double XStep(float x) { return From() + (x - KeyW) / RollCw(); }
    float KeyY(int key) { return Top0 + (rollTop - key) * RollRh(); }
    int YKey(float y) { return rollTop - (int)Math.Floor((y - Top0) / RollRh()); }
    double Grid() { return snap > 0 ? snap : 1 / 24.0; }   // "none" still lands on 1/96 of a beat
    double SnapDown(double s) { double q = Grid(); return Math.Floor(s / q + 1e-6) * q; }
    double SnapNear(double s) { double q = Grid(); return Math.Round(s / q) * q; }
    bool InScale(int key) { return scaleRoot < 0 || ScaleSteps[scaleType].Contains((key - scaleRoot + 120) % 12); }
    void ShowKeys(int low) { rollTop = Math.Max(rollRows - 1, Math.Min(127, low + rollRows - 1)); }   // bring a key range into view from its bottom
    float ScaleBoxX() { return SnapBoxX + SnapBoxW + 6; }
    float StampBoxX() { return ScaleBoxX() + ScaleBoxW + 6; }
    float ToolsBoxX() { return StampBoxX() + StampBoxW + 6; }

    void PaintRoll(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        string name = P.Channels.Count > 0 ? P.Channels[selCh].Name : "";
        int nsel = P.Channels.Count > 0 ? Selected().Count : 0;
        Header(g, roll, "PIANO ROLL — " + name + (nsel > 0 ? "  (" + nsel + ")" : ""));
        RouteBox(g, SnapBoxX, 4, SnapBoxW, "snap: " + SnapNames[Array.IndexOf(SnapValues, snap)], snap != 1);
        RouteBox(g, ScaleBoxX(), 4, ScaleBoxW, "scale: " + (scaleRoot < 0 ? "off" : KeyNames[scaleRoot] + " " + ScaleNames[scaleType]), scaleRoot >= 0);
        RouteBox(g, StampBoxX(), 4, StampBoxW, "stamp: " + StampNames[stamp], stamp > 0);
        RouteBox(g, ToolsBoxX(), 4, ToolsBoxW, "tools ▾", false);
        g.DrawString("click: add · drag: move · edge: length · right-drag: select · wheel: scroll · Ctrl+wheel: zoom · Shift: across",
            Small, DimB, ToolsBoxX() + ToolsBoxW + 12, 5);
        int lowKey = 48 + 12 * pianoOctave;   // the computer keyboard's current range, right-aligned in the header
        string keysHint = "keys Z–M, Q–I play " + NoteName(lowKey) + "–" + NoteName(lowKey + 24) + "  (− / =)";
        g.DrawString(keysHint, Small, TxtB, roll.Width - g.MeasureString(keysHint, Small).Width - 8, 5);
        if (P.Channels.Count == 0) return;
        float rh = RollRh(), cw = RollCw(), vy = roll.Height - VelH;
        var clip0 = g.Clip; g.SetClip(new RectangleF(0, Top0, roll.Width, vy - Top0));
        using (var ivory = new SolidBrush(Color.FromArgb(222, 225, 231))) g.FillRectangle(ivory, 0, Top0, KeyW - 2, vy - Top0);   // white keys behind every row
        using (var seam = new Pen(Color.FromArgb(150, 156, 168)))
        using (var octave = new Pen(Color.FromArgb(48, 55, 67)))
            for (int r = 0; r < rollRows; r++)
            {
                int key = rollTop - r; bool black = "010100101010"[key % 12] == '1', inScale = InScale(key);
                float y = Top0 + r * rh;
                var row = scaleRoot < 0 ? (black ? Color.FromArgb(24, 27, 33) : Color.FromArgb(31, 35, 42))
                    : !inScale ? Color.FromArgb(19, 21, 26) : (key - scaleRoot + 120) % 12 == 0 ? Color.FromArgb(44, 50, 62) : Color.FromArgb(34, 39, 47);   // scale: its notes lit, the root more
                using (var b = new SolidBrush(row)) g.FillRectangle(b, KeyW, y, roll.Width, rh);
                if (black) { g.DrawLine(seam, 0, y + rh / 2, KeyW - 2, y + rh / 2); Round(g, Color.FromArgb(26, 28, 33), 0, y, (KeyW - 2) * 0.62f, rh, 2); }   // a short black key over the seam of two white ones
                else if (key % 12 == 0 || key % 12 == 5) g.DrawLine(seam, 0, y + rh, KeyW - 2, y + rh);   // B|C and E|F: white keys side by side
                if (key % 12 == 0)
                {
                    g.DrawLine(octave, KeyW, y + rh, roll.Width, y + rh);
                    if (rh >= 5) g.DrawString("C" + (key / 12), Small, Brushes.DimGray, new RectangleF(0, y + rh / 2 - 8, KeyW - 4, 16), new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });   // FL naming: MIDI 60 = C5
                }
            }
        g.Clip = clip0;
        using (var b = new SolidBrush(Color.FromArgb(22, 25, 30))) g.FillRectangle(b, 0, vy, roll.Width, VelH);   // the bottom lane
        using (var pen = new Pen(AppBg)) g.DrawLine(pen, 0, vy, roll.Width, vy);
        g.DrawString(new[] { "VEL", "PAN", "PITCH" }[laneMode] + " ▾", Small, DimB, 4, vy + VelH / 2 - 7);
        // grid: snap lines faint, 16ths, beats, bars stronger (snap lines only when they're far enough apart to see)
        double from = From(), to = from + Span(), q = Grid();
        if (q * cw >= 5 && Math.Abs(q - Math.Round(q)) > 1e-6)
            using (var pen = new Pen(Color.FromArgb(29, 33, 40)))
                for (double sx = Math.Ceiling(from / q) * q; sx <= to; sx += q) g.DrawLine(pen, StepX(sx), Top0, StepX(sx), roll.Height);
        for (int st = (int)Math.Ceiling(from); st <= to; st++)
        {
            if (cw < 4 && st % 4 != 0) continue;
            using (var pen = new Pen(st % Project.Steps == 0 ? Color.FromArgb(78, 87, 103) : st % 4 == 0 ? Color.FromArgb(50, 57, 69) : Color.FromArgb(37, 42, 51)))
                g.DrawLine(pen, StepX(st), Top0, StepX(st), roll.Height);
        }
        int ps = eng.PlayStep;
        if (ps >= 0) using (var b = new SolidBrush(Color.FromArgb(34, On))) g.FillRectangle(b, StepX(ps % CurPat.Length), Top0, cw, roll.Height);
        g.SetClip(new RectangleF(KeyW, Top0, roll.Width - KeyW, vy - Top0));
        for (int ch = 0; ch < Math.Min(CurPat.Notes.Count, P.Channels.Count); ch++)   // ghost notes: the other channels in this pattern, faint
        {
            if (ch == selCh) continue;
            var gc = ChColor(P.Channels[ch]);
            foreach (var n in CurPat.Notes[ch])
            {
                if (n.Key < Lo || n.Key >= Hi) continue;
                var rc = new RectangleF(StepX(n.Step) + 1, KeyY(n.Key) + 0.5f, (float)(n.Len * cw) - 2, rh - 1);
                Round(g, Color.FromArgb(38, gc), rc.X, rc.Y, rc.Width, rc.Height, 2);
                RoundEdge(g, Color.FromArgb(90, gc), 1, rc, 2);
            }
        }
        var mine = CurPat.For(selCh); var ccol = ChColor(P.Channels[selCh]);
        foreach (var n in mine)
        {
            if (n.Key < Lo || n.Key >= Hi) continue;
            var col = Color.FromArgb(70 + 185 * n.Vel / 127, ccol);   // softer notes are drawn fainter
            var rc = new RectangleF(StepX(n.Step) + 1, KeyY(n.Key) + 0.5f, (float)(n.Len * cw) - 2, rh - 1);
            Round(g, col, rc.X, rc.Y, rc.Width, rc.Height, 2.5f);
            RoundEdge(g, sel.Contains(n) ? Color.White : Color.FromArgb(150, 120, 60, 10), sel.Contains(n) ? 1.5f : 1, rc, 2.5f);
            if (n.Slide)   // a slide note: an arrow, since it bends the sounding note instead of starting one
                using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    g.FillPolygon(b, new[] { new PointF(rc.Right - 9, rc.Top + 2), new PointF(rc.Right - 2, rc.Top + rc.Height / 2), new PointF(rc.Right - 9, rc.Bottom - 2) });
        }
        g.Clip = clip0;
        var keepAa = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        float laneMid = roll.Height - 4 - (VelH - 8) / 2f;
        foreach (var n in mine)   // the lane: a stem with a knob per note (velocity up from the bottom; pan and pitch from the middle)
        {
            float vx = StepX(n.Step) + Math.Min(6, cw / 2);
            if (vx < KeyW || vx > roll.Width) continue;
            var col = Color.FromArgb(70 + 185 * n.Vel / 127, ccol);
            float y0 = laneMode == 0 ? roll.Height - 4 : laneMid, y1 = laneMode == 0 ? roll.Height - 4 - n.Vel / 127f * (VelH - 8)
                : laneMid - (laneMode == 1 ? n.Pan : n.Fine / 100f) * (VelH - 8) / 2;
            using (var pen = new Pen(col, 2)) g.DrawLine(pen, vx, y0, vx, y1);
            using (var b = new SolidBrush(col)) g.FillEllipse(b, vx - 3.5f, y1 - 3.5f, 7, 7);
        }
        if (laneMode > 0) using (var pen = new Pen(Color.FromArgb(60, 68, 82))) g.DrawLine(pen, KeyW, laneMid, roll.Width, laneMid);
        g.SmoothingMode = keepAa;
        if (boxFrom.HasValue)
        {
            using (var b = new SolidBrush(Color.FromArgb(30, Blue))) g.FillRectangle(b, BoxRect());
            using (var pen = new Pen(Blue) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, BoxRect());
        }
    }

    Rectangle BoxRect() { Point a = boxFrom.Value, b = boxTo.Value; return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)); }

    void FinishBox()   // notes the box touches become the selection; a right-click without a drag clears it
    {
        RectangleF r = BoxRect(); boxFrom = boxTo = null;
        sel = r.Width < 3 && r.Height < 3 ? new List<Note>()
            : CurPat.For(selCh).Where(n => new RectangleF(StepX(n.Step), KeyY(n.Key), (float)(n.Len * RollCw()), RollRh()).IntersectsWith(r)).ToList();
        roll.Invalidate();
    }

    List<Note> Selected() { var list = CurPat.For(selCh); return sel.Where(list.Contains).ToList(); }   // skips notes an undo or a pattern/channel switch left behind
    List<Note> Targets() { var s = Selected(); return s.Count > 0 ? s : CurPat.For(selCh).ToList(); }   // tools work on the selection, or the whole channel

    void CopyNotes(bool cut)   // Ctrl+C / Ctrl+X: the selection, or the channel's whole pattern when nothing is selected
    {
        var src = Targets();
        if (src.Count == 0) return;
        clip = src.Select(n => n.Clone()).ToList();
        if (cut) Edit(() => { lock (P) CurPat.For(selCh).RemoveAll(src.Contains); sel.Clear(); Changed(); });
    }

    // Ctrl+V: at the copied steps, onto the selected channel and pattern (moved left by whole bars if it would all land past the end).
    // Ctrl+B: duplicate the selection (or the whole channel) right after itself.
    void PasteNotes(bool duplicate)
    {
        var src = duplicate ? Targets() : clip;
        if (src.Count == 0) return;
        double first = src.Min(n => n.Step);
        double off = duplicate ? Pattern.DupOffset(src) : first >= CurPat.Length ? -(first - first % Project.Steps) : 0;
        List<Note> added = null;
        Edit(() => { lock (P) added = CurPat.Paste(selCh, src, off); Changed(); });
        sel = added;   // the pasted notes stay selected, so Ctrl+B again keeps extending
    }

    void DeleteNotes()
    {
        var src = Selected();
        if (src.Count > 0) Edit(() => { lock (P) CurPat.For(selCh).RemoveAll(src.Contains); sel.Clear(); Changed(); });
    }

    void SetLane(MouseEventArgs e)   // bottom lane: every note starting in the snap cell under the mouse (a chord moves together)
    {
        double cell = SnapDown(XStep(e.X)), q = Grid();
        float v = Clamp01((roll.Height - 4 - e.Y) / (float)(VelH - 8)); bool reset = e.Button == MouseButtons.Right, any = false;
        lock (P)
            foreach (var n in CurPat.For(selCh))
            {
                if (n.Step < cell - 1e-9 || n.Step >= cell + q - 1e-9) continue;
                if (laneMode == 0) n.Vel = reset ? 100 : Math.Max(1, (int)Math.Round(v * 127));
                else if (laneMode == 1) n.Pan = reset || Math.Abs(v - 0.5f) < 0.04f ? 0 : v * 2 - 1;
                else n.Fine = reset || Math.Abs(v - 0.5f) < 0.04f ? 0 : (int)Math.Round((v * 2 - 1) * 100);
                any = true;
            }
        if (any) { dirty = true; Changed(); }
    }

    void RollDown(object sender, MouseEventArgs e)
    {
        if (e.Y < Top0) { RollHeader(e); return; }
        if (P.Channels.Count == 0) return;
        if (e.Y >= roll.Height - VelH)
        {
            if (e.X < KeyW) { laneMode = (laneMode + 1) % 3; roll.Invalidate(); return; }   // the lane's label switches velocity → pan → pitch
            dragMode = "vel"; SetLane(e); return;
        }
        if (e.Button == MouseButtons.Right) { boxFrom = boxTo = e.Location; roll.Invalidate(); return; }
        if (e.Button != MouseButtons.Left || e.X < KeyW) return;
        double at = XStep(e.X); int key = YKey(e.Y);
        if (at < 0 || at >= CurPat.Length || key < 0 || key > 127) return;
        lock (P)
        {
            var list = CurPat.For(selCh);
            var hit = list.LastOrDefault(n => n.Key == key && at >= n.Step && at < n.Step + n.Len);
            if (hit != null)   // drag the body to move it (with the rest of the selection), the right edge to change its length; a click without a drag removes it
            {
                resizing = e.X >= StepX(hit.Step + hit.Len) - Math.Min(8, RollCw() * hit.Len / 3);
                moving = !resizing && sel.Contains(hit) ? Selected() : new List<Note> { hit };
                moveFrom = moving.ToDictionary(n => n, n => n.Clone());
                moveHit = hit; moveAt = at; moveKey = key; moved = false;
            }
            else   // a new note, or a whole chord with the stamp on, as long as the last one made
            {
                double start = SnapDown(at), len = Math.Min(Math.Max(Grid(), lastLen), CurPat.Length - start);
                dragNotes = new List<Note>();
                foreach (int iv in StampSteps[stamp])
                {
                    if (key + iv > 127) continue;
                    var n = new Note { Step = start, Key = key + iv, Len = len };
                    list.RemoveAll(x => x.Key == n.Key && Math.Abs(x.Step - start) < 1e-6);
                    list.Add(n); dragNotes.Add(n);
                    eng.Preview.Enqueue(new Engine.Voice { Ch = P.Channels[selCh], Key = n.Key, Len = Engine.SR / 4 });
                }
            }
        }
        Changed();
    }

    void RollHeader(MouseEventArgs e)   // the header's switches: snap, scale, stamp, tools
    {
        var m = new ContextMenuStrip(); float x = e.X;
        if (x >= SnapBoxX && x < SnapBoxX + SnapBoxW)
            for (int k = 0; k < SnapNames.Length; k++) { double v = SnapValues[k]; ((ToolStripMenuItem)m.Items.Add(SnapNames[k], null, (s, a) => { snap = v; roll.Invalidate(); })).Checked = snap == v; }
        else if (x >= ScaleBoxX() && x < ScaleBoxX() + ScaleBoxW)
        {
            ((ToolStripMenuItem)m.Items.Add("off", null, (s, a) => { scaleRoot = -1; roll.Invalidate(); })).Checked = scaleRoot < 0;
            for (int root = 0; root < 12; root++)
            {
                var item = new ToolStripMenuItem(KeyNames[root]) { Checked = scaleRoot == root };
                for (int t = 0; t < ScaleNames.Length; t++) { int rr = root, tt = t; ((ToolStripMenuItem)item.DropDownItems.Add(KeyNames[root] + " " + ScaleNames[t], null, (s, a) => { scaleRoot = rr; scaleType = tt; roll.Invalidate(); })).Checked = scaleRoot == root && scaleType == t; }
                m.Items.Add(item);
            }
        }
        else if (x >= StampBoxX() && x < StampBoxX() + StampBoxW)
            for (int k = 0; k < StampNames.Length; k++) { int v = k; ((ToolStripMenuItem)m.Items.Add(StampNames[k], null, (s, a) => { stamp = v; roll.Invalidate(); })).Checked = stamp == v; }
        else if (x >= ToolsBoxX() && x < ToolsBoxX() + ToolsBoxW)
        {
            string on = Selected().Count > 0 ? " the selected notes" : " all notes";
            m.Items.Add("Quantize" + on + " to the snap (Ctrl+Q)", null, (s, a) => Tool("quantize"));
            m.Items.Add("Quantize starts and lengths", null, (s, a) => Tool("quantize both"));
            m.Items.Add("Chop into snap-sized pieces", null, (s, a) => Tool("chop"));
            m.Items.Add("Strum chords (each higher note a little later)", null, (s, a) => Tool("strum"));
            m.Items.Add("Arpeggiate chords at the snap", null, (s, a) => Tool("arp"));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Make slide notes (they bend the sounding note)", null, (s, a) => Tool("slide"));
            m.Items.Add("Make normal notes", null, (s, a) => Tool("unslide"));
        }
        else return;
        m.Show(roll, (int)x, Top0 - 4);
    }

    // Piano roll tools, like FL's Tools menu: quantize, chop, strum, arpeggiate, slide. One undo step each; the results stay selected.
    internal void Tool(string what)
    {
        if (P.Channels.Count == 0) return;
        var src = Targets();
        if (src.Count == 0) return;
        Edit(() =>
        {
            lock (P)
            {
                var list = CurPat.For(selCh); double q = Grid(), len = CurPat.Length; var result = new List<Note>();
                if (what.StartsWith("quantize"))
                    foreach (var n in src)
                    {
                        n.Step = Math.Max(0, Math.Min(len - q, SnapNear(n.Step)));
                        if (what == "quantize both") n.Len = Math.Max(q, SnapNear(n.Len));
                        n.Len = Math.Min(n.Len, len - n.Step); result.Add(n);
                    }
                else if (what == "chop")
                    foreach (var n in src)
                    {
                        if (n.Len <= q + 1e-9) { result.Add(n); continue; }
                        list.Remove(n);
                        for (double t = n.Step; t < n.Step + n.Len - 1e-9; t += q) { var piece = n.Clone(); piece.Step = t; piece.Len = Math.Min(q, n.Step + n.Len - t); list.Add(piece); result.Add(piece); }
                    }
                else if (what == "strum" || what == "arp")
                    foreach (var chord in src.GroupBy(n => Math.Round(n.Step * 96)))   // notes starting together form a chord
                    {
                        var keys = chord.OrderBy(n => n.Key).ToList();
                        if (keys.Count < 2) { result.AddRange(keys); continue; }
                        if (what == "strum")
                            for (int k = 0; k < keys.Count; k++) { double d = Math.Min(0.25 * k, keys[k].Len - 1 / 24.0); keys[k].Step += d; keys[k].Len -= d; result.Add(keys[k]); }   // 1/64 apart, ends kept
                        else
                        {
                            double start = keys[0].Step, end = keys.Max(n => n.Step + n.Len);
                            foreach (var n in keys) list.Remove(n);
                            int k = 0;
                            for (double t = start; t < end - 1e-9; t += q, k++) { var a = keys[k % keys.Count].Clone(); a.Step = t; a.Len = Math.Min(q, end - t); list.Add(a); result.Add(a); }
                        }
                    }
                else foreach (var n in src) { n.Slide = what == "slide"; result.Add(n); }
                sel = result;
            }
            dirty = true; Changed();
        });
    }

    void RollMove(object sender, MouseEventArgs e)
    {
        if (boxFrom.HasValue && e.Button == MouseButtons.Right) { boxTo = e.Location; roll.Invalidate(); return; }
        if (dragMode == "vel" && e.Button != MouseButtons.None) { SetLane(e); return; }
        if (moving != null && e.Button == MouseButtons.Left)
        {
            if (!resizing) { MoveNotes(e); return; }
            double nl = Math.Max(Grid(), Math.Min(CurPat.Length - moveHit.Step, SnapNear(XStep(e.X)) - moveHit.Step));
            if (Math.Abs(nl - moveHit.Len) > 1e-9) { moveHit.Len = nl; lastLen = nl; moved = true; Changed(); }
            return;
        }
        if (dragNotes == null || e.Button != MouseButtons.Left) return;
        double len2 = Math.Max(Grid(), Math.Min(CurPat.Length - dragNotes[0].Step, SnapDown(XStep(e.X)) + Grid() - dragNotes[0].Step));
        if (Math.Abs(len2 - dragNotes[0].Len) > 1e-9) { foreach (var n in dragNotes) n.Len = len2; lastLen = len2; Changed(); }
    }

    void MoveNotes(MouseEventArgs e)   // shift every grabbed note by the mouse's offset, snapping the grabbed note's start; kept inside the pattern and the MIDI range
    {
        var o = moveFrom[moveHit];
        double ds = SnapNear(o.Step + XStep(e.X) - moveAt) - o.Step; int dk = YKey(e.Y) - moveKey;
        Pattern.ClampMove(moveFrom, CurPat.Length, ref ds, ref dk);
        bool newPitch = moveHit.Key != o.Key + dk;
        foreach (var kv in moveFrom) { kv.Key.Step = kv.Value.Step + ds; kv.Key.Key = kv.Value.Key + dk; }
        if (Math.Abs(ds) > 1e-9 || dk != 0) moved = true;
        if (newPitch) eng.Preview.Enqueue(new Engine.Voice { Ch = P.Channels[selCh], Key = moveHit.Key, Len = Engine.SR / 8 });   // hear the new pitch
        Changed();
    }

    void FinishMove()
    {
        lock (P)
        {
            var list = CurPat.For(selCh);
            if (!moved) { list.Remove(moveHit); sel.Remove(moveHit); }   // a plain click still removes the note, as before
            else list.RemoveAll(x => !moveFrom.ContainsKey(x) && moveFrom.Keys.Any(m => Math.Abs(m.Step - x.Step) < 1e-6 && m.Key == x.Key));   // a moved note replaces one it lands on
        }
        moving = null; moveFrom = null; moveHit = null;
        Changed();
    }

    void RollWheel(object sender, MouseEventArgs e)   // wheel: scroll keys; Ctrl: zoom keys; Shift: scroll across; Ctrl+Shift: zoom across
    {
        int notches = e.Delta / 120; if (notches == 0) notches = Math.Sign(e.Delta);
        var mods = ModifierKeys; bool ctrl = (mods & Keys.Control) != 0, shift = (mods & Keys.Shift) != 0;
        if (ctrl && shift)
        {
            double at = XStep(e.X), span = Math.Max(1, Math.Min(CurPat.Length, Span() * Math.Pow(0.8, notches)));
            rollSpan = span >= CurPat.Length ? 0 : span;
            rollFrom = Math.Max(0, at - (e.X - KeyW) / (double)(roll.Width - KeyW) * span);   // the 16th under the mouse stays put
        }
        else if (shift) rollFrom = Math.Max(0, Math.Min(CurPat.Length - Span(), From() - notches * Span() / 8));
        else if (ctrl)
        {
            int mid = rollTop - rollRows / 2;
            rollRows = Math.Max(12, Math.Min(128, (int)Math.Round(rollRows * Math.Pow(0.8, notches))));
            rollTop = Math.Max(rollRows - 1, Math.Min(127, mid + rollRows / 2));
        }
        else rollTop = Math.Max(rollRows - 1, Math.Min(127, rollTop + 3 * notches));
        roll.Invalidate();
    }
    // ---- mixer: master + inserts, each with fader, pan, mute, meter and effect slots
    void ToggleMixer()
    {
        mixer.Visible = !mixer.Visible;
        roll.Visible = !mixer.Visible;
        mixBtn.Text = mixer.Visible ? "Piano roll (F9)" : "Mixer (F9)";
    }

    static float Clamp01(float v) { return Math.Max(0, Math.Min(1, v)); }

    // Like FL's right-click > "Create automation clip": one lane per control, starting flat at its current value, shown in the playlist.
    void AutomateControl(int ins, int slot, int param) { var l = new Lane { Ins = ins, Slot = slot, Param = param }; ControlMenu(l, mixer, mixer.PointToClient(Cursor.Position)); }   // a mixer control
    void AutomateKnob(int ch, int param) { var l = new Lane { Ch = ch, Param = param }; ControlMenu(l, rack, rack.PointToClient(Cursor.Position)); }                               // a channel's sound setting

    void Automate(Lane want)   // a lane for the control (flat at its current value) and an automation clip for it on a free track, then its editor
    {
        int k = P.Automation.FindIndex(l => l.SameTarget(want));
        if (k < 0)
        {
            float v = want.Get(P);
            for (int s = 0; s < want.Values.Length; s++) want.Values[s] = v;
            lock (P)
            {
                P.Automation.Add(want); k = P.Automation.Count - 1;
                P.Clips.Add(new Clip { Lane = k, Pattern = -1, Track = P.FreeTrack(), Start = 0, Len = Math.Max(4, P.SongBars()) * Project.Steps });
            }
            dirty = true;
        }
        Changed(false); ShowAutomation(k);
    }

    // Right-click on any control that can move with the song: make an automation clip, or link it to a knob on a MIDI controller.
    internal void ControlMenu(Lane target, Control at, Point where)
    {
        var m = new ContextMenuStrip(); string name = target.Name(P);
        m.Items.Add("Create automation clip for " + name, null, (s, e) => Edit(() => Automate(target)));
        var linked = P.Links.FirstOrDefault(lk => lk.Target.SameTarget(target));
        if (linked == null) m.Items.Add("Link to MIDI controller… (then move a knob)", null, (s, e) => { learnFor = target; learning = name; eng.LearnTarget = target; Text = "MiniDAW — move a knob or slider on your MIDI controller to link it to " + name + "  (Esc cancels)"; });
        else m.Items.Add("Unlink from MIDI controller " + linked.Cc, null, (s, e) => Edit(() => { lock (P) P.Links.Remove(linked); dirty = true; }));
        m.Show(at, where);
    }
    string learning; Lane learnFor;   // the control waiting for a MIDI knob (its name shows in the title)

    internal void LearnArrived()   // called by the timer: the engine heard a controller while learning
    {
        int cc;
        while (eng.Learned.TryDequeue(out cc))
        {
            var t = learnFor; if (t == null) continue;
            int c = cc;   // one knob drives one control: an older link to either is replaced
            Edit(() => { lock (P) { P.Links.RemoveAll(lk => lk.Target.SameTarget(t) || lk.Cc == c); P.Links.Add(new Link { Cc = c, Target = t }); } dirty = true; });
            Text = "MiniDAW — linked MIDI controller " + c + " to " + learning; learning = null; learnFor = null;
        }
    }

    static int Next(IEnumerable<int> options, int cur, int dir)   // step through options, wrapping around
    {
        var o = options.ToArray(); int k = Math.Max(0, Array.IndexOf(o, cur));
        return o[(k + dir + o.Length) % o.Length];
    }

    static void RouteBox(Graphics g, float x, float y, float w, string text, bool on)   // a pill-shaped switch
    {
        Round(g, on ? Sel : Raised, x, y, w, 14, 7);
        g.DrawString(text, Small, on ? TxtB : DimB, new RectangleF(x + 5, y, w - 7, 14), Fit);
    }

    // The mixer shows the master on the left and a window of the 125 inserts beside it, scrolled with the wheel.
    int mixFrom = 1;   // the first insert shown after the master
    int MixShown() { float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st); return Math.Max(2, (int)((mixer.Width - 8) / sw)); }
    float StripX(int i) { float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st); return 4 + sw * (i == 0 ? 0 : i - mixFrom + 1); }
    int StripAt(float x)   // the insert whose strip is at x, or -1
    {
        float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st);
        int k = (int)Math.Floor((x - 4) / sw), i = k == 0 ? 0 : mixFrom + k - 1;
        return k < 0 || k >= MixShown() || i >= Project.Inserts ? -1 : i;
    }
    void MixerWheel(object sender, MouseEventArgs e)
    {
        int notches = e.Delta / 120; if (notches == 0) notches = Math.Sign(e.Delta);
        mixFrom = Math.Max(1, Math.Min(Project.Inserts - MixShown() + 1, mixFrom - 2 * notches)); mixer.Invalidate();
    }

    void MixGeom(out float sw, out float fy, out float fh, out float slotsTop)
    {
        sw = Math.Max(140, (mixer.Width - 8) / 9f);   // 9 strips across a 1380-pixel window, as before; narrower windows show fewer
        slotsTop = mixer.Height - Insert.SlotCount * HeadH - ParamsH - 4;
        fy = Top0 + StripHead;
        fh = Math.Max(30, slotsTop - fy - 22);   // leave room for the dB readout
    }

    // Slot list: one short row per slot; the open effect's three settings sit right under its row (one open effect per insert).
    readonly int[] openSlot = new int[Project.Inserts];   // view state only, not saved
    bool IsOpen(int i, int s) { return s == openSlot[i] && P.Mixer[i].Slots[s] != null; }
    int slotDragIns = -1, slotDragFrom, slotDownY, slotDropY; bool slotDragging;   // an effect row being clicked or dragged to a new slot

    int SlotAtY(int i, float y, float slotsTop)   // the slot whose row (or open settings) is at height y, clamped to the list
    {
        for (int s = Insert.SlotCount - 1; s >= 0; s--) if (y >= SlotY(i, s, slotsTop)) return s;
        return 0;
    }

    void FinishSlotDrag(MouseEventArgs e)   // a click opens the effect (or shows its menu if it's already open); a drag moves it
    {
        int i = slotDragIns, from = slotDragFrom; slotDragIns = -1;
        if (!slotDragging) { if (!IsOpen(i, from)) openSlot[i] = from; else ShowFxMenu(i, from, e.Location); mixer.Invalidate(); return; }
        slotDragging = false;
        float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st);
        int to = SlotAtY(i, e.Y, st);
        if (to != from) { int[] where; lock (P) where = P.MoveEffect(i, from, to); openSlot[i] = where[openSlot[i]]; dirty = true; }
        Changed();
    }
    float SlotY(int i, int s, float slotsTop) { return slotsTop + s * HeadH + (s > openSlot[i] && IsOpen(i, openSlot[i]) ? ParamsH : 0); }

    void PaintMixer(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Header(g, mixer, "MIXER");
        int shown = MixShown(), last = Math.Min(Project.Inserts - 1, mixFrom + shown - 2);
        g.DrawString("inserts " + mixFrom + "–" + last + " of " + (Project.Inserts - 1) + " (wheel to scroll) · slots: click = add / open, drag = reorder, bar = wet/dry, dot = on/off · \"out\": master, sends and sidechain · right-click a control: automate",
            Small, DimB, new RectangleF(64, 5, mixer.Width - 70, 14), Fit);
        float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st);
        for (int k = 0; k < shown; k++)
        {
            int i = k == 0 ? 0 : mixFrom + k - 1;
            if (i >= Project.Inserts) break;
            var ins = P.Mixer[i]; float sx = 4 + k * sw, w = sw - 6;
            Round(g, i == 0 ? Color.FromArgb(36, 41, 50) : Color.FromArgb(32, 36, 44), sx, Top0 - 2, w, mixer.Height - Top0 - 2, 6);
            g.DrawString(i == 0 ? "Master" : "Insert " + i, Bold, i == 0 ? new SolidBrush(On) : TxtB, sx + 5, Top0);
            string from = string.Join(", ", P.Channels.Where(c => c.Insert == i).Select(c => c.Name));
            g.DrawString(from, Small, DimB, new RectangleF(sx + 5, Top0 + 17, w - 8, 14), Fit);
            Led(g, sx + 4, Top0 + 36, 10, !ins.Mute);
            float pw = w - 24, pan = 0.5f + ins.Pan / 2;
            ValueBar(g, sx + 20, Top0 + 38, pw, 6, Math.Min(0.5f, pan), Math.Max(0.5f, pan), Blue);
            using (var pen = new Pen(Dim)) g.DrawLine(pen, sx + 20 + pw / 2, Top0 + 35, sx + 20 + pw / 2, Top0 + 47);
            if (i > 0)   // routing: where it goes (master, sends) and its sidechain; the first send's level as a bar
            {
                float ry = Top0 + 52, half = (w - 8) / 2; int ns = ins.Sends.Count;
                string outTo = ins.ToMaster ? (ns == 0 ? "out: master" : "out: master + " + ns + (ns == 1 ? " send" : " sends")) : ns == 0 ? "out: nowhere" : "out: " + ns + (ns == 1 ? " send" : " sends") + " only";
                RouteBox(g, sx + 4, ry, w - 8, outTo + "  ▾", ns > 0 || !ins.ToMaster);
                RouteBox(g, sx + 4, ry + 16, half - 2, ins.Sidechain > 0 ? "sc ← " + ins.Sidechain : "sc: off", ins.Sidechain > 0);
                if (ns > 0)
                {
                    ValueBar(g, sx + 4 + half + 2, ry + 16, half - 2, 14, 0, ins.SendLevel, DeepBlue);
                    g.DrawString("→" + ins.SendTo + " " + (int)Math.Round(ins.SendLevel * 100) + "%", Small, TxtB, new RectangleF(sx + 8 + half, ry + 16, half - 6, 14), Fit);
                }
            }
            // fader: dB ticks, a slim track lit below the handle, and the handle; the whole area still takes clicks
            float fw = w - 26, cx = sx + 4 + fw / 2, hy = fy + fh * (1 - ins.Vol);
            using (var pen = new Pen(Line))
                for (float t = 1; t > 0.03f; t /= 2) { float ty = fy + fh * (1 - t); g.DrawLine(pen, cx - 14, ty, cx - 7, ty); g.DrawLine(pen, cx + 7, ty, cx + 14, ty); }   // 0, −6, −12… dB
            Round(g, Well, cx - 3, fy, 6, fh, 3);
            var keep = g.Save();
            g.SetClip(new RectangleF(cx - 3, hy, 6, fy + fh - hy), CombineMode.Intersect);
            Round(g, ins.Mute ? Dim : On, cx - 3, fy, 6, fh, 3);
            g.Restore(keep);
            float hw = Math.Min(fw - 4, 34);
            Round(g, Color.FromArgb(70, 0, 0, 0), cx - hw / 2, hy - 6, hw, 14, 3);   // shadow
            Round(g, Color.FromArgb(206, 211, 220), cx - hw / 2, hy - 7, hw, 14, 3);
            using (var pen = new Pen(Color.FromArgb(70, 76, 88))) g.DrawLine(pen, cx - hw / 2 + 4, hy, cx + hw / 2 - 4, hy);
            // level meter: green → yellow → red, lit up to the peak
            float pk = Math.Min(1, eng.Peaks[i]), mx = sx + w - 16;
            Round(g, Well, mx, fy, 8, fh, 3);
            if (pk > 0)
            {
                var keepM = g.Save();
                g.SetClip(new RectangleF(mx, fy + fh * (1 - pk), 8, fh * pk), CombineMode.Intersect);
                using (var grad = new LinearGradientBrush(new RectangleF(mx, fy - 1, 8, fh + 2), Color.Red, Green, LinearGradientMode.Vertical))
                {
                    grad.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(255, 70, 70), Color.FromArgb(255, 210, 70), Green, Green }, Positions = new[] { 0f, 0.18f, 0.45f, 1f } };
                    var am = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = RoundPath(new RectangleF(mx, fy, 8, fh), 3)) g.FillPath(grad, path);
                    g.SmoothingMode = am;
                }
                g.Restore(keepM);
            }
            g.DrawString(ins.Vol <= 0 ? "-inf dB" : (20 * Math.Log10(ins.Vol)).ToString("0.0") + " dB", Small, ins.Mute ? DimB : TxtB, new RectangleF(sx + 4, fy + fh + 3, fw, 16), new StringFormat { Alignment = StringAlignment.Center });
            for (int s = 0; s < Insert.SlotCount; s++)
            {
                var fx = ins.Slots[s]; float sy = SlotY(i, s, st); bool open = IsOpen(i, s);
                Round(g, fx == null ? Color.FromArgb(38, 43, 52) : open ? Sel : Raised, sx + 2, sy, w - 4, HeadH - 2, 3);
                bool mixing = fx != null && dragFx == fx && dragParam == Lane.FxMix;   // while its mix bar is dragged, the row shows the value
                string label = fx == null ? "+ effect" : mixing ? "Wet/dry " + (int)Math.Round(fx.Mix * 100) + "%" : (open ? "▾ " : "▸ ") + fx.Type + (fx.Bypass ? " off" : "");
                g.DrawString(label, Small, fx == null || fx.Bypass ? DimB : TxtB, new RectangleF(sx + 5, sy, fx == null ? w - 8 : w - 60, HeadH - 2), Fit);
                if (fx == null) continue;
                ValueBar(g, sx + w - 52, sy + 6, 30, 6, 0, fx.Mix, fx.Bypass ? Dim : Blue);   // wet/dry, left of the on/off dot
                Led(g, sx + w - 16, sy + 4, 9, !fx.Bypass);
                if (!open) continue;
                if (fx.P.Length > 3) { PaintMiniEq(g, fx, new RectangleF(sx + 2, sy + HeadH + 1, w - 4, ParamsH - 3)); continue; }   // the Parametric EQ: its curve; a click opens the editor
                for (int p = 0; p < 3; p++)
                {
                    float py = sy + HeadH + 1 + p * 14;
                    ValueBar(g, sx + 2, py, w - 4, 12, 0, fx.P[p], DeepBlue);
                    g.DrawString(fx.Label(p), Small, TxtB, new RectangleF(sx + 6, py - 1, w - 10, 14), Fit);
                }
            }
            if (slotDragging && slotDragIns == i)   // where the dragged effect will land: under the target row moving down, above it moving up
            {
                int to = SlotAtY(i, slotDropY, st);
                float ly = SlotY(i, to, st) + (to > slotDragFrom ? HeadH - 1 : -1);
                using (var pen = new Pen(On, 2)) g.DrawLine(pen, sx + 2, ly, sx + w - 2, ly);
            }
        }
    }

    void MixerDown(object sender, MouseEventArgs e)
    {
        float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st);
        int i = StripAt(e.X);
        if (i < 0 || e.Y < Top0) return;
        var ins = P.Mixer[i]; float sx = StripX(i), w = sw - 6;
        if (e.Y >= st)   // effect slots
        {
            for (int s = 0; s < Insert.SlotCount; s++)
            {
                float sy = SlotY(i, s, st); var fx = ins.Slots[s];
                if (e.Y >= sy && e.Y < sy + HeadH)   // a slot's row: click a closed effect to open it; otherwise pick, change or remove
                {
                    if (fx != null && e.Button == MouseButtons.Left && e.X >= sx + w - 20) { fx.Bypass = !fx.Bypass; Changed(); }   // the on/off dot
                    else if (fx != null && e.X >= sx + w - 56 && e.X < sx + w - 20)   // the mix bar: drag it, or right-click to automate it
                    {
                        if (e.Button == MouseButtons.Right) AutomateControl(i, s, Lane.FxMix);
                        else if (e.Button == MouseButtons.Left) { dragFx = fx; dragParam = Lane.FxMix; dragX0 = sx + w - 52; dragW = 30; MixerMove(sender, e); }
                    }
                    else if (fx != null && e.Button == MouseButtons.Left) { slotDragIns = i; slotDragFrom = s; slotDownY = slotDropY = e.Y; slotDragging = false; }   // decided on mouse-up: click or drag
                    else ShowFxMenu(i, s, e.Location);
                    return;
                }
                if (IsOpen(i, s) && e.Y >= sy + HeadH && e.Y < sy + HeadH + ParamsH)   // the open effect's settings
                {
                    if (fx.P.Length > 3) { if (e.Button == MouseButtons.Left) ShowEq(i, s); return; }   // the Parametric EQ's curve: open its editor
                    int p = (int)((e.Y - sy - HeadH - 1) / 14);
                    if (p < 0 || p > 2) return;
                    if (e.Button == MouseButtons.Right) { AutomateControl(i, s, p); return; }
                    if (e.Button != MouseButtons.Left) return;
                    dragFx = fx; dragParam = p; dragX0 = sx + 2; dragW = w - 4;
                    MixerMove(sender, e); return;
                }
            }
            return;
        }
        if (e.Y >= Top0 + 50 && e.Y < fy)   // routing rows: "out" opens the routing menu; "sc" the sidechain menu; the bar is the first send's level
        {
            if (i == 0) return;
            float half = (w - 8) / 2;
            if (e.Y < Top0 + 67) RoutingMenu(i, new Point((int)sx + 4, Top0 + 66), false);
            else if (e.X < sx + 4 + half) RoutingMenu(i, new Point((int)sx + 4, Top0 + 82), true);
            else if (ins.Sends.Count > 0 && e.Button == MouseButtons.Left) { dragCh = i; dragMode = "send"; MixerMove(sender, e); }
            else if (ins.Sends.Count > 0 && e.Button == MouseButtons.Right) AutomateControl(i, -1, 2);
            return;
        }
        if (e.Button == MouseButtons.Right && e.Y >= Top0 + 34 && (e.Y >= fy || e.X >= sx + 18)) { AutomateControl(i, -1, e.Y >= fy ? 0 : 1); return; }   // fader or pan
        if (e.Button != MouseButtons.Left || e.Y < Top0 + 34) return;
        dragCh = i;
        if (e.Y < fy)
        {
            if (e.X < sx + 18) { ins.Mute = !ins.Mute; Changed(); }
            else { dragMode = "mixpan"; MixerMove(sender, e); }
            return;
        }
        dragMode = "mixvol"; MixerMove(sender, e);
    }

    void MixerMove(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (slotDragIns >= 0) { if (Math.Abs(e.Y - slotDownY) > 4) slotDragging = true; slotDropY = e.Y; mixer.Invalidate(); return; }   // a few pixels make it a drag
        float sw, fy, fh, st; MixGeom(out sw, out fy, out fh, out st);
        if (dragFx != null && dragParam == Lane.FxMix) dragFx.Mix = Clamp01((e.X - dragX0) / dragW);
        else if (dragFx != null) dragFx.P[dragParam] = Clamp01((e.X - dragX0) / dragW);
        else if (dragMode == "mixvol") P.Mixer[dragCh].Vol = Clamp01(1 - (e.Y - fy) / fh);
        else if (dragMode == "send") { float half = (sw - 14) / 2; P.Mixer[dragCh].SendLevel = Clamp01((e.X - (StripX(dragCh) + 6 + half)) / (half - 2)); }   // same bar as PaintMixer
        else if (dragMode == "mixpan")
        {
            float x0 = StripX(dragCh) + 20, pw = sw - 30;
            P.Mixer[dragCh].Pan = Math.Max(-1, Math.Min(1, (e.X - x0) / pw * 2 - 1));
        }
        else return;
        Changed();
    }

    // The routing menu of insert i: master on / off, its sends (level, remove), add a send, and (sc) the sidechain source.
    // Only routes that can't loop back are offered.
    void RoutingMenu(int i, Point at, bool sidechain)
    {
        var m = new ContextMenuStrip(); var ins = P.Mixer[i];
        Func<int, string> name = k => k == 0 ? "Master" : "Insert " + k + (P.Channels.Any(c => c.Insert == k) ? " (" + string.Join(", ", P.Channels.Where(c => c.Insert == k).Select(c => c.Name).Take(3)) + ")" : "");
        if (sidechain)
        {
            ((ToolStripMenuItem)m.Items.Add("No sidechain", null, (s, e) => Edit(() => { P.Mixer[i].Sidechain = 0; dirty = true; Changed(); }))).Checked = ins.Sidechain == 0;
            m.Items.Add(new ToolStripSeparator());
            for (int k = 1; k < Project.Inserts; k++)
            {
                if (k != ins.Sidechain && !P.CanRoute(k, i)) continue;
                int src = k; ((ToolStripMenuItem)m.Items.Add("Sidechain from " + name(k), null, (s, e) => Edit(() => { if (P.CanRoute(src, i)) P.Mixer[i].Sidechain = src; dirty = true; Changed(); }))).Checked = ins.Sidechain == k;
            }
            m.MaximumSize = new Size(400, 600); m.Show(mixer, at); return;
        }
        ((ToolStripMenuItem)m.Items.Add("To the master", null, (s, e) => Edit(() => { P.Mixer[i].ToMaster = !P.Mixer[i].ToMaster; dirty = true; Changed(); }))).Checked = ins.ToMaster;
        if (ins.Sends.Count > 0) m.Items.Add(new ToolStripSeparator());
        foreach (var r in ins.Sends.ToList())
        {
            var route = r; var item = new ToolStripMenuItem("Send to " + name(r.To) + "  —  " + (int)Math.Round(r.Level * 100) + "%");
            for (int pct = 0; pct <= 100; pct += 10) { float lvl = pct / 100f; ((ToolStripMenuItem)item.DropDownItems.Add(pct + "%", null, (s, e) => Edit(() => { route.Level = lvl; dirty = true; Changed(); }))).Checked = Math.Abs(r.Level - lvl) < 0.001f; }
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add("Remove this send", null, (s, e) => Edit(() => { P.Mixer[i].Sends.Remove(route); dirty = true; Changed(); }));
            m.Items.Add(item);
        }
        var add = new ToolStripMenuItem("Add a send to");
        for (int k = 1; k < Project.Inserts; k++)
        {
            if (!P.CanRoute(i, k) || ins.Sends.Any(x => x.To == k)) continue;   // would loop back, or already sends there
            int to = k; add.DropDownItems.Add(name(k), null, (s, e) => Edit(() => { if (P.CanRoute(i, to)) P.Mixer[i].Sends.Add(new Route { To = to }); dirty = true; Changed(); }));
        }
        m.Items.Add(new ToolStripSeparator()); m.Items.Add(add);
        m.Show(mixer, at);
    }

    static void PaintMiniEq(Graphics g, Effect fx, RectangleF r)   // the Parametric EQ's curve in its open slot
    {
        Round(g, Well, r.X, r.Y, r.Width, r.Height, 3);
        using (var pen = new Pen(Color.FromArgb(44, 50, 60))) g.DrawLine(pen, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
        var pts = new PointF[(int)r.Width];
        for (int k = 0; k < pts.Length; k++) pts[k] = new PointF(r.X + k, r.Y + r.Height / 2 - (float)Math.Max(-18, Math.Min(18, fx.ResponseDb(20 * Math.Pow(1000, k / (double)pts.Length)))) / 36 * r.Height);
        var am = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (pts.Length > 1) using (var pen = new Pen(On, 1.5f)) g.DrawLines(pen, pts);
        g.SmoothingMode = am;
        g.DrawString("click to edit", Small, DimB, r.Right - 64, r.Bottom - 14);
    }

    AutoWindow autoWin;
    internal void ShowAutomation(int k)   // one automation editor at a time; it follows whichever lane opened it last
    {
        if (k < 0 || k >= P.Automation.Count) return;
        if (autoWin == null || autoWin.IsDisposed) autoWin = new AutoWindow(this);
        autoWin.Target(P.Automation[k]);
        if (!autoWin.Visible) autoWin.Show(this); else autoWin.Activate();
    }

    // The automation editor, like FL's automation clips: points joined by curves across the lane (as far as its clips reach).
    // Click to add a point, drag to move it, right-click to delete it; drag the small circle between two points to bend that
    // curve. The LFO menu fills the lane with a wave in time with the song. Each change is one undo step.
    internal class AutoWindow : Form
    {
        readonly MainForm m; readonly Canvas view = new Canvas(); Lane key;   // key: which control (looked up each time, so undo can't strand us)
        int drag = -1; bool bending; float bendY, bend0;
        const int X0 = 46, Y0 = 34;
        static readonly string[] Shapes = { "Sine", "Triangle", "Square", "Saw up", "Saw down" };
        static readonly string[] RateNames = { "2 bars", "1 bar", "1/2 bar", "1 beat", "1/8" }; static readonly double[] Rates = { 32, 16, 8, 4, 2 };
        public AutoWindow(MainForm main)
        {
            m = main; FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 300); MinimumSize = new Size(420, 220); Font = main.Font;
            view.Dock = DockStyle.Fill; Controls.Add(view); view.Paint += PaintView;
            view.MouseDown += (s, e) => Down(e);
            view.MouseMove += (s, e) => { if (e.Button == MouseButtons.Left && (drag >= 0 || bending)) DragTo(e); };
            view.MouseUp += (s, e) => { if (drag >= 0 || bending) { drag = -1; bending = false; m.EndEdit(); } };
            HandleCreated += (s, e) => DarkTitle(Handle);
            KeyPreview = true; KeyDown += (s, e) => m.PianoKey(e, true); KeyUp += (s, e) => m.PianoKey(e, false);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { return m.ProcessCmdKey(ref msg, keyData); }
        public void Target(Lane l) { key = l; view.Invalidate(); }
        int Index() { return key == null ? -1 : m.P.Automation.FindIndex(l => l.SameTarget(key)); }
        Lane L() { int k = Index(); return k < 0 ? null : m.P.Automation[k]; }
        double Span() { int k = Index(); double sp = 64; foreach (var c in m.P.Clips) if (c.Lane == k) sp = Math.Max(sp, c.Offset + c.Length(m.P)); var l = L(); if (l != null && l.Points != null && l.Points.Count > 0) sp = Math.Max(sp, l.Points.Max(q => q.At) + 4); return sp; }
        float W { get { return view.Width - X0 - 14; } }
        float H { get { return view.Height - Y0 - 26; } }
        float PX(double pos) { return X0 + (float)(pos / Span()) * W; }
        float PY(float v) { return Y0 + (1 - v) * H; }
        double Pos(float x) { return Math.Max(0, Math.Round((x - X0) / W * Span() * 4) / 4); }   // snapped to 1/64 notes
        float Val(float y) { return Clamp01(1 - (y - Y0) / H); }
        List<LanePoint> Pts(Lane l) { l.MakePoints(); l.Points.Sort((a, b) => a.At.CompareTo(b.At)); return l.Points; }
        PointF Mid(List<LanePoint> pts, int j)   // the bend handle of segment j: where the curve is halfway across
        {
            var a = pts[j]; var b = pts[j + 1]; return new PointF(PX((a.At + b.At) / 2), PY(a.Value + (b.Value - a.Value) * Lane.Bend(0.5f, a.Tension)));
        }
        void Changed(Lane l) { l.Render(); m.dirty = true; m.playlist.Invalidate(); view.Invalidate(); }

        void Down(MouseEventArgs e)
        {
            var l = L(); if (l == null) return;
            if (e.Y < Y0 - 6) { Header(e); return; }
            var pts = Pts(l);
            int hit = pts.FindIndex(q => Math.Abs(PX(q.At) - e.X) < 8 && Math.Abs(PY(q.Value) - e.Y) < 8);
            if (e.Button == MouseButtons.Right) { if (hit >= 0 && pts.Count > 1) { m.Edit(() => pts.RemoveAt(hit)); Changed(l); } return; }
            if (e.Button != MouseButtons.Left) return;
            m.BeginEdit();
            if (hit >= 0) { drag = hit; return; }
            for (int j = 0; j < pts.Count - 1; j++)
            {
                var mp = Mid(pts, j);
                if (Math.Abs(mp.X - e.X) < 8 && Math.Abs(mp.Y - e.Y) < 8) { drag = j; bending = true; bendY = e.Y; bend0 = pts[j].Tension; return; }
            }
            var np = new LanePoint { At = Pos(e.X), Value = Val(e.Y) };
            pts.Add(np); pts.Sort((a, b) => a.At.CompareTo(b.At)); drag = pts.IndexOf(np);
            Changed(l);
        }

        void DragTo(MouseEventArgs e)
        {
            var l = L(); if (l == null) return; var pts = Pts(l);
            if (drag < 0 || drag >= pts.Count) return;
            if (bending) pts[drag].Tension = Math.Max(-1, Math.Min(1, bend0 + (bendY - e.Y) / 80f * (pts[drag + 1].Value >= pts[drag].Value ? -1 : 1)));
            else
            {
                double lo = drag > 0 ? pts[drag - 1].At + 0.25 : 0, hi = drag < pts.Count - 1 ? pts[drag + 1].At - 0.25 : double.MaxValue;   // points keep their order
                pts[drag].At = Math.Max(lo, Math.Min(hi, Pos(e.X))); pts[drag].Value = Val(e.Y);
            }
            Changed(l);
        }

        void Header(MouseEventArgs e)   // LFO ▾ and Flatten
        {
            var l = L(); if (l == null) return;
            if (e.X < X0 + 90)
            {
                var menu = new ContextMenuStrip();
                for (int sh = 0; sh < Shapes.Length; sh++)
                {
                    var item = new ToolStripMenuItem(Shapes[sh]);
                    for (int r = 0; r < Rates.Length; r++) { int shape = sh; double rate = Rates[r]; item.DropDownItems.Add("every " + RateNames[r], null, (s, a) => { m.Edit(() => { l.Points = Lfo(shape, rate, Span()); }); Changed(l); }); }
                    menu.Items.Add(item);
                }
                menu.Show(view, X0, Y0 - 4);
            }
            else if (e.X < X0 + 170) { float v = l.At(0); m.Edit(() => { l.Points = new List<LanePoint> { new LanePoint { At = 0, Value = v }, new LanePoint { At = Span(), Value = v } }; }); Changed(l); }
        }

        internal static List<LanePoint> Lfo(int shape, double period, double span)   // a wave between 0 and 1, one cycle per period (16ths)
        {
            var pts = new List<LanePoint>(); double half = period / 2;
            if (shape == 0) for (double t = 0; t <= span + 1e-9; t += period / 16) pts.Add(Pt(t, 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / period)));
            else if (shape == 1) for (int k = 0; k * half <= span + 1e-9; k++) pts.Add(Pt(k * half, k % 2));   // triangle: straight lines between 0 and 1
            else if (shape == 2) for (int k = 0; k * half <= span + 1e-9; k++) { int v = k % 2 == 0 ? 1 : 0; if (k > 0) pts.Add(Pt(k * half - 0.01, 1 - v)); pts.Add(Pt(k * half, v)); }   // square: on, off
            else for (int k = 0; k * period <= span + 1e-9; k++) { int start = shape == 3 ? 0 : 1; if (k > 0) pts.Add(Pt(k * period - 0.01, 1 - start)); pts.Add(Pt(k * period, start)); }   // saw: a ramp, then a jump back
            return pts;
        }
        static LanePoint Pt(double at, double v) { return new LanePoint { At = at, Value = (float)v }; }
        void PaintView(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; var l = L();
            Text = l == null ? "Automation" : "Automation — " + l.Name(m.P);
            if (l == null) { g.DrawString("This automation lane was deleted.", Font, DimB, 16, 16); return; }
            RouteBox(g, X0, 8, 84, "LFO  ▾", false); RouteBox(g, X0 + 92, 8, 72, "Flatten", false);
            g.DrawString("click: add a point · drag: move it · drag a small circle: bend the curve · right-click a point: delete", Small, DimB, X0 + 176, 8);
            Round(g, Well, X0, Y0, W, H, 4);
            double span = Span(); int k = Index();
            foreach (var c in m.P.Clips.Where(c => c.Lane == k))   // the stretches its clips play, lightly marked
                using (var b = new SolidBrush(Color.FromArgb(18, Blue))) g.FillRectangle(b, PX(c.Offset), Y0, (float)(c.Length(m.P) / span * W), H);
            using (var pen = new Pen(Color.FromArgb(40, 46, 56)))
                for (int bar = 0; bar * 16 <= span; bar++) { g.DrawLine(pen, PX(bar * 16), Y0, PX(bar * 16), Y0 + H); if (span / 16 < 40 || bar % 4 == 0) g.DrawString((bar + 1).ToString(), Small, DimB, PX(bar * 16) + 2, Y0 + H + 4); }
            var curve = new List<PointF>();
            for (float x = 0; x <= W; x += 2) curve.Add(new PointF(X0 + x, PY(l.At(x / W * span))));
            var am = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            var poly = new List<PointF>(curve) { new PointF(X0 + W, Y0 + H), new PointF(X0, Y0 + H) };
            using (var b = new SolidBrush(Color.FromArgb(40, On))) g.FillPolygon(b, poly.ToArray());
            using (var pen = new Pen(On, 2)) g.DrawLines(pen, curve.ToArray());
            var pts = l.Points;
            if (pts != null)
            {
                var sorted = pts.OrderBy(q => q.At).ToList();
                float gap = sorted.Count > 1 ? (PX(sorted[sorted.Count - 1].At) - PX(sorted[0].At)) / (sorted.Count - 1) : W, r = gap < 14 ? 2.5f : 5;   // dense (an LFO): small dots, no bend circles
                if (gap >= 24) for (int j = 0; j < sorted.Count - 1; j++) { var mp = Mid(sorted, j); using (var pen = new Pen(Color.FromArgb(200, 206, 211, 220), 1.5f)) g.DrawEllipse(pen, mp.X - 3.5f, mp.Y - 3.5f, 7, 7); }
                foreach (var q in sorted) using (var b = new SolidBrush(Color.FromArgb(230, 235, 242))) g.FillEllipse(b, PX(q.At) - r, PY(q.Value) - r, 2 * r, 2 * r);
            }
            g.SmoothingMode = am;
            g.DrawString("max", Small, DimB, 8, Y0 - 2); g.DrawString("min", Small, DimB, 8, Y0 + H - 12);
        }
    }
    EqWindow eqWin;
    void ShowEq(int ins, int slot)   // one EQ editor at a time; it follows whichever slot opened it last
    {
        if (eqWin == null || eqWin.IsDisposed) eqWin = new EqWindow(this);
        eqWin.Target(ins, slot);
        if (!eqWin.Visible) eqWin.Show(this); else eqWin.Activate();
    }

    // The Parametric EQ's editor, like FL's Parametric EQ 2: the true response curve, and a handle per band to drag (frequency
    // across, gain up and down); the wheel over a handle sets its Q; right-click flattens it. Each drag is one undo step.
    internal class EqWindow : Form
    {
        readonly MainForm m; readonly Canvas view = new Canvas();
        int ins, slot, drag = -1, hover = -1;
        const int X0 = 44, Y0 = 14;
        public EqWindow(MainForm main)
        {
            m = main; FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 300); MinimumSize = new Size(380, 220); Font = main.Font;
            view.Dock = DockStyle.Fill; Controls.Add(view); view.Paint += PaintView;
            view.MouseDown += (s, e) =>
            {
                var fx = Fx(); int b = Near(e.Location); if (fx == null || b < 0) return;
                if (e.Button == MouseButtons.Right) { m.Edit(() => { fx.P[b * 3 + 1] = 0.5f; m.dirty = true; }); view.Invalidate(); return; }
                m.BeginEdit(); drag = b; DragTo(e);
            };
            view.MouseMove += (s, e) => { if (drag >= 0 && e.Button == MouseButtons.Left) DragTo(e); else { int h = Near(e.Location); if (h != hover) { hover = h; view.Invalidate(); } } };
            view.MouseUp += (s, e) => { if (drag >= 0) { drag = -1; m.EndEdit(); } };
            view.MouseWheel += (s, e) =>
            {
                var fx = Fx(); int b = hover >= 0 ? hover : Near(e.Location); if (fx == null || b < 0) return;
                m.Edit(() => { fx.P[b * 3 + 2] = Clamp01(fx.P[b * 3 + 2] + Math.Sign(e.Delta) * 0.04f); m.dirty = true; }); view.Invalidate();
            };
            HandleCreated += (s, e) => DarkTitle(Handle);
            KeyPreview = true; KeyDown += (s, e) => m.PianoKey(e, true); KeyUp += (s, e) => m.PianoKey(e, false);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { return m.ProcessCmdKey(ref msg, keyData); }
        public void Target(int i, int s) { ins = i; slot = s; view.Invalidate(); }
        Effect Fx() { var fx = ins < Project.Inserts ? m.P.Mixer[ins].Slots[slot] : null; return fx != null && fx.P.Length > 3 ? fx : null; }   // looked up each time: undo may have replaced it
        float W { get { return view.Width - X0 - 12; } }
        float Hh { get { return view.Height - Y0 - 24; } }
        float FX(double hz) { return X0 + W * (float)(Math.Log(hz / 20) / Math.Log(1000)); }
        float DY(double db) { return Y0 + Hh * (0.5f - (float)db / 36); }
        PointF BandPoint(Effect fx, int b) { return new PointF(FX(Effect.BandHz(fx.P[b * 3])), DY(Effect.BandDb(fx.P[b * 3 + 1]))); }
        int Near(Point p) { var fx = Fx(); if (fx == null) return -1; int best = -1; double bd = 14; for (int b = 0; b < Effect.Bands; b++) { var h = BandPoint(fx, b); double d = Math.Sqrt((h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y)); if (d < bd) { bd = d; best = b; } } return best; }
        void DragTo(MouseEventArgs e)
        {
            var fx = Fx(); if (fx == null) return;
            fx.P[drag * 3] = Clamp01((e.X - X0) / W); fx.P[drag * 3 + 1] = Clamp01(1 - (e.Y - Y0) / Hh);   // across: frequency (log); up: gain
            m.dirty = true; view.Invalidate();
        }
        void PaintView(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; var fx = Fx();
            Text = "Insert " + ins + " — Parametric EQ";
            if (fx == null) { g.DrawString("This slot has no Parametric EQ any more.", Font, DimB, 16, 16); return; }
            Round(g, Well, X0, Y0, W, Hh, 4);
            using (var pen = new Pen(Color.FromArgb(40, 46, 56)))
            {
                foreach (var f in new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 }) { g.DrawLine(pen, FX(f), Y0, FX(f), Y0 + Hh); g.DrawString(f >= 1000 ? f / 1000 + "k" : f.ToString(), Small, DimB, FX(f) - 8, Y0 + Hh + 4); }
                foreach (var db in new[] { -12, -6, 0, 6, 12 }) { g.DrawLine(pen, X0, DY(db), X0 + W, DY(db)); g.DrawString((db > 0 ? "+" : "") + db, Small, DimB, 8, DY(db) - 7); }
            }
            var pts = new PointF[(int)W]; for (int k = 0; k < pts.Length; k++) pts[k] = new PointF(X0 + k, DY(Math.Max(-18, Math.Min(18, fx.ResponseDb(20 * Math.Pow(1000, k / (double)pts.Length))))));
            var am = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (pts.Length > 1)
            {
                var poly = new List<PointF>(pts) { new PointF(pts[pts.Length - 1].X, DY(0)), new PointF(pts[0].X, DY(0)) };
                using (var b = new SolidBrush(Color.FromArgb(40, On))) g.FillPolygon(b, poly.ToArray());
                using (var pen = new Pen(On, 2)) g.DrawLines(pen, pts);
            }
            for (int b = 0; b < Effect.Bands; b++)
            {
                var h = BandPoint(fx, b); bool hot = b == hover || b == drag;
                using (var br = new SolidBrush(hot ? Color.White : Color.FromArgb(206, 211, 220))) g.FillEllipse(br, h.X - 7, h.Y - 7, 14, 14);
                g.DrawString((b + 1).ToString(), Small, Brushes.Black, new RectangleF(h.X - 7, h.Y - 7, 14, 14), Mid);
            }
            g.SmoothingMode = am;
            int show = drag >= 0 ? drag : hover;
            g.DrawString(show >= 0 ? fx.Label(show * 3) + "   " + fx.Label(show * 3 + 1).Split(new[] { "  " }, StringSplitOptions.None).Last() + "   " + fx.Label(show * 3 + 2).Split(new[] { "  " }, StringSplitOptions.None).Last()
                : "drag a handle: frequency and gain · wheel over it: Q (width) · right-click: flat", Small, show >= 0 ? TxtB : DimB, X0 + 6, Y0 + 4);
        }
    }

    void ShowFxMenu(int i, int slot, Point at)   // the insert is looked up when an item is picked, in case undo replaced the project meanwhile
    {
        var m = new ContextMenuStrip();
        foreach (var type in Effect.Types)
        {
            string t = type;   // foreach variables are shared between iterations in C# 5
            m.Items.Add(t, null, (s, e) => Edit(() => { P.Mixer[i].Slots[slot] = new Effect(t); openSlot[i] = slot; Changed(); }));   // new object = fresh effect state; show its settings
        }
        m.Items.Add("(remove)", null, (s, e) => Edit(() => { P.Mixer[i].Slots[slot] = null; Changed(); }));
        m.Show(mixer, at);
    }

    // ---- playlist: up to 500 tracks of clips over up to 999 bars, scrolled and zoomed; a ruler with markers, time signatures and a loop region
    int plTop; float plRowH = 22.5f;           // first track shown; track height
    double plFrom, plSpan = 32 * 16, plSnap = 16;   // first 16th shown; 16ths shown; the clip grid (bar, beat, 16th)
    const int PlHead = 118, PlY0 = 38, PlSnapX = 92, PlSnapW = 92;
    static readonly string[] PlSnapNames = { "bar", "beat", "1/16" }; static readonly double[] PlSnaps = { 16, 4, 1 };
    Clip plClip, plOrig; string plMode; double plGrab, loopDown; bool plMoved;   // a clip being moved / resized / trimmed, as it was, and where it was grabbed

    float PlCw() { return (float)((playlist.Width - PlHead) / plSpan); }
    float PlX(double st) { return (float)(PlHead + (st - plFrom) * PlCw()); }
    double PlStep(float x) { return plFrom + (x - PlHead) / PlCw(); }
    int PlTrack(float y) { return plTop + (int)Math.Floor((y - PlY0) / plRowH); }
    float PlTrackY(int t) { return PlY0 + (t - plTop) * plRowH; }
    double PlSnapDown(double st) { return Math.Floor(st / plSnap + 1e-9) * plSnap; }
    double PlSnapNear(double st) { return Math.Round(st / plSnap) * plSnap; }
    Clip ClipHit(int t, double st) { return P.Clips.LastOrDefault(c => c.Track == t && st >= c.Start && st < c.Start + c.Length(P)); }

    void PaintPlaylist(object sender, PaintEventArgs e)
    {
        var g = e.Graphics; int W = playlist.Width, H = playlist.Height;
        Header(g, playlist, "PLAYLIST");
        int nl = P.Automation.Count;
        RouteBox(g, PlSnapX, 4, PlSnapW, "snap: " + PlSnapNames[Array.IndexOf(PlSnaps, plSnap)], plSnap != 16);
        g.DrawString("click: place · drag: move (Shift: copy) · edges: resize / trim · Ctrl+click: split · right: erase · click an automation clip: edit · ruler: drag = loop, right-click = markers · wheel: tracks",
            Small, DimB, new RectangleF(PlSnapX + PlSnapW + 10, 5, Math.Max(10, W - AutoBoxX - PlSnapX - PlSnapW - 20), 14), Fit);
        RouteBox(g, W - AutoBoxX, 4, AutoBoxW, nl == 0 ? "automation: right-click a control (fader, knob, BPM…) to make a clip" : "automation: " + nl + (nl == 1 ? " lane" : " lanes") + "  ▾", nl > 0);
        float cw = PlCw(); double to = plFrom + plSpan;
        // ruler: bar numbers (following time signatures), the loop region, markers
        using (var b = new SolidBrush(Color.FromArgb(24, 27, 33))) g.FillRectangle(b, PlHead, 23, W - PlHead, PlY0 - 23);
        if (P.LoopTo > P.LoopFrom)
            using (var b = new SolidBrush(Color.FromArgb(90, On))) g.FillRectangle(b, PlX(P.LoopFrom), 24, (float)((P.LoopTo - P.LoopFrom) * cw), PlY0 - 26);
        var bars = new List<double>(); int barNo, beats, lastLabel = -100;
        for (int st = (int)Math.Floor(plFrom); st <= to; st++) if (P.BarAt(st, out barNo, out beats)) bars.Add(st);
        float barPx = (float)(16 * cw); int every = barPx >= 30 ? 1 : barPx >= 12 ? 4 : 16;
        foreach (var st in bars)   // bar numbers; a marker's flag takes the place of its bar's number
        {
            P.BarAt(st, out barNo, out beats);
            if ((barNo - 1) % every == 0 && PlX(st) - lastLabel > 20 && !P.Markers.Any(mk => Math.Abs(PlX(mk.At) - PlX(st)) < 30)) { g.DrawString(barNo.ToString(), Small, barNo == 1 ? TxtB : DimB, PlX(st) + 2, 24); lastLabel = (int)PlX(st); }
        }
        foreach (var m in P.Markers)
        {
            float mx = PlX(m.At); if (mx < PlHead - 1 || mx > W) continue;
            var mc = m.Beats > 0 ? Color.FromArgb(150, 104, 224) : Blue;
            using (var b = new SolidBrush(mc)) g.FillPolygon(b, new[] { new PointF(mx, 23), new PointF(mx + 7, 23), new PointF(mx, 31) });
            g.DrawString(m.Beats > 0 ? m.Beats + "/4" + (string.IsNullOrEmpty(m.Name) || m.Name == m.Beats + "/4" ? "" : " " + m.Name) : m.Name, Small, new SolidBrush(mc), mx + 8, 23);
        }
        // tracks: lanes, name tabs, bar and beat lines, clips with a preview of their notes
        int shown = (int)Math.Ceiling((H - PlY0) / plRowH);
        var clip0 = g.Clip; g.SetClip(new RectangleF(0, PlY0, W, H - PlY0));
        for (int r = 0; r < shown && plTop + r < Project.Tracks; r++)
        {
            int t = plTop + r; float y = PlY0 + r * plRowH; bool muted = P.TrackMuted(t); var tr = P.TrackInfo[t];
            using (var br = new SolidBrush(muted ? Color.FromArgb(22, 24, 29) : t % 2 == 0 ? Color.FromArgb(31, 35, 42) : Color.FromArgb(27, 31, 37))) g.FillRectangle(br, PlHead, y, W - PlHead, plRowH);
            Round(g, Raised, 4, y + 1, PlHead - 8, plRowH - 2, 3);
            if (tr != null && tr.Color != 0) Round(g, Color.FromArgb(tr.Color), 4, y + 1, 3, plRowH - 2, 1.5f);
            Led(g, 10, y + plRowH / 2 - 4, 8, !muted);
            g.DrawString(P.TrackName(t), Small, muted ? DimB : TxtB, new RectangleF(22, y, PlHead - 30, plRowH), Fit);
        }
        float yEnd = Math.Min(H, PlTrackY(Project.Tracks));
        if (16 * cw >= 24) using (var pen = new Pen(Color.FromArgb(34, 38, 46))) for (int st = (int)Math.Ceiling(plFrom / 4) * 4; st <= to; st += 4) g.DrawLine(pen, PlX(st), PlY0, PlX(st), yEnd);   // beats
        foreach (var st in bars) { P.BarAt(st, out barNo, out beats); using (var pen = new Pen((barNo - 1) % 4 == 0 ? Color.FromArgb(60, 68, 82) : Color.FromArgb(44, 50, 60))) g.DrawLine(pen, PlX(st), PlY0, PlX(st), yEnd); }
        if (P.LoopTo > P.LoopFrom) using (var b = new SolidBrush(Color.FromArgb(14, On))) g.FillRectangle(b, PlX(P.LoopFrom), PlY0, (float)((P.LoopTo - P.LoopFrom) * cw), yEnd - PlY0);
        g.SetClip(new RectangleF(PlHead, PlY0, W - PlHead, H - PlY0));
        foreach (var c in P.Clips)
        {
            if (c.Track < plTop || c.Track >= plTop + shown || c.Lane < 0 && (c.Pattern < 0 || c.Pattern >= P.Patterns.Count) || c.Lane >= P.Automation.Count) continue;
            double len = c.Length(P);
            if (c.Start > to || c.Start + len < plFrom) continue;
            var rc = new RectangleF(PlX(c.Start) + 1, PlTrackY(c.Track) + 1, (float)(len * cw) - 2, plRowH - 2);
            if (c.Lane >= 0) { PaintAutoClip(g, c, rc, cw); continue; }
            var col = PatColors[c.Pattern % PatColors.Length]; if (P.TrackMuted(c.Track)) col = Color.FromArgb(90, col);
            Round(g, col, rc.X, rc.Y, rc.Width, rc.Height, 4);
            RoundEdge(g, c == plClip ? Color.White : ControlPaint.Dark(col, 0.25f), c == plClip ? 1.5f : 1, rc, 4);
            var pat = P.Patterns[c.Pattern];
            if (rc.Width > 18) g.DrawString(rc.Width > 44 ? pat.Name : (c.Pattern + 1).ToString(), Small, Brushes.White, new RectangleF(rc.X + 4, rc.Y, rc.Width - 6, 14), Fit);
            if (rc.Height >= 20 && rc.Width > 24) PreviewNotes(g, pat, c, new RectangleF(rc.X + 2, rc.Y + 13, rc.Width - 4, rc.Height - 15), cw);
        }
        g.Clip = clip0;
        int ps = eng.PlayStep;
        if (eng.SongMode && ps >= 0 && ps >= plFrom && ps <= to)   // playhead: a line with a marker in the ruler
        {
            float x = PlX(ps);
            using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2)) g.DrawLine(pen, x, PlY0, x, yEnd);
            var am = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(On)) g.FillPolygon(b, new[] { new PointF(x - 5, PlY0 - 8), new PointF(x + 5, PlY0 - 8), new PointF(x, PlY0 - 1) });
            g.SmoothingMode = am;
        }
    }

    void PaintAutoClip(Graphics g, Clip c, RectangleF rc, float cw)   // an automation clip: its name and the stretch of curve it plays
    {
        var lane = P.Automation[c.Lane]; var col = Color.FromArgb(P.TrackMuted(c.Track) ? 90 : 255, 62, 110, 120);
        Round(g, col, rc.X, rc.Y, rc.Width, rc.Height, 4);
        RoundEdge(g, c == plClip ? Color.White : Color.FromArgb(90, 160, 170), c == plClip ? 1.5f : 1, rc, 4);
        var pts = new List<PointF>();
        for (float x = 0; x <= rc.Width; x += 2) pts.Add(new PointF(rc.X + x, rc.Bottom - 2 - lane.At(c.Offset + x / cw) * (rc.Height - 4)));
        var am = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (pts.Count > 1)
        {
            var poly = new List<PointF>(pts) { new PointF(pts[pts.Count - 1].X, rc.Bottom - 1), new PointF(pts[0].X, rc.Bottom - 1) };
            using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillPolygon(b, poly.ToArray());
            using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f)) g.DrawLines(pen, pts.ToArray());
        }
        g.SmoothingMode = am;
        if (rc.Width > 30) g.DrawString(lane.Name(P), Small, Brushes.White, new RectangleF(rc.X + 4, rc.Y, rc.Width - 6, 14), Fit);
    }

    void PreviewNotes(Graphics g, Pattern pat, Clip c, RectangleF area, float cw)   // the clip's notes as small dashes, like FL's clip previews
    {
        var all = pat.Notes.Take(P.Channels.Count).SelectMany(l => l).ToList();
        if (all.Count == 0) return;
        int lo = all.Min(n => n.Key), hi = all.Max(n => n.Key); double len = c.Length(P);
        using (var b = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            for (double rep = -c.Offset; rep < len; rep += pat.Length)   // a stretched clip repeats its pattern
                foreach (var n in all)
                {
                    double at = rep + n.Step;
                    if (at < 0 || at >= len) continue;
                    float y = hi == lo ? area.Y + area.Height / 2 : area.Bottom - 2 - (n.Key - lo) / (float)(hi - lo) * (area.Height - 2);
                    g.FillRectangle(b, area.X + (float)(at * cw), y, Math.Max(1.5f, (float)(Math.Min(n.Len, len - at) * cw) - 1), 1.5f);
                }
    }

    void PlaylistDown(object sender, MouseEventArgs e)
    {
        dragMode = null;
        if (e.Y < 22)   // header: snap, lane picker (click next, right-click previous, through "off") and delete
        {
            int nl = P.Automation.Count;
            if (e.X >= PlSnapX && e.X < PlSnapX + PlSnapW)
            {
                var m = new ContextMenuStrip();
                for (int k = 0; k < PlSnaps.Length; k++) { double v = PlSnaps[k]; ((ToolStripMenuItem)m.Items.Add(PlSnapNames[k], null, (s, a) => { plSnap = v; playlist.Invalidate(); })).Checked = plSnap == v; }
                m.Show(playlist, e.X, 22); return;
            }
            if (nl > 0 && e.X >= playlist.Width - AutoBoxX && e.X < playlist.Width - AutoBoxX + AutoBoxW)   // every lane: open its editor, or delete it with its clips
            {
                var m = new ContextMenuStrip();
                for (int k = 0; k < nl; k++)
                {
                    int lk = k; var item = new ToolStripMenuItem(P.Automation[k].Name(P) + "  (" + P.Clips.Count(c => c.Lane == k) + " clips)");
                    item.DropDownItems.Add("Edit the curve…", null, (s, a) => ShowAutomation(lk));
                    item.DropDownItems.Add("Add a clip of it on a free track", null, (s, a) => Edit(() => { lock (P) P.Clips.Add(new Clip { Lane = lk, Pattern = -1, Track = P.FreeTrack(), Start = 0, Len = Math.Max(4, P.SongBars()) * Project.Steps }); dirty = true; Changed(); }));
                    item.DropDownItems.Add("Delete this lane and its clips", null, (s, a) => Edit(() => { lock (P) P.RemoveLane(lk); dirty = true; Changed(); }));
                    m.Items.Add(item);
                }
                m.Show(playlist, e.X, 22);
            }
            return;
        }
        double st = PlStep(e.X);
        if (e.Y < PlY0)   // the ruler: drag for a loop region; right-click for markers and time signatures
        {
            if (e.X < PlHead) return;
            if (e.Button == MouseButtons.Right) { RulerMenu(st, e.Location); return; }
            loopDown = PlSnapNear(st); dragMode = "loop"; return;
        }
        int t = PlTrack(e.Y);
        if (t < 0 || t >= Project.Tracks) return;
        if (e.X < PlHead)   // a track's tab: the light mutes it, right-click for name and colour
        {
            if (e.Button == MouseButtons.Right) TrackMenu(t, e.Location);
            else if (e.X < 20) { lock (P) P.TrackAt(t).Mute = !P.TrackMuted(t); dirty = true; Changed(); }
            return;
        }
        var hit = ClipHit(t, st);
        if (e.Button == MouseButtons.Right) { dragMode = "erase"; PlaylistMouse(sender, e); return; }
        if (e.Button != MouseButtons.Left) return;
        if (hit != null && (ModifierKeys & Keys.Control) != 0) { SplitClip(hit, PlSnapNear(st)); return; }   // Ctrl+click: split at the nearest grid line
        if (hit != null)   // grab a clip: its right edge resizes, its left edge trims, the rest moves it
        {
            float x0 = PlX(hit.Start), x1 = PlX(hit.Start + hit.Length(P)); float edge = Math.Min(6, (x1 - x0) / 4);
            plMode = e.X >= x1 - edge ? "resize" : e.X <= x0 + edge ? "trim" : "move";
            plClip = hit; plOrig = hit.Copy(); plGrab = st - hit.Start; plMoved = false;
            playlist.Invalidate(); return;
        }
        dragMode = "paint"; PlaylistMouse(sender, e);   // an empty spot: place the selected pattern (and keep placing while dragging)
    }

    void PlaylistMouse(object sender, MouseEventArgs e)
    {
        double st = PlStep(e.X);
        if (dragMode == "loop" && e.Button == MouseButtons.Left)
        {
            double at = PlSnapNear(Math.Max(0, st));
            P.LoopFrom = Math.Min(loopDown, at); P.LoopTo = Math.Max(loopDown, at); dirty = true; playlist.Invalidate(); return;
        }
        if (plClip != null && e.Button == MouseButtons.Left)
        {
            double len = plOrig.Length(P), end = plOrig.Start + len;
            if (plMode == "move")
            {
                plClip.Start = Math.Max(0, PlSnapNear(st - plGrab)); plClip.Track = Math.Max(0, Math.Min(Project.Tracks - 1, PlTrack(e.Y)));
            }
            else if (plMode == "resize")
                plClip.Len = Math.Max(Math.Min(plSnap, len), PlSnapNear(st) - plClip.Start);
            else   // trim: move the front, keeping the end; never before the pattern's own start
            {
                double front = Math.Max(plOrig.Start - plOrig.Offset, Math.Min(end - Math.Min(plSnap, len), PlSnapNear(st)));
                plClip.Start = front; plClip.Offset = plOrig.Offset + (front - plOrig.Start); plClip.Len = end - front;
            }
            if (plClip.Start != plOrig.Start || plClip.Track != plOrig.Track || plClip.Len != plOrig.Len) plMoved = true;
            Changed(); return;
        }
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
        int t = PlTrack(e.Y);
        if (t < 0 || t >= Project.Tracks || e.X < PlHead || st < 0) return;
        if (dragMode == "erase" && e.Button == MouseButtons.Right)
        {
            var hit = ClipHit(t, st);
            if (hit != null) { lock (P) { P.Clips.Remove(hit); if (hit.Lane >= 0 && !P.Clips.Any(c => c.Lane == hit.Lane)) P.RemoveLane(hit.Lane); } dirty = true; Changed(); }   // a lane's last clip takes the lane with it
            return;
        }
        if (dragMode == "paint" && e.Button == MouseButtons.Left && patBox.SelectedIndex >= 0 && ClipHit(t, st) == null && plClip == null)
        {
            double at = PlSnapDown(st);
            if (at >= Project.MaxBars * Project.Steps) return;
            lock (P) P.Clips.Add(new Clip { Track = t, Start = at, Pattern = patBox.SelectedIndex });
            dirty = true; Changed();
        }
    }

    void SplitClip(Clip c, double cut)   // two clips that play exactly what the one did
    {
        double end = c.Start + c.Length(P);
        if (cut <= c.Start + 1e-9 || cut >= end - 1e-9) return;
        lock (P)
        {
            var right = c.Copy(); right.Start = cut; right.Offset = c.Offset + (cut - c.Start); right.Len = end - cut;
            c.Len = cut - c.Start; P.Clips.Add(right);
        }
        dirty = true; Changed();
    }
    void FinishClipDrag()   // mouse-up: a click on a clip picks its pattern; a moved clip with Shift held leaves a copy behind
    {
        if (dragMode == "loop") { if (P.LoopTo - P.LoopFrom < 1e-9) P.LoopFrom = P.LoopTo = 0; dragMode = null; playlist.Invalidate(); return; }   // a click without a drag clears the loop
        dragMode = null;
        if (plClip == null) return;
        var c = plClip; plClip = null;
        if (!plMoved)   // a click: edit that pattern, or that automation curve
        {
            if (plMode == "move" && c.Lane >= 0) ShowAutomation(c.Lane);
            else if (plMode == "move" && c.Pattern >= 0 && c.Pattern < P.Patterns.Count) patBox.SelectedIndex = c.Pattern;
            playlist.Invalidate(); return;
        }
        if (plMode == "move" && (ModifierKeys & Keys.Shift) != 0)
            lock (P) { var copy = c.Copy(); c.Start = plOrig.Start; c.Track = plOrig.Track; P.Clips.Add(copy); }
        dirty = true; Changed();
    }

    void PlaylistWheel(object sender, MouseEventArgs e)   // wheel: tracks; Shift: across; Ctrl: zoom across; Ctrl+Shift: track height
    {
        int notches = e.Delta / 120; if (notches == 0) notches = Math.Sign(e.Delta);
        var mods = ModifierKeys; bool ctrl = (mods & Keys.Control) != 0, shift = (mods & Keys.Shift) != 0;
        double max = Project.MaxBars * Project.Steps;
        if (ctrl && shift) plRowH = Math.Max(12, Math.Min(64, plRowH * (float)Math.Pow(1.15, notches)));
        else if (ctrl)
        {
            double at = PlStep(e.X); plSpan = Math.Max(16, Math.Min(max, plSpan * Math.Pow(0.8, notches)));
            plFrom = Math.Max(0, at - (e.X - PlHead) / (double)(playlist.Width - PlHead) * plSpan);   // the bar under the mouse stays put
        }
        else if (shift) plFrom = Math.Max(0, Math.Min(max - plSpan, plFrom - notches * plSpan / 8));
        else plTop = Math.Max(0, Math.Min(Project.Tracks - 1, plTop - 3 * notches));
        playlist.Invalidate();
    }

    void TrackMenu(int t, Point at)   // right-click a track's tab: rename, colour, mute
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Rename…", null, (s, e) => { string n = Prompt("Rename track", P.TrackName(t)); if (n != null) Edit(() => { P.TrackAt(t).Name = n; dirty = true; Changed(); }); });
        var colour = new ToolStripMenuItem("Colour");
        Action<int> paint = argb => Edit(() => { P.TrackAt(t).Color = argb; dirty = true; Changed(); });
        ((ToolStripMenuItem)colour.DropDownItems.Add("None", null, (s, e) => paint(0))).Checked = P.TrackInfo[t] == null || P.TrackInfo[t].Color == 0;
        foreach (var sw in Swatches) { int argb = sw.ToArgb(); ((ToolStripMenuItem)colour.DropDownItems.Add("", Swatch(sw), (s, e) => paint(argb))).Checked = P.TrackInfo[t] != null && P.TrackInfo[t].Color == argb; }
        m.Items.Add(colour);
        ((ToolStripMenuItem)m.Items.Add("Mute", null, (s, e) => Edit(() => { P.TrackAt(t).Mute = !P.TrackMuted(t); dirty = true; Changed(); }))).Checked = P.TrackMuted(t);
        m.Show(playlist, at);
    }

    void RulerMenu(double st, Point at)   // right-click the ruler: markers, time signatures, the loop region
    {
        var m = new ContextMenuStrip(); double here = PlSnapNear(Math.Max(0, st));
        m.Items.Add("Add a marker here…", null, (s, e) => { string n = Prompt("Marker", "Chorus"); if (n != null) Edit(() => { P.Markers.Add(new Marker { At = here, Name = n }); dirty = true; Changed(); }); });
        var sig = new ToolStripMenuItem("Time signature from here");
        for (int b = 2; b <= 7; b++) { int beats = b; sig.DropDownItems.Add(b + "/4", null, (s, e) => Edit(() => { P.Markers.RemoveAll(x => x.Beats > 0 && Math.Abs(x.At - here) < 1e-9); P.Markers.Add(new Marker { At = here, Beats = beats, Name = beats + "/4" }); dirty = true; Changed(); })); }
        m.Items.Add(sig);
        var near = P.Markers.OrderBy(x => Math.Abs(PlX(x.At) - at.X)).FirstOrDefault(x => Math.Abs(PlX(x.At) - at.X) < 40);
        if (near != null) m.Items.Add("Remove \"" + (near.Name ?? near.Beats + "/4") + "\"", null, (s, e) => Edit(() => { P.Markers.Remove(near); dirty = true; Changed(); }));
        if (P.LoopTo > P.LoopFrom) m.Items.Add("Clear the loop region", null, (s, e) => Edit(() => { P.LoopFrom = P.LoopTo = 0; dirty = true; Changed(); }));
        m.Show(playlist, at);
    }
    // ---------------------------------------------------------------- entry + self-test
    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 2 && args[0] == "--selftest") return SelfTest(args[1]);
        if (args.Length == 2 && args[0] == "--logo")   // write the logo as an .ico (for /win32icon) and a 256 px .png next to it
        {
            File.WriteAllBytes(args[1], Logo.Ico());
            using (var b = Logo.Draw(256)) b.Save(Path.ChangeExtension(args[1], ".png"), System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
        Application.EnableVisualStyles();
        if (args.Length >= 2 && args[0] == "--screenshot")   // the real window drawn to a PNG: built off-screen, never activated, then closed
        {
            screenshotMode = true;
            var f = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
            if (args.Length >= 3 && args[2] == "mixer") f.ToggleMixer();
            f.Shown += (s, e) =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 400 };   // let layout and the first paint settle
                t.Tick += (s2, e2) => { t.Stop(); using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(args[1]); } f.Close(); };
                t.Start();
            };
            Application.Run(f);
            return 0;
        }
        var splash = new AboutForm(true);   // shown while the main window builds (audio, MIDI), then for a moment on top of it
        splash.Show(); splash.Refresh();
        var main = new MainForm();
        splash.Owner = main;   // owned: stays above the main window without being top-most over other apps
        var given = args.Where(a => File.Exists(a) && Droppable(new[] { a })).ToArray();   // files dropped on MiniDAW.exe, or "Open with"
        if (given.Length > 0) main.Shown += (s, e) => main.OpenFiles(given);              // after the recovery offer, which is hooked first
        main.Shown += (s, e) => { var t = new System.Windows.Forms.Timer { Interval = 900 }; t.Tick += (s2, e2) => { t.Dispose(); splash.Close(); }; t.Start(); };
        Application.Run(main);
        return 0;
    }

    static int SelfTest(string outPath)
    {
        var log = new List<string>(); bool ok = true;
        Action<bool, string> check = (c, m) => { log.Add((c ? "ok   " : "FAIL ") + m); if (!c) ok = false; };
        try
        {
            var e = new Engine { Proj = Project.Demo(), Playing = true };
            var buf = new float[Engine.SR * 2 * 2];
            e.Render(buf, Engine.SR * 2);
            check(buf.All(x => !float.IsNaN(x) && Math.Abs(x) <= 1), "pattern render is finite and within [-1, 1]");
            check(buf.Take(2000).Max(x => Math.Abs(x)) > 0.1, "kick on step 1 sounds immediately");
            check(buf.Max(x => Math.Abs(x)) > 0.3, "demo pattern is audible");

            var song = new Engine { Proj = Project.Demo(), Playing = true, SongMode = true };
            song.Render(buf, Engine.SR * 2);
            check(song.PlayStep > Project.Steps, "song mode advances past bar 1 (step " + song.PlayStep + ")");

            string dir = Path.GetTempPath(), wav = Path.Combine(dir, "minidaw_test.wav"), f = Path.Combine(dir, "minidaw_test.mdaw");
            var tone = Enumerable.Range(0, 4410).Select(i => (float)Math.Sin(i * 0.1) * 0.5f).ToArray();
            File.WriteAllBytes(wav, Wav.Make16(tone, 22050, 1));
            var ch = new Channel { Kind = "sample", Name = "Tone" };
            Wav.Load(wav, ch);
            check(ch.Sample.Length == 4410 && ch.SampleRate == 22050 && Math.Abs(ch.Sample[100] - tone[100]) < 1e-3, "16-bit WAV import");

            var p = Project.Demo(); p.Bpm = 97; p.Channels.Add(ch);
            p.Patterns[0].For(4).Add(new Note { Step = 3, Key = 62, Len = 2 }); p.Playlist[2, 5] = 1; p.Channels[1].Pan = -0.25f;
            ch.Insert = 6; p.Mixer[6].Vol = 0.5f; p.Mixer[6].Slots[2] = new Effect("Filter"); p.Mixer[6].Slots[2].P[1] = 0.75f;
            p.Mixer[1].SendTo = 5; p.Mixer[1].SendLevel = 0.25f; p.Mixer[4].Sidechain = 1;
            p.Save(f);
            var q = Project.Load(f);
            check(q.Bpm == 97 && q.Channels.Count == 5 && q.Channels[4].Sample != null && q.Channels[1].Pan == -0.25f
                  && q.Patterns[0].For(4)[0].Key == 62 && q.Patterns[0].For(4)[0].Len == 2 && q.Playlist[2, 5] == 1 && q.Playlist[1, 3] == 1
                  && q.Patterns[1].For(3).Count == 4, "project save/load round trip");
            check(q.Channels[4].Insert == 6 && q.Channels[0].Insert == 1 && q.Mixer[6].Vol == 0.5f && q.Mixer[6].Slots[2].Type == "Filter"
                  && q.Mixer[6].Slots[2].P[1] == 0.75f && q.Mixer[2].Slots[0].Type == "Reverb" && q.Mixer[3].Slots[0] == null
                  && q.Mixer[1].SendTo == 5 && q.Mixer[1].SendLevel == 0.25f && q.Mixer[4].Sidechain == 1 && q.Mixer[2].SendTo == 0,
                  "mixer routing, sends, sidechain and effects survive save/load");

            File.WriteAllText(f, "garbage");
            bool threw = false; try { Project.Load(f); } catch (InvalidDataException) { threw = true; }
            check(threw, "rejects a non-project file");
            File.WriteAllText(f, "MINIDAW\t1\npat\tP\nn\t7\t0\t60\t1\n");
            threw = false; try { Project.Load(f); } catch (InvalidDataException) { threw = true; }
            check(threw, "rejects a note on a missing channel");
            File.WriteAllText(f, "MINIDAW\t1\nfx\t1\t0\tBogus\t0\t0\t0\n");
            threw = false; try { Project.Load(f); } catch (InvalidDataException) { threw = true; }
            check(threw, "rejects an unknown effect");
            File.WriteAllText(f, "MINIDAW\t1\nins\t2\t1\t0\t0\t3\t0.5\t0\nins\t3\t1\t0\t0\t2\t0.5\t0\n");
            threw = false; try { Project.Load(f); } catch (InvalidDataException) { threw = true; }
            File.WriteAllText(f, "MINIDAW\t1\nins\t3\t1\t0\t0\t2\t0.5\t0\n");
            bool backOk = true; try { backOk = Project.Load(f).Mixer[3].SendTo == 2; } catch (InvalidDataException) { backOk = false; }
            check(threw && backOk, "a send to an earlier insert is fine; one that would loop back (2 → 3 → 2) is refused");
            File.WriteAllText(f, "MINIDAW\t1\nins\t2\t0.5\t0\t0\n");   // line from a file saved before sends existed
            var old = Project.Load(f);
            check(old.Mixer[2].Vol == 0.5f && old.Mixer[2].SendTo == 0 && old.Mixer[2].Sidechain == 0, "older project files without sends still open");
            File.Delete(f); File.Delete(wav);

            // export: demo song is 6 bars at 128 BPM; reverb and delay ring past the end, then it stops
            var dp = Project.Demo();
            int body = (int)Math.Ceiling(6 * 16 * (60.0 / 128 / 4 * Engine.SR));
            var b1 = Engine.Bounce(dp.ExportCopy(), true, 0);
            int len = b1.Length / 2;
            check(len > body && len < body + 5 * Engine.SR, "song export covers 6 bars plus a tail and stops (" + (len / (double)Engine.SR).ToString("0.00") + " s)");
            check(b1.All(x => !float.IsNaN(x) && Math.Abs(x) <= 1) && b1.Take(2000).Max(x => Math.Abs(x)) > 0.1, "song export is audible from the first sample and within [-1, 1]");
            var b2 = Engine.Bounce(dp.ExportCopy(), true, 0);
            check(dp.Mixer[2].Slots[0] != dp.ExportCopy().Mixer[2].Slots[0] && b2.SequenceEqual(b1), "export copies effects, so two exports are identical");
            var dry = Project.Demo(); dry.Mixer[2].Slots[0] = dry.Mixer[4].Slots[0] = null;
            dry.Patterns[0].For(0).Clear(); dry.Patterns[0].For(1).Clear();   // only hats remain: short sounds, no tail past the pattern
            int pbody = (int)Math.Ceiling(16 * (60.0 / 128 / 4 * Engine.SR));
            check(Engine.Bounce(dry.ExportCopy(), false, 0).Length == 2 * pbody, "pattern export without effects is exactly one bar long");
            File.WriteAllBytes(wav, Wav.Make16(b1, Engine.SR, 2));
            var back = new Channel { Kind = "sample", Name = "Bounce" };
            Wav.Load(wav, back);
            int mid = len / 3;
            check(back.Sample.Length == len && back.SampleR != null && back.SampleR.Length == len && back.SampleRate == Engine.SR
                  && Math.Abs(back.Sample[mid] - b1[2 * mid]) < 1e-3 && Math.Abs(back.SampleR[mid] - b1[2 * mid + 1]) < 1e-3,
                "exported stereo WAV reads back with the right length, rate and both sides");
            // stereo samples: a WAV with a tone on the left and silence on the right plays exactly that way; a mono WAV plays the same on both sides
            Func<float[], int, float[]> playSample = (data, chans) =>
            {
                File.WriteAllBytes(wav, Wav.Make16(data, Engine.SR, chans));
                var sp5 = new Project(); var sc = new Channel { Kind = "sample", Name = "S", Insert = 1 }; Wav.Load(wav, sc); sp5.Channels.Add(sc);
                var spat = new Pattern { Name = "P" }; sp5.Patterns.Add(spat); spat.For(0).Add(new Note { Step = 0, Key = 60, Len = 1 });
                return Engine.Bounce(sp5, false, 0);
            };
            var leftOnly = Enumerable.Range(0, Engine.SR).Select(k => k % 2 == 0 ? (float)(0.5 * Math.Sin(k * 0.05)) : 0f).ToArray();   // 0.5 s, interleaved
            var stOut = playSample(leftOnly, 2);
            var monoOut = playSample(Enumerable.Range(0, Engine.SR / 2).Select(k => (float)(0.5 * Math.Sin(k * 0.1))).ToArray(), 1);
            check(stOut.Where((x, k) => k % 2 == 0).Max(x => Math.Abs(x)) > 0.2 && stOut.Where((x, k) => k % 2 == 1).All(x => x == 0)
                  && Enumerable.Range(0, monoOut.Length / 2).All(k => monoOut[2 * k] == monoOut[2 * k + 1]) && monoOut.Max(x => Math.Abs(x)) > 0.2,
                "a stereo sample keeps its sides (left tone, silent right stays silent); a mono sample plays equally on both sides");
            File.Delete(wav);

            // automation: a master-volume lane holds 0.8 for bar 1, then 0; a second lane sets insert 4's Delay mix to 0.9
            var ap = Project.Demo();
            var vol = new Lane { Ins = 0, Param = 0 };
            for (int k = 0; k < vol.Values.Length; k++) vol.Values[k] = k < Project.Steps ? 0.8f : 0;
            var mixLane = new Lane { Ins = 4, Slot = 0, Param = 2 };
            for (int k = 0; k < mixLane.Values.Length; k++) mixLane.Values[k] = 0.9f;
            ap.Automation.Add(vol); ap.Automation.Add(mixLane);
            int sps128 = (int)(60.0 / 128 / 4 * Engine.SR);
            var cp = ap.ExportCopy();
            var ab = Engine.Bounce(cp, true, 0);
            check(ab.Take(15 * sps128 * 2).Max(x => Math.Abs(x)) > 0.1 && ab.Skip(17 * sps128 * 2).Take(14 * sps128 * 2).All(x => x == 0),
                "exported song follows the master volume lane: bar 1 plays, bar 2 is silent");
            check(Math.Abs(cp.Mixer[4].Slots[0].P[2] - 0.9f) < 1e-6 && ap.Mixer[4].Slots[0].P[2] == 0.35f && ap.Mixer[0].Vol == 0.8f,
                "lanes move the export copy's controls and leave the live project alone");
            var pe = new Engine { Proj = ap, Playing = true };   // Pattern mode
            pe.Render(buf, Engine.SR / 2);
            check(buf.Take(Engine.SR).Max(x => Math.Abs(x)) > 0.1 && ap.Mixer[0].Vol == 0.8f, "automation doesn't play in Pattern mode");
            var le = new Engine { Proj = ap, Playing = true, SongMode = true };
            var lb = new float[2048];
            for (int k = 0; k < 100; k++) le.Render(lb, 1024);   // 100 blocks of 1024 = 2.3 s, past the end of bar 1
            check(ap.Mixer[0].Vol == 0 && ap.Mixer[4].Slots[0].P[2] == 0.9f, "during song playback the lanes move the mixer's fader and effect setting");
            ap.Save(f);
            var aq = Project.Load(f);
            check(aq.Automation.Count == 2 && aq.Automation[0].Slot == -1 && aq.Automation[0].Values.SequenceEqual(vol.Values)
                  && aq.Automation[1].Ins == 4 && aq.Automation[1].Slot == 0 && aq.Automation[1].Param == 2, "automation lanes survive save/load");
            File.WriteAllText(f, "MINIDAW\t1\nauto\t1\t-1\t0\t0.5\n");
            threw = false; try { Project.Load(f); } catch (InvalidDataException) { threw = true; }
            check(threw, "rejects an automation lane with missing values");
            File.Delete(f);

            // MIDI keyboard: a note-on holds the synth until its note-off; controllers make no sound
            var mp = Project.Demo(); mp.Mixer[4].Slots[0] = null;   // no delay echoes on the synth's insert
            var me = new Engine { Proj = mp, MidiTarget = mp.Channels[3] };
            var mb = new float[Engine.SR];   // 0.5 s of stereo per render
            Func<double> lastFifth = () => mb.Skip(mb.Length - Engine.SR / 5).Max(x => Math.Abs(x));   // peak of the last 0.1 s
            me.MidiIn.Enqueue(0xB0 | 7 << 8 | 100 << 16);   // volume controller
            me.Render(mb, Engine.SR / 2);
            bool ccSilent = mb.All(x => x == 0);
            me.MidiIn.Enqueue(0x90 | 60 << 8 | 100 << 16);
            me.Render(mb, Engine.SR / 2);
            double heldLvl = lastFifth();
            me.MidiIn.Enqueue(0x80 | 60 << 8);
            me.Render(mb, Engine.SR / 2);
            double offLvl = lastFifth();
            me.MidiIn.Enqueue(0x90 | 64 << 8 | 90 << 16); me.Render(mb, Engine.SR / 2);
            double held2 = lastFifth();
            me.MidiIn.Enqueue(0x90 | 64 << 8);   // note-on with velocity 0 = note-off
            me.Render(mb, Engine.SR / 2);
            check(ccSilent && heldLvl > 0.05 && held2 > 0.05 && offLvl == 0 && lastFifth() == 0,
                "MIDI note holds the synth (" + heldLvl.ToString("0.00") + " after 0.5 s) until note-off; velocity-0 note-on also ends it; controllers are silent");
            // sustain pedal: a key let go while the pedal is down keeps sounding until the pedal comes up (value 63 counts as up)
            var sp3 = Project.Demo(); sp3.Mixer[4].Slots[0] = null;
            var pe2 = new Engine { Proj = sp3, MidiTarget = sp3.Channels[3] };
            pe2.MidiIn.Enqueue(0xB0 | 64 << 8 | 127 << 16); pe2.MidiIn.Enqueue(0x90 | 60 << 8 | 100 << 16); pe2.MidiIn.Enqueue(0x80 | 60 << 8);
            pe2.Render(mb, Engine.SR / 2);
            double pedalHeld = lastFifth();
            pe2.MidiIn.Enqueue(0xB0 | 64 << 8 | 63 << 16);
            pe2.Render(mb, Engine.SR / 2);
            check(pedalHeld > 0.05 && lastFifth() == 0, "sustain pedal keeps a released key sounding (" + pedalHeld.ToString("0.00") + " after 0.5 s) until the pedal comes up");
            var mr = new Engine { Proj = Project.Demo(), Playing = true, MidiTarget = mp.Channels[3] };
            var blk = new float[2048];
            for (int k = 0; k < 40; k++) mr.Render(blk, 1024);
            mr.MidiIn.Enqueue(0x90 | 60 << 8 | 100 << 16);
            mr.Render(blk, 1024);
            double spsD = 60.0 / 128 / 4 * Engine.SR, heard = (40 - 4.5) * 1024 / spsD;   // engine position minus the output buffers still to be heard
            KeyValuePair<int, double> rec;
            check(mr.MidiRec.TryDequeue(out rec) && rec.Key == (0x90 | 60 << 8 | 100 << 16) && Math.Abs(rec.Value - heard) < 1e-6,
                "a key pressed while the pattern plays is placed where it was heard: step " + rec.Value.ToString("0.00") + " (rounds to " + Math.Round(rec.Value) + ")");
            // velocity: a held synth note at velocity 50 plays at half the level of 100, both from the pattern and live from a key
            Func<int, bool, double> synthAt = (vel, live) =>
            {
                var vp = Project.Demo(); vp.Mixer[4].Slots[0] = null;   // no delay on the synth's insert
                foreach (var pt in vp.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
                var ve = new Engine { Proj = vp, Playing = !live, MidiTarget = vp.Channels[3] };
                if (live) ve.MidiIn.Enqueue(0x90 | 60 << 8 | vel << 16);
                else vp.Patterns[0].For(3).Add(new Note { Step = 0, Key = 60, Len = 4, Vel = vel });
                var vb = new float[Engine.SR / 5 * 2];
                ve.Render(vb, Engine.SR / 5);
                return vb.Max(x => Math.Abs(x));
            };
            double v100 = synthAt(100, false), v50 = synthAt(50, false), v127 = synthAt(127, false), k50 = synthAt(50, true) / synthAt(100, true);
            check(Math.Abs(v50 / v100 - 0.5) < 0.03 && v127 > v100 * 1.2 && Math.Abs(k50 - 0.5) < 0.03,
                "velocity sets the level: 50 plays at " + (v50 / v100).ToString("0.00") + "x of 100 from the pattern, " + k50.ToString("0.00") + "x live; 127 is louder");
            var vs = Project.Demo(); vs.Patterns[0].For(0)[0].Vel = 37; vs.Save(f);
            var vq = Project.Load(f);
            File.WriteAllText(f, "MINIDAW\t1\nch\tsynth\t0.8\t0\t0\tS\t\t1\npat\tP\nn\t0\t0\t60\t1\n");   // a note line from before velocity existed
            var vo = Project.Load(f); File.Delete(f);
            check(vq.Patterns[0].For(0)[0].Vel == 37 && vo.Patterns[0].For(0)[0].Vel == 100, "velocity survives save/load; older notes without one get 100");
            var pr = new Engine { Proj = Project.Demo(), Playing = true, MidiTarget = sp3.Channels[3] };
            for (int k = 0; k < 10; k++) pr.Render(blk, 1024);
            pr.MidiIn.Enqueue(0xB0 | 64 << 8 | 127 << 16); pr.MidiIn.Enqueue(0x90 | 62 << 8 | 90 << 16); pr.MidiIn.Enqueue(0x80 | 62 << 8);
            pr.Render(blk, 1024);
            int beforeUp = pr.MidiRec.Count;
            pr.MidiIn.Enqueue(0xB0 | 64 << 8);
            pr.Render(blk, 1024);
            var recd = pr.MidiRec.ToArray();
            check(beforeUp == 1 && recd.Length == 2 && (recd[1].Key & 0xF0) == 0x80 && (recd[1].Key >> 8 & 0x7F) == 62 && recd[1].Value > recd[0].Value,
                "while recording, the pedal holds the note open: its note-off is placed when the pedal comes up");
            check(MidiInput.Devices() != null, "MIDI device list works (" + MidiInput.Devices().Count + " input(s) on this computer)");
            var ph = Enumerable.Repeat(-1, PianoKeys.Length).ToArray();
            int zOn = PianoMsg(Keys.Z, true, ph, 0), zRepeat = PianoMsg(Keys.Z, true, ph, 0), zOff = PianoMsg(Keys.Z, false, ph, 0);
            Func<Keys, int, int> noteAt = (k, oct) => { int n = (PianoMsg(k, true, ph, oct) >> 8) & 0x7F; PianoMsg(k, false, ph, oct); return n; };
            check(zOn == (0x90 | 48 << 8 | 100 << 16) && zRepeat == 0 && zOff == (0x80 | 48 << 8)
                  && noteAt(Keys.M, 0) == 59 && noteAt(Keys.Q, 0) == 60 && noteAt(Keys.D2, 0) == 61 && noteAt(Keys.I, 0) == 72 && PianoMsg(Keys.A, true, ph, 0) == 0,
                "computer keyboard: Z = C4, M = B4, Q = C5, 2 = C#5, I = C6; auto-repeat ignored; other keys aren't notes");
            int heldOn = PianoMsg(Keys.Z, true, ph, 0), heldOff = PianoMsg(Keys.Z, false, ph, 1);   // the octave moves while Z is held
            check(noteAt(Keys.Z, 1) == 60 && noteAt(Keys.Z, -3) == 12 && noteAt(Keys.I, 3) == 108 && heldOff == (0x80 | 48 << 8) && (heldOn >> 8 & 0x7F) == 48
                  && NoteName(48) == "C4" && NoteName(73) == "C#6",
                "octave shift: Z plays C5 one octave up, C1 three down, I reaches C9 three up; a key held across a shift releases the note it started");

            // undo/redo history: steps only for gestures that changed something; a new edit clears redo; capped at 100
            var h = new History(); string cur = "a";
            Func<string> snapCur = () => cur;
            h.Begin(snapCur); h.End(snapCur);                    // click that changed nothing
            bool noStep = h.Undo(snapCur) == null;
            h.Begin(snapCur); cur = "b"; h.End(snapCur);
            h.Begin(snapCur); cur = "c"; h.End(snapCur);
            string u1 = h.Undo(snapCur); cur = u1; string u2 = h.Undo(snapCur); cur = u2; string u3 = h.Undo(snapCur);
            string r1 = h.Redo(snapCur); cur = r1; string r2 = h.Redo(snapCur); cur = r2; string r3 = h.Redo(snapCur);
            cur = h.Undo(snapCur);                                // back to "b"
            h.Begin(snapCur); cur = "d"; h.End(snapCur);          // new edit after undo
            bool redoCleared = h.Redo(snapCur) == null;
            check(noStep && u1 == "b" && u2 == "a" && u3 == null && r1 == "b" && r2 == "c" && r3 == null && redoCleared,
                "undo history: no-op clicks leave no step; undo b->a, redo a->b->c; a new edit clears redo");
            h.Clear(); cur = "0";
            for (int k = 1; k <= 150; k++) { h.Begin(snapCur); cur = k.ToString(); h.End(snapCur); }
            int undos = 0; string prev; while ((prev = h.Undo(snapCur)) != null) { cur = prev; undos++; }
            check(undos == 100 && cur == "50", "undo history keeps the last 100 steps");
            var sp2 = Project.Demo(); sp2.Channels.Add(new Channel { Kind = "sample", Name = "Mem", SamplePath = @"C:\no\such\file.wav", Sample = new float[] { 0.5f, -0.5f }, SampleRate = 22050 });
            var rp = Project.Parse(sp2.Lines().ToArray(), sp2);
            check(string.Join("\n", rp.Lines()) == string.Join("\n", sp2.Lines()) && rp.Warnings.Count == 0 && rp.Channels[4].Sample == sp2.Channels[4].Sample
                  && rp.Mixer[2].Slots[0] != sp2.Mixer[2].Slots[0], "undo snapshot restores the project exactly, reuses samples in memory, and gives effects fresh state");

            // deleting a channel shifts later channels' notes down; deleting a pattern fixes the playlist
            var dc = Project.Demo(); dc.RemoveChannel(1);   // snare
            var dcBack = Project.Parse(dc.Lines().ToArray(), null);
            check(dc.Channels.Count == 3 && dc.Channels[1].Name == "Hat" && dc.Patterns[0].For(1).Count == 4 && dc.Patterns[0].For(1)[0].Step == 2
                  && dc.Patterns[1].For(2).Count == 4 && dcBack.Channels.Count == 3, "deleting the Snare keeps Hat and Synth notes on the right channels, and the project still saves and loads");
            var dpat = Project.Demo(); dpat.RemovePattern(0);
            check(dpat.Patterns.Count == 1 && dpat.Patterns[0].Name == "Pattern 2" && dpat.Playlist[0, 0] == -1 && dpat.Playlist[0, 3] == -1
                  && dpat.Playlist[1, 2] == 0 && dpat.Playlist[1, 5] == 0 && dpat.SongBars() == 6, "deleting Pattern 1 clears its playlist cells and renumbers Pattern 2's");

            // longer patterns: Pattern 1 made 2 bars long with a single kick on step 25 (bar 2), placed at bar 1 of the song
            Func<Project> twoBar = () =>
            {
                var tp = Project.Demo();
                foreach (var pt in tp.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
                tp.Mixer[2].Slots[0] = tp.Mixer[4].Slots[0] = null;
                tp.Patterns[0].SetBars(2);
                tp.Patterns[0].For(0).Add(new Note { Step = 24, Key = 60, Len = 1 });
                for (int t = 0; t < Project.Tracks; t++) for (int b = 0; b < Project.Bars; b++) tp.Playlist[t, b] = -1;
                tp.Playlist[0, 0] = 0;
                return tp;
            };
            double sp16 = 60.0 / 128 / 4 * Engine.SR;
            Func<float[], int, int, double> peakIn = (a, from, to) => a.Skip((int)(from * sp16) * 2).Take((int)((to - from) * sp16) * 2).Max(x => Math.Abs(x));
            var pb = Engine.Bounce(twoBar().ExportCopy(), false, 0);
            var sb = Engine.Bounce(twoBar().ExportCopy(), true, 0);
            check(pb.Length == 2 * (int)Math.Ceiling(32 * sp16) && peakIn(pb, 0, 23) == 0 && peakIn(pb, 24, 26) > 0.1,
                "a 2-bar pattern exports as 2 bars and plays its bar-2 kick on step 25");
            check(sb.SequenceEqual(pb), "placed at bar 1 of the song, the 2-bar pattern plays through bar 2 (same audio as the pattern)");
            var cap = twoBar(); cap.Patterns[0].SetBars(4); cap.Playlist[0, 30] = 0;
            check(cap.ClipAt(0, 1) == 0 && cap.ClipAt(0, 4) == -1 && cap.ClipAt(0, 31) == 30 && cap.SongBars() == 34,
                "a clip covers as many bars as its pattern; a 4-bar clip at bar 31 makes the song 34 bars long");
            {   // playlist clips: stretched clips repeat their pattern, trimmed ones skip their front, muted tracks are silent, the loop region repeats
                Func<Project, double, double, int, float[]> clipSong = (pj, start, clen, bars) => { pj.Clips.Clear(); pj.Clips.Add(new Clip { Track = 0, Start = 0, Pattern = 0, Len = clen, Offset = start }); return Engine.Bounce(pj.ExportCopy(), true, 0); };
                Func<Project> kickOnly = () => { var pj = Project.Demo(); foreach (var pt in pj.Patterns) for (int c = 1; c < 4; c++) pt.For(c).Clear(); pj.Patterns[0].For(0).RemoveAll(n => n.Step != 0); return pj; };   // one kick, on step 1
                double s16b = 60.0 / 128 / 4 * Engine.SR;
                Func<float[], int, double> loudAt = (a, st) => a.Skip((int)(st * s16b) * 2).Take((int)(2 * s16b) * 2).DefaultIfEmpty(0).Max(x => Math.Abs(x));
                var stretched = clipSong(kickOnly(), 0, 48, 3); var trimmed = clipSong(kickOnly(), 8, 8, 1);
                var muted = kickOnly(); muted.Clips.Clear(); muted.Playlist[0, 0] = 0; muted.TrackAt(0).Mute = true; var mutedOut = Engine.Bounce(muted.ExportCopy(), true, 0);
                check(loudAt(stretched, 0) > 0.1 && loudAt(stretched, 16) > 0.1 && loudAt(stretched, 32) > 0.1 && loudAt(trimmed, 0) < 1e-4 && mutedOut.All(x => x == 0),
                    "playlist clips: one stretched to 3 bars repeats its 1-bar pattern (a kick in each bar); one trimmed to the pattern's 2nd half skips its kick; a muted track is silent");
                var loopProj = Project.Demo(); loopProj.LoopFrom = 16; loopProj.LoopTo = 32;
                var le2 = new Engine { Proj = loopProj, Playing = true, SongMode = true }; var lbk = new float[2048]; var seen = new List<int>();
                for (int k = 0; k < 120; k++) { le2.Render(lbk, 1024); if (seen.Count == 0 || seen[seen.Count - 1] != le2.PlayStep) seen.Add(le2.PlayStep); }
                int bar7, beats7, barB, beatsB; var ts = Project.Demo(); ts.Markers.Add(new Marker { At = 32, Beats = 3, Name = "3/4" });
                bool startA = ts.BarAt(32, out bar7, out beats7), startB = ts.BarAt(44, out barB, out beatsB), midBar = ts.BarAt(40, out barB, out beatsB);
                check(seen.First() == 16 && seen.Max() == 31 && seen.Min() == 16 && seen.Count(x => x == 16) >= 2 && startA && bar7 == 3 && beats7 == 3 && startB && !midBar,
                    "loop region bars 2-3: playback starts at its start and wraps back to it; a 3/4 marker at bar 3 makes bars 12 16ths long from there");
                var keep2 = Project.Demo(); keep2.Clips[1].Len = 40; keep2.Clips[1].Offset = 4; keep2.TrackAt(3).Name = "Bass"; keep2.TrackAt(3).Color = -16711936; keep2.TrackAt(1).Mute = true;
                keep2.Markers.Add(new Marker { At = 64, Name = "Chorus" }); keep2.Markers.Add(new Marker { At = 96, Beats = 7, Name = "7/4" }); keep2.LoopFrom = 32; keep2.LoopTo = 96;
                var back2 = Project.Parse(keep2.Lines().ToArray(), null);
                check(back2.Clips.Count == keep2.Clips.Count && back2.Clips[1].Len == 40 && back2.Clips[1].Offset == 4 && back2.Clips[0].Len == -1 && back2.TrackName(3) == "Bass" && back2.TrackAt(3).Color == -16711936
                      && back2.TrackMuted(1) && !back2.TrackMuted(0) && back2.Markers.Count == 2 && back2.Markers[1].Beats == 7 && back2.Markers[0].Name == "Chorus" && back2.LoopFrom == 32 && back2.LoopTo == 96,
                    "saved and opened again: clip lengths and trims, track names, colours and mutes, markers, time signatures and the loop region");
            }
            var sh = twoBar(); sh.Patterns[0].For(1).Add(new Note { Step = 12, Key = 60, Len = 8 }); sh.Patterns[0].SetBars(1);
            check(sh.Patterns[0].For(0).Count == 0 && sh.Patterns[0].For(1)[0].Len == 4, "shortening a pattern drops notes past its end and trims notes that run over");
            twoBar().Save(f);
            var lq = Project.Load(f);
            File.WriteAllText(f, "MINIDAW\t1\nch\tkick\t0.8\t0\t0\tK\t\t1\npat\tP\t1\nn\t0\t20\t60\t1\n");
            threw = false; try { Project.Load(f); } catch (InvalidDataException) { threw = true; }
            File.Delete(f);
            check(lq.Patterns[0].Length == 32 && lq.Patterns[0].For(0)[0].Step == 24 && lq.Patterns[1].Length == 16 && threw,
                "pattern length survives save/load; a note past a 1-bar pattern's end is rejected");

            // swing: a kick on an off-beat 16th (step 2) starts half a 16th later at full swing; an on-beat one (step 3) doesn't move
            Func<int, float, int> onset = (step, swing) =>
            {
                var wp = twoBar(); wp.Patterns[0].For(0).Clear(); wp.Patterns[0].For(0).Add(new Note { Step = step, Key = 60, Len = 1 }); wp.Swing = swing;
                var wb = Engine.Bounce(wp.ExportCopy(), false, 0);
                for (int k = 0; k < wb.Length; k += 2) if (wb[k] != 0) return k / 2;
                return -1;
            };
            int straightOff = onset(1, 0), swungOff = onset(1, 1), halfOff = onset(1, 0.5f), straightOn = onset(2, 0), swungOn = onset(2, 1);
            check(swungOff - straightOff == (int)Math.Round(0.5 * sp16) && halfOff - straightOff == (int)Math.Round(0.25 * sp16) && swungOn == straightOn && straightOff > 0,
                "swing 100% delays an off-beat 16th by " + (swungOff - straightOff) + " samples (half a 16th), 50% by a quarter; on-beat notes stay put");
            var swp = Project.Demo(); swp.Swing = 0.4f; swp.Save(f);
            float swBack = Project.Load(f).Swing; File.Delete(f);
            check(swBack == 0.4f && Project.Demo().Swing == 0, "swing survives save/load; new projects start straight");

            // paste and duplicate: the synth's 4 chord notes in Pattern 2, copied into a 2-bar pattern and duplicated into bar 2
            var cpj = Project.Demo(); cpj.Patterns[0].SetBars(2);
            var chord = cpj.Patterns[1].For(3);   // steps 0, 4, 8, 12; each 3 steps long; velocity 100
            chord[1].Vel = 70;
            var pasted = cpj.Patterns[0].Paste(3, chord, 0);
            double dup = Pattern.DupOffset(pasted);
            var duped = cpj.Patterns[0].Paste(3, pasted, dup);
            var again = cpj.Patterns[0].Paste(3, duped, dup);   // a third copy would start at step 32: past the 2-bar end
            var p0 = cpj.Patterns[0].For(3);
            check(pasted.Count == 4 && dup == 16 && duped.Count == 4 && again.Count == 0 && p0.Count == 8 && p0.Count(n => n.Step == 20 && n.Vel == 70) == 1
                  && chord.Count == 4 && !p0.Intersect(chord).Any(),
                "pasting copies the 4 notes (velocity too), Ctrl+B puts the next copy 16 steps later, copies past the end are dropped, the source is untouched");
            cpj.Patterns[0].Paste(3, new[] { new Note { Step = 4, Key = chord[1].Key, Len = 2 } }, 0);
            check(cpj.Patterns[0].For(3).Count(n => n.Step == 4 && n.Key == chord[1].Key) == 1 && cpj.Patterns[0].For(3).First(n => n.Step == 4).Len == 2
                  && Pattern.DupOffset(new[] { new Note { Step = 2, Len = 3 } }) == 4, "a pasted note replaces one on the same step and key; a short selection duplicates one beat later");
            var mvA = new Note { Step = 2, Key = 60, Len = 4 }; var mvB = new Note { Step = 10, Key = 125, Len = 2 };
            var mvFrom = new Dictionary<Note, Note> { { mvA, mvA.Clone() }, { mvB, mvB.Clone() } };
            Func<double, int, string> clampTo = (ds0, dk0) => { double ds1 = ds0; int dk1 = dk0; Pattern.ClampMove(mvFrom, 16, ref ds1, ref dk1); return ds1 + "," + dk1; };
            check(clampTo(3, 1) == "3,1" && clampTo(9, 5) == "4,2" && clampTo(-9, -70) == "-2,-60" && clampTo(2.5, 0) == "2.5,0",
                "dragging two notes stops at the pattern's edges and the top/bottom of the MIDI range, keeping their spacing");

            // every built-in sound plays, stays under full scale and ends after its note; the pitched ones play A4 in tune
            var soundLog = new List<string>(); double worstTune = 1, worstHalf = -1;
            Func<float[], int, double> match = (x, lag) =>   // normalised correlation of the left channel with itself shifted by lag, 0.1..0.3 s in
            {
                double num = 0, d1 = 0, d2 = 0;
                for (int i = Engine.SR / 10; i < Engine.SR * 3 / 10; i++) { double x0 = x[2 * i], x1 = x[2 * (i + lag)]; num += x0 * x1; d1 += x0 * x0; d2 += x1 * x1; }
                return num / Math.Sqrt(d1 * d2);
            };
            foreach (var kind in Channel.Sounds)
            {
                var sp4 = new Project(); sp4.Channels.Add(new Channel { Kind = kind, Name = kind, Insert = 1 });
                if (kind == "analog") { var plain = (float[])Channel.SynPresets[0].Clone(); plain[6] = plain[9] = plain[25] = 0; sp4.Channels[0].Syn = plain; }   // one saw: the preset's detuned 2nd oscillator, sub and unison never repeat exactly
                var pat4 = new Pattern { Name = "P" }; sp4.Patterns.Add(pat4);
                pat4.For(0).Add(new Note { Step = 0, Key = 69, Len = 16 });   // A4 = 440 Hz, held for the whole bar
                var a4 = Engine.Bounce(sp4, false, 0);
                double pk = a4.Max(x => Math.Abs(x));
                bool ends = a4.Length / 2 < (int)Math.Ceiling(16 * sp16) + 3 * Engine.SR;
                if (a4.Any(x => float.IsNaN(x)) || pk < 0.05 || pk > 0.9 || !ends) soundLog.Add(kind + " peak " + pk.ToString("0.00") + (ends ? "" : ", never ends"));
                if (Array.IndexOf(new[] { "kick", "snare", "hat", "clap" }, kind) >= 0) continue;
                int lag = (int)Math.Round(Engine.SR / (kind == "bass" ? 220.0 : 440.0));   // the bass adds a sine an octave down, so it repeats at 220 Hz
                worstTune = Math.Min(worstTune, match(a4, lag));
                worstHalf = Math.Max(worstHalf, match(a4, lag / 2));   // an octave too high would also repeat at half the period
                soundLog.Add(kind + " " + match(a4, lag).ToString("0.00") + "/" + match(a4, lag / 2).ToString("0.00"));
            }
            string tuneInfo = string.Join(", ", soundLog.Where(l => !l.Contains("peak")));
            soundLog.RemoveAll(l => !l.Contains("peak"));
            check(soundLog.Count == 0, "all " + Channel.Sounds.Length + " built-in sounds play, stay under full scale and end after their note" + (soundLog.Count > 0 ? ": " + string.Join("; ", soundLog) : ""));
            // pitch wheel: bends a keyboard note that's already sounding by up to ±2 semitones; pattern notes don't bend
            Func<float[], int> periodOf = x => Enumerable.Range(60, 81).OrderByDescending(l => match(x, l)).First();   // best-repeating lag, 60..140 samples
            Func<int, bool, int> bent = (wheel, fromPattern) =>
            {
                var bp = Project.Demo(); bp.Mixer[4].Slots[0] = null;
                foreach (var pt in bp.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
                if (fromPattern) bp.Patterns[0].For(3).Add(new Note { Step = 0, Key = 69, Len = 16 });
                var be = new Engine { Proj = bp, Playing = fromPattern, MidiTarget = bp.Channels[3] };
                if (!fromPattern) be.MidiIn.Enqueue(0x90 | 69 << 8 | 100 << 16);
                var bb = new float[Engine.SR];
                be.Render(bb, Engine.SR / 10);   // the A4 is sounding before the wheel moves
                be.MidiIn.Enqueue(0xE0 | (wheel & 0x7F) << 8 | (wheel >> 7) << 16);
                be.Render(bb, Engine.SR / 2);
                return periodOf(bb);
            };
            int pCentre = bent(8192, false), pUp = bent(16383, false), pDown = bent(0, false), pPattern = bent(16383, true);
            check(pCentre == 100 && pUp == 89 && Math.Abs(pDown - 112.5) <= 1 && pPattern == 100,
                "pitch wheel bends a sounding A4 (period " + pCentre + " samples) up 2 semitones to B4 (" + pUp + ") and down to G4 (" + pDown + "); a pattern note stays put (" + pPattern + ")");
            // mod wheel: vibrato wobbles a live note's pitch around the same centre; with the wheel down, and for pattern notes, the pitch stays steady
            Func<int, bool, float[]> modded = (wheel, fromPattern) =>
            {
                var vibP = Project.Demo(); vibP.Mixer[4].Slots[0] = null;
                foreach (var pt in vibP.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
                if (fromPattern) vibP.Patterns[0].For(3).Add(new Note { Step = 0, Key = 69, Len = 16 });
                var vibE = new Engine { Proj = vibP, Playing = fromPattern, MidiTarget = vibP.Channels[3] };
                vibE.MidiIn.Enqueue(0xB0 | 1 << 8 | wheel << 16);
                if (!fromPattern) vibE.MidiIn.Enqueue(0x90 | 69 << 8 | 100 << 16);
                var vibB = new float[Engine.SR];
                vibE.Render(vibB, Engine.SR / 2);
                return vibB;
            };
            double steady = match(modded(0, false), 100), wobble = match(modded(127, false), 100), patSteady = match(modded(127, true), 100);
            int wobbleCentre = periodOf(modded(127, false));
            check(steady > 0.995 && wobble < 0.985 && patSteady > 0.995 && Math.Abs(wobbleCentre - 100) <= 1,   // steady is just under 1: A4's true period is 100.23 samples
                "mod wheel adds vibrato to a live A4 (how exactly it repeats every 100 samples falls from " + steady.ToString("0.000") + " to " + wobble.ToString("0.000")
                + "; it still centres on " + wobbleCentre + "); a pattern note stays steady (" + patSteady.ToString("0.000") + ")");
            // sound settings: the built-in knob positions give exactly the old sounds; each knob does what its label says
            var ksyn = new Channel { Kind = "synth" }; var kpad = new Channel { Kind = "pad" }; var kpl = new Channel { Kind = "pluck" };
            check(ksyn.AttackSamples() == 220 && ksyn.ReleaseSamples() == 3500 && kpad.AttackSamples() == Engine.SR * 3 / 10 && kpad.ReleaseSamples() == Engine.SR * 6 / 10
                  && kpl.AttackSamples() == 1 && kpl.ReleaseSamples() == 2500 && ksyn.Bright() == 1 && ksyn.Semitones() == 0,
                "built-in knob positions give the original envelopes (saw 220/3500 samples, pad 0.3/0.6 s, pluck 1/2500), tone 1x, pitch 0");
            Func<string, float[], float[]> playWith = (kind, knob) =>   // one channel holding A4 for a bar, with these knob values
            {
                var kp = new Project(); kp.Channels.Add(new Channel { Kind = kind, Name = kind, Insert = 1, Knob = knob });
                var kpt = new Pattern { Name = "P" }; kp.Patterns.Add(kpt);
                kpt.For(0).Add(new Note { Step = 0, Key = 69, Len = 16 });
                return Engine.Bounce(kp, false, 0);
            };
            Func<string, int, float, float[]> knobAt = (kind, which, val) => { var kv2 = (float[])Channel.BuiltIn(kind).Clone(); kv2[which] = val; return kv2; };
            Func<float[], double> hf = x =>   // share of high-frequency energy, 0.1..0.3 s in
            {
                double hdiff = 0, hsum = 0;
                for (int i = Engine.SR / 10; i < Engine.SR * 3 / 10; i++) { double hv0 = x[2 * i], hv1 = x[2 * i - 2]; hdiff += (hv0 - hv1) * (hv0 - hv1); hsum += hv0 * hv0; }
                return hdiff / hsum;
            };
            int pitched = periodOf(playWith("synth", knobAt("synth", Channel.KPitch, 0.5f + 3 / 48f)));
            double dark = hf(playWith("synth", knobAt("synth", Channel.KTone, 0))), midTone = hf(playWith("synth", null)), bright = hf(playWith("synth", knobAt("synth", Channel.KTone, 1)));
            float[] slowA = playWith("synth", knobAt("synth", Channel.KAttack, 1)), normal = playWith("synth", null);
            double slowAt = slowA.Skip(Engine.SR / 10 * 2).Take(400).Max(x => Math.Abs(x)), normalAt = normal.Skip(Engine.SR / 10 * 2).Take(400).Max(x => Math.Abs(x));
            int body16 = (int)Math.Ceiling(16 * sp16);
            int longRel = playWith("synth", knobAt("synth", Channel.KRelease, 1)).Length / 2 - body16, normRel = normal.Length / 2 - body16;
            check(pitched == 84 && dark < midTone && midTone < bright && slowAt < normalAt * 0.2 && longRel > 2 * Engine.SR && normRel < Engine.SR / 10,
                "knobs work: Pitch +3 plays C5 (period " + pitched + " samples); Tone darkens and brightens (high-frequency share " + dark.ToString("0.0000") + " < " + midTone.ToString("0.0000")
                + " < " + bright.ToString("0.0000") + "); a 2 s Attack is still quiet at 0.1 s; a 3 s Release rings " + (longRel / (double)Engine.SR).ToString("0.0") + " s past the note");
            int kickNorm = playWith("kick", null).Length, kickLong = playWith("kick", knobAt("kick", Channel.KTone, 1)).Length;
            var ksv = Project.Demo(); ksv.Channels[3].Knob = knobAt("synth", Channel.KTone, 0.8f); ksv.Save(f);
            var ksq = Project.Load(f); File.Delete(f);
            check(kickLong > kickNorm && ksq.Channels[3].K(Channel.KTone) == 0.8f && ksq.Channels[0].K(Channel.KAttack) == Channel.BuiltIn("kick")[0],
                "drum Decay lengthens the kick; sound settings survive save/load");
            // automating a sound setting: a Tone lane that is dark in bar 1 and bright in bar 2; exporting only changes the export's copy
            var tp2 = new Project(); tp2.Channels.Add(new Channel { Kind = "synth", Name = "S", Insert = 1 });
            var tpat = new Pattern { Name = "P" }; tp2.Patterns.Add(tpat); tpat.For(0).Add(new Note { Step = 0, Key = 69, Len = 16 });
            tp2.Playlist[0, 0] = tp2.Playlist[0, 1] = 0;
            var toneLane = new Lane { Ch = 0, Param = Channel.KTone };
            for (int k = 0; k < toneLane.Values.Length; k++) toneLane.Values[k] = k < Project.Steps ? 0.1f : 0.9f;
            tp2.Automation.Add(toneLane);
            var tb = Engine.Bounce(tp2.ExportCopy(), true, 0);
            Func<int, double> hfFrom = frame =>   // high-frequency share over 0.2 s from this frame
            {
                double hd2 = 0, hs2 = 0;
                for (int i = frame; i < frame + Engine.SR / 5; i++) { double q0 = tb[2 * i], q1 = tb[2 * i - 2]; hd2 += (q0 - q1) * (q0 - q1); hs2 += q0 * q0; }
                return hd2 / hs2;
            };
            double bar1 = hfFrom(Engine.SR / 10), bar2 = hfFrom(body16 + Engine.SR / 10);
            check(bar2 > bar1 * 3 && tp2.Channels[0].Knob == null && toneLane.Name(tp2) == "S · Tone",
                "a Tone lane makes bar 2 brighter than bar 1 in the export (high-frequency share " + bar1.ToString("0.0000") + " -> " + bar2.ToString("0.0000") + ") and leaves the live channel alone");
            var dl = Project.Demo();
            dl.Automation.Add(new Lane { Ch = 1, Param = Channel.KTone }); dl.Automation.Add(new Lane { Ch = 3, Param = Channel.KPitch }); dl.Automation[1].Values[5] = 0.7f;
            dl.Save(f);
            var dlq = Project.Load(f); File.Delete(f);
            dl.RemoveChannel(1);
            check(dlq.Automation.Count == 2 && dlq.Automation[1].Ch == 3 && dlq.Automation[1].Param == Channel.KPitch && dlq.Automation[1].Values[5] == 0.7f
                  && dl.Automation.Count == 1 && dl.Automation[0].Ch == 2,
                "sound-setting lanes survive save/load; deleting a channel drops its lanes and renumbers later channels' lanes");
            // rack volume and pan lanes, on the demo's Kick playing alone
            Func<int, float, float[]> rackLane = (param, first) =>   // lane value "first" for bar 1, 0 afterwards
            {
                var rl = Project.Demo();
                foreach (var pt in rl.Patterns) for (int c = 1; c < 4; c++) pt.For(c).Clear();
                var lane = new Lane { Ch = 0, Param = param };
                for (int k = 0; k < lane.Values.Length; k++) lane.Values[k] = k < Project.Steps ? first : 0;
                rl.Automation.Add(lane);
                return Engine.Bounce(rl.ExportCopy(), true, 0);
            };
            var volOut = rackLane(Channel.LaneVol, 0.8f); var panOut = rackLane(Channel.LanePan, 0);   // pan lane 0 = hard left throughout
            var rlive = Project.Demo(); var rlane = new Lane { Ch = 0, Param = Channel.LaneVol }; rlive.Automation.Add(rlane);
            Engine.Bounce(rlive.ExportCopy(), true, 0);
            var rs = Project.Demo(); rs.Automation.Add(new Lane { Ch = 2, Param = Channel.LanePan }); rs.Save(f);
            var rsq = Project.Load(f); File.Delete(f);
            check(volOut.Take(15 * sps128 * 2).Max(x => Math.Abs(x)) > 0.1 && volOut.Skip(17 * sps128 * 2).Take(14 * sps128 * 2).All(x => x == 0)
                  && panOut.Where((x, i) => i % 2 == 1).All(x => x == 0) && panOut.Where((x, i) => i % 2 == 0).Max(x => Math.Abs(x)) > 0.1
                  && rlive.Channels[0].Vol == 0.8f && rlane.Name(rlive) == "Kick · Volume" && rsq.Automation[0].Name(rsq) == "Hat · Pan",
                "rack lanes: the Kick's volume lane silences bar 2, its pan lane at hard left leaves the right speaker silent; exports leave the live rack alone; they save and load");
            // tempo lane: the demo kick for 2 bars, bar 1 at 120 BPM (2 s) and bar 2 at 60 BPM (4 s)
            var tpo = Project.Demo();
            foreach (var pt in tpo.Patterns) for (int c = 1; c < 4; c++) pt.For(c).Clear();
            for (int t = 0; t < Project.Tracks; t++) for (int b = 0; b < Project.Bars; b++) tpo.Playlist[t, b] = -1;
            tpo.Playlist[0, 0] = tpo.Playlist[0, 1] = 0;
            var tl = new Lane { Tempo = true };
            for (int k = 0; k < tl.Values.Length; k++) tl.Values[k] = ((k < Project.Steps ? 120 : 60) - Project.MinBpm) / (float)(Project.MaxBpm - Project.MinBpm);
            tpo.Automation.Add(tl);
            double tsec = Engine.Bounce(tpo.ExportCopy(), true, 0).Length / 2.0 / Engine.SR;
            tpo.Save(f);
            var tpq = Project.Load(f); File.Delete(f);
            check(tsec > 5.9 && tsec < 6.3 && tpo.Bpm == 128 && tl.Name(tpo) == "Tempo (BPM)" && tpq.Automation[0].Tempo && tpq.Automation[0].Values.SequenceEqual(tl.Values),
                "a tempo lane (120 BPM, then 60) stretches the 2-bar export to " + tsec.ToString("0.00") + " s (2 s + 4 s); the live BPM stays 128; it saves and loads");
            // 8 effect slots: a 40 Hz low-pass in the last slot of the synth's insert still filters, and that slot saves and loads
            var e8 = Project.Demo();
            foreach (var pt in e8.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
            e8.Patterns[0].For(3).Add(new Note { Step = 0, Key = 81, Len = 16 });   // A5 on the synth, insert 4
            e8.Mixer[4].Slots[0] = null;
            var lp8 = new Effect("Filter"); lp8.P[0] = 0; lp8.P[1] = 0; lp8.P[2] = 0;
            e8.Mixer[4].Slots[Insert.SlotCount - 1] = lp8;
            double filtered = Engine.Bounce(e8.ExportCopy(), false, 0).Max(x => Math.Abs(x));
            e8.Mixer[4].Slots[Insert.SlotCount - 1] = null;
            double unfiltered = Engine.Bounce(e8.ExportCopy(), false, 0).Max(x => Math.Abs(x));
            e8.Mixer[4].Slots[Insert.SlotCount - 1] = lp8; e8.Save(f);
            var q8 = Project.Load(f); File.Delete(f);
            check(Insert.SlotCount == 8 && filtered < unfiltered * 0.1 && q8.Mixer[4].Slots[7] != null && q8.Mixer[4].Slots[7].Type == "Filter",
                "8 effect slots per insert: a 40 Hz low-pass in slot 8 cuts the synth to " + (filtered / unfiltered).ToString("0.000") + " of its level, and slot 8 saves and loads");
            // bypass: the same low-pass switched off sounds exactly like no effect at all, keeps its settings, and the off state saves and loads
            lp8.Bypass = true;
            var dry8 = Engine.Bounce(e8.ExportCopy(), false, 0);
            e8.Mixer[4].Slots[Insert.SlotCount - 1] = null;
            bool sameAsEmpty = dry8.SequenceEqual(Engine.Bounce(e8.ExportCopy(), false, 0));
            e8.Mixer[4].Slots[Insert.SlotCount - 1] = lp8; e8.Save(f);
            var qb = Project.Load(f); File.Delete(f);
            check(sameAsEmpty && lp8.P[0] == 0 && qb.Mixer[4].Slots[7].Bypass && !q8.Mixer[4].Slots[7].Bypass,
                "a bypassed effect sounds exactly like an empty slot and keeps its settings; the on/off state saves and loads");
            // reordering: low-pass then distortion sounds different from distortion then low-pass; moving the effect gives exactly the
            // reversed chain, an automation lane follows its effect, and empty slots shift along with the rest
            Func<bool, Project> twoFx = reversed =>
            {
                var fxp = Project.Demo();
                foreach (var pt in fxp.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
                fxp.Patterns[0].For(3).Add(new Note { Step = 0, Key = 69, Len = 16 });
                var lpf = new Effect("Filter"); lpf.P[0] = 0; lpf.P[1] = 0; lpf.P[2] = 0;
                var dst = new Effect("Distortion"); dst.P[0] = 1; dst.P[1] = 1; dst.P[2] = 1;
                fxp.Mixer[4].Slots[0] = reversed ? dst : lpf; fxp.Mixer[4].Slots[1] = reversed ? lpf : dst;
                return fxp;
            };
            var mv = twoFx(false); var drive = new Lane { Ins = 4, Slot = 1, Param = 0 }; mv.Automation.Add(drive);
            var beforeMove = Engine.Bounce(mv.ExportCopy(), false, 0);
            mv.MoveEffect(4, 1, 0);
            var afterMove = Engine.Bounce(mv.ExportCopy(), false, 0);
            var gp = Project.Demo(); Effect ea = new Effect("EQ"), eb = new Effect("Delay"), ec = new Effect("Reverb");
            gp.Mixer[3].Slots = new[] { ea, eb, null, ec, null, null, null, null };
            gp.MoveEffect(3, 0, 5);
            check(!afterMove.SequenceEqual(beforeMove) && afterMove.SequenceEqual(Engine.Bounce(twoFx(true).ExportCopy(), false, 0)) && mv.Mixer[4].Slots[0].Type == "Distortion"
                  && drive.Slot == 0 && gp.Mixer[3].Slots[0] == eb && gp.Mixer[3].Slots[1] == null && gp.Mixer[3].Slots[2] == ec && gp.Mixer[3].Slots[5] == ea,
                "moving Distortion above the low-pass gives exactly the reversed chain's sound; its automation lane follows it; moving slot 1 to slot 6 shifts the rest up");
            // wet/dry mix: 0 leaves the sound untouched (in the full export too), 50% lands exactly halfway between dry and fully processed
            var sig = Enumerable.Range(0, Engine.SR).Select(k => (float)(0.05 * Math.Sin(2 * Math.PI * 100 * (k / 2) / Engine.SR))).ToArray();   // a quiet stereo tone
            Func<float, float[]> distAt = mixv =>
            {
                var dd = new Effect("Distortion"); dd.P[0] = 1; dd.P[1] = 1; dd.P[2] = 1; dd.Mix = mixv;
                var xs2 = (float[])sig.Clone(); float[] scratch = null;
                dd.Run(xs2, xs2.Length, null, ref scratch);
                return xs2;
            };
            float[] wetOut = distAt(1), halfOut = distAt(0.5f), dryOut = distAt(0);
            bool halfway = Enumerable.Range(0, sig.Length).All(k => Math.Abs(halfOut[k] - (sig[k] + wetOut[k]) / 2) < 1e-5);
            lp8.Bypass = false; lp8.Mix = 0; e8.Mixer[4].Slots[Insert.SlotCount - 1] = lp8;
            bool zeroIsEmpty = Engine.Bounce(e8.ExportCopy(), false, 0).SequenceEqual(dry8);
            lp8.Mix = 0.3f; var wetLane = new Lane { Ins = 4, Slot = Insert.SlotCount - 1, Param = Lane.FxMix }; e8.Automation.Add(wetLane); e8.Save(f);
            var qm = Project.Load(f); File.Delete(f);
            check(halfway && dryOut.SequenceEqual(sig) && !wetOut.SequenceEqual(sig) && zeroIsEmpty && qm.Mixer[4].Slots[7].Mix == 0.3f
                  && qm.Automation.Last().Param == Lane.FxMix && wetLane.Name(e8) == "Insert 4 · Filter Wet/dry",
                "effect mix: 0% leaves the sound exactly untouched, even in an export; 50% is exactly halfway between dry and processed; mix and its lane save and load");
            // recent projects: newest first, at most 8, no repeats (ignoring case); tested on a temp file, never the user's real list
            string realRecent = Recent.FilePath;
            Recent.FilePath = Path.Combine(Path.GetTempPath(), "minidaw_recent_test.txt");
            try
            {
                File.Delete(Recent.FilePath);
                for (int k = 1; k <= 10; k++) Recent.Add(Path.Combine(Path.GetTempPath(), "song" + k + ".mdaw"));
                Recent.Add(Path.Combine(Path.GetTempPath(), "SONG5.mdaw"));   // already listed: moves to the top instead of repeating
                var rl2 = Recent.Load();
                Recent.Remove(rl2[1]);
                var rl3 = Recent.Load();
                check(rl2.Count == 8 && rl2[0].EndsWith("SONG5.mdaw") && rl2[1].EndsWith("song10.mdaw") && rl2.Count(x => x.ToLowerInvariant().EndsWith("song5.mdaw")) == 1
                      && !rl2.Any(x => x.EndsWith("song1.mdaw") || x.EndsWith("song2.mdaw")) && rl3.Count == 7 && rl3[1].EndsWith("song9.mdaw"),
                    "recent projects: newest first, at most 8; opening one again moves it to the top without repeating it; a removed one disappears");
            }
            finally { File.Delete(Recent.FilePath); Recent.FilePath = realRecent; }
            // autosave file = the project's own lines plus an "origin" line; it must read back as exactly the same project
            var orig = Project.Demo(); orig.Bpm = 111; orig.Patterns[0].For(3).Add(new Note { Step = 5, Key = 70, Len = 2, Vel = 64 });
            var withOrigin = orig.Lines(); withOrigin.Add("origin\tC:\\Music\\My song.mdaw");
            check(string.Join("\n", Project.Parse(withOrigin.ToArray(), null).Lines()) == string.Join("\n", orig.Lines()),
                "an autosave (the project plus where it came from) reads back as exactly the same project");
            check(Droppable(new[] { @"C:\x\Kick.WAV" }) && Droppable(new[] { "notes.txt", "song.mdaw" }) && !Droppable(new[] { "notes.txt", "photo.jpg" }) && !Droppable(null),
                "drag-and-drop accepts WAV and project files (any case) and refuses a drop with neither");
            var mc = Project.Demo();   // track 1: Pattern 1 on bars 1-4; track 2: Pattern 2 on bars 3-6
            mc.MoveClip(1, 5, 4, 10, false); mc.MoveClip(0, 0, 0, 20, true); mc.MoveClip(2, 2, 3, 3, false);   // the last one: nothing there
            check(mc.Playlist[1, 5] == -1 && mc.Playlist[4, 10] == 1 && mc.Playlist[0, 0] == 0 && mc.Playlist[0, 20] == 0 && mc.Playlist[3, 3] == -1 && mc.SongBars() == 21,
                "moving a playlist clip empties its old place; copying keeps it; the song grows to reach the moved clip");
            // logo: a yellow banana on a transparent square, and a well-formed .ico with 5 sizes
            using (var logo = Logo.Draw(64))
            {
                var ico = Logo.Ico();
                int yellow = 0; for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) { var px = logo.GetPixel(x, y); if (px.A > 200 && px.R > 200 && px.G > 150 && px.B < 140) yellow++; }
                check(logo.GetPixel(0, 0).A == 0 && logo.GetPixel(63, 63).A == 0 && yellow > 250 && BitConverter.ToInt16(ico, 2) == 1 && BitConverter.ToInt16(ico, 4) == 5 && ico[6] == 16
                      && ico[6 + 16 * 4] == 0 && BitConverter.ToInt32(ico, 6 + 16 * 4 + 12) + BitConverter.ToInt32(ico, 6 + 16 * 4 + 8) == ico.Length,
                    "logo: " + yellow + " yellow pixels of 64x64 with transparent corners; the .ico holds 16..256 px images and its last image ends at the end of the file");
            }
            check(worstTune > 0.8 && worstHalf < 0.5, "the pitched sounds play A4 in tune: each repeats every 1/440 s (bass 1/220 s) and not every half of that (match full/half: " + tuneInfo + ")");

            // mixer routing: only the kick plays, and it feeds insert 1
            var r = Project.Demo();
            foreach (var pat in r.Patterns) for (int c = 1; c < 4; c++) pat.For(c).Clear();
            r.Mixer[1].Mute = true;
            var rb = new float[Engine.SR];
            new Engine { Proj = r, Playing = true }.Render(rb, Engine.SR / 2);
            check(rb.All(v => v == 0), "muting insert 1 silences the kick routed to it");
            r.Mixer[1].Mute = false;
            var re = new Engine { Proj = r, Playing = true };
            re.Render(rb, Engine.SR / 2);
            check(rb.Max(v => Math.Abs(v)) > 0.1 && re.Peaks[1] > 0.1 && re.Peaks[3] == 0, "insert 1 passes the kick and meters it; insert 3 stays silent");
            check(re.Peaks[5] == 0, "with no send, insert 5 stays silent");
            r.Mixer[1].SendTo = 5; r.Mixer[1].SendLevel = 1;
            var se = new Engine { Proj = r, Playing = true };
            se.Render(rb, Engine.SR / 2);
            check(se.Peaks[5] > 0.1 && Math.Abs(se.Peaks[5] - se.Peaks[1]) < 1e-4, "send copies insert 1 into insert 5 at full level");

            // sidechain: the kick on insert 1 drives the Compressor on insert 4, which holds one long synth note
            Func<bool, double> synthPeak = withKick =>
            {
                var sp = Project.Demo();
                foreach (var pt in sp.Patterns) for (int c = 0; c < 4; c++) pt.For(c).Clear();
                if (withKick) sp.Patterns[0].For(0).Add(new Note { Step = 0, Key = 60, Len = 1 });
                sp.Patterns[0].For(3).Add(new Note { Step = 0, Key = 60, Len = 16 });
                var cm = new Effect("Compressor"); cm.P[0] = 1; cm.P[1] = 1; cm.P[2] = 0;   // -48 dB, 20:1, no make-up
                sp.Mixer[4].Slots[0] = cm; sp.Mixer[4].Sidechain = 1;
                var en = new Engine { Proj = sp, Playing = true };
                en.Render(new float[Engine.SR / 5 * 2], Engine.SR / 5);
                return en.Peaks[4];
            };
            double ducked = synthPeak(true), open = synthPeak(false);
            check(open > 0.1 && ducked < 0.3 * open, "sidechain: kick ducks the synth to " + ducked.ToString("0.00") + "; without the kick the synth stays at " + open.ToString("0.00"));

            // effects: stable at parameter extremes, and each one does its job
            foreach (var type in Effect.Types)
                foreach (var val in new[] { 0f, 1f })
                {
                    var fx = new Effect(type);
                    for (int j = 0; j < 3; j++) fx.P[j] = val;
                    var xs = new float[Engine.SR * 2];   // 1 s stereo: 0.1 s noise burst, then silence
                    var rnd = new Random(1);
                    for (int k = 0; k < 4410 * 2; k++) xs[k] = (float)(rnd.NextDouble() * 2 - 1);
                    fx.Process(xs, xs.Length);
                    check(xs.All(v => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 100), type + " stays stable with every setting at " + val);
                }
            var dly = new Effect("Delay"); dly.P[0] = 0.5f; dly.P[1] = 0; dly.P[2] = 1;
            var imp = new float[Engine.SR * 2]; imp[0] = imp[1] = 1;
            dly.Process(imp, imp.Length);
            int dSamples = (int)((0.02 + 0.5 * 0.98) * Engine.SR);
            check(Math.Abs(imp[2 * dSamples] - 1) < 1e-6 && Math.Abs(imp[2 * dSamples + 1] - 1) < 1e-6, "delay echo lands at " + dly.Label(0));
            var rev = new Effect("Reverb");
            var tail = new float[Engine.SR * 2]; tail[0] = tail[1] = 1;
            rev.Process(tail, tail.Length);
            check(tail.Skip(Engine.SR).Max(v => Math.Abs(v)) > 1e-4, "reverb still rings 0.5 s after a click");
            var lp = new Effect("Filter"); lp.P[0] = 0; lp.P[1] = 0; lp.P[2] = 0;   // 40 Hz low-pass
            var sine = Enumerable.Range(0, Engine.SR).Select(k => (float)Math.Sin(2 * Math.PI * 5000 * (k / 2) / Engine.SR)).ToArray();
            lp.Process(sine, sine.Length);
            check(sine.Skip(Engine.SR / 2).Max(v => Math.Abs(v)) < 0.01, "40 Hz low-pass removes a 5 kHz tone");
            var dist = new Effect("Distortion"); dist.P[0] = 1; dist.P[1] = 1; dist.P[2] = 1;
            var quiet = Enumerable.Range(0, Engine.SR).Select(k => (float)(0.05 * Math.Sin(2 * Math.PI * 100 * (k / 2) / Engine.SR))).ToArray();
            dist.Process(quiet, quiet.Length);
            check(quiet.Skip(Engine.SR / 2).Max(v => Math.Abs(v)) > 0.3, "distortion drive boosts a quiet tone");

            // EQ and compressor: tone(hz, amp) = 1 s of stereo sine; settled() = peak of its second half after processing
            Func<double, double, float[]> toneOf = (hz, amp) => Enumerable.Range(0, Engine.SR * 2).Select(k => (float)(amp * Math.Sin(2 * Math.PI * hz * (k / 2) / Engine.SR))).ToArray();
            Func<Effect, float[], double> settled = (fx, x) => { fx.Process(x, x.Length); return x.Skip(Engine.SR).Max(v => Math.Abs(v)); };
            var noise = new float[Engine.SR * 2]; var rn = new Random(2);
            for (int k = 0; k < noise.Length; k++) noise[k] = (float)(rn.NextDouble() * 2 - 1);
            var flat = (float[])noise.Clone();
            new Effect("EQ").Process(flat, flat.Length);
            check(flat.Zip(noise, (x, y) => Math.Abs(x - y)).Max() < 1e-5, "EQ with all bands at 0 dB leaves the sound unchanged");
            var eq = new Effect("EQ"); eq.P[0] = 0;
            double lo = settled(eq, toneOf(50, 1));
            check(lo < 0.4, "EQ low band at " + eq.Label(0) + " cuts a 50 Hz tone to " + lo.ToString("0.00"));
            eq = new Effect("EQ"); eq.P[2] = 1;
            double hi = settled(eq, toneOf(10000, 1));
            check(hi > 2.5, "EQ high band at " + eq.Label(2) + " boosts a 10 kHz tone to " + hi.ToString("0.00"));
            var comp = new Effect("Compressor"); comp.P[0] = 0.25f; comp.P[1] = 1; comp.P[2] = 0;
            double loud = settled(comp, toneOf(1000, 1));
            check(loud > 0.2 && loud < 0.35, "compressor (" + comp.Label(0) + ", " + comp.Label(1) + ") squashes a full-scale tone to " + loud.ToString("0.00"));
            comp = new Effect("Compressor"); comp.P[0] = 0.25f; comp.P[1] = 1;
            double soft = settled(comp, toneOf(1000, 0.1));
            check(Math.Abs(soft / 0.1 - 1.995) < 0.04, "compressor leaves a tone under the threshold alone and adds " + comp.Label(2) + " (0.10 -> " + soft.ToString("0.00") + ")");

            {   // metronome: a blip at the start of every beat, silence between, nothing when off; count-in delays the song by one bar
                var silentProj = Project.Demo(); foreach (var pt in silentProj.Patterns) foreach (var l in pt.Notes) l.Clear();
                Func<Engine, int, float[]> run = (eng2, frames) => { var all = new float[frames * 2]; var mblk = new float[2048]; for (int at = 0; at < frames; at += 1024) { eng2.Render(mblk, 1024); Array.Copy(mblk, 0, all, at * 2, Math.Min(2048, (frames - at) * 2)); } return all; };
                double sps16 = 60.0 / silentProj.Bpm / 4 * Engine.SR; int beat = (int)(4 * sps16);
                Func<float[], int, int, float> peak = (x, a, b) => { float m = 0; for (int k = 2 * a; k < 2 * b && k < x.Length; k++) m = Math.Max(m, Math.Abs(x[k])); return m; };
                var on = run(new Engine { Proj = silentProj, Playing = true, Metronome = true }, 4 * beat + 2048);
                var off = run(new Engine { Proj = silentProj, Playing = true }, 4 * beat);
                bool beatsClick = Enumerable.Range(0, 4).All(k => peak(on, k * beat, k * beat + 3000) > 0.2 && peak(on, k * beat + 6000, (k + 1) * beat - 50) < 0.001);
                check(beatsClick && peak(off, 0, 4 * beat) == 0, "metronome: a click at each of the bar's 4 beats and silence between them; none when it's off");
                var ci = new Engine { Proj = Project.Demo(), Playing = true, CountIn = true }; var blk1 = new float[2048];
                int heardAt = -1, stepDuringCount = -2; bool recDuring = false;
                for (int at = 0; at < 4 * beat + 8192 && heardAt < 0; at += 1024)
                {
                    if (at == 1024) ci.MidiIn.Enqueue(0x90 | 60 << 8 | 100 << 16);   // a key pressed during the count-in
                    ci.Render(blk1, 1024);
                    if (at + 1024 <= 4 * beat && at + 2048 > 4 * beat) stepDuringCount = ci.PlayStep;   // the last block wholly inside the count-in
                    if (at == 1024) recDuring = ci.MidiRec.Count > 0;
                    if (at >= 4 * beat - 1024 && blk1.Any(x => Math.Abs(x) > 0.5)) heardAt = at;
                }
                check(stepDuringCount == -1 && !recDuring && heardAt >= 4 * beat - 1024 && heardAt <= 4 * beat + 1024,
                    "count-in: one bar (" + 4 * beat + " samples) of clicks, then the kick at sample ~" + heardAt + "; keys pressed meanwhile aren't recorded");
            }

            {   // MIDI: the demo song out and midiBack in
                var mbytes = Midi.Export(Project.Demo(), true, 0);
                var midiBack = new Project();
                string msum = Midi.Import(midiBack, mbytes, "demo");
                Func<string, List<Note>> notesOf = kind => { int ci2 = midiBack.Channels.FindIndex(c => c.Kind == kind); return ci2 < 0 ? new List<Note>() : midiBack.Patterns.SelectMany(pt => pt.For(ci2)).ToList(); };
                var kicks = notesOf("kick"); var leads = notesOf("synth");
                check(string.Join(",", midiBack.Channels.Select(c => c.Kind).OrderBy(k => k)) == "hat,kick,snare,synth" && midiBack.Patterns.Count == 2 && midiBack.Patterns[0].Bars == 4 && midiBack.Patterns[1].Bars == 2
                    && kicks.Count == 16 && kicks.All(n => n.Step % 4 == 0 && n.Vel == 100) && leads.Count == 16 && leads.All(n => n.Len == 3 && new[] { 57, 60, 64, 67 }.Contains(n.Key))
                    && midiBack.Playlist[0, 0] == 0 && midiBack.Playlist[0, 4] == 1,
                    "MIDI: the demo song exported and imported again: 4 channels (drums by GM note, the lead by its program), 16 kicks on the beat, 16 lead notes keeping key and length, 2 patterns placed in order");
                bool refusedCut = false, refusedJunk = false;
                try { Midi.Import(new Project(), mbytes.Take(mbytes.Length / 2).ToArray(), "cut"); } catch (InvalidDataException) { refusedCut = true; }
                try { Midi.Import(new Project(), Encoding.ASCII.GetBytes("RIFF this is a wav, not midi"), "junk"); } catch (InvalidDataException) { refusedJunk = true; }
                check(refusedCut && refusedJunk, "MIDI: a cut-off file and a non-MIDI file are refused with a message, not a crash");
            }

            {   // channel rack extras: per-note pan and fine pitch, the arpeggiator, and the new fields saved
                Func<Project> lead = () => { var pj = new Project(); pj.Channels.Add(new Channel { Kind = "synth", Name = "Lead", Insert = 1, Knob = new[] { 0f, 0.2f, 0f, 0.5f } }); pj.Patterns.Add(new Pattern { Name = "P" }); return pj; };
                Func<Project, int, float[]> play = (pj, frames) => { var en = new Engine { Proj = pj, Playing = true }; var o = new float[frames * 2]; var bk = new float[2048];
                    for (int at = 0; at < frames; at += 1024) { en.Render(bk, 1024); Array.Copy(bk, 0, o, at * 2, Math.Min(2048, (frames - at) * 2)); } return o; };
                var panned = lead(); panned.Patterns[0].For(0).Add(new Note { Step = 0, Key = 60, Len = 4, Pan = 1 });
                var po = play(panned, 8000); double lsum = 0, rsum = 0; for (int k = 0; k < 8000; k++) { lsum += Math.Abs(po[2 * k]); rsum += Math.Abs(po[2 * k + 1]); }
                var up = lead(); up.Patterns[0].For(0).Add(new Note { Step = 0, Key = 69, Len = 4, Fine = 100 });
                var semi = lead(); semi.Patterns[0].For(0).Add(new Note { Step = 0, Key = 70, Len = 4 });
                var uo = play(up, 8000); var so = play(semi, 8000); double fineErr = uo.Zip(so, (u, w) => Math.Abs(u - w)).Max();
                check(lsum < 1e-3 * rsum && rsum > 1 && fineErr < 1e-3, "a note panned hard right is heard only on the right; +100 cents of fine pitch is exactly one semitone up (difference " + fineErr.ToString("0.0000") + ")");

                Func<float[], int, int, double> hz = (o, a0, n) => { int cross = 0, first = -1, last = -1; for (int k = a0 + 1; k < a0 + n; k++) if (o[2 * (k - 1)] < 0 && o[2 * k] >= 0) { cross++; if (first < 0) first = k; last = k; } return cross < 2 ? 0 : (cross - 1) * (double)Engine.SR / (last - first); };
                var arp = lead(); arp.Channels[0].ArpDir = 1; arp.Channels[0].ArpRange = 2; arp.Channels[0].ArpGate = 0.6f;
                foreach (int key in new[] { 60, 64, 67 }) arp.Patterns[0].For(0).Add(new Note { Step = 0, Key = key, Len = 16 });
                int st16 = (int)(60.0 / arp.Bpm / 4 * Engine.SR); var ao = play(arp, st16 * 7);
                var arpHeard = Enumerable.Range(0, 6).Select(k => hz(ao, k * st16 + 600, st16 / 2)).ToList();
                double[] want = { 60, 64, 67, 72, 76, 79 }; bool arpOk = true;
                for (int k = 0; k < 6; k++) arpOk &= Math.Abs(arpHeard[k] / (440 * Math.Pow(2, (want[k] - 69) / 12)) - 1) < 0.04;
                check(arpOk, "arpeggiator (up, 2 octaves, 1/16): a held C-E-G chord plays C E G C' E' G' one 16th apart (" + string.Join(", ", arpHeard.Select(fq => fq.ToString("0"))) + " Hz)");
                var live = lead(); live.Channels[0].ArpDir = 3; var liveEng = new Engine { Proj = live, MidiTarget = live.Channels[0] }; var liveBuf = new float[2048];
                liveEng.MidiIn.Enqueue(0x90 | 60 << 8 | 100 << 16); liveEng.MidiIn.Enqueue(0x90 | 67 << 8 | 100 << 16);
                double during = 0, after = 0;
                for (int k = 0; k < 20; k++) { liveEng.Render(liveBuf, 1024); during = Math.Max(during, liveBuf.Max(v => Math.Abs(v))); }
                liveEng.MidiIn.Enqueue(0x80 | 60 << 8); liveEng.MidiIn.Enqueue(0x80 | 67 << 8);
                for (int k = 0; k < 40; k++) { liveEng.Render(liveBuf, 1024); if (k >= 30) after = Math.Max(after, liveBuf.Max(v => Math.Abs(v))); }
                check(during > 0.05 && after < 1e-4, "arpeggiator on keys held live: it plays while they're held (even when stopped) and goes quiet once they're let go");

                var keep = Project.Demo(); var kc = keep.Channels[3]; kc.Color = Swatches[2].ToArgb(); kc.Group = "Synths"; kc.ArpDir = 4; kc.ArpRange = 3; kc.ArpTime = 2; kc.ArpGate = 0.3f;
                keep.Patterns[1].For(3)[0].Pan = -0.5f; keep.Patterns[1].For(3)[0].Fine = -37;
                var kb = Project.Parse(keep.Lines().ToArray(), null); var kc2 = kb.Channels[3]; var kn = kb.Patterns[1].For(3)[0];
                check(kc2.Color == kc.Color && kc2.Group == "Synths" && kc2.ArpDir == 4 && kc2.ArpRange == 3 && kc2.ArpTime == 2 && Math.Abs(kc2.ArpGate - 0.3f) < 1e-6 && kn.Pan == -0.5f && kn.Fine == -37
                      && kb.Channels[0].Group == null && kb.Channels[0].ArpDir == 0,
                      "saved and opened again: channel colour, group and arpeggiator, and each note's pan and fine pitch");
            }

            {   // piano roll timing: off-grid notes, slide notes, glide
                Func<Project> one = () => { var pj = new Project(); pj.Channels.Add(new Channel { Kind = "synth", Name = "L", Insert = 1, Knob = new[] { 0f, 0.1f, 0f, 0.5f } }); pj.Patterns.Add(new Pattern { Name = "P" }); return pj; };
                Func<Project, int, float[]> run2 = (pj, frames) => { var en = new Engine { Proj = pj, Playing = true }; var o = new float[frames * 2]; var bk = new float[2048];
                    for (int at = 0; at < frames; at += 1024) { en.Render(bk, 1024); Array.Copy(bk, 0, o, at * 2, Math.Min(2048, (frames - at) * 2)); } return o; };
                Func<float[], int> firstSound = o => { for (int k = 0; k < o.Length / 2; k++) if (Math.Abs(o[2 * k]) > 1e-4) return k; return -1; };
                double sps16 = 60.0 / 128 / 4 * Engine.SR; string got = ""; bool timeOk = true;
                foreach (var at in new[] { 0.5, 2 / 3.0, 1.25, 3 + 1 / 3.0 })
                {
                    var pj = one(); pj.Patterns[0].For(0).Add(new Note { Step = at, Key = 69, Len = 1 });
                    int on = firstSound(run2(pj, 30000)), want = (int)Math.Round(at * sps16);
                    timeOk &= Math.Abs(on - want) <= 2; got += at.ToString("0.###") + "→" + on + "/" + want + " ";
                }
                check(timeOk, "notes off the 16th grid (1/32, triplets, 1/64) start exactly then (16ths → sample heard / expected: " + got.Trim() + ")");

                Func<float[], int, int, double> freq = (o, a0, n) => { int cross = 0, first = -1, last = -1; for (int k = a0 + 1; k < a0 + n; k++) if (o[2 * (k - 1)] < 0 && o[2 * k] >= 0) { cross++; if (first < 0) first = k; last = k; } return cross < 2 ? 0 : (cross - 1) * (double)Engine.SR / (last - first); };
                int s16 = (int)sps16;
                var sl = one(); sl.Patterns[0].For(0).Add(new Note { Step = 0, Key = 60, Len = 16 }); sl.Patterns[0].For(0).Add(new Note { Step = 4, Key = 67, Len = 4, Slide = true });
                var so2 = run2(sl, s16 * 12);
                double before = freq(so2, s16 * 1, s16 * 2), after = freq(so2, s16 * 9, s16 * 2);
                check(Math.Abs(before / 261.6 - 1) < 0.03 && Math.Abs(after / 392 - 1) < 0.03,
                    "slide note: a held C (" + before.ToString("0") + " Hz) bends up to the slide's G (" + after.ToString("0") + " Hz) over the slide note, without starting a new note");
                var gl = one(); gl.Channels[0].Glide = 0.2f; gl.Patterns[0].For(0).Add(new Note { Step = 0, Key = 60, Len = 8 }); gl.Patterns[0].For(0).Add(new Note { Step = 4, Key = 72, Len = 8 });
                var go = run2(gl, s16 * 10);
                double early = freq(go, s16 * 4 + 60, 900), late = freq(go, s16 * 4 + Engine.SR / 8, s16);
                check(early < 360 && Math.Abs(late / 523.3 - 1) < 0.03, "glide (100 ms): the next note starts near the last one's pitch (" + early.ToString("0") + " Hz) and slides up to its own (" + late.ToString("0") + " Hz)");

                var fr = Project.Demo(); var fn = new Note { Step = 2 / 3.0, Key = 61, Len = 1 / 3.0, Slide = true }; fr.Patterns[0].For(3).Add(fn); fr.Channels[3].Glide = 0.35f;
                var fb = Project.Parse(fr.Lines().ToArray(), null); var fb2 = fb.Patterns[0].For(3).Last();
                check(fb2.Step == fn.Step && fb2.Len == fn.Len && fb2.Slide && fb.Channels[3].Glide == 0.35f && !fb.Patterns[0].For(0)[0].Slide,
                    "saved and opened again: a triplet note's exact position and length, the slide flag, and the channel's glide");
            }

            {   // sampler options and the analog synth
                int sr = Engine.SR;
                Func<float[], Channel> sampleCh = data => new Channel { Kind = "sample", Name = "S", Insert = 1, Sample = data, SampleRate = sr };
                Func<Channel, int, double, float[]> playSmp = (smpCh, key, len16) => { var pj = new Project(); pj.Channels.Add(smpCh); var pt = new Pattern { Name = "P" }; pt.SetBars(4); pj.Patterns.Add(pt);
                    pt.For(0).Add(new Note { Step = 0, Key = key, Len = len16 }); return Engine.Bounce(pj, false, 0); };
                var ramp = Enumerable.Range(0, sr / 4).Select(k => 0.5f * k / (sr / 4)).ToArray();
                var fwd = playSmp(sampleCh(ramp), 60, 1); var rch = sampleCh(ramp); rch.Reverse = true; var revOut = playSmp(rch, 60, 1);
                bool reversed = fwd[2 * 10000] > 20 * fwd[2 * 100] && revOut[2 * 100] > 20 * fwd[2 * 100] && revOut[2 * 10000] < 0.2f * revOut[2 * 100];   // the faders (0.8 × 0.8) scale everything: compare, don't measure
                var lch = sampleCh(Enumerable.Range(0, sr / 4).Select(k => (float)(0.4 * Math.Sin(2 * Math.PI * 220 * k / sr))).ToArray()); lch.Loop = true;
                double s16 = 60.0 / 128 / 4 * sr; var looped = playSmp(lch, 60, 8);   // held 8 16ths (~0.94 s): four times the sample's length
                Func<float[], int> soundEnd = a => { int k = a.Length / 2 - 1; while (k > 0 && Math.Abs(a[2 * k]) < 1e-4) k--; return k; };   // the test pattern is 4 bars: find where the sound itself stops
                bool loopOk = looped.Skip((int)(0.8 * sr) * 2).Take(2000).Max(x => Math.Abs(x)) > 0.15 && soundEnd(looped) < 8 * s16 + sr / 30 + 64;
                var sine440 = Enumerable.Range(0, sr / 2).Select(k => (float)(0.4 * Math.Sin(2 * Math.PI * 440 * k / sr))).ToArray();
                var sch = sampleCh(sine440); sch.Stretch = 2; var st = playSmp(sch, 60, 1);
                int cross = 0, first = -1, last = -1; for (int k = sr / 10; k < sr * 9 / 10; k++) if (st[2 * (k - 1)] < 0 && st[2 * k] >= 0) { cross++; if (first < 0) first = k; last = k; }
                double stHz = (cross - 1) * (double)sr / (last - first), stLen = soundEnd(st) / (double)sr;
                check(reversed && loopOk && Math.Abs(stLen - 1.0) < 0.05 && Math.Abs(stHz / 440 - 1) < 0.02,
                    "sampler: reverse plays a rising ramp falling; a looped 1/4 s sample keeps going while held and stops 30 ms after; stretched 2× a 1/2 s 440 Hz tone lasts " + stLen.ToString("0.00") + " s at " + stHz.ToString("0") + " Hz");
                var hits = new float[sr]; foreach (double t0 in new[] { 0, 0.25, 0.5, 0.75 }) for (int k = 0; k < 2000; k++) hits[(int)(t0 * sr) + k] = (float)(0.8 * Math.Exp(-k / 300.0) * Math.Sin(k * 0.3));
                var found = Channel.Transients(hits, sr); var slc = sampleCh(hits); slc.Slices = found; var second = playSmp(slc, 61, 1); var none = playSmp(slc, 59, 1);
                check(found.Length == 4 && found.Zip(new[] { 0, sr / 4, sr / 2, sr * 3 / 4 }, (fs, ws) => Math.Abs(fs - ws) <= 64).All(b => b) && second.Take(400).Max(x => Math.Abs(x)) > 0.3 && soundEnd(second) <= sr / 4 && none.All(x => x == 0),
                    "slicing: the 4 hits in a sample are found (at " + string.Join(", ", found) + "); C#5 plays the 2nd slice alone; notes below C5 play nothing");
                var an = new Project(); an.Channels.Add(new Channel { Kind = "analog", Name = "A", Insert = 1, Syn = (float[])Channel.SynPresets[3].Clone() }); var apt = new Pattern { Name = "P" }; an.Patterns.Add(apt);
                apt.For(0).Add(new Note { Step = 0, Key = 57, Len = 8 }); var ao = Engine.Bounce(an, false, 0);
                Func<int, double> brightAt = frame => { double d = 0, en = 0; for (int k = frame; k < frame + 2000; k++) { double x = ao[2 * k], y = ao[2 * k - 2]; d += (x - y) * (x - y); en += x * x; } return d / (en + 1e-12); };
                var wide = new Project(); wide.Channels.Add(new Channel { Kind = "analog", Name = "W", Insert = 1, Syn = (float[])Channel.SynPresets[5].Clone() }); var wpt = new Pattern { Name = "P" }; wide.Patterns.Add(wpt);
                wpt.For(0).Add(new Note { Step = 0, Key = 60, Len = 8 }); var wo = Engine.Bounce(wide, false, 0); double lrDiff = 0; for (int k = 8000; k < 20000; k++) lrDiff = Math.Max(lrDiff, Math.Abs(wo[2 * k] - wo[2 * k + 1]));
                var keep3 = Project.Demo(); var kc3 = new Channel { Kind = "sample", Name = "Loop", SamplePath = null, Loop = true, Reverse = true, LoopStart = 0.2f, LoopEnd = 0.7f, Stretch = 1.5f, Slices = new[] { 0, 100, 900 } };
                keep3.Channels.Add(kc3); keep3.Channels[3].Kind = "analog"; keep3.Channels[3].Syn = (float[])Channel.SynPresets[4].Clone(); keep3.Channels[3].Syn[11] = 0.123f;
                var kb3 = Project.Parse(keep3.Lines().ToArray(), null); var k4 = kb3.Channels[4];
                check(brightAt(400) > 2 * brightAt(Engine.SR / 2) && lrDiff > 0.01 && k4.Loop && k4.Reverse && k4.LoopStart == 0.2f && k4.LoopEnd == 0.7f && k4.Stretch == 1.5f && k4.Slices.SequenceEqual(new[] { 0, 100, 900 }) && kb3.Channels[3].Syn[11] == 0.123f,
                    "analog synth: the Pluck preset starts bright and darkens as its filter envelope closes; Supersaw unison is wider than mono; synth settings and sampler options save and load");
            }
            {   // automation clips: they act only where they sit; points with bentMid curves; the LFO tool; MIDI controller links
                Func<double, float[]> silenceAt = start =>
                {
                    var ap2 = Project.Demo(); foreach (var pt in ap2.Patterns) for (int c = 1; c < 4; c++) pt.For(c).Clear();   // kicks only, on insert 1
                    var lane = new Lane { Ins = 1, Param = 0 }; ap2.Automation.Add(lane);   // insert 1's fader, all the way down
                    ap2.Clips.Add(new Clip { Lane = 0, Pattern = -1, Track = 5, Start = start, Len = 16 });
                    return Engine.Bounce(ap2.ExportCopy(), true, 0);
                };
                double b16 = 60.0 / 128 / 4 * Engine.SR;
                Func<float[], int, double> barPeak = (a, bar) => a.Skip((int)((bar * 16 + 1) * b16) * 2).Take((int)(4 * b16) * 2).DefaultIfEmpty(0).Max(x => Math.Abs(x));   // from its 2nd 16th: mixer settings change once per audio block (~23 ms)
                var at3 = silenceAt(32); var at4 = silenceAt(48);
                check(barPeak(at3, 0) > 0.1 && barPeak(at3, 1) > 0.1 && barPeak(at3, 2) < 1e-4 && barPeak(at4, 2) > 0.1 && barPeak(at4, 3) < 1e-4,
                    "automation clip: a fader-down clip at bar 3 leaves bars 1-2 playing and silences bar 3; moved to bar 4, bar 3 plays again");
                var bend = new Lane { Points = new List<LanePoint> { new LanePoint { At = 0, Value = 0 }, new LanePoint { At = 16, Value = 1 } } }; bend.Render(); float straight = bend.Values[8];
                bend.Points[0].Tension = 0.5f; bend.Render(); float bentMid = bend.Values[8];
                var lfo = new Lane { Points = AutoWindow.Lfo(0, 16, 64) }; lfo.Render();
                check(Math.Abs(straight - 0.5f) < 1e-4 && bentMid < 0.3f && Math.Abs(lfo.Values[4] - 1) < 0.01 && Math.Abs(lfo.Values[12]) < 0.01 && Math.Abs(lfo.Values[20] - 1) < 0.01,
                    "automation points: a straight ramp is halfway at its middle, a bent one " + bentMid.ToString("0.00") + "; a 1-bar sine LFO peaks on beat 2 and dips on beat 4 of every bar");
                var lk = Project.Demo(); lk.Links.Add(new Link { Cc = 74, Target = new Lane { Ins = 1, Param = 0 } });
                var le3 = new Engine { Proj = lk }; var lb3 = new float[2048];
                le3.MidiIn.Enqueue(0xB0 | 74 << 8 | 32 << 16); le3.Render(lb3, 1024); float linked = lk.Mixer[1].Vol;
                le3.LearnTarget = new Lane { Ins = 2, Param = 1 }; le3.MidiIn.Enqueue(0xB0 | 21 << 8 | 127 << 16); le3.Render(lb3, 1024); int learned; bool got = le3.Learned.TryDequeue(out learned);
                check(Math.Abs(linked - 32 / 127f) < 1e-6 && got && learned == 21 && le3.LearnTarget == null && lk.Mixer[2].Pan == 0,
                    "MIDI link: controller 74 at 32 sets insert 1's fader to " + linked.ToString("0.00") + "; while learning, the next controller (21) is captured instead of moving anything");
                var sv = Project.Demo(); var svLane = new Lane { Ch = 1, Param = Channel.KTone }; svLane.Points = new List<LanePoint> { new LanePoint { At = 0, Value = 0.2f, Tension = -0.4f }, new LanePoint { At = 40, Value = 0.9f } }; svLane.Render();
                sv.Automation.Add(new Lane { Tempo = true }); sv.Automation.Add(svLane); sv.Clips.Add(new Clip { Lane = 1, Pattern = -1, Track = 7, Start = 16, Len = 48, Offset = 4 });
                sv.Links.Add(new Link { Cc = 10, Target = new Lane { Ch = 2, Param = Channel.LanePan } });
                var svb = Project.Parse(sv.Lines().ToArray(), null); var c2 = svb.Clips.Last();
                bool saved = c2.Lane == 1 && c2.Start == 16 && c2.Len == 48 && c2.Offset == 4 && svb.Automation[1].Points.Count == 2 && svb.Automation[1].Points[0].Tension == -0.4f
                    && Math.Abs(svb.Automation[1].Values[20] - svLane.Values[20]) < 1e-6 && svb.Links.Count == 1 && svb.Links[0].Cc == 10 && svb.Links[0].Target.Ch == 2;
                sv.RemoveChannel(1);
                check(saved && sv.Automation.Count == 1 && !sv.Clips.Any(c => c.Lane >= 0 && c.Lane != 0) && sv.Links[0].Target.Ch == 1,
                    "saved and opened again: automation clips, points with their bends, MIDI links; deleting a channel takes its lanes and their clips with it");
            }

            {   // mixer: 125 inserts, free routing without loops, the new effects
                var routeProj = Project.Demo();
                routeProj.Mixer[3].SendTo = 5; routeProj.Mixer[5].SendTo = 7;
                bool loopRefused = !routeProj.CanRoute(7, 3) && !routeProj.CanRoute(7, 5) && routeProj.CanRoute(7, 2) && routeProj.CanRoute(3, 7) && routeProj.CanRoute(9, 1);
                routeProj.Mixer[3].Sidechain = 0; bool scRefused = !routeProj.CanRoute(7, 3);   // 3 can't listen to 7: 7 already hears 3
                var order = routeProj.ProcessOrder(); bool ordered = Array.IndexOf(order, 3) < Array.IndexOf(order, 5) && Array.IndexOf(order, 5) < Array.IndexOf(order, 7);
                check(loopRefused && scRefused && ordered && order.Length == Project.Inserts - 1, "routing: 3 → 5 → 7 is processed in that order; 7 → 3, 7 → 5 and a sidechain 7 → 3 are refused (they'd loop); 7 → 2 and 3 → 7 are fine");
                var far = Project.Demo(); foreach (var pt in far.Patterns) for (int c = 1; c < 4; c++) pt.For(c).Clear();
                far.Channels[0].Insert = 1; far.Mixer[1].ToMaster = false; far.Mixer[1].Sends.Add(new Route { To = 100, Level = 0.5f }); far.Mixer[1].Sends.Add(new Route { To = 120, Level = 0.5f });
                float[] everF; var farOut = Engine.Bounce(far.ExportCopy(), false, 0, out everF);
                var last = Project.Demo(); last.Channels[0].Insert = Project.Inserts - 1; float[] everL; Engine.Bounce(last.ExportCopy(), false, 0, out everL);
                check(everF[100] > 0.05 && everF[120] > 0.05 && Math.Abs(everF[100] - everF[120]) < 1e-4 && farOut.Max(x => Math.Abs(x)) > 0.05 && everL[Project.Inserts - 1] > 0.05,
                    "a kick on insert 1 with \"to master\" off, sending 50% to inserts 100 and 120, reaches the master through them; a channel on insert 125 plays");
                var rt = Project.Demo(); rt.Mixer[2].ToMaster = false; rt.Mixer[2].SendTo = 9; rt.Mixer[2].SendLevel = 0.3f; rt.Mixer[2].Sends.Add(new Route { To = 110, Level = 0.7f });
                var peq = new Effect("Parametric EQ"); peq.P[7] = 0.83f; rt.Mixer[110].Slots[2] = peq;
                var rtb = Project.Parse(rt.Lines().ToArray(), null);
                check(!rtb.Mixer[2].ToMaster && rtb.Mixer[2].Sends.Count == 2 && rtb.Mixer[2].Sends[0].To == 9 && rtb.Mixer[2].Sends[0].Level == 0.3f && rtb.Mixer[2].Sends[1].To == 110 && rtb.Mixer[2].Sends[1].Level == 0.7f
                      && rtb.Mixer[1].ToMaster && rtb.Mixer[110].Slots[2].Type == "Parametric EQ" && rtb.Mixer[110].Slots[2].P.SequenceEqual(peq.P),
                    "saved and opened again: \"to master\" off, two sends with their levels, and a Parametric EQ's 15 settings on insert 110");

                Func<double, float[]> sineOf = hz => { var x = new float[Engine.SR]; for (int k = 0; k < x.Length / 2; k++) x[2 * k] = x[2 * k + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * hz * k / Engine.SR)); return x; };
                Func<Effect, float[], double> gainDb = (fx, x) => { var y = (float[])x.Clone(); for (int at = 0; at < y.Length; at += 2048) { var bk = new float[Math.Min(2048, y.Length - at)]; Array.Copy(y, at, bk, 0, bk.Length); fx.Process(bk, bk.Length); Array.Copy(bk, 0, y, at, bk.Length); }
                    return 20 * Math.Log10(y.Skip(y.Length / 2).Max(v => Math.Abs(v)) / x.Skip(x.Length / 2).Max(v => Math.Abs(v))); };
                var peq1k = new Effect("Parametric EQ"); peq1k.P[7] = (float)(0.5 + 12 / 36.0);   // band 3 (1 kHz) +12 dB
                double at1k = gainDb(peq1k, sineOf(1000)), at100 = gainDb(new Effect("Parametric EQ") { }, sineOf(100)); var eq2 = new Effect("Parametric EQ"); eq2.P[7] = peq1k.P[7]; double at100b = gainDb(eq2, sineOf(100));
                check(Math.Abs(at1k - 12) < 0.6 && Math.Abs(at100b) < 0.6 && Math.Abs(peq1k.ResponseDb(1000) - 12) < 0.3 && Math.Abs(at100) < 0.01,
                    "Parametric EQ: +12 dB on the 1 kHz band boosts a 1 kHz tone by " + at1k.ToString("0.0") + " dB and leaves 100 Hz at " + at100b.ToString("+0.0;-0.0") + " dB; the drawn curve says " + peq1k.ResponseDb(1000).ToString("0.0") + " dB there");
                var loudNoise = new float[Engine.SR]; var nr = new Random(3); for (int k = 0; k < loudNoise.Length; k++) loudNoise[k] = (float)(nr.NextDouble() * 2 - 1) * 0.9f;
                var mb0 = new Effect("Multiband"); mb0.P[0] = 0; mb0.P[2] = 0; var mb1 = new Effect("Multiband"); mb1.P[0] = 1; mb1.P[2] = 0;
                Func<Effect, float[], float[]> run3 = (fx, x) => { var y = (float[])x.Clone(); for (int at = 0; at < y.Length; at += 2048) { var bk = new float[Math.Min(2048, y.Length - at)]; Array.Copy(y, at, bk, 0, bk.Length); fx.Process(bk, bk.Length); Array.Copy(bk, 0, y, at, bk.Length); } return y; };
                var flatOut = run3(mb0, loudNoise); var squashed = run3(mb1, loudNoise);
                double rmsN = Math.Sqrt(loudNoise.Average(v => (double)v * v)), rmsS = Math.Sqrt(squashed.Skip(loudNoise.Length / 2).Average(v => (double)v * v));
                check(flatOut.Zip(loudNoise, (u, w) => Math.Abs(u - w)).Max() < 1e-4 && 20 * Math.Log10(rmsN / rmsS) > 6,
                    "Multiband: at zero depth it passes sound unchanged; at full depth it turns loud noise down by " + (20 * Math.Log10(rmsN / rmsS)).ToString("0.0") + " dB");
                var gate = new Effect("Beat FX"); gate.P[1] = 0.6f; gate.Sps = 5000; gate.Playing = true;   // gate, 1/16 slices of 5000 samples
                var gx = new float[40000]; for (int k = 0; k < gx.Length; k++) gx[k] = 0.5f; var gy = (float[])gx.Clone();
                for (int at = 0; at < gy.Length; at += 2000) { var bk = new float[2000]; Array.Copy(gy, at, bk, 0, 2000); gate.Pos = at / 2 / 5000.0; gate.Process(bk, 2000); Array.Copy(bk, 0, gy, at, 2000); }
                bool gated = Enumerable.Range(0, 4).All(sl => gy[2 * (sl * 5000 + 1200)] > 0.45f && gy[2 * (sl * 5000 + 3700)] == 0);
                var stut = new Effect("Beat FX"); stut.P[0] = 0.3f; stut.P[1] = 0.6f; stut.Sps = 1000; stut.Playing = true;   // stutter: 1/16 = 1000 samples, beats of 4000
                var sx2 = new float[16000]; for (int k = 0; k < sx2.Length / 2; k++) sx2[2 * k] = sx2[2 * k + 1] = k; var sy2 = (float[])sx2.Clone();
                for (int at = 0; at < sy2.Length; at += 2000) { var bk = new float[2000]; Array.Copy(sy2, at, bk, 0, 2000); stut.Pos = at / 2 / 1000.0; stut.Process(bk, 2000); Array.Copy(bk, 0, sy2, at, 2000); }
                bool stuttered = sy2[2 * 1500] == 500 && sy2[2 * 2500] == 500 && sy2[2 * 4200] == 4200 && sy2[2 * 5300] == 4300;   // each beat's first slice repeats
                check(gated && stuttered, "Beat FX: the gate lets the first half of each 1/16 through and silences the second; the stutter repeats each beat's first 1/16");
                var voc = new Effect("Vocoder"); var carrier = new float[Engine.SR]; for (int k = 0; k < carrier.Length / 2; k++) carrier[2 * k] = carrier[2 * k + 1] = (float)(((k * 110.0 / Engine.SR) % 1) * 2 - 1) * 0.5f;
                var quietKey = new float[carrier.Length]; var talkKey = (float[])loudNoise.Clone();
                Func<float[], float[], double> vrun = (key, car) => { var v2 = new Effect("Vocoder"); var y = (float[])car.Clone(); for (int at = 0; at < y.Length; at += 2048) { var bk = new float[Math.Min(2048, y.Length - at)]; var kk = new float[bk.Length]; Array.Copy(y, at, bk, 0, bk.Length); Array.Copy(key, at, kk, 0, kk.Length); v2.Process(bk, bk.Length, kk); Array.Copy(bk, 0, y, at, bk.Length); } return Math.Sqrt(y.Skip(y.Length / 2).Average(v => (double)v * v)); };
                double silentOut = vrun(quietKey, carrier), talkOut = vrun(talkKey, carrier); var untouched = run3(voc, carrier);
                check(silentOut < 1e-3 && talkOut > 0.05 && untouched.SequenceEqual(carrier), "Vocoder: a saw with a silent voice is silent (" + silentOut.ToString("0.0000") + "), with a noisy one it speaks (" + talkOut.ToString("0.000") + "); with no sidechain it leaves the sound alone");
            }

            {   // limiter: nothing past the ceiling, quiet sound untouched (just 5 ms late); chorus, flanger, phaser change the sound, not its level
                int sr = Engine.SR, half = sr / 2, look = (int)(0.005 * sr);
                var x = new float[sr * 2];
                for (int k = 0; k < sr; k++) { float v = (float)Math.Sin(2 * Math.PI * 220 * k / sr) * (k < half ? 0.1f : 2f); x[2 * k] = v; x[2 * k + 1] = v; }
                var lim = new Effect("Limiter"); lim.P[0] = 0.5f; var y = (float[])x.Clone();   // -6 dB ceiling
                for (int at = 0; at < y.Length; at += 2048) { var blkL = new float[Math.Min(2048, y.Length - at)]; Array.Copy(y, at, blkL, 0, blkL.Length); lim.Process(blkL, blkL.Length); Array.Copy(blkL, 0, y, at, blkL.Length); }
                double ceilAmp = Math.Pow(10, -6 / 20.0), over = y.Max(v => Math.Abs(v)), loudPeak = y.Skip(2 * (half + look + 2000)).Max(v => Math.Abs(v)), quietErr = 0;
                for (int k = look; k < half - 10; k++) quietErr = Math.Max(quietErr, Math.Abs(y[2 * k] - x[2 * (k - look)]));
                check(over <= ceilAmp + 1e-6 && loudPeak > 0.9 * ceilAmp && quietErr < 1e-4, "limiter (" + lim.Label(0) + "): a tone 6 dB over full scale peaks at " + over.ToString("0.000") + " (ceiling " + ceilAmp.ToString("0.000") + "); a quiet tone passes unchanged, 5 ms late");
                var src = Engine.Bounce(Project.Demo().ExportCopy(), false, 0); double rmsIn = Math.Sqrt(src.Average(v => (double)v * v)); string fxInfo = ""; bool fxOk = true;
                foreach (var name in new[] { "Chorus", "Flanger", "Phaser" })
                {
                    var fx = new Effect(name); var o = (float[])src.Clone();
                    for (int at = 0; at < o.Length; at += 2048) { var blkF = new float[Math.Min(2048, o.Length - at)]; Array.Copy(o, at, blkF, 0, blkF.Length); fx.Process(blkF, blkF.Length); Array.Copy(blkF, 0, o, at, blkF.Length); }
                    double rmsOut = Math.Sqrt(o.Average(v => (double)v * v)), diff = o.Zip(src, (u, w) => Math.Abs(u - w)).Max();
                    bool good = o.All(v => !float.IsNaN(v) && Math.Abs(v) < 4) && rmsOut > 0.5 * rmsIn && rmsOut < 1.5 * rmsIn && diff > 0.01;
                    fxOk &= good; fxInfo += name + " " + (20 * Math.Log10(rmsOut / rmsIn)).ToString("+0.0;-0.0") + " dB" + (good ? "" : " (bad)") + ", ";
                }
                check(fxOk, "chorus, flanger, phaser: audibly change the demo pattern while keeping its level (" + fxInfo.TrimEnd(' ', ',') + ")");
            }

            {   // export formats: WAV read back by MiniDAW, FLAC and MP3 decoded by Windows' own decoders (a reader that isn't ours)
                var pat = Engine.Bounce(Project.Demo().ExportCopy(), false, 0);   // the demo pattern, ~1.9 s
                var mix = new float[pat.Length + 2 * 9000]; Array.Copy(pat, mix, pat.Length);
                var rnd = new Random(7); for (int k = pat.Length; k < pat.Length + 9000; k++) mix[k] = (float)(rnd.NextDouble() * 2 - 1);   // full-scale noise, then silence
                string tmp = Path.Combine(Path.GetTempPath(), "minidaw-selftest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
                try
                {
                    var probe = new Channel { Kind = "sample" }; bool wavOk = true;
                    foreach (int bits in new[] { 24, 32 })
                    {
                        string wp = Path.Combine(tmp, "t" + bits + ".wav"); File.WriteAllBytes(wp, Wav.Make(mix, Engine.SR, 2, bits)); Wav.Load(wp, probe);
                        double err = 0; for (int k = 0; k < probe.Sample.Length; k++) err = Math.Max(err, Math.Abs(probe.Sample[k] - Math.Max(-1, Math.Min(1, mix[2 * k]))));
                        wavOk &= probe.Sample.Length == mix.Length / 2 && err < (bits == 32 ? 1e-7 : 2.5e-7);
                    }
                    check(wavOk, "WAV export: 24-bit and 32-bit float files read back within one step of 24-bit precision");
                    bool mf = true; int rate, chans;
                    try { File.WriteAllBytes(Path.Combine(tmp, "probe.wav"), Wav.Make(pat, Engine.SR, 2, 16)); MediaFoundation.Decode(Path.Combine(tmp, "probe.wav"), out rate, out chans); }
                    catch (InvalidOperationException) { mf = false; }
                    if (!mf) log.Add("skip FLAC/MP3 checks: this Windows has no Media Foundation");
                    else
                    {
                        string flacInfo = ""; bool flacOk = true;
                        foreach (int bits in new[] { 16, 24 })
                        {
                            string fp = Path.Combine(tmp, "t" + bits + ".flac"); var enc = Flac.Encode(mix, Engine.SR, bits); File.WriteAllBytes(fp, enc);
                            var dec = MediaFoundation.Decode(fp, out rate, out chans); double scale = 1 << (bits - 1);
                            int bad = dec.Length != mix.Length ? -1 : Enumerable.Range(0, mix.Length).Count(k => (int)Math.Round(dec[k] * scale) != Wav.Sample(mix[k], bits));
                            flacOk &= bad == 0 && rate == Engine.SR && chans == 2;
                            flacInfo += bits + "-bit " + (100 * enc.Length / (mix.Length * bits / 8)) + "% of WAV size" + (bad == 0 ? "" : ", " + bad + " samples differ") + "; ";
                        }
                        check(flacOk, "FLAC export: Windows decodes it bit-for-bit identical to the input, 16 and 24-bit, through music, noise and silence (" + flacInfo.TrimEnd(' ', ';') + ")");
                        string mp3Path = Path.Combine(tmp, "t.mp3"); MediaFoundation.Mp3(mp3Path, pat, Engine.SR, 192);
                        var md = MediaFoundation.Decode(mp3Path, out rate, out chans);
                        double bestCorr = 0; int bestLag = 0;
                        for (int lag = 0; lag < 3000 && chans == 2; lag += 1)
                        {
                            double sxy = 0, sxx = 0, syy = 0;
                            for (int k = 0; k < 40000 && 2 * (k + lag) < md.Length; k += 4) { double x = pat[2 * k], y = md[2 * (k + lag)]; sxy += x * y; sxx += x * x; syy += y * y; }
                            double c = sxy / Math.Sqrt(sxx * syy + 1e-12); if (c > bestCorr) { bestCorr = c; bestLag = lag; }
                        }
                        check(bestCorr > 0.95 && rate == Engine.SR, "MP3 export (192 kbps, Windows' encoder): decodes to the same sound (correlation " + bestCorr.ToString("0.000") + " after the encoder's " + bestLag + "-sample delay)");
                    }
                    float[] ever; var demoSong = Project.Demo(); var master = Engine.Bounce(demoSong.ExportCopy(), true, 0, out ever);
                    var active = Enumerable.Range(1, Project.Inserts - 1).Where(k => ever[k] >= 1e-4f).ToList();
                    var sum = new double[master.Length];
                    foreach (var k in active) { var st = Engine.BounceStem(demoSong.ExportCopy(), true, 0, k, master.Length / 2); for (int j = 0; j < st.Length; j++) sum[j] += st[j]; }
                    double a = 0, bb = 0, crossSum = 0; for (int j = 0; j < master.Length; j++) { a += sum[j] * sum[j]; bb += master[j] * master[j]; crossSum += sum[j] * master[j]; }
                    check(string.Join(",", active) == "1,2,3,4" && crossSum / Math.Sqrt(a * bb) > 0.99, "stems: one file per insert that carries sound (" + string.Join(", ", active) + "), and together they add up to the master mix");
                }
                finally { Directory.Delete(tmp, true); }
            }

            check(Updates.Newer("v1.2", "1.1") && Updates.Newer("1.10", "1.9") && Updates.Newer("v2.0.1", "2.0") && !Updates.Newer("v1.1.0", "1.1") && !Updates.Newer("v1.0", "1.1")
                && !Updates.Newer("nightly", "1.1") && !Updates.Newer(null, "1.1"),
                "update check: v1.2 > 1.1, 1.10 > 1.9, v2.0.1 > 2.0; v1.1.0 is the same as 1.1; an older or non-numeric tag is no update");
            string asmVer = typeof(AboutForm).Assembly.GetName().Version.ToString();
            check(!Updates.Newer(asmVer, AboutForm.Version) && !Updates.Newer(AboutForm.Version, asmVer), "the exe's own version (read by the installer) is " + asmVer + ", matching " + AboutForm.Version);
        }
        catch (Exception ex) { log.Add("FAIL exception: " + ex); ok = false; }
        File.WriteAllLines(outPath, log);
        return ok ? 0 : 1;
    }
}
