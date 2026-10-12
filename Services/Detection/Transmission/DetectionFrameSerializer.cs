// Подключаем модели распознавания: снимок кадра и найденные объекты
using MauiBluethooth.Models.Detection;
// Подключаем список классов COCO — по нему подпись превращается в номер класса
using MauiBluethooth.Services.Detection.Helpers;

// Объявляем пространство имён передачи результатов по Bluetooth
namespace MauiBluethooth.Services.Detection.Transmission;

// Сериализатор: превращает снимок распознавания в список строк протокола S/D/E.
//
// Класс чисто статический: у него нет состояния, он только переводит данные
// в текст. Вся «умность» передачи (таймер, условия) живёт в DetectionTransmitter,
// а здесь — исключительно правила построения строк.
//
// Почему такой формат:
//   • строки читаются построчно — на Arduino не нужен разбор бинарных пакетов;
//   • каждая строка < = MaxPacketBytes байт — это гарантия, что она поместится
//     в маленький буфер SoftwareSerial и уйдёт одним сообщением;
//   • первая строка кадра начинается с «S», продолжения с «D», последняя «E» —
//     приёмник по этим маркерам понимает, где начинается и заканчивается кадр.
public static class DetectionFrameSerializer
{
	// Максимальный размер одной строки-пакета в байтах.
	// Ограничение общее для передатчика и (в следующем этапе) для Arduino-скетча:
	// меньше буфера SoftwareSerial (64 байта), чтобы строка прошла целиком.
	//
	// Единицы — «байты», а не «символы»: строки состоят только из ASCII
	// (цифры, запятые, буквы S/D/E), а в ASCII один символ равен одному байту,
	// поэтому длина строки и число байт совпадают.
	public const int MaxPacketBytes = 56;

	// Код типа «лицо». Число выбрано вне диапазона классов COCO (0–79),
	// чтобы код никогда не пересекался с номерами классов набора
	private const int FaceCode = 100;

	// Код «неизвестная подпись»: если подпись объекта не нашлась ни в COCO,
	// ни среди фигур, на Arduino уходит этот резервный код вместо падения
	private const int UnknownCode = 255;

	// База кодов фигур: 200 + номер элемента ShapeKind.
	// ShapeKind.Circle = 0 → 200, Square = 1 → 201, Rectangle = 2 → 202,
	// Triangle = 3 → 203. Диапазон 200–203 тоже вне 0–79 и не пересекается с лицом (100)
	private const int ShapeCodeBase = 200;

	// Превращает снимок распознавания в список строк для отправки.
	//
	// Правила:
	//   • пустой кадр (объектов нет) — одна строка «E»: приёмник знает,
	//     что кадр пустой и просто очищает экран;
	//   • иначе объекты превращаются в фрагменты «code,x,y,w,h», фрагменты
	//     собираются в строки, каждая строка = MaxPacketBytes;
	//   • сборка строк: первая непустая строка получает префикс «S:»,
	//     остальные «D:», после всех строк всегда добавляется «E».
	public static string[] FormatPackets(DetectionSnapshot snapshot)
	{
		// 1. Строим фрагменты для каждого объекта.
		// Фрагмент — это неизменяемая часть строки до соединения через «;».
		var segments = new List<string>(snapshot.Objects.Count);
		foreach (var obj in snapshot.Objects)
		{
			// Код типа объекта (фигура, лицо, класс COCO или 255)
			var code = CodeFor(snapshot, obj);

			// Координаты приводим к целым пикселям.
			// Math.Round ближе к истине, чем простое приведение (которое отсекает дробь вниз)
			var x = (int)Math.Round(obj.Bounds.X);
			var y = (int)Math.Round(obj.Bounds.Y);
			var w = (int)Math.Round(obj.Bounds.Width);
			var h = (int)Math.Round(obj.Bounds.Height);

			// Собираем фрагмент «код,координаты» — так его разберёт sscanf на Arduino
			segments.Add($"{code},{x},{y},{w},{h}");
		}

		// 2. Если ничего не найдено — кадр пустой, вместе него уходит один «E»
		if (segments.Count == 0)
		{
			return ["E"];
		}

		// 3. Сворачиваем фрагменты в строки, не превышающие лимит.
		var packets = new List<string>(segments.Count);
		var current = new System.Text.StringBuilder(MaxPacketBytes);

		foreach (var segment in segments)
		{
			// Сколько займёт добавление этого фрагмента в текущую строку:
			// «;» до фрагмента, если строка уже непустая, и «2» — на будущий префикс «S:»/«D:»
			var needed = current.Length + (current.Length > 0 ? 1 : 0) + segment.Length + 2;

			// Если строка перерастёт лимит — заканчиваем её и начинаем новую
			if (needed > MaxPacketBytes && current.Length > 0)
			{
				packets.Add(BuildLine(packets.Count, current.ToString()));
				current.Clear();
			}

			// Разделитель «;» вставляем только между фрагментами, не в начало строки
			if (current.Length > 0)
				current.Append(';');
			current.Append(segment);
		}

		// 4. Выносим остаток фрагментов последней строкой
		if (current.Length > 0)
			packets.Add(BuildLine(packets.Count, current.ToString()));

		// 5. Завершающий маркер кадра
		packets.Add("E");

		// Возвращаем готовый список строк для отправки по одной
		return packets.ToArray();
	}

	// Определяет код типа объекта для протокола.
	//
	// Приоритет (важен порядок проверок):
	//   1) фигура — поле Shape заполняется только детектором фигур;
	//   2) лицо — категория модели в снимке;
	//   3) обычный объект — номер класса COCO по подписи;
	//   4) подпись не из COCO — резервный код 255.
	// Почему не передаём саму подпись текстом: текст бы не влез в маленькие
	// пакеты и заставил бы Arduino хранить таблицу строк — а так на обеих
	// сторонах только маленькая таблица чисел
	private static int CodeFor(DetectionSnapshot snapshot, DetectedObject obj)
	{
		// Фигура: код выводится из элемента ShapeKind
		if (obj.Shape is ShapeKind shape)
			return ShapeCodeBase + (int)shape;

		// Лицо: категория модели — Face
		if (snapshot.Kind == DetectionModelKind.Face)
			return FaceCode;

		// Обычный объект: ищем подпись в списке COCO.
		// IndexOf возвращает -1, если подпись не найдена
		var classIndex = Array.IndexOf(CocoClasses.Names, obj.Label);

		// Найденный индекс и есть номер класса (0–79), иначе — резерв
		return classIndex >= 0 ? classIndex : UnknownCode;
	}

	// Оборачивает содержимое строки в пакет с префиксом.
	// Первый пакет — «S» (start, приёмник начинает новый кадр),
	// последующие — «D» (data, продолжение кадра).
	// «E» добавляется отдельно, в FormatPackets, поэтому тут про неё нет ничего
	private static string BuildLine(int packetIndex, string content)
		=> (packetIndex == 0 ? "S:" : "D:") + content;
}