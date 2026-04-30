const int MIC_PIN = A2;
const unsigned long SAMPLE_RATE_HZ = 2000;
const unsigned long SAMPLE_PERIOD_US = 1000000UL / SAMPLE_RATE_HZ;

unsigned long nextSampleUs = 0;

void setup() {
  Serial.begin(115200);
  pinMode(MIC_PIN, INPUT);
  nextSampleUs = micros();
}

void loop() {
  unsigned long now = micros();

  if ((long)(now - nextSampleUs) < 0) {
    return;
  }

  nextSampleUs += SAMPLE_PERIOD_US;

  int sample = analogRead(MIC_PIN);

  // light smoothing
  delayMicroseconds(30);
  sample = (sample + analogRead(MIC_PIN)) / 2;

  // center signal
  int centered = sample - 512;

  Serial.println(centered);
}