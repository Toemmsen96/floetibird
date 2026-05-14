using Godot;
using System.Collections.Generic;

public partial class Gametest : GodotP5
{
    private sealed class PitchRange
    {
        public char Note { get; set; }
        public float Low { get; set; }
        public float High { get; set; }
    }

    private sealed class NoteState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public char Note { get; set; }
        public bool Hit { get; set; }
    }

    private struct FreqPeak
    {
        public float Freq;
        public float Magnitude;
    }

    // 2-octave melodica F3–F5: ~175 Hz – ~699 Hz, split into three playable registers
    private static readonly PitchRange[] MelodicaRanges =
    {
        new() { Note = 'L', Low = 175, High = 350 },  // low    F3–F4
        new() { Note = 'M', Low = 350, High = 523 },  // mid    F4–C5
        new() { Note = 'H', Low = 523, High = 699 },  // high   C5–F5
    };

    // Full 2-octave melodica range used for the silence gate
    private const float MelodicaMinHz = 175f;
    private const float MelodicaMaxHz = 699f;

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
    private char lastDetectedNote = 'M';
    private int lastBarValue = 64;

    private const int SpectrumSize = 64;
    private const float SpectrumDisplayMinHz = 100f;
    private const float SpectrumDisplayMaxHz = 1400f;
    private readonly float[] spectrum = new float[SpectrumSize];
    private FreqPeak[] detectedPeaks = [];
    private readonly float[] windowedBuffer = new float[AnalysisWindowSize];
    private const int MaxDetectedPeaks = 4;

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
        CreateCanvas(800, 400);
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
        Line(noteLaneX, 0, noteLaneX, Height);
        StrokeWeight(1f);

        NoStroke();
        playerY = GetYForNote(lastDetectedNote, lastBarValue);
        Fill(NoteColor(lastDetectedNote));
        Circle(noteLaneX, playerY, 12);

        foreach (NoteState note in notes)
        {
            note.X -= speed;

            Fill(NoteColor(note.Note));
            Circle(note.X, note.Y, 10);

            if (!note.Hit &&
                Mathf.Abs(note.X - noteLaneX) < 12 &&
                note.Note == lastDetectedNote)
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

    private static Color NoteColor(char note) => note switch
    {
        'H' => new Color(0.4f, 0.6f, 1.0f),   // blue
        'M' => new Color(0.3f, 1.0f, 0.5f),   // green
        'L' => new Color(1.0f, 0.6f, 0.2f),   // orange
        _   => Colors.White,
    };

    private void DrawNoteLanes()
    {
        StrokeWeight(1f);

        // horizontal lane guide lines
        float[] laneYs = [100, 200, 300];
        char[] laneNotes = ['H', 'M', 'L'];

        for (int i = 0; i < laneYs.Length; i++)
        {
            Color c = NoteColor(laneNotes[i]);
            Stroke(new Color(c.R, c.G, c.B, 0.35f));
            Line(0, laneYs[i], Width, laneYs[i]);
        }

        // labels on the left
        TextAlign(HorizontalAlignment.Left);
        TextSize(13);
        NoStroke();
        for (int i = 0; i < laneYs.Length; i++)
        {
            Fill(NoteColor(laneNotes[i]));
            Text(laneNotes[i].ToString(), 6, laneYs[i] - 3);
        }
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

        if (TryMapFrequencyToNote(frequency, out char note, out int barValue))
        {
            lastDetectedNote = note;
            lastBarValue = barValue;
            string topPeak = detectedPeaks.Length > 0 ? $" top={detectedPeaks[0].Freq:0}Hz" : "";
            analysisText = $"f={frequency:0.0}Hz note={note}{topPeak} peaks={detectedPeaks.Length}";
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

        if (rms < 160f) return 0f;
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

    private static bool TryMapFrequencyToNote(float frequency, out char note, out int barValue)
    {
        note = '\0';
        barValue = 64;

        foreach (PitchRange range in MelodicaRanges)
        {
            if (frequency <= range.Low || frequency >= range.High)
                continue;

            note = range.Note;
            float t = Mathf.Clamp((frequency - range.Low) / (range.High - range.Low), 0.0f, 1.0f);
            barValue = Mathf.RoundToInt(Mathf.Lerp(1, 128, t));
            return true;
        }

        return false;
    }

    private float GetYForNote(char note, int barValue)
    {
        return note switch
        {
            'L' => 300,  // low register
            'M' => 200,  // mid register
            'H' => 100,  // high register
            _ => Mathf.Lerp(Height - 40, 40, Mathf.Clamp(barValue, 0, 128) / 128.0f),
        };
    }

    private void SpawnRandomNote()
    {
        char[] possibleNotes = ['L', 'M', 'H'];
        char note = possibleNotes[Random(0, possibleNotes.Length)];

        notes.Add(new NoteState
        {
            X = Width + 30,
            Y = GetYForNote(note, 64),
            Note = note,
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
        float baseY = Height - 50;
        float maxBarH = 110f;
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
    }
}
