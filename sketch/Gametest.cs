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
        public bool Hit { get; set; }
    }

    // Melodica F3–F6: ~175 Hz – ~1397 Hz, split into three playable registers
    private static readonly PitchRange[] MelodicaRanges =
    {
        new() { Note = 'L', Low = 175,  High = 370  },  // low register  F3–F#4
        new() { Note = 'M', Low = 370,  High = 740  },  // mid register  G4–F#5
        new() { Note = 'H', Low = 740,  High = 1397 },  // high register G5–F6
    };

    // Full melodica range used for the silence gate
    private const float MelodicaMinHz = 175f;
    private const float MelodicaMaxHz = 1397f;

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
    private readonly float[] spectrum = new float[SpectrumSize];

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

        Stroke(new Color(0f, 1f, 0f));
        Line(noteLaneX, 0, noteLaneX, Height);

        NoStroke();
        Fill(new Color(0f, 1f, 0f));
        playerY = GetYForNote(lastDetectedNote, lastBarValue);
        Circle(noteLaneX, playerY, 12);

        foreach (NoteState note in notes)
        {
            note.X -= speed;

            Fill(new Color(1f, 200f / 255f, 0f));
            Circle(note.X, note.Y, 10);

            float pY = GetYForNote(lastDetectedNote, lastBarValue);

            if (!note.Hit &&
                Mathf.Abs(note.X - noteLaneX) < 10 &&
                Mathf.Abs(note.Y - pY) < 20)
            {
                note.Hit = true;
                score += 10;
            }
        }

        notes.RemoveAll(note => note.X <= -50);

        TextAlign(HorizontalAlignment.Center);
        TextSize(20);
        Fill(Colors.Black);
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
            analysisText = "No stable pitch";
            return;
        }

        if (TryMapFrequencyToNote(frequency, out char note, out int barValue))
        {
            lastDetectedNote = note;
            lastBarValue = barValue;
            analysisText = $"f={frequency:0.0}Hz note={note} bar={barValue}";
        }
        else
        {
            analysisText = $"f={frequency:0.0}Hz (out of range)";
        }
    }

    private float EstimateFrequencyFromLatestSamples()
    {
        int offset = rawSamples.Count - AnalysisWindowSize;
        float mean = 0.0f;
        for (int i = 0; i < AnalysisWindowSize; i++)
            mean += rawSamples[offset + i];
        mean /= AnalysisWindowSize;

        float[] centered = new float[AnalysisWindowSize];
        for (int i = 0; i < AnalysisWindowSize; i++)
            centered[i] = rawSamples[offset + i] - mean;

        // At 44100 Hz: lag 32 ≈ 1397 Hz (melodica top), lag 252 ≈ 175 Hz (melodica bottom)
        int minLag = 32;
        int maxLag = 252;
        float bestScore = float.MinValue;
        int bestLag = -1;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            float corr = 0.0f;
            for (int i = 0; i < AnalysisWindowSize - lag; i++)
                corr += centered[i] * centered[i + lag];

            if (corr > bestScore)
            {
                bestScore = corr;
                bestLag = lag;
            }
        }

        if (bestLag <= 0 || bestScore <= 0)
            return 0.0f;

        float freq = (float)SampleRateHz / bestLag;

        // Silence gate: reject anything outside the melodica range
        if (freq < MelodicaMinHz || freq > MelodicaMaxHz)
            return 0.0f;

        return freq;
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
            Hit = false,
        });
    }

    private void ComputeSpectrum()
    {
        if (rawSamples.Count < AnalysisWindowSize)
            return;

        int offset = rawSamples.Count - AnalysisWindowSize;

        float mean = 0;
        for (int i = 0; i < AnalysisWindowSize; i++)
            mean += rawSamples[offset + i];
        mean /= AnalysisWindowSize;

        float[] centered = new float[AnalysisWindowSize];
        for (int i = 0; i < AnalysisWindowSize; i++)
            centered[i] = rawSamples[offset + i] - mean;

        for (int i = 0; i < SpectrumSize; i++)
            spectrum[i] = 0;

        int minLag = 32;
        int maxLag = 252;

        for (int lag = minLag; lag < maxLag; lag++)
        {
            float corr = 0;
            for (int i = 0; i < AnalysisWindowSize - lag; i++)
                corr += centered[i] * centered[i + lag];

            int index = Mathf.Clamp((int)((float)lag / maxLag * SpectrumSize), 0, SpectrumSize - 1);
            spectrum[index] += Mathf.Max(0, corr);
        }

        float max = 0;
        for (int i = 0; i < SpectrumSize; i++)
            max = Mathf.Max(max, spectrum[i]);

        if (max > 0)
        {
            for (int i = 0; i < SpectrumSize; i++)
                spectrum[i] /= max;
        }
    }

    private void DrawSpectrum()
    {
        float baseY = Height - 50;
        float width = Width;
        float step = width / SpectrumSize;

        Stroke(new Color(0.3f, 1f, 0.6f));

        Vector2 prev = Vector2.Zero;
        for (int i = 0; i < SpectrumSize; i++)
        {
            float x = i * step;
            float y = baseY - spectrum[i] * 120;

            if (i > 0)
                Line(prev.X, prev.Y, x, y);

            prev = new Vector2(x, y);
        }
    }
}
