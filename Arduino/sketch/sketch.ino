// Скетч: приём и разбор кадров распознавания с телефона по Bluetooth.
//
// Телефон шлёт построчный протокол:
//   S:code,x,y,w,h;code,x,y,w,h;...  — начало кадра (объекты разделены ';', поля — ',')
//   D:code,x,y,w,h;...               — дополнение текущего кадра
//   E                                — конец кадра (пустой кадр = одна строка 'E')
//   1 / 0                            — команда светодиода (обратная совместимость)
//
// Каждая строка заканчивается '\n', длина строки не больше 56 байт.
// Разобранный кадр складывается в массив frame[], готовый к дальнейшей обработке,
// и дублируется в USB-монитор в человекочитаемом виде.

#include <SoftwareSerial.h>
// Пины Bluetooth-модуля HC-05: (RX, TX). Приём данных — пин 10, передача — пин 11
SoftwareSerial bluetooth(10, 11);

// Встроенный светодиод на плате (пин 13)
const int LED_PIN = 13;

// Буфер одной строки от телефона. Строки протокола не длиннее 56 байт + '\n',
// поэтому 64 байт хватает с запасом
const int LINE_BUFFER_SIZE = 64;
char lineBuffer[LINE_BUFFER_SIZE];
int linePos = 0;

// Буфер текущего кадра: дальше с ним можно делать что угодно (индикация, дисплей и т.п.)
const int MAX_FRAME_OBJECTS = 32; // 32 x (2+2+2+2+2) = 320 байт RAM — укладывается в Uno

struct FrameObject {
  uint16_t code; // номер класса: 0–79 COCO, 100 лицо, 200–203 фигуры, 255 неизвестный
  uint16_t x;
  uint16_t y;
  uint16_t w;
  uint16_t h;
};

FrameObject frame[MAX_FRAME_OBJECTS];
int frameCount = 0;
bool frameActive = false; // true после 'S', false после 'E'
unsigned long frameNumber = 0;

// Названия 80 классов COCO в памяти программ (PROGMEM) — код не занимает RAM.
// Имена совпадают с CocoClasses.cs в приложении (и порядок — по номерам классов)
static const char COCO_0[] PROGMEM = "person";
static const char COCO_1[] PROGMEM = "bicycle";
static const char COCO_2[] PROGMEM = "car";
static const char COCO_3[] PROGMEM = "motorcycle";
static const char COCO_4[] PROGMEM = "airplane";
static const char COCO_5[] PROGMEM = "bus";
static const char COCO_6[] PROGMEM = "train";
static const char COCO_7[] PROGMEM = "truck";
static const char COCO_8[] PROGMEM = "boat";
static const char COCO_9[] PROGMEM = "traffic light";
static const char COCO_10[] PROGMEM = "fire hydrant";
static const char COCO_11[] PROGMEM = "stop sign";
static const char COCO_12[] PROGMEM = "parking meter";
static const char COCO_13[] PROGMEM = "bench";
static const char COCO_14[] PROGMEM = "bird";
static const char COCO_15[] PROGMEM = "cat";
static const char COCO_16[] PROGMEM = "dog";
static const char COCO_17[] PROGMEM = "horse";
static const char COCO_18[] PROGMEM = "sheep";
static const char COCO_19[] PROGMEM = "cow";
static const char COCO_20[] PROGMEM = "elephant";
static const char COCO_21[] PROGMEM = "bear";
static const char COCO_22[] PROGMEM = "zebra";
static const char COCO_23[] PROGMEM = "giraffe";
static const char COCO_24[] PROGMEM = "backpack";
static const char COCO_25[] PROGMEM = "umbrella";
static const char COCO_26[] PROGMEM = "handbag";
static const char COCO_27[] PROGMEM = "tie";
static const char COCO_28[] PROGMEM = "suitcase";
static const char COCO_29[] PROGMEM = "frisbee";
static const char COCO_30[] PROGMEM = "skis";
static const char COCO_31[] PROGMEM = "snowboard";
static const char COCO_32[] PROGMEM = "sports ball";
static const char COCO_33[] PROGMEM = "kite";
static const char COCO_34[] PROGMEM = "baseball bat";
static const char COCO_35[] PROGMEM = "baseball glove";
static const char COCO_36[] PROGMEM = "skateboard";
static const char COCO_37[] PROGMEM = "surfboard";
static const char COCO_38[] PROGMEM = "tennis racket";
static const char COCO_39[] PROGMEM = "bottle";
static const char COCO_40[] PROGMEM = "wine glass";
static const char COCO_41[] PROGMEM = "cup";
static const char COCO_42[] PROGMEM = "fork";
static const char COCO_43[] PROGMEM = "knife";
static const char COCO_44[] PROGMEM = "spoon";
static const char COCO_45[] PROGMEM = "bowl";
static const char COCO_46[] PROGMEM = "banana";
static const char COCO_47[] PROGMEM = "apple";
static const char COCO_48[] PROGMEM = "sandwich";
static const char COCO_49[] PROGMEM = "orange";
static const char COCO_50[] PROGMEM = "broccoli";
static const char COCO_51[] PROGMEM = "carrot";
static const char COCO_52[] PROGMEM = "hot dog";
static const char COCO_53[] PROGMEM = "pizza";
static const char COCO_54[] PROGMEM = "donut";
static const char COCO_55[] PROGMEM = "cake";
static const char COCO_56[] PROGMEM = "chair";
static const char COCO_57[] PROGMEM = "couch";
static const char COCO_58[] PROGMEM = "potted plant";
static const char COCO_59[] PROGMEM = "bed";
static const char COCO_60[] PROGMEM = "dining table";
static const char COCO_61[] PROGMEM = "toilet";
static const char COCO_62[] PROGMEM = "tv";
static const char COCO_63[] PROGMEM = "laptop";
static const char COCO_64[] PROGMEM = "mouse";
static const char COCO_65[] PROGMEM = "remote";
static const char COCO_66[] PROGMEM = "keyboard";
static const char COCO_67[] PROGMEM = "cell phone";
static const char COCO_68[] PROGMEM = "microwave";
static const char COCO_69[] PROGMEM = "oven";
static const char COCO_70[] PROGMEM = "toaster";
static const char COCO_71[] PROGMEM = "sink";
static const char COCO_72[] PROGMEM = "refrigerator";
static const char COCO_73[] PROGMEM = "book";
static const char COCO_74[] PROGMEM = "clock";
static const char COCO_75[] PROGMEM = "vase";
static const char COCO_76[] PROGMEM = "scissors";
static const char COCO_77[] PROGMEM = "teddy bear";
static const char COCO_78[] PROGMEM = "hair drier";
static const char COCO_79[] PROGMEM = "toothbrush";

// Массив указателей на строки выше — сам тоже лежит в PROGMEM (на AVR указатели 16-битные)
static const char* const COCO_NAMES[80] PROGMEM = {
  COCO_0, COCO_1, COCO_2, COCO_3, COCO_4, COCO_5, COCO_6, COCO_7, COCO_8, COCO_9,
  COCO_10, COCO_11, COCO_12, COCO_13, COCO_14, COCO_15, COCO_16, COCO_17, COCO_18, COCO_19,
  COCO_20, COCO_21, COCO_22, COCO_23, COCO_24, COCO_25, COCO_26, COCO_27, COCO_28, COCO_29,
  COCO_30, COCO_31, COCO_32, COCO_33, COCO_34, COCO_35, COCO_36, COCO_37, COCO_38, COCO_39,
  COCO_40, COCO_41, COCO_42, COCO_43, COCO_44, COCO_45, COCO_46, COCO_47, COCO_48, COCO_49,
  COCO_50, COCO_51, COCO_52, COCO_53, COCO_54, COCO_55, COCO_56, COCO_57, COCO_58, COCO_59,
  COCO_60, COCO_61, COCO_62, COCO_63, COCO_64, COCO_65, COCO_66, COCO_67, COCO_68, COCO_69,
  COCO_70, COCO_71, COCO_72, COCO_73, COCO_74, COCO_75, COCO_76, COCO_77, COCO_78, COCO_79
};

void setup() {
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, LOW);

  // USB-монитор (компьютер) — 9600 бод
  Serial.begin(9600);
  // Bluetooth-модуль HC-05 в режиме данных — заводские 9600 бод
  bluetooth.begin(9600);

  Serial.println(F("=== Приём кадров распознавания ==="));
  Serial.println(F("От телефона: строки S/D/E с объектами; 1/0 - светодиод."));
  Serial.println(F("Монитор порта: 9600 бод."));
}

void loop() {
  // Данные от Bluetooth (телефон): накапливаем строку и разбираем её
  while (bluetooth.available()) {
    char c = bluetooth.read();
    if (c == '\n') {
      lineBuffer[linePos] = '\0';
      processLine(lineBuffer);
      linePos = 0;
    } else if (linePos < LINE_BUFFER_SIZE - 1) {
      lineBuffer[linePos++] = c;
    } else {
      // Строка длиннее буфера — такое случаться не должно (лимит протокола 56 байт)
      linePos = 0;
      Serial.println(F("Предупреждение: строка длиннее 63 байт, сброс буфера"));
    }
  }

  // Данные с USB-монитора: просто зеркалим их в Bluetooth (на телефон)
  while (Serial.available()) {
    bluetooth.write(Serial.read());
  }
}

// Разбор одной строки от телефона
void processLine(char* line) {
  if (line[0] == 'S') {
    // Начало нового кадра: старый кадр отбрасываем, счётчик обнуляем
    frameActive = true;
    frameCount = 0;
    frameNumber++;
    Serial.print(F("Кадр #"));
    Serial.print(frameNumber);
    Serial.println(F(" начат"));
    parseSegments(line + 2); // после "S:" идут сегменты объектов
  } else if (line[0] == 'D') {
    // Продолжение текущего кадра
    if (frameActive) {
      parseSegments(line + 2);
    } else {
      Serial.println(F("Пропущено: строка D вне открытого кадра"));
    }
  } else if (line[0] == 'E' && line[1] == '\0') {
    // Конец кадра (или пустой кадр — телефон шлёт одну строку 'E')
    if (frameActive) {
      Serial.print(F("Кадр #"));
      Serial.print(frameNumber);
      Serial.print(F(" завершён: "));
      Serial.print(frameCount);
      if (frameCount == 1) {
        Serial.println(F(" объект"));
      } else {
        Serial.println(F(" объектов"));
      }
      frameActive = false;
    } else {
      Serial.println(F("Кадр пуст"));
    }
  } else if (line[0] == '1' && line[1] == '\0') {
    // Команда «включить светодиод» (обратная совместимость со старым демо)
    digitalWrite(LED_PIN, HIGH);
    Serial.println(F("LED ON"));
  } else if (line[0] == '0' && line[1] == '\0') {
    // Команда «выключить светодиод»
    digitalWrite(LED_PIN, LOW);
    Serial.println(F("LED OFF"));
  } else {
    // Всё остальное (например, текст из чата) не относится к протоколу — игнорируем
    Serial.print(F("Пропущено: "));
    Serial.println(line);
  }
}

// Разбор сегментов "code,x,y,w,h;code,x,y,w,h;..." в буфер кадра с печатью
void parseSegments(char* body) {
  char* saveSegments = NULL;
  for (char* seg = strtok_r(body, ";", &saveSegments);
       seg != NULL;
       seg = strtok_r(NULL, ";", &saveSegments)) {
    if (frameCount >= MAX_FRAME_OBJECTS) {
      Serial.println(F("Предупреждение: кадр переполнен, лишние объекты пропущены"));
      return;
    }

    // Поля внутри сегмента разделены запятыми
    char* saveFields = NULL;
    char* token = strtok_r(seg, ",", &saveFields);
    if (token == NULL) {
      continue;
    }
    uint16_t code = (uint16_t)atoi(token);
    token = strtok_r(NULL, ",", &saveFields);
    if (token == NULL) {
      continue;
    }
    uint16_t x = (uint16_t)atoi(token);
    token = strtok_r(NULL, ",", &saveFields);
    if (token == NULL) {
      continue;
    }
    uint16_t y = (uint16_t)atoi(token);
    token = strtok_r(NULL, ",", &saveFields);
    if (token == NULL) {
      continue;
    }
    uint16_t w = (uint16_t)atoi(token);
    token = strtok_r(NULL, ",", &saveFields);
    if (token == NULL) {
      continue;
    }
    uint16_t h = (uint16_t)atoi(token);

    // Кладём объект в буфер кадра — дальше его можно обрабатывать в кадре
    frame[frameCount].code = code;
    frame[frameCount].x = x;
    frame[frameCount].y = y;
    frame[frameCount].w = w;
    frame[frameCount].h = h;
    frameCount++;

    // Человекочитаемый вывод для монитора и дальнейшей обработки
    Serial.print(F("объект: "));
    printLabel(code);
    Serial.print(F(" ("));
    Serial.print(code);
    Serial.print(F(") x="));
    Serial.print(x);
    Serial.print(F(" y="));
    Serial.print(y);
    Serial.print(F(" w="));
    Serial.print(w);
    Serial.print(F(" h="));
    Serial.println(h);
  }
}

// Печать имени класса по коду: лицо, фигуры, COCO-классы, понятная заглушка
void printLabel(uint16_t code) {
  if (code == 100) {
    Serial.print(F("лицо"));
  } else if (code == 200) {
    Serial.print(F("круг"));
  } else if (code == 201) {
    Serial.print(F("квадрат"));
  } else if (code == 202) {
    Serial.print(F("прямоугольник"));
  } else if (code == 203) {
    Serial.print(F("треугольник"));
  } else if (code == 255) {
    Serial.print(F("неизвестный"));
  } else if (code < 80) {
    // Имя классического класса COCO лежит в PROGMEM — читаем его по байтам
    const char* name = (const char*)pgm_read_word(&COCO_NAMES[code]);
    printPgmString(name);
  } else {
    Serial.print(F("класс "));
    Serial.print(code);
  }
}

// Вывод PROGMEM-строки в Serial без копирования в RAM
void printPgmString(const char* pgmString) {
  char c;
  while ((c = pgm_read_byte(pgmString++)) != '\0') {
    Serial.write(c);
  }
}