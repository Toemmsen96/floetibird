using Godot;
using System.Collections.Generic;

public partial class Gametest : GodotP5
{
    private sealed class NoteState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public int MidiNote { get; set; }
        public bool Hit { get; set; }
    }

    private struct FreqPeak
    {
        public float Freq;
        public float Magnitude;
    }

    // Melodica MIDI range: E3 (52) – D#5 (75), approximately 160–637 Hz
    private const float MelodicaMinHz = 160f;
    private const float MelodicaMaxHz = 637f;
    private const int MidiNoteMin = 52;   // E3
    private const int MidiNoteMax = 75;   // D#5

    private static readonly string[] ChromaticNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static bool IsNaturalNote(int midi) =>
        (midi % 12) is 0 or 2 or 4 or 5 or 7 or 9 or 11;

    private static string MidiToNoteName(int midi) =>
        ChromaticNames[midi % 12] + (midi / 12 - 1);

    private static float MidiToFreq(int midi) =>
        440f * Mathf.Pow(2f, (midi - 69) / 12f);

    // Silence gate: RMS below this value (out of 32767) is treated as no signal
    private const float SilenceGateRms = 500f;

    // Game lane bounds (Y coords, top = highest pitch)
    private const float LaneTopY = 45f;
    private const float LaneBottomY = 510f;

    private readonly List<NoteState> notes = new();
    private readonly List<int> rawSamples = new();
    private readonly List<int> recordedSamples = new();

    private AudioStreamPlayer micPlayer;
    private AudioEffectCapture captureEffect;
    private int captureBusIndex = -1;

    private const int SampleRateHz = 44100;

    private float speed = 2.0f;
    private int score = 0;
    private float noteLaneX = 100.0f;
    private float lastSpawnTimeSeconds = -1.0f;
    private float lastAnalysisTimeSeconds = -1.0f;
    private float lastEstimatedFrequency = 0.0f;
    private const float SpawnCooldownSeconds = 0.08f;
    private const float AnalysisIntervalSeconds = 0.05f;
    private const int AnalysisWindowSize = 4096;
    private string micStatusText = "Mic: initializing";
    private string analysisText = "Waiting for samples";
    private bool isRecording;
    private bool isPlayingBack;
    private int playbackIndex;
    private float playbackSampleAccumulator;
    private AudioStreamPlayer playbackAudioPlayer;
    private const int PlaybackMixRateHz = 44100;
    private float playerY = 200;
    private int lastDetectedMidiNote = 64;  // E4

    private const int SpectrumSize = 64;
    private const float SpectrumDisplayMinHz = 100f;
    private const float SpectrumDisplayMaxHz = 1400f;
    private readonly float[] spectrum = new float[SpectrumSize];
    private FreqPeak[] detectedPeaks = [];
    private readonly float[] windowedBuffer = new float[AnalysisWindowSize];
    private const int MaxDetectedPeaks = 4;
    private float lastRms = 0f;
    private const float RmsDisplayMax = 8000f;

    private float lastRandomSpawnTime = 0f;
    private const float RandomSpawnInterval = 1.0f;

    public override void Setup()
    {
        StopMicCapture();
        EnsureAudioPlayer();
        notes.Clear();
        rawSamples.Clear();
        recordedSamples.Clear();
        score = 0;
        lastSpawnTimeSeconds = -1.0f;
        lastAnalysisTimeSeconds = -1.0f;
        lastEstimatedFrequency = 0.0f;
        analysisText = "Waiting for samples";
        isRecording = false;
        isPlayingBack = false;
        playbackIndex = 0;
        playbackSampleAccumulator = 0.0f;
        SetTitle("Gametest");
        SetViewportMode(ViewportMode.Always);
        CreateCanvas(1200, 700);
        Background(new Color(30f / 255f, 30f / 255f, 30f / 255f));

        StartMicCapture();
    }

    public override void DrawSketch()
    {
        PollMicrophone();
        ComputeSpectrum();
        PumpPlaybackSamples();
        AnalyzeSignalAndSpawnNote();
        float now = Time.GetTicksMsec() / 1000.0f;

        if (now - lastRandomSpawnTime > RandomSpawnInterval)
        {
            SpawnRandomNote();
            lastRandomSpawnTime = now;
        }

        Background(new Color(30f / 255f, 30f / 255f, 30f / 255f));

        DrawNoteLanes();

        Stroke(new Color(0f, 1f, 0f));
        StrokeWeight(2f);
        Line(noteLaneX, LaneTopY, noteLaneX, LaneBottomY);
        StrokeWeight(1f);

        NoStroke();
        playerY = GetYForNote(lastDetectedMidiNote);
        Fill(NoteColor(lastDetectedMidiNote));
        Circle(noteLaneX, playerY, 7);

        foreach (NoteState note in notes)
        {
            note.X -= speed;

            Fill(NoteColor(note.MidiNote));
            Circle(note.X, note.Y, 6);

            if (!note.Hit &&
                Mathf.Abs(note.X - noteLaneX) < 12 &&
                note.MidiNote == lastDetectedMidiNote)
            {
                note.Hit = true;
                score += 10;
            }
        }

        notes.RemoveAll(note => note.X <= -50);

        TextAlign(HorizontalAlignment.Center);
        TextSize(20);
        Fill(Colors.White);
        Text($"Score: {score}", Width * 0.5f, 30);

        TextSize(14);
        Fill(Colors.White);
        Text("Listening via computer microphone", Width * 0.5f, Height - 40);

        TextSize(12);
        Text($"{micStatusText} | {analysisText} | rec={isRecording} play={isPlayingBack} samples={recordedSamples.Count}", Width * 0.5f, Height - 20);

        DrawButtons();
        DrawSpectrum();
    }

    public override void _ExitTree()
    {
        StopMicCapture();
        base._ExitTree();
    }

    // ── Microphone capture ────────────────────────────────────────────────────

    private void StartMicCapture()
    {
        // Create a dedicated bus so we don't pollute Master
        captureBusIndex = AudioServer.BusCount;
        AudioServer.AddBus(captureBusIndex);
        AudioServer.SetBusName(captureBusIndex, "MicCapture");
        AudioServer.SetBusMute(captureBusIndex, true); // don't play mic back through speakers

        captureEffect = new AudioEffectCapture();
        AudioServer.AddBusEffect(captureBusIndex, captureEffect);

        micPlayer = new AudioStreamPlayer
        {
            Stream = new AudioStreamMicrophone(),
            Bus = "MicCapture",
        };
        AddChild(micPlayer);
        micPlayer.Play();

        micStatusText = "Mic: active";
    }

    private void StopMicCapture()
    {
        if (micPlayer != null)
        {
            micPlayer.Stop();
            if (IsInstanceValid(micPlayer))
                micPlayer.QueueFree();
            micPlayer = null;
        }

        if (captureEffect != null)
        {
            captureEffect = null;
        }

        if (captureBusIndex >= 0 && captureBusIndex < AudioServer.BusCount)
        {
            AudioServer.RemoveBus(captureBusIndex);
            captureBusIndex = -1;
        }
    }

    private void PollMicrophone()
    {
        if (captureEffect == null)
            return;

        int available = captureEffect.GetFramesAvailable();
        if (available <= 0)
            return;

        Vector2[] frames = captureEffect.GetBuffer(available);

        foreach (Vector2 frame in frames)
        {
            float mono = (frame.X + frame.Y) * 0.5f;
            int sample = Mathf.Clamp(Mathf.RoundToInt(mono * 32767f), -32767, 32767);

            rawSamples.Add(sample);
            if (isRecording)
                recordedSamples.Add(Mathf.Clamp(Mathf.RoundToInt(mono * 32767f) + 32767, 0, 65534));
        }

        if (rawSamples.Count > AnalysisWindowSize * 4)
            rawSamples.RemoveRange(0, rawSamples.Count - AnalysisWindowSize * 4);
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    private void PumpPlaybackSamples()
    {
        if (!isPlayingBack)
            return;

        if (playbackIndex >= recordedSamples.Count)
        {
            isPlayingBack = false;
            return;
        }

        playbackSampleAccumulator += DeltaTime * SampleRateHz;
        int samplesToPush = Mathf.FloorToInt(playbackSampleAccumulator);
        if (samplesToPush <= 0)
            return;

        playbackSampleAccumulator -= samplesToPush;

        for (int i = 0; i < samplesToPush && playbackIndex < recordedSamples.Count; i++)
        {
            rawSamples.Add(recordedSamples[playbackIndex]);
            playbackIndex += 1;
        }

        if (rawSamples.Count > AnalysisWindowSize * 4)
            rawSamples.RemoveRange(0, rawSamples.Count - AnalysisWindowSize * 4);

        if (playbackIndex >= recordedSamples.Count)
            isPlayingBack = false;
    }

    private void ToggleRecording()
    {
        if (!isRecording)
        {
            recordedSamples.Clear();
            isPlayingBack = false;
            playbackIndex = 0;
            playbackSampleAccumulator = 0.0f;
            isRecording = true;
            return;
        }

        isRecording = false;
    }

    private void StartPlayback()
    {
        if (recordedSamples.Count == 0)
        {
            analysisText = "No recording available";
            return;
        }

        isRecording = false;
        isPlayingBack = true;
        playbackIndex = 0;
        playbackSampleAccumulator = 0.0f;
        PlayRecordedAudio();
    }

    private void EnsureAudioPlayer()
    {
        if (playbackAudioPlayer != null)
            return;

        playbackAudioPlayer = new AudioStreamPlayer();
        AddChild(playbackAudioPlayer);
    }

    private void PlayRecordedAudio()
    {
        EnsureAudioPlayer();
        if (recordedSamples.Count == 0)
            return;

        int outputSampleCount = Mathf.Max(1, Mathf.RoundToInt(recordedSamples.Count * (float)PlaybackMixRateHz / SampleRateHz));
        byte[] pcm16 = new byte[outputSampleCount * 2];

        for (int i = 0; i < outputSampleCount; i++)
        {
            float srcIndex = i * (float)SampleRateHz / PlaybackMixRateHz;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(srcIndex), 0, recordedSamples.Count - 1);
            int i1 = Mathf.Clamp(i0 + 1, 0, recordedSamples.Count - 1);
            float frac = srcIndex - i0;

            float s0 = Mathf.Clamp((recordedSamples[i0] - 32767) / 32767.0f, -1f, 1f);
            float s1 = Mathf.Clamp((recordedSamples[i1] - 32767) / 32767.0f, -1f, 1f);
            float interpolated = Mathf.Lerp(s0, s1, frac);

            short sample16 = (short)(Mathf.Clamp(interpolated, -1f, 1f) * 30000f);
            int byteIndex = i * 2;
            pcm16[byteIndex] = (byte)(sample16 & 0xFF);
            pcm16[byteIndex + 1] = (byte)((sample16 >> 8) & 0xFF);
        }

        AudioStreamWav stream = new AudioStreamWav
        {
            Data = pcm16,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = PlaybackMixRateHz,
            Stereo = false,
        };

        playbackAudioPlayer.Stop();
        playbackAudioPlayer.Stream = stream;
        playbackAudioPlayer.Play();
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    private void DrawButtons()
    {
        Rect2 recordButtonRect = GetRecordButtonRect();
        Rect2 playButtonRect = GetPlayButtonRect();

        Color recordColor = isRecording ? new Color(0.85f, 0.2f, 0.2f) : new Color(0.2f, 0.2f, 0.2f);
        Color playColor = isPlayingBack ? new Color(0.2f, 0.7f, 0.3f) : new Color(0.2f, 0.2f, 0.2f);

        DrawRect(recordButtonRect, recordColor, true);
        DrawRect(playButtonRect, playColor, true);
        DrawRect(recordButtonRect, Colors.White, false, 1.0f);
        DrawRect(playButtonRect, Colors.White, false, 1.0f);

        TextAlign(HorizontalAlignment.Center);
        TextSize(12);
        Fill(Colors.White);
        Text(isRecording ? "STOP" : "RECORD", recordButtonRect.Position.X + recordButtonRect.Size.X * 0.5f, recordButtonRect.Position.Y + 20);
        Text("PLAY", playButtonRect.Position.X + playButtonRect.Size.X * 0.5f, playButtonRect.Position.Y + 20);
    }

    public override void MouseClicked()
    {
        Vector2 clickPos = new(MouseX, MouseY);
        if (GetRecordButtonRect().HasPoint(clickPos))
            ToggleRecording();
        else if (GetPlayButtonRect().HasPoint(clickPos))
            StartPlayback();
    }

    private Rect2 GetRecordButtonRect() => new(new Vector2(Width - 200, 10), new Vector2(90, 28));
    private Rect2 GetPlayButtonRect() => new(new Vector2(Width - 100, 10), new Vector2(90, 28));

    private static Color NoteColor(int midiNote) =>
        Color.FromHsv((midiNote % 12) / 12f, 0.75f, 0.95f);

    private void DrawNoteLanes()
    {
        float noteH = (LaneBottomY - LaneTopY) / (MidiNoteMax - MidiNoteMin + 1);

        for (int midi = MidiNoteMin; midi <= MidiNoteMax; midi++)
        {
            float cy = GetYForNote(midi);
            bool natural = IsNaturalNote(midi);
            bool isC     = (midi % 12) == 0;

            // background band
            Color bg = natural ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.10f, 0.10f, 0.10f);
            DrawRect(new Rect2(0, cy - noteH * 0.5f, Width, noteH - 1f), bg, true);

            // separator line at bottom of band
            Stroke(isC ? new Color(0.50f, 0.50f, 0.80f, 0.8f) : new Color(0.28f, 0.28f, 0.28f, 0.6f));
            StrokeWeight(isC ? 1.5f : 0.5f);
            Line(0, cy + noteH * 0.5f, Width, cy + noteH * 0.5f);

            // note label on the left
            if (natural)
            {
                NoStroke();
                Color col = isC ? new Color(0.75f, 0.75f, 1.00f) : new Color(0.50f, 0.50f, 0.50f);
                Fill(col);
                TextAlign(HorizontalAlignment.Left);
                TextSize(isC ? 11 : 9);
                Text(MidiToNoteName(midi), 4, cy + 4);
            }
        }
        NoStroke();
    }

    // ── Signal analysis ───────────────────────────────────────────────────────

    private void AnalyzeSignalAndSpawnNote()
    {
        if (rawSamples.Count < AnalysisWindowSize)
            return;

        float nowSeconds = Time.GetTicksMsec() / 1000.0f;
        if (lastAnalysisTimeSeconds >= 0 && nowSeconds - lastAnalysisTimeSeconds < AnalysisIntervalSeconds)
            return;

        float frequency = EstimateFrequencyFromLatestSamples();
        lastEstimatedFrequency = frequency;
        lastAnalysisTimeSeconds = nowSeconds;

        if (frequency <= 0)
        {
            analysisText = $"No stable pitch | peaks={detectedPeaks.Length}";
            return;
        }

        if (TryMapFrequencyToNote(frequency, out int midiNote))
        {
            lastDetectedMidiNote = midiNote;
            string topPeak = detectedPeaks.Length > 0 ? $" top={detectedPeaks[0].Freq:0}Hz" : "";
            analysisText = $"f={frequency:0.0}Hz {MidiToNoteName(midiNote)}{topPeak} peaks={detectedPeaks.Length}";
        }
        else
        {
            analysisText = $"f={frequency:0.0}Hz (out of range) peaks={detectedPeaks.Length}";
        }
    }

    private float EstimateFrequencyFromLatestSamples()
    {
        if (rawSamples.Count < AnalysisWindowSize) return 0f;

        int offset = rawSamples.Count - AnalysisWindowSize;
        float mean = 0f;
        for (int i = 0; i < AnalysisWindowSize; i++) mean += rawSamples[offset + i];
        mean /= AnalysisWindowSize;

        float rms = 0f;
        for (int i = 0; i < AnalysisWindowSize; i++)
        {
            float s = rawSamples[offset + i] - mean;
            rms += s * s;
        }
        rms = Mathf.Sqrt(rms / AnalysisWindowSize);

        lastRms = rms;
        if (rms < SilenceGateRms) return 0f;
        if (detectedPeaks.Length == 0) return 0f;

        float bestScore = 0f;
        float bestFreq = 0f;

        foreach (FreqPeak peak in detectedPeaks)
        {
            if (peak.Freq < MelodicaMinHz || peak.Freq > MelodicaMaxHz) continue;

            // Reward harmonics at 2f and 3f being present in the spectrum
            float score = peak.Magnitude;
            score += SampleSpectrumAtFreq(peak.Freq * 2f) * 0.5f;
            score += SampleSpectrumAtFreq(peak.Freq * 3f) * 0.3f;
            // Penalize if a strong sub-harmonic exists (suggests this peak is itself a harmonic)
            score *= 1f - SampleSpectrumAtFreq(peak.Freq * 0.5f) * 0.7f;

            if (score > bestScore)
            {
                bestScore = score;
                bestFreq = peak.Freq;
            }
        }

        return bestFreq;
    }

    private float SampleSpectrumAtFreq(float freq)
    {
        float binF = (freq - SpectrumDisplayMinHz) * (SpectrumSize - 1) / (SpectrumDisplayMaxHz - SpectrumDisplayMinHz);
        if (binF < 0 || binF >= SpectrumSize - 1) return 0f;
        int b = (int)binF;
        return Mathf.Lerp(spectrum[b], spectrum[b + 1], binF - b);
    }

    private static bool TryMapFrequencyToNote(float frequency, out int midiNote)
    {
        if (frequency < MelodicaMinHz || frequency > MelodicaMaxHz)
        {
            midiNote = 0;
            return false;
        }
        float midi = 69f + 12f * (Mathf.Log(frequency / 440f) / Mathf.Log(2f));
        midiNote = Mathf.Clamp(Mathf.RoundToInt(midi), MidiNoteMin, MidiNoteMax);
        return true;
    }

    private static float GetYForNote(int midiNote)
    {
        float t = (float)(midiNote - MidiNoteMin) / (MidiNoteMax - MidiNoteMin);
        return Mathf.Lerp(LaneBottomY, LaneTopY, t);
    }

    private void SpawnRandomNote()
    {
        int midi = Random(MidiNoteMin, MidiNoteMax + 1);
        notes.Add(new NoteState
        {
            X = Width + 30,
            Y = GetYForNote(midi),
            MidiNote = midi,
            Hit = false,
        });
    }

    private void ComputeSpectrum()
    {
        if (rawSamples.Count < AnalysisWindowSize)
        {
            detectedPeaks = [];
            return;
        }

        int offset = rawSamples.Count - AnalysisWindowSize;

        float mean = 0;
        for (int i = 0; i < AnalysisWindowSize; i++) mean += rawSamples[offset + i];
        mean /= AnalysisWindowSize;

        for (int i = 0; i < AnalysisWindowSize; i++)
        {
            float hann = 0.5f * (1f - Mathf.Cos(2f * Mathf.Pi * i / (AnalysisWindowSize - 1)));
            windowedBuffer[i] = (rawSamples[offset + i] - mean) * hann;
        }

        float freqSpan = SpectrumDisplayMaxHz - SpectrumDisplayMinHz;
        float max = 0;
        for (int k = 0; k < SpectrumSize; k++)
        {
            float freq = SpectrumDisplayMinHz + k * freqSpan / (SpectrumSize - 1);
            spectrum[k] = GoertzelMagnitude(windowedBuffer, freq, SampleRateHz);
            if (spectrum[k] > max) max = spectrum[k];
        }

        if (max > 0)
            for (int i = 0; i < SpectrumSize; i++) spectrum[i] /= max;
        else
            System.Array.Clear(spectrum, 0, SpectrumSize);

        detectedPeaks = FindSpectrumPeaks();
    }

    private static float GoertzelMagnitude(float[] samples, float targetFreq, float sampleRate)
    {
        float omega = 2f * Mathf.Pi * targetFreq / sampleRate;
        float coeff = 2f * Mathf.Cos(omega);
        float s1 = 0f, s2 = 0f;
        foreach (float x in samples)
        {
            float s0 = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return Mathf.Sqrt(s1 * s1 + s2 * s2 - s1 * s2 * coeff);
    }

    private FreqPeak[] FindSpectrumPeaks()
    {
        var candidates = new System.Collections.Generic.List<FreqPeak>(8);
        for (int i = 1; i < SpectrumSize - 1; i++)
        {
            if (spectrum[i] > spectrum[i - 1] && spectrum[i] > spectrum[i + 1] && spectrum[i] >= 0.1f)
            {
                float freq = SpectrumDisplayMinHz + i * (SpectrumDisplayMaxHz - SpectrumDisplayMinHz) / (SpectrumSize - 1);
                candidates.Add(new FreqPeak { Freq = freq, Magnitude = spectrum[i] });
            }
        }
        candidates.Sort((a, b) => b.Magnitude.CompareTo(a.Magnitude));
        int count = Mathf.Min(candidates.Count, MaxDetectedPeaks);
        FreqPeak[] result = new FreqPeak[count];
        for (int i = 0; i < count; i++) result[i] = candidates[i];
        return result;
    }

    private void DrawSpectrum()
    {
        const float baseY = 575f;   // sits just below LaneBottomY (510)
        const float maxBarH = 55f;
        float barWidth = (float)Width / SpectrumSize;

        // Highlight melodica fundamental range
        float melStartX = (MelodicaMinHz - SpectrumDisplayMinHz) / (SpectrumDisplayMaxHz - SpectrumDisplayMinHz) * Width;
        float melEndX   = (MelodicaMaxHz - SpectrumDisplayMinHz) / (SpectrumDisplayMaxHz - SpectrumDisplayMinHz) * Width;
        DrawRect(new Rect2(melStartX, baseY - maxBarH, melEndX - melStartX, maxBarH), new Color(0.15f, 0.3f, 0.15f), true);

        // Spectrum bars
        NoStroke();
        for (int i = 0; i < SpectrumSize; i++)
        {
            float h = spectrum[i] * maxBarH;
            if (h < 1f) continue;
            float freq = SpectrumDisplayMinHz + i * (SpectrumDisplayMaxHz - SpectrumDisplayMinHz) / (SpectrumSize - 1);
            bool inRange = freq >= MelodicaMinHz && freq <= MelodicaMaxHz;
            DrawRect(new Rect2(i * barWidth + 1, baseY - h, barWidth - 2, h),
                     inRange ? new Color(0.2f, 0.75f, 0.45f) : new Color(0.25f, 0.4f, 0.55f), true);
        }

        // Spectrum outline
        Stroke(new Color(0.3f, 1f, 0.65f));
        StrokeWeight(1f);
        Vector2 prev = Vector2.Zero;
        for (int i = 0; i < SpectrumSize; i++)
        {
            float x = i * barWidth + barWidth * 0.5f;
            float y = baseY - spectrum[i] * maxBarH;
            if (i > 0) Line(prev.X, prev.Y, x, y);
            prev = new Vector2(x, y);
        }

        // Detected peak markers with Hz labels
        for (int p = 0; p < detectedPeaks.Length; p++)
        {
            FreqPeak peak = detectedPeaks[p];
            float px = (peak.Freq - SpectrumDisplayMinHz) / (SpectrumDisplayMaxHz - SpectrumDisplayMinHz) * Width;
            float py = baseY - peak.Magnitude * maxBarH;
            bool inRange = peak.Freq >= MelodicaMinHz && peak.Freq <= MelodicaMaxHz;
            Color peakCol = inRange ? new Color(1f, 0.9f, 0.1f) : new Color(1f, 0.5f, 0.2f);

            Stroke(peakCol);
            StrokeWeight(1f);
            Line(px, py, px, baseY);
            NoStroke();
            Fill(peakCol);
            Circle(px, py, 4);

            TextAlign(HorizontalAlignment.Center);
            TextSize(9);
            Fill(Colors.White);
            Text($"{peak.Freq:0}Hz", px, py - 8);
        }

        // Frequency axis labels
        NoStroke();
        Fill(new Color(0.55f, 0.55f, 0.55f));
        TextSize(9);
        TextAlign(HorizontalAlignment.Center);
        foreach (float lf in new float[] { 200f, 400f, 600f, 800f, 1000f, 1200f })
        {
            float lx = (lf - SpectrumDisplayMinHz) / (SpectrumDisplayMaxHz - SpectrumDisplayMinHz) * Width;
            Text($"{(int)lf}", lx, baseY + 12);
        }

        // VU meter — thin vertical bar on the right edge
        const float vuW = 10f;
        float vuX = Width - vuW - 2;
        float rmsNorm = Mathf.Min(lastRms / RmsDisplayMax, 1f);
        float threshNorm = Mathf.Min(SilenceGateRms / RmsDisplayMax, 1f);
        // background track
        DrawRect(new Rect2(vuX, baseY - maxBarH, vuW, maxBarH), new Color(0.15f, 0.15f, 0.15f), true);
        // level bar: red below threshold, green above
        bool active = lastRms >= SilenceGateRms;
        float barH = rmsNorm * maxBarH;
        DrawRect(new Rect2(vuX, baseY - barH, vuW, barH),
                 active ? new Color(0.2f, 0.9f, 0.3f) : new Color(0.7f, 0.2f, 0.2f), true);
        // threshold line
        float threshY = baseY - threshNorm * maxBarH;
        Stroke(new Color(1f, 0.85f, 0f));
        StrokeWeight(1f);
        Line(vuX - 2, threshY, vuX + vuW + 2, threshY);
        NoStroke();
        Fill(new Color(0.55f, 0.55f, 0.55f));
        TextSize(8);
        TextAlign(HorizontalAlignment.Right);
        Text("VOL", vuX - 3, baseY - maxBarH + 8);
    }
}
