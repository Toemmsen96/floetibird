using Godot;
using System;
using System.Collections.Generic;
using System.IO.Ports;

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

    private static readonly PitchRange[] GuitarRanges =
    {
        new() { Note = 'e', Low = 62, High = 102 },
        new() { Note = 'A', Low = 100, High = 120 },
        new() { Note = 'D', Low = 120, High = 165 },
        new() { Note = 'G', Low = 165, High = 210 },
        new() { Note = 'B', Low = 210, High = 290 },
        new() { Note = 'E', Low = 290, High = 380 },
    };

    private readonly List<NoteState> notes = new();
    private readonly List<int> rawSamples = new();
    private readonly List<int> recordedSamples = new();
    private SerialPort serialPort;
    private float speed = 2.0f;
    private int score = 0;
    private float noteLaneX = 100.0f;
    private float lastSpawnTimeSeconds = -1.0f;
    private float lastAnalysisTimeSeconds = -1.0f;
    private float lastEstimatedFrequency = 0.0f;
    private const float SpawnCooldownSeconds = 0.08f;
    private const float AnalysisIntervalSeconds = 0.05f;
    private const int SampleRateHz = 1000;
    private const int AnalysisWindowSize = 128;
    private string serialConnectionText = "Serial: disconnected";
    private string analysisText = "Waiting for raw samples";
    private bool isRecording;
    private bool isPlayingBack;
    private int playbackIndex;
    private float playbackSampleAccumulator;
    private bool wasLeftMouseDown;
    private AudioStreamPlayer playbackAudioPlayer;
    private const int PlaybackMixRateHz = 8000;
    private float playerY = 200;
    private char lastDetectedNote = 'e';
    private int lastBarValue = 64;

    private const int SpectrumSize = 64;
    private float lastFilteredSample = 0f;

    private readonly float[] spectrum = new float[SpectrumSize];
    private float lowPass = 0f;

    private float lastRandomSpawnTime = 0f;
private const float RandomSpawnInterval = 1.0f; // adjust difficulty

    public override void Setup()
    {
        CloseSerialPort();
        EnsureAudioPlayer();
        notes.Clear();
        rawSamples.Clear();
        recordedSamples.Clear();
        score = 0;
        lastSpawnTimeSeconds = -1.0f;
        lastAnalysisTimeSeconds = -1.0f;
        lastEstimatedFrequency = 0.0f;
        analysisText = "Waiting for raw samples";
        isRecording = false;
        isPlayingBack = false;
        playbackIndex = 0;
        playbackSampleAccumulator = 0.0f;
        wasLeftMouseDown = false;

        SetTitle("Gametest");
        SetViewportMode(ViewportMode.Always);
        CreateCanvas(800, 400);
        GD.Randomize();
        Background(new Color(30f / 255f, 30f / 255f, 30f / 255f));

        OpenSerialPort();
    }

    public override void DrawSketch()
    {
        HandleButtonClicks();
        PollSerial();
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

            float playerY = GetYForNote(lastDetectedNote, lastBarValue);

            if (!note.Hit &&
                Mathf.Abs(note.X - noteLaneX) < 10 &&
                Mathf.Abs(note.Y - playerY) < 20)
            {
                note.Hit = true;
                score += 10;
            }
        }

        notes.RemoveAll(note => note.X <= -50);

        DrawSketchString(
            ThemeDB.FallbackFont,
            new Vector2(Width * 0.5f, 30),
            $"Score: {score}",
            HorizontalAlignment.Center,
            -1,
            20,
            Colors.Black
        );

        DrawSketchString(
            ThemeDB.FallbackFont,
            new Vector2(Width * 0.5f, Height - 40),
            "Arduino sends raw ADC values, PC computes pitch",
            HorizontalAlignment.Center,
            -1,
            14,
            Colors.White
        );

        DrawSketchString(
            ThemeDB.FallbackFont,
            new Vector2(Width * 0.5f, Height - 20),
            $"{serialConnectionText} | {analysisText} | rec={isRecording} play={isPlayingBack} samples={recordedSamples.Count}",
            HorizontalAlignment.Center,
            -1,
            12,
            Colors.White
        );

        DrawButtons();
        DrawSpectrum();
    }

    public override void _ExitTree()
    {
        CloseSerialPort();
        base._ExitTree();
    }

    private void OpenSerialPort()
    {
        string[] ports = SerialPort.GetPortNames();
        if (ports.Length == 0)
        {
            serialConnectionText = "Serial: no ports found";
            return;
        }

        Array.Sort(ports, ComparePortNames);

        foreach (string portName in ports)
        {
            try
            {
                SerialPort candidate = new SerialPort(portName, 115200)
                {
                    NewLine = "\n",
                    ReadTimeout = 1,
                    DtrEnable = true,
                    RtsEnable = true,
                };

                candidate.Open();
                candidate.DiscardInBuffer();

                serialPort = candidate;
                serialConnectionText = $"Serial: connected ({portName})";
                return;
            }
            catch (Exception)
            {
                continue;
            }
        }

        serialConnectionText = "Serial: failed to open available ports";
    }

    private void CloseSerialPort()
    {
        if (serialPort == null)
        {
            return;
        }

        try
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }
        }
        catch (Exception)
        {
            // Intentionally ignored during shutdown/restart.
        }

        serialPort.Dispose();
        serialPort = null;
    }

    private void PollSerial()
    {
        if (serialPort == null || !serialPort.IsOpen)
        {
            return;
        }

        try
        {
            int linesRead = 0;
            while (serialPort.BytesToRead > 0 && linesRead < 50)
            {
                string line = serialPort.ReadLine().Trim();
                linesRead += 1;

                if (line.Length == 0)
                {
                    continue;
                }

                if (int.TryParse(line, out int sample))
                {
                    lowPass = Mathf.Lerp(lowPass, sample, 0.15f);
                    float alpha = 0.15f;

                    float filtered = Mathf.Lerp(lastFilteredSample, sample, alpha);
                    lastFilteredSample = filtered;

                    float centered = filtered - 512.0f;

                    rawSamples.Add(Mathf.Clamp((int)centered, -512, 512));
                    if (isRecording)
                    {
                        recordedSamples.Add(Mathf.Clamp(sample, 0, 1023));
                    }
                    if (rawSamples.Count > AnalysisWindowSize * 4)
                    {
                        rawSamples.RemoveRange(0, rawSamples.Count - AnalysisWindowSize * 4);
                    }
                }
            }
        }
        catch (TimeoutException)
        {
            // No full line available yet.
        }
        catch (Exception ex)
        {
            string portName = serialPort.PortName;
            CloseSerialPort();
            serialConnectionText = $"Serial: error on {portName} ({ex.Message})";
        }
    }

    private void PumpPlaybackSamples()
    {
        if (!isPlayingBack)
        {
            return;
        }

        if (playbackIndex >= recordedSamples.Count)
        {
            isPlayingBack = false;
            return;
        }

        playbackSampleAccumulator += DeltaTime * SampleRateHz;
        int samplesToPush = Mathf.FloorToInt(playbackSampleAccumulator);
        if (samplesToPush <= 0)
        {
            return;
        }

        playbackSampleAccumulator -= samplesToPush;

        for (int i = 0; i < samplesToPush && playbackIndex < recordedSamples.Count; i++)
        {
            rawSamples.Add(recordedSamples[playbackIndex]);
            playbackIndex += 1;
        }

        if (rawSamples.Count > AnalysisWindowSize * 4)
        {
            rawSamples.RemoveRange(0, rawSamples.Count - AnalysisWindowSize * 4);
        }

        if (playbackIndex >= recordedSamples.Count)
        {
            isPlayingBack = false;
        }
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
        {
            return;
        }

        playbackAudioPlayer = new AudioStreamPlayer();
        AddChild(playbackAudioPlayer);
    }

    private void PlayRecordedAudio()
    {
        EnsureAudioPlayer();
        if (recordedSamples.Count == 0)
        {
            return;
        }

        int outputSampleCount = Mathf.Max(1, Mathf.RoundToInt(recordedSamples.Count * (float)PlaybackMixRateHz / SampleRateHz));
        byte[] pcm16 = new byte[outputSampleCount * 2];

        float mean = 0.0f;
        for (int i = 0; i < recordedSamples.Count; i++)
        {
            mean += recordedSamples[i];
        }

        mean /= recordedSamples.Count;

        for (int i = 0; i < outputSampleCount; i++)
        {
            float srcIndex = i * (float)SampleRateHz / PlaybackMixRateHz;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(srcIndex), 0, recordedSamples.Count - 1);
            int i1 = Mathf.Clamp(i0 + 1, 0, recordedSamples.Count - 1);
            float frac = srcIndex - i0;

            float s0 = Mathf.Clamp((recordedSamples[i0]) / 512.0f, -1f, 1f);
            float s1 = Mathf.Clamp((recordedSamples[i1]) / 512.0f, -1f, 1f);
            float interpolated = Mathf.Lerp(s0, s1, frac);

            float normalized = Mathf.Clamp(interpolated, -1f, 1f);
            short sample16 = (short)(normalized * 30000f);

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

        DrawSketchString(
            ThemeDB.FallbackFont,
            new Vector2(recordButtonRect.Position.X + recordButtonRect.Size.X * 0.5f, recordButtonRect.Position.Y + 20),
            isRecording ? "STOP" : "RECORD",
            HorizontalAlignment.Center,
            -1,
            12,
            Colors.White
        );

        DrawSketchString(
            ThemeDB.FallbackFont,
            new Vector2(playButtonRect.Position.X + playButtonRect.Size.X * 0.5f, playButtonRect.Position.Y + 20),
            "PLAY",
            HorizontalAlignment.Center,
            -1,
            12,
            Colors.White
        );
    }

    private void HandleButtonClicks()
    {
        bool isLeftMouseDown = Input.IsMouseButtonPressed(Godot.MouseButton.Left);
        if (isLeftMouseDown && !wasLeftMouseDown)
        {
            Vector2 clickPos = new Vector2(MouseX, MouseY);
            Rect2 recordButtonRect = GetRecordButtonRect();
            Rect2 playButtonRect = GetPlayButtonRect();

            if (recordButtonRect.HasPoint(clickPos))
            {
                ToggleRecording();
            }
            else if (playButtonRect.HasPoint(clickPos))
            {
                StartPlayback();
            }
        }

        wasLeftMouseDown = isLeftMouseDown;
    }

    private Rect2 GetRecordButtonRect()
    {
        return new Rect2(new Vector2(Width - 200, 10), new Vector2(90, 28));
    }

    private Rect2 GetPlayButtonRect()
    {
        return new Rect2(new Vector2(Width - 100, 10), new Vector2(90, 28));
    }

    private void AnalyzeSignalAndSpawnNote()
    {
        if (rawSamples.Count < AnalysisWindowSize)
        {
            return;
        }

        float nowSeconds = Time.GetTicksMsec() / 1000.0f;
        if (lastAnalysisTimeSeconds >= 0 && nowSeconds - lastAnalysisTimeSeconds < AnalysisIntervalSeconds)
        {
            return;
        }

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
        {
            mean += rawSamples[offset + i];
        }

        mean /= AnalysisWindowSize;

        float[] centered = new float[AnalysisWindowSize];
        for (int i = 0; i < AnalysisWindowSize; i++)
        {
            centered[i] = rawSamples[offset + i] - mean;
        }

        int minLag = 2;
        int maxLag = 32;
        float bestScore = float.MinValue;
        int bestLag = -1;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            float corr = 0.0f;
            for (int i = 0; i < AnalysisWindowSize - lag; i++)
            {
                corr += centered[i] * centered[i + lag];
            }

            if (corr > bestScore)
            {
                bestScore = corr;
                bestLag = lag;
            }
        }

        if (bestLag <= 0 || bestScore <= 0)
        {
            return 0.0f;
        }

        return (float)SampleRateHz / bestLag;
    }

    private static bool TryMapFrequencyToNote(float frequency, out char note, out int barValue)
    {
        note = '\0';
        barValue = 64;

        foreach (PitchRange range in GuitarRanges)
        {
            if (frequency <= range.Low || frequency >= range.High)
            {
                continue;
            }

            note = range.Note;
            float t = Mathf.Clamp((frequency - range.Low) / (range.High - range.Low), 0.0f, 1.0f);
            barValue = Mathf.RoundToInt(Mathf.Lerp(1, 128, t));
            return true;
        }

        return false;
    }
    private void SpawnNoteFromSerial(char note, int barValue)
    {
        float nowSeconds = Time.GetTicksMsec() / 1000.0f;
        if (lastSpawnTimeSeconds >= 0 && nowSeconds - lastSpawnTimeSeconds < SpawnCooldownSeconds)
        {
            return;
        }

        notes.Add(
            new NoteState
            {
                X = Width + 30,
                Y = GetYForNote(note, barValue),
                Hit = false,
            }
        );

        lastSpawnTimeSeconds = nowSeconds;
    }

    private float GetYForNote(char note, int barValue)
    {
        return note switch
        {
            'e' => 60,
            'A' => 120,
            'D' => 180,
            'G' => 240,
            'B' => 300,
            'E' => 360,
            _ => Mathf.Lerp(Height - 40, 40, Mathf.Clamp(barValue, 0, 128) / 128.0f),
        };
    }

    private static int ComparePortNames(string left, string right)
    {
        int leftRank = GetPortRank(left);
        int rightRank = GetPortRank(right);
        if (leftRank != rightRank)
        {
            return leftRank.CompareTo(rightRank);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPortRank(string name)
    {
        if (name.Contains("ttyACM", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.Contains("COM", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private void SpawnRandomNote()
{
    char[] possibleNotes = { 'e', 'A', 'D', 'G', 'B', 'E' };
    char note = possibleNotes[GD.RandRange(0, possibleNotes.Length - 1)];

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

    // reset spectrum
    for (int i = 0; i < SpectrumSize; i++)
        spectrum[i] = 0;

    int minLag = 2;
    int maxLag = 40;

    for (int lag = minLag; lag < maxLag; lag++)
    {
        float corr = 0;

        for (int i = 0; i < AnalysisWindowSize - lag; i++)
        {
            corr += centered[i] * centered[i + lag];
        }

        int index = Mathf.Clamp((int)((float)lag / maxLag * SpectrumSize), 0, SpectrumSize - 1);

        spectrum[index] += Mathf.Max(0, corr);
    }

    // normalize
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
        float y = baseY - spectrum[i] * 120; // height scale

        if (i > 0)
        {
            Line(prev.X, prev.Y, x, y);
        }

        prev = new Vector2(x, y);
    }
}
}