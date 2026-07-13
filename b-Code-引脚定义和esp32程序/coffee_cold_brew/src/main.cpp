#include <Arduino.h>
#include "board_pins.h"

namespace {
constexpr uint32_t kBaudRate = 115200;
constexpr uint32_t kStateIntervalMs = 1000;
constexpr size_t kLineCapacity = 192;

enum class DeviceMode {
  Idle,
  Up,
  Down,
  Fault,
};

DeviceMode currentMode = DeviceMode::Idle;
String lastError;
String rxLine;
uint32_t lastStateAt = 0;
uint32_t sequenceNumber = 0;

const char* modeName(DeviceMode mode) {
  switch (mode) {
    case DeviceMode::Up:
      return "up";
    case DeviceMode::Down:
      return "down";
    case DeviceMode::Fault:
      return "fault";
    case DeviceMode::Idle:
    default:
      return "idle";
  }
}

int readId(const String& line) {
  const int key = line.indexOf("\"id\"");
  if (key < 0) {
    return 0;
  }

  const int colon = line.indexOf(':', key);
  if (colon < 0) {
    return 0;
  }

  return line.substring(colon + 1).toInt();
}

void writeRelaySafe() {
  digitalWrite(CB_PIN_RELAY_MOTOR1, CB_RELAY_SAFE_LEVEL);
  digitalWrite(CB_PIN_RELAY_MOTOR2, CB_RELAY_SAFE_LEVEL);
}

void applyMode(DeviceMode mode) {
  writeRelaySafe();
  delay(10);

  currentMode = mode;
  lastError = "";
  if (mode == DeviceMode::Up) {
    digitalWrite(CB_PIN_RELAY_MOTOR1, CB_RELAY_ACTIVE_LEVEL);
  } else if (mode == DeviceMode::Down) {
    digitalWrite(CB_PIN_RELAY_MOTOR2, CB_RELAY_ACTIVE_LEVEL);
  } else if (mode == DeviceMode::Fault) {
    lastError = "fault";
  }

  const bool gpio15 = digitalRead(CB_PIN_RELAY_MOTOR1) == CB_RELAY_ACTIVE_LEVEL;
  const bool gpio16 = digitalRead(CB_PIN_RELAY_MOTOR2) == CB_RELAY_ACTIVE_LEVEL;
  if (gpio15 && gpio16) {
    writeRelaySafe();
    currentMode = DeviceMode::Fault;
    lastError = "interlock";
  }
}

void sendAck(int id, const char* cmd, bool ok, const char* message = "") {
  Serial.print("{\"type\":\"ack\",\"id\":");
  Serial.print(id);
  Serial.print(",\"cmd\":\"");
  Serial.print(cmd);
  Serial.print("\",\"ok\":");
  Serial.print(ok ? "true" : "false");
  if (message[0] != '\0') {
    Serial.print(",\"message\":\"");
    Serial.print(message);
    Serial.print("\"");
  }
  Serial.println("}");
}

void sendError(int id, const char* code, const char* message) {
  Serial.print("{\"type\":\"error\",\"id\":");
  Serial.print(id);
  Serial.print(",\"code\":\"");
  Serial.print(code);
  Serial.print("\",\"message\":\"");
  Serial.print(message);
  Serial.println("\"}");
}

void sendHello(int id) {
  Serial.print("{\"type\":\"hello\",\"id\":");
  Serial.print(id);
  Serial.println(",\"device\":\"coffee-cold-brew-esp32\",\"proto\":1}");
}

void sendPong(int id) {
  Serial.print("{\"type\":\"pong\",\"id\":");
  Serial.print(id);
  Serial.println("}");
}

void sendState() {
  sequenceNumber++;
  const bool gpio15 = digitalRead(CB_PIN_RELAY_MOTOR1) == CB_RELAY_ACTIVE_LEVEL;
  const bool gpio16 = digitalRead(CB_PIN_RELAY_MOTOR2) == CB_RELAY_ACTIVE_LEVEL;

  Serial.print("{\"type\":\"state\",\"seq\":");
  Serial.print(sequenceNumber);
  Serial.print(",\"mode\":\"");
  Serial.print(modeName(currentMode));
  Serial.print("\",\"gpio15\":");
  Serial.print(gpio15 ? "true" : "false");
  Serial.print(",\"gpio16\":");
  Serial.print(gpio16 ? "true" : "false");
  Serial.print(",\"uptime\":");
  Serial.print(millis());
  if (lastError.length() > 0) {
    Serial.print(",\"error\":\"");
    Serial.print(lastError);
    Serial.print("\"");
  }
  Serial.println("}");
}

void handleSet(int id, const String& line) {
  if (line.indexOf("\"mode\":\"up\"") >= 0) {
    applyMode(DeviceMode::Up);
    sendAck(id, "set", true);
    sendState();
  } else if (line.indexOf("\"mode\":\"down\"") >= 0) {
    applyMode(DeviceMode::Down);
    sendAck(id, "set", true);
    sendState();
  } else if (line.indexOf("\"mode\":\"stop\"") >= 0 || line.indexOf("\"mode\":\"idle\"") >= 0) {
    applyMode(DeviceMode::Idle);
    sendAck(id, "set", true);
    sendState();
  } else {
    sendAck(id, "set", false, "bad mode");
    sendError(id, "bad_mode", "mode must be up, down, stop, or idle");
  }
}

void handleCommand(const String& line) {
  const int id = readId(line);
  if (line.indexOf("\"cmd\":\"hello\"") >= 0) {
    sendHello(id);
    sendState();
  } else if (line.indexOf("\"cmd\":\"ping\"") >= 0) {
    sendPong(id);
  } else if (line.indexOf("\"cmd\":\"state\"") >= 0) {
    sendAck(id, "state", true);
    sendState();
  } else if (line.indexOf("\"cmd\":\"set\"") >= 0) {
    handleSet(id, line);
  } else {
    sendError(id, "bad_cmd", "unknown command");
  }
}

void readSerialLines() {
  while (Serial.available() > 0) {
    const char ch = static_cast<char>(Serial.read());
    if (ch == '\r') {
      continue;
    }

    if (ch == '\n') {
      rxLine.trim();
      if (rxLine.length() > 0) {
        handleCommand(rxLine);
      }
      rxLine = "";
      continue;
    }

    if (rxLine.length() < kLineCapacity) {
      rxLine += ch;
    } else {
      rxLine = "";
      sendError(0, "line_too_long", "serial line too long");
    }
  }
}
}  // namespace

void setup() {
  Serial.begin(kBaudRate);

  digitalWrite(CB_PIN_RELAY_MOTOR1, CB_RELAY_SAFE_LEVEL);
  digitalWrite(CB_PIN_RELAY_MOTOR2, CB_RELAY_SAFE_LEVEL);
  pinMode(CB_PIN_RELAY_MOTOR1, OUTPUT);
  pinMode(CB_PIN_RELAY_MOTOR2, OUTPUT);
  pinMode(CB_PIN_MOTOR1_PWM, OUTPUT);
  pinMode(CB_PIN_MOTOR2_PWM, OUTPUT);
  digitalWrite(CB_PIN_MOTOR1_PWM, LOW);
  digitalWrite(CB_PIN_MOTOR2_PWM, LOW);
  pinMode(CB_PIN_MODE_SWITCH, INPUT);
  pinMode(CB_PIN_BATTERY_ADC, INPUT);

  applyMode(DeviceMode::Idle);
  sendHello(0);
  sendState();
  lastStateAt = millis();
}

void loop() {
  readSerialLines();

  const uint32_t now = millis();
  if (now - lastStateAt >= kStateIntervalMs) {
    lastStateAt = now;
    sendState();
  }
}
