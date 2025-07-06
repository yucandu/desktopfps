// Keep only necessary includes and definitions
#include <LovyanGFX.hpp>
#include <SPI.h>
#include <ArduinoJson.h>
#include <ArduinoOTA.h>
#include <WiFi.h>
#include <BlynkSimpleEsp32.h>

char auth[] = "8gJkMOvx8u5vKCVbjsAheg-gL9mp64Cg";
const char* ssid = "mikesnet";
const char* password = "springchicken";
bool otaStarted = false;

#define every(interval) \
    static uint32_t __every__##interval = millis(); \
    if (millis() - __every__##interval >= interval && (__every__##interval = millis()))

class LGFX : public lgfx::LGFX_Device {
  lgfx::Panel_ST7735S   _panel_instance;
  lgfx::Bus_SPI _bus_instance;
public:
  LGFX(void) {
    { // SPI bus config
      auto cfg = _bus_instance.config();
      cfg.spi_host = SPI2_HOST;
      cfg.spi_mode = 3;
      cfg.freq_write = 40000000;
      cfg.freq_read = 20000000;
      cfg.spi_3wire = true;
      cfg.use_lock = true;
      cfg.dma_channel = SPI_DMA_CH_AUTO;
      cfg.pin_sclk = 4;
      cfg.pin_mosi = 3;
      cfg.pin_miso = -1;
      cfg.pin_dc   = 5;
      _bus_instance.config(cfg);
      _panel_instance.setBus(&_bus_instance);
    }
    { // Panel config
      auto cfg = _panel_instance.config();
      cfg.pin_cs = 6;
      cfg.pin_rst = 8;
      cfg.pin_busy = -1;
      cfg.panel_width = 80;
      cfg.panel_height = 160;
      cfg.offset_x = 24;
      cfg.offset_y = 0;
      cfg.offset_rotation = 0;
      cfg.readable = false;
      cfg.invert = false;
      cfg.rgb_order = false; 
      cfg.dlen_16bit = false;
      cfg.bus_shared = true;
      _panel_instance.config(cfg);
    }
    setPanel(&_panel_instance);
  }
};

LGFX tft;
LGFX_Sprite img(&tft);

// Anti-aliased text rendering setup
void setupAntiAliasing() {
  tft.setTextSize(1);
  tft.setFont(&fonts::FreeSans9pt7b);  // Use built-in anti-aliased font
  img.setTextSize(1);
  img.setFont(&fonts::FreeSans9pt7b);
}

// Modern cyberpunk-inspired color palette
#define COLOR_BG_DARK    0x0841    // Deep dark purple
#define COLOR_BG_MID     0x1082    // Medium purple-blue
#define COLOR_ACCENT     0x0314    // Hot magenta/pink
#define COLOR_GPU_MAIN   0x0575    // Bright cyan
#define COLOR_CPU_MAIN   0x04bf    // Bright yellow
#define COLOR_TEMP_HOT   0xFD20    // Hot orange
#define COLOR_TEMP_COOL  0x04f3    // Cool blue
#define COLOR_FPS_GOOD   0x07E0    // Bright green
#define COLOR_FPS_BAD    0xF800    // Red
#define COLOR_GRAPH_LINE 0x07FF    // Cyan for graph
#define COLOR_GRAPH_FILL 0x0410    // Dark cyan for fill
#define COLOR_GRID       0x2945    // Subtle grid
#define COLOR_TEXT_MAIN  0xFFFF    // White
#define COLOR_TEXT_DIM   0x8410    // Dim gray

// FPS Graph Configuration
#define GRAPH_WIDTH   155
#define GRAPH_HEIGHT  45
#define GRAPH_X       2
#define GRAPH_Y       2
#define MAX_FPS_SAMPLES 160  // More samples for smoother graph
#define GRAPH_UPDATE_INTERVAL 40

struct HardwareData {
  float cpu_temp = -1;
  float gpu_temp = -1;
  float fps = -1;
  float gpu_fan_speed = -1;
  int brightness = 127;
  float cpu_load = -1;
  unsigned long timestamp = 0;
  unsigned long last_update = 0;
  bool data_valid = false;
};

HardwareData hwData;

// FPS Graph data
float fpsHistory[MAX_FPS_SAMPLES];
float cpuUsageHistory[MAX_FPS_SAMPLES];
int cpuUsageIndex = 0;
int cpuUsageCount = 0;
float minCPU = 0, maxCPU = 100;
int fpsIndex = 0;
int fpsCount = 0;
int oldBrightness = 127;
float minFPS = 0, maxFPS = 60;
unsigned long lastGraphUpdate = 0;

// Timeout for data validity (10 seconds)
const unsigned long DATA_TIMEOUT = 10000;

// Custom degree symbol drawing
void drawDegreeSymbol(int x, int y, uint16_t color) {
  img.drawCircle(x, y, 2, color);
  img.drawCircle(x, y, 1, color);
}

void addCPUUsageData(float usage) {
  if (usage >= 0) {
    cpuUsageHistory[cpuUsageIndex] = usage;
    cpuUsageIndex = (cpuUsageIndex + 1) % MAX_FPS_SAMPLES;
    if (cpuUsageCount < MAX_FPS_SAMPLES) cpuUsageCount++;

    minCPU = 0;
    maxCPU = 100; 
  }
}

void drawCPUUsageGraph() {
  for (int i = 0; i < GRAPH_HEIGHT; i++) {
    uint16_t color = img.color565(
      map(i, 0, GRAPH_HEIGHT, 12, 25),   // R
      map(i, 0, GRAPH_HEIGHT, 12, 25),   // G  
      map(i, 0, GRAPH_HEIGHT, 0, 10)     // B
    );
    img.drawLine(GRAPH_X, GRAPH_Y + i, GRAPH_X + GRAPH_WIDTH, GRAPH_Y + i, color);
  }

  for (int i = 1; i < 5; i++) {
    int y = GRAPH_Y + (GRAPH_HEIGHT * i / 5);
    img.drawLine(GRAPH_X + 5, y, GRAPH_X + GRAPH_WIDTH - 5, y, COLOR_GRID);
  }

  if (cpuUsageCount > 1) {
    int startIdx = (cpuUsageIndex - cpuUsageCount + MAX_FPS_SAMPLES) % MAX_FPS_SAMPLES;
    
    for (int i = 1; i < cpuUsageCount; i++) {
      int idx1 = (startIdx + i - 1) % MAX_FPS_SAMPLES;
      int idx2 = (startIdx + i) % MAX_FPS_SAMPLES;

      float c1 = cpuUsageHistory[idx1];
      float c2 = cpuUsageHistory[idx2];

      int x1 = GRAPH_X + 3 + ((i - 1) * (GRAPH_WIDTH - 6)) / (cpuUsageCount - 1);
      int x2 = GRAPH_X + 3 + (i * (GRAPH_WIDTH - 6)) / (cpuUsageCount - 1);

      int y1 = GRAPH_Y + GRAPH_HEIGHT - 3 - ((c1 - minCPU) / (maxCPU - minCPU)) * (GRAPH_HEIGHT - 6);
      int y2 = GRAPH_Y + GRAPH_HEIGHT - 3 - ((c2 - minCPU) / (maxCPU - minCPU)) * (GRAPH_HEIGHT - 6);

      y1 = constrain(y1, GRAPH_Y + 3, GRAPH_Y + GRAPH_HEIGHT - 3);
      y2 = constrain(y2, GRAPH_Y + 3, GRAPH_Y + GRAPH_HEIGHT - 3);

      img.drawLine(x1, y1, x2, y2, COLOR_CPU_MAIN);
      img.fillTriangle(x1, y1, x2, y2, x1, GRAPH_Y + GRAPH_HEIGHT - 3, COLOR_CPU_MAIN);
      img.fillTriangle(x1, GRAPH_Y + GRAPH_HEIGHT - 3, x2, y2, x2, GRAPH_Y + GRAPH_HEIGHT - 3, COLOR_CPU_MAIN);
      //img.drawLine(x1, y1 - 1, x2, y2 - 1, COLOR_CPU_MAIN);
      //img.drawLine(x1, y1 + 1, x2, y2 + 1, COLOR_CPU_MAIN);
    }
  }

  img.drawRoundRect(GRAPH_X, GRAPH_Y, GRAPH_WIDTH, GRAPH_HEIGHT, 4, COLOR_ACCENT);

  img.setTextColor(COLOR_TEXT_DIM);
  char maxLabel[8];
  snprintf(maxLabel, sizeof(maxLabel), "%.0f%%", maxCPU);
  img.drawString(maxLabel, GRAPH_X + 5, GRAPH_Y + 3);

  char minLabel[8];
  snprintf(minLabel, sizeof(minLabel), "%.0f%%", minCPU);
  img.drawString(minLabel, GRAPH_X + 5, GRAPH_Y + GRAPH_HEIGHT - 12);

  img.setTextColor(COLOR_CPU_MAIN);
  img.drawString("CPU", GRAPH_X + GRAPH_WIDTH - 60, GRAPH_Y + 3);

  if (hwData.cpu_load >= 0) {
    char cpuStr[10];
    snprintf(cpuStr, sizeof(cpuStr), "%.1f%%", hwData.cpu_load);
    img.drawString(cpuStr, GRAPH_X + GRAPH_WIDTH - 25, GRAPH_Y + 3);
  } else {
    img.setTextColor(COLOR_TEXT_DIM);
    img.drawString("N/A", GRAPH_X + GRAPH_WIDTH - 25, GRAPH_Y + 3);
  }
}


// Enhanced text drawing with custom symbols
void drawTempText(const char* label, float temp, int x, int y, uint16_t labelColor, uint16_t tempColor) {
  img.setTextColor(labelColor);
  img.drawString(label, x, y);
  
  if (temp > 0) {
    char tempStr[10];
    snprintf(tempStr, sizeof(tempStr), "%.1f", temp);
    img.setTextColor(tempColor);
    img.drawString(tempStr, x + strlen(label) * 6 + 2, y);
    
    // Draw custom degree symbol
    int degreeX = x + strlen(label) * 6 + 2 + strlen(tempStr) * 6 + 2;
    //drawDegreeSymbol(degreeX, y + 2, tempColor);
    
    // Draw 'C'
    img.drawString(String((char)247) + "C", degreeX, y);

  } else {
    img.setTextColor(COLOR_TEXT_DIM);
    img.drawString("N/A", x + strlen(label) * 6 + 2, y);
  }
}

void addFPSData(float fps) {
  if (fps > 0) {
    fpsHistory[fpsIndex] = fps;
    fpsIndex = (fpsIndex + 1) % MAX_FPS_SAMPLES;
    if (fpsCount < MAX_FPS_SAMPLES) {
      fpsCount++;
    }
    
    // Auto-scale with better range detection
    minFPS = fpsHistory[0];
    maxFPS = fpsHistory[0];
    for (int i = 0; i < fpsCount; i++) {
      if (fpsHistory[i] < minFPS) minFPS = fpsHistory[i];
      if (fpsHistory[i] > maxFPS) maxFPS = fpsHistory[i];
    }
    
    // Smart scaling
    float range = maxFPS - minFPS;
    if (range < 15) range = 15;
    minFPS = max(0.0f, minFPS - range * 0.15f);
    maxFPS += range * 0.15f;
  }
}

void drawFPSGraph() {
  // Gradient background
  for (int i = 0; i < GRAPH_HEIGHT; i++) {
    uint16_t color = img.color565(
      map(i, 0, GRAPH_HEIGHT, 8, 25),   // R
      map(i, 0, GRAPH_HEIGHT, 4, 16),   // G  
      map(i, 0, GRAPH_HEIGHT, 20, 40)   // B
    );
    img.drawLine(GRAPH_X, GRAPH_Y + i, GRAPH_X + GRAPH_WIDTH, GRAPH_Y + i, color);
  }
  
  // Subtle grid
  for (int i = 1; i < 5; i++) {
    int y = GRAPH_Y + (GRAPH_HEIGHT * i / 5);
    img.drawLine(GRAPH_X + 5, y, GRAPH_X + GRAPH_WIDTH - 5, y, COLOR_GRID);
  }
  
  // Draw filled area under curve first
  if (fpsCount > 1) {
    int startIdx = (fpsIndex - fpsCount + MAX_FPS_SAMPLES) % MAX_FPS_SAMPLES;
    

    
    // Draw the main line with anti-aliasing
    for (int i = 1; i < fpsCount; i++) {
      int idx1 = (startIdx + i - 1) % MAX_FPS_SAMPLES;
      int idx2 = (startIdx + i) % MAX_FPS_SAMPLES;
      
      float fps1 = fpsHistory[idx1];
      float fps2 = fpsHistory[idx2];
      
      int x1 = GRAPH_X + 3 + ((i - 1) * (GRAPH_WIDTH - 6)) / (fpsCount - 1);
      int x2 = GRAPH_X + 3 + (i * (GRAPH_WIDTH - 6)) / (fpsCount - 1);
      
      int y1 = GRAPH_Y + GRAPH_HEIGHT - 3 - ((fps1 - minFPS) / (maxFPS - minFPS)) * (GRAPH_HEIGHT - 6);
      int y2 = GRAPH_Y + GRAPH_HEIGHT - 3 - ((fps2 - minFPS) / (maxFPS - minFPS)) * (GRAPH_HEIGHT - 6);
      
      y1 = constrain(y1, GRAPH_Y + 3, GRAPH_Y + GRAPH_HEIGHT - 3);
      y2 = constrain(y2, GRAPH_Y + 3, GRAPH_Y + GRAPH_HEIGHT - 3);
      
      // Anti-aliased line drawing
      img.drawLine(x1, y1, x2, y2, COLOR_GRAPH_LINE);
      img.drawLine(x1, y1-1, x2, y2-1, COLOR_GRAPH_LINE);
      img.drawLine(x1, y1+1, x2, y2+1, COLOR_GRAPH_LINE);
    }
  }
  
  // Stylish border with rounded corners
  img.drawRoundRect(GRAPH_X, GRAPH_Y, GRAPH_WIDTH, GRAPH_HEIGHT, 4, COLOR_ACCENT);
  
  // Scale labels with better positioning
  img.setTextColor(COLOR_TEXT_MAIN);
  char maxLabel[8];
  snprintf(maxLabel, sizeof(maxLabel), "%.0f", maxFPS);
  img.drawString(maxLabel, GRAPH_X + 5, GRAPH_Y + 3);
  
  char minLabel[8];
  snprintf(minLabel, sizeof(minLabel), "%.0f", minFPS);
  img.drawString(minLabel, GRAPH_X + 5, GRAPH_Y + GRAPH_HEIGHT - 12);
  
  // FPS label
  img.setTextColor(COLOR_FPS_GOOD);
  //img.drawString("FPS", GRAPH_X + GRAPH_WIDTH - 25, GRAPH_Y + 3);
}

void readSerialData() {
  String jsonString = Serial.readStringUntil('\n');
  
  if (jsonString.length() > 0) {
    DynamicJsonDocument doc(512);
    DeserializationError error = deserializeJson(doc, jsonString);
    
    if (error) {
      Serial.print("JSON parsing failed: ");
      Serial.println(error.c_str());
      return;
    }
    
    hwData.cpu_temp = doc["cpu_temp"].as<float>();
    hwData.gpu_temp = doc["gpu_temp"].as<float>();
    hwData.fps = doc["fps"].as<float>();
    hwData.gpu_fan_speed = doc["gpu_fan_speed"].as<float>();
    hwData.brightness = doc["brightness"].as<int>();
    hwData.cpu_load = doc["cpu_load"].as<float>();
    hwData.timestamp = doc["timestamp"].as<unsigned long>();
    hwData.last_update = millis();
    hwData.data_valid = true;
    
    if (millis() - lastGraphUpdate >= GRAPH_UPDATE_INTERVAL) {
      addFPSData(hwData.fps);
      addCPUUsageData(hwData.cpu_load);
      lastGraphUpdate = millis();
    }
    
    Serial.printf("Received - CPU: %.1f°C, GPU: %.1f°C, FPS: %.1f\n", 
                  hwData.cpu_temp, hwData.gpu_temp, hwData.fps);
  }
}

void handle_oled() {
  // Rich gradient background
  for (int i = 0; i < 80; i++) {
    uint16_t color = img.color565(
      map(i, 0, 80, 4, 12),     // R
      map(i, 0, 80, 2, 8),      // G  
      map(i, 0, 80, 16, 32)     // B
    );
    img.drawLine(0, i, 160, i, color);
  }
  
  bool dataExpired = (millis() - hwData.last_update) > DATA_TIMEOUT;
  
  if (!hwData.data_valid || dataExpired) {
    // Stylish error display
    img.fillRoundRect(10, 25, 140, 30, 8, COLOR_ACCENT);
    img.setTextColor(COLOR_TEXT_MAIN);
    img.drawString("WAITING FOR PC DATA", 20, 32);
    img.setTextColor(COLOR_TEXT_DIM);
    img.drawString("Check COM26 connection", 20, 42);
    analogWrite(10, 0);
  } else {
    if (hwData.brightness != oldBrightness) {
      oldBrightness = hwData.brightness;
      analogWrite(10, hwData.brightness);
    }
    // Draw the beautiful FPS graph
    if (hwData.fps >= 0)
      drawFPSGraph();
    else
      drawCPUUsageGraph();
    
    // GPU Section with better layout
    int startY = 52;
    
    // GPU Temperature
    uint16_t gpuTempColor = (hwData.gpu_temp > 80) ? COLOR_TEMP_HOT : COLOR_TEMP_COOL;
    drawTempText("GPU:", hwData.gpu_temp, 5, startY, COLOR_GPU_MAIN, gpuTempColor);
    
    // GPU Fan Speed
    img.setTextColor(COLOR_GPU_MAIN);
    img.drawString("FAN:", 85, startY);
    if (hwData.gpu_fan_speed > 0) {
      char fanStr[10];
      snprintf(fanStr, sizeof(fanStr), "%.0fRPM", hwData.gpu_fan_speed);
      uint16_t fanColor = (hwData.gpu_fan_speed > 2000) ? COLOR_TEMP_HOT : COLOR_TEMP_COOL;
      img.setTextColor(fanColor);
      img.drawString(fanStr, 115, startY);
    } else {
      img.setTextColor(COLOR_TEXT_DIM);
      img.drawString("0", 115, startY);
    }
    
    // CPU Section
    startY += 12;
    uint16_t cpuTempColor = (hwData.cpu_temp > 80) ? COLOR_TEMP_HOT : COLOR_TEMP_COOL;
    drawTempText("CPU:", hwData.cpu_temp, 5, startY, COLOR_CPU_MAIN, cpuTempColor);
    
    // Current FPS with color coding
    img.setTextColor(COLOR_CPU_MAIN);
    img.drawString("FPS:", 85, startY);
    if (hwData.fps > 0) {
      char fpsStr[10];
      snprintf(fpsStr, sizeof(fpsStr), "%.1f", hwData.fps);
      uint16_t fpsColor = (hwData.fps > 60) ? COLOR_FPS_GOOD : 
                         (hwData.fps > 30) ? COLOR_TEMP_HOT : COLOR_FPS_BAD;
      img.setTextColor(fpsColor);
      img.drawString(fpsStr, 115, startY);
    } else {
      img.setTextColor(COLOR_TEXT_DIM);
      img.drawString("N/A", 115, startY);
    }
  }
  
  img.pushSprite(0, 0);
}

void setup() {
  Serial.begin(256000);
  WiFi.begin(ssid, password);
  delay(10);
  pinMode(10, OUTPUT);
  digitalWrite(10, HIGH);
  
  tft.init();
  delay(10);
  tft.setRotation(1);
  tft.fillScreen(COLOR_BG_DARK);
  
  img.createSprite(160, 80);
  img.fillSprite(COLOR_BG_DARK);
  img.setTextColor(COLOR_TEXT_MAIN);
  img.setTextSize(1);
  img.setTextDatum(TL_DATUM);
  
  // Stylish startup screen
  img.setTextColor(COLOR_ACCENT);
  img.drawString("PC STATS MONITOR", 15, 15);
  img.setTextColor(COLOR_TEXT_DIM);
  img.drawString("Initializing...", 35, 30);
  img.pushSprite(0, 0);
  
  for (int i = 0; i < MAX_FPS_SAMPLES; i++) {
    fpsHistory[i] = 0;
  }
  
  delay(1000);
}

void loop() {
  if (Serial.available()) {
    readSerialData();
  }
  if (!otaStarted && WiFi.status() == WL_CONNECTED) {
    ArduinoOTA.setHostname("desktopfps");
    ArduinoOTA.begin();
    
    Blynk.config(auth, IPAddress(192, 168, 50, 197), 8080);
    Blynk.connect();
    Serial.println("OTA Ready");
    otaStarted = true;
  }
  if (otaStarted) {
    ArduinoOTA.handle();
    Blynk.run();
  }

  every(30000) {
    if (otaStarted) {
      Blynk.virtualWrite(V0, hwData.cpu_temp);
      Blynk.virtualWrite(V1, hwData.gpu_temp);
      Blynk.virtualWrite(V2, hwData.fps);
      Blynk.virtualWrite(V3, hwData.gpu_fan_speed);
      Blynk.virtualWrite(V4, hwData.cpu_load);
      Blynk.virtualWrite(V5, hwData.brightness);
    }
  }

  handle_oled();
  //delay(40);
}
