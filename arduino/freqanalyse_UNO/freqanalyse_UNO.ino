const int MIC_PIN = A2;
const unsigned long SAMPLE_RATE_HZ = 1000;
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

  // Raw ADC sample in range [0..1023], one sample per line.
  int sample = analogRead(MIC_PIN);
  Serial.println(sample);
}