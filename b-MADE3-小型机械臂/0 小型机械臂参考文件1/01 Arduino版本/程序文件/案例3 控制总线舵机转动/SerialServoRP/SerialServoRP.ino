#include "include.h"
#include "SerialServo.h"

void setup() {
  Serial.begin(115200);
  delay(1000);
}

void loop() {
  int id1 = LobotSerialServoReadID(Serial);  //读取当前舵机ID
  int pos1 = LobotSerialServoReadPosition(Serial, id1); //读取舵机实时位置
  Serial.println(id1);
  Serial.println(pos1);
  LobotSerialServoMove(Serial, ID_ALL, 0, 1000);
  delay(1000);
  int id2 = LobotSerialServoReadID(Serial)+1;  //读取当前舵机ID
  Serial.println(id2);
  int pos2 = LobotSerialServoReadPosition(Serial, 1); //读取舵机实时位置
  Serial.println(pos2);
  LobotSerialServoMove(Serial, ID_ALL, 500, 1000);
  delay(1000);
  int id3 = LobotSerialServoReadID(Serial)+2;  //读取当前舵机ID
  Serial.println(id3);
  int pos3 = LobotSerialServoReadPosition(Serial, 1); //读取舵机实时位置
  Serial.println(pos3);
  LobotSerialServoMove(Serial, ID_ALL, 1000, 1000);
  delay(1000);
}
